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
        var seconds = int.TryParse(_cfg["Polling:Seconds"], out var s) ? s : 10;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnce(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error general en polling");
            }

            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private async Task PollOnce(CancellationToken ct)
    {
        // 1) DB
        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync(ct);

        // 2) el mp_account activo (FK de transfers.mp_account_id)
        var mpAccountId = await GetActiveMpAccountId(conn, ct);
        var token = await GetActiveMpAccountToken(conn, mpAccountId, ct);

        if (string.IsNullOrWhiteSpace(token) || token == "PENDING")
        {
            _log.LogWarning("mp_accounts({Id}) no tiene access_token válido. Setealo en DB.", mpAccountId);
            return;
        }

        // 3) Fechas: últimos X minutos (default 60)
        var minutes = int.TryParse(_cfg["Polling:Minutes"], out var m) ? m : 60;
        var endUtc = DateTimeOffset.UtcNow;
        var beginUtc = endUtc.AddMinutes(-minutes);

        // MercadoPago: mandamos ISO invariante
        var beginStr = beginUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var endStr   = endUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        // 4) MP API
        var client = _http.CreateClient("MP");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // OJO: NO filtremos por bank_transfer por ahora, porque puede venir como account_money.
        // Luego afinamos.
        var url = $"v1/payments/search?sort=date_created&criteria=desc&limit=50&range=date_created" +
                  $"&begin_date={Uri.EscapeDataString(beginStr)}&end_date={Uri.EscapeDataString(endStr)}";

        var resp = await client.GetAsync(url, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("MP search error {Status}. Body={Body}", (int)resp.StatusCode, json);
            return;
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            _log.LogWarning("MP search sin results[]");
            return;
        }

        foreach (var item in results.EnumerateArray())
        {
            // id viene como número (ej 139322351059)
            var paymentId = item.TryGetProperty("id", out var idEl)
                ? idEl.ToString()
                : null;

            if (string.IsNullOrWhiteSpace(paymentId))
                continue;

            // Campos típicos
            var dateStr = item.GetProperty("date_created").GetString() ?? "";
            var fechaUtc = DateTimeOffset.Parse(dateStr, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime();



            var monto = item.TryGetProperty("transaction_amount", out var mEl) && mEl.ValueKind == JsonValueKind.Number
                ? mEl.GetDecimal()
                : 0m;

            var status = item.TryGetProperty("status", out var sEl) ? (sEl.GetString() ?? "") : "";
            var paymentType = item.TryGetProperty("payment_type_id", out var pEl) ? (pEl.GetString() ?? "") : "";

            // Guardamos el json entero (para debug)
            var raw = item.GetRawText();

            var sql = """
            insert into transfers
            (mp_account_id, payment_id, fecha_utc, monto, status, payment_type, json_raw)
            values
            (@mp_account_id, @payment_id, @fecha_utc, @monto, @status, @payment_type, @json_raw)
            on conflict (payment_id) do nothing;
            """;

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("mp_account_id", mpAccountId);
            cmd.Parameters.AddWithValue("payment_id", paymentId);
            cmd.Parameters.AddWithValue("fecha_utc", fecha);
            cmd.Parameters.AddWithValue("monto", monto);
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("payment_type", paymentType);
            cmd.Parameters.AddWithValue("json_raw", NpgsqlTypes.NpgsqlDbType.Jsonb, raw);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        _log.LogInformation("Polling OK. mp_account_id={Id} results={Count}", mpAccountId, results.GetArrayLength());
    }

    private async Task<int> GetActiveMpAccountId(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = "select id from mp_accounts where activa = true order by id limit 1;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        var obj = await cmd.ExecuteScalarAsync(ct);

        if (obj == null)
        {
            // fallback: primer registro
            sql = "select id from mp_accounts order by id limit 1;";
            await using var cmd2 = new NpgsqlCommand(sql, conn);
            obj = await cmd2.ExecuteScalarAsync(ct);
        }

        if (obj == null) throw new Exception("No hay registros en mp_accounts");

        return Convert.ToInt32(obj);
    }

    private async Task<string> GetActiveMpAccountToken(NpgsqlConnection conn, int id, CancellationToken ct)
    {
        var sql = "select access_token from mp_accounts where id = @id limit 1;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        var obj = await cmd.ExecuteScalarAsync(ct);
        return obj?.ToString() ?? "";
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
