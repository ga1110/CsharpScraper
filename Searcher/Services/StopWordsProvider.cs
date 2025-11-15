using System;
using System.Collections.Generic;

namespace Searcher.Services;

/// <summary>
/// Управляет встроенным списком стоп-слов для фильтрации поисковых запросов.
/// </summary>
public class StopWordsProvider
{
    private static readonly string[] DefaultStopWords =
    {
        "и", "в", "во", "на", "к", "ко", "с", "из", "у", "за",
        "не", "что", "как", "но", "а", "же", "ли", "это", "тот", "эта",
        "я", "мы", "вы", "он", "она", "оно", "они", "его", "ее", "их",
        "для", "по", "при", "о", "об", "от", "до", "бы", "же", "или",
        "так", "также", "уже", "еще", "тот", "там", "тут", "зато", "чтобы",
        "если", "куда", "когда", "тогда"
    };

    private readonly HashSet<string> _stopWords;

    private StopWordsProvider()
    {
        _stopWords = new HashSet<string>(DefaultStopWords, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Создает провайдер со встроенным списком стоп-слов.
    /// </summary>
    public static StopWordsProvider CreateDefault()
    {
        return new StopWordsProvider();
    }

    /// <summary>
    /// Текущее количество стоп-слов.
    /// </summary>
    public int Count => _stopWords.Count;

    /// <summary>
    /// Проверяет, является ли токен стоп-словом.
    /// </summary>
    public bool IsStopWord(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return true;

        var normalized = TextPreprocessor.Normalize(token);
        return normalized.Length == 0 || _stopWords.Contains(normalized);
    }
}

