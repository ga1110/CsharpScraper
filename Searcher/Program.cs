using Searcher.Services;
using Scraper.Models;
using Scraper.Services;

namespace Searcher;

/// <summary>
/// Главный класс приложения для поиска статей в ElasticSearch с поддержкой индексации и интерактивного поиска
/// </summary>
class Program
{
    /// <summary>
    /// Точка входа в приложение. Поддерживает два режима: индексацию статей (index) и интерактивный поиск
    /// </summary>
    /// <param name="args">Аргументы командной строки: "index <путь_к_json>" для индексации или без аргументов для поиска</param>
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        // Настройки подключения к ElasticSearch
        var elasticSearchService = new ElasticSearchService(
            connectionString: "https://localhost:9200",
            username: "elastic",
            password: "muVmg+YxSgExd2NKBttV"
        );
        var indexingService = new IndexingService(elasticSearchService);

        // Проверяем подключение к ElasticSearch
        Console.WriteLine("Проверка подключения к ElasticSearch");
        try
        {
            // Сначала проверяем ping
            if (!await elasticSearchService.PingAsync())
            {
                Console.WriteLine("Ошибка подключения к ElasticSearch");
                return;
            }
            
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

        if (args.Length > 0 && args[0] == "index" && args.Length > 1)
        {
            await indexingService.IndexArticlesFromJsonAsync(args[1]);
            return;
        }

        // Иначе запускаем поиск
        Console.WriteLine("Поисковик статей");
        Console.WriteLine("Введите команды:");
        Console.WriteLine("  scrape <количество> [--clear] - скрапить статьи и проиндексировать в ElasticSearch");
        Console.WriteLine("  index - проиндексировать статьи из articles.json ");
        Console.WriteLine("  search <запрос> [размер=10] - поиск статей");
        Console.WriteLine("  search <запрос> --category <категория> [размер=10] - поиск с фильтром по категории");
        Console.WriteLine("  search <запрос> --author <автор> [размер=10] - поиск с фильтром по автору");
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
                await HandleScrapeCommand(input, elasticSearchService);
            else if (input.ToLower().Trim() == "index")
                await HandleIndexCommand(indexingService, elasticSearchService);
            else if (input.ToLower().StartsWith("search "))
                await HandleSearchCommand(input, elasticSearchService);
            else
                Console.WriteLine("Неизвестная команда. Используйте 'scrape <количество>', 'index', 'search <запрос>' или 'exit'");
        }
    }

    /// <summary>
    /// Обрабатывает команду поиска, парсит аргументы (запрос, размер, фильтры по категории и автору) и выводит результаты
    /// </summary>
    /// <param name="command">Строка команды поиска в формате "search <запрос> [--category <категория>] [--author <автор>] [размер]"</param>
    /// <param name="elasticSearchService">Сервис для выполнения поиска в ElasticSearch</param>
    static async Task HandleSearchCommand(string command, ElasticSearchService elasticSearchService)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length < 2)
        {
            Console.WriteLine("Использование: search <запрос> [размер] или search <запрос> --category <категория> [размер]");
            return;
        }

        string query = string.Empty;
        int size = 10;
        string? category = null;
        string? author = null;

        // Парсим аргументы
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i] == "--category" && i + 1 < parts.Length)
            {
                category = parts[i + 1];
                i++;
            }
            else if (parts[i] == "--author" && i + 1 < parts.Length)
            {
                author = parts[i + 1];
                i++;
            }
            else if (int.TryParse(parts[i], out var parsedSize))
                size = parsedSize;
            else
            {
                if (string.IsNullOrEmpty(query))
                    query = parts[i];
                else
                    query += " " + parts[i];
            }
        }

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
            var result = await elasticSearchService.SearchAsync(query, 0, size, category, author);
            
            Console.WriteLine($"Найдено документов: {result.Total}");
            Console.WriteLine($"Показано: {result.Documents.Count}");
            Console.WriteLine(new string('-', 80));

            for (int i = 0; i < result.Documents.Count; i++)
            {
                var doc = result.Documents[i];
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
                    Console.WriteLine($"   Выделенные фрагменты:");
                    foreach (var highlight in highlights.Take(3))
                        Console.WriteLine($"     ...{highlight}...");
                }
                else if (!string.IsNullOrEmpty(doc.Content))
                {
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

        // Парсим опции
        bool clearJson = parts.Any(p => p == "--clear");
        
        // Парсим количество статей (первый аргумент после "scrape")
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
        
        // Определяем путь к корню проекта для работы с articles.json
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var jsonFilePath = Path.Combine(projectRoot, "articles.json");
        
        // Если указана опция --clear, очищаем файл перед началом скрапинга
        if (clearJson)
        {
            File.WriteAllText(jsonFilePath, "[]");
            Console.WriteLine("Файл очищен");
            Console.WriteLine();
        }
        
        try
        {
            using var scraper = new Scraper.Services.Scraper();
        
            var articles = await scraper.ScrapeArticlesAsync(maxArticles);

            Console.WriteLine();
            Console.WriteLine($"Скрапинг завершен! Собрано статей: {articles.Count}");

            if (articles.Count == 0)
            {
                Console.WriteLine("Статьи не найдены.");
                return;
            }

            // Проверяем на дубликаты URL
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

            // Сохраняем статьи в JSON файл
            
            var scraperUtils = new Scraper.Services.ScraperUtils();
            await scraperUtils.SaveToJsonAsync(articles, jsonFilePath);


            // Всегда индексируем статьи
            Console.WriteLine();
            Console.WriteLine("Начало индексации в ElasticSearch...");
            
            // Очищаем индекс перед индексацией
            Console.WriteLine("Очистка индекса перед индексацией...");
            await elasticSearchService.DeleteIndexAsync();
            
            // Создаем индекс, если его нет
            if (!await elasticSearchService.EnsureIndexExistsAsync())
            {
                Console.WriteLine("Ошибка при создании индекса!");
                return;
            }

            // Индексируем статьи пакетами
            const int batchSize = 100;
            int totalIndexed = 0;
            int totalDuplicatesRemoved = 0;
            
            for (int i = 0; i < articles.Count; i += batchSize)
            {
                var batch = articles.Skip(i).Take(batchSize).ToList();
                var (success, indexedCount, duplicatesRemoved) = await elasticSearchService.IndexArticlesAsync(batch);
                totalIndexed += indexedCount;
                totalDuplicatesRemoved += duplicatesRemoved;
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
            
            // Проверяем фактическое количество документов в индексе
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





