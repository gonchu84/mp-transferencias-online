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
        var ar = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3));
        return Ok(new
        {
            ok = true,
            user,
            utc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            ar = ar.ToString("yyyy-MM-dd HH:mm:ss")
        });
    }

    // ✅ POST: /api/transfers/{transferId}/ack
    [HttpPost("{transferId:long}/ack")]
    public async Task<IActionResult> Ack(long transferId)
    {
        var username = User.Identity?.Name ?? "unknown";

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        // Insert con RETURNING para saber si insertó o ya existía
        var sql = """
        insert into transfer_ack (transfer_id, username, ack_at_utc, ack_date_ar)
        values (
          @transferId,
          @username,
          now(),
          (now() at time zone 'America/Argentina/Buenos_Aires')::date
        )
        on conflict (transfer_id) do nothing
        returning id;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("transferId", transferId);
        cmd.Parameters.AddWithValue("username", username);

        var insertedId = await cmd.ExecuteScalarAsync();

        if (insertedId is null)
        {
            return Conflict(new
            {
                ok = false,
                message = "Esta transferencia ya fue aceptada por otra sucursal."
            });
        }

        return Ok(new
        {
            ok = true,
            transferId,
            username,
            insertedId
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
            SslMode = SslMode.Require,
            TrustServerCertificate = true
        };

        return builder.ConnectionString;
    }
}
