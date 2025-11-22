using System.Text.Json;
using Searcher.Models;
using Searcher.Services.TextProcessing;

namespace Searcher.Services.Synonyms;

/// <summary>
/// Управляет словарем синонимов для расширения поисковых запросов.
/// </summary>
public class SynonymProvider
{
    private Dictionary<string, HashSet<string>> _synonyms;
    private readonly Dictionary<string, double> _confidenceScores;
    private SynonymData? _synonymData;
    private double _minConfidence = 0.0;
    private readonly string _defaultFilePath = "synonyms.json";

    /// <summary>
    /// Инициализирует новый экземпляр SynonymProvider.
    /// </summary>
    public SynonymProvider()
    {
        _synonyms = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        _confidenceScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Загружает синонимы из JSON файла.
    /// </summary>
    public void LoadFromFile(string? filePath = null)
    {
        filePath ??= _defaultFilePath;

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"Файл синонимов {filePath} не найден. Будет создан новый словарь.");
            _synonyms = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<SynonymData>(json);

            if (data != null)
            {
                LoadFromData(data);
            }
            else
            {
                InitializeEmpty();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при загрузке синонимов из {filePath}: {ex.Message}");
            InitializeEmpty();
        }
    }

    /// <summary>
    /// Сохраняет синонимы в JSON файл.
    /// </summary>
    public void SaveToFile(SynonymData data, string? filePath = null)
    {
        filePath ??= _defaultFilePath;

        try
        {
            // Убеждаемся, что директория существует
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            var json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(filePath, json);

            var fullPath = Path.GetFullPath(filePath);
            Console.WriteLine($"Синонимы сохранены в: {fullPath}");
            _synonymData = data;
            SyncConfidenceScores(data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при сохранении синонимов в {filePath}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Загружает синонимы из объекта SynonymData.
    /// </summary>
    public void LoadFromData(SynonymData data)
    {
        if (data == null || data.Synonyms == null)
        {
            InitializeEmpty();
            return;
        }

        _synonymData = data;
        _synonyms = CloneDictionary(data.Synonyms);
        SyncConfidenceScores(data);
    }

    /// <summary>
    /// Получает список синонимов для указанного слова.
    /// </summary>
    public IReadOnlySet<string> GetSynonyms(string? word, double? minConfidenceOverride = null)
    {
        if (string.IsNullOrWhiteSpace(word))
            return new HashSet<string>();

        var normalized = TextPreprocessor.Normalize(word);
        if (string.IsNullOrEmpty(normalized))
            return new HashSet<string>();

        var threshold = ResolveThreshold(minConfidenceOverride);
        if (!PassesConfidence(normalized, threshold))
            return new HashSet<string>();

        if (!_synonyms.TryGetValue(normalized, out var synonyms) || synonyms.Count == 0)
            return new HashSet<string>();

        var filtered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var synonym in synonyms)
        {
            if (!string.IsNullOrEmpty(synonym) && PassesConfidence(synonym, threshold))
                filtered.Add(synonym);
        }

        return filtered;
    }

    /// <summary>
    /// Проверяет, есть ли синонимы для указанного слова.
    /// </summary>
    public bool HasSynonyms(string? word, double? minConfidenceOverride = null)
    {
        if (string.IsNullOrWhiteSpace(word))
            return false;

        var normalized = TextPreprocessor.Normalize(word);
        if (string.IsNullOrEmpty(normalized))
            return false;

        var threshold = ResolveThreshold(minConfidenceOverride);
        return _synonyms.ContainsKey(normalized) && PassesConfidence(normalized, threshold);
    }

    /// <summary>
    /// Расширяет поисковый запрос, добавляя синонимы для каждого слова.
    /// </summary>
    /// <param name="query">Исходный поисковый запрос</param>
    /// <returns>Расширенный запрос с синонимами</returns>
    public string ExpandQuery(string? query, double? minConfidenceOverride = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var tokens = query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var expandedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var threshold = ResolveThreshold(minConfidenceOverride);

        foreach (var token in tokens)
        {
            var normalized = TextPreprocessor.Normalize(token);
            if (string.IsNullOrEmpty(normalized))
                continue;

            // Добавляем исходное слово
            expandedTerms.Add(normalized);

            // Добавляем все синонимы
            var synonyms = GetSynonyms(normalized, threshold);
            foreach (var synonym in synonyms)
            {
                if (!string.IsNullOrEmpty(synonym))
                    expandedTerms.Add(synonym);
            }
        }

        return string.Join(" ", expandedTerms);
    }

    /// <summary>
    /// Добавляет группу синонимов. Все слова в группе считаются синонимами друг друга.
    /// </summary>
    public void AddSynonymGroup(params string[] words)
    {
        if (words == null || words.Length < 2)
            return;

        // Нормализуем все слова
        var normalizedWords = words
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => TextPreprocessor.Normalize(w))
            .Where(w => !string.IsNullOrEmpty(w))
            .Distinct()
            .ToList();

        if (normalizedWords.Count < 2)
            return;

        // Для каждого слова добавляем все остальные как синонимы
        foreach (var word in normalizedWords)
        {
            if (!_synonyms.ContainsKey(word))
                _synonyms[word] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var synonym in normalizedWords)
            {
                if (synonym != word)
                    _synonyms[word].Add(synonym);
            }
        }

        _synonymData = null; // пользовательские изменения нарушают исходные оценки
    }

    /// <summary>
    /// Получает текущее количество групп синонимов.
    /// </summary>
    public int GroupCount => _synonyms.Count;

    /// <summary>
    /// Получает все группы синонимов.
    /// </summary>
    public Dictionary<string, HashSet<string>> GetAllSynonyms()
    {
        return new Dictionary<string, HashSet<string>>(_synonyms);
    }

    /// <summary>
    /// Устанавливает минимальный порог уверенности для использования синонимов.
    /// </summary>
    public void SetMinConfidence(double value)
    {
        _minConfidence = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// Возвращает текущий порог уверенности.
    /// </summary>
    public double GetMinConfidence() => _minConfidence;

    /// <summary>
    /// Формирует группы синонимов с учетом порога уверенности.
    /// </summary>
    public IReadOnlyList<HashSet<string>> GetSynonymGroups(double? minConfidenceOverride = null)
    {
        var threshold = ResolveThreshold(minConfidenceOverride);
        var groups = new List<HashSet<string>>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allWords = new HashSet<string>(_synonyms.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _synonyms.Values)
        {
            foreach (var synonym in entry)
                allWords.Add(synonym);
        }

        foreach (var word in allWords)
        {
            if (!visited.Add(word))
                continue;

            var queue = new Queue<string>();
            queue.Enqueue(word);
            var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                if (PassesConfidence(current, threshold))
                    group.Add(current);

                if (!_synonyms.TryGetValue(current, out var neighbors))
                    continue;

                foreach (var neighbor in neighbors)
                {
                    if (visited.Add(neighbor))
                        queue.Enqueue(neighbor);

                    if (PassesConfidence(neighbor, threshold))
                        group.Add(neighbor);
                }
            }

            if (group.Count > 0)
                groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    /// Формирует правила синонимов для elasticsearch.
    /// </summary>
    public List<string> BuildElasticSynonymRules(double? minConfidenceOverride = null)
    {
        var groups = GetSynonymGroups(minConfidenceOverride);
        var rules = new List<string>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in groups)
        {
            if (group.Count < 2)
                continue;

            var ordered = group
                .Where(word => !string.IsNullOrWhiteSpace(word))
                .Select(TextPreprocessor.Normalize)
                .Where(word => !string.IsNullOrEmpty(word))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(word => word, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ordered.Count < 2)
                continue;

            var signature = string.Join("|", ordered);
            if (!seenGroups.Add(signature))
                continue;

            rules.Add(string.Join(", ", ordered));
        }

        return rules;
    }

    /// <summary>
    /// Очищает словарь синонимов.
    /// </summary>
    public void Clear()
    {
        InitializeEmpty();
    }

    private void InitializeEmpty()
    {
        _synonyms = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        _confidenceScores.Clear();
        _synonymData = null;
    }

    private Dictionary<string, HashSet<string>> CloneDictionary(Dictionary<string, HashSet<string>> source)
    {
        var clone = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, values) in source)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var normalizedKey = TextPreprocessor.Normalize(key);
            if (string.IsNullOrEmpty(normalizedKey))
                continue;

            var normalizedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values != null)
            {
                foreach (var value in values)
                {
                    var normalizedValue = TextPreprocessor.Normalize(value);
                    if (!string.IsNullOrEmpty(normalizedValue) && !normalizedValue.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        normalizedValues.Add(normalizedValue);
                    }
                }
            }

            clone[normalizedKey] = normalizedValues;
        }

        return clone;
    }

    private void SyncConfidenceScores(SynonymData data)
    {
        _confidenceScores.Clear();
        if (data.ConfidenceScores == null)
            return;

        foreach (var (word, score) in data.ConfidenceScores)
        {
            var normalized = TextPreprocessor.Normalize(word);
            if (!string.IsNullOrEmpty(normalized))
            {
                _confidenceScores[normalized] = Math.Clamp(score, 0.0, 1.0);
            }
        }
    }

    private bool PassesConfidence(string word, double threshold)
    {
        if (threshold <= 0.0)
            return true;

        if (!_confidenceScores.TryGetValue(word, out var score))
            return true; // Нет оценки — считаем надежным

        return score >= threshold;
    }

    private double ResolveThreshold(double? overrideThreshold)
    {
        var threshold = overrideThreshold ?? _minConfidence;
        return Math.Clamp(threshold, 0.0, 1.0);
    }
}

