using System.Text.Json;
using Scraper.Models;
using Scraper.Services;
using Searcher.Services.StopWords;
using Searcher.Services.TextProcessing;

namespace Searcher.Services.Reranking;

public record GeneratedQuery(string QueryId, string QueryText, string? Category, string? Author);

/// <summary>
/// Генератор поисковых запросов на основе корпуса статей.
/// </summary>
public sealed class QueryGenerator
{
    private readonly string _articlesFile;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly StopWordsProvider _stopWords;
    private readonly Random _random = new();

    public QueryGenerator(string? articlesFile = null)
    {
        _articlesFile = ResolveArticlesPath(articlesFile);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new CustomDateTimeConverter() }
        };
        _stopWords = StopWordsProvider.CreateDefault();
    }

    public async Task<List<GeneratedQuery>> GenerateQueriesAsync(
        int targetCount,
        CancellationToken cancellationToken = default)
    {
        if (targetCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetCount));

        var articles = await LoadArticlesAsync(cancellationToken);
        if (articles.Count == 0)
            throw new InvalidOperationException($"Файл со статьями '{_articlesFile}' пуст или не найден.");

        var queries = new List<GeneratedQuery>(targetCount);
        var usedTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var article in Shuffle(articles))
        {
            foreach (var candidate in BuildCandidates(article))
            {
                if (usedTexts.Add(candidate))
                {
                    queries.Add(new GeneratedQuery(
                        Guid.NewGuid().ToString(),
                        candidate,
                        TextPreprocessor.NormalizeOrNull(article.Category),
                        TextPreprocessor.NormalizeOrNull(article.Author)));

                    if (queries.Count >= targetCount)
                        return queries;
                }
            }
        }

        if (queries.Count < targetCount)
        {
            foreach (var fallback in BuildFallbackQueries(articles))
            {
                if (usedTexts.Add(fallback))
                {
                    queries.Add(new GeneratedQuery(Guid.NewGuid().ToString(), fallback, null, null));
                    if (queries.Count >= targetCount)
                        break;
                }
            }
        }

        return queries;
    }

    private async Task<List<Article>> LoadArticlesAsync(CancellationToken ct)
    {
        await using var stream = File.OpenRead(_articlesFile);
        var data = await JsonSerializer.DeserializeAsync<List<Article>>(stream, _jsonOptions, ct);
        return data ?? new List<Article>();
    }

    private IEnumerable<Article> Shuffle(IEnumerable<Article> source)
    {
        return source.OrderBy(_ => _random.Next());
    }

    private IEnumerable<string> BuildCandidates(Article article)
    {
        if (!string.IsNullOrWhiteSpace(article.Title))
        {
            var normalizedTitle = TextPreprocessor.Normalize(article.Title);
            if (!string.IsNullOrWhiteSpace(normalizedTitle))
            {
                yield return normalizedTitle;

                var titleTokens = normalizedTitle
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(token => token.Length > 2)
                    .Take(5)
                    .ToList();

                if (titleTokens.Count >= 2)
                    yield return string.Join(' ', titleTokens.Take(2));

                if (titleTokens.Count >= 3)
                    yield return string.Join(' ', titleTokens.Take(3));
            }
        }

        var tokens = TextTokenizer.TokenizeArticle(article, _stopWords);

        foreach (var token in tokens.Take(6))
        {
            yield return token;

            if (!string.IsNullOrWhiteSpace(article.Category))
            {
                var normalizedCategory = TextPreprocessor.Normalize(article.Category);
                if (!string.IsNullOrWhiteSpace(normalizedCategory))
                    yield return $"{normalizedCategory} {token}";
            }
        }

        if (!string.IsNullOrWhiteSpace(article.Author))
        {
            var author = TextPreprocessor.Normalize(article.Author);
            var firstPiece = author.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstPiece))
            {
                yield return firstPiece;
                if (tokens.Count > 0)
                    yield return $"{firstPiece} {tokens[0]}";
            }
        }
    }

    private IEnumerable<string> BuildFallbackQueries(List<Article> articles)
    {
        var frequencies = TextTokenizer.GetWordFrequencies(articles, includeContent: true, stopWords: _stopWords);
        var filtered = TextTokenizer.FilterByFrequency(frequencies, minFrequency: 3);
        var pool = filtered.OrderByDescending(w => frequencies[w]).Take(3000).ToList();

        for (int i = 0; i < pool.Count; i++)
        {
            yield return pool[i];
            if (i + 1 < pool.Count)
            {
                var bigram = $"{pool[i]} {pool[i + 1]}";
                yield return bigram;
            }
        }
    }

    private static string ResolveArticlesPath(string? customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
            return Path.GetFullPath(customPath);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var defaultPath = Path.Combine(projectRoot, "articles.json");

        if (File.Exists(defaultPath))
            return defaultPath;

        var fallback = Path.GetFullPath("articles.json");
        return fallback;
    }
}

