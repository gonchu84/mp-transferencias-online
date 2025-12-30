using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

class MpPollingService : BackgroundService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ConcurrentQueue<object> _queue;

    public MpPollingService(
        IHttpClientFactory http,
        IConfiguration cfg,
        ConcurrentQueue<object> queue)
    {
        _http = http;
        _cfg = cfg;
        _queue = queue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = _cfg["MercadoPago:AccessToken"];
        var cuenta = _cfg["Cuenta"] ?? "Cuenta";

        if (string.IsNullOrWhiteSpace(token))
        {
            _queue.Enqueue(new { cuenta, error = "FALTA AccessToken" });
            return;
        }

        var seconds = int.TryParse(_cfg["Polling:Seconds"], out var s) ? s : 10;
        DateTimeOffset? lastSeen = null;

        // ✅ Timezone AR (funciona en Linux/Render)
        var tzAr = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = _http.CreateClient("MP");
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                var resp = await client.GetAsync(
                    "v1/payments/search?sort=date_created&criteria=desc&limit=20",
                    stoppingToken);

                var body = await resp.Content.ReadAsStringAsync(stoppingToken);

                if (!resp.IsSuccessStatusCode)
                {
                    _queue.Enqueue(new { cuenta, error = $"MP {((int)resp.StatusCode)}", detail = body });
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("results", out var results)) continue;

                foreach (var item in results.EnumerateArray())
                {
                    var created = item.GetProperty("date_created").GetDateTimeOffset();

                    if (lastSeen != null && created <= lastSeen) continue;
                    lastSeen = created;

                    // ✅ Convertimos bien a AR (sin depender del timezone del server)
                    var createdAr = TimeZoneInfo.ConvertTime(created, tzAr);

                    _queue.Enqueue(new
                    {
                        cuenta,
                        id = item.GetProperty("id").ToString(),
                        fecha = createdAr.ToString("yyyy-MM-dd HH:mm:ss"),
                        monto = item.GetProperty("transaction_amount").GetDecimal(),
                        status = item.GetProperty("status").GetString(),
                        payment_type_id = item.GetProperty("payment_type_id").GetString()
                    });
                }
            }
            catch (Exception ex)
            {
                _queue.Enqueue(new { cuenta, error = ex.Message });
            }

            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }
}
