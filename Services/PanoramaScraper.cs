using HtmlAgilityPack;
using Scrapper.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scrapper.Services;

public class PanoramaScraper
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl = "https://panorama.pub/news";

    public PanoramaScraper()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<List<Article>> ScrapeArticlesAsync(int maxArticles = 20000)
    {
        var articles = new List<Article>();
        var visitedUrls = new HashSet<string>();
        
        Console.WriteLine("Начинаю скрапинг сайта panorama.pub/news...");
        Console.WriteLine("Начинаю с текущей даты и перехожу к предыдущим датам");
        Console.WriteLine();
        
        // Начинаем с текущей даты
        var currentDate = DateTime.Now;
        var daysBack = 0;
        var maxDaysBack = 3650; // Максимум 10 лет назад
        var consecutiveEmptyDays = 0;
        const int maxConsecutiveEmptyDays = 7; // Останавливаемся после 7 дней подряд без статей
        
        while (articles.Count < maxArticles && daysBack < maxDaysBack)
        {
            var dateUrl = GetDateUrl(currentDate);
            Console.WriteLine($"Скрапинг даты: {currentDate:dd-MM-yyyy} ({dateUrl})");
            
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
                
                Console.WriteLine($"  Найдено новых статей: {newArticlesCount} (всего: {articles.Count}/{maxArticles})");
                Console.WriteLine();
                
                if (newArticlesCount == 0)
                {
                    consecutiveEmptyDays++;
                    if (consecutiveEmptyDays >= maxConsecutiveEmptyDays)
                    {
                        Console.WriteLine($"Не найдено статей в течение {maxConsecutiveEmptyDays} дней подряд. Останавливаю скрапинг.");
                        break;
                    }
                }
                else
                {
                    consecutiveEmptyDays = 0;
                }
                
                // Переходим к предыдущей дате
                currentDate = currentDate.AddDays(-1);
                daysBack++;
                
                // Задержка между запросами
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Ошибка при скрапинге даты {currentDate:dd-MM-yyyy}: {ex.Message}");
                Console.WriteLine();
                
                // Продолжаем со следующей даты даже при ошибке
                currentDate = currentDate.AddDays(-1);
                daysBack++;
                consecutiveEmptyDays++;
                
                if (consecutiveEmptyDays >= maxConsecutiveEmptyDays)
                {
                    Console.WriteLine($"Слишком много ошибок подряд. Останавливаю скрапинг.");
                    break;
                }
                
                await Task.Delay(2000); // Увеличиваем задержку при ошибке
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
                
                if (processed % 10 == 0)
                {
                    Console.WriteLine($"Обработано: {processed}/{articles.Count}");
                }
                
                // Небольшая задержка, чтобы не перегружать сервер
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при обработке статьи {article.Url}: {ex.Message}");
                detailedArticles.Add(article); // Добавляем базовую версию
            }
        }
        
        return detailedArticles;
    }

    private string GetDateUrl(DateTime date)
    {
        // Формат: https://panorama.pub/news/DD-MM-YYYY
        return $"{_baseUrl}/{date:dd-MM-yyyy}";
    }

    private async Task<List<Article>> ScrapeDatePageAsync(string url, int maxCount)
    {
        var articles = new List<Article>();
        
        try
        {
            var html = await FetchWithRetryAsync(url);
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
                                href = "https://panorama.pub" + href;
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
                        
                        // Проверяем, что это ссылка на статью из раздела /news/
                        if (IsArticleUrl(href))
                        {
                            var title = ExtractTitle(link);
                            if (!string.IsNullOrEmpty(title) && title.Length > 10 && title.Length < 500)
                            {
                                articles.Add(new Article
                                {
                                    Title = title,
                                    Url = href,
                                    Section = "Новости"
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
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при скрапинге страницы {url}: {ex.Message}");
            throw; // Пробрасываем исключение, чтобы обработать его в основном методе
        }
        
        return articles.DistinctBy(a => a.Url).ToList();
    }

    private async Task<Article> ScrapeArticleDetailsAsync(Article article)
    {
        try
        {
            var html = await FetchWithRetryAsync(article.Url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            
            // Заголовок - пробуем разные селекторы
            if (string.IsNullOrEmpty(article.Title))
            {
                var titleNode = doc.DocumentNode.SelectSingleNode("//h1") ?? 
                               doc.DocumentNode.SelectSingleNode("//h1[@class]") ??
                               doc.DocumentNode.SelectSingleNode("//title");
                if (titleNode != null)
                {
                    article.Title = CleanText(titleNode.InnerText);
                    // Убираем суффикс сайта из title если есть
                    if (article.Title.Contains("|"))
                    {
                        article.Title = article.Title.Split('|')[0].Trim();
                    }
                }
            }
            
            // Основной контент статьи - пробуем разные селекторы
            var contentNodes = doc.DocumentNode.SelectNodes("//article//p") ??
                              doc.DocumentNode.SelectNodes("//div[contains(@class, 'content')]//p") ??
                              doc.DocumentNode.SelectNodes("//div[contains(@class, 'article')]//p") ??
                              doc.DocumentNode.SelectNodes("//div[contains(@class, 'post')]//p") ??
                              doc.DocumentNode.SelectNodes("//div[contains(@class, 'text')]//p") ??
                              doc.DocumentNode.SelectNodes("//main//p");
            
            if (contentNodes != null && contentNodes.Count > 0)
            {
                var contentParts = contentNodes
                    .Select(n => CleanText(n.InnerText))
                    .Where(t => !string.IsNullOrEmpty(t) && t.Length > 20)
                    .ToList();
                
                article.Content = string.Join("\n\n", contentParts);
                
                // Первый абзац как excerpt
                if (contentParts.Any())
                {
                    article.Excerpt = contentParts.First().Substring(0, Math.Min(300, contentParts.First().Length));
                }
            }
            else
            {
                // Если не нашли параграфы, пробуем получить весь текст из article или main
                var mainContent = doc.DocumentNode.SelectSingleNode("//article") ??
                                 doc.DocumentNode.SelectSingleNode("//main");
                if (mainContent != null)
                {
                    var fullText = CleanText(mainContent.InnerText);
                    if (fullText.Length > 100)
                    {
                        article.Content = fullText;
                        article.Excerpt = fullText.Substring(0, Math.Min(300, fullText.Length));
                    }
                }
            }
            
            // Извлекаем метаданные после заголовка h1
            // Структура: дата/время, автор, категория
            var h1Node = doc.DocumentNode.SelectSingleNode("//h1");
            if (h1Node != null)
            {
                // Получаем весь текст после h1 (включая все дочерние элементы)
                var parentNode = h1Node.ParentNode;
                var allTextAfterH1 = "";
                
                if (parentNode != null)
                {
                    // Ищем все текстовые узлы и элементы после h1 в родительском элементе
                    var foundH1 = false;
                    foreach (var child in parentNode.ChildNodes)
                    {
                        if (child == h1Node)
                        {
                            foundH1 = true;
                            continue;
                        }
                        
                        if (foundH1)
                        {
                            var text = CleanText(child.InnerText);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                allTextAfterH1 += text + " ";
                            }
                        }
                    }
                }
                
                // Также пробуем получить текст из следующих элементов
                var nextSibling = h1Node.NextSibling;
                var siblingCount = 0;
                while (nextSibling != null && siblingCount < 10) // Ограничиваем поиск 10 элементами
                {
                    var text = CleanText(nextSibling.InnerText);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        allTextAfterH1 += text + " ";
                    }
                    nextSibling = nextSibling.NextSibling;
                    siblingCount++;
                }
                
                // Ищем дату и время в собранном тексте
                if (!article.PublishDate.HasValue && !string.IsNullOrEmpty(allTextAfterH1))
                {
                    // Паттерн: "сегодня, 09:00" или "вчера, 15:30" или "12.11.2025, 09:00"
                    var dateTimePattern = @"(сегодня|вчера|\d{1,2}[\.\s]+\d{1,2}[\.\s]+\d{4}),\s*(\d{1,2}):(\d{2})";
                    var dateTimeMatch = Regex.Match(allTextAfterH1, dateTimePattern, RegexOptions.IgnoreCase);
                    
                    if (dateTimeMatch.Success)
                    {
                        var datePart = dateTimeMatch.Groups[1].Value.ToLower();
                        var hour = int.Parse(dateTimeMatch.Groups[2].Value);
                        var minute = int.Parse(dateTimeMatch.Groups[3].Value);
                        
                        DateTime publishDate;
                        if (datePart == "сегодня")
                        {
                            publishDate = DateTime.Now.Date.AddHours(hour).AddMinutes(minute);
                        }
                        else if (datePart == "вчера")
                        {
                            publishDate = DateTime.Now.Date.AddDays(-1).AddHours(hour).AddMinutes(minute);
                        }
                        else
                        {
                            // Формат DD.MM.YYYY или DD MM YYYY
                            var datePattern = @"(\d{1,2})[\.\s]+(\d{1,2})[\.\s]+(\d{4})";
                            var dateMatch = Regex.Match(datePart, datePattern);
                            if (dateMatch.Success)
                            {
                                var day = int.Parse(dateMatch.Groups[1].Value);
                                var month = int.Parse(dateMatch.Groups[2].Value);
                                var year = int.Parse(dateMatch.Groups[3].Value);
                                publishDate = new DateTime(year, month, day, hour, minute, 0);
                            }
                            else
                            {
                                publishDate = DateTime.Now.Date.AddHours(hour).AddMinutes(minute);
                            }
                        }
                        
                        article.PublishDate = publishDate;
                    }
                }
                
                // Ищем автора в собранном тексте
                if (string.IsNullOrEmpty(article.Author) && !string.IsNullOrEmpty(allTextAfterH1))
                {
                    // Автор обычно идет после даты/времени
                    // Паттерн: имя и фамилия (обычно 2-3 слова с заглавными буквами)
                    // Ищем в тексте паттерн ФИО
                    var authorPattern = @"\b([А-ЯЁ][а-яё]+\s+[А-ЯЁ][а-яё]+(?:\s+[А-ЯЁ][а-яё]+)?)\b";
                    var authorMatches = Regex.Matches(allTextAfterH1, authorPattern);
                    
                    foreach (Match match in authorMatches)
                    {
                        var potentialAuthor = CleanText(match.Groups[1].Value);
                        
                        // Пропускаем, если это категория или содержит недопустимые символы
                        if (potentialAuthor == "Политика" || potentialAuthor == "Общество" || 
                            potentialAuthor == "Наука" || potentialAuthor == "Экономика" || 
                            potentialAuthor == "Статьи" || potentialAuthor.Contains(":") ||
                            potentialAuthor.Any(char.IsDigit) || potentialAuthor.Length < 5)
                        {
                            continue;
                        }
                        
                        // Проверяем, что это похоже на ФИО (2-3 слова, каждое начинается с заглавной)
                        var words = potentialAuthor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (words.Length >= 2 && words.Length <= 3)
                        {
                            bool isValid = true;
                            foreach (var word in words)
                            {
                                if (word.Length == 0 || !char.IsUpper(word[0]) || word.Length < 2)
                                {
                                    isValid = false;
                                    break;
                                }
                            }
                            
                            if (isValid)
                            {
                                article.Author = potentialAuthor;
                                break;
                            }
                        }
                    }
                }
                
                // Ищем категорию в собранном тексте
                if (string.IsNullOrEmpty(article.Category) && !string.IsNullOrEmpty(allTextAfterH1))
                {
                    var categories = new[] { "Политика", "Общество", "Наука", "Экономика", "Статьи" };
                    foreach (var category in categories)
                    {
                        if (allTextAfterH1.Contains(category, StringComparison.OrdinalIgnoreCase))
                        {
                            article.Category = category;
                            break;
                        }
                    }
                }
            }
            
            // Дополнительный поиск даты в атрибутах time (если не нашли выше)
            if (!article.PublishDate.HasValue)
            {
                var dateNodes = doc.DocumentNode.SelectNodes("//time[@datetime]") ??
                               doc.DocumentNode.SelectNodes("//time") ??
                               doc.DocumentNode.SelectNodes("//span[contains(@class, 'date')]") ??
                               doc.DocumentNode.SelectNodes("//div[contains(@class, 'date')]") ??
                               doc.DocumentNode.SelectNodes("//*[contains(@class, 'published')]");
                
                if (dateNodes != null)
                {
                    foreach (var dateNode in dateNodes)
                    {
                        var dateAttr = dateNode.GetAttributeValue("datetime", "");
                        var dateText = CleanText(dateNode.InnerText);
                        
                        if (!string.IsNullOrEmpty(dateAttr))
                        {
                            if (DateTime.TryParse(dateAttr, out var dt))
                            {
                                article.PublishDate = dt;
                                break;
                            }
                        }
                        
                        if (!string.IsNullOrEmpty(dateText))
                        {
                            // Обрабатываем "сегодня" и "вчера"
                            if (dateText.Contains("сегодня", StringComparison.OrdinalIgnoreCase))
                            {
                                article.PublishDate = DateTime.Now.Date;
                                break;
                            }
                            if (dateText.Contains("вчера", StringComparison.OrdinalIgnoreCase))
                            {
                                article.PublishDate = DateTime.Now.Date.AddDays(-1);
                                break;
                            }
                            
                            // Пробуем разные форматы дат
                            if (DateTime.TryParse(dateText, out var parsedDate))
                            {
                                article.PublishDate = parsedDate;
                                break;
                            }
                            
                            // Формат DD.MM.YYYY
                            var datePattern = @"(\d{1,2})[\.\s]+(\d{1,2})[\.\s]+(\d{4})";
                            var dateMatch = Regex.Match(dateText, datePattern);
                            if (dateMatch.Success)
                            {
                                if (DateTime.TryParse($"{dateMatch.Groups[3].Value}-{dateMatch.Groups[2].Value}-{dateMatch.Groups[1].Value}", out var date))
                                {
                                    article.PublishDate = date;
                                    break;
                                }
                            }
                        }
                    }
                }
                
                // Если не нашли, ищем в HTML напрямую
                if (!article.PublishDate.HasValue)
                {
                    var dateTimePattern = @"(сегодня|вчера|\d{1,2}[\.\s]+\d{1,2}[\.\s]+\d{4}),\s*(\d{1,2}):(\d{2})";
                    var dateTimeMatch = Regex.Match(html, dateTimePattern, RegexOptions.IgnoreCase);
                    
                    if (dateTimeMatch.Success)
                    {
                        var datePart = dateTimeMatch.Groups[1].Value.ToLower();
                        var hour = int.Parse(dateTimeMatch.Groups[2].Value);
                        var minute = int.Parse(dateTimeMatch.Groups[3].Value);
                        
                        DateTime publishDate;
                        if (datePart == "сегодня")
                        {
                            publishDate = DateTime.Now.Date.AddHours(hour).AddMinutes(minute);
                        }
                        else if (datePart == "вчера")
                        {
                            publishDate = DateTime.Now.Date.AddDays(-1).AddHours(hour).AddMinutes(minute);
                        }
                        else
                        {
                            var datePattern = @"(\d{1,2})[\.\s]+(\d{1,2})[\.\s]+(\d{4})";
                            var dateMatch = Regex.Match(datePart, datePattern);
                            if (dateMatch.Success)
                            {
                                var day = int.Parse(dateMatch.Groups[1].Value);
                                var month = int.Parse(dateMatch.Groups[2].Value);
                                var year = int.Parse(dateMatch.Groups[3].Value);
                                publishDate = new DateTime(year, month, day, hour, minute, 0);
                            }
                            else
                            {
                                publishDate = DateTime.Now.Date.AddHours(hour).AddMinutes(minute);
                            }
                        }
                        
                        article.PublishDate = publishDate;
                    }
                    else
                    {
                        // Пробуем просто дату без времени
                        var datePattern = @"(\d{1,2})[\.\s]+(\d{1,2})[\.\s]+(\d{4})";
                        var dateMatch = Regex.Match(html, datePattern);
                        if (dateMatch.Success)
                        {
                            if (DateTime.TryParse($"{dateMatch.Groups[3].Value}-{dateMatch.Groups[2].Value}-{dateMatch.Groups[1].Value}", out var date))
                            {
                                article.PublishDate = date;
                            }
                        }
                    }
                }
            }
            
            // Дополнительный поиск автора (если не нашли выше)
            if (string.IsNullOrEmpty(article.Author))
            {
                var authorNodes = doc.DocumentNode.SelectNodes("//span[contains(@class, 'author')]") ??
                                 doc.DocumentNode.SelectNodes("//div[contains(@class, 'author')]") ??
                                 doc.DocumentNode.SelectNodes("//a[contains(@class, 'author')]");
                
                if (authorNodes != null)
                {
                    var authorText = authorNodes.FirstOrDefault()?.InnerText?.Trim();
                    if (!string.IsNullOrEmpty(authorText))
                    {
                        article.Author = CleanText(authorText);
                    }
                }
            }
            
            // Теги
            var tagNodes = doc.DocumentNode.SelectNodes("//a[contains(@class, 'tag')]") ??
                          doc.DocumentNode.SelectNodes("//span[contains(@class, 'tag')]");
            
            if (tagNodes != null)
            {
                article.Tags = tagNodes
                    .Select(n => CleanText(n.InnerText))
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();
            }
            
            // Изображение
            var imageNode = doc.DocumentNode.SelectSingleNode("//img[contains(@class, 'article')]") ??
                           doc.DocumentNode.SelectSingleNode("//article//img");
            
            if (imageNode != null)
            {
                var imgSrc = imageNode.GetAttributeValue("src", "");
                if (!string.IsNullOrEmpty(imgSrc))
                {
                    if (imgSrc.StartsWith("/"))
                    {
                        article.ImageUrl = _baseUrl + imgSrc;
                    }
                    else if (imgSrc.StartsWith("http"))
                    {
                        article.ImageUrl = imgSrc;
                    }
                }
            }
            
            // Дополнительный поиск категории (если не нашли выше)
            if (string.IsNullOrEmpty(article.Category))
            {
                // Ищем в ссылках и элементах с классом category
                var categoryNode = doc.DocumentNode.SelectSingleNode("//a[contains(@class, 'category')]") ??
                                  doc.DocumentNode.SelectSingleNode("//span[contains(@class, 'category')]") ??
                                  doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'category')]");
                
                if (categoryNode != null)
                {
                    article.Category = CleanText(categoryNode.InnerText);
                }
                else
                {
                    // Ищем категорию в тексте после h1
                    var h1NodeForCategory = doc.DocumentNode.SelectSingleNode("//h1");
                    if (h1NodeForCategory != null)
                    {
                        var followingText = h1NodeForCategory.InnerText + " " + (h1NodeForCategory.NextSibling?.InnerText ?? "");
                        var categories = new[] { "Политика", "Общество", "Наука", "Экономика", "Статьи" };
                        foreach (var category in categories)
                        {
                            if (followingText.Contains(category, StringComparison.OrdinalIgnoreCase))
                            {
                                article.Category = category;
                                break;
                            }
                        }
                    }
                }
            }
            
            // Количество просмотров
            var viewCountText = html;
            var viewMatch = Regex.Match(viewCountText, @"(\d+)\s*(просмотр|view)", RegexOptions.IgnoreCase);
            if (viewMatch.Success && int.TryParse(viewMatch.Groups[1].Value, out var views))
            {
                article.ViewCount = views;
            }
            
            // Количество комментариев
            var commentMatch = Regex.Match(viewCountText, @"(\d+)\s*(комментари|comment)", RegexOptions.IgnoreCase);
            if (commentMatch.Success && int.TryParse(commentMatch.Groups[1].Value, out var comments))
            {
                article.CommentCount = comments;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при получении деталей статьи {article.Url}: {ex.Message}");
        }
        
        return article;
    }

    private string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        
        return System.Net.WebUtility.HtmlDecode(text)
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\t", " ")
            .Trim();
    }


    public async Task SaveToJsonAsync(List<Article> articles, string filename = "panorama_articles.json")
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        
        var json = JsonSerializer.Serialize(articles, options);
        await File.WriteAllTextAsync(filename, json);
        Console.WriteLine($"Сохранено {articles.Count} статей в файл {filename}");
    }

    bool IsArticleUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!url.Contains("panorama.pub")) return false;
        if (url.Contains("#")) return false;
        
        // Исключаем главную страницу и страницы с датами
        if (url == _baseUrl || url == _baseUrl + "/") return false;
        if (Regex.IsMatch(url, @"/news/\d{2}-\d{2}-\d{4}$")) return false; // Страницы с датами
        
        // Разрешаем только /news/ с конкретными статьями (не страницы дат)
        var isNews = url.Contains("/news/") && 
                    !url.Contains("/news?page=") &&
                    !Regex.IsMatch(url, @"/news/\d{2}-\d{2}-\d{4}$"); // Не страницы с датами
        
        // Также разрешаем /articles/
        var isArticle = url.Contains("/articles/");

        // Отсеиваем очевидные служебные/внутренние
        var excludedPaths = new[]
        {
            "/login", "/register", "/about", "/contact", "/search", "/tag", "/author",
            "/hall-of-fame", "/member/", "/help", "/help/"
        };
        if (excludedPaths.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase))) return false;

        return isArticle || isNews;
    }

    private string ExtractTitle(HtmlNode link)
    {
        // Пробуем получить заголовок из разных мест
        var title = link.InnerText?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            title = link.GetAttributeValue("title", "")?.Trim();
        }
        if (string.IsNullOrEmpty(title))
        {
            var titleNode = link.SelectSingleNode(".//span") ?? link.SelectSingleNode(".//strong");
            title = titleNode?.InnerText?.Trim();
        }
        return CleanText(title ?? "");
    }
    
    private async Task<string> FetchWithRetryAsync(string url, int maxRetries = 3)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                if (i == maxRetries - 1) throw;
                Console.WriteLine($"Повторная попытка {i + 1}/{maxRetries} для {url}: {ex.Message}");
                await Task.Delay(1000 * (i + 1)); // Увеличиваем задержку с каждой попыткой
            }
        }
        throw new Exception($"Не удалось загрузить {url} после {maxRetries} попыток");
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

