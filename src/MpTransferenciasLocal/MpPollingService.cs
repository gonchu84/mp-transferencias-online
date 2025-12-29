using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

class MpPollingService : BackgroundService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ConcurrentQueue<object> _queue;

    private static readonly TimeZoneInfo ArTz = GetArgentinaTimeZone();

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

        // OJO: MP devuelve los últimos 20 ordenados desc.
        // lastSeen guarda el más “nuevo” que ya procesamos.
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

                var json = await resp.Content.ReadAsStringAsync(stoppingToken);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("results", out var results))
                    goto Sleep;

                foreach (var item in results.EnumerateArray())
                {
                    // MP suele mandar ISO con offset. Esto queda perfecto en DateTimeOffset.
                    var created = item.GetProperty("date_created").GetDateTimeOffset();

                    // Como viene desc, el primero es el más nuevo.
                    if (lastSeen != null && created <= lastSeen) continue;

                    // Convertimos a hora Argentina SIEMPRE (Render está en UTC)
                    var createdAr = TimeZoneInfo.ConvertTime(created, ArTz);

                    _queue.Enqueue(new
                    {
                        cuenta,
                        id = item.GetProperty("id").ToString(),

                        // te dejo ambas por si después querés agrupar por día AR
                        fecha_utc = created.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        fecha_ar = createdAr.ToString("yyyy-MM-dd HH:mm:ss"),

                        monto = item.GetProperty("transaction_amount").GetDecimal(),
                        status = item.TryGetProperty("status", out var st) ? st.GetString() : null,
                        payment_type_id = item.TryGetProperty("payment_type_id", out var pt) ? pt.GetString() : null,
                        json_raw = json // opcional, si querés debug (si te molesta, lo sacamos)
                    });

                    // actualizamos lastSeen al más nuevo procesado
                    if (lastSeen == null || created > lastSeen) lastSeen = created;
                }
            }
            catch (Exception ex)
            {
                _queue.Enqueue(new { cuenta, error = ex.Message });
            }

        Sleep:
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private static TimeZoneInfo GetArgentinaTimeZone()
    {
        // Linux (Render): "America/Argentina/Buenos_Aires"
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { /* ignore */ }

        // Windows: "Argentina Standard Time"
        try { return TimeZoneInfo.FindSystemTimeZoneById("Argentina Standard Time"); }
        catch { /* ignore */ }

        // Fallback: UTC (no debería pasar)
        return TimeZoneInfo.Utc;
    }
}
