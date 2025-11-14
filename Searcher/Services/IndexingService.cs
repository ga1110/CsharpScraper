using Scraper.Models;
using Scraper.Services;

namespace Searcher.Services;

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
        _elasticSearchService = elasticSearchService;
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

        Console.WriteLine($"Загрузка статей из {jsonFilePath}...");
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
            articles = System.Text.Json.JsonSerializer.Deserialize<List<Article>>(json, options);
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.WriteLine($"Ошибка при чтении JSON файла: {ex.Message}");
            Console.WriteLine("Файл articles.json содержит невалидный JSON. Убедитесь, что файл не поврежден.");
            return false;
        }

        if (articles == null || articles.Count == 0)
        {
            Console.WriteLine("Статьи не найдены в файле или файл содержит пустой массив.");
            Console.WriteLine("Выполните команду 'scrape <количество>' для сбора статей.");
            return false;
        }

        Console.WriteLine($"Найдено статей: {articles.Count}");
        
        // Проверяем на дубликаты URL
        var duplicateGroups = articles
            .GroupBy(a => a.Url)
            .Where(g => g.Count() > 1)
            .ToList();
        
        if (duplicateGroups.Any())
        {
            Console.WriteLine();
            Console.WriteLine($"⚠️  Обнаружено дубликатов URL: {duplicateGroups.Count} уникальных URL встречаются несколько раз");
            Console.WriteLine($"   Всего дубликатов: {duplicateGroups.Sum(g => g.Count() - 1)}");
            Console.WriteLine();
            Console.WriteLine("Примеры дубликатов:");
            foreach (var group in duplicateGroups.Take(5))
            {
                Console.WriteLine($"   URL: {group.Key}");
                Console.WriteLine($"   Количество: {group.Count()}");
                foreach (var article in group.Take(3))
                {
                    Console.WriteLine($"      - \"{article.Title}\"");
                }
                if (group.Count() > 3)
                {
                    Console.WriteLine($"      ... и еще {group.Count() - 3}");
                }
            }
            if (duplicateGroups.Count > 5)
            {
                Console.WriteLine($"   ... и еще {duplicateGroups.Count - 5} групп дубликатов");
            }
            Console.WriteLine();
            Console.WriteLine("Удаляем дубликаты, оставляя только первую статью для каждого URL...");
            
            // Удаляем дубликаты, оставляя первую статью для каждого URL
            articles = articles
                .GroupBy(a => a.Url)
                .Select(g => g.First())
                .ToList();
            
            Console.WriteLine($"После удаления дубликатов: {articles.Count} уникальных статей");
            Console.WriteLine();
        }
        
        // Очищаем индекс перед индексацией
        Console.WriteLine("Очистка индекса перед индексацией...");
        await _elasticSearchService.DeleteIndexAsync();
        
        Console.WriteLine("Создание индекса");

        if (!await _elasticSearchService.EnsureIndexExistsAsync())
        {
            Console.WriteLine("Ошибка при создании индекса");
            return false;
        };
        
        const int batchSize = 100;
        int totalIndexed = 0;
        int totalDuplicatesRemoved = 0;
        
        for (int i = 0; i < articles.Count; i += batchSize)
        {
            var batch = articles.Skip(i).Take(batchSize).ToList();
            var (success, indexedCount, duplicatesRemoved) = await _elasticSearchService.IndexArticlesAsync(batch);
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
        var actualCount = await _elasticSearchService.GetTotalDocumentsAsync();
        Console.WriteLine($"Всего документов в индексе: {actualCount}");
        
        if (actualCount != totalIndexed)
        {
            Console.WriteLine();
            Console.WriteLine($"⚠️  Внимание: Расхождение в количестве документов!");
            Console.WriteLine($"   Ожидалось: {totalIndexed}");
            Console.WriteLine($"   Фактически в индексе: {actualCount}");
            Console.WriteLine($"   Разница: {totalIndexed - actualCount}");
        }
        
        return true;
    }
}

