using Searcher.Models;

namespace Searcher.Services.Reranking;

public class RerankerSample
{
    public string QueryId { get; set; } = string.Empty;
    public string QueryText { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ContentSnippet { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Author { get; set; }
    public double ElasticScore { get; set; }
    public int Position { get; set; }
    public int Label { get; set; }
    public float Confidence { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public static RerankerSample FromDocument(
        string queryId,
        string queryText,
        ArticleDocument doc,
        RelevancePrediction prediction)
    {
        return new RerankerSample
        {
            QueryId = queryId,
            QueryText = queryText,
            DocumentId = doc.Id,
            Title = doc.Title ?? string.Empty,
            ContentSnippet = Trim(doc.Content),
            Category = string.IsNullOrWhiteSpace(doc.Category) ? null : doc.Category,
            Author = string.IsNullOrWhiteSpace(doc.Author) ? null : doc.Author,
            ElasticScore = doc.ElasticScore,
            Position = doc.SearchRank,
            Label = prediction.Label,
            Confidence = prediction.Confidence,
            Timestamp = DateTime.UtcNow
        };
    }

    private static string Trim(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        var normalized = content.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length <= 700)
            return normalized;

        return normalized[..700];
    }
}

public class RerankerDataPoint
{
    public float Label { get; set; }
    public string QueryText { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public float ElasticScore { get; set; }
    public float Position { get; set; }
    public float QueryLength { get; set; }
    public float TitleLength { get; set; }
    public float ContentLength { get; set; }
    public uint GroupId { get; set; }

    public static RerankerDataPoint FromSample(RerankerSample sample)
    {
        return new RerankerDataPoint
        {
            Label = sample.Label,
            QueryText = sample.QueryText,
            Title = sample.Title ?? string.Empty,
            Content = sample.ContentSnippet ?? string.Empty,
            ElasticScore = (float)sample.ElasticScore,
            Position = sample.Position,
            QueryLength = sample.QueryText.Length,
            TitleLength = (sample.Title ?? string.Empty).Length,
            ContentLength = (sample.ContentSnippet ?? string.Empty).Length,
            GroupId = ComputeGroupId(sample.QueryId)
        };
    }

    public static RerankerDataPoint FromDocument(string queryId, string queryText, ArticleDocument doc)
    {
        var snippet = string.IsNullOrWhiteSpace(doc.Content)
            ? string.Empty
            : (doc.Content.Length <= 700 ? doc.Content : doc.Content[..700]);

        return new RerankerDataPoint
        {
            Label = 0,
            QueryText = queryText,
            Title = doc.Title ?? string.Empty,
            Content = snippet,
            ElasticScore = (float)doc.ElasticScore,
            Position = doc.SearchRank,
            QueryLength = queryText.Length,
            TitleLength = (doc.Title ?? string.Empty).Length,
            ContentLength = snippet.Length,
            GroupId = ComputeGroupId(queryId)
        };
    }

    private static uint ComputeGroupId(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in value)
            {
                var lower = char.ToLowerInvariant(ch);
                hash ^= lower;
                hash *= 16777619;
            }

            if (hash == 0)
                hash = 1;

            return hash;
        }
    }
}

