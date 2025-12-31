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

        _log.LogInformation("MpPollingService iniciado. Poll cada {Seconds}s", seconds);

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
        var cs = _cfg.GetConnectionString("Db");
        if (string.IsNullOrWhiteSpace(cs))
        {
            _log.LogWarning("ConnectionStrings:Db vacío");
            return;
        }

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);

        // 1) Traigo la cuenta activa (id + token)
        const string qAcc = """
        select id, access_token
        from mp_accounts
        where activa = true
        order by id
        limit 1;
        """;

        int mpAccountId;
        string token;

        await using (var cmdAcc = new NpgsqlCommand(qAcc, conn))
        await using (var r = await cmdAcc.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct))
            {
                _log.LogWarning("No hay mp_accounts activa = true");
                return;
            }

            mpAccountId = r.GetInt32(0);
            token = r.GetString(1);

            if (string.IsNullOrWhiteSpace(token) || token == "PENDING")
            {
                _log.LogWarning("mp_accounts id={Id} tiene access_token vacío/PENDING", mpAccountId);
                return;
            }
        }

        // 2) Llamo a MercadoPago con rango de tiempo bien formateado
        var minutes = 60; // podés subirlo a 1440 si querés
        var endUtc = DateTime.UtcNow;
        var beginUtc = endUtc.AddMinutes(-minutes);

        // MP suele aceptar ISO 8601 en UTC con 'Z'
        string begin = beginUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        string end = endUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

        var url = $"v1/payments/search?sort=date_created&criteria=desc&limit=50" +
                  $"&range=date_created&begin_date={Uri.EscapeDataString(begin)}&end_date={Uri.EscapeDataString(end)}" +
                  $"&payment_type_id=bank_transfer";

        var client = _http.CreateClient("MP");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync(url, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("MP ERROR {Status} body={Body}", (int)resp.StatusCode, raw);
            return;
        }

        using var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            _log.LogWarning("Respuesta MP sin results[]");
            return;
        }

        int inserted = 0;
        foreach (var item in results.EnumerateArray())
        {
            // Campos típicos
            var mpPaymentId = item.GetProperty("id").GetInt64();
            var fecha = item.GetProperty("date_created").GetDateTimeOffset();
            var monto = item.GetProperty("transaction_amount").GetDecimal();
            var status = item.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";
            var paymentType = item.TryGetProperty("payment_type_id", out var pt) ? (pt.GetString() ?? "") : "";

            // 3) Insert con FK correcto
            const string ins = """
            insert into transfers
            (mp_account_id, payment_id, fecha_utc, monto, status, payment_type, json_raw)
            values
            (@accId, @paymentId, @fecha, @monto, @status, @ptype, @raw::jsonb)
            on conflict (payment_id) do nothing;
            """;

            await using var cmd = new NpgsqlCommand(ins, conn);
            cmd.Parameters.AddWithValue("accId", mpAccountId);
            cmd.Parameters.AddWithValue("paymentId", mpPaymentId.ToString()); // tu columna payment_id es text
            cmd.Parameters.AddWithValue("fecha", fecha);
            cmd.Parameters.AddWithValue("monto", monto);
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("ptype", paymentType);
            cmd.Parameters.AddWithValue("raw", item.GetRawText());

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows > 0) inserted++;
        }

        _log.LogInformation("Polling OK. mp_account_id={AccId} results={Total} inserted={Ins}",
            mpAccountId, results.GetArrayLength(), inserted);
    }
}
