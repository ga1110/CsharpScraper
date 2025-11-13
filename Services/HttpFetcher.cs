using System.Net;

namespace Scraper.Services;

public class HttpFetcher
{
    private readonly HttpClient _httpClient;

    public HttpFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

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
                
                // Для 4xx ошибок (особенно 404) не делаем повторные попытки - сразу выбрасываем исключение
                if (statusCode >= 400 && statusCode < 500)
                {
                    // Выбрасываем исключение с информацией о статусе коде
                    // response будет освобожден в finally блоке
                    throw new HttpRequestException(
                        $"Response status code does not indicate success: {statusCode} (Not Found)");
                }
                
                // Для успешных ответов возвращаем содержимое
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    response.Dispose();
                    response = null; // Помечаем как освобожденный, чтобы finally не пытался освободить снова
                    return content;
                }
                
                // Для 5xx ошибок вызываем EnsureSuccessStatusCode для генерации исключения
                // которое будет обработано в catch блоке для повторных попыток
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
                {
                    // Для 4xx ошибок выбрасываем исключение сразу, без повторных попыток
                    // response будет освобожден в finally блоке
                    throw;
                }
                
                // Для других ошибок (5xx, таймауты и т.д.) делаем повторные попытки
                if (i == maxRetries - 1)
                {
                    // response будет освобожден в finally блоке
                    throw;
                }
                // response будет освобожден в finally блоке
                await Task.Delay(1000 * (i + 1)); // Увеличиваем задержку с каждой попыткой
            }
            catch (Exception ex)
            {
                // Проверяем, не является ли это ошибкой 404 или другой 4xx ошибкой
                if (ex.Message.Contains("404") || ex.Message.Contains("Not Found") ||
                    ex.Message.Contains("Response status code does not indicate success: 4"))
                {
                    // Для 4xx ошибок выбрасываем исключение сразу, без повторных попыток
                    // response будет освобожден в finally блоке
                    throw;
                }
                
                if (i == maxRetries - 1)
                {
                    // response будет освобожден в finally блоке
                    throw;
                }
                // response будет освобожден в finally блоке
                await Task.Delay(1000 * (i + 1)); // Увеличиваем задержку с каждой попыткой
            }
            finally
            {
                // Освобождаем response в finally блоке, если он еще не был освобожден
                if (response != null)
                {
                    response.Dispose();
                }
            }
        }
        throw new Exception($"Не удалось загрузить {url} после {maxRetries} попыток");
    }
}

