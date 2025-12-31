using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

public class MpPollingService : BackgroundService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<MpPollingService> _log;

    public MpPollingService(IHttpClientFactory http, IConfiguration cfg, ILogger<MpPollingService> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = _cfg["MercadoPago:AccessToken"];
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.LogWarning("MP Polling: MercadoPago:AccessToken vacío. No arranca.");
            return;
        }

        var seconds = int.TryParse(_cfg["Polling:Seconds"], out var s) ? s : 10;
        var minutes = int.TryParse(_cfg["Polling:Minutes"], out var m) ? m : 10; // ventana de búsqueda

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1) Armo rango UTC bien formateado (ISO 8601 con Z)
                var endUtc = DateTimeOffset.UtcNow;
                var beginUtc = endUtc.AddMinutes(-minutes);

                string begin = beginUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                string end = endUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

                // 2) Endpoint (sin filtrar por payment_type_id por ahora)
                var url =
                    $"v1/payments/search?sort=date_created&criteria=desc&limit=50" +
                    $"&range=date_created&begin_date={Uri.EscapeDataString(begin)}&end_date={Uri.EscapeDataString(end)}";

                var client = _http.CreateClient("MP");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var resp = await client.GetAsync(url, stoppingToken);
                var raw = await resp.Content.ReadAsStringAsync(stoppingToken);

                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogError("MP Polling: HTTP {Status}. Body: {Body}", (int)resp.StatusCode, raw);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                    continue;
                }

                using var doc = JsonDocument.Parse(raw);

                if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                {
                    _log.LogWarning("MP Polling: no vino 'results' array. Raw: {Raw}", raw);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                    continue;
                }

                var count = results.GetArrayLength();
                _log.LogInformation("MP Polling: OK. results={Count} (begin={Begin} end={End})", count, begin, end);

                // 3) Guardo en DB (conversión de postgres:// incluida, igual que tu controller)
                await using var conn = new NpgsqlConnection(GetConnString());
                await conn.OpenAsync(stoppingToken);

                foreach (var item in results.EnumerateArray())
                {
                    // id de MP (lo guardás como text en payment_id)
                    var mpPaymentId = item.GetProperty("id").ToString(); // seguro

                    // fecha
                    DateTimeOffset fechaUtc = default;
                    if (item.TryGetProperty("date_created", out var dc) && dc.ValueKind == JsonValueKind.String)
                        fechaUtc = DateTimeOffset.Parse(dc.GetString()!, CultureInfo.InvariantCulture);

                    // monto
                    decimal monto = 0;
                    if (item.TryGetProperty("transaction_amount", out var ta) && ta.ValueKind == JsonValueKind.Number)
                        monto = ta.GetDecimal();

                    // status
                    var status = item.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";

                    // payment_type_id (ojo: puede ser account_money, bank_transfer, etc.)
                    var paymentType = item.TryGetProperty("payment_type_id", out var pt) ? (pt.GetString() ?? "") : "";

                    // json_raw
                    var jsonRaw = item.GetRawText();

                    var sql = """
                    insert into transfers
                    (mp_account_id, payment_id, fecha_utc, monto, status, payment_type, json_raw)
                    values
                    (@mp_account_id, @payment_id, @fecha_utc, @monto, @status, @payment_type, @json_raw::jsonb)
                    on conflict (payment_id) do update set
                        fecha_utc = excluded.fecha_utc,
                        monto = excluded.monto,
                        status = excluded.status,
                        payment_type = excluded.payment_type,
                        json_raw = excluded.json_raw;
                    """;

                    await using var cmd = new NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("mp_account_id", int.TryParse(_cfg["MpAccountId"], out var acc) ? acc : 0);
                    cmd.Parameters.AddWithValue("payment_id", mpPaymentId);
                    cmd.Parameters.AddWithValue("fecha_utc", fechaUtc == default ? (object)DBNull.Value : fechaUtc);
                    cmd.Parameters.AddWithValue("monto", monto);
                    cmd.Parameters.AddWithValue("status", status);
                    cmd.Parameters.AddWithValue("payment_type", paymentType);
                    cmd.Parameters.AddWithValue("json_raw", jsonRaw);

                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "MP Polling: EXCEPTION");
            }

            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private string GetConnString()
    {
        var cs = _cfg.GetConnectionString("Db");
        if (string.IsNullOrWhiteSpace(cs))
            throw new Exception("ConnectionStrings:Db vacío");

        if (cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
            cs = ConvertPostgresUrlToConnectionString(cs);

        return cs;
    }

    private static string ConvertPostgresUrlToConnectionString(string url)
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var db = uri.AbsolutePath.TrimStart('/');

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = user,
            Password = pass,
            Database = db,
            SslMode = SslMode.Require
        };

        return builder.ConnectionString;
    }
}
