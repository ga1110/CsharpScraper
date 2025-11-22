using System.Text.Json.Serialization;

namespace Searcher.Models;

/// <summary>
/// Модель данных для хранения словаря синонимов.
/// </summary>
public class SynonymData
{
    /// <summary>
    /// Словарь синонимов: ключ - основное слово, значение - множество синонимов.
    /// </summary>
    [JsonPropertyName("synonyms")]
    public Dictionary<string, HashSet<string>> Synonyms { get; set; } = new();

    /// <summary>
    /// Дата последнего обновления словаря.
    /// </summary>
    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Общее количество групп синонимов.
    /// </summary>
    [JsonPropertyName("totalGroups")]
    public int TotalGroups { get; set; }

    /// <summary>
    /// Оценки уверенности для каждой группы синонимов (0.0 - 1.0).
    /// </summary>
    [JsonPropertyName("confidenceScores")]
    public Dictionary<string, double> ConfidenceScores { get; set; } = new();

    /// <summary>
    /// Статистика майнинга.
    /// </summary>
    [JsonPropertyName("statistics")]
    public MiningStatistics? Statistics { get; set; }
}

/// <summary>
/// Статистика процесса майнинга синонимов.
/// </summary>
public class MiningStatistics
{
    /// <summary>
    /// Общее количество проанализированных слов.
    /// </summary>
    [JsonPropertyName("totalWords")]
    public int TotalWords { get; set; }

    /// <summary>
    /// Общее количество найденных пар синонимов.
    /// </summary>
    [JsonPropertyName("totalPairs")]
    public int TotalPairs { get; set; }

    /// <summary>
    /// Минимальная схожесть среди найденных пар.
    /// </summary>
    [JsonPropertyName("minSimilarity")]
    public double MinSimilarity { get; set; }

    /// <summary>
    /// Средняя схожесть найденных пар.
    /// </summary>
    [JsonPropertyName("avgSimilarity")]
    public double AvgSimilarity { get; set; }

    /// <summary>
    /// Максимальная схожесть среди найденных пар.
    /// </summary>
    [JsonPropertyName("maxSimilarity")]
    public double MaxSimilarity { get; set; }

    /// <summary>
    /// Количество проанализированных статей.
    /// </summary>
    [JsonPropertyName("articlesAnalyzed")]
    public int ArticlesAnalyzed { get; set; }
}

