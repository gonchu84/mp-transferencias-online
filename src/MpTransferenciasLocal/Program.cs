using System.Security.Claims;
using System.Text;
using Npgsql;
using Npgsql.PostgresTypes;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "5286";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Configuration.AddEnvironmentVariables();

builder.Services.AddControllers();
builder.Services.AddAuthorization();

builder.Services.AddHttpClient("MP", c =>
{
    c.BaseAddress = new Uri("https://api.mercadopago.com/");
});
builder.Services.AddHostedService<MpPollingService>();

static bool TryGetBasicCredentials(string authHeader, out string user, out string pass)
{
    user = string.Empty;
    pass = string.Empty;

    if (string.IsNullOrWhiteSpace(authHeader)) return false;
    if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;

    try
    {
        var encoded = authHeader["Basic ".Length..].Trim();
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        var parts = decoded.Split(':', 2);
        if (parts.Length != 2) return false;

        user = parts[0];
        pass = parts[1];
        return true;
    }
    catch { return false; }
}

static string NormalizeConnString(IConfiguration cfg)
{
    // Prioridad: ConnectionStrings:Db -> DATABASE_URL
    var cs = cfg.GetConnectionString("Db");
    if (string.IsNullOrWhiteSpace(cs))
        cs = cfg["DATABASE_URL"];

    if (string.IsNullOrWhiteSpace(cs))
        throw new Exception("NO_CONN_STRING: No se encontró ConnectionStrings:Db ni DATABASE_URL");

    cs = cs.Trim().Trim('"');

    // URL estilo Render: postgresql://user:pass@host/db
    if (cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        cs.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(cs);

        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";

        var db = uri.AbsolutePath.TrimStart('/');

        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = user,
            Password = pass,
            Database = db,
            SslMode = SslMode.Require,
            Timeout = 10,
            CommandTimeout = 10
        };

        return b.ConnectionString;
    }

    // Si ya viene como connstring tradicional, le forzamos SSL safe:
    var nb = new NpgsqlConnectionStringBuilder(cs)
    {
        SslMode = SslMode.Require,
        Timeout = 10,
        CommandTimeout = 10
    };
    return nb.ConnectionString;
}

static async Task<(bool ok, string? role, string? reason)> ValidateUserFromDbAsync(
    IConfiguration cfg,
    string username,
    string password)
{
    string cs;
    try
    {
        cs = NormalizeConnString(cfg);
    }
    catch (Exception ex)
    {
        return (false, null, ex.Message);
    }

    try
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        var sql = """
            select role, password_hash
            from app_users
            where username = @u
            limit 1;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("u", username);

        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
            return (false, null, "NO_USER: No existe el usuario en app_users (username).");

        var role = rd.IsDBNull(0) ? null : rd.GetString(0);
        var hash = rd.IsDBNull(1) ? "" : rd.GetString(1);

        if (string.IsNullOrWhiteSpace(hash))
            return (false, role, "EMPTY_HASH: password_hash vacío.");

        var ok = BCrypt.Net.BCrypt.Verify(password, hash);
        return ok ? (true, role, null) : (false, role, "BAD_PASS: clave incorrecta.");
    }
    catch (PostgresException pex) when (pex.SqlState == "42P01")
    {
        // tabla no existe
        return (false, null, "NO_TABLE: La tabla app_users no existe en esta base.");
    }
    catch (Exception ex)
    {
        // Esto es el “Error validando contra DB” que te aparece
        return (false, null, "DB_ERROR: " + ex.GetType().Name + " - " + ex.Message);
    }
}

static async Task Json401(HttpContext ctx, string message)
{
    ctx.Response.StatusCode = 401;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    await ctx.Response.WriteAsync($$"""{"ok":false,"message":"{{message}}"}""");
}

var app = builder.Build();

app.UseRouting();

app.Use(async (ctx, next) =>
{
    var hasAuth = ctx.Request.Headers.ContainsKey("Authorization");
    Console.WriteLine($"REQ {ctx.Request.Method} {ctx.Request.Path} AuthHdr={(hasAuth ? "YES" : "NO")}");
    await next();
    Console.WriteLine($"RESP {ctx.Response.StatusCode} {ctx.Request.Method} {ctx.Request.Path}");
});

// ✅ Auth SOLO /api
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var authHeader = ctx.Request.Headers["Authorization"].ToString();
    if (!TryGetBasicCredentials(authHeader, out var user, out var pass))
    {
        await Json401(ctx, "Falta header Authorization Basic.");
        return;
    }

    var (ok, role, reason) = await ValidateUserFromDbAsync(app.Configuration, user, pass);
    if (!ok)
    {
        // 🔥 IMPORTANTE: Esto te va a mostrar la causa real en el modal (y en Network)
        Console.WriteLine($"AUTH FAIL user={user} reason={reason}");
        await Json401(ctx, reason ?? "Credenciales inválidas.");
        return;
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, user),
        new(ClaimTypes.Role, string.IsNullOrWhiteSpace(role) ? "sucursal" : role!)
    };
    ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Basic"));

    await next();
});

app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.Now }));
app.MapControllers();

// HOME (dejala como la tuya actual con modal; no la repito acá)
app.MapGet("/", () => Results.Text("OK HOME (pegá acá tu HTML)"));

app.Run();
