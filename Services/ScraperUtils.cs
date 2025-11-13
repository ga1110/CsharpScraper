
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Scraper.Models;

namespace Scraper.Services;

public class ScraperUtils
{
    public static string GetDateUrl(string url, DateTime date)
    {
        return $"{url}/{date:dd-MM-yyyy}";
    }

    public static string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        return System.Net.WebUtility.HtmlDecode(text)
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\t", " ")
            .Trim();
    }

    public static string? NormalizeUrl(string baseUrl, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var trimmed = url.Trim();
        if (trimmed.StartsWith("//"))
        {
            return $"https:{trimmed}";
        }

        if (trimmed.StartsWith("/"))
        {
            return $"{baseUrl}{trimmed}";
        }

        return trimmed;
    }

    public async Task SaveToJsonAsync(List<Article> articles, string filename = "articles.json")
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var json = JsonSerializer.Serialize(articles, options);
        await File.WriteAllTextAsync(filename, json);
    }

    public static string CleanTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;

        // Убираем суффикс сайта из title если есть
        if (title.Contains("|"))
        {
            title = title.Split('|')[0].Trim();
        }

        // Убираем "Рейтинг: число" в начале заголовка
        title = Regex.Replace(title, @"^Рейтинг:\s*\d+\s+", "", RegexOptions.IgnoreCase);

        // Убираем лишние пробелы
        title = Regex.Replace(title, @"\s+", " ").Trim();

        return title;
    }

    public static string ExtractTitle(HtmlNode link)
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

    public static bool IsArticleUrl(string url, string baseUrl)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!url.Contains("panorama.pub")) return false;
        if (url.Contains("#")) return false;

        // Исключаем главную страницу и страницы с датами
        if (url == baseUrl || url == baseUrl + "/") return false;
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

    public static DateTime? ParseDateTimeFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Паттерн: "сегодня, 09:00" или "вчера, 15:30" или "12.11.2025, 09:00"
        var dateTimePattern = @"(сегодня|вчера|\d{1,2}[\.\s]+\d{1,2}[\.\s]+\d{4}),\s*(\d{1,2}):(\d{2})";
        var dateTimeMatch = Regex.Match(text, dateTimePattern, RegexOptions.IgnoreCase);

        if (dateTimeMatch.Success)
        {
            var datePart = dateTimeMatch.Groups[1].Value.ToLower();
            if (!int.TryParse(dateTimeMatch.Groups[2].Value, out var hour) ||
                !int.TryParse(dateTimeMatch.Groups[3].Value, out var minute))
            {
                return null;
            }

            DateTime publishDate;
            if (datePart == "сегодня")
            {
                publishDate = DateTime.SpecifyKind(DateTime.Now.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
            }
            else if (datePart == "вчера")
            {
                publishDate = DateTime.SpecifyKind(DateTime.Now.Date.AddDays(-1).AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
            }
            else
            {
                // Формат DD.MM.YYYY или DD MM YYYY
                var datePattern = @"(\d{1,2})[\.\s]+(\d{1,2})[\.\s]+(\d{4})";
                var dateMatch = Regex.Match(datePart, datePattern);
                if (dateMatch.Success)
                {
                    if (!int.TryParse(dateMatch.Groups[1].Value, out var day) ||
                        !int.TryParse(dateMatch.Groups[2].Value, out var month) ||
                        !int.TryParse(dateMatch.Groups[3].Value, out var year))
                    {
                        return null;
                    }
                    publishDate = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
                }
                else
                {
                    publishDate = DateTime.SpecifyKind(DateTime.Now.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
                }
            }

            return publishDate;
        }

        // Пробуем просто дату без времени
        var dateOnlyPattern = @"(\d{1,2})[\.\s]+(\d{1,2})[\.\s]+(\d{4})";
        var dateOnlyMatch = Regex.Match(text, dateOnlyPattern);
        if (dateOnlyMatch.Success)
        {
            if (DateTime.TryParse($"{dateOnlyMatch.Groups[3].Value}-{dateOnlyMatch.Groups[2].Value}-{dateOnlyMatch.Groups[1].Value}", out var date))
            {
                return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            }
        }

        return null;
    }

    public static string? ExtractAuthorFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Разбиваем текст на строки для более точного поиска
        var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => CleanText(l))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // Ищем автора - обычно это отдельная строка после даты/времени
        foreach (var line in lines)
        {
            // Пропускаем строки с датой, временем, категорией
            if (line.Contains("сегодня") || line.Contains("вчера") ||
                Regex.IsMatch(line, @"\d{1,2}:\d{2}") ||
                line == "Политика" || line == "Общество" || line == "Наука" ||
                line == "Экономика" || line == "Статьи" || line.Length > 100)
            {
                continue;
            }

            // Проверяем, похоже ли на ФИО (2-3 слова, каждое с заглавной буквы)
            var words = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && words.Length <= 3)
            {
                bool isValid = true;
                foreach (var word in words)
                {
                    if (word.Length < 2 || !char.IsUpper(word[0]) || word.Any(char.IsDigit))
                    {
                        isValid = false;
                        break;
                    }
                }

                if (isValid)
                {
                    return line;
                }
            }
        }

        // Если не нашли по строкам, ищем паттерном в тексте
        var authorPattern = @"\b([А-ЯЁ][а-яё]{2,}\s+[А-ЯЁ][а-яё]{2,}(?:\s+[А-ЯЁ][а-яё]{2,})?)\b";
        var authorMatches = Regex.Matches(text, authorPattern);

        foreach (Match match in authorMatches)
        {
            var potentialAuthor = CleanText(match.Groups[1].Value);

            // Пропускаем категории и недопустимые значения
            if (potentialAuthor == "Политика" || potentialAuthor == "Общество" ||
                potentialAuthor == "Наука" || potentialAuthor == "Экономика" ||
                potentialAuthor == "Статьи" || potentialAuthor.Contains(":") ||
                potentialAuthor.Any(char.IsDigit) || potentialAuthor.Length < 5)
            {
                continue;
            }

            var words = potentialAuthor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && words.Length <= 3)
            {
                bool isValid = true;
                foreach (var word in words)
                {
                    if (word.Length < 2 || !char.IsUpper(word[0]))
                    {
                        isValid = false;
                        break;
                    }
                }

                if (isValid)
                {
                    return potentialAuthor;
                }
            }
        }

        return null;
    }

    public static string? ExtractCategoryFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var categories = new[] { "Политика", "Общество", "Наука", "Экономика", "Статьи" };

        // Ищем точное совпадение в отдельных строках
        var lines = text.Split(new[] { '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => CleanText(l))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        foreach (var category in categories)
        {
            // Проверяем точное совпадение в строках
            if (lines.Contains(category, StringComparer.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        // Если не нашли точное совпадение, ищем в тексте
        foreach (var category in categories)
        {
            if (text.Contains(category, StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        return null;
    }

    public static string? ExtractCategoryFromBreadcrumbs(HtmlDocument doc)
    {
        var ldJsonNodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (ldJsonNodes == null) return null;

        foreach (var node in ldJsonNodes)
        {
            var json = node.InnerText?.Trim();
            if (string.IsNullOrEmpty(json)) continue;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!root.TryGetProperty("@type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var typeValue = typeElement.GetString();
                if (!string.Equals(typeValue, "BreadcrumbList", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!root.TryGetProperty("itemListElement", out var listElement) ||
                    listElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in listElement.EnumerateArray())
                {
                    if (item.TryGetProperty("position", out var position) &&
                        position.ValueKind == JsonValueKind.Number &&
                        position.GetInt32() == 2 &&
                        item.TryGetProperty("name", out var nameElement) &&
                        nameElement.ValueKind == JsonValueKind.String)
                    {
                        var name = nameElement.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            return CleanText(name);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return null;
    }

    public static (string channel, int postId)? ExtractcommentDiscussionInfo(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var postIdMatch = Regex.Match(html, @"_telegram_post_id\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        if (!postIdMatch.Success) return null;

        if (!int.TryParse(postIdMatch.Groups[1].Value, out var postId)) return null;

        var channelMatch = Regex.Match(html, @"dataset\.telegramDiscussion\s*=\s*`([^/`]+)", RegexOptions.IgnoreCase);
        var channel = channelMatch.Success ? channelMatch.Groups[1].Value : "ia_panorama";

        if (string.IsNullOrWhiteSpace(channel))
        {
            channel = "ia_panorama";
        }

        return (channel, postId);
    }
}