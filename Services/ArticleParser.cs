using HtmlAgilityPack;
using Scraper.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scraper.Services;

/// <summary>
/// Класс для парсинга детальной информации о статье из HTML содержимого страницы
/// </summary>
public class ArticleParser
{
    private readonly string _rootUrl;
    private readonly CommentService? _commentService;

    /// <summary>
    /// Инициализирует новый экземпляр ArticleParser с указанным базовым URL и опциональным сервисом комментариев
    /// </summary>
    /// <param name="rootUrl">Базовый URL сайта для нормализации относительных ссылок</param>
    /// <param name="commentService">Сервис для получения количества комментариев (опционально)</param>
    public ArticleParser(string rootUrl, CommentService? commentService = null)
    {
        _rootUrl = rootUrl;
        _commentService = commentService;
    }

    /// <summary>
    /// Парсит детальную информацию о статье из HTML: заголовок, контент, автора, дату публикации, категорию, изображение и количество комментариев
    /// </summary>
    /// <param name="article">Объект статьи для заполнения данными</param>
    /// <param name="html">HTML содержимое страницы статьи</param>
    /// <returns>Обновленный объект статьи с заполненными полями</returns>
    public async Task<Article> ParseArticleDetailsAsync(Article article, string html)
    {
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Заголовок - пробуем разные селекторы
            if (string.IsNullOrEmpty(article.Title))
            {
                var titleNode = doc.DocumentNode.SelectSingleNode("//h1") ??
                               doc.DocumentNode.SelectSingleNode("//h1[@class]") ??
                               doc.DocumentNode.SelectSingleNode("//title");
                if (titleNode != null)
                    article.Title = ScraperUtils.CleanTitle(ScraperUtils.CleanText(titleNode.InnerText));
            }

            // Очищаем заголовок от рейтинга и других служебных данных
            if (!string.IsNullOrEmpty(article.Title))
                article.Title = ScraperUtils.CleanTitle(article.Title);

            // Автор из метатегов
            ExtractAuthorFromMetaTags(article, doc);

            // Дата из метатегов (Open Graph, Schema.org и т.д.)
            ExtractPublishDateFromMetaTags(article, doc);

            // Основной контент статьи
            ExtractContent(article, doc);

            // Извлекаем метаданные после заголовка h1
            ExtractMetadataAfterH1(article, doc);

            // Дополнительный поиск даты в атрибутах time
            ExtractPublishDateFromTimeNodes(article, doc, html);

            // Дополнительный поиск автора
            ExtractAuthorFromHtmlNodes(article, doc);

            // Категория из бейджа
            ExtractCategoryFromBadge(article, doc);

            // Изображение
            ExtractImageUrl(article, doc);

            // Дополнительный поиск категории
            ExtractCategoryFromHtmlNodes(article, doc);

            // Категория из breadcrumbs
            ExtractCategoryFromBreadcrumbs(article, doc);

            // Количество комментариев
            await ExtractCommentCount(article, html);

            return article;
        }
        catch (Exception)
        {
            return article;
        }
    }

    /// <summary>
    /// Извлекает автора статьи из мета-тегов Open Graph или стандартных мета-тегов автора
    /// </summary>
    /// <param name="article">Объект статьи для заполнения</param>
    /// <param name="doc">HTML документ страницы</param>
    private void ExtractAuthorFromMetaTags(Article article, HtmlDocument doc)
    {
        if (string.IsNullOrEmpty(article.Author))
        {
            var authorMeta = doc.DocumentNode.SelectSingleNode("//meta[@property='article:author']") ??
                             doc.DocumentNode.SelectSingleNode("//meta[@name='author']");
            var authorValue = authorMeta?.GetAttributeValue("content", null);
            if (!string.IsNullOrWhiteSpace(authorValue) &&
                !authorValue.Equals("ИА Панорама", StringComparison.OrdinalIgnoreCase))
                article.Author = ScraperUtils.CleanText(authorValue);
        }
    }

    /// <summary>
    /// Извлекает дату публикации из мета-тегов Open Graph, Schema.org или стандартных мета-тегов
    /// </summary>
    /// <param name="article">Объект статьи для заполнения</param>
    /// <param name="doc">HTML документ страницы</param>
    private void ExtractPublishDateFromMetaTags(Article article, HtmlDocument doc)
    {
        if (article.PublishDate.HasValue) return;

        // Пробуем разные мета-теги для даты публикации
        var dateMeta = doc.DocumentNode.SelectSingleNode("//meta[@property='article:published_time']") ??
                       doc.DocumentNode.SelectSingleNode("//meta[@property='og:published_time']") ??
                       doc.DocumentNode.SelectSingleNode("//meta[@name='publish-date']") ??
                       doc.DocumentNode.SelectSingleNode("//meta[@name='date']") ??
                       doc.DocumentNode.SelectSingleNode("//meta[@itemprop='datePublished']") ??
                       doc.DocumentNode.SelectSingleNode("//meta[@name='DC.date']") ??
                       doc.DocumentNode.SelectSingleNode("//meta[@name='PublicationDate']");

        if (dateMeta != null)
        {
            var dateValue = dateMeta.GetAttributeValue("content", null);
            if (!string.IsNullOrWhiteSpace(dateValue))
            {
                if (DateTime.TryParse(dateValue, out var dt))
                {
                    article.PublishDate = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Извлекает основной контент статьи из параграфов внутри элементов article, main или div с классами content/article/post/text
    /// </summary>
    /// <param name="article">Объект статьи для заполнения</param>
    /// <param name="doc">HTML документ страницы</param>
    private void ExtractContent(Article article, HtmlDocument doc)
    {
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
                .Select(n => ScraperUtils.CleanText(n.InnerText))
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            article.Content = string.Join("\n\n", contentParts);
        }
        else
        {
            // Если не нашли параграфы, пробуем получить весь текст из article или main
            var mainContent = doc.DocumentNode.SelectSingleNode("//article") ??
                             doc.DocumentNode.SelectSingleNode("//main");
            if (mainContent != null)
            {
                var fullText = ScraperUtils.CleanText(mainContent.InnerText);
                if (fullText.Length > 100)
                    article.Content = fullText;
            }
        }
    }

    /// <summary>
    /// Извлекает метаданные (дату, автора, категорию) из текста, следующего после заголовка H1
    /// </summary>
    /// <param name="article">Объект статьи для заполнения</param>
    /// <param name="doc">HTML документ страницы</param>
    private void ExtractMetadataAfterH1(Article article, HtmlDocument doc)
    {
        var h1Node = doc.DocumentNode.SelectSingleNode("//h1");
        if (h1Node == null) return;

        var allTextAfterH1 = GetTextAfterH1(h1Node);

        if (string.IsNullOrEmpty(allTextAfterH1)) return;

        // Ищем дату и время в собранном тексте
        if (!article.PublishDate.HasValue)
            article.PublishDate = ScraperUtils.ParseDateTimeFromText(allTextAfterH1);

        // Ищем автора в собранном тексте
        if (string.IsNullOrEmpty(article.Author))
            article.Author = ScraperUtils.ExtractAuthorFromText(allTextAfterH1) ?? article.Author;

        // Ищем категорию в собранном тексте
        if (string.IsNullOrEmpty(article.Category))
            article.Category = ScraperUtils.ExtractCategoryFromText(allTextAfterH1) ?? article.Category;
    }

    /// <summary>
    /// Собирает весь текст, следующий после указанного узла H1, включая соседние элементы и дочерние узлы родителя
    /// </summary>
    /// <param name="h1Node">HTML узел заголовка H1</param>
    /// <returns>Собранный текст после H1</returns>
    private string GetTextAfterH1(HtmlNode h1Node)
    {
        var allTextAfterH1 = "";

        // Получаем весь текст после h1 (включая все дочерние элементы)
        var parentNode = h1Node.ParentNode;
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
                    var text = ScraperUtils.CleanText(child.InnerText);
                    if (!string.IsNullOrWhiteSpace(text))
                        allTextAfterH1 += text + " ";
                }
            }
        }

        // Также пробуем получить текст из следующих элементов
        var nextSibling = h1Node.NextSibling;
        var siblingCount = 0;
        while (nextSibling != null && siblingCount < 10) // Ограничиваем поиск 10 элементами
        {
            var text = ScraperUtils.CleanText(nextSibling.InnerText);
            if (!string.IsNullOrWhiteSpace(text))
                allTextAfterH1 += text + " ";

            nextSibling = nextSibling.NextSibling;
            siblingCount++;
        }

        return allTextAfterH1;
    }

    /// <summary>
    /// Извлекает дату публикации из элементов time с атрибутом datetime, элементов с классами date/published или из HTML текста
    /// </summary>
    /// <param name="article">Объект статьи для заполнения</param>
    /// <param name="doc">HTML документ страницы</param>
    /// <param name="html">HTML содержимое страницы для парсинга текстовых дат</param>
    private void ExtractPublishDateFromTimeNodes(Article article, HtmlDocument doc, string html)
    {
        if (article.PublishDate.HasValue) return;

        // Пробуем извлечь дату из JSON-LD разметки (Schema.org)
        ExtractPublishDateFromJsonLd(article, doc);

        if (article.PublishDate.HasValue) return;

        var dateNodes = doc.DocumentNode.SelectNodes("//time[@datetime]") ??
                       doc.DocumentNode.SelectNodes("//time") ??
                       doc.DocumentNode.SelectNodes("//span[contains(@class, 'date')]") ??
                       doc.DocumentNode.SelectNodes("//div[contains(@class, 'date')]") ??
                       doc.DocumentNode.SelectNodes("//*[contains(@class, 'published')]") ??
                       doc.DocumentNode.SelectNodes("//*[contains(@class, 'time')]") ??
                       doc.DocumentNode.SelectNodes("//*[@data-date]") ??
                       doc.DocumentNode.SelectNodes("//*[@data-published]");

        if (dateNodes != null)
        {
            foreach (var dateNode in dateNodes)
            {
                // Пробуем разные атрибуты
                var dateAttr = dateNode.GetAttributeValue("datetime", "") ??
                              dateNode.GetAttributeValue("data-date", "") ??
                              dateNode.GetAttributeValue("data-published", "") ??
                              dateNode.GetAttributeValue("content", "");
                var dateText = ScraperUtils.CleanText(dateNode.InnerText);

                if (!string.IsNullOrEmpty(dateAttr))
                {
                    if (DateTime.TryParse(dateAttr, out var dt))
                    {
                        article.PublishDate = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(dateText))
                {
                    // Обрабатываем "сегодня" и "вчера"
                    if (dateText.Contains("сегодня", StringComparison.OrdinalIgnoreCase))
                    {
                        article.PublishDate = DateTime.SpecifyKind(DateTime.Now.Date, DateTimeKind.Unspecified);
                        return;
                    }
                    if (dateText.Contains("вчера", StringComparison.OrdinalIgnoreCase))
                    {
                        article.PublishDate = DateTime.SpecifyKind(DateTime.Now.Date.AddDays(-1), DateTimeKind.Unspecified);
                        return;
                    }

                    // Пробуем разные форматы дат
                    if (DateTime.TryParse(dateText, out var parsedDate))
                    {
                        article.PublishDate = DateTime.SpecifyKind(parsedDate, DateTimeKind.Unspecified);
                        return;
                    }

                    // Формат DD.MM.YYYY
                    var datePattern = @"(\d{1,2})[\.\s]+(\d{1,2})[\.\s]+(\d{4})";
                    var dateMatch = Regex.Match(dateText, datePattern);
                    if (dateMatch.Success)
                    {
                        if (DateTime.TryParse($"{dateMatch.Groups[3].Value}-{dateMatch.Groups[2].Value}-{dateMatch.Groups[1].Value}", out var date))
                        {
                            article.PublishDate = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
                            return;
                        }
                    }
                }
            }
        }

        // Если не нашли, ищем в HTML напрямую
        if (!article.PublishDate.HasValue)
            article.PublishDate = ScraperUtils.ParseDateTimeFromText(html);
    }

    /// <summary>
    /// Извлекает дату публикации из JSON-LD разметки Schema.org
    /// </summary>
    /// <param name="article">Объект статьи для заполнения</param>
    /// <param name="doc">HTML документ страницы</param>
    private void ExtractPublishDateFromJsonLd(Article article, HtmlDocument doc)
    {
        if (article.PublishDate.HasValue) return;

        var ldJsonNodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (ldJsonNodes == null) return;

        foreach (var node in ldJsonNodes)
        {
            var json = node.InnerText?.Trim();
            if (string.IsNullOrEmpty(json)) continue;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                // Пробуем найти datePublished в разных местах
                if (root.TryGetProperty("datePublished", out var datePublished))
                {
                    if (datePublished.ValueKind == JsonValueKind.String)
                    {
                        var dateStr = datePublished.GetString();
                        if (!string.IsNullOrWhiteSpace(dateStr) && DateTime.TryParse(dateStr, out var dt))
                        {
                            article.PublishDate = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                            return;
                        }
                    }
                }

                // Пробуем в массиве @graph
                if (root.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in graph.EnumerateArray())
                    {
                        if (item.TryGetProperty("datePublished", out var graphDatePublished))
                        {
                            if (graphDatePublished.ValueKind == JsonValueKind.String)
                            {
                                var dateStr = graphDatePublished.GetString();
                                if (!string.IsNullOrWhiteSpace(dateStr) && DateTime.TryParse(dateStr, out var dt))
                                {
                                    article.PublishDate = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                                    return;
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }
    }

    /// <summary>
    /// Извлекает автора из элементов, следующих после H1, или из элементов с классами author
    /// </summary>
    /// <param name="article">Объект статьи для заполнения</param>
    /// <param name="doc">HTML документ страницы</param>
    private void ExtractAuthorFromHtmlNodes(Article article, HtmlDocument doc)
    {
        if (!string.IsNullOrEmpty(article.Author)) return;

        // Ищем в HTML напрямую после h1
        var h1ForAuthor = doc.DocumentNode.SelectSingleNode("//h1");
        if (h1ForAuthor != null)
        {
            // Ищем все элементы после h1
            var allNodes = doc.DocumentNode.SelectNodes("//h1/following-sibling::*");
            if (allNodes != null)
            {
                var textAfterH1 = string.Join(" ", allNodes.Take(5).Select(n => ScraperUtils.CleanText(n.InnerText)));
                var extractedAuthor = ScraperUtils.ExtractAuthorFromText(textAfterH1);
                if (!string.IsNullOrEmpty(extractedAuthor))
                    article.Author = extractedAuthor;
            }
        }

        // Также ищем в элементах с классом author
        if (string.IsNullOrEmpty(article.Author))
        {
            var authorNodes = doc.DocumentNode.SelectNodes("//span[contains(@class, 'author')]") ??
                             doc.DocumentNode.SelectNodes("//div[contains(@class, 'author')]") ??
                             doc.DocumentNode.SelectNodes("//a[contains(@class, 'author')]");

            if (authorNodes != null)
            {
                var authorText = authorNodes.FirstOrDefault()?.InnerText?.Trim();
                if (!string.IsNullOrEmpty(authorText))
                    article.Author = ScraperUtils.CleanText(authorText);
            }
        }
    }

    /// <summary>
    /// Извлекает категорию из элемента-бейджа с классом badge
    /// </summary>
    /// <param name="article">Объект статьи для заполнения</param>
    /// <param name="doc">HTML документ страницы</param>
    private void ExtractCategoryFromBadge(Article article, HtmlDocument doc)
    {
        if (string.IsNullOrEmpty(article.Category))
        {
            var categoryBadge = doc.DocumentNode.SelectSingleNode("//a[contains(@class, 'badge')]");
            var badgeValue = categoryBadge != null ? ScraperUtils.CleanText(categoryBadge.InnerText) : string.Empty;
            if (!string.IsNullOrEmpty(badgeValue))
                article.Category = badgeValue;
        }
    }

    /// <summary>
    /// Извлекает URL изображения статьи из элементов с data-bg-image, элементов img, или из мета-тегов Open Graph/Twitter
    /// </summary>
    /// <param name="article">Объект статьи для заполнения</param>
    /// <param name="doc">HTML документ страницы</param>
    private void ExtractImageUrl(Article article, HtmlDocument doc)
    {
        if (string.IsNullOrEmpty(article.ImageUrl))
        {
            var heroNode = doc.DocumentNode.SelectSingleNode("//*[@data-bg-image-webp]") ??
                           doc.DocumentNode.SelectSingleNode("//*[@data-bg-image-jpeg]");

            var heroUrl = heroNode?.GetAttributeValue("data-bg-image-webp", null) ??
                          heroNode?.GetAttributeValue("data-bg-image-jpeg", null);

            var normalizedHero = ScraperUtils.NormalizeUrl(_rootUrl, heroUrl);
            if (!string.IsNullOrEmpty(normalizedHero))
                article.ImageUrl = normalizedHero;
        }

        var imageNode = doc.DocumentNode.SelectSingleNode("//img[contains(@class, 'article')]") ??
                       doc.DocumentNode.SelectSingleNode("//article//img");

        if (imageNode != null)
        {
            var imgSrc = imageNode.GetAttributeValue("src", "");
            var normalized = ScraperUtils.NormalizeUrl(_rootUrl, imgSrc);
            if (!string.IsNullOrEmpty(normalized))
                article.ImageUrl = normalized;
        }

        if (string.IsNullOrEmpty(article.ImageUrl))
        {
            var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", null) ??
                          doc.DocumentNode.SelectSingleNode("//meta[@property='vk:image']")?.GetAttributeValue("content", null) ??
                          doc.DocumentNode.SelectSingleNode("//meta[@name='twitter:image']")?.GetAttributeValue("content", null) ??
                          doc.DocumentNode.SelectSingleNode("//meta[@itemprop='image']")?.GetAttributeValue("content", null);

            var normalized = ScraperUtils.NormalizeUrl(_rootUrl, ogImage);
            if (!string.IsNullOrEmpty(normalized))
                article.ImageUrl = normalized;
        }
    }

    /// <summary>
    /// Извлекает категорию из элементов, следующих после H1, или из элементов с классами category
    /// </summary>
    /// <param name="article">Объект статьи для заполнения</param>
    /// <param name="doc">HTML документ страницы</param>
    private void ExtractCategoryFromHtmlNodes(Article article, HtmlDocument doc)
    {
        if (string.IsNullOrEmpty(article.Category))
        {
            // Ищем в элементах после h1
            var h1ForCategory = doc.DocumentNode.SelectSingleNode("//h1");
            if (h1ForCategory != null)
            {
                var allNodes = doc.DocumentNode.SelectNodes("//h1/following-sibling::*");
                if (allNodes != null)
                {
                    var textAfterH1 = string.Join(" ", allNodes.Take(5).Select(n => ScraperUtils.CleanText(n.InnerText)));
                    var extractedCategory = ScraperUtils.ExtractCategoryFromText(textAfterH1);
                    if (!string.IsNullOrEmpty(extractedCategory))
                        article.Category = extractedCategory;
                }
            }

            // Ищем в ссылках и элементах с классом category
            if (string.IsNullOrEmpty(article.Category))
            {
                var categoryNode = doc.DocumentNode.SelectSingleNode("//a[contains(@class, 'category')]") ??
                                  doc.DocumentNode.SelectSingleNode("//span[contains(@class, 'category')]") ??
                                  doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'category')]");

                if (categoryNode != null)
                    article.Category = ScraperUtils.CleanText(categoryNode.InnerText);
            }
        }
    }

    /// <summary>
    /// Извлекает категорию из JSON-LD разметки BreadcrumbList в HTML документе
    /// </summary>
    /// <param name="article">Объект статьи для заполнения</param>
    /// <param name="doc">HTML документ страницы</param>
    private void ExtractCategoryFromBreadcrumbs(Article article, HtmlDocument doc)
    {
        if (string.IsNullOrEmpty(article.Category))
        {
            var categoryFromBreadcrumbs = ScraperUtils.ExtractCategoryFromBreadcrumbs(doc);
            if (!string.IsNullOrEmpty(categoryFromBreadcrumbs))
                article.Category = categoryFromBreadcrumbs;
        }
    }

    /// <summary>
    /// Извлекает количество комментариев из HTML через Telegram Discussion API или регулярными выражениями
    /// </summary>
    /// <param name="article">Объект статьи для заполнения</param>
    /// <param name="html">HTML содержимое страницы</param>
    private async Task ExtractCommentCount(Article article, string html)
    {
        if (article.CommentCount.HasValue) return;

        if (_commentService != null)
        {
            var commentInfo = ScraperUtils.ExtractcommentDiscussionInfo(html);
            if (commentInfo != null)
            {
                var commentComments = await _commentService.GetCommentCountAsync(commentInfo.Value.channel, commentInfo.Value.postId);
                if (commentComments.HasValue)
                {
                    article.CommentCount = commentComments.Value;
                    return;
                }
            }
        }

        // Пробуем найти в HTML
        var commentMatch = Regex.Match(html, @"(\d+)\s*(комментари|comment)", RegexOptions.IgnoreCase);
        if (commentMatch.Success && int.TryParse(commentMatch.Groups[1].Value, out var htmlComments))
            article.CommentCount = htmlComments;
    }
}

