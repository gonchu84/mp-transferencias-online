using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Net.Http.Headers;
using System.Text.Json;

[ApiController]
[Route("api/transfers")]
[Authorize]
public class TransfersController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly IHttpClientFactory _http;

    public TransfersController(IConfiguration cfg, IHttpClientFactory http)
    {
        _cfg = cfg;
        _http = http;
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

    private string GetMpAccessToken()
    {
        // soporta: MercadoPago:AccessToken  (como me mostraste)
        var token = _cfg["MercadoPago:AccessToken"];
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("MercadoPago:AccessToken vacío");
        return token;
    }

    private static string ToMpUtcZ(DateTimeOffset dto)
        => dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"); // <-- FORMATO SEGURO PARA MP

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
    public async Task<IActionResult> Last([FromQuery] int limit = 20)
    {
        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
        select id, payment_id, fecha_utc, monto, payment_type, status
        from transfers
        order by id desc
        limit @limit;
        """;

        var list = new List<object>();

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            list.Add(new
            {
                id = rd.GetInt64(0),
                payment_id = rd.GetString(1),
                fecha_utc = rd.GetFieldValue<DateTimeOffset>(2).ToString("o"),
                monto = rd.GetDecimal(3),
                payment_type = rd.IsDBNull(4) ? null : rd.GetString(4),
                status = rd.IsDBNull(5) ? null : rd.GetString(5),
            });
        }

        return Ok(new { ok = true, count = list.Count, items = list });
    }

    // ✅ POST: /api/transfers/payment/{paymentId}/ack
    // ACK por payment_id de MP (FK es transfers.id, así que primero lo busco)
    [HttpPost("payment/{paymentId}/ack")]
    public async Task<IActionResult> AckByPaymentId(string paymentId)
    {
        var username = User.Identity?.Name ?? "unknown";

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        // 1) buscar transfers.id por payment_id
        long transferId;
        {
            var findSql = "select id from transfers where payment_id = @paymentId limit 1;";
            await using var findCmd = new NpgsqlCommand(findSql, conn);
            findCmd.Parameters.AddWithValue("paymentId", paymentId);

            var obj = await findCmd.ExecuteScalarAsync();
            if (obj is null)
                return NotFound(new { ok = false, message = "No existe esa transferencia en la tabla transfers (payment_id no encontrado).", paymentId });

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

    // ✅ POST: /api/transfers/{transferId}/ack (por id interno)
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

    // ✅ GET: /api/transfers/mp/me
    [HttpGet("mp/me")]
    public async Task<IActionResult> MpMe()
    {
        var token = GetMpAccessToken();
        var client = _http.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.mercadopago.com/users/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        return StatusCode((int)res.StatusCode, new
        {
            ok = res.IsSuccessStatusCode,
            status = (int)res.StatusCode,
            body
        });
    }

    // ✅ GET: /api/transfers/mp/search?minutes=1440&payment_type_id=bank_transfer
    // Esto ahora arma begin/end en formato UTC Z (válido) y evita el 400.
    [HttpGet("mp/search")]
    public async Task<IActionResult> MpSearch([FromQuery] int minutes = 10, [FromQuery] string? payment_type_id = "bank_transfer")
    {
        var token = GetMpAccessToken();
        var client = _http.CreateClient();

        var end = DateTimeOffset.UtcNow;
        var begin = end.AddMinutes(-Math.Abs(minutes));

        var beginStr = Uri.EscapeDataString(ToMpUtcZ(begin));
        var endStr = Uri.EscapeDataString(ToMpUtcZ(end));

        // OJO: renombro la variable para que NO choque con otra "url" (tu error de deploy)
        var mpUrl =
            $"https://api.mercadopago.com/v1/payments/search" +
            $"?sort=date_created&criteria=desc&limit=50" +
            $"&begin_date={beginStr}&end_date={endStr}" +
            (string.IsNullOrWhiteSpace(payment_type_id) ? "" : $"&payment_type_id={Uri.EscapeDataString(payment_type_id)}");

        var req = new HttpRequestMessage(HttpMethod.Get, mpUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        return StatusCode((int)res.StatusCode, new
        {
            ok = res.IsSuccessStatusCode,
            status = (int)res.StatusCode,
            url = mpUrl,
            begin_utc = begin.ToString("o"),
            end_utc = end.ToString("o"),
            body
        });
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
