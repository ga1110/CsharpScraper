namespace Searcher.Services.TextProcessing;

/// <summary>
/// Предобработка текстов перед индексацией и поиском.
/// </summary>
public static class TextPreprocessor
{
    /// <summary>
    /// Нормализует строку: обрезает пробелы по краям и переводит в нижний регистр.
    /// Возвращает пустую строку, если исходное значение null или состоит из пробелов.
    /// </summary>
    public static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Нормализует строку и возвращает null, если после нормализации она пустая.
    /// </summary>
    public static string? NormalizeOrNull(string? value)
    {
        var normalized = Normalize(value);
        return string.IsNullOrEmpty(normalized)
            ? null
            : normalized;
    }
}

