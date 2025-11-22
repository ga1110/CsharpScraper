using System.Text.Json.Serialization;

namespace Searcher.Models;

/// <summary>
/// Модель для хранения оценки одного поискового запроса
/// </summary>
public class QueryEvaluation
{
    /// <summary>
    /// Уникальный идентификатор запроса
    /// </summary>
    public string QueryId { get; set; } = string.Empty;
    
    /// <summary>
    /// Текст поискового запроса
    /// </summary>
    public string QueryText { get; set; } = string.Empty;
    
    /// <summary>
    /// Время выполнения запроса
    /// </summary>
    public DateTime Timestamp { get; set; }
    
    /// <summary>
    /// Список результатов с оценками релевантности
    /// </summary>
    public List<ResultRelevance> Results { get; set; } = new();
    
    /// <summary>
    /// Фильтр по категории (если использовался)
    /// </summary>
    public string? Category { get; set; }
    
    /// <summary>
    /// Фильтр по автору (если использовался)
    /// </summary>
    public string? Author { get; set; }
    
    /// <summary>
    /// Общее количество найденных документов
    /// </summary>
    public long TotalFound { get; set; }
    
    /// <summary>
    /// Комментарий оценщика к запросу
    /// </summary>
    public string? Comment { get; set; }
}

/// <summary>
/// Модель для хранения оценки релевантности одного результата поиска
/// </summary>
public class ResultRelevance
{
    /// <summary>
    /// Позиция в выдаче (1, 2, 3, ...)
    /// </summary>
    public int Position { get; set; }
    
    /// <summary>
    /// Идентификатор документа в ElasticSearch
    /// </summary>
    public string DocumentId { get; set; } = string.Empty;
    
    /// <summary>
    /// Заголовок статьи
    /// </summary>
    public string Title { get; set; } = string.Empty;
    
    /// <summary>
    /// URL статьи
    /// </summary>
    public string Url { get; set; } = string.Empty;
    
    /// <summary>
    /// Категория статьи
    /// </summary>
    public string? Category { get; set; }
    
    /// <summary>
    /// Автор статьи
    /// </summary>
    public string? Author { get; set; }
    
    /// <summary>
    /// Оценка релевантности: 0 = нерелевантно, 1 = частично релевантно, 2 = очень релевантно
    /// </summary>
    public int RelevanceScore { get; set; }
    
    /// <summary>
    /// Комментарий оценщика к результату
    /// </summary>
    public string? Comment { get; set; }
}

/// <summary>
/// Модель для хранения рассчитанных метрик качества поиска для одного запроса
/// </summary>
public class EvaluationMetrics
{
    /// <summary>
    /// Идентификатор запроса
    /// </summary>
    public string QueryId { get; set; } = string.Empty;
    
    /// <summary>
    /// Текст запроса
    /// </summary>
    public string QueryText { get; set; } = string.Empty;
    
    /// <summary>
    /// Precision@1 - точность на первой позиции
    /// </summary>
    public double PrecisionAt1 { get; set; }
    
    /// <summary>
    /// Precision@5 - точность в топ-5
    /// </summary>
    public double PrecisionAt5 { get; set; }
    
    /// <summary>
    /// Precision@10 - точность в топ-10
    /// </summary>
    public double PrecisionAt10 { get; set; }
    
    /// <summary>
    /// Average Precision - средняя точность для данного запроса
    /// </summary>
    public double AveragePrecision { get; set; }
    
    /// <summary>
    /// NDCG (Normalized Discounted Cumulative Gain) - нормализованный дисконтированный выигрыш
    /// </summary>
    public double NDCG { get; set; }
    
    /// <summary>
    /// Reciprocal Rank - обратный ранг первого релевантного документа
    /// </summary>
    public double ReciprocalRank { get; set; }
    
    /// <summary>
    /// Количество релевантных документов в выдаче
    /// </summary>
    public int RelevantCount { get; set; }
    
    /// <summary>
    /// Общее количество документов в выдаче
    /// </summary>
    public int TotalCount { get; set; }
}

/// <summary>
/// Модель для хранения сводной статистики по всем оценкам
/// </summary>
public class OverallEvaluationStats
{
    /// <summary>
    /// Общее количество оцененных запросов
    /// </summary>
    public int TotalQueries { get; set; }
    
    /// <summary>
    /// MAP (Mean Average Precision) - средняя точность по всем запросам
    /// </summary>
    public double MAP { get; set; }
    
    /// <summary>
    /// Mean NDCG - средний NDCG по всем запросам
    /// </summary>
    public double MeanNDCG { get; set; }
    
    /// <summary>
    /// MRR (Mean Reciprocal Rank) - средний обратный ранг
    /// </summary>
    public double MRR { get; set; }
    
    /// <summary>
    /// Средний Precision@1
    /// </summary>
    public double MeanPrecisionAt1 { get; set; }
    
    /// <summary>
    /// Средний Precision@5
    /// </summary>
    public double MeanPrecisionAt5 { get; set; }
    
    /// <summary>
    /// Средний Precision@10
    /// </summary>
    public double MeanPrecisionAt10 { get; set; }
    
    /// <summary>
    /// Общее количество оцененных документов
    /// </summary>
    public int TotalDocumentsEvaluated { get; set; }
    
    /// <summary>
    /// Общее количество релевантных документов
    /// </summary>
    public int TotalRelevantDocuments { get; set; }
    
    /// <summary>
    /// Общая точность (релевантные / все оцененные)
    /// </summary>
    public double OverallPrecision { get; set; }
    
    /// <summary>
    /// Дата последнего обновления статистики
    /// </summary>
    public DateTime LastUpdated { get; set; }
}

