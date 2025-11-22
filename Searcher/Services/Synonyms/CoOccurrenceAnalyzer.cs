using Scraper.Models;
using Searcher.Services.StopWords;
using Searcher.Services.TextProcessing;

namespace Searcher.Services.Synonyms;

/// <summary>
/// Параметры для майнинга синонимов.
/// </summary>
public class MiningOptions
{
    /// <summary>
    /// Минимальный порог схожести (0.0 - 1.0).
    /// </summary>
    public double MinSimilarityThreshold { get; set; } = 0.25;

    /// <summary>
    /// Минимальное количество совместных вхождений.
    /// </summary>
    public int MinCoOccurrences { get; set; } = 2;

    /// <summary>
    /// Минимальная длина слова.
    /// </summary>
    public int MinWordLength { get; set; } = 3;

    /// <summary>
    /// Максимальная длина слова.
    /// </summary>
    public int MaxWordLength { get; set; } = 30;

    /// <summary>
    /// Использовать заголовки статей для анализа.
    /// </summary>
    public bool UseTitles { get; set; } = true;

    /// <summary>
    /// Использовать контент статей для анализа.
    /// </summary>
    public bool UseContent { get; set; } = true;

    /// <summary>
    /// Вес заголовков относительно контента (заголовки важнее).
    /// </summary>
    public double TitleWeight { get; set; } = 2.0;

    /// <summary>
    /// Максимальное количество синонимов на слово.
    /// </summary>
    public int MaxSynonymsPerWord { get; set; } = 15;

    /// <summary>
    /// Минимальная частота слова в статьях.
    /// </summary>
    public int MinWordFrequency { get; set; } = 2;

    /// <summary>
    /// Максимальная частота слова (чтобы исключить слишком частые).
    /// </summary>
    public int? MaxWordFrequency { get; set; } = null;

    /// <summary>
    /// Исключенные слова (не анализировать).
    /// </summary>
    public HashSet<string> ExcludedWords { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Пользовательский список слов, которые никогда не должны становиться синонимами.
    /// </summary>
    public HashSet<string> ForbiddenWords { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Исключать ли имена собственные.
    /// </summary>
    public bool ExcludeProperNouns { get; set; } = true;

    /// <summary>
    /// Минимальное количество вхождений слова с заглавной буквы, чтобы считать его именем собственным.
    /// </summary>
    public int MinProperNounOccurrences { get; set; } = 2;

    /// <summary>
    /// Минимальная доля заглавных вхождений для признания слова именем собственным.
    /// </summary>
    public double ProperNounCapitalizationThreshold { get; set; } = 0.8;

    /// <summary>
    /// Исключать ли устойчивые словосочетания (составные термины).
    /// </summary>
    public bool ExcludeCompoundTerms { get; set; } = true;

    /// <summary>
    /// Минимальное количество совместных вхождений слов подряд, чтобы считать их устойчивым выражением.
    /// </summary>
    public int MinCompoundOccurrences { get; set; } = 3;

    /// <summary>
    /// Порог схожести для выявления морфологических вариантов.
    /// </summary>
    public double MorphologicalSimilarityThreshold { get; set; } = 0.78;

    /// <summary>
    /// Создает параметры по умолчанию.
    /// </summary>
    public static MiningOptions CreateDefault()
    {
        return new MiningOptions();
    }
}

/// <summary>
/// Результат анализа схожести между двумя словами.
/// </summary>
public class WordSimilarity
{
    public string Word1 { get; set; } = string.Empty;
    public string Word2 { get; set; } = string.Empty;
    public double JaccardSimilarity { get; set; }
    public double CosineSimilarity { get; set; }
    public int CoOccurrenceCount { get; set; }
    public int Word1Frequency { get; set; }
    public int Word2Frequency { get; set; }
}

/// <summary>
/// Анализирует совместное появление слов в статьях для поиска синонимов.
/// </summary>
public class CoOccurrenceAnalyzer
{
    private readonly StopWordsProvider _stopWords;

    public CoOccurrenceAnalyzer(StopWordsProvider? stopWords = null)
    {
        _stopWords = stopWords ?? StopWordsProvider.CreateDefault();
    }

    /// <summary>
    /// Строит инвертированный индекс: для каждого слова список ID статей, где оно встречается.
    /// </summary>
    private Dictionary<string, HashSet<int>> BuildInvertedIndex(
        List<Article> articles,
        MiningOptions options)
    {
        var index = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var wordFrequencies = TextTokenizer.GetWordFrequencies(
            articles,
            includeTitles: options.UseTitles,
            includeContent: options.UseContent,
            stopWords: _stopWords);

        // Фильтруем слова по частоте
        var validWords = TextTokenizer.FilterByFrequency(
            wordFrequencies,
            minFrequency: options.MinWordFrequency,
            maxFrequency: options.MaxWordFrequency);

        // Исключаем слова из списка исключений
        foreach (var excluded in options.ExcludedWords)
        {
            var normalized = TextPreprocessor.Normalize(excluded);
            validWords.Remove(normalized);
        }

        // Строим индекс
        for (int i = 0; i < articles.Count; i++)
        {
            var article = articles[i];
            HashSet<string> articleWords = new(StringComparer.OrdinalIgnoreCase);

            if (options.UseTitles)
            {
                var titleTokens = TextTokenizer.TokenizeTitle(article, _stopWords);
                foreach (var token in titleTokens)
                {
                    if (validWords.Contains(token))
                        articleWords.Add(token);
                }
            }

            if (options.UseContent)
            {
                var contentTokens = TextTokenizer.TokenizeContent(article, _stopWords);
                foreach (var token in contentTokens)
                {
                    if (validWords.Contains(token))
                        articleWords.Add(token);
                }
            }

            // Добавляем слова в индекс
            foreach (var word in articleWords)
            {
                if (!index.ContainsKey(word))
                    index[word] = new HashSet<int>();

                index[word].Add(i);
            }
        }

        return index;
    }

    /// <summary>
    /// Вычисляет коэффициент Жаккара между двумя множествами.
    /// </summary>
    private double CalculateJaccardSimilarity(HashSet<int> set1, HashSet<int> set2)
    {
        if (set1.Count == 0 && set2.Count == 0)
            return 1.0;

        if (set1.Count == 0 || set2.Count == 0)
            return 0.0;

        var intersection = set1.Intersect(set2).Count();
        var union = set1.Union(set2).Count();

        return union > 0 ? (double)intersection / union : 0.0;
    }

    /// <summary>
    /// Вычисляет косинусное сходство между двумя множествами.
    /// </summary>
    private double CalculateCosineSimilarity(HashSet<int> set1, HashSet<int> set2)
    {
        if (set1.Count == 0 || set2.Count == 0)
            return 0.0;

        var intersection = set1.Intersect(set2).Count();
        var magnitude1 = Math.Sqrt(set1.Count);
        var magnitude2 = Math.Sqrt(set2.Count);

        if (magnitude1 == 0 || magnitude2 == 0)
            return 0.0;

        return intersection / (magnitude1 * magnitude2);
    }

    /// <summary>
    /// Находит потенциальные синонимы на основе совместного появления.
    /// </summary>
    public Dictionary<string, List<WordSimilarity>> FindPotentialSynonyms(
        List<Article> articles,
        MiningOptions options)
    {
        if (articles == null || articles.Count == 0)
            return new Dictionary<string, List<WordSimilarity>>();

        Console.WriteLine($"Построение инвертированного индекса из {articles.Count} статей");
        var invertedIndex = BuildInvertedIndex(articles, options);
        Console.WriteLine($"Индекс построен. Уникальных слов: {invertedIndex.Count}");

        var results = new Dictionary<string, List<WordSimilarity>>(StringComparer.OrdinalIgnoreCase);
        var words = invertedIndex.Keys.ToList();
        var totalPairs = words.Count * (words.Count - 1) / 2;
        var processedPairs = 0;
        var lastReportedPercent = -1;

        // Анализируем все пары слов
        for (int i = 0; i < words.Count; i++)
        {
            var word1 = words[i];
            var docSet1 = invertedIndex[word1];

            // Пропускаем слова, которые встречаются слишком редко
            if (docSet1.Count < options.MinCoOccurrences)
                continue;

            var similarities = new List<WordSimilarity>();

            for (int j = i + 1; j < words.Count; j++)
            {
                var word2 = words[j];
                var docSet2 = invertedIndex[word2];

                processedPairs++;

                // Показываем прогресс каждые 5%
                var percent = (int)((double)processedPairs / totalPairs * 100 / 5) * 5;
                if (percent > lastReportedPercent && percent >= 5)
                {
                    lastReportedPercent = percent;
                    Console.WriteLine($"  Прогресс: {percent}% ({processedPairs} / {totalPairs} пар)");
                }

                // Пропускаем слова, которые встречаются слишком редко
                if (docSet2.Count < options.MinCoOccurrences)
                    continue;

                var jaccard = CalculateJaccardSimilarity(docSet1, docSet2);
                var cosine = CalculateCosineSimilarity(docSet1, docSet2);
                var coOccurrence = docSet1.Intersect(docSet2).Count();

                // Используем Jaccard как основную метрику
                var similarity = jaccard;

                // Проверяем пороги
                if (similarity >= options.MinSimilarityThreshold &&
                    coOccurrence >= options.MinCoOccurrences)
                {
                    similarities.Add(new WordSimilarity
                    {
                        Word1 = word1,
                        Word2 = word2,
                        JaccardSimilarity = jaccard,
                        CosineSimilarity = cosine,
                        CoOccurrenceCount = coOccurrence,
                        Word1Frequency = docSet1.Count,
                        Word2Frequency = docSet2.Count
                    });
                }
            }

            // Сортируем по схожести и ограничиваем количество
            if (similarities.Count > 0)
            {
                similarities = similarities
                    .OrderByDescending(s => s.JaccardSimilarity)
                    .Take(options.MaxSynonymsPerWord)
                    .ToList();

                results[word1] = similarities;
            }
        }

        Console.WriteLine($"Анализ завершен. Найдено {results.Count} слов с потенциальными синонимами.");
        return results;
    }

    /// <summary>
    /// Группирует транзитивно связанные слова в группы синонимов.
    /// </summary>
    public Dictionary<string, HashSet<string>> GroupSynonyms(
        Dictionary<string, List<WordSimilarity>> similarities)
    {
        var groups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var wordToGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (sourceWord, simList) in similarities)
        {
            foreach (var sim in simList)
            {
                var word1 = sim.Word1;
                var word2 = sim.Word2;

                // Находим группы для обоих слов
                string? group1 = null, group2 = null;
                wordToGroup.TryGetValue(word1, out group1);
                wordToGroup.TryGetValue(word2, out group2);

                if (group1 == null && group2 == null)
                {
                    // Создаем новую группу
                    var newGroup = word1;
                    groups[newGroup] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { word1, word2 };
                    wordToGroup[word1] = newGroup;
                    wordToGroup[word2] = newGroup;
                }
                else if (group1 != null && group2 == null)
                {
                    // Добавляем word2 в группу word1
                    groups[group1].Add(word2);
                    wordToGroup[word2] = group1;
                }
                else if (group1 == null && group2 != null)
                {
                    // Добавляем word1 в группу word2
                    groups[group2].Add(word1);
                    wordToGroup[word1] = group2;
                }
                else if (group1 != null && group2 != null && group1 != group2)
                {
                    // Объединяем две группы
                    var mergeFrom = group2;
                    var mergeTo = group1;

                    foreach (var wordInMerge in groups[mergeFrom])
                    {
                        groups[mergeTo].Add(wordInMerge);
                        wordToGroup[wordInMerge] = mergeTo;
                    }

                    groups.Remove(mergeFrom);
                }
            }
        }

        // Преобразуем группы в формат словаря синонимов
        var synonymDict = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (groupKey, groupWords) in groups)
        {
            foreach (var wordInGroup in groupWords)
            {
                if (!synonymDict.ContainsKey(wordInGroup))
                    synonymDict[wordInGroup] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var synonym in groupWords)
                {
                    if (synonym != wordInGroup)
                        synonymDict[wordInGroup].Add(synonym);
                }
            }
        }

        return synonymDict;
    }
}

