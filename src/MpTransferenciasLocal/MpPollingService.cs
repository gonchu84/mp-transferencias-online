using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Npgsql;

public class MpPollingService : BackgroundService
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
            return;

        var seconds = int.TryParse(_cfg["Polling:Seconds"], out var s) ? s : 10;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = _http.CreateClient("MP");
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                var resp = await client.GetAsync(
                    "v1/account/movements/search?limit=20",
                    stoppingToken);

                var json = await resp.Content.ReadAsStringAsync(stoppingToken);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("results", out var results))
                    goto delay;

                await using var conn = new NpgsqlConnection(
                    _cfg.GetConnectionString("Db"));
                await conn.OpenAsync(stoppingToken);

                foreach (var item in results.EnumerateArray())
                {
                    // Solo transferencias entrantes
                    if (item.GetProperty("type").GetString() != "transfer")
                        continue;

                    var id = item.GetProperty("id").GetString();
                    var amount = item.GetProperty("amount").GetDecimal();
                    var date = item.GetProperty("date_created").GetDateTimeOffset();

                    var sql = """
                    insert into transfers
                    (payment_id, fecha_utc, monto, status, payment_type)
                    values
                    (@id, @fecha, @monto, 'approved', 'bank_transfer')
                    on conflict (payment_id) do nothing;
                    """;

                    await using var cmd = new NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("id", id ?? "");
                    cmd.Parameters.AddWithValue("fecha", date);
                    cmd.Parameters.AddWithValue("monto", amount);

                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("MP Polling error: " + ex.Message);
            }

        delay:
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }
}
