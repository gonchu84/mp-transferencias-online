using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ================= ONLINE HOSTING (Render/Docker/VPS) =================
var port = Environment.GetEnvironmentVariable("PORT") ?? "5286";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ================= CONFIG =================
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
builder.Services.AddControllers();
builder.Services.AddAuthorization();

builder.Services.AddHttpClient("MP", c =>
{
    c.BaseAddress = new Uri("https://api.mercadopago.com/");
});

// Polling
builder.Services.AddHostedService<MpPollingService>();

// ================= HELPERS =================
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

// 🔧 Normaliza ConnectionString (Render postgres:// o DATABASE_URL)
static string NormalizeConnString(IConfiguration cfg)
{
    var cs = cfg.GetConnectionString("Db") ?? "";

    if (string.IsNullOrWhiteSpace(cs))
        cs = cfg["DATABASE_URL"] ?? "";

    if (string.IsNullOrWhiteSpace(cs))
        throw new Exception("No se encontró ConnectionStrings:Db ni DATABASE_URL");

    cs = cs.Trim().Trim('"');

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
            SslMode = SslMode.Require
        };

        return b.ConnectionString;
    }

    return cs;
}

// ✅ Valida usuario/clave contra DB usando BCrypt
static async Task<(bool ok, string? role)> ValidateUserFromDbAsync(
    IConfiguration cfg,
    string username,
    string password)
{
    var cs = NormalizeConnString(cfg);

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
        return (false, null);

    var role = rd.IsDBNull(0) ? null : rd.GetString(0);
    var hash = rd.IsDBNull(1) ? "" : rd.GetString(1);

    if (string.IsNullOrWhiteSpace(hash))
        return (false, role);

    // BCrypt.Net-Next
    var ok = BCrypt.Net.BCrypt.Verify(password, hash);
    return (ok, role);
}

var app = builder.Build();

// ================= DB INIT =================
await DbBootstrap.EnsureCreatedAsync(app.Configuration);

try
{
    var cs = NormalizeConnString(app.Configuration);
    await DbSeed.SeedAsync(cs);
}
catch (Exception ex)
{
    Console.WriteLine("⚠ DbSeed falló: " + ex.Message);
}

// ================= AUTH (Basic) - AHORA POR DB =================
app.Use(async (ctx, next) =>
{
    // permitir health sin auth
    if (ctx.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    var authHeader = ctx.Request.Headers["Authorization"].ToString();

    if (!TryGetBasicCredentials(authHeader, out var user, out var pass))
    {
        ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"MP Transferencias\"";
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsync("Unauthorized");
        return;
    }

    try
    {
        var (ok, role) = await ValidateUserFromDbAsync(app.Configuration, user, pass);

        if (!ok)
        {
            ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"MP Transferencias\"";
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("Unauthorized");
            return;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user),
            new(ClaimTypes.Role, string.IsNullOrWhiteSpace(role) ? "sucursal" : role!)
        };

        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Basic"));
        await next();
    }
    catch (Exception ex)
    {
        Console.WriteLine("⚠ Auth DB error: " + ex.Message);
        ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"MP Transferencias\"";
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsync("Unauthorized");
    }
});

app.UseAuthorization();

// ================= LOG =================
Console.WriteLine("========================================");
Console.WriteLine("MPTransferenciasLocal iniciado");
Console.WriteLine("ContentRootPath: " + builder.Environment.ContentRootPath);
Console.WriteLine("Config usado: " + (configUsado ?? "(ninguno)"));
Console.WriteLine("Cuenta: " + (builder.Configuration["Cuenta"] ?? "(null)"));
Console.WriteLine("Alias: " + (builder.Configuration["Alias"] ?? "(null)"));
Console.WriteLine("CVU: " + (builder.Configuration["CVU"] ?? "(null)"));
Console.WriteLine("Token OK?: " + (!string.IsNullOrWhiteSpace(builder.Configuration["MercadoPago:AccessToken"])));
Console.WriteLine("Auth: DB (app_users + BCrypt)");
Console.WriteLine("========================================");

// ================= ENDPOINTS =================
app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.Now }));

app.MapControllers();

// ✅ HOME (Pendientes + Aceptadas hoy)
// ✅ HOME (Pendientes + Aceptadas hoy + (ADMIN) Aceptadas por día)
app.MapGet("/", (HttpContext ctx, IConfiguration cfg) =>
{
    var cuenta = cfg["Sucursal"] ?? cfg["Cuenta"] ?? "Cuenta";
    var alias = cfg["Alias"] ?? "";
    var cvu = cfg["CVU"] ?? "";

    var isAdmin = ctx.User?.IsInRole("admin") == true;

    var showAccountBox = !(string.IsNullOrWhiteSpace(alias) && string.IsNullOrWhiteSpace(cvu));
    var accountBoxHtml = showAccountBox
        ? "<div class='accountBox'>"
          + (string.IsNullOrWhiteSpace(alias) ? "" : ("<div class='kv'><div class='k'>Alias</div><div class='v mono'>" + alias + "</div></div>"))
          + (string.IsNullOrWhiteSpace(cvu) ? "" : ("<div class='kv'><div class='k'>CVU</div><div class='v mono'>" + cvu + "</div></div>"))
          + "</div>"
        : "";

    // Bloque ADMIN (HTML) - aparece solo si role=admin
    var adminHtml = isAdmin ? """
    <div class='sectionTitle adminTitle'>
      <h2>ADMIN · Aceptadas por día (todas)</h2>
      <div class='badge'>Total: <b id='adminTotal'>$0</b> · Cant: <b id='adminCant'>0</b></div>
    </div>

    <div class='adminBar'>
      <label class='adminLbl'>Día</label>
      <input id='adminDate' type='date' class='adminInput' />
      <button class='btn' onclick='loadAdminAcceptedByDay()'>Buscar</button>
      <span class='muted' id='adminHint'></span>
    </div>

    <div class='tableWrap'>
      <table>
        <thead>
          <tr>
            <th>Hora (AR)</th>
            <th>Monto</th>
            <th>Medio</th>
            <th>Estado</th>
            <th>Aceptada por</th>
            <th class='muted'>Payment ID</th>
          </tr>
        </thead>
        <tbody id='adminAcceptedBody'>
          <tr><td colspan='6'><div class='errorBox'>Elegí un día para ver las aceptadas (todas).</div></td></tr>
        </tbody>
      </table>
    </div>
    """ : "";

    // ✅ IMPORTANTE: usamos $$""" ... """ para que NO se rompa con { } del JS
    var html = $$"""
<!doctype html>
<html lang='es'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>MP Transferencias</title>
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
.mono{font-family:ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace}
.accountBox{
  margin-top:14px; display:grid; grid-template-columns:1fr 1fr; gap:12px;
  padding:14px; background:rgba(255,255,255,.03);
  border:1px solid rgba(148,163,184,.18); border-radius:14px;
}
.kv{
  display:flex;flex-direction:column;gap:6px;
  padding:12px 14px;border-radius:12px;
  background:linear-gradient(180deg, rgba(34,197,94,.12), rgba(255,255,255,.02));
  border:1px solid rgba(34,197,94,.22);
}
.k{color:rgba(226,232,240,.85);font-size:12px;letter-spacing:.8px;text-transform:uppercase;font-weight:800}
.v{font-weight:900;font-size:18px;word-break:break-all}
.sectionTitle{
  display:flex;justify-content:space-between;align-items:center;
  padding:14px 18px;border-top:1px solid var(--border);
  background:rgba(255,255,255,.02);
}
.sectionTitle h2{margin:0;font-size:16px;font-weight:900;letter-spacing:.2px}
.badge{
  display:inline-flex;align-items:center;gap:8px;
  padding:6px 10px;border-radius:999px;
  border:1px solid rgba(255,255,255,.08);
  color:#cbd5e1;background:rgba(255,255,255,.03);
  font-size:12px;font-weight:800;
}
.badge b{color:#fff}
.tableWrap{overflow:auto}
table{width:100%;border-collapse:collapse;min-width:860px}
thead th{position:sticky;top:0;z-index:1;background:rgba(15,23,42,.95);backdrop-filter:blur(8px);border-bottom:1px solid var(--border)}
th,td{padding:12px 14px;border-bottom:1px solid rgba(31,41,55,.85);font-size:14.5px;white-space:nowrap}
tbody tr:nth-child(odd){background:rgba(255,255,255,.015)}
th{text-align:left;color:#cbd5e1;font-weight:700}
.money{font-weight:900}
.pill{display:inline-flex;padding:4px 10px;border-radius:999px;font-size:12px;font-weight:800;border:1px solid rgba(255,255,255,.08)}
.ok{background:rgba(34,197,94,.18);color:#bbf7d0}
.bad{background:rgba(239,68,68,.18);color:#fecaca}
.muted{color:var(--muted)}
.btn{
  cursor:pointer; padding:8px 12px; border-radius:12px;
  border:1px solid rgba(34,197,94,.35);
  background:rgba(34,197,94,.12);
  color:#bbf7d0;font-weight:900;
}
.btn:disabled{opacity:.5;cursor:not-allowed}
.btnDanger{
  border:1px solid rgba(239,68,68,.35);
  background:rgba(239,68,68,.10);
  color:#fecaca;
}
.errorBox{
  padding:12px 14px;border-radius:12px;
  border:1px solid rgba(239,68,68,.35);
  background:rgba(239,68,68,.08);color:#fecaca;font-size:14px;
}
.footer{
  position:fixed;bottom:0;left:0;right:0;padding:12px 14px;text-align:center;
  color:rgba(148,163,184,.85);font-size:12px;
  background:rgba(11,18,32,.75);backdrop-filter:blur(8px);
  border-top:1px solid rgba(31,41,55,.7)
}
.brand{color:#e5e7eb;font-weight:800}

/* ADMIN */
.adminTitle{
  background:linear-gradient(90deg, rgba(59,130,246,.18), transparent 60%);
}
.adminBar{
  display:flex; align-items:center; gap:10px; flex-wrap:wrap;
  padding:12px 18px;
  border-top:1px solid var(--border);
  background:rgba(255,255,255,.015);
}
.adminLbl{font-weight:800;color:#cbd5e1;font-size:13px}
.adminInput{
  background:rgba(255,255,255,.03);
  border:1px solid rgba(148,163,184,.25);
  color:#e5e7eb;
  padding:8px 10px;
  border-radius:12px;
  font-weight:800;
}

@media (max-width: 700px){
  h1{font-size:24px}
  .accountBox{grid-template-columns:1fr}
  .v{font-size:16px}
  table{min-width:740px}
}
</style>
</head>
<body>
<div class='container'>
  <div class='card'>
    <div class='header'>
      <div class='titleRow'>
        <h1>MP Transferencias · {{cuenta}}</h1>
        <div class='sub'>Pendientes (últimas <b>20</b>) · Actualiza cada <b>8</b> segundos</div>
      </div>
      {{accountBoxHtml}}
    </div>

    <div class='sectionTitle'>
      <h2>Pendientes</h2>
      <div class='badge'>Actualizando…</div>
    </div>

    <div class='tableWrap'>
      <table>
        <thead>
          <tr>
            <th>✅</th>
            <th>Fecha (AR)</th>
            <th>Monto</th>
            <th>Medio</th>
            <th>Estado</th>
            <th class='muted'>Payment ID</th>
          </tr>
        </thead>
        <tbody id='pendingBody'>
          <tr><td colspan='6'><div class='errorBox'>Cargando…</div></td></tr>
        </tbody>
      </table>
    </div>

    <div class='sectionTitle'>
      <h2>Aceptadas hoy (por vos)</h2>
      <div class='badge'>Total hoy: <b id='totalHoy'>$0</b> · Cant: <b id='cantHoy'>0</b></div>
    </div>

    <div class='tableWrap'>
      <table>
        <thead>
          <tr>
            <th>Hora (AR)</th>
            <th>Monto</th>
            <th>Medio</th>
            <th>Estado</th>
            <th class='muted'>Payment ID</th>
          </tr>
        </thead>
        <tbody id='acceptedBody'>
          <tr><td colspan='5'><div class='errorBox'>Cargando…</div></td></tr>
        </tbody>
      </table>
    </div>

    {{adminHtml}}

  </div>
</div>

<div class='footer'>By <span class='brand'>PS3 Larroque</span></div>

<script>
const arMoney = new Intl.NumberFormat('es-AR', { style:'currency', currency:'ARS' });

function pillClass(status) {
  if (!status) return 'bad';
  const s = status.toLowerCase();
  return (s === 'approved' || s === 'accredited') ? 'ok' : 'bad';
}

function toArDate(iso) {
  try {
    const d = new Date(iso);
    const pad = (n)=> String(n).padStart(2,'0');
    return pad(d.getDate()) + '/' + pad(d.getMonth()+1) + ' ' +
           pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
  } catch {
    return iso;
  }
}

async function loadPending() {
  const body = document.getElementById('pendingBody');
  try {
    const r = await fetch('/api/transfers/pending?limit=20');
    const j = await r.json();

    if (!r.ok || !j.ok) throw new Error(j?.message || 'Error pending');

    if (!j.items || j.items.length === 0) {
      body.innerHTML = `<tr><td colspan='6'><div class='errorBox'>No hay transferencias pendientes.</div></td></tr>`;
      return;
    }

    body.innerHTML = j.items.map(x => `
      <tr data-id='${x.id}'>
        <td><button class='btn' onclick='ackTransfer(${x.id}, this)'>Aceptar</button></td>
        <td class='mono'>${toArDate(x.fecha_utc)}</td>
        <td class='money'>${arMoney.format(x.monto)}</td>
        <td>${x.payment_type}</td>
        <td><span class='pill ${pillClass(x.status)}'>${x.status}</span></td>
        <td class='muted mono'>${x.payment_id}</td>
      </tr>
    `).join('');
  } catch (e) {
    body.innerHTML = `<tr><td colspan='6'><div class='errorBox'>Error cargando pendientes: ${e.message}</div></td></tr>`;
  }
}

async function loadAcceptedToday() {
  const body = document.getElementById('acceptedBody');
  const total = document.getElementById('totalHoy');
  const cant = document.getElementById('cantHoy');

  try {
    const r = await fetch('/api/transfers/accepted/today');
    const j = await r.json();

    if (!r.ok || !j.ok) throw new Error(j?.message || 'Error accepted/today');

    total.textContent = arMoney.format(j.total || 0);
    cant.textContent = (j.count || 0);

    if (!j.items || j.items.length === 0) {
      body.innerHTML = `<tr><td colspan='5'><div class='errorBox'>Todavía no aceptaste transferencias hoy.</div></td></tr>`;
      return;
    }

    body.innerHTML = j.items.map(x => `
      <tr>
        <td class='mono'>${toArDate(x.fecha_utc)}</td>
        <td class='money'>${arMoney.format(x.monto)}</td>
        <td>${x.payment_type}</td>
        <td><span class='pill ${pillClass(x.status)}'>${x.status}</span></td>
        <td class='muted mono'>${x.payment_id}</td>
      </tr>
    `).join('');
  } catch (e) {
    body.innerHTML = `<tr><td colspan='5'><div class='errorBox'>Error cargando aceptadas: ${e.message}</div></td></tr>`;
    total.textContent = '$0';
    cant.textContent = '0';
  }
}

async function ackTransfer(id, btn) {
  try {
    btn.disabled = true;
    const r = await fetch(`/api/transfers/${id}/ack`, { method: 'POST' });

    if (r.status === 409) {
      btn.classList.add('btnDanger');
      btn.textContent = 'Ya tomada';
      await refreshAll();
      return;
    }

    if (!r.ok) {
      const t = await r.text();
      throw new Error(t || 'Error aceptando');
    }

    await refreshAll();
  } catch (e) {
    btn.disabled = false;
    alert('Error al aceptar: ' + e.message);
  }
}

// ===== ADMIN =====
async function loadAdminAcceptedByDay() {
  const input = document.getElementById('adminDate');
  const body = document.getElementById('adminAcceptedBody');
  const total = document.getElementById('adminTotal');
  const cant = document.getElementById('adminCant');
  const hint = document.getElementById('adminHint');

  if (!input || !body) return; // no es admin (no existe el bloque)

  const date = input.value;
  if (!date) {
    hint.textContent = 'Elegí una fecha.';
    return;
  }
  hint.textContent = '';

  try {
    body.innerHTML = `<tr><td colspan='6'><div class='errorBox'>Cargando…</div></td></tr>`;
    const r = await fetch(`/api/transfers/admin/accepted/by-day?date=${encodeURIComponent(date)}`);
    const j = await r.json();

    if (!r.ok || !j.ok) throw new Error(j?.message || 'Error admin accepted/by-day');

    total.textContent = arMoney.format(j.total || 0);
    cant.textContent = (j.count || 0);

    if (!j.items || j.items.length === 0) {
      body.innerHTML = `<tr><td colspan='6'><div class='errorBox'>No hay aceptadas para ese día.</div></td></tr>`;
      return;
    }

    body.innerHTML = j.items.map(x => `
      <tr>
        <td class='mono'>${toArDate(x.fecha_utc)}</td>
        <td class='money'>${arMoney.format(x.monto)}</td>
        <td>${x.payment_type}</td>
        <td><span class='pill ${pillClass(x.status)}'>${x.status}</span></td>
        <td class='mono'>${x.accepted_by || ''}</td>
        <td class='muted mono'>${x.payment_id}</td>
      </tr>
    `).join('');
  } catch (e) {
    body.innerHTML = `<tr><td colspan='6'><div class='errorBox'>Error cargando admin: ${e.message}</div></td></tr>`;
    total.textContent = '$0';
    cant.textContent = '0';
  }
}

function setAdminDefaultDateToday() {
  const input = document.getElementById('adminDate');
  if (!input) return;
  // Fecha local del navegador (AR si estás en AR)
  const now = new Date();
  const yyyy = now.getFullYear();
  const mm = String(now.getMonth()+1).padStart(2,'0');
  const dd = String(now.getDate()).padStart(2,'0');
  input.value = `${yyyy}-${mm}-${dd}`;
}

async function refreshAll() {
  await loadPending();
  await loadAcceptedToday();
}

refreshAll();
setInterval(refreshAll, 8000);

// si es admin, seteamos hoy por defecto y traemos el día automáticamente
setAdminDefaultDateToday();
loadAdminAcceptedByDay();
</script>

</body>
</html>
""";

    // inyectamos el bloque admin solo si corresponde
    html = html.Replace("{{adminHtml}}", adminHtml);

    return Results.Content(html, "text/html; charset=utf-8");
});


app.Run();
