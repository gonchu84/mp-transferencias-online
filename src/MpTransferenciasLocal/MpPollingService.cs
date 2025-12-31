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
            _log.LogWarning("MercadoPago:AccessToken vacío. MpPollingService no arranca.");
            return;
        }

        var seconds = int.TryParse(_cfg["Polling:Seconds"], out var s) ? s : 10;
        var minutes = int.TryParse(_cfg["Polling:Minutes"], out var m) ? m : 60;

        _log.LogInformation("MpPollingService OK. Polling cada {Seconds}s - ventana {Minutes} min.", seconds, minutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnce(token, minutes, stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "MpPollingService error general en PollOnce()");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            }
            catch { /* cancel */ }
        }
    }

    private async Task PollOnce(string token, int minutes, CancellationToken ct)
    {
        // Ventana en UTC (formato simple sin milisegundos para evitar “Invalid date parameter”)
        var endUtc = DateTimeOffset.UtcNow;
        var beginUtc = endUtc.AddMinutes(-minutes);

        string beginStr = beginUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        string endStr = endUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        // OJO: si filtrás por bank_transfer y te devuelve 0, probá sin ese filtro.
        // Acá lo dejo con filtro SOLO si está configurado.
        var paymentType = _cfg["Polling:PaymentTypeId"]; // ej: "bank_transfer" o vacío

        var url =
            $"v1/payments/search?sort=date_created&criteria=desc&limit=50&range=date_created" +
            $"&begin_date={Uri.EscapeDataString(beginStr)}&end_date={Uri.EscapeDataString(endStr)}";

        if (!string.IsNullOrWhiteSpace(paymentType))
            url += $"&payment_type_id={Uri.EscapeDataString(paymentType)}";

        var client = _http.CreateClient("MP");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        _log.LogInformation("MP search: {Url}", url);

        var resp = await client.GetAsync(url, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("MP search HTTP {Status}. Body: {Body}", (int)resp.StatusCode, raw);
            return;
        }

        using var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            _log.LogWarning("MP search sin 'results' array.");
            return;
        }

        var connStr = GetConn();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);

        int inserted = 0;

        foreach (var item in results.EnumerateArray())
        {
            // id (MP) viene como número (long) normalmente
            if (!item.TryGetProperty("id", out var idProp)) continue;

            var paymentId = idProp.ValueKind switch
            {
                JsonValueKind.Number => idProp.GetInt64().ToString(),
                JsonValueKind.String => idProp.GetString() ?? "",
                _ => ""
            };

            if (string.IsNullOrWhiteSpace(paymentId))
                continue;

            // Datos
            var status = item.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";
            var paymentTypeId = item.TryGetProperty("payment_type_id", out var pt) ? (pt.GetString() ?? "") : "";

            // fecha
            DateTimeOffset fechaUtc = DateTimeOffset.UtcNow;
            if (item.TryGetProperty("date_created", out var dc))
            {
                // MP suele venir ISO string
                if (dc.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(dc.GetString(), out var parsed))
                    fechaUtc = parsed.ToUniversalTime();
            }

            // monto
            decimal monto = 0m;
            if (item.TryGetProperty("transaction_amount", out var ta))
            {
                if (ta.ValueKind == JsonValueKind.Number) monto = ta.GetDecimal();
                else if (ta.ValueKind == JsonValueKind.String && decimal.TryParse(ta.GetString(), out var d)) monto = d;
            }

            // Guardamos el JSON completo
            var jsonRaw = item.GetRawText();

            // Insert acorde a tu tabla (según tu captura):
            // id (PK), mp_account_id, payment_id, fecha_utc, monto, status, payment_type, json_raw, created_at
            var sql = """
                insert into transfers (mp_account_id, payment_id, fecha_utc, monto, status, payment_type, json_raw)
                values (@mpAccountId, @paymentId, @fechaUtc, @monto, @status, @paymentType, @jsonRaw::jsonb)
                on conflict (payment_id) do nothing;
            """;

            await using var cmd = new NpgsqlCommand(sql, conn);

            // mp_account_id: si lo tenés en config, lo usamos. Si no, 0.
            var mpAccountId = int.TryParse(_cfg["MpAccountId"], out var acc) ? acc : 0;

            cmd.Parameters.AddWithValue("mpAccountId", mpAccountId);
            cmd.Parameters.AddWithValue("paymentId", paymentId);
            cmd.Parameters.AddWithValue("fechaUtc", fechaUtc);
            cmd.Parameters.AddWithValue("monto", monto);
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("paymentType", paymentTypeId);
            cmd.Parameters.AddWithValue("jsonRaw", jsonRaw);

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows > 0) inserted++;
        }

        _log.LogInformation("MP polling OK. results={Count}, inserted={Inserted}", results.GetArrayLength(), inserted);
    }

    private string GetConn()
    {
        var cs = _cfg.GetConnectionString("Db");
        if (string.IsNullOrWhiteSpace(cs))
            throw new Exception("ConnectionStrings:Db vacío");

        // aceptar postgres://...
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
