using System.Text.Json;
using System.Text.Encodings.Web;
using Searcher.Models;

namespace Searcher.Services;

/// <summary>
/// Сервис для сбора, хранения и управления данными оценки качества поиска
/// </summary>
public class EvaluationService
{
    private readonly string _evaluationFile;
    private readonly JsonSerializerOptions _jsonOptions;
    
    /// <summary>
    /// Инициализирует новый экземпляр EvaluationService
    /// </summary>
    /// <param name="evaluationFile">Путь к файлу для хранения данных оценки</param>
    public EvaluationService(string? evaluationFile = null)
    {
        if (evaluationFile == null)
        {
            // Определяем путь к корню проекта
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
            _evaluationFile = Path.Combine(projectRoot, "evaluation_data.json");
        }
        else
        {
            _evaluationFile = evaluationFile;
        }
        
        _jsonOptions = new JsonSerializerOptions 
        { 
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    /// <summary>
    /// Сохраняет оценку запроса в файл
    /// </summary>
    /// <param name="evaluation">Оценка запроса для сохранения</param>
    public async Task SaveQueryEvaluationAsync(QueryEvaluation evaluation)
    {
        var evaluations = await LoadAllEvaluationsAsync();
        
        // Проверяем, есть ли уже оценка с таким ID
        var existingIndex = evaluations.FindIndex(e => e.QueryId == evaluation.QueryId);
        if (existingIndex >= 0)
        {
            evaluations[existingIndex] = evaluation;
        }
        else
        {
            evaluations.Add(evaluation);
        }
        
        await SaveAllEvaluationsAsync(evaluations);
    }
    
    /// <summary>
    /// Загружает все сохраненные оценки из файла
    /// </summary>
    /// <returns>Список всех оценок</returns>
    public async Task<List<QueryEvaluation>> LoadAllEvaluationsAsync()
    {
        if (!File.Exists(_evaluationFile))
            return new List<QueryEvaluation>();
            
        try
        {
            var json = await File.ReadAllTextAsync(_evaluationFile);
            if (string.IsNullOrWhiteSpace(json))
                return new List<QueryEvaluation>();
                
            return JsonSerializer.Deserialize<List<QueryEvaluation>>(json, _jsonOptions) 
                   ?? new List<QueryEvaluation>();
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Ошибка при чтении файла оценок: {ex.Message}");
            return new List<QueryEvaluation>();
        }
    }
    
    /// <summary>
    /// Сохраняет все оценки в файл
    /// </summary>
    /// <param name="evaluations">Список оценок для сохранения</param>
    private async Task SaveAllEvaluationsAsync(List<QueryEvaluation> evaluations)
    {
        try
        {
            // Убеждаемся, что директория существует
            var directory = Path.GetDirectoryName(_evaluationFile);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            var json = JsonSerializer.Serialize(evaluations, _jsonOptions);
            await File.WriteAllTextAsync(_evaluationFile, json);
            Console.WriteLine($"Файл сохранен: {Path.GetFullPath(_evaluationFile)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка сохранения в файл {_evaluationFile}: {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    /// Получает оценку по идентификатору запроса
    /// </summary>
    /// <param name="queryId">Идентификатор запроса</param>
    /// <returns>Оценка запроса или null, если не найдена</returns>
    public async Task<QueryEvaluation?> GetEvaluationByIdAsync(string queryId)
    {
        var evaluations = await LoadAllEvaluationsAsync();
        return evaluations.FirstOrDefault(e => e.QueryId == queryId);
    }
    
    /// <summary>
    /// Удаляет оценку по идентификатору запроса
    /// </summary>
    /// <param name="queryId">Идентификатор запроса</param>
    /// <returns>true, если оценка была удалена, иначе false</returns>
    public async Task<bool> DeleteEvaluationAsync(string queryId)
    {
        var evaluations = await LoadAllEvaluationsAsync();
        var initialCount = evaluations.Count;
        
        evaluations.RemoveAll(e => e.QueryId == queryId);
        
        if (evaluations.Count < initialCount)
        {
            await SaveAllEvaluationsAsync(evaluations);
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Получает статистику по всем оценкам
    /// </summary>
    /// <returns>Базовая статистика</returns>
    public async Task<(int TotalQueries, int TotalDocuments, int TotalRelevant)> GetBasicStatsAsync()
    {
        var evaluations = await LoadAllEvaluationsAsync();
        
        var totalQueries = evaluations.Count;
        var totalDocuments = evaluations.Sum(e => e.Results.Count);
        var totalRelevant = evaluations.Sum(e => e.Results.Count(r => r.RelevanceScore > 0));
        
        return (totalQueries, totalDocuments, totalRelevant);
    }
    
    /// <summary>
    /// Экспортирует данные оценки в CSV формат
    /// </summary>
    /// <param name="csvFilePath">Путь к CSV файлу</param>
    public async Task ExportToCsvAsync(string csvFilePath)
    {
        var evaluations = await LoadAllEvaluationsAsync();
        
        using var writer = new StreamWriter(csvFilePath);
        
        // Заголовок CSV
        await writer.WriteLineAsync("QueryId,QueryText,Position,DocumentId,Title,Url,Category,Author,RelevanceScore,Comment,Timestamp");
        
        // Данные
        foreach (var evaluation in evaluations)
        {
            foreach (var result in evaluation.Results)
            {
                var line = $"\"{evaluation.QueryId}\"," +
                          $"\"{EscapeCsv(evaluation.QueryText)}\"," +
                          $"{result.Position}," +
                          $"\"{result.DocumentId}\"," +
                          $"\"{EscapeCsv(result.Title)}\"," +
                          $"\"{result.Url}\"," +
                          $"\"{EscapeCsv(result.Category ?? "")}\"," +
                          $"\"{EscapeCsv(result.Author ?? "")}\"," +
                          $"{result.RelevanceScore}," +
                          $"\"{EscapeCsv(result.Comment ?? "")}\"," +
                          $"\"{evaluation.Timestamp:yyyy-MM-dd HH:mm:ss}\"";
                
                await writer.WriteLineAsync(line);
            }
        }
    }
    
    /// <summary>
    /// Экранирует специальные символы для CSV
    /// </summary>
    /// <param name="value">Значение для экранирования</param>
    /// <returns>Экранированное значение</returns>
    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
            
        return value.Replace("\"", "\"\"");
    }
    
    /// <summary>
    /// Очищает все сохраненные оценки
    /// </summary>
    public async Task ClearAllEvaluationsAsync()
    {
        if (File.Exists(_evaluationFile))
        {
            File.Delete(_evaluationFile);
        }
    }
    
    /// <summary>
    /// Создает резервную копию файла оценок
    /// </summary>
    /// <returns>Путь к созданной резервной копии</returns>
    public Task<string> CreateBackupAsync()
    {
        if (!File.Exists(_evaluationFile))
            throw new FileNotFoundException("Файл оценок не найден");
            
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupPath = $"{Path.GetFileNameWithoutExtension(_evaluationFile)}_backup_{timestamp}.json";
        
        File.Copy(_evaluationFile, backupPath);
        return Task.FromResult(backupPath);
    }
}
