using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// ================= ONLINE HOSTING (Render/Docker/VPS) =================
var port = Environment.GetEnvironmentVariable("PORT") ?? "5286";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ================= CONFIG =================
//
// OJO: CreateBuilder() YA agrega appsettings + env vars.
// Si volvemos a agregar JSON después, podemos pisar env vars.
// Entonces: agregamos SOLO el appsettings.Sucursal.json y
// volvemos a agregar EnvironmentVariables AL FINAL para que ganen.
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

// ✅ IMPORTANTE: asegurar que las variables de entorno pisan a los JSON
builder.Configuration.AddEnvironmentVariables();

// ================= SERVICES =================
builder.Services.AddHttpClient("MP", c =>
{
    c.BaseAddress = new Uri("https://api.mercadopago.com/");
});

builder.Services.AddSingleton<ConcurrentQueue<object>>();
builder.Services.AddHostedService<MpPollingService>();

// ================= HELPERS (TOP-LEVEL SAFE) =================
bool TryGetBasicCredentials(string authHeader, out string user, out string pass)
{
    user = string.Empty;
    pass = string.Empty;

    if (string.IsNullOrWhiteSpace(authHeader))
        return false;

    if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        return false;

    try
    {
        var encoded = authHeader["Basic ".Length..].Trim();
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
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



var app = builder.Build();

await DbBootstrap.EnsureCreatedAsync(app.Configuration);
await DbSeed.SeedAsync(app.Configuration.GetConnectionString("Db")!);

// ================= AUTH (Basic) =================
var authUsers = AuthHelpers.LoadAuthUsers(app.Configuration);


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

        // ✅ leer header de forma segura
        var authHeader = ctx.Request.Headers["Authorization"].ToString();

        if (TryGetBasicCredentials(authHeader, out var user, out var pass))
        {
            if (authUsers.TryGetValue(user, out var u) && u.Pass == pass)
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.Name, u.User),
                    new(ClaimTypes.Role, string.IsNullOrWhiteSpace(u.Role) ? "Sucursal" : u.Role!)
                };
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Basic"));

                await next();
                return;
            }
        }

        ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"MP Transferencias\"";
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsync("Unauthorized");
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
Console.WriteLine("Auth Users: " + authUsers.Count);
Console.WriteLine("Auth Usernames: " + (authUsers.Count == 0 ? "(none)" : string.Join(", ", authUsers.Keys)));
Console.WriteLine("========================================");

// ================= ENDPOINTS =================
app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.Now }));

app.MapGet("/api/transferencias", (ConcurrentQueue<object> q) => Results.Ok(q.ToArray()));

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

// ================= POLLING SERVICE =================
