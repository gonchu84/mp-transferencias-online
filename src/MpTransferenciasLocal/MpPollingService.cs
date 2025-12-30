using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Npgsql;

class MpPollingService : BackgroundService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;

    public MpPollingService(
        IHttpClientFactory http,
        IConfiguration cfg)
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
                    "v1/payments/search?sort=date_created&criteria=desc&limit=20",
                    stoppingToken);

                using var doc = JsonDocument.Parse(
                    await resp.Content.ReadAsStringAsync(stoppingToken));

                if (!doc.RootElement.TryGetProperty("results", out var results))
                    continue;

                await using var conn = new NpgsqlConnection(
                    _cfg.GetConnectionString("Db"));
                await conn.OpenAsync();

                foreach (var item in results.EnumerateArray())
                {
                    var mpId = item.GetProperty("id").GetInt64();

                    var sql = """
                    insert into transfers
                    (mp_payment_id, date_created, amount, payment_type_id, status)
                    values
                    (@mpId, @fecha, @monto, @medio, @estado)
                    on conflict (mp_payment_id) do nothing;
                    """;

                    await using var cmd = new NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("mpId", mpId);
                    cmd.Parameters.AddWithValue("fecha", item.GetProperty("date_created").GetDateTimeOffset());
                    cmd.Parameters.AddWithValue("monto", item.GetProperty("transaction_amount").GetDecimal());
                    cmd.Parameters.AddWithValue("medio", item.GetProperty("payment_type_id").GetString() ?? "");
                    cmd.Parameters.AddWithValue("estado", item.GetProperty("status").GetString() ?? "");

                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch
            {
                // loguear si querés
            }

            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }
}
