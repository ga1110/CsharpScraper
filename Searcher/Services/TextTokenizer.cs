using System.Text.RegularExpressions;
using Scraper.Models;

namespace Searcher.Services;

/// <summary>
/// Токенизация и предобработка текста для анализа синонимов.
/// </summary>
public static class TextTokenizer
{
    private static readonly Regex WordRegex = new(@"\b[а-яё]{3,}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CleanRegex = new(@"[^\w\s]", RegexOptions.Compiled);

    /// <summary>
    /// Токенизирует текст, извлекая значимые слова.
    /// </summary>
    /// <param name="text">Текст для токенизации</param>
    /// <param name="stopWords">Провайдер стоп-слов для фильтрации</param>
    /// <param name="minLength">Минимальная длина слова (по умолчанию 3)</param>
    /// <param name="maxLength">Максимальная длина слова (по умолчанию 50)</param>
    /// <returns>Список нормализованных токенов</returns>
    public static List<string> Tokenize(
        string? text, 
        StopWordsProvider? stopWords = null,
        int minLength = 3,
        int maxLength = 50)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        stopWords ??= StopWordsProvider.CreateDefault();

        // Извлекаем только русские слова длиной от minLength символов
        var matches = WordRegex.Matches(text);
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            var word = TextPreprocessor.Normalize(match.Value);
            
            if (string.IsNullOrEmpty(word))
                continue;

            // Фильтруем по длине
            if (word.Length < minLength || word.Length > maxLength)
                continue;

            // Фильтруем стоп-слова
            if (stopWords.IsStopWord(word))
                continue;

            tokens.Add(word);
        }

        return tokens.ToList();
    }

    /// <summary>
    /// Извлекает слова из заголовка статьи.
    /// </summary>
    public static List<string> TokenizeTitle(Article article, StopWordsProvider? stopWords = null)
    {
        return Tokenize(article.Title, stopWords);
    }

    /// <summary>
    /// Извлекает слова из контента статьи.
    /// </summary>
    public static List<string> TokenizeContent(Article article, StopWordsProvider? stopWords = null)
    {
        return Tokenize(article.Content, stopWords);
    }

    /// <summary>
    /// Извлекает слова из заголовка и контента статьи.
    /// </summary>
    public static List<string> TokenizeArticle(Article article, StopWordsProvider? stopWords = null)
    {
        var titleTokens = TokenizeTitle(article, stopWords);
        var contentTokens = TokenizeContent(article, stopWords);
        
        // Объединяем, убирая дубликаты
        var allTokens = new HashSet<string>(titleTokens, StringComparer.OrdinalIgnoreCase);
        foreach (var token in contentTokens)
        {
            allTokens.Add(token);
        }
        
        return allTokens.ToList();
    }

    /// <summary>
    /// Подсчитывает частоту встречаемости слов в списке статей.
    /// </summary>
    public static Dictionary<string, int> GetWordFrequencies(
        List<Article> articles, 
        bool includeTitles = true,
        bool includeContent = true,
        StopWordsProvider? stopWords = null)
    {
        var frequencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        stopWords ??= StopWordsProvider.CreateDefault();

        foreach (var article in articles)
        {
            HashSet<string> articleWords = new(StringComparer.OrdinalIgnoreCase);

            if (includeTitles)
            {
                var titleTokens = TokenizeTitle(article, stopWords);
                foreach (var token in titleTokens)
                {
                    articleWords.Add(token);
                }
            }

            if (includeContent)
            {
                var contentTokens = TokenizeContent(article, stopWords);
                foreach (var token in contentTokens)
                {
                    articleWords.Add(token);
                }
            }

            // Считаем каждое слово только один раз на статью
            foreach (var word in articleWords)
            {
                frequencies.TryGetValue(word, out var count);
                frequencies[word] = count + 1;
            }
        }

        return frequencies;
    }

    /// <summary>
    /// Фильтрует слова по частоте встречаемости.
    /// </summary>
    public static HashSet<string> FilterByFrequency(
        Dictionary<string, int> frequencies,
        int minFrequency = 2,
        int? maxFrequency = null)
    {
        var filtered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (word, frequency) in frequencies)
        {
            if (frequency >= minFrequency && (maxFrequency == null || frequency <= maxFrequency.Value))
            {
                filtered.Add(word);
            }
        }

        return filtered;
    }
}

