using Scraper.Models;
using Scraper.Services;
using Searcher.Services.Search;

namespace Searcher.Services.Search.Indexing;

/// <summary>
/// Сервис для индексации статей из JSON файла в ElasticSearch с пакетной обработкой
/// </summary>
public class IndexingService
{
    private readonly ElasticSearchService _elasticSearchService;
    private readonly ScraperUtils _scraperUtils;

    /// <summary>
    /// Инициализирует новый экземпляр IndexingService с указанным сервисом ElasticSearch
    /// </summary>
    /// <param name="elasticSearchService">Сервис для работы с ElasticSearch</param>
    public IndexingService(ElasticSearchService elasticSearchService)
    {
        // Храним ссылку на сервис ElasticSearch, чтобы переиспользовать одно подключение и настройки
        _elasticSearchService = elasticSearchService;
        // Утилиты нужны для сериализации/десериализации дат в едином формате
        _scraperUtils = new ScraperUtils();
    }

    /// <summary>
    /// Загружает статьи из JSON файла, создает индекс при необходимости и индексирует статьи пакетами по 100 штук
    /// </summary>
    /// <param name="jsonFilePath">Путь к JSON файлу со статьями</param>
    /// <returns>true, если индексация прошла успешно, иначе false</returns>
    public async Task<bool> IndexArticlesFromJsonAsync(string jsonFilePath)
    {
        if (!File.Exists(jsonFilePath))
        {
            Console.WriteLine($"Файл {jsonFilePath} не найден!");
            return false;
        }

        Console.WriteLine($"Загрузка статей из {jsonFilePath}");
        var json = await File.ReadAllTextAsync(jsonFilePath);
        
        // Проверяем, что файл не пустой
        if (string.IsNullOrWhiteSpace(json))
            return false;
        
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new CustomDateTimeConverter());
        
        List<Article>? articles;
        try
        {
            // Используем те же конвертеры, что и при сохранении, чтобы корректно распарсить даты и null значения
            articles = System.Text.Json.JsonSerializer.Deserialize<List<Article>>(json, options);
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.WriteLine($"Ошибка при чтении JSON файла: {ex.Message}");
            return false;
        }

        if (articles == null || articles.Count == 0)
        {
            Console.WriteLine("Статьи не найдены в файле или файл содержит пустой массив.");
            Console.WriteLine("Выполните команду 'scrape <количество>' для сбора статей.");
            return false;
        }

        Console.WriteLine($"Найдено статей: {articles.Count}");
        
        // Чтобы избежать конфликтов, перед загрузкой удаляем предыдущий индекс
        await _elasticSearchService.DeleteIndexAsync();
        
        Console.WriteLine("Создание индекса");

        if (!await _elasticSearchService.EnsureIndexExistsAsync())
        {
            Console.WriteLine("Ошибка при создании индекса");
            return false;
        };
        
        const int batchSize = 100;
        int totalIndexed = 0;
        
        for (int i = 0; i < articles.Count; i += batchSize)
        {
            var batch = articles.Skip(i).Take(batchSize).ToList();
            // Результат IndexArticlesAsync сообщает, сколько документов реально попали в индекс
            var (success, indexedCount) = await _elasticSearchService.IndexArticlesAsync(batch);
            totalIndexed += indexedCount;
            Console.WriteLine($"Проиндексировано: {totalIndexed} из {articles.Count}");
        }
        
        Console.WriteLine();
        Console.WriteLine($"Проиндексировано статей: {totalIndexed}");
        
        // Даем ElasticSearch время обновить индекс перед проверкой
        await Task.Delay(1000);
        
        // Проверяем фактическое количество документов в индексе
        var actualCount = await _elasticSearchService.GetTotalDocumentsAsync();
        Console.WriteLine($"Всего документов в индексе: {actualCount}");

        return true;
    }
}

