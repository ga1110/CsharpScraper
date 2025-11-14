using System.Net;

namespace Scraper.Services;

/// <summary>
/// Класс для выполнения HTTP-запросов с поддержкой повторных попыток при ошибках
/// </summary>
public class HttpFetcher
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Инициализирует новый экземпляр HttpFetcher с указанным HttpClient
    /// </summary>
    /// <param name="httpClient">HTTP клиент для выполнения запросов</param>
    public HttpFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Выполняет HTTP GET запрос с повторными попытками при ошибках (5xx, таймауты), но не повторяет при 4xx ошибках (404 и т.д.)
    /// </summary>
    /// <param name="url">URL для загрузки</param>
    /// <param name="maxRetries">Максимальное количество повторных попыток (по умолчанию 3)</param>
    /// <returns>HTML содержимое страницы</returns>
    /// <exception cref="HttpRequestException">Выбрасывается при 4xx ошибках или если все попытки исчерпаны</exception>
    public async Task<string> FetchWithRetryAsync(string url, int maxRetries = 3)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.GetAsync(url);

                // Проверяем статус код перед обработкой
                var statusCode = (int)response.StatusCode;

                if (statusCode >= 400 && statusCode < 500)
                    throw new HttpRequestException(
                        $"Response status code does not indicate success: {statusCode} (Not Found)");

                // Для успешных ответов возвращаем содержимое
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    response.Dispose();
                    response = null; // Помечаем как освобожденный, чтобы finally не пытался освободить снова
                    return content;
                }

                // Для 5xx ошибок вызываем EnsureSuccessStatusCode
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadAsStringAsync();
                response.Dispose();
                response = null; // Помечаем как освобожденный
                return result;
            }
            catch (HttpRequestException ex)
            {
                // Проверяем сообщение на наличие 404 или других 4xx ошибок
                // Сообщение обычно содержит "Response status code does not indicate success: 404 (Not Found)"
                if (ex.Message.Contains("404") || ex.Message.Contains("Not Found") ||
                    ex.Message.Contains("Response status code does not indicate success: 4"))
                    throw;

                // Для других ошибок (5xx, таймауты и т.д.) делаем повторные попытки
                if (i == maxRetries - 1)
                    throw;

                await Task.Delay(1000 * (i + 1)); // Увеличиваем задержку с каждой попыткой
            }
            catch (Exception ex)
            {
                // Проверяем, не является ли это ошибкой 404 или другой 4xx ошибкой
                if (ex.Message.Contains("404") || ex.Message.Contains("Not Found") ||
                    ex.Message.Contains("Response status code does not indicate success: 4"))
                    throw;

                if (i == maxRetries - 1)
                    throw;

                await Task.Delay(1000 * (i + 1)); // Увеличиваем задержку с каждой попыткой
            }
            finally
            {
                if (response != null)
                    response.Dispose();
            }
        }
        throw new Exception($"Не удалось загрузить {url} после {maxRetries} попыток");
    }
}

