using System.Text.Json;
using Searcher.Models;

namespace Searcher.Services.SpellChecking;

/// <summary>
/// Исправление опечаток на основе расстояния Левенштейна и словаря
/// </summary>
public class LevenshteinSpellChecker : ISpellChecker
{
    public int Priority => 1;
    public string Name => "Levenshtein";
    
    private readonly Dictionary<string, HashSet<string>> _dictionary;
    private readonly HashSet<string> _validWords;
    private readonly int _maxDistance;

    public LevenshteinSpellChecker(int maxDistance = 2)
    {
        _maxDistance = maxDistance;
        _dictionary = LoadDictionary();
        _validWords = BuildValidWordsSet();
    }

    public async Task<SpellCheckResult> TryCorrectAsync(string query, CancellationToken cancellationToken = default)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var correctedWords = new List<string>();
        bool hasCorrections = false;

        foreach (var word in words)
        {
            var corrected = FindBestMatch(word);
            correctedWords.Add(corrected ?? word);
            
            if (corrected != null && !string.Equals(word, corrected, StringComparison.OrdinalIgnoreCase))
            {
                hasCorrections = true;
            }
        }

        var result = string.Join(" ", correctedWords);
        
        return hasCorrections 
            ? SpellCheckResult.Correction(query, result, Name)
            : SpellCheckResult.NoChange(query, Name);
    }

    private string? FindBestMatch(string word)
    {
        if (_validWords.Contains(word))
            return word;

        // Сначала проверяем прямые исправления из словаря
        foreach (var (correct, mistakes) in _dictionary)
        {
            if (mistakes.Contains(word))
                return correct;
        }

        // Затем ищем по расстоянию Левенштейна
        var candidates = new List<(string word, int distance)>();
        
        foreach (var validWord in _validWords)
        {
            var distance = CalculateLevenshteinDistance(word, validWord);
            if (distance <= _maxDistance && distance > 0)
            {
                candidates.Add((validWord, distance));
            }
        }

        // Возвращаем ближайшее совпадение
        return candidates
            .OrderBy(c => c.distance)
            .ThenByDescending(c => c.word.Length) // Предпочитаем более длинные слова
            .FirstOrDefault().word;
    }

    private static int CalculateLevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source))
            return string.IsNullOrEmpty(target) ? 0 : target.Length;
        
        if (string.IsNullOrEmpty(target))
            return source.Length;

        var matrix = new int[source.Length + 1, target.Length + 1];

        // Инициализация первой строки и столбца
        for (int i = 0; i <= source.Length; i++)
            matrix[i, 0] = i;
        
        for (int j = 0; j <= target.Length; j++)
            matrix[0, j] = j;

        // Заполнение матрицы
        for (int i = 1; i <= source.Length; i++)
        {
            for (int j = 1; j <= target.Length; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                
                matrix[i, j] = Math.Min(
                    Math.Min(
                        matrix[i - 1, j] + 1,     // удаление
                        matrix[i, j - 1] + 1),    // вставка
                    matrix[i - 1, j - 1] + cost  // замена
                );
            }
        }

        return matrix[source.Length, target.Length];
    }

    private Dictionary<string, HashSet<string>> LoadDictionary()
    {
        return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // Политические фигуры
            ["путин"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "путен", "пуьин", "путтин", "пытин", "путеин", "пуин" },
            ["зеленский"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "зеленскый", "зеленски", "зиленский", "зеленьский", "зелинский" },
            ["трамп"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "трамб", "трампп", "трам", "трамм", "трумп" },
            ["медведев"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "медвидев", "медведдев", "мидведев", "медвeдев" },
            ["лавров"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "лавроф", "лавроов", "лаврав", "лавроw" },
            ["байден"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "байдeн", "бaйден", "байдин", "бойден" },

            // Страны
            ["россия"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "расия", "росия", "россиа", "рассия", "росcия" },
            ["украина"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "укранна", "украйна", "укрина", "украинна", "укpaина" },
            ["америка"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "амерка", "америкка", "амирика", "aмерика" },
            ["китай"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "кытай", "китaй", "кетай" },

            // Города
            ["москва"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "масква", "моксва", "москав", "мосва", "моcква" },
            ["петербург"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "питербург", "петерьург", "петербурк", "петeрбург" },
            ["киев"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "кыев", "киив", "кеив", "кийев", "киeв" },
            ["вашингтон"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "вашынгтон", "вашингтoн", "вашингтан", "ващингтон" },

            // Общие термины
            ["президент"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "презедент", "президeнт", "призидент", "президнт" },
            ["правительство"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "правитeльство", "правительтво", "правителство" },
            ["парламент"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "парлaмент", "парламeнт", "парламнт" }
        };
    }

    private HashSet<string> BuildValidWordsSet()
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Добавляем все правильные слова из словаря
        foreach (var correct in _dictionary.Keys)
        {
            words.Add(correct);
        }

        // Добавляем дополнительные валидные слова
        var additionalWords = new[]
        {
            "новости", "политика", "экономика", "общество", "спорт", "культура",
            "международный", "российский", "украинский", "американский",
            "выборы", "санкции", "война", "мир", "договор", "соглашение",
            "министр", "депутат", "сенатор", "губернатор", "мэр"
        };

        foreach (var word in additionalWords)
        {
            words.Add(word);
        }

        return words;
    }
}
