using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

[ApiController]
[Route("api/transfers")]
[Authorize]
public class TransfersController : ControllerBase
{
    private readonly IConfiguration _cfg;

    public TransfersController(IConfiguration cfg)
    {
        _cfg = cfg;
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

    // ✅ GET: /api/transfers/ping  (para probar rápido)
    // Lo dejo público para testear sin drama
    [HttpGet("ping")]
    [AllowAnonymous]
    public IActionResult Ping()
    {
        return Ok(new
        {
            ok = true,
            utc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            ar = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)).ToString("yyyy-MM-dd HH:mm:ss")
        });
    }

    // ✅ POST: /api/transfers/payment/{paymentId}/ack
    // Este es el que vas a usar SIEMPRE con el ID de MercadoPago (payment_id)
    [HttpPost("payment/{paymentId}/ack")]
    public async Task<IActionResult> AckByPaymentId(string paymentId)
    {
        var username = User.Identity?.Name ?? "unknown";

        try
        {
            await using var conn = new NpgsqlConnection(GetConn());
            await conn.OpenAsync();

            // 1) Busco el ID interno de transfers por payment_id (MP)
            long transferId;
            const string findSql = """
                select id
                from transfers
                where payment_id = @paymentId
                limit 1;
            """;

            await using (var findCmd = new NpgsqlCommand(findSql, conn))
            {
                findCmd.Parameters.AddWithValue("paymentId", paymentId);
                var result = await findCmd.ExecuteScalarAsync();

                if (result is null)
                    return NotFound(new
                    {
                        ok = false,
                        message = "No existe esa transferencia en la tabla transfers (payment_id no encontrado).",
                        paymentId
                    });

                transferId = Convert.ToInt64(result);
            }

            // 2) Inserto ACK usando el ID interno (transfer_id FK)
            // Si ya existe, devuelvo 409
            const string insertSql = """
                insert into transfer_ack (transfer_id, username, ack_at_utc, ack_date_ar)
                values (
                  @transferId,
                  @username,
                  now(),
                  (now() at time zone 'America/Argentina/Buenos_Aires')::date
                );
            """;

            try
            {
                await using var cmd = new NpgsqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue("transferId", transferId);
                cmd.Parameters.AddWithValue("username", username);

                await cmd.ExecuteNonQueryAsync();

                return Ok(new { ok = true, paymentId, transferId, username });
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Conflict(new
                {
                    ok = false,
                    message = "Esta transferencia ya fue aceptada (ACK ya existe).",
                    paymentId,
                    transferId
                });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                ok = false,
                message = "Error en ACK",
                error = ex.Message
            });
        }
    }

    // ✅ (Opcional) POST: /api/transfers/{transferId}/ack
    // Este sirve si alguna vez querés llamar por ID interno (transfers.id)
    [HttpPost("{transferId:long}/ack")]
    public async Task<IActionResult> AckByTransferId(long transferId)
    {
        var username = User.Identity?.Name ?? "unknown";

        try
        {
            await using var conn = new NpgsqlConnection(GetConn());
            await conn.OpenAsync();

            // Verifico que exista transfers.id para no caer en FK
            const string existsSql = "select 1 from transfers where id=@id limit 1;";
            await using (var existsCmd = new NpgsqlCommand(existsSql, conn))
            {
                existsCmd.Parameters.AddWithValue("id", transferId);
                var exists = await existsCmd.ExecuteScalarAsync();
                if (exists is null)
                    return NotFound(new { ok = false, message = "No existe transfers.id", transferId });
            }

            const string insertSql = """
                insert into transfer_ack (transfer_id, username, ack_at_utc, ack_date_ar)
                values (
                  @transferId,
                  @username,
                  now(),
                  (now() at time zone 'America/Argentina/Buenos_Aires')::date
                );
            """;

            try
            {
                await using var cmd = new NpgsqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue("transferId", transferId);
                cmd.Parameters.AddWithValue("username", username);
                await cmd.ExecuteNonQueryAsync();

                return Ok(new { ok = true, transferId, username });
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Conflict(new { ok = false, message = "ACK ya existe", transferId });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { ok = false, message = "Error en ACK", error = ex.Message });
        }
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
            SslMode = SslMode.Require,
            TrustServerCertificate = true
        };

        return builder.ConnectionString;
    }
}
