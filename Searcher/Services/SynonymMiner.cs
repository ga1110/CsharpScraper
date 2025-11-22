using Scraper.Models;
using Searcher.Models;

namespace Searcher.Services;

/// <summary>
/// Основной класс для автоматического майнинга синонимов из статей.
/// </summary>
public class SynonymMiner
{
    private readonly CoOccurrenceAnalyzer _analyzer;
    private readonly SynonymValidator _validator;
    private readonly StopWordsProvider _stopWords;

    public SynonymMiner(StopWordsProvider? stopWords = null)
    {
        _stopWords = stopWords ?? StopWordsProvider.CreateDefault();
        _analyzer = new CoOccurrenceAnalyzer(_stopWords);
        _validator = new SynonymValidator(_stopWords);
    }

    /// <summary>
    /// Основной метод майнинга синонимов из статей.
    /// </summary>
    public Task<SynonymData> MineSynonymsAsync(
        List<Article> articles,
        MiningOptions? options = null)
    {
        options ??= MiningOptions.CreateDefault();

        if (articles == null || articles.Count == 0)
        {
            Console.WriteLine("Нет статей для анализа.");
            return Task.FromResult(new SynonymData());
        }

        Console.WriteLine();
        Console.WriteLine("Начало майнинга синонимов");
        Console.WriteLine();

        var startTime = DateTime.UtcNow;

        var similarities = _analyzer.FindPotentialSynonyms(articles, options);

        if (similarities.Count == 0)
        {
            Console.WriteLine("Синонимы не найдены. Попробуйте снизить порог схожести.");
            return Task.FromResult(new SynonymData());
        }

        var groupedSynonyms = _analyzer.GroupSynonyms(similarities);

        // Шаг 3: Валидация
        var validatedSynonyms = _validator.Validate(groupedSynonyms, articles, options);

        // Шаг 4: Вычисляем статистику
        var statistics = CalculateStatistics(similarities, validatedSynonyms, articles);

        // Шаг 5: Вычисляем оценки уверенности
        var confidenceScores = CalculateConfidenceScores(validatedSynonyms, similarities);

        var endTime = DateTime.UtcNow;
        var duration = endTime - startTime;

        Console.WriteLine("Майнинг завершен");
        Console.WriteLine($"Время выполнения: {duration.TotalSeconds:F2} секунд");
        Console.WriteLine($"Найдено групп: {validatedSynonyms.Count}");
        Console.WriteLine($"Всего пар синонимов: {statistics.TotalPairs}");
        Console.WriteLine($"Средняя схожесть: {statistics.AvgSimilarity:F3}");
        Console.WriteLine();

        return Task.FromResult(new SynonymData
        {
            Synonyms = validatedSynonyms,
            LastUpdated = DateTime.UtcNow,
            TotalGroups = validatedSynonyms.Count,
            ConfidenceScores = confidenceScores,
            Statistics = statistics
        });
    }

    /// <summary>
    /// Вычисляет статистику майнинга.
    /// </summary>
    private MiningStatistics CalculateStatistics(
        Dictionary<string, List<WordSimilarity>> similarities,
        Dictionary<string, HashSet<string>> validatedSynonyms,
        List<Article> articles)
    {
        var allSimilarities = new List<double>();
        var totalPairs = 0;

        foreach (var (word, simList) in similarities)
        {
            foreach (var sim in simList)
            {
                if (validatedSynonyms.ContainsKey(sim.Word1) &&
                    validatedSynonyms[sim.Word1].Contains(sim.Word2))
                {
                    allSimilarities.Add(sim.JaccardSimilarity);
                    totalPairs++;
                }
            }
        }

        return new MiningStatistics
        {
            TotalWords = similarities.Count,
            TotalPairs = totalPairs,
            MinSimilarity = allSimilarities.Count > 0 ? allSimilarities.Min() : 0.0,
            AvgSimilarity = allSimilarities.Count > 0 ? allSimilarities.Average() : 0.0,
            MaxSimilarity = allSimilarities.Count > 0 ? allSimilarities.Max() : 0.0,
            ArticlesAnalyzed = articles.Count
        };
    }

    /// <summary>
    /// Вычисляет оценки уверенности для каждой группы синонимов.
    /// </summary>
    private Dictionary<string, double> CalculateConfidenceScores(
        Dictionary<string, HashSet<string>> validatedSynonyms,
        Dictionary<string, List<WordSimilarity>> similarities)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var (word, synonyms) in validatedSynonyms)
        {
            if (!similarities.ContainsKey(word))
                continue;

            var wordSimilarities = similarities[word];
            var relevantSimilarities = wordSimilarities
                .Where(s => synonyms.Contains(s.Word2))
                .ToList();

            if (relevantSimilarities.Count == 0)
            {
                scores[word] = 0.5; // Средняя уверенность, если нет данных
                continue;
            }

            // Средняя схожесть как оценка уверенности
            var avgSimilarity = relevantSimilarities.Average(s => s.JaccardSimilarity);
            scores[word] = Math.Min(1.0, avgSimilarity);
        }

        return scores;
    }

    /// <summary>
    /// Майнит синонимы из файла со статьями.
    /// </summary>
    public async Task<SynonymData> MineFromJsonFileAsync(
        string jsonFilePath,
        MiningOptions? options = null)
    {
        if (!File.Exists(jsonFilePath))
        {
            Console.WriteLine($"Файл {jsonFilePath} не найден!");
            return new SynonymData();
        }

        Console.WriteLine($"Загрузка статей из {jsonFilePath}");
        var json = await File.ReadAllTextAsync(jsonFilePath);

        if (string.IsNullOrWhiteSpace(json))
        {
            Console.WriteLine("Файл пуст!");
            return new SynonymData();
        }

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        jsonOptions.Converters.Add(new Scraper.Services.CustomDateTimeConverter());

        List<Article>? articles;
        try
        {
            articles = System.Text.Json.JsonSerializer.Deserialize<List<Article>>(json, jsonOptions);
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.WriteLine($"Ошибка при чтении JSON файла: {ex.Message}");
            return new SynonymData();
        }

        if (articles == null || articles.Count == 0)
        {
            Console.WriteLine("Статьи не найдены в файле.");
            return new SynonymData();
        }

        return await MineSynonymsAsync(articles, options);
    }
}

