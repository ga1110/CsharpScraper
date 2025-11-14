using HtmlAgilityPack;
using Scraper.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scraper.Services;

/// <summary>
/// Основной класс для скрапинга статей с сайта panorama.pub, собирает статьи по датам и парсит их детальную информацию
/// </summary>
public class Scraper : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HttpFetcher _httpFetcher;
    private readonly ArticleParser _articleParser;
    private readonly string _baseUrl = "https://panorama.pub/news";
    private readonly string _rootUrl = "https://panorama.pub";

    /// <summary>
    /// Инициализирует новый экземпляр Scraper с настроенным HTTP клиентом и зависимостями
    /// </summary>
    public Scraper()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        
        _httpFetcher = new HttpFetcher(_httpClient);
        var commentService = new CommentService(_httpFetcher);
        _articleParser = new ArticleParser(_rootUrl, commentService);
    }

    /// <summary>
    /// Скрапит статьи, начиная с текущей даты и двигаясь назад во времени, собирая ссылки и затем парся детальную информацию
    /// </summary>
    /// <param name="maxArticles">Максимальное количество статей для сбора</param>
    /// <returns>Список статей с заполненными детальными данными</returns>
    public async Task<List<Article>> ScrapeArticlesAsync(int maxArticles = 20000)
    {
        const int delay = 200;

        var articles = new List<Article>();
        var visitedUrls = new HashSet<string>();

        // Начинаем с текущей даты
        var currentDate = DateTime.Now;
        var consecutiveEmptyDays = 0;
        
        Console.WriteLine($"Скрапинг начался");
        
        while (articles.Count < maxArticles)
        {
            var dateUrl = ScraperUtils.GetDateUrl(_baseUrl, currentDate);

            try
            {
                var dateArticles = await ScrapeDatePageAsync(dateUrl, maxArticles - articles.Count);
                var newArticlesCount = 0;
                
                foreach (var article in dateArticles)
                {
                    if (!visitedUrls.Contains(article.Url) && articles.Count < maxArticles)
                    {
                        visitedUrls.Add(article.Url);
                        articles.Add(article);
                        newArticlesCount++;
                    }
                }

                if (articles.Count > 0 && articles.Count % 10 == 0)
                    Console.WriteLine($"Собрано: {articles.Count} из {maxArticles}");

                if (newArticlesCount == 0)
                    consecutiveEmptyDays++;
                else
                    consecutiveEmptyDays = 0;
                
                // Переходим к предыдущей дате
                currentDate = currentDate.AddDays(-1);
                
                // Задержка между запросами
                await Task.Delay(delay);
            }
            catch (Exception)
            {
                currentDate = currentDate.AddDays(-1);
                consecutiveEmptyDays++;
                
                await Task.Delay(delay * 10);
            }
        }
        
        var detailedArticles = new List<Article>();
        int processed = 0;
        int total = articles.Count;
        int lastReportedPercent = 0;
        
        
        foreach (var article in articles)
        {
            try
            {
                var detailed = await ScrapeArticleDetailsAsync(article);
                detailedArticles.Add(detailed);
                processed++;
                
                double percent = (double)processed / total * 100;
                int currentPercent = (int)(percent / 20) * 20;
                
                if (currentPercent > lastReportedPercent && currentPercent >= 20)
                {
                    lastReportedPercent = currentPercent;
                    Console.WriteLine($"Обработано: {processed} из {total} ({currentPercent}%)");
                }
            }
            catch (Exception)
            {
                    processed++;
                    double percent = (double)processed / total * 100;
                    int currentPercent = (int)(percent / 20) * 20;
                    continue;
            }
        }
        
        // Выводим финальный результат, если еще не вывели 100%
        if (lastReportedPercent < 100)
        {
            Console.WriteLine($"Обработано: {processed} из {total} (100%)");
        }
        
        return detailedArticles;
    }

    /// <summary>
    /// Скрапит страницу с датой и извлекает ссылки на статьи, нормализуя URL и проверяя их валидность
    /// </summary>
    /// <param name="url">URL страницы с датой для скрапинга</param>
    /// <param name="maxCount">Максимальное количество статей для сбора с этой страницы</param>
    /// <returns>Список найденных статей (только URL и заголовки) без дубликатов</returns>
    private async Task<List<Article>> ScrapeDatePageAsync(string url, int maxCount)
    {
        var articles = new List<Article>();
        
        try
        {
            var html = await _httpFetcher.FetchWithRetryAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            
            // Ищем ссылки на статьи - пробуем разные селекторы
            var articleLinks = doc.DocumentNode.SelectNodes("//a[@href]") ?? 
                              doc.DocumentNode.SelectNodes("//a[contains(@href, '/')]");
            
            if (articleLinks != null)
            {
                foreach (var link in articleLinks)
                {
                    try
                    {
                        if (articles.Count >= maxCount) break;
                        
                        var href = link.GetAttributeValue("href", "");
                        if (string.IsNullOrEmpty(href)) continue;
                        
                        // Нормализуем URL
                        if (href.StartsWith("/") && href.Length > 1)
                        {
                            if (!href.StartsWith("//"))
                                href = _rootUrl + href;
                            else
                                continue;
                        }
                        else if (!href.StartsWith("http"))
                            continue;
                        
                        // Проверяем, что это ссылка на статью из раздела 
                        if (ScraperUtils.IsArticleUrl(href, _baseUrl))
                        {
                            var title = ScraperUtils.ExtractTitle(link);
                            if (!string.IsNullOrEmpty(title) && title.Length > 10 && title.Length < 500)
                            {
                                articles.Add(new Article
                                {
                                    Title = title,
                                    Url = href
                                });
                            }
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
        }
        catch (Exception)
        {
            throw; // Пробрасываем исключение, чтобы обработать его в основном методе
        }
        
        return articles.DistinctBy(a => a.Url).ToList();
    }

    /// <summary>
    /// Загружает HTML страницы статьи и парсит её детальную информацию через ArticleParser
    /// </summary>
    /// <param name="article">Объект статьи с базовой информацией (URL, заголовок)</param>
    /// <returns>Обновленный объект статьи с заполненными детальными данными</returns>
    private async Task<Article> ScrapeArticleDetailsAsync(Article article)
    {
        var html = await _httpFetcher.FetchWithRetryAsync(article.Url);
        return await _articleParser.ParseArticleDetailsAsync(article, html);
    }

    /// <summary>
    /// Освобождает ресурсы HTTP клиента
    /// </summary>
    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

