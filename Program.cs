using Scrapper.Services;

namespace Scrapper;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
        
        var scraper = new PanoramaScraper();
        
        try
        {
            // Скрапим до 5000 статей (можно изменить)
            int maxArticles = args.Length > 0 && int.TryParse(args[0], out var count) ? count : 5000;
            
            Console.WriteLine($"Начинаю скрапинг panorama.pub (максимум {maxArticles} статей)...");
            Console.WriteLine();
            
            var articles = await scraper.ScrapeArticlesAsync(maxArticles);
            
            Console.WriteLine();
            Console.WriteLine($"Собрано {articles.Count} статей");
            
            // Сохраняем в JSON
            await scraper.SaveToJsonAsync(articles, "panorama_articles.json");
            
            // Также сохраняем в CSV для удобства
            await SaveToCsvAsync(articles, "panorama_articles.csv");
            
            Console.WriteLine();
            Console.WriteLine("Скрапинг завершен успешно!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Критическая ошибка: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            scraper.Dispose();
        }
    }
    
    static async Task SaveToCsvAsync(List<Models.Article> articles, string filename)
    {
        using var writer = new StreamWriter(filename, false, System.Text.Encoding.UTF8);
        
        // Заголовки
        await writer.WriteLineAsync("Title,Url,Category,Section,Author,PublishDate,Excerpt,ContentLength,TagCount,ViewCount,CommentCount,ImageUrl");
        
        foreach (var article in articles)
        {
            var line = string.Join(",",
                EscapeCsv(article.Title),
                EscapeCsv(article.Url),
                EscapeCsv(article.Category),
                EscapeCsv(article.Section),
                EscapeCsv(article.Author),
                article.PublishDate?.ToString("yyyy-MM-dd") ?? "",
                EscapeCsv(article.Excerpt),
                article.Content?.Length ?? 0,
                article.Tags.Count,
                article.ViewCount?.ToString() ?? "",
                article.CommentCount?.ToString() ?? "",
                EscapeCsv(article.ImageUrl)
            );
            
            await writer.WriteLineAsync(line);
        }
        
        Console.WriteLine($"Сохранено {articles.Count} статей в CSV файл {filename}");
    }
    
    static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        
        // Заменяем кавычки на двойные кавычки и оборачиваем в кавычки если есть запятые или переносы строк
        value = value.Replace("\"", "\"\"");
        if (value.Contains(",") || value.Contains("\n") || value.Contains("\r") || value.Contains("\""))
        {
            return $"\"{value}\"";
        }
        return value;
    }
}
