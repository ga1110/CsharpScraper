using System.Collections.Generic;
using System.Text;
using Searcher.Services;
using Scraper.Models;
using Scraper.Services;

namespace Searcher;

/// <summary>
/// Главный класс приложения для поиска статей в ElasticSearch с поддержкой индексации и интерактивного поиска
/// </summary>
class Program
{
    private static readonly char[] TagSeparators = new[] { '=', ':' };

    /// <summary>
    /// Точка входа в приложение. Поддерживает два режима: индексацию статей (index) и интерактивный поиск
    /// </summary>
    /// <param name="args">Аргументы командной строки: "index <путь_к_json>" для индексации или без аргументов для поиска</param>
    static async Task Main(string[] args)
    {
        // Включаем поддержку UTF-8 в консоли, чтобы корректно отображать русские тексты и подсветки
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        // Подготавливаем сервисы ElasticSearch и индексации, чтобы использовать их в обоих режимах работы
        var elasticSearchService = new ElasticSearchService(
            username: "elastic",
            password: "muVmg+YxSgExd2NKBttV"
        );
        var indexingService = new IndexingService(elasticSearchService);

        // Перед работой проверяем доступность кластера и сообщаем пользователю диагностическую информацию
        Console.WriteLine("Проверка подключения к ElasticSearch");
        try
        {
            // Простой ping даёт быстрый ответ о том, жив ли кластер
            if (!await elasticSearchService.PingAsync())
            {
                Console.WriteLine("Ошибка подключения к ElasticSearch");
                return;
            }
            
            // Если пинг успешен — выводим количество документов, чтобы понимать текущее состояние индекса
            var health = await elasticSearchService.GetTotalDocumentsAsync();
            Console.WriteLine($"Подключение успешно. Документов в индексе: {health}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка подключения к ElasticSearch: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Детали: {ex.InnerException.Message}");
            }
            Console.WriteLine();
            Console.WriteLine("Убедитесь, что ElasticSearch запущен и доступен на http://localhost:9200");
            return;
        }

        // Режим "index <path>" позволяет индексировать заранее подготовленный JSON без запуска интерактивного меню
        if (args.Length > 0 && args[0] == "index" && args.Length > 1)
        {
            await indexingService.IndexArticlesFromJsonAsync(args[1]);
            return;
        }

        // Иначе переходим в интерактивный режим, где доступны команды scrape/index/search
        Console.WriteLine("Поисковик статей");
        Console.WriteLine("Введите команды:");
        Console.WriteLine("  scrape <количество> [--clear] - скрапить статьи и проиндексировать в ElasticSearch");
        Console.WriteLine("  index - проиндексировать статьи из articles.json ");
        Console.WriteLine("  search <запрос> [category=<категория>] [author=<автор>] [size=10] - единый поиск с тегами");
        Console.WriteLine("     Теги: category=, author=, size= или --category, --author, --size");
        Console.WriteLine("  exit - выход");
        Console.WriteLine();

        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (input.ToLower() == "exit")
                break;

            if (input.ToLower().StartsWith("scrape "))
                // Команда scrape запускает сбор свежих статей и их последующую индексацию
                await HandleScrapeCommand(input, elasticSearchService);
            else if (input.ToLower().Trim() == "index")
                // Команда index переиспользует существующий JSON, чтобы быстро перестроить индекс
                await HandleIndexCommand(indexingService, elasticSearchService);
            else if (input.ToLower().StartsWith("search "))
                // Команда search выполняет полнотекстовый поиск и выводит результаты в консоль
                await HandleSearchCommand(input, elasticSearchService);
            else
                Console.WriteLine("Неизвестная команда. Используйте 'scrape <количество>', 'index', 'search <запрос>' или 'exit'");
        }
    }

    /// <summary>
    /// Обрабатывает команду поиска, поддерживая теги category/author/size в любом порядке и формате записи
    /// </summary>
    /// <param name="command">Строка команды поиска в формате "search <запрос> [category=<категория>] [author=<автор>] [size=<число>]"</param>
    /// <param name="elasticSearchService">Сервис для выполнения поиска в ElasticSearch</param>
    static async Task HandleSearchCommand(string command, ElasticSearchService elasticSearchService)
    {
        var arguments = command.Length > "search".Length
            ? command.Substring("search".Length).Trim()
            : string.Empty;

        if (string.IsNullOrWhiteSpace(arguments))
        {
            PrintSearchUsage();
            return;
        }

        var tokens = SplitArguments(arguments);

        if (tokens.Count == 0)
        {
            PrintSearchUsage();
            return;
        }

        var queryParts = new List<string>();
        int size = 10;
        string? category = null;
        string? author = null;

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (TryParseSearchTag(token, out var tagType, out var inlineValue, out var requiresNextValue))
            {
                string? tagValue = inlineValue;

                if (requiresNextValue)
                {
                    if (i + 1 >= tokens.Count)
                    {
                        Console.WriteLine($"Тег '{GetTagLabel(tagType)}' требует значение. Пример: category=\"Новости\"");
                        return;
                    }

                    tagValue = tokens[++i];
                }

                if (string.IsNullOrWhiteSpace(tagValue))
                    continue;

                switch (tagType)
                {
                    case SearchTagType.Category:
                        category = tagValue;
                        break;
                    case SearchTagType.Author:
                        author = tagValue;
                        break;
                    case SearchTagType.Size:
                        if (int.TryParse(tagValue, out var parsedSize) && parsedSize > 0)
                            size = parsedSize;
                        else
                            Console.WriteLine($"Размер должен быть положительным числом. Игнорируем значение '{tagValue}'.");
                        break;
                }

                continue;
            }

            queryParts.Add(token);
        }

        var query = string.Join(' ', queryParts).Trim();

        if (string.IsNullOrEmpty(query))
        {
            Console.WriteLine("Запрос не может быть пустым!");
            return;
        }

        Console.WriteLine($"Поиск: '{query}' (результатов: {size})");
        if (!string.IsNullOrEmpty(category))
            Console.WriteLine($"Категория: {category}");
        if (!string.IsNullOrEmpty(author))
            Console.WriteLine($"Автор: {author}");
        Console.WriteLine();

        try
        {
            // Выполняем запрос в ElasticSearch, передавая при необходимости смещение, фильтры и размер выдачи
            var result = await elasticSearchService.SearchAsync(query, 0, size, category, author);
            
            Console.WriteLine($"Найдено документов: {result.Total}");
            Console.WriteLine($"Показано: {result.Documents.Count}");
            Console.WriteLine(new string('-', 80));

            for (int i = 0; i < result.Documents.Count; i++)
            {
                var doc = result.Documents[i];
                // Для каждого результата пробуем показать подсветки; если их нет — выводим короткий фрагмент контента
                var highlights = result.Highlights.ContainsKey(doc.Id) 
                    ? result.Highlights[doc.Id] 
                    : new List<string>();

                Console.WriteLine($"{i + 1}. {doc.Title}");
                Console.WriteLine($"   URL: {doc.Url}");
                if (!string.IsNullOrEmpty(doc.Category))
                    Console.WriteLine($"   Категория: {doc.Category}");
                if (!string.IsNullOrEmpty(doc.Author))
                    Console.WriteLine($"   Автор: {doc.Author}");
                if (doc.PublishDate.HasValue)
                    Console.WriteLine($"   Дата: {doc.PublishDate.Value:yyyy-MM-dd HH:mm}");
                if (doc.CommentCount.HasValue)
                    Console.WriteLine($"   Комментариев: {doc.CommentCount}");
                
                if (highlights.Any())
                {
                    // Подсветки нагляднее показывают, почему документ попал в выдачу
                    Console.WriteLine($"   Выделенные фрагменты:");
                    foreach (var highlight in highlights.Take(3))
                        Console.WriteLine($"     ...{highlight}...");
                }
                else if (!string.IsNullOrEmpty(doc.Content))
                {
                    // Если подсветок нет, выводим превью первых 200 символов текста
                    var preview = doc.Content.Length > 200 
                        ? doc.Content.Substring(0, 200) + "..." 
                        : doc.Content;
                    Console.WriteLine($"   Содержание: {preview}");
                }

                Console.WriteLine(new string('-', 80));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при поиске: {ex.Message}");
        }

        Console.WriteLine();
    }

    static void PrintSearchUsage()
    {
        Console.WriteLine("Использование: search <запрос> [category=<категория>] [author=<автор>] [size=<число>]");
        Console.WriteLine("Теги: category=, author=, size= или --category, --author, --size");
    }

    static List<string> SplitArguments(string arguments)
    {
        var tokens = new List<string>();

        if (string.IsNullOrWhiteSpace(arguments))
            return tokens;

        // Простое разбиение по пробелам, кавычки не обязательны
        // Значения в кавычках сохраняются как один токен, но кавычки не обязательны
        var parts = arguments.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var part in parts)
        {
            // Убираем кавычки с краев, если они есть (для обратной совместимости)
            var trimmed = part.Trim();
            if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) ||
                (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            }
            
            if (!string.IsNullOrEmpty(trimmed))
                tokens.Add(trimmed);
        }

        return tokens;
    }

    static bool TryParseSearchTag(string rawToken, out SearchTagType tagType, out string? inlineValue, out bool expectsNextValue)
    {
        tagType = default;
        inlineValue = null;
        expectsNextValue = false;

        if (string.IsNullOrWhiteSpace(rawToken))
            return false;

        var token = NormalizeToken(rawToken);

        if (TryMatchInlineTag(token, out tagType, out inlineValue))
        {
            inlineValue = inlineValue?.Trim();
            return true;
        }

        if (TryMatchSeparatedTag(token, out tagType))
        {
            expectsNextValue = true;
            return true;
        }

        return false;
    }

    static string NormalizeToken(string token)
    {
        var trimmed = token.Trim();

        while (trimmed.StartsWith("["))
            trimmed = trimmed.Substring(1);

        while (trimmed.EndsWith("]"))
            trimmed = trimmed.Substring(0, trimmed.Length - 1);

        return trimmed;
    }

    static bool TryMatchInlineTag(string token, out SearchTagType tagType, out string? inlineValue)
    {
        tagType = default;
        inlineValue = null;

        if (string.IsNullOrEmpty(token))
            return false;

        var normalized = token;
        var dashCount = 0;
        while (dashCount < normalized.Length && normalized[dashCount] == '-')
            dashCount++;

        if (dashCount > 0)
            normalized = normalized.Substring(dashCount);

        foreach (var separator in TagSeparators)
        {
            var separatorIndex = normalized.IndexOf(separator);
            if (separatorIndex <= 0)
                continue;

            var alias = normalized.Substring(0, separatorIndex).Trim();
            var value = normalized.Substring(separatorIndex + 1).Trim();

            if (TryResolveTag(alias, out tagType))
            {
                inlineValue = value;
                return true;
            }
        }

        return false;
    }

    static bool TryMatchSeparatedTag(string token, out SearchTagType tagType)
    {
        tagType = default;

        if (!token.StartsWith("--", StringComparison.Ordinal))
            return false;

        var alias = token.Substring(2).Trim();

        return TryResolveTag(alias, out tagType);
    }

    static bool TryResolveTag(string candidate, out SearchTagType tagType)
    {
        if (candidate.Equals("category", StringComparison.OrdinalIgnoreCase))
        {
            tagType = SearchTagType.Category;
            return true;
        }

        if (candidate.Equals("author", StringComparison.OrdinalIgnoreCase))
        {
            tagType = SearchTagType.Author;
            return true;
        }

        if (candidate.Equals("size", StringComparison.OrdinalIgnoreCase))
        {
            tagType = SearchTagType.Size;
            return true;
        }

        tagType = default;
        return false;
    }

    static string GetTagLabel(SearchTagType tagType) => tagType switch
    {
        SearchTagType.Category => "category",
        SearchTagType.Author => "author",
        SearchTagType.Size => "size",
        _ => "tag"
    };

    enum SearchTagType
    {
        Category,
        Author,
        Size
    }

    /// <summary>
    /// Обрабатывает команду скрапинга статей и автоматически индексирует их в ElasticSearch
    /// </summary>
    /// <param name="command">Строка команды скрапинга в формате "scrape <количество> [--clear]"</param>
    /// <param name="elasticSearchService">Сервис для работы с ElasticSearch</param>
    static async Task HandleScrapeCommand(string command, ElasticSearchService elasticSearchService)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length < 2)
        {
            Console.WriteLine("Использование: scrape <количество> [--clear]");
            Console.WriteLine("  количество - количество статей для скрапинга и индексации");
            Console.WriteLine("  --clear - очистить articles.json перед сохранением новых статей");
            return;
        }

        // Флаг --clear заставляет очистить существующий JSON перед сохранением новых статей
        bool clearJson = parts.Any(p => p == "--clear");
        
        // Определяем количество статей, перебирая оставшиеся токены, пока не встретим валидное число
        int maxArticles = 0;
        foreach (var part in parts.Skip(1))
        {
            if (part == "--clear")
                continue;
            
            if (int.TryParse(part, out maxArticles) && maxArticles > 0)
                break;
        }

        if (maxArticles <= 0)
        {
            Console.WriteLine($"Ошибка: не удалось определить количество статей.");
            Console.WriteLine("Использование: scrape <количество> [--clear]");
            return;
        }
        
        // Определяем путь к корню решения, чтобы всегда сохранять статьи в один и тот же файл
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var jsonFilePath = Path.Combine(projectRoot, "articles.json");
        
        // Очищаем файл заранее, если пользователь запросил явную перезапись
        if (clearJson)
        {
            File.WriteAllText(jsonFilePath, "[]");
            Console.WriteLine("Файл очищен");
            Console.WriteLine();
        }
        
        try
        {
            // Создаём скрапер и сразу завернём его в using, чтобы корректно освободить ресурсы
            using var scraper = new Scraper.Services.Scraper();
        
            // Сбор статей может занять время, поэтому выполняем его асинхронно
            var articles = await scraper.ScrapeArticlesAsync(maxArticles);

            Console.WriteLine();
            Console.WriteLine($"Скрапинг завершен! Собрано статей: {articles.Count}");

            if (articles.Count == 0)
            {
                Console.WriteLine("Статьи не найдены.");
                return;
            }

            // Дополнительно проверяем наличие дубликатов URL и удаляем их, чтобы индекс не разрастался
            var duplicateGroups = articles
                .GroupBy(a => a.Url)
                .Where(g => g.Count() > 1)
                .ToList();
            
            if (duplicateGroups.Any())
            {
                Console.WriteLine();
                Console.WriteLine($"Обнаружено дубликатов URL: {duplicateGroups.Count} уникальных URL встречаются несколько раз");
                Console.WriteLine($"   Всего дубликатов: {duplicateGroups.Sum(g => g.Count() - 1)}");
                Console.WriteLine("Удаляем дубликаты, оставляя только первую статью для каждого URL...");
                
                // Удаляем дубликаты, оставляя первую статью для каждого URL
                articles = articles
                    .GroupBy(a => a.Url)
                    .Select(g => g.First())
                    .ToList();
                
                Console.WriteLine($"После удаления дубликатов: {articles.Count} уникальных статей");
                Console.WriteLine();
            }

            // После очистки и дедупликации сохраняем итоговый список в JSON
            
            var scraperUtils = new Scraper.Services.ScraperUtils();
            await scraperUtils.SaveToJsonAsync(articles, jsonFilePath);


            // Далее автоматически переиндексируем ElasticSearch, чтобы свежие данные стали доступны в поиске
            Console.WriteLine();
            Console.WriteLine("Начало индексации в ElasticSearch...");
            
            // Перед индексацией гарантированно удаляем старый индекс, чтобы избежать конфликтов схемы
            Console.WriteLine("Очистка индекса перед индексацией...");
            await elasticSearchService.DeleteIndexAsync();
            
            // Создаём новый индекс с корректным маппингом перед загрузкой документов
            if (!await elasticSearchService.EnsureIndexExistsAsync())
            {
                Console.WriteLine("Ошибка при создании индекса!");
                return;
            }

            // Индексируем статьи пакетами, чтобы не перегружать кластер
            const int batchSize = 100;
            int totalIndexed = 0;
            int totalDuplicatesRemoved = 0;
            
            for (int i = 0; i < articles.Count; i += batchSize)
            {
                var batch = articles.Skip(i).Take(batchSize).ToList();
                // ElasticSearchService возвращает, сколько документов реально попало в индекс
                var (success, indexedCount) = await elasticSearchService.IndexArticlesAsync(batch);
                totalIndexed += indexedCount;
                Console.WriteLine($"Проиндексировано: {totalIndexed} из {articles.Count}");
            }

            Console.WriteLine();
            Console.WriteLine($"Индексация завершена! Проиндексировано статей: {totalIndexed}");
            
            if (totalDuplicatesRemoved > 0)
            {
                Console.WriteLine($"   (Удалено дубликатов ID: {totalDuplicatesRemoved})");
            }
            
            // Даем ElasticSearch время обновить индекс перед проверкой
            await Task.Delay(1000);
            
            // Сверяем отчёт по индексации с фактическим количеством документов
            var totalDocs = await elasticSearchService.GetTotalDocumentsAsync();
            Console.WriteLine($"Всего документов в индексе: {totalDocs}");
            
            if (totalDocs != totalIndexed)
            {
                Console.WriteLine();
                Console.WriteLine($"⚠️  Внимание: Расхождение в количестве документов!");
                Console.WriteLine($"   Ожидалось: {totalIndexed}");
                Console.WriteLine($"   Фактически в индексе: {totalDocs}");
                Console.WriteLine($"   Разница: {totalIndexed - totalDocs}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при скрапинге: {ex.Message}");
            if (ex.StackTrace != null)
                Console.WriteLine($"Стек вызовов: {ex.StackTrace}");
            // Даже при ошибке оставляем пустую строку, чтобы визуально отделить события
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Обрабатывает команду индексации статей из articles.json файла в корне проекта
    /// </summary>
    /// <param name="indexingService">Сервис для индексации статей</param>
    /// <param name="elasticSearchService">Сервис для работы с ElasticSearch</param>
    static async Task HandleIndexCommand(IndexingService indexingService, ElasticSearchService elasticSearchService)
    {
        // Определяем путь к корню проекта
        // Если запускается из bin/Debug, идем на 3 уровня выше (Searcher/bin/Debug -> Searcher -> Scraper)
        // Если запускается из проекта напрямую, идем на 1 уровень выше (Searcher -> Scraper)
        var currentDir = Directory.GetCurrentDirectory();
        var jsonFilePath = "articles.json";
        
        // Сначала пробуем файл в текущей директории
        if (!File.Exists(jsonFilePath))
        {
            // Пробуем в родительской директории (корень проекта)
            jsonFilePath = Path.Combine("..", "articles.json");
        }
        
        if (!File.Exists(jsonFilePath))
        {
            // Пробуем через полный путь относительно текущего расположения exe
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
            jsonFilePath = Path.Combine(projectRoot, "articles.json");
        }

        Console.WriteLine($"Индексация статей");
        Console.WriteLine();

        try
        {
            if (!File.Exists(jsonFilePath))
            {
                Console.WriteLine($"Файл articles.json не найден в корне проекта!");
                Console.WriteLine($"Искали в: {Path.GetFullPath(jsonFilePath)}");
                return;
            }

            // Переиспользуем сервис индексации, который уже содержит всю логику разбора JSON
            await indexingService.IndexArticlesFromJsonAsync(jsonFilePath);
            
            // Показываем общее количество документов в индексе после индексации
            var totalDocs = await elasticSearchService.GetTotalDocumentsAsync();
            Console.WriteLine($"Всего документов в индексе: {totalDocs}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при индексации: {ex.Message}");
            if (ex.StackTrace != null)
                Console.WriteLine($"Стек вызовов: {ex.StackTrace}");
        }

        Console.WriteLine();
    }
}





