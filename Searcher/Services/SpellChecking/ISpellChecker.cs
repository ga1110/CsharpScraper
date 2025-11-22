using Searcher.Models;

namespace Searcher.Services.SpellChecking;

/// <summary>
/// Интерфейс для различных методов исправления опечаток
/// </summary>
public interface ISpellChecker
{
    /// <summary>
    /// Приоритет проверки (чем меньше, тем раньше выполняется)
    /// </summary>
    int Priority { get; }
    
    /// <summary>
    /// Название метода для логирования
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Пытается исправить опечатки в запросе
    /// </summary>
    Task<SpellCheckResult> TryCorrectAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Результат проверки орфографии с дополнительной информацией
/// </summary>
public class DetailedSpellCheckResult
{
    public string OriginalQuery { get; set; } = string.Empty;
    public string CorrectedQuery { get; set; } = string.Empty;
    public bool HasCorrection => !string.Equals(OriginalQuery, CorrectedQuery, StringComparison.OrdinalIgnoreCase);
    public List<CorrectionStep> Steps { get; set; } = new();
    public double Confidence { get; set; }
    public TimeSpan ProcessingTime { get; set; }
}

/// <summary>
/// Шаг исправления для отладки и аналитики
/// </summary>
public class CorrectionStep
{
    public string Method { get; set; } = string.Empty;
    public string Before { get; set; } = string.Empty;
    public string After { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
}
