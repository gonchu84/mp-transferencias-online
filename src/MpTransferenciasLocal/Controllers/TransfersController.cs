using System.Globalization;
using System.Net.Http.Headers;
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
        // 1) Primero ConnectionStrings:Db
        var cs = _cfg.GetConnectionString("Db") ?? "";

        // 2) Fallback por si Render lo pone en DATABASE_URL
        if (string.IsNullOrWhiteSpace(cs))
            cs = _cfg["DATABASE_URL"] ?? "";

        if (string.IsNullOrWhiteSpace(cs))
            throw new Exception("No se encontró ConnectionStrings:Db ni DATABASE_URL");

        cs = cs.Trim().Trim('"');

        // aceptar postgres:// y postgresql://
        if (cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            cs.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            cs = ConvertPostgresUrlToConnectionString(cs);

        return cs;
    }

    private string GetMpToken()
    {
        var token = _cfg["MercadoPago:AccessToken"];
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("MercadoPago:AccessToken vacío");
        return token;
    }

    private static TimeZoneInfo GetArTz()
        => TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    // ✅ GET: /api/transfers/ping
    [HttpGet("ping")]
    public IActionResult Ping()
    {
        var user = User.Identity?.Name ?? "unknown";
        var tz = GetArTz();

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

    // ✅ DEBUG: últimos items guardados
    // GET /api/transfers/last?limit=20
    [HttpGet("last")]
    public async Task<IActionResult> Last([FromQuery] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 200);

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
            select id, payment_id, fecha_utc, monto, payment_type, status
            from transfers
            order by id desc
            limit @limit;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);

        var items = new List<object>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            items.Add(new
            {
                id = rd.GetInt64(0),
                payment_id = rd.GetString(1),
                fecha_utc = rd.GetFieldValue<DateTimeOffset>(2).ToString("o"),
                monto = rd.GetDecimal(3),
                payment_type = rd.GetString(4),
                status = rd.GetString(5)
            });
        }

        return Ok(new { ok = true, count = items.Count, items });
    }

    // ✅ NUEVO: Pendientes (no aceptadas por nadie)
    // GET /api/transfers/pending?limit=20
    [HttpGet("pending")]
    public async Task<IActionResult> Pending([FromQuery] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 200);

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
            select t.id, t.payment_id, t.fecha_utc, t.monto, t.payment_type, t.status
            from transfers t
            left join transfer_ack a on a.transfer_id = t.id
            where a.transfer_id is null
            order by t.id desc
            limit @limit;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);

        var items = new List<object>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            items.Add(new
            {
                id = rd.GetInt64(0),
                payment_id = rd.GetString(1),
                fecha_utc = rd.GetFieldValue<DateTimeOffset>(2).ToString("o"),
                monto = rd.GetDecimal(3),
                payment_type = rd.GetString(4),
                status = rd.GetString(5)
            });
        }

        return Ok(new { ok = true, count = items.Count, items });
    }

    // ✅ NUEVO: Aceptadas HOY por el usuario logueado (detalle + total)
    // GET /api/transfers/accepted/today
    [HttpGet("accepted/today")]
    public async Task<IActionResult> AcceptedToday()
    {
        var username = User.Identity?.Name ?? "unknown";

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
            select
                t.id, t.payment_id, t.fecha_utc, t.monto, t.payment_type, t.status,
                a.username, a.ack_at_utc, a.ack_date_ar
            from transfer_ack a
            join transfers t on t.id = a.transfer_id
            where a.username = @username
              and a.ack_date_ar = (now() at time zone 'America/Argentina/Buenos_Aires')::date
            order by a.ack_at_utc desc;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("username", username);

        var items = new List<object>();
        decimal total = 0;

        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var monto = rd.GetDecimal(3);
            total += monto;

            items.Add(new
            {
                id = rd.GetInt64(0),
                payment_id = rd.GetString(1),
                fecha_utc = rd.GetFieldValue<DateTimeOffset>(2).ToString("o"),
                monto,
                payment_type = rd.GetString(4),
                status = rd.GetString(5),
                accepted_by = rd.GetString(6),
                ack_at_utc = rd.GetFieldValue<DateTimeOffset>(7).ToString("o"),
                ack_date_ar = rd.GetFieldValue<DateTime>(8).ToString("yyyy-MM-dd")
            });
        }

        return Ok(new { ok = true, username, count = items.Count, total, items });
    }

    // ✅ NUEVO: Aceptadas por DÍA (por usuario logueado) - historial + total
    // GET /api/transfers/accepted/by-day?date=2025-12-31
    [HttpGet("accepted/by-day")]
    public async Task<IActionResult> AcceptedByDay([FromQuery] string date)
    {
        var username = User.Identity?.Name ?? "unknown";

        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return BadRequest(new { ok = false, message = "Formato inválido. Usá yyyy-MM-dd", date });
        }

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
            select
                t.id, t.payment_id, t.fecha_utc, t.monto, t.payment_type, t.status,
                a.username, a.ack_at_utc, a.ack_date_ar
            from transfer_ack a
            join transfers t on t.id = a.transfer_id
            where a.username = @username
              and a.ack_date_ar = @day::date
            order by a.ack_at_utc desc;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("username", username);
        cmd.Parameters.AddWithValue("day", day);

        var items = new List<object>();
        decimal total = 0;

        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var monto = rd.GetDecimal(3);
            total += monto;

            items.Add(new
            {
                id = rd.GetInt64(0),
                payment_id = rd.GetString(1),
                fecha_utc = rd.GetFieldValue<DateTimeOffset>(2).ToString("o"),
                monto,
                payment_type = rd.GetString(4),
                status = rd.GetString(5),
                accepted_by = rd.GetString(6),
                ack_at_utc = rd.GetFieldValue<DateTimeOffset>(7).ToString("o"),
                ack_date_ar = rd.GetFieldValue<DateTime>(8).ToString("yyyy-MM-dd")
            });
        }

        return Ok(new { ok = true, username, date, count = items.Count, total, items });
    }

    // ✅ MP: /api/transfers/mp/me
    [HttpGet("mp/me")]
    public async Task<IActionResult> MpMe()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetMpToken());

            var mpUrl = "https://api.mercadopago.com/users/me";
            var resp = await http.GetAsync(mpUrl);
            var body = await resp.Content.ReadAsStringAsync();

            return Ok(new
            {
                ok = resp.IsSuccessStatusCode,
                status = (int)resp.StatusCode,
                url = mpUrl,
                body
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { ok = false, message = "Error MP /me", error = ex.Message });
        }
    }

    // ✅ MP SEARCH
    // /api/transfers/mp/search?minutes=1440&payment_type_id=bank_transfer
    [HttpGet("mp/search")]
    public async Task<IActionResult> MpSearch([FromQuery] int minutes = 60, [FromQuery(Name = "payment_type_id")] string paymentTypeId = "bank_transfer")
    {
        minutes = Math.Clamp(minutes, 1, 60 * 24 * 7);

        var endUtc = DateTimeOffset.UtcNow;
        var beginUtc = endUtc.AddMinutes(-minutes);

        var beginStr = beginUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var endStr = endUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        var mpUrl =
            "https://api.mercadopago.com/v1/payments/search" +
            "?sort=date_created&criteria=desc&limit=50" +
            $"&begin_date={Uri.EscapeDataString(beginStr)}" +
            $"&end_date={Uri.EscapeDataString(endStr)}" +
            $"&payment_type_id={Uri.EscapeDataString(paymentTypeId)}";

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetMpToken());

            var resp = await http.GetAsync(mpUrl);
            var body = await resp.Content.ReadAsStringAsync();

            return Ok(new
            {
                ok = resp.IsSuccessStatusCode,
                status = (int)resp.StatusCode,
                url = mpUrl,
                begin_utc = beginUtc.ToString("o"),
                end_utc = endUtc.ToString("o"),
                body
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { ok = false, message = "Error MP search", error = ex.Message, url = mpUrl });
        }
    }

    // ✅ POST: /api/transfers/payment/{paymentId}/ack
    [HttpPost("payment/{paymentId}/ack")]
    public async Task<IActionResult> AckByPaymentId(string paymentId)
    {
        var username = User.Identity?.Name ?? "unknown";

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var findSql = "select id from transfers where payment_id = @paymentId limit 1;";
        long transferId;

        await using (var findCmd = new NpgsqlCommand(findSql, conn))
        {
            findCmd.Parameters.AddWithValue("paymentId", paymentId);
            var obj = await findCmd.ExecuteScalarAsync();
            if (obj == null)
            {
                return NotFound(new
                {
                    ok = false,
                    message = "No existe esa transferencia en la tabla transfers (payment_id no encontrado).",
                    paymentId
                });
            }
            transferId = (long)obj;
        }

        var insertSql = """
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
            return Conflict(new { ok = false, message = "Esta transferencia ya fue aceptada por otra sucursal." });
        }
    }

    // ✅ POST: /api/transfers/{transferId}/ack
    [HttpPost("{transferId:long}/ack")]
    public async Task<IActionResult> Ack(long transferId)
    {
        var username = User.Identity?.Name ?? "unknown";

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

        try
        {
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
