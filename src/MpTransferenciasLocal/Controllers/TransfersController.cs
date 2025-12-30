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

    // ✅ GET: /api/transfers/ping
    [HttpGet("ping")]
    public IActionResult Ping()
    {
        var user = User.Identity?.Name ?? "unknown";

        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        var nowUtc = DateTimeOffset.UtcNow;
        var nowAr = TimeZoneInfo.ConvertTime(nowUtc, tz);

        return Ok(new
        {
            ok = true,
            user,
            utc = nowUtc.ToString("yyyy-MM-dd HH:mm:ss"),
            ar = nowAr.ToString("yyyy-MM-dd HH:mm:ss")
        });
    }

    // ✅ DEBUG: /api/transfers/last
    [HttpGet("last")]
    public async Task<IActionResult> Last()
    {
        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
        select id, mp_account_id, payment_id, fecha_utc, monto, status, payment_type
        from transfers
        order by id desc
        limit 10;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var rd = await cmd.ExecuteReaderAsync();

        var list = new List<object>();
        while (await rd.ReadAsync())
        {
            list.Add(new
            {
                id = rd.GetInt64(0),
                mp_account_id = rd.GetInt32(1),
                payment_id = rd.GetString(2),
                fecha_utc = rd.GetFieldValue<DateTimeOffset>(3),
                monto = rd.GetDecimal(4),
                status = rd.GetString(5),
                payment_type = rd.GetString(6),
            });
        }

        return Ok(new { ok = true, count = list.Count, items = list });
    }

    // ✅ POST: /api/transfers/payment/{paymentId}/ack  (ACK usando payment_id de MP)
    [HttpPost("payment/{paymentId}/ack")]
    public async Task<IActionResult> AckByPaymentId(string paymentId)
    {
        var username = User.Identity?.Name ?? "unknown";

        try
        {
            await using var conn = new NpgsqlConnection(GetConn());
            await conn.OpenAsync();

            // 1) buscar el ID interno (transfers.id) por payment_id
            var findSql = "select id from transfers where payment_id = @paymentId limit 1;";
            long transferId;

            await using (var findCmd = new NpgsqlCommand(findSql, conn))
            {
                findCmd.Parameters.AddWithValue("paymentId", paymentId);

                var obj = await findCmd.ExecuteScalarAsync();
                if (obj == null)
                    return NotFound(new
                    {
                        ok = false,
                        message = "No existe esa transferencia en la tabla transfers (payment_id no encontrado).",
                        paymentId
                    });

                transferId = (long)obj;
            }

            // 2) insertar ACK
            var insertSql = """
            insert into transfer_ack (transfer_id, username, ack_at_utc, ack_date_ar)
            values (
              @transferId,
              @username,
              now(),
              (now() at time zone 'America/Argentina/Buenos_Aires')::date
            );
            """;

            await using (var cmd = new NpgsqlCommand(insertSql, conn))
            {
                cmd.Parameters.AddWithValue("transferId", transferId);
                cmd.Parameters.AddWithValue("username", username);

                await cmd.ExecuteNonQueryAsync();
            }

            return Ok(new { ok = true, paymentId, transferId, username });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Conflict(new { ok = false, message = "Esta transferencia ya fue aceptada por otra sucursal." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { ok = false, message = "Error en ACK", error = ex.Message });
        }
    }

    // ✅ POST: /api/transfers/{transferId}/ack  (ACK por ID interno)
    [HttpPost("{transferId:long}/ack")]
    public async Task<IActionResult> Ack(long transferId)
    {
        var username = User.Identity?.Name ?? "unknown";

        try
        {
            await using var conn = new NpgsqlConnection(GetConn());
            await conn.OpenAsync();

            var sql = """
            insert into transfer_ack (transfer_id, username, ack_at_utc, ack_date_ar)
            values (
              @transferId,
              @username,
              now(),
              (now() at time zone 'America/Argentina/Buenos_Aires')::date
            );
            """;

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("transferId", transferId);
            cmd.Parameters.AddWithValue("username", username);

            await cmd.ExecuteNonQueryAsync();

            return Ok(new { ok = true, transferId, username });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Conflict(new { ok = false, message = "Esta transferencia ya fue aceptada por otra sucursal." });
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
