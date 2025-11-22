using System.Text.Json;
using System.Linq;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.LightGbm;

namespace Searcher.Services.Reranking;

public sealed class RerankerTrainer
{
    private readonly string _datasetPath;
    private readonly string _modelPath;
    private readonly MLContext _mlContext;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RerankerTrainer(string datasetPath, string? modelPath = null, int? seed = null)
    {
        _datasetPath = Path.GetFullPath(datasetPath);
        _modelPath = ResolveModelPath(modelPath);
        _mlContext = new MLContext(seed ?? 2024);
    }

    public async Task<RerankerTrainingReport> TrainAsync(
        double testFraction = 0.2,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_datasetPath))
            throw new FileNotFoundException("Dataset не найден", _datasetPath);

        var samples = await LoadSamplesAsync(cancellationToken);
        if (samples.Count == 0)
            throw new InvalidOperationException("Dataset пуст. Сначала выполните auto-label.");

        var dataPoints = samples.Select(RerankerDataPoint.FromSample).ToList();
        var data = _mlContext.Data.LoadFromEnumerable(dataPoints);
        var split = _mlContext.Data.TrainTestSplit(data, testFraction: testFraction);

        var pipeline = _mlContext.Transforms.Text.FeaturizeText("QueryFeats", nameof(RerankerDataPoint.QueryText))
            .Append(_mlContext.Transforms.Text.FeaturizeText("TitleFeats", nameof(RerankerDataPoint.Title)))
            .Append(_mlContext.Transforms.Text.FeaturizeText("ContentFeats", nameof(RerankerDataPoint.Content)))
            .Append(_mlContext.Transforms.Conversion.MapValueToKey("GroupIdKey", nameof(RerankerDataPoint.GroupId)))
            .Append(_mlContext.Transforms.Concatenate(
                "Features",
                "QueryFeats",
                "TitleFeats",
                "ContentFeats",
                nameof(RerankerDataPoint.ElasticScore),
                nameof(RerankerDataPoint.Position),
                nameof(RerankerDataPoint.QueryLength),
                nameof(RerankerDataPoint.TitleLength),
                nameof(RerankerDataPoint.ContentLength)))
            .Append(_mlContext.Transforms.NormalizeMeanVariance("Features"))
            .AppendCacheCheckpoint(_mlContext)
            .Append(_mlContext.Ranking.Trainers.LightGbm(new LightGbmRankingTrainer.Options
            {
                LabelColumnName = nameof(RerankerDataPoint.Label),
                FeatureColumnName = "Features",
                RowGroupColumnName = "GroupIdKey",
                NumberOfLeaves = 64,
                MinimumExampleCountPerLeaf = 20,
                NumberOfIterations = 200,
                LearningRate = 0.1
            }));

        Console.WriteLine("[Trainer] Обучение модели...");
        var model = pipeline.Fit(split.TrainSet);

        Console.WriteLine("[Trainer] Расчёт метрик на hold-out выборке...");
        var predictions = model.Transform(split.TestSet);
        var metrics = _mlContext.Ranking.Evaluate(
            predictions,
            labelColumnName: nameof(RerankerDataPoint.Label),
            scoreColumnName: "Score",
            rowGroupColumnName: "GroupIdKey");

        Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
        await using var fileStream = new FileStream(_modelPath, FileMode.Create, FileAccess.Write);
        _mlContext.Model.Save(model, split.TrainSet.Schema, fileStream);

        return new RerankerTrainingReport(
            samples.Count,
            samples.Select(s => s.QueryId).Distinct().Count(),
            testFraction,
            metrics,
            _modelPath);
    }

    private async Task<List<RerankerSample>> LoadSamplesAsync(CancellationToken ct)
    {
        var samples = new List<RerankerSample>();
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
                if (sample != null)
                    samples.Add(sample);
            }
            catch
            {
                // игнорируем битые записи
            }
        }

        return samples;
    }

    private static string ResolveModelPath(string? modelPath)
    {
        if (!string.IsNullOrWhiteSpace(modelPath))
            return Path.GetFullPath(modelPath);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return Path.Combine(projectRoot, "models", "reranker", "reranker.zip");
    }
}

public sealed record RerankerTrainingReport(
    int TotalPairs,
    int TotalQueries,
    double TestFraction,
    RankingMetrics Metrics,
    string ModelPath)
{
    public double MeanNdcgAt3 => GetNdcgAt(3);
    public double MeanNdcgAt10 => GetNdcgAt(10);

    private double GetNdcgAt(int k)
    {
        var gains = Metrics.NormalizedDiscountedCumulativeGains;
        if (gains == null || gains.Count == 0)
            return 0;

        var index = Math.Min(k - 1, gains.Count - 1);
        return gains[index];
    }
}

