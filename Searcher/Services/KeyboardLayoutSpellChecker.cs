using Searcher.Models;

namespace Searcher.Services;

/// <summary>
/// Исправление опечаток, связанных с неправильной раскладкой клавиатуры
/// </summary>
public class KeyboardLayoutSpellChecker : ISpellChecker
{
    public int Priority => 2;
    public string Name => "KeyboardLayout";

    private readonly Dictionary<char, char> _enToRu;
    private readonly Dictionary<char, char> _ruToEn;
    private readonly HashSet<string> _commonRussianWords;

    public KeyboardLayoutSpellChecker()
    {
        _enToRu = BuildEnToRuMapping();
        _ruToEn = BuildRuToEnMapping();
        _commonRussianWords = LoadCommonRussianWords();
    }

    public async Task<SpellCheckResult> TryCorrectAsync(string query, CancellationToken cancellationToken = default)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var correctedWords = new List<string>();
        bool hasCorrections = false;

        foreach (var word in words)
        {
            var corrected = TryCorrectWord(word);
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

    private string? TryCorrectWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return word;

        // Проверяем, не набрано ли русское слово английскими буквами
        if (IsLikelyEnglishLayout(word))
        {
            var russianVariant = ConvertToRussian(word);
            if (IsValidRussianWord(russianVariant))
            {
                return russianVariant;
            }
        }

        // Проверяем, не набрано ли английское слово русскими буквами
        if (IsLikelyRussianLayout(word))
        {
            var englishVariant = ConvertToEnglish(word);
            if (IsValidEnglishWord(englishVariant))
            {
                return englishVariant;
            }
        }

        return null;
    }

    private bool IsLikelyEnglishLayout(string word)
    {
        // Проверяем, содержит ли слово только английские буквы
        return word.All(c => char.IsLetter(c) && c <= 127);
    }

    private bool IsLikelyRussianLayout(string word)
    {
        // Проверяем, содержит ли слово русские буквы
        return word.Any(c => c >= 'а' && c <= 'я' || c >= 'А' && c <= 'Я' || c == 'ё' || c == 'Ё');
    }

    private string ConvertToRussian(string englishWord)
    {
        return new string(englishWord.Select(c => 
            _enToRu.TryGetValue(char.ToLower(c), out var ru) ? 
                (char.IsUpper(c) ? char.ToUpper(ru) : ru) : c
        ).ToArray());
    }

    private string ConvertToEnglish(string russianWord)
    {
        return new string(russianWord.Select(c => 
            _ruToEn.TryGetValue(char.ToLower(c), out var en) ? 
                (char.IsUpper(c) ? char.ToUpper(en) : en) : c
        ).ToArray());
    }

    private bool IsValidRussianWord(string word)
    {
        return _commonRussianWords.Contains(word.ToLowerInvariant());
    }

    private bool IsValidEnglishWord(string word)
    {
        // Простая проверка на английские слова
        var commonEnglishWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "putin", "trump", "biden", "ukraine", "russia", "america", "china",
            "moscow", "kiev", "washington", "london", "paris", "berlin",
            "president", "minister", "government", "parliament", "election",
            "news", "politics", "economy", "society", "sport", "culture"
        };
        
        return commonEnglishWords.Contains(word);
    }

    private Dictionary<char, char> BuildEnToRuMapping()
    {
        return new Dictionary<char, char>
        {
            // Основные соответствия QWERTY -> ЙЦУКЕН
            ['q'] = 'й', ['w'] = 'ц', ['e'] = 'у', ['r'] = 'к', ['t'] = 'е',
            ['y'] = 'н', ['u'] = 'г', ['i'] = 'ш', ['o'] = 'щ', ['p'] = 'з',
            ['['] = 'х', [']'] = 'ъ',
            
            ['a'] = 'ф', ['s'] = 'ы', ['d'] = 'в', ['f'] = 'а', ['g'] = 'п',
            ['h'] = 'р', ['j'] = 'о', ['k'] = 'л', ['l'] = 'д', [';'] = 'ж',
            ['\''] = 'э',
            
            ['z'] = 'я', ['x'] = 'ч', ['c'] = 'с', ['v'] = 'м', ['b'] = 'и',
            ['n'] = 'т', ['m'] = 'ь', [','] = 'б', ['.'] = 'ю', ['/'] = '.',
            
            // Дополнительные символы
            ['`'] = 'ё', ['~'] = 'Ё'
        };
    }

    private Dictionary<char, char> BuildRuToEnMapping()
    {
        var mapping = new Dictionary<char, char>();
        foreach (var (en, ru) in _enToRu)
        {
            mapping[ru] = en;
        }
        return mapping;
    }

    private HashSet<string> LoadCommonRussianWords()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Политические термины
            "путин", "зеленский", "трамп", "байден", "медведев", "лавров",
            "президент", "министр", "правительство", "парламент", "депутат",
            "выборы", "политика", "власть", "государство", "страна",
            
            // Географические названия
            "россия", "украина", "америка", "китай", "европа", "азия",
            "москва", "петербург", "киев", "вашингтон", "лондон", "париж",
            
            // Общие слова
            "новости", "экономика", "общество", "культура", "спорт",
            "международный", "российский", "украинский", "американский",
            "война", "мир", "договор", "соглашение", "санкции",
            
            // Частые слова
            "который", "сказать", "время", "человек", "работа", "жизнь",
            "день", "рука", "делать", "вопрос", "дом", "сторона", "страна",
            "образ", "место", "право", "слово", "дело", "голова", "ребенок",
            "сила", "конец", "вид", "система", "часть", "город", "отношение"
        };
    }
}
