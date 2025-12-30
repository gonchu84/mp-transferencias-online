using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

[ApiController]
[Route("api/transfers")]
[Authorize] // por defecto TODO requiere login
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

    // ✅ GET: /api/transfers/ping (SIN login) -> para probar que el controller existe y enruta bien
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

    // ✅ POST: /api/transfers/{transferId}/ack (REQUIERE login)
    [HttpPost("{transferId:long}/ack")]
    [Authorize]
    public async Task<IActionResult> Ack(long transferId)
    {
        var username = User?.Identity?.Name ?? "unknown";

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
        insert into transfer_ack (transfer_id, username, ack_date_ar)
        values (
          @transferId,
          @username,
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

    // ✅ POST: /api/transfers/{transferId}/ack-test (SIN login) -> SOLO para probar DB y ruta
    // Después lo borramos cuando ya esté OK el login.
    [HttpPost("{transferId:long}/ack-test")]
    [AllowAnonymous]
    public async Task<IActionResult> AckTest(long transferId)
    {
        // podés pasar un usuario fake por query: ?u=Banfield
        var username = Request.Query["u"].ToString();
        if (string.IsNullOrWhiteSpace(username)) username = "test";

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        var sql = """
        insert into transfer_ack (transfer_id, username, ack_date_ar)
        values (
          @transferId,
          @username,
          (now() at time zone 'America/Argentina/Buenos_Aires')::date
        );
        """;

        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("transferId", transferId);
            cmd.Parameters.AddWithValue("username", username);

            await cmd.ExecuteNonQueryAsync();
            return Ok(new { ok = true, transferId, username, mode = "ack-test" });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Conflict(new { ok = false, message = "Ya existe ACK para esa transferencia." });
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
