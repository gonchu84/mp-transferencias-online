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

    // =========================
    // TIMEZONE ARGENTINA
    // =========================
    private static TimeZoneInfo GetBuenosAiresTz()
    {
        // Linux (Render)
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { }

        // Windows
        try { return TimeZoneInfo.FindSystemTimeZoneById("Argentina Standard Time"); }
        catch { }

        return TimeZoneInfo.Utc;
    }

    private static string FormatBuenosAires(DateTimeOffset dto)
    {
        var tz = GetBuenosAiresTz();
        var dt = TimeZoneInfo.ConvertTime(dto.UtcDateTime, tz);
        return dt.ToString("yyyy-MM-dd HH:mm:ss");
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
        DateTimeOffset? lastSeenUtc = null;

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

                using var doc = JsonDocument.Parse(
                    await resp.Content.ReadAsStringAsync(stoppingToken));

                if (!doc.RootElement.TryGetProperty("results", out var results))
                    goto WAIT;

                foreach (var item in results.EnumerateArray())
                {
                    var created = item.GetProperty("date_created").GetDateTimeOffset();

                    if (lastSeenUtc != null && created <= lastSeenUtc)
                        continue;

                    _queue.Enqueue(new
                    {
                        cuenta,
                        id = item.GetProperty("id").ToString(),
                        fecha = FormatBuenosAires(created), // ✅ Argentina
                        monto = item.GetProperty("transaction_amount").GetDecimal(),
                        status = item.GetProperty("status").GetString(),
                        payment_type_id = item.GetProperty("payment_type_id").GetString()
                    });

                    // El listado viene ordenado DESC
                    if (lastSeenUtc == null || created > lastSeenUtc)
                        lastSeenUtc = created;
                }
            }
            catch (Exception ex)
            {
                _queue.Enqueue(new { cuenta, error = ex.Message });
            }

        WAIT:
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }
}
