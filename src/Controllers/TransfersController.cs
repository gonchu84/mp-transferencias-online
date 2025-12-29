using System.Security.Claims;
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

    private string GetUsername()
    {
        // 1) Lo más confiable cuando hay auth por cookies o claims
        var claimName = User.FindFirstValue(ClaimTypes.Name);
        if (!string.IsNullOrWhiteSpace(claimName))
            return claimName;

        // 2) Fallback
        var idName = User.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(idName))
            return idName;

        return "unknown";
    }

    // POST: /api/transfers/{transferId}/ack
    [HttpPost("{transferId:long}/ack")]
    public async Task<IActionResult> Ack(long transferId)
    {
        var username = GetUsername();

        await using var conn = new NpgsqlConnection(GetConn());
        await conn.OpenAsync();

        // Reglas:
        // - Si ya existe ACK para ese transfer_id por el mismo usuario => OK (idempotente)
        // - Si ya existe ACK para ese transfer_id por otro usuario => 409 Conflict
        // - Si no existe => inserta
        var sql = """
        insert into transfer_ack (transfer_id, username, ack_date_ar)
        values (
          @transferId,
          @username,
          (now() at time zone 'America/Argentina/Buenos_Aires')::date
        )
        on conflict (transfer_id) do nothing;

        select username
        from transfer_ack
        where transfer_id = @transferId
        limit 1;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("transferId", transferId);
        cmd.Parameters.AddWithValue("username", username);

        // Ejecutamos y leemos quién quedó como dueño del ACK
        var ackOwnerObj = await cmd.ExecuteScalarAsync();
        var ackOwner = ackOwnerObj?.ToString() ?? "";

        if (string.IsNullOrWhiteSpace(ackOwner))
            return StatusCode(500, new { ok = false, message = "No se pudo confirmar el ACK." });

        if (!string.Equals(ackOwner, username, StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new
            {
                ok = false,
                message = "Esta transferencia ya fue aceptada por otra sucursal.",
                acceptedBy = ackOwner
            });
        }

        // OK: o la insertó recién, o ya era suya
        return Ok(new { ok = true, transferId, username });
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
