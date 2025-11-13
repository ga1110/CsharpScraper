using HtmlAgilityPack;
using Scraper.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scraper.Services;

public class Scraper
{
    private readonly HttpClient _httpClient;
    private readonly HttpFetcher _httpFetcher;
    private readonly ArticleParser _articleParser;
    private readonly string _baseUrl = "https://panorama.pub/news";
    private readonly string _rootUrl = "https://panorama.pub";

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

    public async Task<List<Article>> ScrapeArticlesAsync(int maxArticles = 20000)
    {
        const int delay = 200;

        var articles = new List<Article>();
        var visitedUrls = new HashSet<string>();

        // Начинаем с текущей даты
        var currentDate = DateTime.Now;
        var consecutiveEmptyDays = 0;
        const int maxConsecutiveEmptyDays = 7; // Останавливаемся после 7 дней подряд без статей
        
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

                if (newArticlesCount == 0)
                {
                    consecutiveEmptyDays++;
                    if (consecutiveEmptyDays >= maxConsecutiveEmptyDays)
                        break;
                }
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
                
                if (consecutiveEmptyDays >= maxConsecutiveEmptyDays)
                    break;
                
                await Task.Delay(delay * 10);
            }
        }
        
        var detailedArticles = new List<Article>();
        int processed = 0;
        
        foreach (var article in articles)
        {
            try
            {
                var detailed = await ScrapeArticleDetailsAsync(article);
                detailedArticles.Add(detailed);
                processed++;
                
                // Небольшая задержка, чтобы не перегружать сервер
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                // Для 404 ошибок (статья не найдена) пропускаем статью и не добавляем ее в список
                if (ex.Message.Contains("404") || ex.Message.Contains("Not Found") ||
                    ex.Message.Contains("Response status code does not indicate success: 4"))
                {
                    continue; // Пропускаем статью
                }
                
                // Для других ошибок добавляем базовую версию статьи
                detailedArticles.Add(article);
            }
        }
        
        return detailedArticles;
    }

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
                            {
                                href = _rootUrl + href;
                            }
                            else
                            {
                                continue;
                            }
                        }
                        else if (!href.StartsWith("http"))
                        {
                            continue;
                        }
                        
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
                        // Пропускаем проблемные ссылки
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

    private async Task<Article> ScrapeArticleDetailsAsync(Article article)
    {
        var html = await _httpFetcher.FetchWithRetryAsync(article.Url);
        return await _articleParser.ParseArticleDetailsAsync(article, html);
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

