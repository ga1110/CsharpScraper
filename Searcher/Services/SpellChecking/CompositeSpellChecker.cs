using System.Diagnostics;

using Searcher.Models;
using Searcher.Services.Search;

namespace Searcher.Services.SpellChecking;

/// <summary>
/// Композитный проверщик орфографии, объединяющий несколько методов
/// </summary>
public class CompositeSpellChecker : IDisposable
{
    public readonly List<ISpellChecker> _checkers;
    private readonly Dictionary<string, SpellCheckResult> _cache;
    private readonly int _maxCacheSize;
    private readonly TimeSpan _cacheExpiry;
    private readonly Dictionary<string, DateTime> _cacheTimestamps;

    public CompositeSpellChecker(int maxCacheSize = 1000, TimeSpan? cacheExpiry = null, ElasticSearchService? searchService = null)
    {
        _maxCacheSize = maxCacheSize;
        _cacheExpiry = cacheExpiry ?? TimeSpan.FromHours(1);
        _cache = new Dictionary<string, SpellCheckResult>(StringComparer.OrdinalIgnoreCase);
        _cacheTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        
        _checkers = new List<ISpellChecker>
        {
            new LevenshteinSpellChecker(),
            new KeyboardLayoutSpellChecker(),
            new PhoneticSpellChecker(),
            new SearchAnalyticsSpellChecker(searchService)
        };
        
        // Сортируем по приоритету
        _checkers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    /// <summary>
    /// Добавляет дополнительный проверщик (например, Ollama)
    /// </summary>
    public void AddChecker(ISpellChecker checker)
    {
        _checkers.Add(checker);
        _checkers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    /// <summary>
    /// Пытается исправить опечатки, используя все доступные методы
    /// </summary>
    public async Task<DetailedSpellCheckResult> TryCorrectAsync(string query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        
        // Проверяем кэш
        var cacheKey = query.ToLowerInvariant().Trim();
        if (_cache.TryGetValue(cacheKey, out var cached) && 
            _cacheTimestamps.TryGetValue(cacheKey, out var timestamp) &&
            DateTime.UtcNow - timestamp < _cacheExpiry)
        {
                return new DetailedSpellCheckResult
                {
                    OriginalQuery = query,
                    CorrectedQuery = cached.CorrectedQuery,
                    Confidence = 1.0,
                    ProcessingTime = stopwatch.Elapsed,
                    Steps = new List<CorrectionStep>
                    {
                        new CorrectionStep
                        {
                            Method = "Cache",
                            Before = query,
                            After = cached.CorrectedQuery,
                            Confidence = 1.0,
                            Reason = "Cached result"
                        }
                    }
                };
        }

        var result = new DetailedSpellCheckResult
        {
            OriginalQuery = query,
            CorrectedQuery = query,
            Steps = new List<CorrectionStep>()
        };

        var currentQuery = query;
        var totalConfidence = 1.0;

        // Применяем проверщики по порядку приоритета
        foreach (var checker in _checkers)
        {
            try
            {
                var checkResult = await checker.TryCorrectAsync(currentQuery, cancellationToken);
                
                if (checkResult.Success && checkResult.HasCorrection)
                {
                    result.Steps.Add(new CorrectionStep
                    {
                        Method = checker.Name,
                        Before = currentQuery,
                        After = checkResult.CorrectedQuery,
                        Confidence = 0.8, // Default confidence for corrections
                        Reason = $"Corrected by {checker.Name}"
                    });

                    currentQuery = checkResult.CorrectedQuery;
                    totalConfidence *= 0.8;
                }
            }
            catch (Exception ex)
            {
                // Логируем ошибку, но продолжаем с другими проверщиками
                Console.WriteLine($"Error in {checker.Name}: {ex.Message}");
            }
        }

        result.CorrectedQuery = currentQuery;
        result.Confidence = totalConfidence;
        result.ProcessingTime = stopwatch.Elapsed;

        // Кэшируем результат
        var cacheResult = result.HasCorrection 
            ? SpellCheckResult.Correction(query, currentQuery, "Composite")
            : SpellCheckResult.NoChange(query, "Composite");
        CacheResult(cacheKey, cacheResult);

        return result;
    }

    /// <summary>
    /// Простая версия для обратной совместимости
    /// </summary>
    public async Task<SpellCheckResult> TryCorrectSimpleAsync(string query, CancellationToken cancellationToken = default)
    {
        var detailed = await TryCorrectAsync(query, cancellationToken);
        
        return detailed.HasCorrection 
            ? SpellCheckResult.Correction(detailed.OriginalQuery, detailed.CorrectedQuery, "Composite")
            : SpellCheckResult.NoChange(detailed.OriginalQuery, "Composite");
    }

    /// <summary>
    /// Получает статистику работы проверщиков
    /// </summary>
    public SpellCheckerStats GetStats()
    {
        return new SpellCheckerStats
        {
            CacheSize = _cache.Count,
            MaxCacheSize = _maxCacheSize,
            CheckersCount = _checkers.Count,
            CheckerNames = _checkers.Select(c => c.Name).ToList()
        };
    }

    private void CacheResult(string key, SpellCheckResult result)
    {
        // Простая LRU логика - удаляем старые записи при превышении лимита
        if (_cache.Count >= _maxCacheSize)
        {
            var oldestKey = _cacheTimestamps
                .OrderBy(kvp => kvp.Value)
                .First().Key;
                
            _cache.Remove(oldestKey);
            _cacheTimestamps.Remove(oldestKey);
        }

        _cache[key] = result;
        _cacheTimestamps[key] = DateTime.UtcNow;
    }

    public void Dispose()
    {
        foreach (var checker in _checkers.OfType<IDisposable>())
        {
            checker.Dispose();
        }
    }
}

/// <summary>
/// Статистика работы композитного проверщика
/// </summary>
public class SpellCheckerStats
{
    public int CacheSize { get; set; }
    public int MaxCacheSize { get; set; }
    public int CheckersCount { get; set; }
    public List<string> CheckerNames { get; set; } = new();
}
