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
            _log.LogWarning("MercadoPago:AccessToken vacío. MpPollingService no inicia.");
            return;
        }

        var seconds = int.TryParse(_cfg["Polling:Seconds"], out var s) ? s : 10;
        if (seconds < 5) seconds = 5;

        _log.LogInformation("MpPollingService iniciado. Intervalo: {Seconds}s", seconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Ventana: últimos N minutos (un poco más grande para no perder nada)
                var minutes = 120;

                var endUtc = DateTimeOffset.UtcNow;
                var beginUtc = endUtc.AddMinutes(-minutes);

                // MP es MUY sensible al formato de fechas:
                // usar ISO con Z (UTC) y milisegundos opcionales
                string begin = beginUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
                string end = endUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

                var url =
                    $"v1/payments/search" +
                    $"?sort=date_created&criteria=desc&limit=50" +
                    $"&range=date_created" +
                    $"&begin_date={Uri.EscapeDataString(begin)}" +
                    $"&end_date={Uri.EscapeDataString(end)}";

                var client = _http.CreateClient("MP");
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                var resp = await client.GetAsync(url, stoppingToken);
                var bodyText = await resp.Content.ReadAsStringAsync(stoppingToken);

                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning("MP search error {Status}. Body: {Body}", (int)resp.StatusCode, bodyText);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                    continue;
                }

                using var doc = JsonDocument.Parse(bodyText);

                if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                {
                    _log.LogWarning("MP search: no vino 'results' array.");
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                    continue;
                }

                // Conexión DB (soporta postgres://)
                await using var conn = new NpgsqlConnection(GetConn());
                await conn.OpenAsync(stoppingToken);

                var inserted = 0;
                foreach (var item in results.EnumerateArray())
                {
                    // Solo nos interesan transferencias:
                    // En MP suelen venir como payment_type_id = bank_transfer
                    // PERO para no perder nada, guardamos todo y filtrás en UI si querés.
                    var mpId = item.GetProperty("id").GetInt64();

                    var dateCreated = item.TryGetProperty("date_created", out var dc)
                        ? dc.GetDateTimeOffset().ToUniversalTime()
                        : DateTimeOffset.UtcNow;

                    var amount = item.TryGetProperty("transaction_amount", out var ta)
                        ? ta.GetDecimal()
                        : 0m;

                    var status = item.TryGetProperty("status", out var st)
                        ? (st.GetString() ?? "")
                        : "";

                    var paymentTypeId = item.TryGetProperty("payment_type_id", out var pti)
                        ? (pti.GetString() ?? "")
                        : "";

                    var paymentMethodId = item.TryGetProperty("payment_method_id", out var pmi)
                        ? (pmi.GetString() ?? "")
                        : "";

                    var paymentType = string.IsNullOrWhiteSpace(paymentMethodId)
                        ? paymentTypeId
                        : $"{paymentTypeId}/{paymentMethodId}";

                    var rawJson = item.GetRawText();

                    var sql = """
                        insert into transfers
                        (mp_payment_id, payment_id, fecha_utc, monto, status, payment_type, json_raw)
                        values
                        (@mpId, @paymentId, @fechaUtc, @monto, @status, @paymentType, @json::jsonb)
                        on conflict (mp_payment_id) do nothing;
                    """;

                    await using var cmd = new NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("mpId", mpId);
                    cmd.Parameters.AddWithValue("paymentId", mpId.ToString());  // compat (tu controller busca por payment_id)
                    cmd.Parameters.AddWithValue("fechaUtc", dateCreated);
                    cmd.Parameters.AddWithValue("monto", amount);
                    cmd.Parameters.AddWithValue("status", status);
                    cmd.Parameters.AddWithValue("paymentType", paymentType);
                    cmd.Parameters.AddWithValue("json", rawJson);

                    var rows = await cmd.ExecuteNonQueryAsync(stoppingToken);
                    if (rows > 0) inserted += rows;
                }

                _log.LogInformation("MP polling OK. results={Count}, inserted={Inserted}", results.GetArrayLength(), inserted);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error en MpPollingService");
            }

            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private string GetConn()
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
