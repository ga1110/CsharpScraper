using Scraper.Services;

namespace Scraper;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
        
        var scraper = new Services.Scraper();
        
        try
        {
            if(args.Length == 0 || !int.TryParse(args[0], out var count) || count <= 0)
                return;

            var articles = await scraper.ScrapeArticlesAsync(count);

            // Сохраняем в JSON
            var scraperUtils = new ScraperUtils();
            await scraperUtils.SaveToJsonAsync(articles, "articles.json");
            
            // Также сохраняем в CSV для удобства
            await SaveToCsvAsync(articles, "articles.csv");
            
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
        await writer.WriteLineAsync("Title,Url,Category,Author,PublishDate,ContentLength,CommentCount,ImageUrl");
        
        foreach (var article in articles)
        {
            var line = string.Join(",",
                EscapeCsv(article.Title),
                EscapeCsv(article.Url),
                EscapeCsv(article.Category),
                EscapeCsv(article.Author),
                article.PublishDate?.ToString("yyyy-MM-dd") ?? "",
                article.Content?.Length ?? 0,
                article.CommentCount?.ToString() ?? "",
                EscapeCsv(article.ImageUrl)
            );
            
            await writer.WriteLineAsync(line);
        }
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
