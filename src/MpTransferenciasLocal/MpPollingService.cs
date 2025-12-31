using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

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

        // 2) mp_account activo (FK transfers.mp_account_id)
        var mpAccountId = await GetActiveMpAccountId(conn, ct);
        var token = await GetMpAccountToken(conn, mpAccountId, ct);

        if (string.IsNullOrWhiteSpace(token) || token == "PENDING")
        {
            _log.LogWarning("mp_accounts({Id}) no tiene access_token válido (está vacío o PENDING).", mpAccountId);
            return;
        }

        // 3) Rango de fechas: últimos X minutos (default 60)
        var minutes = int.TryParse(_cfg["Polling:Minutes"], out var m) ? m : 60;
        var endUtc = DateTimeOffset.UtcNow;
        var beginUtc = endUtc.AddMinutes(-minutes);

        // MercadoPago: ISO con Z
        var beginStr = beginUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var endStr   = endUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        // 4) MP API
        var client = _http.CreateClient("MP");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // NO filtrar todavía por bank_transfer (a veces viene como account_money)
        var url =
            $"v1/payments/search?sort=date_created&criteria=desc&limit=50&range=date_created" +
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

        var inserted = 0;

        foreach (var item in results.EnumerateArray())
        {
            // payment_id (MP id)
            var paymentId = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
            if (string.IsNullOrWhiteSpace(paymentId))
                continue;

            // Fecha: viene como string con offset, la convertimos a UTC
            var dateStr = item.TryGetProperty("date_created", out var dEl) ? (dEl.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(dateStr))
                continue;

            // RoundtripKind respeta el offset y ToUniversalTime lo deja en UTC
            var dto = DateTimeOffset.Parse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                                   .ToUniversalTime();

            // Para evitar el error de Npgsql con offsets, mandamos DateTime UTC
            var fechaUtc = DateTime.SpecifyKind(dto.UtcDateTime, DateTimeKind.Utc);

            var monto = item.TryGetProperty("transaction_amount", out var mEl) && mEl.ValueKind == JsonValueKind.Number
                ? mEl.GetDecimal()
                : 0m;

            var status = item.TryGetProperty("status", out var sEl) ? (sEl.GetString() ?? "") : "";
            var paymentType = item.TryGetProperty("payment_type_id", out var pEl) ? (pEl.GetString() ?? "") : "";

            var raw = item.GetRawText();

            const string sql = """
            insert into transfers
            (mp_account_id, payment_id, fecha_utc, monto, status, payment_type, json_raw)
            values
            (@mp_account_id, @payment_id, @fecha_utc, @monto, @status, @payment_type, @json_raw)
            on conflict (payment_id) do nothing;
            """;

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("mp_account_id", mpAccountId);
            cmd.Parameters.AddWithValue("payment_id", paymentId);
            cmd.Parameters.AddWithValue("fecha_utc", fechaUtc);
            cmd.Parameters.AddWithValue("monto", monto);
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("payment_type", paymentType);
            cmd.Parameters.Add("json_raw", NpgsqlDbType.Jsonb).Value = raw;

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows > 0) inserted++;
        }

        _log.LogInformation("Polling OK. mp_account_id={Id} results={Count} inserted={Inserted}",
            mpAccountId, results.GetArrayLength(), inserted);
    }

    private static async Task<int> GetActiveMpAccountId(NpgsqlConnection conn, CancellationToken ct)
    {
        // prioriza activa=true
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

    private static async Task<string> GetMpAccountToken(NpgsqlConnection conn, int id, CancellationToken ct)
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
            throw new Exception("ConnectionStrings:Db vacío (falta en env vars o appsettings).");

        // Render a veces da DATABASE_URL tipo postgres://
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
