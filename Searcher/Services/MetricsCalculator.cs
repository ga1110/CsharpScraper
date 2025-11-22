using Searcher.Models;

namespace Searcher.Services;

/// <summary>
/// Калькулятор метрик качества информационного поиска
/// </summary>
public class MetricsCalculator
{
    /// <summary>
    /// Рассчитывает все метрики для одного запроса
    /// </summary>
    /// <param name="evaluation">Оценка запроса</param>
    /// <returns>Рассчитанные метрики</returns>
    public EvaluationMetrics CalculateMetrics(QueryEvaluation evaluation)
    {
        var metrics = new EvaluationMetrics
        {
            QueryId = evaluation.QueryId,
            QueryText = evaluation.QueryText,
            TotalCount = evaluation.Results.Count,
            RelevantCount = evaluation.Results.Count(r => r.RelevanceScore > 0)
        };
        
        // Precision@K
        metrics.PrecisionAt1 = CalculatePrecisionAtK(evaluation.Results, 1);
        metrics.PrecisionAt5 = CalculatePrecisionAtK(evaluation.Results, 5);
        metrics.PrecisionAt10 = CalculatePrecisionAtK(evaluation.Results, 10);
        
        // Average Precision
        metrics.AveragePrecision = CalculateAveragePrecision(evaluation.Results);
        
        // NDCG
        metrics.NDCG = CalculateNDCG(evaluation.Results);
        
        // Reciprocal Rank
        metrics.ReciprocalRank = CalculateReciprocalRank(evaluation.Results);
        
        return metrics;
    }
    
    /// <summary>
    /// Рассчитывает сводную статистику по всем оценкам
    /// </summary>
    /// <param name="evaluations">Список всех оценок</param>
    /// <returns>Сводная статистика</returns>
    public OverallEvaluationStats CalculateOverallStats(List<QueryEvaluation> evaluations)
    {
        if (evaluations.Count == 0)
        {
            return new OverallEvaluationStats
            {
                LastUpdated = DateTime.UtcNow
            };
        }
        
        var allMetrics = evaluations.Select(CalculateMetrics).ToList();
        
        var stats = new OverallEvaluationStats
        {
            TotalQueries = evaluations.Count,
            MAP = allMetrics.Average(m => m.AveragePrecision),
            MeanNDCG = allMetrics.Average(m => m.NDCG),
            MRR = allMetrics.Average(m => m.ReciprocalRank),
            MeanPrecisionAt1 = allMetrics.Average(m => m.PrecisionAt1),
            MeanPrecisionAt5 = allMetrics.Average(m => m.PrecisionAt5),
            MeanPrecisionAt10 = allMetrics.Average(m => m.PrecisionAt10),
            TotalDocumentsEvaluated = evaluations.Sum(e => e.Results.Count),
            TotalRelevantDocuments = evaluations.Sum(e => e.Results.Count(r => r.RelevanceScore > 0)),
            LastUpdated = DateTime.UtcNow
        };
        
        stats.OverallPrecision = stats.TotalDocumentsEvaluated > 0 
            ? (double)stats.TotalRelevantDocuments / stats.TotalDocumentsEvaluated 
            : 0.0;
        
        return stats;
    }
    
    /// <summary>
    /// Рассчитывает Precision@K - точность в топ-K результатах
    /// </summary>
    /// <param name="results">Результаты поиска с оценками</param>
    /// <param name="k">Количество топ результатов для анализа</param>
    /// <returns>Precision@K значение от 0.0 до 1.0</returns>
    private double CalculatePrecisionAtK(List<ResultRelevance> results, int k)
    {
        var topK = results.OrderBy(r => r.Position).Take(k).ToList();
        if (topK.Count == 0) return 0.0;
        
        var relevant = topK.Count(r => r.RelevanceScore > 0);
        return (double)relevant / topK.Count;
    }
    
    /// <summary>
    /// Рассчитывает Average Precision - среднюю точность для запроса
    /// </summary>
    /// <param name="results">Результаты поиска с оценками</param>
    /// <returns>Average Precision значение от 0.0 до 1.0</returns>
    private double CalculateAveragePrecision(List<ResultRelevance> results)
    {
        var sortedResults = results.OrderBy(r => r.Position).ToList();
        var totalRelevant = sortedResults.Count(r => r.RelevanceScore > 0);
        
        if (totalRelevant == 0) return 0.0;
        
        var relevantCount = 0;
        var sumPrecision = 0.0;
        
        for (int i = 0; i < sortedResults.Count; i++)
        {
            if (sortedResults[i].RelevanceScore > 0)
            {
                relevantCount++;
                var precision = (double)relevantCount / (i + 1);
                sumPrecision += precision;
            }
        }
        
        return sumPrecision / totalRelevant;
    }
    
    /// <summary>
    /// Рассчитывает NDCG (Normalized Discounted Cumulative Gain)
    /// </summary>
    /// <param name="results">Результаты поиска с оценками</param>
    /// <returns>NDCG значение от 0.0 до 1.0</returns>
    private double CalculateNDCG(List<ResultRelevance> results)
    {
        if (results.Count == 0) return 0.0;
        
        var sortedResults = results.OrderBy(r => r.Position).ToList();
        
        // DCG (Discounted Cumulative Gain)
        var dcg = 0.0;
        for (int i = 0; i < sortedResults.Count; i++)
        {
            var gain = Math.Pow(2, sortedResults[i].RelevanceScore) - 1; // 2^rel - 1
            var discount = Math.Log2(i + 2); // log2(i + 2) потому что позиции начинаются с 1
            dcg += gain / discount;
        }
        
        // IDCG (Ideal DCG) - DCG для идеального порядка
        var idealOrder = sortedResults.OrderByDescending(r => r.RelevanceScore).ToList();
        var idcg = 0.0;
        for (int i = 0; i < idealOrder.Count; i++)
        {
            var gain = Math.Pow(2, idealOrder[i].RelevanceScore) - 1;
            var discount = Math.Log2(i + 2);
            idcg += gain / discount;
        }
        
        return idcg > 0 ? dcg / idcg : 0.0;
    }
    
    /// <summary>
    /// Рассчитывает Reciprocal Rank - обратный ранг первого релевантного документа
    /// </summary>
    /// <param name="results">Результаты поиска с оценками</param>
    /// <returns>Reciprocal Rank значение от 0.0 до 1.0</returns>
    private double CalculateReciprocalRank(List<ResultRelevance> results)
    {
        var sortedResults = results.OrderBy(r => r.Position).ToList();
        
        for (int i = 0; i < sortedResults.Count; i++)
        {
            if (sortedResults[i].RelevanceScore > 0)
            {
                return 1.0 / (i + 1);
            }
        }
        
        return 0.0; // Нет релевантных документов
    }
    
    /// <summary>
    /// Рассчитывает детальную статистику по релевантности
    /// </summary>
    /// <param name="evaluations">Список оценок</param>
    /// <returns>Статистика по уровням релевантности</returns>
    public Dictionary<int, int> CalculateRelevanceDistribution(List<QueryEvaluation> evaluations)
    {
        var distribution = new Dictionary<int, int>
        {
            [0] = 0, // нерелевантно
            [1] = 0, // частично релевантно
            [2] = 0  // очень релевантно
        };
        
        foreach (var evaluation in evaluations)
        {
            foreach (var result in evaluation.Results)
            {
                if (distribution.ContainsKey(result.RelevanceScore))
                {
                    distribution[result.RelevanceScore]++;
                }
            }
        }
        
        return distribution;
    }
    
    /// <summary>
    /// Рассчитывает статистику по позициям релевантных документов
    /// </summary>
    /// <param name="evaluations">Список оценок</param>
    /// <returns>Словарь: позиция -> количество релевантных документов на этой позиции</returns>
    public Dictionary<int, int> CalculatePositionStats(List<QueryEvaluation> evaluations)
    {
        var positionStats = new Dictionary<int, int>();
        
        foreach (var evaluation in evaluations)
        {
            foreach (var result in evaluation.Results.Where(r => r.RelevanceScore > 0))
            {
                if (positionStats.ContainsKey(result.Position))
                {
                    positionStats[result.Position]++;
                }
                else
                {
                    positionStats[result.Position] = 1;
                }
            }
        }
        
        return positionStats.OrderBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
}
