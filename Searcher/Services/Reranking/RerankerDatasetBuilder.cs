using System.Text.Json;
using Searcher.Services.Search;

namespace Searcher.Services.Reranking;

public sealed class RerankerDatasetBuilder
{
    private readonly ElasticSearchService _searchService;
    private readonly QueryGenerator _queryGenerator;
    private readonly QwenRelevanceLabeler _labeler;
    private readonly string _datasetPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public RerankerDatasetBuilder(
        ElasticSearchService searchService,
        QueryGenerator queryGenerator,
        QwenRelevanceLabeler labeler,
        string? datasetPath = null)
    {
        _searchService = searchService;
        _queryGenerator = queryGenerator;
        _labeler = labeler;
        _datasetPath = ResolveDatasetPath(datasetPath);
    }

    public async Task<DatasetBuildReport> BuildAsync(
        int targetQueryCount,
        int docsPerQuery,
        CancellationToken cancellationToken = default)
    {
        if (targetQueryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetQueryCount));
        if (docsPerQuery <= 0)
            throw new ArgumentOutOfRangeException(nameof(docsPerQuery));

        Directory.CreateDirectory(Path.GetDirectoryName(_datasetPath)!);
        var summary = await LoadSummaryAsync(cancellationToken);

        var missingQueries = targetQueryCount - summary.QueryCount;
        if (missingQueries <= 0)
        {
            return new DatasetBuildReport(0, 0, summary.QueryCount, summary.PairCount);
        }

        Console.WriteLine($"[Dataset] Собрано {summary.QueryCount} запросов, нужно ещё {missingQueries}.");

        var candidates = await _queryGenerator.GenerateQueriesAsync(targetQueryCount * 2, cancellationToken);
        var newQueries = candidates
            .Where(q => !summary.QueryTexts.Contains(q.QueryText))
            .Take(missingQueries)
            .ToList();

        if (newQueries.Count == 0)
        {
            Console.WriteLine("[Dataset] Не удалось сгенерировать новые уникальные запросы.");
            return new DatasetBuildReport(0, 0, summary.QueryCount, summary.PairCount);
        }

        var appendedPairs = 0;
        await using var stream = new FileStream(_datasetPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);

        foreach (var query in newQueries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var search = await _searchService.SearchAsync(query.QueryText, 0, docsPerQuery, query.Category, query.Author);
            if (search.Documents.Count == 0)
                continue;

            Console.WriteLine($"[Dataset] {query.QueryText} -> {search.Documents.Count} документов");

            foreach (var doc in search.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var judgement = await _labeler.EvaluateAsync(query.QueryText, doc, cancellationToken);
                if (!judgement.IsSuccess)
                {
                    Console.WriteLine($"[Dataset] Ошибка Qwen: {judgement.ErrorMessage}");
                    continue;
                }

                var sample = RerankerSample.FromDocument(query.QueryId, query.QueryText, doc, judgement);
                var line = JsonSerializer.Serialize(sample, _jsonOptions);
                await writer.WriteLineAsync(line);
                appendedPairs++;
            }

            summary.QueryTexts.Add(query.QueryText);
            summary.QueryCount++;
        }

        await writer.FlushAsync();

        return new DatasetBuildReport(
            newQueries.Count,
            appendedPairs,
            summary.QueryCount,
            summary.PairCount + appendedPairs);
    }

    public string DatasetPath => _datasetPath;

    private async Task<DatasetSummary> LoadSummaryAsync(CancellationToken ct)
    {
        if (!File.Exists(_datasetPath))
            return new DatasetSummary();

        var summary = new DatasetSummary();
        using var stream = new FileStream(_datasetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;

        while ((line = await reader.ReadLineAsync()) != null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var sample = JsonSerializer.Deserialize<RerankerSample>(line, _jsonOptions);
                if (sample == null)
                    continue;

                summary.PairCount++;
                summary.QueryTexts.Add(sample.QueryText);
                summary.QueryCount = summary.QueryTexts.Count;
            }
            catch
            {
                // Пропускаем битые строки
            }
        }

        return summary;
    }

    private static string ResolveDatasetPath(string? customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
            return Path.GetFullPath(customPath);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return Path.Combine(projectRoot, "data", "reranker", "dataset.jsonl");
    }
}

public sealed record DatasetBuildReport(
    int GeneratedQueries,
    int GeneratedPairs,
    int TotalQueries,
    int TotalPairs);

internal sealed class DatasetSummary
{
    public int QueryCount { get; set; }
    public int PairCount { get; set; }
    public HashSet<string> QueryTexts { get; } = new(StringComparer.OrdinalIgnoreCase);
}

