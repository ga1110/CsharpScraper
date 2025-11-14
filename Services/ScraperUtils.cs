
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Scraper.Models;

namespace Scraper.Services;

/// <summary>
/// Конвертер для кастомной сериализации/десериализации DateTime в формате "yyyy-MM-dd|HH:mm:ss"
/// </summary>
public class CustomDateTimeConverter : JsonConverter<DateTime?>
{
    /// <summary>
    /// Читает DateTime из JSON строки, заменяя символы "|" и ";" на "T" для корректного парсинга
    /// </summary>
    /// <param name="reader">JSON reader для чтения значения</param>
    /// <param name="typeToConvert">Тип для конвертации</param>
    /// <param name="options">Опции сериализации</param>
    /// <returns>Распарсенное значение DateTime или null, если парсинг не удался</returns>
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Replace("|", "T").Replace(";", "T");
        
        if (DateTime.TryParse(value, out var date))
        {
            return date;
        }
        return null;
    }

    /// <summary>
    /// Записывает DateTime в JSON строку в формате "yyyy-MM-dd|HH:mm:ss"
    /// </summary>
    /// <param name="writer">JSON writer для записи значения</param>
    /// <param name="value">Значение DateTime для записи</param>
    /// <param name="options">Опции сериализации</param>
    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            var formatted = value.Value.ToString("yyyy-MM-dd|HH:mm:ss");
            writer.WriteStringValue(formatted);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// Утилитный класс для работы со скрапингом: валидация, нормализация и извлечение данных из текста и HTML
/// </summary>
public class ScraperUtils
{
    private static readonly string[] Categories = { "Политика", "Общество", "Наука", "Экономика", "Статьи" };
    private static readonly string DatePattern = @"(\d{1,2})[\.\s]+(\d{1,2})[\.\s]+(\d{4})";

    /// <summary>
    /// Проверяет, является ли строка валидным именем автора (2-3 слова, каждое с заглавной буквы)
    /// </summary>
    /// <param name="name">Имя для проверки</param>
    /// <param name="allowDigits">Разрешить наличие цифр в имени</param>
    /// <returns>true, если имя валидно, иначе false</returns>
    private static bool IsValidAuthorName(string name, bool allowDigits = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var words = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words.Length > 3) return false;

        foreach (var word in words)
        {
            if (word.Length < 2 || !char.IsUpper(word[0]) || (!allowDigits && word.Any(char.IsDigit)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Проверяет, является ли текст одной из известных категорий
    /// </summary>
    /// <param name="text">Текст для проверки</param>
    /// <returns>true, если текст является категорией, иначе false</returns>
    private static bool IsCategory(string text)
    {
        return Categories.Contains(text, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Формирует URL для страницы с указанной датой в формате "url/dd-MM-yyyy"
    /// </summary>
    /// <param name="url">Базовый URL</param>
    /// <param name="date">Дата для формирования URL</param>
    /// <returns>URL с добавленной датой</returns>
    public static string GetDateUrl(string url, DateTime date)
    {
        return $"{url}/{date:dd-MM-yyyy}";
    }

    /// <summary>
    /// Очищает текст от HTML-сущностей, переносов строк и лишних пробелов
    /// </summary>
    /// <param name="text">Исходный текст для очистки</param>
    /// <returns>Очищенный текст</returns>
    public static string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        return System.Net.WebUtility.HtmlDecode(text)
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\t", " ")
            .Trim();
    }

    /// <summary>
    /// Нормализует относительные URL в абсолютные, обрабатывая протокол-относительные и абсолютные пути
    /// </summary>
    /// <param name="baseUrl">Базовый URL для формирования абсолютных путей</param>
    /// <param name="url">URL для нормализации</param>
    /// <returns>Нормализованный URL или null, если входной URL пустой</returns>
    public static string? NormalizeUrl(string baseUrl, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var trimmed = url.Trim();
        if (trimmed.StartsWith("//"))
            return $"https:{trimmed}";

        if (trimmed.StartsWith("/"))
            return $"{baseUrl}{trimmed}";

        return trimmed;
    }

    /// <summary>
    /// Сохраняет список статей в JSON файл с отступами и кастомным форматированием дат
    /// </summary>
    /// <param name="articles">Список статей для сохранения</param>
    /// <param name="filename">Имя файла для сохранения (по умолчанию "articles.json")</param>
    public async Task SaveToJsonAsync(List<Article> articles, string filename = "articles.json")
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new CustomDateTimeConverter() }
        };

        var json = JsonSerializer.Serialize(articles, options);
        await File.WriteAllTextAsync(filename, json);
    }

    /// <summary>
    /// Очищает заголовок от суффикса сайта, рейтинга, количества комментариев и лишних пробелов
    /// </summary>
    /// <param name="title">Исходный заголовок</param>
    /// <returns>Очищенный заголовок</returns>
    public static string CleanTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;

        // Убираем суффикс сайта из title если есть
        if (title.Contains("|"))
            title = title.Split('|')[0].Trim();

        // Убираем "Рейтинг: число" в начале заголовка
        title = Regex.Replace(title, @"^Рейтинг:\s*\d+\s+", "", RegexOptions.IgnoreCase);

        // Убираем количество комментариев в начале заголовка (например, "13 комментариев", "27 комментариев")
        title = Regex.Replace(title, @"^\d+\s+комментари(ев|й|я)\s+", "", RegexOptions.IgnoreCase);

        // Убираем лишние пробелы
        title = Regex.Replace(title, @"\s+", " ").Trim();

        return title;
    }

    /// <summary>
    /// Извлекает заголовок из HTML-элемента ссылки, проверяя InnerText, атрибут title и дочерние элементы
    /// </summary>
    /// <param name="link">HTML-узел ссылки для извлечения заголовка</param>
    /// <returns>Извлеченный и очищенный заголовок</returns>
    public static string ExtractTitle(HtmlNode link)
    {
        // Пробуем получить заголовок из разных мест
        var title = link.InnerText?.Trim();
        if (string.IsNullOrEmpty(title))
            title = link.GetAttributeValue("title", "")?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            var titleNode = link.SelectSingleNode(".//span") ?? link.SelectSingleNode(".//strong");
            title = titleNode?.InnerText?.Trim();
        }
        return CleanTitle(CleanText(title ?? ""));
    }

    /// <summary>
    /// Проверяет, является ли URL ссылкой на статью, исключая служебные страницы, главную и страницы с датами
    /// </summary>
    /// <param name="url">URL для проверки</param>
    /// <param name="baseUrl">Базовый URL сайта</param>
    /// <returns>true, если URL является ссылкой на статью, иначе false</returns>
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

    /// <summary>
    /// Парсит дату и время из текста, поддерживая форматы "сегодня/вчера, HH:mm" и "DD.MM.YYYY, HH:mm" или только дату
    /// </summary>
    /// <param name="text">Текст для парсинга даты и времени</param>
    /// <returns>Распарсенное значение DateTime или null, если парсинг не удался</returns>
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
                return null;

            DateTime publishDate;
            if (datePart == "сегодня")
                publishDate = DateTime.SpecifyKind(DateTime.Now.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
            else if (datePart == "вчера")
                publishDate = DateTime.SpecifyKind(DateTime.Now.Date.AddDays(-1).AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
            else
            {
                // Формат DD.MM.YYYY или DD MM YYYY
                var dateMatch = Regex.Match(datePart, DatePattern);
                if (dateMatch.Success)
                {
                    if (!int.TryParse(dateMatch.Groups[1].Value, out var day) ||
                        !int.TryParse(dateMatch.Groups[2].Value, out var month) ||
                        !int.TryParse(dateMatch.Groups[3].Value, out var year))
                        return null;
                    publishDate = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
                }
                else
                    publishDate = DateTime.SpecifyKind(DateTime.Now.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
            }

            return publishDate;
        }

        // Пробуем просто дату без времени
        var dateOnlyMatch = Regex.Match(text, DatePattern);
        if (dateOnlyMatch.Success)
        {
            if (DateTime.TryParse($"{dateOnlyMatch.Groups[3].Value}-{dateOnlyMatch.Groups[2].Value}-{dateOnlyMatch.Groups[1].Value}", out var date))
                return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        }

        return null;
    }

    /// <summary>
    /// Извлекает имя автора из текста, ища валидные ФИО (2-3 слова с заглавной буквы), исключая категории и строки с датами
    /// </summary>
    /// <param name="text">Текст для поиска автора</param>
    /// <returns>Имя автора или null, если не найдено</returns>
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
                IsCategory(line) || line.Length > 100)
                continue;

            // Проверяем, похоже ли на ФИО (2-3 слова, каждое с заглавной буквы)
            if (IsValidAuthorName(line))
                return line;
        }

        // Если не нашли по строкам, ищем паттерном в тексте
        var authorPattern = @"\b([А-ЯЁ][а-яё]{2,}\s+[А-ЯЁ][а-яё]{2,}(?:\s+[А-ЯЁ][а-яё]{2,})?)\b";
        var authorMatches = Regex.Matches(text, authorPattern);

        foreach (Match match in authorMatches)
        {
            var potentialAuthor = CleanText(match.Groups[1].Value);

            // Пропускаем категории и недопустимые значения
            if (IsCategory(potentialAuthor) || potentialAuthor.Contains(":") ||
                potentialAuthor.Any(char.IsDigit) || potentialAuthor.Length < 5)
                continue;

            if (IsValidAuthorName(potentialAuthor, allowDigits: false))
                return potentialAuthor;
        }

        return null;
    }

    /// <summary>
    /// Извлекает категорию из текста, проверяя соответствие известным категориям
    /// </summary>
    /// <param name="text">Текст для поиска категории</param>
    /// <returns>Название категории или null, если не найдено</returns>
    public static string? ExtractCategoryFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Ищем точное совпадение в отдельных строках
        var lines = text.Split(new[] { '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => CleanText(l))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        foreach (var category in Categories)
        {
            // Проверяем точное совпадение в строках
            if (lines.Contains(category, StringComparer.OrdinalIgnoreCase))
                return category;
        }

        // Если не нашли точное совпадение, ищем в тексте
        foreach (var category in Categories)
        {
            if (text.Contains(category, StringComparison.OrdinalIgnoreCase))
                return category;
        }

        return null;
    }

    /// <summary>
    /// Извлекает категорию из JSON-LD разметки BreadcrumbList в HTML документе
    /// </summary>
    /// <param name="doc">HTML документ для поиска категории</param>
    /// <returns>Название категории из breadcrumbs или null, если не найдено</returns>
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
                    continue;

                var typeValue = typeElement.GetString();
                if (!string.Equals(typeValue, "BreadcrumbList", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!root.TryGetProperty("itemListElement", out var listElement) ||
                    listElement.ValueKind != JsonValueKind.Array)
                    continue;

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
                            return CleanText(name);
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

    /// <summary>
    /// Извлекает информацию о Telegram-обсуждении (канал и ID поста) из HTML
    /// </summary>
    /// <param name="html">HTML содержимое страницы</param>
    /// <returns>Кортеж с названием канала и ID поста, или null, если не найдено</returns>
    public static (string channel, int postId)? ExtractcommentDiscussionInfo(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var postIdMatch = Regex.Match(html, @"_telegram_post_id\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        if (!postIdMatch.Success) return null;

        if (!int.TryParse(postIdMatch.Groups[1].Value, out var postId)) return null;

        var channelMatch = Regex.Match(html, @"dataset\.telegramDiscussion\s*=\s*`([^/`]+)", RegexOptions.IgnoreCase);
        var channel = channelMatch.Success ? channelMatch.Groups[1].Value : "ia_panorama";

        if (string.IsNullOrWhiteSpace(channel))
            channel = "ia_panorama";

        return (channel, postId);
    }
}