using System.Text.Json;
using Searcher.Models;

namespace Searcher.Services;

/// <summary>
/// Проверщик орфографии, обучающийся на основе аналитики поисковых запросов
/// </summary>
public class SearchAnalyticsSpellChecker : ISpellChecker
{
    public int Priority => 4;
    public string Name => "SearchAnalytics";

    private readonly Dictionary<string, SearchQueryStats> _queryStats;
    private readonly Dictionary<string, string> _learnedCorrections;
    private readonly string _analyticsFilePath;
    private readonly ElasticSearchService? _searchService;

    public SearchAnalyticsSpellChecker(ElasticSearchService? searchService = null, string? analyticsFilePath = null)
    {
        _searchService = searchService;
        _analyticsFilePath = analyticsFilePath ?? "search_analytics.json";
        _queryStats = LoadAnalytics();
        _learnedCorrections = ExtractCorrections();
    }

    public async Task<SpellCheckResult> TryCorrectAsync(string query, CancellationToken cancellationToken = default)
    {
        // Проверяем выученные исправления
        var normalizedQuery = query.ToLowerInvariant().Trim();
        
        if (_learnedCorrections.TryGetValue(normalizedQuery, out var correction))
        {
            return SpellCheckResult.Correction(query, correction, Name);
        }

        // Если есть доступ к поисковому сервису, проверяем результативность запроса
        if (_searchService != null)
        {
            var searchResult = await _searchService.SearchAsync(query, size: 1);
            
            // Если запрос не дает результатов, пытаемся найти похожие успешные запросы
            if (searchResult.Total == 0)
            {
                var suggestion = FindSimilarSuccessfulQuery(normalizedQuery);
                if (suggestion != null)
                {
                    return SpellCheckResult.Correction(query, suggestion, Name);
                }
            }
        }

        return SpellCheckResult.NoChange(query, Name);
    }

    /// <summary>
    /// Записывает статистику поискового запроса для обучения
    /// </summary>
    public void RecordSearch(string query, int resultsCount, bool wasSuccessful, string? correctedFrom = null)
    {
        var normalizedQuery = query.ToLowerInvariant().Trim();
        
        if (!_queryStats.TryGetValue(normalizedQuery, out var stats))
        {
            stats = new SearchQueryStats
            {
                Query = normalizedQuery,
                FirstSeen = DateTime.UtcNow
            };
            _queryStats[normalizedQuery] = stats;
        }

        stats.SearchCount++;
        stats.LastSeen = DateTime.UtcNow;
        stats.TotalResults += resultsCount;
        stats.SuccessfulSearches += wasSuccessful ? 1 : 0;

        if (!string.IsNullOrEmpty(correctedFrom))
        {
            stats.CorrectedFrom.Add(correctedFrom);
            
            // Обучаемся: если исправленный запрос успешен, запоминаем исправление
            if (wasSuccessful && resultsCount > 0)
            {
                _learnedCorrections[correctedFrom.ToLowerInvariant().Trim()] = normalizedQuery;
            }
        }

        // Периодически сохраняем аналитику
        if (_queryStats.Count % 10 == 0)
        {
            _ = Task.Run(SaveAnalytics);
        }
    }

    /// <summary>
    /// Получает статистику по запросам
    /// </summary>
    public SearchAnalyticsStats GetAnalyticsStats()
    {
        var totalQueries = _queryStats.Count;
        var totalSearches = _queryStats.Values.Sum(s => s.SearchCount);
        var successfulQueries = _queryStats.Values.Count(s => s.SuccessRate > 0.5);
        var learnedCorrections = _learnedCorrections.Count;

        return new SearchAnalyticsStats
        {
            TotalUniqueQueries = totalQueries,
            TotalSearches = totalSearches,
            SuccessfulQueries = successfulQueries,
            LearnedCorrections = learnedCorrections,
            SuccessRate = totalSearches > 0 ? (double)_queryStats.Values.Sum(s => s.SuccessfulSearches) / totalSearches : 0
        };
    }

    private string? FindSimilarSuccessfulQuery(string query)
    {
        var successfulQueries = _queryStats.Values
            .Where(s => s.SuccessRate > 0.7 && s.SearchCount >= 2)
            .ToList();

        var bestMatch = successfulQueries
            .Select(s => new { Query = s.Query, Distance = CalculateLevenshteinDistance(query, s.Query) })
            .Where(m => m.Distance <= 2 && m.Distance > 0)
            .OrderBy(m => m.Distance)
            .FirstOrDefault();

        return bestMatch?.Query;
    }

    private static int CalculateLevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source))
            return string.IsNullOrEmpty(target) ? 0 : target.Length;
        
        if (string.IsNullOrEmpty(target))
            return source.Length;

        var matrix = new int[source.Length + 1, target.Length + 1];

        for (int i = 0; i <= source.Length; i++)
            matrix[i, 0] = i;
        
        for (int j = 0; j <= target.Length; j++)
            matrix[0, j] = j;

        for (int i = 1; i <= source.Length; i++)
        {
            for (int j = 1; j <= target.Length; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                
                matrix[i, j] = Math.Min(
                    Math.Min(
                        matrix[i - 1, j] + 1,
                        matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost
                );
            }
        }

        return matrix[source.Length, target.Length];
    }

    private Dictionary<string, SearchQueryStats> LoadAnalytics()
    {
        try
        {
            if (File.Exists(_analyticsFilePath))
            {
                var json = File.ReadAllText(_analyticsFilePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, SearchQueryStats>>(json);
                return data ?? new Dictionary<string, SearchQueryStats>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка загрузки аналитики: {ex.Message}");
        }

        return new Dictionary<string, SearchQueryStats>();
    }

    private Dictionary<string, string> ExtractCorrections()
    {
        var corrections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var stats in _queryStats.Values)
        {
            if (stats.SuccessRate > 0.7 && stats.CorrectedFrom.Count > 0)
            {
                foreach (var originalQuery in stats.CorrectedFrom)
                {
                    corrections[originalQuery] = stats.Query;
                }
            }
        }

        return corrections;
    }

    private void SaveAnalytics()
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            var json = JsonSerializer.Serialize(_queryStats, options);
            File.WriteAllText(_analyticsFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка сохранения аналитики: {ex.Message}");
        }
    }
}

/// <summary>
/// Статистика по поисковому запросу
/// </summary>
public class SearchQueryStats
{
    public string Query { get; set; } = string.Empty;
    public int SearchCount { get; set; }
    public int SuccessfulSearches { get; set; }
    public int TotalResults { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public HashSet<string> CorrectedFrom { get; set; } = new();
    
    public double SuccessRate => SearchCount > 0 ? (double)SuccessfulSearches / SearchCount : 0;
    public double AvgResults => SearchCount > 0 ? (double)TotalResults / SearchCount : 0;
}

/// <summary>
/// Общая статистика аналитики поиска
/// </summary>
public class SearchAnalyticsStats
{
    public int TotalUniqueQueries { get; set; }
    public int TotalSearches { get; set; }
    public int SuccessfulQueries { get; set; }
    public int LearnedCorrections { get; set; }
    public double SuccessRate { get; set; }
}
