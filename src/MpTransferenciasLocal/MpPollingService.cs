using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Npgsql;

class MpPollingService : BackgroundService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;

    public MpPollingService(IHttpClientFactory http, IConfiguration cfg)
    {
        _http = http;
        _cfg = cfg;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = _cfg["MercadoPago:AccessToken"];
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("❌ MpPollingService: Falta MercadoPago:AccessToken");
            return;
        }

        var seconds = int.TryParse(_cfg["Polling:Seconds"], out var s) ? s : 10;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = _http.CreateClient("MP");
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                // Traemos los últimos movimientos (MP decide el tipo; filtramos nosotros)
                var resp = await client.GetAsync(
                    "v1/payments/search?sort=date_created&criteria=desc&limit=50",
                    stoppingToken);

                var raw = await resp.Content.ReadAsStringAsync(stoppingToken);

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ MP search HTTP {(int)resp.StatusCode}: {raw}");
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                    continue;
                }

                using var doc = JsonDocument.Parse(raw);

                if (!doc.RootElement.TryGetProperty("results", out var results))
                {
                    Console.WriteLine("⚠ MP search: no existe 'results'");
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                    continue;
                }

                await using var conn = new NpgsqlConnection(_cfg.GetConnectionString("Db"));
                await conn.OpenAsync(stoppingToken);

                var inserted = 0;
                foreach (var item in results.EnumerateArray())
                {
                    // Campos principales
                    var paymentType = item.TryGetProperty("payment_type_id", out var pt) ? (pt.GetString() ?? "") : "";
                    var status = item.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";

                    // 👉 Solo transferencias bancarias (como venías usando)
                    if (!string.Equals(paymentType, "bank_transfer", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 👉 Solo aprobadas (para que no meta estados raros; luego afinamos)
                    if (!string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var paymentId = item.GetProperty("id").ToString(); // texto
                    var fechaUtc = item.GetProperty("date_created").GetDateTimeOffset(); // viene con offset
                    var monto = item.GetProperty("transaction_amount").GetDecimal();

                    // collector_id = id de tu cuenta (sirve para mp_account_id)
                    var mpAccountId = item.TryGetProperty("collector_id", out var col) ? col.GetInt32() : 0;

                    var jsonRaw = item.GetRawText();

                    // Insert en tu esquema REAL
                    var sql = """
                    insert into transfers
                      (mp_account_id, payment_id, fecha_utc, monto, status, payment_type, json_raw, created_at)
                    values
                      (@mp_account_id, @payment_id, @fecha_utc, @monto, @status, @payment_type, @json_raw::jsonb, now())
                    on conflict (payment_id) do nothing;
                    """;

                    await using var cmd = new NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("mp_account_id", mpAccountId);
                    cmd.Parameters.AddWithValue("payment_id", paymentId);
                    cmd.Parameters.AddWithValue("fecha_utc", fechaUtc);
                    cmd.Parameters.AddWithValue("monto", monto);
                    cmd.Parameters.AddWithValue("status", status);
                    cmd.Parameters.AddWithValue("payment_type", paymentType);
                    cmd.Parameters.AddWithValue("json_raw", jsonRaw);

                    var rows = await cmd.ExecuteNonQueryAsync(stoppingToken);
                    if (rows > 0) inserted++;
                }

                if (inserted > 0)
                    Console.WriteLine($"✅ MpPollingService: Insertadas {inserted} transferencias nuevas.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ MpPollingService ERROR: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }
}
