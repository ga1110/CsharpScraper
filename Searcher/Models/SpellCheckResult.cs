using System;

namespace Searcher.Models;

/// <summary>
/// Результат проверки запроса на наличие опечаток.
/// </summary>
public sealed record SpellCheckResult(
    string OriginalQuery,
    string CorrectedQuery,
    bool HasCorrection,
    bool Success,
    string Source,
    string? Message = null)
{
    public static SpellCheckResult NoChange(string query, string source = "none") =>
        new(query, query, false, true, source);

    public static SpellCheckResult Correction(string query, string corrected, string source) =>
        new(query, corrected, !query.Equals(corrected, StringComparison.OrdinalIgnoreCase), true, source);

    public static SpellCheckResult Error(string query, string source, string? message = null) =>
        new(query, query, false, false, source, message);
}

