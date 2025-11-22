using System.Text.RegularExpressions;
using Scraper.Models;
using Searcher.Services.StopWords;
using Searcher.Services.TextProcessing;

namespace Searcher.Services.Synonyms;

/// <summary>
/// Валидация и фильтрация найденных синонимов.
/// </summary>
public class SynonymValidator
{
    private static readonly Regex WordRegex = new(@"\b[А-ЯЁа-яё]{3,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly StopWordsProvider _stopWords;
    private readonly Dictionary<string, bool> _compoundCache;

    public SynonymValidator(StopWordsProvider? stopWords = null)
    {
        _stopWords = stopWords ?? StopWordsProvider.CreateDefault();
        _compoundCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Валидирует найденные синонимы, применяя различные фильтры.
    /// </summary>
    public Dictionary<string, HashSet<string>> Validate(
        Dictionary<string, HashSet<string>> candidates,
        List<Article> articles,
        MiningOptions options)
    {
        var validated = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var wordFrequencies = TextTokenizer.GetWordFrequencies(
            articles,
            includeTitles: true,
            includeContent: true,
            stopWords: _stopWords);
        var capitalizationStats = BuildWordCaseStats(articles);

        int totalPairs = 0;
        int validPairs = 0;

        foreach (var (word, synonyms) in candidates)
        {
            if (ShouldSkipWord(word, wordFrequencies, options, capitalizationStats))
                continue;

            var validSynonyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var synonym in synonyms)
            {
                totalPairs++;

                if (ShouldSkipWord(synonym, wordFrequencies, options, capitalizationStats))
                    continue;

                if (word.Equals(synonym, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (options.ExcludeCompoundTerms && IsStrongCollocation(word, synonym, articles, options))
                    continue;

                if (IsValidSynonymPair(word, synonym, wordFrequencies, options))
                {
                    validSynonyms.Add(synonym);
                    validPairs++;
                }
            }

            if (validSynonyms.Count > 0)
            {
                validated[word] = validSynonyms;
            }
        }

        Console.WriteLine($"Валидация завершена: {validPairs} / {totalPairs} пар прошли проверку");
        return validated;
    }

    /// <summary>
    /// Проверяет, является ли пара слов валидными синонимами.
    /// </summary>
    private bool IsValidSynonymPair(
        string word1,
        string word2,
        Dictionary<string, int> wordFrequencies,
        MiningOptions options)
    {
        if (!wordFrequencies.TryGetValue(word1, out var freq1) ||
            !wordFrequencies.TryGetValue(word2, out var freq2))
            return false;

        if (freq1 < options.MinWordFrequency || freq2 < options.MinWordFrequency)
            return false;

        if (options.ExcludedWords.Contains(word1) || options.ExcludedWords.Contains(word2))
            return false;

        if (options.ForbiddenWords.Contains(word1) || options.ForbiddenWords.Contains(word2))
            return false;

        if (word1.Length < options.MinWordLength || word1.Length > options.MaxWordLength ||
            word2.Length < options.MinWordLength || word2.Length > options.MaxWordLength)
            return false;

        if (AreMorphologicalVariants(word1, word2, options))
            return false;

        return true;
    }

    private bool ShouldSkipWord(
        string word,
        Dictionary<string, int> wordFrequencies,
        MiningOptions options,
        Dictionary<string, WordCaseStats> capitalizationStats)
    {
        if (string.IsNullOrWhiteSpace(word))
            return true;

        if (options.ExcludedWords.Contains(word) || options.ForbiddenWords.Contains(word))
            return true;

        if (!wordFrequencies.TryGetValue(word, out var frequency) || frequency < options.MinWordFrequency)
            return true;

        if (options.ExcludeProperNouns && IsLikelyProperNoun(word, capitalizationStats, options))
            return true;

        return false;
    }

    private Dictionary<string, WordCaseStats> BuildWordCaseStats(List<Article> articles)
    {
        var stats = new Dictionary<string, WordCaseStats>(StringComparer.OrdinalIgnoreCase);

        foreach (var article in articles)
        {
            ProcessTextForCaseStats(article.Title, stats);
            ProcessTextForCaseStats(article.Content, stats);
        }

        return stats;
    }

    private static void ProcessTextForCaseStats(string? text, Dictionary<string, WordCaseStats> stats)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (Match match in WordRegex.Matches(text))
        {
            var original = match.Value;
            var normalized = TextPreprocessor.Normalize(original);
            if (string.IsNullOrEmpty(normalized))
                continue;

            if (!stats.TryGetValue(normalized, out var wordStats))
            {
                wordStats = new WordCaseStats();
                stats[normalized] = wordStats;
            }

            if (char.IsUpper(original[0]))
                wordStats.CapitalizedCount++;
            else
                wordStats.LowercaseCount++;
        }
    }

    private static bool IsLikelyProperNoun(
        string word,
        Dictionary<string, WordCaseStats> capitalizationStats,
        MiningOptions options)
    {
        if (!capitalizationStats.TryGetValue(word, out var stats))
            return false;

        if (stats.CapitalizedCount < options.MinProperNounOccurrences)
            return false;

        return stats.CapitalizedRatio >= options.ProperNounCapitalizationThreshold;
    }

    private bool IsStrongCollocation(
        string word1,
        string word2,
        List<Article> articles,
        MiningOptions options)
    {
        if (!options.ExcludeCompoundTerms || options.MinCompoundOccurrences <= 0)
            return false;

        var key = CreatePairKey(word1, word2);
        if (_compoundCache.TryGetValue(key, out var cached))
            return cached;

        var phrase1 = $"{word1} {word2}";
        var phrase2 = $"{word2} {word1}";
        var occurrences = 0;

        foreach (var article in articles)
        {
            occurrences += CountOccurrences(article.Title, phrase1);
            if (occurrences >= options.MinCompoundOccurrences)
                break;

            occurrences += CountOccurrences(article.Title, phrase2);
            if (occurrences >= options.MinCompoundOccurrences)
                break;

            occurrences += CountOccurrences(article.Content, phrase1);
            if (occurrences >= options.MinCompoundOccurrences)
                break;

            occurrences += CountOccurrences(article.Content, phrase2);
            if (occurrences >= options.MinCompoundOccurrences)
                break;
        }

        var isCompound = occurrences >= options.MinCompoundOccurrences;
        _compoundCache[key] = isCompound;
        return isCompound;
    }

    private static int CountOccurrences(string? text, string phrase)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(phrase))
            return 0;

        var haystack = text.ToLowerInvariant();
        var needle = phrase.ToLowerInvariant();
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string CreatePairKey(string word1, string word2)
    {
        return string.Compare(word1, word2, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{word1}|{word2}"
            : $"{word2}|{word1}";
    }

    /// <summary>
    /// Проверяет, являются ли два слова морфологическими вариантами.
    /// </summary>
    private bool AreMorphologicalVariants(string word1, string word2, MiningOptions options)
    {
        if (word1.Equals(word2, StringComparison.OrdinalIgnoreCase))
            return true;

        if (word1.Length < 3 || word2.Length < 3)
            return false;

        var minLength = Math.Min(word1.Length, word2.Length);
        var commonPrefix = GetCommonPrefixLength(word1, word2);
        if (commonPrefix >= Math.Min(4, minLength - 1))
            return true;

        var commonSuffix = GetCommonSuffixLength(word1, word2);
        if (commonSuffix >= 4)
            return true;

        var distance = CalculateLevenshteinDistance(word1, word2);
        var maxLength = Math.Max(word1.Length, word2.Length);
        if (maxLength == 0)
            return false;

        var similarity = 1.0 - (double)distance / maxLength;
        return similarity >= options.MorphologicalSimilarityThreshold;
    }

    private static int GetCommonPrefixLength(string word1, string word2)
    {
        var length = Math.Min(word1.Length, word2.Length);
        var count = 0;

        for (int i = 0; i < length; i++)
        {
            if (char.ToLowerInvariant(word1[i]) == char.ToLowerInvariant(word2[i]))
                count++;
            else
                break;
        }

        return count;
    }

    private static int GetCommonSuffixLength(string word1, string word2)
    {
        var length = Math.Min(word1.Length, word2.Length);
        var count = 0;

        for (int i = 1; i <= length; i++)
        {
            if (char.ToLowerInvariant(word1[^i]) == char.ToLowerInvariant(word2[^i]))
                count++;
            else
                break;
        }

        return count;
    }

    private static int CalculateLevenshteinDistance(string source, string target)
    {
        var n = source.Length;
        var m = target.Length;

        var dp = new int[n + 1, m + 1];

        for (int i = 0; i <= n; i++)
            dp[i, 0] = i;

        for (int j = 0; j <= m; j++)
            dp[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                var cost = char.ToLowerInvariant(source[i - 1]) == char.ToLowerInvariant(target[j - 1]) ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }

        return dp[n, m];
    }

    /// <summary>
    /// Вычисляет схожесть контекстов использования двух слов.
    /// </summary>
    public double CalculateContextSimilarity(
        string word1,
        string word2,
        List<Article> articles)
    {
        var word1Articles = new HashSet<int>();
        var word2Articles = new HashSet<int>();

        for (int i = 0; i < articles.Count; i++)
        {
            var article = articles[i];
            var titleTokens = TextTokenizer.TokenizeTitle(article, _stopWords);
            var contentTokens = TextTokenizer.TokenizeContent(article, _stopWords);
            var allTokens = titleTokens.Concat(contentTokens).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (allTokens.Contains(word1))
                word1Articles.Add(i);

            if (allTokens.Contains(word2))
                word2Articles.Add(i);
        }

        if (word1Articles.Count == 0 || word2Articles.Count == 0)
            return 0.0;

        var intersection = word1Articles.Intersect(word2Articles).Count();
        var union = word1Articles.Union(word2Articles).Count();

        return union > 0 ? (double)intersection / union : 0.0;
    }

    private sealed class WordCaseStats
    {
        public int CapitalizedCount { get; set; }
        public int LowercaseCount { get; set; }
        public int Total => CapitalizedCount + LowercaseCount;
        public double CapitalizedRatio => Total == 0 ? 0 : (double)CapitalizedCount / Total;
    }
}

