using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

class MpPollingService : BackgroundService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ConcurrentQueue<object> _queue;

    // ✅ Timezone AR (Render suele estar en UTC)
    private static readonly TimeZoneInfo ArTz = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

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

                // Si MP responde no-200, logueamos el body para ver el motivo
                if (!resp.IsSuccessStatusCode)
                {
                    var bodyErr = await resp.Content.ReadAsStringAsync(stoppingToken);
                    _queue.Enqueue(new { cuenta, error = $"MP {(int)resp.StatusCode}: {bodyErr}" });
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                    continue;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(stoppingToken));
                if (!doc.RootElement.TryGetProperty("results", out var results)) continue;

                foreach (var item in results.EnumerateArray())
                {
                    var created = item.GetProperty("date_created").GetDateTimeOffset();

                    if (lastSeen != null && created <= lastSeen) continue;
                    lastSeen = created;

                    // ✅ Convertimos a hora AR SIEMPRE
                    var createdAr = TimeZoneInfo.ConvertTime(created, ArTz);

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
