using Searcher.Models;

namespace Searcher.Services.SpellChecking;

/// <summary>
/// Исправление опечаток на основе фонетического сходства (русский Soundex)
/// </summary>
public class PhoneticSpellChecker : ISpellChecker
{
    public int Priority => 3;
    public string Name => "Phonetic";

    private readonly Dictionary<string, List<string>> _phoneticGroups;

    public PhoneticSpellChecker()
    {
        _phoneticGroups = BuildPhoneticGroups();
    }

    public async Task<SpellCheckResult> TryCorrectAsync(string query, CancellationToken cancellationToken = default)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var correctedWords = new List<string>();
        bool hasCorrections = false;

        foreach (var word in words)
        {
            var corrected = FindPhoneticMatch(word);
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

    private string? FindPhoneticMatch(string word)
    {
        var phoneticCode = CalculateRussianSoundex(word);
        
        foreach (var (groupCode, words) in _phoneticGroups)
        {
            if (groupCode == phoneticCode)
            {
                // Возвращаем наиболее вероятное слово из группы
                var bestMatch = words.FirstOrDefault(w => !string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
                if (bestMatch != null)
                {
                    return bestMatch;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Упрощенный русский Soundex алгоритм
    /// </summary>
    private string CalculateRussianSoundex(string word)
    {
        if (string.IsNullOrEmpty(word))
            return string.Empty;

        word = word.ToLowerInvariant();
        
        // Замены для фонетического сходства
        var phoneticReplacements = new Dictionary<string, string>
        {
            // Гласные
            {"а", "0"}, {"о", "0"}, {"у", "1"}, {"ы", "1"}, {"и", "2"}, {"е", "2"}, {"ё", "2"}, {"э", "2"}, {"я", "2"}, {"ю", "1"},
            
            // Согласные по звучанию
            {"б", "1"}, {"п", "1"},
            {"в", "2"}, {"ф", "2"},
            {"г", "3"}, {"к", "3"}, {"х", "3"},
            {"д", "4"}, {"т", "4"},
            {"ж", "5"}, {"ш", "5"}, {"щ", "5"}, {"ч", "5"},
            {"з", "6"}, {"с", "6"}, {"ц", "6"},
            {"л", "7"},
            {"м", "8"},
            {"н", "9"},
            {"р", "A"},
            
            // Мягкие и твердые знаки игнорируем
            {"ь", ""}, {"ъ", ""}
        };

        var result = string.Empty;
        var firstChar = word[0].ToString();
        
        foreach (char c in word)
        {
            var charStr = c.ToString();
            if (phoneticReplacements.TryGetValue(charStr, out var replacement))
            {
                result += replacement;
            }
            else
            {
                result += charStr;
            }
        }

        // Убираем повторяющиеся символы
        var compressed = string.Empty;
        char? lastChar = null;
        
        foreach (char c in result)
        {
            if (c != lastChar)
            {
                compressed += c;
                lastChar = c;
            }
        }

        // Ограничиваем длину и добавляем первую букву
        return firstChar + compressed.Substring(Math.Min(1, compressed.Length)).PadRight(4, '0').Substring(0, 4);
    }

    private Dictionary<string, List<string>> BuildPhoneticGroups()
    {
        var groups = new Dictionary<string, List<string>>();
        
        // Группы фонетически похожих слов
        var phoneticWords = new[]
        {
            // Политики с похожим звучанием
            new[] { "путин", "пуьин", "путен" },
            new[] { "трамп", "трамб", "трумп" },
            new[] { "байден", "бойден", "байдин" },
            
            // Города
            new[] { "москва", "масква", "моксва" },
            new[] { "киев", "кыев", "кеив" },
            
            // Страны
            new[] { "россия", "расия", "росия" },
            new[] { "украина", "укрина", "украйна" },
            
            // Общие термины
            new[] { "президент", "презедент", "призидент" },
            new[] { "правительство", "правителство", "правитeльство" }
        };

        foreach (var wordGroup in phoneticWords)
        {
            if (wordGroup.Length > 0)
            {
                var phoneticCode = CalculateRussianSoundex(wordGroup[0]);
                groups[phoneticCode] = wordGroup.ToList();
            }
        }

        return groups;
    }
}
