using Scraper.Services;
using Scraper.Models;

namespace ScraperApp;

/// <summary>
/// Главный класс приложения для скрапинга статей с сайта panorama.pub
/// </summary>
class Program
{
    /// <summary>
    /// Точка входа в приложение. Запускает скрапинг указанного количества статей и сохраняет результат в JSON файл
    /// </summary>
    /// <param name="args">Аргументы командной строки: [количество_статей] [имя_файла]
    /// Примеры:
    /// - без аргументов: скрапит 100 статей в articles.json
    /// - 50: скрапит 50 статей в articles.json
    /// - 200 articles.json: скрапит 200 статей в articles.json
    /// </param>
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        // Парсим аргументы командной строки
        int maxArticles = 100; // По умолчанию
        string filename = "articles.json";

        if (args.Length > 0)
        {
            // Первый аргумент - количество статей
            if (int.TryParse(args[0], out var parsedCount) && parsedCount > 0)
            {
                maxArticles = parsedCount;
            }
            else
            {
                return;
            }

            // Второй аргумент - имя файла (опционально)
            if (args.Length > 1)
            {
                filename = args[1];
            }
        }

        Console.WriteLine($"Количество статей для сбора: {maxArticles}");
        Console.WriteLine();

        try
        {
            using var scraper = new Scraper.Services.Scraper();
            
            // Запускаем скрапинг
            var articles = await scraper.ScrapeArticlesAsync(maxArticles);

            Console.WriteLine();
            Console.WriteLine($"Скрапинг завершен. Собрано статей: {articles.Count}");

            var scraperUtils = new ScraperUtils();
            await scraperUtils.SaveToJsonAsync(articles, filename);

        }
        catch (Exception)
        {
            Environment.Exit(1);
        }
    }
}

