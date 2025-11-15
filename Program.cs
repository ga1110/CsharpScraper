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
        // Настраиваем кодировку консоли, чтобы корректно отображать и вводить UTF-8 символы
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        // Задаём значения по умолчанию для количества статей и имени выходного файла
        int maxArticles = 100;
        string filename = "articles.json";

        if (args.Length > 0)
        {
            // Пробуем интерпретировать первый аргумент как количество статей, которое нужно собрать
            if (int.TryParse(args[0], out var parsedCount) && parsedCount > 0)
            {
                maxArticles = parsedCount;
            }
            else
            {
                // Если аргумент невалидный, завершаем выполнение, чтобы не запускать скрапинг с некорректными параметрами
                return;
            }

            // При наличии второго аргумента используем его как имя файла для сохранения результатов
            if (args.Length > 1)
            {
                filename = args[1];
            }
        }

        Console.WriteLine($"Количество статей для сбора: {maxArticles}");
        Console.WriteLine();

        try
        {
            // Создаём экземпляр скрапера в using, чтобы гарантированно освободить HTTP ресурсы
            using var scraper = new Scraper.Services.Scraper();
            
            // Запускаем сбор статей с сайта и дожидаемся завершения асинхронной операции
            var articles = await scraper.ScrapeArticlesAsync(maxArticles);

            Console.WriteLine();
            Console.WriteLine($"Скрапинг завершен. Собрано статей: {articles.Count}");

            // Сохраняем собранные статьи в JSON файл на диск
            var scraperUtils = new ScraperUtils();
            await scraperUtils.SaveToJsonAsync(articles, filename);

        }
        catch (Exception)
        {
            // Любая ошибка считается критической – завершаем процесс с кодом 1, чтобы сигнализировать об ошибке
            Environment.Exit(1);
        }
    }
}

