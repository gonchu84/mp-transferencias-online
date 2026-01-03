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

    // DTOs
    public record AssignMpAccountDto(string Username, int MpAccountId);

    // ==============================
    // Connection / Helpers
    // ==============================
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
        // Solo para endpoints /mp/* de debug (no multi-cuenta)
        var token = _cfg["MercadoPago:AccessToken"];
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("MercadoPago:AccessToken vacío");
        return token;
    }

    private static TimeZoneInfo GetArTz()
        => TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

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

    // mp_account_id del usuario logueado
    private static async Task<int> GetUserMpAccountId(NpgsqlConnection conn, string username)
    {
        var sql = """
            select mp_account_id
            from app_users
            where username = @u
            limit 1;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("u", username);

        var obj = await cmd.ExecuteScalarAsync();
        if (obj == null || obj == DBNull.Value)
            throw new Exception($"El usuario '{username}' no tiene mp_account_id asignado.");

        return Convert.ToInt32(obj);
    }

    // ==============================
    // Básicos
    // ==============================
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

    // DEBUG global (no filtra por cuenta)
    // GET /api/transfers/last?limit=20
    [HttpGet("last")]
    public async Task<IActionResult> Last([FromQuery] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 200);

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
            select id, payment_id, fecha_utc, monto, payment_type, status, mp_account_id
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
                status = rd.GetString(5),
                mp_account_id = rd.IsDBNull(6) ? (int?)null : rd.GetInt32(6)
            });
        }

        return Ok(new { ok = true, count = items.Count, items });
    }

    // ==============================
    // Sucursal: ver SOLO su cuenta asignada
    // ==============================

    // Pendientes (no aceptadas por nadie) - FILTRA por cuenta asignada
    // GET /api/transfers/pending?limit=20
    [HttpGet("pending")]
    public async Task<IActionResult> Pending([FromQuery] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 200);
        var username = User.Identity?.Name ?? "unknown";

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var mpAccountId = await GetUserMpAccountId(conn, username);

        var sql = """
            select t.id, t.payment_id, t.fecha_utc, t.monto, t.payment_type, t.status
            from transfers t
            left join transfer_ack a on a.transfer_id = t.id
            where a.transfer_id is null
              and t.mp_account_id = @acc
            order by t.id desc
            limit @limit;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("acc", mpAccountId);
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

        return Ok(new { ok = true, mp_account_id = mpAccountId, count = items.Count, items });
    }

    // Aceptadas HOY por el usuario - FILTRA por cuenta asignada
    // GET /api/transfers/accepted/today
    [HttpGet("accepted/today")]
    public async Task<IActionResult> AcceptedToday()
    {
        var username = User.Identity?.Name ?? "unknown";

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var mpAccountId = await GetUserMpAccountId(conn, username);

        var sql = """
            select
                t.id, t.payment_id, t.fecha_utc, t.monto, t.payment_type, t.status,
                a.username, a.ack_at_utc, a.ack_date_ar
            from transfer_ack a
            join transfers t on t.id = a.transfer_id
            where a.username = @username
              and a.ack_date_ar = (now() at time zone 'America/Argentina/Buenos_Aires')::date
              and t.mp_account_id = @acc
            order by a.ack_at_utc desc;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("username", username);
        cmd.Parameters.AddWithValue("acc", mpAccountId);

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

        return Ok(new { ok = true, username, mp_account_id = mpAccountId, count = items.Count, total, items });
    }

    // Aceptadas por día (usuario logueado) - FILTRA por cuenta asignada
    // GET /api/transfers/accepted/by-day?date=2025-12-31
    [HttpGet("accepted/by-day")]
    public async Task<IActionResult> AcceptedByDay([FromQuery] string date)
    {
        var username = User.Identity?.Name ?? "unknown";

        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return BadRequest(new { ok = false, message = "Formato inválido. Usá yyyy-MM-dd", date });

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var mpAccountId = await GetUserMpAccountId(conn, username);

        var sql = """
            select
                t.id, t.payment_id, t.fecha_utc, t.monto, t.payment_type, t.status,
                a.username, a.ack_at_utc, a.ack_date_ar
            from transfer_ack a
            join transfers t on t.id = a.transfer_id
            where a.username = @username
              and a.ack_date_ar = @day::date
              and t.mp_account_id = @acc
            order by a.ack_at_utc desc;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("username", username);
        cmd.Parameters.AddWithValue("day", day);
        cmd.Parameters.AddWithValue("acc", mpAccountId);

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

        return Ok(new { ok = true, username, mp_account_id = mpAccountId, date, count = items.Count, total, items });
    }

    // Datos de cuenta MP asignada a la sucursal
    // GET /api/transfers/me/account
    [HttpGet("me/account")]
    public async Task<IActionResult> MyMpAccount()
    {
        var username = User.Identity?.Name ?? "unknown";

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
            select a.id, a.nombre, coalesce(a.alias,''), coalesce(a.cvu,''), a.activa
            from app_users u
            join mp_accounts a on a.id = u.mp_account_id
            where u.username = @username
            limit 1;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("username", username);

        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
            return NotFound(new { ok = false, message = "La sucursal no tiene cuenta MP asignada" });

        return Ok(new
        {
            ok = true,
            id = rd.GetInt32(0),
            nombre = rd.GetString(1),
            alias = rd.GetString(2),
            cvu = rd.GetString(3),
            activa = rd.GetBoolean(4)
        });
    }

    // Aceptar por paymentId (SEGURIDAD: solo si pertenece a su cuenta)
    // POST /api/transfers/payment/{paymentId}/ack
    [HttpPost("payment/{paymentId}/ack")]
    public async Task<IActionResult> AckByPaymentId(string paymentId)
    {
        var username = User.Identity?.Name ?? "unknown";

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var mpAccountId = await GetUserMpAccountId(conn, username);

        var findSql = "select id from transfers where payment_id = @paymentId and mp_account_id = @acc limit 1;";
        long transferId;

        await using (var findCmd = new NpgsqlCommand(findSql, conn))
        {
            findCmd.Parameters.AddWithValue("paymentId", paymentId);
            findCmd.Parameters.AddWithValue("acc", mpAccountId);

            var obj = await findCmd.ExecuteScalarAsync();
            if (obj == null)
                return NotFound(new { ok = false, message = "No existe esa transferencia para tu cuenta asignada.", paymentId });

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

    // Aceptar por transferId (SEGURIDAD: solo si pertenece a su cuenta)
    // POST /api/transfers/{transferId}/ack
    [HttpPost("{transferId:long}/ack")]
    public async Task<IActionResult> Ack(long transferId)
    {
        var username = User.Identity?.Name ?? "unknown";

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var mpAccountId = await GetUserMpAccountId(conn, username);

        // chequear que el transferId es de esa cuenta
        var checkSql = "select 1 from transfers where id = @id and mp_account_id = @acc limit 1;";
        await using (var checkCmd = new NpgsqlCommand(checkSql, conn))
        {
            checkCmd.Parameters.AddWithValue("id", transferId);
            checkCmd.Parameters.AddWithValue("acc", mpAccountId);

            var ok = await checkCmd.ExecuteScalarAsync();
            if (ok == null)
                return NotFound(new { ok = false, message = "No existe esa transferencia para tu cuenta asignada." });
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
            return Ok(new { ok = true, transferId, username });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Conflict(new { ok = false, message = "Esta transferencia ya fue aceptada por otra sucursal." });
        }
    }

    // ==============================
    // Admin
    // ==============================

    // ADMIN: aceptadas por día + filtro por sucursal(username)
    // GET /api/transfers/admin/accepted/by-day?date=2026-01-03&sucursal=Banfield
    [HttpGet("admin/accepted/by-day")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> AdminAcceptedByDay([FromQuery] string date, [FromQuery] string? sucursal = null)
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return BadRequest(new { ok = false, message = "Formato inválido. Usá yyyy-MM-dd", date });

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var hasSucursal = !string.IsNullOrWhiteSpace(sucursal);

        var sql = hasSucursal ? """
            select
                t.id, t.payment_id, t.fecha_utc, t.monto, t.payment_type, t.status,
                a.username, a.ack_at_utc, a.ack_date_ar,
                t.mp_account_id
            from transfer_ack a
            join transfers t on t.id = a.transfer_id
            where a.ack_date_ar = @day::date
              and a.username = @sucursal
            order by a.ack_at_utc desc;
        """ : """
            select
                t.id, t.payment_id, t.fecha_utc, t.monto, t.payment_type, t.status,
                a.username, a.ack_at_utc, a.ack_date_ar,
                t.mp_account_id
            from transfer_ack a
            join transfers t on t.id = a.transfer_id
            where a.ack_date_ar = @day::date
            order by a.ack_at_utc desc;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("day", day);
        if (hasSucursal) cmd.Parameters.AddWithValue("sucursal", sucursal!);

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
                ack_date_ar = rd.GetFieldValue<DateTime>(8).ToString("yyyy-MM-dd"),
                mp_account_id = rd.IsDBNull(9) ? (int?)null : rd.GetInt32(9)
            });
        }

        return Ok(new { ok = true, date, sucursal = sucursal ?? "", count = items.Count, total, items });
    }

    // ADMIN: lista de sucursales/usuarios
    // GET /api/transfers/admin/sucursales
    [HttpGet("admin/sucursales")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> AdminSucursales()
    {
        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
            select username
            from app_users
            where coalesce(role,'') <> 'admin'
            order by username;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);

        var items = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            items.Add(rd.GetString(0));

        return Ok(new { ok = true, items });
    }

    // ADMIN: listar cuentas MP (para dropdown)
    // GET /api/transfers/admin/mp-accounts
    [HttpGet("admin/mp-accounts")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> AdminMpAccounts()
    {
        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
            select id, nombre, activa, coalesce(alias,''), coalesce(cvu,'')
            from mp_accounts
            order by id;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);

        var items = new List<object>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            items.Add(new
            {
                id = rd.GetInt32(0),
                nombre = rd.GetString(1),
                activa = rd.GetBoolean(2),
                alias = rd.GetString(3),
                cvu = rd.GetString(4)
            });
        }

        return Ok(new { ok = true, items });
    }

    // ADMIN: asignar cuenta MP a una sucursal
    // POST /api/transfers/admin/assign-mp-account
    [HttpPost("admin/assign-mp-account")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> AssignMpAccount([FromBody] AssignMpAccountDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Username))
            return BadRequest(new { ok = false, message = "Username requerido" });

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
            update app_users
            set mp_account_id = @acc
            where username = @u;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("u", dto.Username);
        cmd.Parameters.AddWithValue("acc", dto.MpAccountId);

        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0)
            return NotFound(new { ok = false, message = "Sucursal no encontrada" });

        return Ok(new { ok = true, dto.Username, dto.MpAccountId });
    }

    // ==============================
    // MP Debug (opcional)
    // ==============================

    // GET /api/transfers/mp/me
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

            return Ok(new { ok = resp.IsSuccessStatusCode, status = (int)resp.StatusCode, url = mpUrl, body });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { ok = false, message = "Error MP /me", error = ex.Message });
        }
    }

    // GET /api/transfers/mp/search?minutes=60&payment_type_id=bank_transfer
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
}
