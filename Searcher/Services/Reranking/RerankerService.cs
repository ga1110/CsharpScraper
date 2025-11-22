using Microsoft.ML;
using Searcher.Models;

namespace Searcher.Services.Reranking;

public sealed class RerankerService : IDisposable
{
    private readonly string _modelPath;
    private readonly MLContext _mlContext = new();
    private PredictionEngine<RerankerDataPoint, RerankerPrediction>? _predictionEngine;
    private ITransformer? _model;

    public RerankerService(string? modelPath = null)
    {
        _modelPath = ResolveModelPath(modelPath);
    }

    public bool IsReady => _predictionEngine != null;
    public string ModelPath => _modelPath;

    public bool TryLoad()
    {
        if (!File.Exists(_modelPath))
            return false;

        try
        {
            using var stream = File.OpenRead(_modelPath);
            _model = _mlContext.Model.Load(stream, out _);
            _predictionEngine = _mlContext.Model.CreatePredictionEngine<RerankerDataPoint, RerankerPrediction>(_model);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Reranker] Ошибка загрузки модели: {ex.Message}");
            return false;
        }
    }

    public List<ArticleDocument> Rerank(string query, List<ArticleDocument> documents)
    {
        if (!IsReady || documents.Count == 0)
            return documents;

        var queryId = query;

        var reranked = documents
            .Select(doc =>
            {
                var dataPoint = RerankerDataPoint.FromDocument(queryId, query, doc);
                var score = _predictionEngine!.Predict(dataPoint).Score;
                doc.RerankerScore = score;
                return doc;
            })
            .OrderByDescending(doc => doc.RerankerScore)
            .ToList();

        return reranked;
    }

    private static string ResolveModelPath(string? modelPath)
    {
        if (!string.IsNullOrWhiteSpace(modelPath))
            return Path.GetFullPath(modelPath);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return Path.Combine(projectRoot, "models", "reranker", "reranker.zip");
    }

    public void Dispose()
    {
        _predictionEngine?.Dispose();
    }
}

