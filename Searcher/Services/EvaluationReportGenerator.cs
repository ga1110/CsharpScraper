using System.Text;
using Searcher.Models;

namespace Searcher.Services;

/// <summary>
/// Генератор отчетов по оценке качества поиска
/// </summary>
public class EvaluationReportGenerator
{
    private readonly MetricsCalculator _calculator;
    
    public EvaluationReportGenerator()
    {
        _calculator = new MetricsCalculator();
    }
    
    /// <summary>
    /// Генерирует полный отчет по оценке качества поиска
    /// </summary>
    /// <param name="evaluations">Список всех оценок</param>
    /// <returns>Текст отчета</returns>
    public string GenerateFullReport(List<QueryEvaluation> evaluations)
    {
        if (evaluations.Count == 0)
        {
            return "Нет данных для создания отчета. Сначала выполните оценку запросов.";
        }
        
        var report = new StringBuilder();
        var allMetrics = evaluations.Select(e => _calculator.CalculateMetrics(e)).ToList();
        var overallStats = _calculator.CalculateOverallStats(evaluations);
        
        // Заголовок отчета
        report.AppendLine("ОТЧЕТ ПО КАЧЕСТВУ ПОИСКОВОЙ СИСТЕМЫ");
        report.AppendLine(new string('=', 80));
        report.AppendLine($"Дата создания отчета: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Всего оценено запросов: {evaluations.Count}");
        report.AppendLine($"Всего оценено документов: {overallStats.TotalDocumentsEvaluated}");
        report.AppendLine($"Релевантных документов: {overallStats.TotalRelevantDocuments}");
        report.AppendLine();
        
        // Сводные метрики
        report.AppendLine("СВОДНЫЕ МЕТРИКИ:");
        report.AppendLine($"  MAP (Mean Average Precision): {overallStats.MAP:F3}");
        report.AppendLine($"  Mean NDCG:                   {overallStats.MeanNDCG:F3}");
        report.AppendLine($"  MRR (Mean Reciprocal Rank):  {overallStats.MRR:F3}");
        report.AppendLine($"  Mean Precision@1:            {overallStats.MeanPrecisionAt1:F3}");
        report.AppendLine($"  Mean Precision@5:            {overallStats.MeanPrecisionAt5:F3}");
        report.AppendLine($"  Mean Precision@10:           {overallStats.MeanPrecisionAt10:F3}");
        report.AppendLine($"  Общая точность:              {overallStats.OverallPrecision:F3}");
        report.AppendLine();
        
        // Интерпретация результатов
        report.AppendLine("ИНТЕРПРЕТАЦИЯ РЕЗУЛЬТАТОВ:");
        report.AppendLine(GetQualityAssessment(overallStats));
        report.AppendLine();
        
        // Распределение по релевантности
        var relevanceDistribution = _calculator.CalculateRelevanceDistribution(evaluations);
        report.AppendLine("РАСПРЕДЕЛЕНИЕ ПО РЕЛЕВАНТНОСТИ:");
        report.AppendLine($"  Нерелевантно (0):         {relevanceDistribution[0]} документов ({(double)relevanceDistribution[0] / overallStats.TotalDocumentsEvaluated * 100:F1}%)");
        report.AppendLine($"  Частично релевантно (1):  {relevanceDistribution[1]} документов ({(double)relevanceDistribution[1] / overallStats.TotalDocumentsEvaluated * 100:F1}%)");
        report.AppendLine($"  Очень релевантно (2):     {relevanceDistribution[2]} документов ({(double)relevanceDistribution[2] / overallStats.TotalDocumentsEvaluated * 100:F1}%)");
        report.AppendLine();
        
        // Статистика по позициям
        var positionStats = _calculator.CalculatePositionStats(evaluations);
        if (positionStats.Count > 0)
        {
            report.AppendLine("РЕЛЕВАНТНЫЕ ДОКУМЕНТЫ ПО ПОЗИЦИЯМ:");
            foreach (var kvp in positionStats.Take(10))
            {
                var percentage = (double)kvp.Value / overallStats.TotalRelevantDocuments * 100;
                report.AppendLine($"  Позиция {kvp.Key}: {kvp.Value} релевантных документов ({percentage:F1}%)");
            }
            report.AppendLine();
        }
        
        // Детальная таблица по запросам
        report.AppendLine("ДЕТАЛЬНЫЕ РЕЗУЛЬТАТЫ ПО ЗАПРОСАМ:");
        report.AppendLine($"{"Запрос",-25} {"P@1",-6} {"P@5",-6} {"P@10",-6} {"MAP",-6} {"NDCG",-6} {"RR",-6} {"Рел/Всего",-10}");
        report.AppendLine(new string('-', 80));
        
        for (int i = 0; i < evaluations.Count; i++)
        {
            var eval = evaluations[i];
            var metrics = allMetrics[i];
            var queryShort = eval.QueryText.Length > 23 ? eval.QueryText.Substring(0, 20) + " [обрезано]" : eval.QueryText;
            var relevantInfo = $"{metrics.RelevantCount}/{metrics.TotalCount}";
            
            report.AppendLine($"{queryShort,-25} {metrics.PrecisionAt1,-6:F2} {metrics.PrecisionAt5,-6:F2} {metrics.PrecisionAt10,-6:F2} {metrics.AveragePrecision,-6:F2} {metrics.NDCG,-6:F2} {metrics.ReciprocalRank,-6:F2} {relevantInfo,-10}");
        }
        
        report.AppendLine();
        report.AppendLine("ОБЪЯСНЕНИЕ МЕТРИК:");
        report.AppendLine("  P@K  - Precision@K: доля релевантных документов в топ-K");
        report.AppendLine("  MAP  - Mean Average Precision: качество ранжирования");
        report.AppendLine("  NDCG - Normalized Discounted Cumulative Gain: учитывает степень релевантности");
        report.AppendLine("  RR   - Reciprocal Rank: обратный ранг первого релевантного документа");
        report.AppendLine();
        report.AppendLine("Хорошие значения: P@1 > 0.8, MAP > 0.7, NDCG > 0.8, MRR > 0.8");
        
        return report.ToString();
    }
    
    /// <summary>
    /// Генерирует краткий отчет с основными метриками
    /// </summary>
    /// <param name="evaluations">Список всех оценок</param>
    /// <returns>Краткий отчет</returns>
    public string GenerateSummaryReport(List<QueryEvaluation> evaluations)
    {
        if (evaluations.Count == 0)
        {
            return "Нет данных для создания отчета.";
        }
        
        var overallStats = _calculator.CalculateOverallStats(evaluations);
        var report = new StringBuilder();
        
        report.AppendLine("КРАТКИЙ ОТЧЕТ ПО КАЧЕСТВУ ПОИСКА");
        report.AppendLine(new string('-', 40));
        report.AppendLine($"Запросов: {evaluations.Count}");
        report.AppendLine($"Документов: {overallStats.TotalDocumentsEvaluated}");
        report.AppendLine($"Релевантных: {overallStats.TotalRelevantDocuments}");
        report.AppendLine();
        report.AppendLine($"MAP: {overallStats.MAP:F3}");
        report.AppendLine($"NDCG: {overallStats.MeanNDCG:F3}");
        report.AppendLine($"MRR: {overallStats.MRR:F3}");
        report.AppendLine($"P@1: {overallStats.MeanPrecisionAt1:F3}");
        report.AppendLine();
        report.AppendLine($"Качество: {GetQualityLevel(overallStats)}");
        
        return report.ToString();
    }
    
    /// <summary>
    /// Оценивает общее качество поисковой системы
    /// </summary>
    /// <param name="stats">Сводная статистика</param>
    /// <returns>Текстовая оценка качества</returns>
    private string GetQualityAssessment(OverallEvaluationStats stats)
    {
        var assessment = new StringBuilder();
        
        // Оценка MAP
        if (stats.MAP >= 0.8)
            assessment.AppendLine("  ✓ Отличное качество ранжирования (MAP ≥ 0.8)");
        else if (stats.MAP >= 0.6)
            assessment.AppendLine("  ○ Хорошее качество ранжирования (MAP ≥ 0.6)");
        else if (stats.MAP >= 0.4)
            assessment.AppendLine("  △ Удовлетворительное качество ранжирования (MAP ≥ 0.4)");
        else
            assessment.AppendLine("  ✗ Низкое качество ранжирования (MAP < 0.4)");
        
        // Оценка Precision@1
        if (stats.MeanPrecisionAt1 >= 0.8)
            assessment.AppendLine("  ✓ Отличная точность первого результата (P@1 ≥ 0.8)");
        else if (stats.MeanPrecisionAt1 >= 0.6)
            assessment.AppendLine("  ○ Хорошая точность первого результата (P@1 ≥ 0.6)");
        else if (stats.MeanPrecisionAt1 >= 0.4)
            assessment.AppendLine("  △ Удовлетворительная точность первого результата (P@1 ≥ 0.4)");
        else
            assessment.AppendLine("  ✗ Низкая точность первого результата (P@1 < 0.4)");
        
        // Оценка NDCG
        if (stats.MeanNDCG >= 0.8)
            assessment.AppendLine("  ✓ Отличное качество с учетом степени релевантности (NDCG ≥ 0.8)");
        else if (stats.MeanNDCG >= 0.6)
            assessment.AppendLine("  ○ Хорошее качество с учетом степени релевантности (NDCG ≥ 0.6)");
        else if (stats.MeanNDCG >= 0.4)
            assessment.AppendLine("  △ Удовлетворительное качество с учетом степени релевантности (NDCG ≥ 0.4)");
        else
            assessment.AppendLine("  ✗ Низкое качество с учетом степени релевантности (NDCG < 0.4)");
        
        // Общие рекомендации
        assessment.AppendLine();
        if (stats.MAP < 0.6 || stats.MeanPrecisionAt1 < 0.6)
        {
            assessment.AppendLine("РЕКОМЕНДАЦИИ ПО УЛУЧШЕНИЮ:");
            if (stats.MeanPrecisionAt1 < 0.6)
                assessment.AppendLine("  - Улучшить алгоритм ранжирования для повышения точности первого результата");
            if (stats.MAP < 0.6)
                assessment.AppendLine("  - Настроить веса полей поиска (title, content, author, category)");
            if (stats.MeanNDCG < 0.6)
                assessment.AppendLine("  - Улучшить обработку синонимов и расширение запросов");
            assessment.AppendLine("  - Проанализировать запросы с низкими метриками для выявления проблем");
        }
        
        return assessment.ToString().TrimEnd();
    }
    
    /// <summary>
    /// Определяет уровень качества системы
    /// </summary>
    /// <param name="stats">Сводная статистика</param>
    /// <returns>Уровень качества</returns>
    private string GetQualityLevel(OverallEvaluationStats stats)
    {
        var avgScore = (stats.MAP + stats.MeanNDCG + stats.MRR + stats.MeanPrecisionAt1) / 4;
        
        if (avgScore >= 0.8) return "Отличное";
        if (avgScore >= 0.6) return "Хорошее";
        if (avgScore >= 0.4) return "Удовлетворительное";
        return "Требует улучшения";
    }
    
    /// <summary>
    /// Сохраняет отчет в файл
    /// </summary>
    /// <param name="report">Текст отчета</param>
    /// <param name="filePath">Путь к файлу</param>
    public async Task SaveReportToFileAsync(string report, string filePath)
    {
        await File.WriteAllTextAsync(filePath, report, Encoding.UTF8);
    }
}
