using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// ================= ONLINE HOSTING (Render/Docker/VPS) =================
// Render (y muchos hosts) inyectan PORT. Si no existe, usamos 5286 como fallback.
var port = Environment.GetEnvironmentVariable("PORT") ?? "5286";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ================= CONFIG =================

// Base
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

// ✅ Override por cuenta (publish / dotnet run)
var configFromExe = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.Sucursal.json");
var configFromDev = Path.GetFullPath(
    Path.Combine(builder.Environment.ContentRootPath, "..", "..", "config", "appsettings.Sucursal.json")
);

string? configUsado = null;

if (File.Exists(configFromExe))
{
    builder.Configuration.AddJsonFile(configFromExe, optional: false, reloadOnChange: true);
    configUsado = configFromExe;
}
else if (File.Exists(configFromDev))
{
    builder.Configuration.AddJsonFile(configFromDev, optional: false, reloadOnChange: true);
    configUsado = configFromDev;
}
else
{
    Console.WriteLine("⚠ No se encontró appsettings.Sucursal.json en:");
    Console.WriteLine(" - " + configFromExe);
    Console.WriteLine(" - " + configFromDev);
}

// ================= SERVICES =================

builder.Services.AddHttpClient("MP", c =>
{
    c.BaseAddress = new Uri("https://api.mercadopago.com/");
});

builder.Services.AddSingleton<ConcurrentQueue<object>>();
builder.Services.AddHostedService<MpPollingService>();

var app = builder.Build();

// ================= AUTH (Basic) =================
// Soporta múltiples usuarios (pensado para 11 sucursales + 1 admin).
// Se configura con Auth:Users (array) por appsettings o variables de entorno.
var authUsers = LoadAuthUsers(app.Configuration);

if (authUsers.Count > 0)
{
    app.Use(async (ctx, next) =>
    {
        // Health sin auth (para monitoreo del host)
        if (ctx.Request.Path.StartsWithSegments("/health"))
        {
            await next();
            return;
        }

        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var encoded = header["Basic ".Length..].Trim();
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var parts = decoded.Split(':', 2);

                if (parts.Length == 2)
                {
                    var user = parts[0];
                    var pass = parts[1];

                    if (authUsers.TryGetValue(user, out var u) && u.Pass == pass)
                    {
                        // Setear identidad simple (por si más adelante querés roles/admin)
                        var claims = new List<Claim>
                        {
                            new(ClaimTypes.Name, u.User),
                            new(ClaimTypes.Role, u.Role ?? "user")
                        };
                        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Basic"));
                        await next();
                        return;
                    }
                }
            }
            catch
            {
                // ignore -> cae en 401
            }
        }

        ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"MP Transferencias\"";
        ctx.Response.StatusCode = 401;
    });
}

// ================= LOG =================

Console.WriteLine("========================================");
Console.WriteLine("MPTransferenciasLocal iniciado");
Console.WriteLine("ContentRootPath: " + builder.Environment.ContentRootPath);
Console.WriteLine("Config usado: " + (configUsado ?? "(ninguno)"));
Console.WriteLine("Cuenta: " + (builder.Configuration["Cuenta"] ?? "(null)"));
Console.WriteLine("Alias: " + (builder.Configuration["Alias"] ?? "(null)"));
Console.WriteLine("CVU: " + (builder.Configuration["CVU"] ?? "(null)"));
Console.WriteLine("Token OK?: " + (!string.IsNullOrWhiteSpace(builder.Configuration["MercadoPago:AccessToken"])));
Console.WriteLine("========================================");

// ================= AUTH (BASIC) =================
// Config esperada (por appsettings o variables de entorno):
// Auth:Users: [ {"User":"admin","Pass":"clave","Role":"Admin"}, {"User":"banfield","Pass":"...","Role":"Sucursal"} ]
// En variables de entorno:
// Auth__Users__0__User, Auth__Users__0__Pass, Auth__Users__0__Role, etc.

var users = (builder.Configuration.GetSection("Auth:Users").Get<List<AuthUser>>() ?? new List<AuthUser>())
    .Where(u => !string.IsNullOrWhiteSpace(u.User) && !string.IsNullOrWhiteSpace(u.Pass))
    .ToList();

if (users.Count > 0)
{
    app.Use(async (ctx, next) =>
    {
        // Permitimos health sin auth (útil para monitoreo del host)
        if (ctx.Request.Path.StartsWithSegments("/health"))
        {
            await next();
            return;
        }

        if (TryGetBasicCredentials(ctx.Request.Headers.Authorization, out var user, out var pass))
        {
            var match = users.FirstOrDefault(u =>
                string.Equals(u.User, user, StringComparison.Ordinal) &&
                string.Equals(u.Pass, pass, StringComparison.Ordinal));

            if (match != null)
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.Name, match.User),
                    new(ClaimTypes.Role, string.IsNullOrWhiteSpace(match.Role) ? "Sucursal" : match.Role)
                };
                var identity = new ClaimsIdentity(claims, "Basic");
                ctx.User = new ClaimsPrincipal(identity);

                await next();
                return;
            }
        }

        ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"MP Transferencias\"";
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsync("Unauthorized");
    });
}

// ================= ENDPOINTS =================

app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.Now }));

// Endpoint JSON (por si lo usás desde otro lado)
app.MapGet("/api/transferencias", (ConcurrentQueue<object> q) => Results.Ok(q.ToArray()));

// UI HTML
app.MapGet("/", (ConcurrentQueue<object> q, IConfiguration cfg) =>
{
    var cuenta = cfg["Cuenta"] ?? "Cuenta";
    var alias = cfg["Alias"] ?? "";
    var cvu = cfg["CVU"] ?? "";

    var items = q.ToArray();
    var ar = CultureInfo.GetCultureInfo("es-AR");

    string RenderRow(object x)
    {
        var json = JsonSerializer.Serialize(x);
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;

        if (r.TryGetProperty("error", out var err))
        {
            var msg = err.GetString() ?? "Error";
            var cuentaErr = r.TryGetProperty("cuenta", out var cta) ? (cta.GetString() ?? cuenta) : cuenta;
            return "<tr><td colspan='5'><div class='errorBox'><b>" + cuentaErr + "</b> · " + msg + "</div></td></tr>";
        }

        var fecha = r.TryGetProperty("fecha", out var f) ? (f.GetString() ?? "") : "";
        var estado = r.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";
        var medio = r.TryGetProperty("payment_type_id", out var mt) ? (mt.GetString() ?? "") : "";
        var id = r.TryGetProperty("id", out var pid) ? pid.ToString() : "";

        string montoTxt = "";
        if (r.TryGetProperty("monto", out var m))
        {
            if (m.ValueKind == JsonValueKind.Number && m.TryGetDecimal(out var dec))
                montoTxt = dec.ToString("C", ar);
            else
                montoTxt = "$ " + m.ToString();
        }

        var pillClass = (estado == "approved" || estado == "accredited") ? "ok" : "bad";

        return
            "<tr>" +
            "<td class='mono'>" + fecha + "</td>" +
            "<td class='money'>" + montoTxt + "</td>" +
            "<td>" + medio + "</td>" +
            "<td><span class='pill " + pillClass + "'>" + estado + "</span></td>" +
            "<td class='muted mono'>" + id + "</td>" +
            "</tr>";
    }

    var rows = string.Join("", items.Reverse().Select(RenderRow));

    var showAccountBox = !(string.IsNullOrWhiteSpace(alias) && string.IsNullOrWhiteSpace(cvu));
    var accountBoxHtml = showAccountBox
        ? "<div class='accountBox'>"
          + (string.IsNullOrWhiteSpace(alias) ? "" : ("<div class='kv'><div class='k'>Alias</div><div class='v mono'>" + alias + "</div></div>"))
          + (string.IsNullOrWhiteSpace(cvu) ? "" : ("<div class='kv'><div class='k'>CVU</div><div class='v mono'>" + cvu + "</div></div>"))
          + "</div>"
        : "";

    var htmlTemplate = @"
<!doctype html>
<html lang='es'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>MP Transferencias</title>
<meta http-equiv='refresh' content='8'>
<style>
:root{
  --bg:#0b1220; --card:#0f172a; --border:#1f2937;
  --muted:#94a3b8; --text:#e5e7eb; --accent:#22c55e; --danger:#ef4444;
}
*{box-sizing:border-box}
body{
  margin:0;
  font-family:system-ui,-apple-system,Segoe UI,Roboto,Arial,sans-serif;
  background:radial-gradient(1200px 600px at 20% -10%, rgba(34,197,94,.15), transparent),
             radial-gradient(900px 500px at 100% 0%, rgba(59,130,246,.12), transparent),
             var(--bg);
  color:var(--text);
}
.container{max-width:1200px;margin:18px auto;padding:0 14px 80px}
.card{
  background:linear-gradient(180deg, rgba(255,255,255,.03), transparent), var(--card);
  border:1px solid var(--border); border-radius:18px;
  box-shadow:0 10px 30px rgba(0,0,0,.25); overflow:hidden;
}
.header{
  padding:18px 18px 14px;
  background:linear-gradient(90deg, rgba(34,197,94,.16), transparent 60%);
  border-bottom:1px solid var(--border);
}
.titleRow{display:flex;align-items:baseline;justify-content:space-between;gap:12px;flex-wrap:wrap}
h1{margin:0;font-size:30px;letter-spacing:.3px;font-weight:900}
.sub{margin-top:6px;color:rgba(226,232,240,.9);font-size:14px;font-weight:600}

.accountBox{
  margin-top:14px;
  display:grid;
  grid-template-columns:1fr 1fr;
  gap:12px;
  padding:14px;
  background:rgba(255,255,255,.03);
  border:1px solid rgba(148,163,184,.18);
  border-radius:14px;
}
.kv{
  display:flex;
  flex-direction:column;
  gap:6px;
  padding:12px 14px;
  border-radius:12px;
  background:linear-gradient(180deg, rgba(34,197,94,.12), rgba(255,255,255,.02));
  border:1px solid rgba(34,197,94,.22);
}
.k{
  color:rgba(226,232,240,.85);
  font-size:12px;
  letter-spacing:.8px;
  text-transform:uppercase;
  font-weight:800;
}
.v{font-weight:900;font-size:18px;word-break:break-all}

.mono{font-family:ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace}
.tableWrap{overflow:auto}
table{width:100%;border-collapse:collapse;min-width:760px}
thead th{
  position:sticky;top:0;z-index:1;
  background:rgba(15,23,42,.95);backdrop-filter:blur(8px);
  border-bottom:1px solid var(--border)
}
th,td{padding:12px 14px;border-bottom:1px solid rgba(31,41,55,.85);font-size:14.5px;white-space:nowrap}
tbody tr:nth-child(odd){background:rgba(255,255,255,.015)}
th{text-align:left;color:#cbd5e1;font-weight:700}
.money{font-weight:900}
.pill{
  display:inline-flex;padding:4px 10px;border-radius:999px;
  font-size:12px;font-weight:800;border:1px solid rgba(255,255,255,.08)
}
.ok{background:rgba(34,197,94,.18);color:#bbf7d0}
.bad{background:rgba(239,68,68,.18);color:#fecaca}
.muted{color:var(--muted)}
.errorBox{
  padding:12px 14px;border-radius:12px;
  border:1px solid rgba(239,68,68,.35);
  background:rgba(239,68,68,.08);color:#fecaca;
  font-size:14px;
}
.footer{
  position:fixed;bottom:0;left:0;right:0;padding:12px 14px;text-align:center;
  color:rgba(148,163,184,.85);font-size:12px;
  background:rgba(11,18,32,.75);backdrop-filter:blur(8px);
  border-top:1px solid rgba(31,41,55,.7)
}
.brand{color:#e5e7eb;font-weight:800}
@media (max-width: 700px){
  h1{font-size:24px}
  .accountBox{grid-template-columns:1fr}
  .v{font-size:16px}
}
</style>
</head>
<body>
<div class='container'>
  <div class='card'>
    <div class='header'>
      <div class='titleRow'>
        <h1>MP Transferencias · %%CUENTA%%</h1>
        <div class='sub'>Últimos <b>5 minutos</b> · Actualiza cada 8 segundos</div>
      </div>
      %%ACCOUNT_BOX%%
    </div>

    <div class='tableWrap'>
      <table>
        <thead>
          <tr>
            <th>Fecha</th>
            <th>Monto</th>
            <th>Medio</th>
            <th>Estado</th>
            <th class='muted'>ID</th>
          </tr>
        </thead>
        <tbody>
          %%ROWS%%
        </tbody>
      </table>
    </div>
  </div>
</div>

<div class='footer'>By <span class='brand'>PS3 Larroque</span></div>
</body>
</html>
";

    var html = htmlTemplate
        .Replace("%%CUENTA%%", cuenta)
        .Replace("%%ACCOUNT_BOX%%", accountBoxHtml)
        .Replace("%%ROWS%%", rows);

    return Results.Content(html, "text/html; charset=utf-8");
});

app.Run();

// ================= HELPERS =================

static Dictionary<string, AuthUser> LoadAuthUsers(IConfiguration cfg)
{
    // Ejemplo esperado en JSON:
    // "Auth": { "Users": [ { "User":"admin", "Pass":"123", "Role":"admin" } ] }
    var list = cfg.GetSection("Auth:Users").Get<List<AuthUser>>() ?? new List<AuthUser>();
    var dict = new Dictionary<string, AuthUser>(StringComparer.OrdinalIgnoreCase);
    foreach (var u in list)
    {
        if (string.IsNullOrWhiteSpace(u.User) || string.IsNullOrWhiteSpace(u.Pass)) continue;
        dict[u.User] = u;
    }
    return dict;
}

sealed class AuthUser
{
    public string User { get; set; } = "";
    public string Pass { get; set; } = "";
    public string? Role { get; set; }
}

// ================= POLLING SERVICE =================

class MpPollingService : BackgroundService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ConcurrentQueue<object> _queue;

    public MpPollingService(IHttpClientFactory http, IConfiguration cfg, ConcurrentQueue<object> queue)
    {
        _http = http;
        _cfg = cfg;
        _queue = queue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = _cfg["MercadoPago:AccessToken"];
        var cuenta = _cfg["Cuenta"] ?? "Cuenta";

        if (string.IsNullOrWhiteSpace(token))
        {
            _queue.Enqueue(new { cuenta, error = "FALTA AccessToken (no se leyó config)" });
            return;
        }

        var tzAr = GetArgentinaTimeZone();

        var seconds = int.TryParse(_cfg["Polling:Seconds"], out var s) ? s : 10;
        DateTimeOffset? lastSeenUtc = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = _http.CreateClient("MP");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var resp = await client.GetAsync("v1/payments/search?sort=date_created&criteria=desc&limit=20", stoppingToken);
                var raw = await resp.Content.ReadAsStringAsync(stoppingToken);

                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("results", out var results)) continue;

                // ✅ Filtro en UTC
                var ahoraUtc = DateTimeOffset.UtcNow;
                var hace5Utc = ahoraUtc.AddMinutes(-5);

                DateTimeOffset? maxCreatedUtc = null;

                foreach (var item in results.EnumerateArray())
                {
                    var dateStr = item.GetProperty("date_created").GetString();
                    if (!DateTimeOffset.TryParse(dateStr, out var created)) continue;

                    var createdUtc = created.ToUniversalTime();

                    // SOLO últimos 5 minutos
                    if (createdUtc < hace5Utc) continue;

                    // SOLO transferencias
                    var paymentType = item.GetProperty("payment_type_id").GetString();
                    if (paymentType != "account_money" && paymentType != "bank_transfer") continue;

                    if (maxCreatedUtc == null || createdUtc > maxCreatedUtc) maxCreatedUtc = createdUtc;
                    if (lastSeenUtc != null && createdUtc <= lastSeenUtc) continue;

                    // ✅ Mostrar hora Argentina
                    var createdAr = TimeZoneInfo.ConvertTime(createdUtc, tzAr);

                    _queue.Enqueue(new
                    {
                        cuenta,
                        id = item.GetProperty("id").ToString(),
                        fecha = createdAr.ToString("yyyy-MM-dd HH:mm:ss"),
                        monto = item.TryGetProperty("transaction_amount", out var ta) ? ta.GetDecimal() : 0,
                        status = item.GetProperty("status").GetString(),
                        payment_type_id = paymentType,
                        payment_method_id = item.TryGetProperty("payment_method_id", out var pm) ? pm.GetString() : ""
                    });

                    while (_queue.Count > 200) _queue.TryDequeue(out _);
                }

                if (maxCreatedUtc != null) lastSeenUtc = maxCreatedUtc;
            }
            catch (Exception ex)
            {
                _queue.Enqueue(new { cuenta, error = ex.Message });
            }

            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    // ✅ Dentro de la clase (evita CS8801) y compatible Windows/Linux
    private static TimeZoneInfo GetArgentinaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Argentina Standard Time"); }
        catch { }

        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { }

        return TimeZoneInfo.Local;
    }
    
}
static bool TryGetBasicCredentials(string authHeader, out string user, out string pass)
{
    user = string.Empty;
    pass = string.Empty;

    if (string.IsNullOrWhiteSpace(authHeader))
        return false;

    if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        return false;

    try
    {
        var encoded = authHeader.Substring("Basic ".Length).Trim();
        var decodedBytes = Convert.FromBase64String(encoded);
        var decoded = System.Text.Encoding.UTF8.GetString(decodedBytes);

        var parts = decoded.Split(':', 2);
        if (parts.Length != 2)
            return false;

        user = parts[0];
        pass = parts[1];
        return true;
    }
    catch
    {
        return false;
    }
}
