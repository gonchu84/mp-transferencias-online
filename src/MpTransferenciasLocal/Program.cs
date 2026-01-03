using System.Security.Claims;
using System.Text;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ================= HOSTING =================
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

builder.Configuration.AddEnvironmentVariables();

// ================= SERVICES =================
builder.Services.AddControllers();
builder.Services.AddAuthorization();

builder.Services.AddHttpClient("MP", c =>
{
    c.BaseAddress = new Uri("https://api.mercadopago.com/");
});

builder.Services.AddHostedService<MpPollingService>();

// ================= HELPERS =================
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
    catch
    {
        return false;
    }
}

static string NormalizeConnString(IConfiguration cfg)
{
    var cs = cfg.GetConnectionString("Db") ?? "";

    if (string.IsNullOrWhiteSpace(cs))
        cs = cfg["DATABASE_URL"] ?? "";

    if (string.IsNullOrWhiteSpace(cs))
        throw new Exception("NO_CONN_STRING: No se encontró ConnectionStrings:Db ni DATABASE_URL");

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
            SslMode = SslMode.Require,
            Timeout = 10,
            CommandTimeout = 10
        };

        return b.ConnectionString;
    }

    // Si ya viene como connstring tradicional, forzamos SSL Require y timeouts
    var nb = new NpgsqlConnectionStringBuilder(cs)
    {
        SslMode = SslMode.Require,
        Timeout = 10,
        CommandTimeout = 10
    };

    return nb.ConnectionString;
}

static async Task<(bool ok, string? role, string? failReason)> ValidateUserFromDbAsync(
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

        // ✅ IMPORTANTE: valida por username (igual que tu controller)
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
            return (false, null, "NO_USER: No existe el usuario (username) en app_users.");

        var role = rd.IsDBNull(0) ? null : rd.GetString(0);
        var hash = rd.IsDBNull(1) ? "" : rd.GetString(1);

        if (string.IsNullOrWhiteSpace(hash))
            return (false, role, "EMPTY_HASH: El usuario existe pero password_hash está vacío.");

        var ok = BCrypt.Net.BCrypt.Verify(password, hash);
        return ok ? (true, role, null) : (false, role, "BAD_PASS: Clave incorrecta (BCrypt.Verify=false).");
    }
    catch (PostgresException pex) when (pex.SqlState == "42P01")
    {
        // tabla no existe
        return (false, null, "NO_TABLE: La tabla app_users no existe en esta base.");
    }
    catch (Exception ex)
    {
        return (false, null, $"DB_ERROR: {ex.GetType().Name} - {ex.Message}");
    }
}

static async Task Json401(HttpContext ctx, string message)
{
    ctx.Response.StatusCode = 401;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    await ctx.Response.WriteAsync($$"""{"ok":false,"message":"{{message}}"}""");
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

// ================= PIPELINE =================
app.UseRouting();

app.Use(async (ctx, next) =>
{
    var hasAuth = ctx.Request.Headers.ContainsKey("Authorization");
    Console.WriteLine($"REQ  {ctx.Request.Method} {ctx.Request.Path}  AuthHdr={(hasAuth ? "YES" : "NO")}");
    await next();
    Console.WriteLine($"RESP {ctx.Response.StatusCode} {ctx.Request.Method} {ctx.Request.Path}");
});

// ✅ Basic Auth SOLO para /api
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    var needsAuth = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase);

    if (!needsAuth)
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

app.MapGet("/favicon.ico", () => Results.NoContent());
app.MapGet("/apple-touch-icon.png", () => Results.NoContent());
app.MapGet("/site.webmanifest", () => Results.NoContent());

Console.WriteLine("========================================");
Console.WriteLine("MPTransferenciasOnline iniciado");
Console.WriteLine("ContentRootPath: " + builder.Environment.ContentRootPath);
Console.WriteLine("Config usado: " + (configUsado ?? "(ninguno)"));
Console.WriteLine("Auth: Basic + DB SOLO /api");
Console.WriteLine("========================================");

app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.Now }));
app.MapControllers();

// HOME sin auth (login lo maneja JS)
app.MapGet("/", (IConfiguration cfg) =>
{
    var cuenta = cfg["Sucursal"] ?? cfg["Cuenta"] ?? "Cuenta";

    var html = """
<!doctype html>
<html lang='es'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>MP Transferencias</title>
<style>
:root{ --bg:#0b1220; --card:#0f172a; --border:#1f2937; --muted:#94a3b8; --text:#e5e7eb; --danger:#ef4444; }
*{box-sizing:border-box}
body{ margin:0;font-family:system-ui,-apple-system,Segoe UI,Roboto,Arial,sans-serif;background:var(--bg);color:var(--text); }
.container{max-width:1200px;margin:18px auto;padding:0 14px 80px}
.card{ background:var(--card); border:1px solid var(--border); border-radius:18px; overflow:hidden; }
.header{ padding:18px;border-bottom:1px solid var(--border); }
h1{margin:0;font-size:28px;font-weight:900}
.muted{color:var(--muted)}
.btn{ cursor:pointer; padding:10px 14px; border-radius:12px; border:1px solid rgba(34,197,94,.35); background:rgba(34,197,94,.12); color:#bbf7d0;font-weight:900;}
.btnDanger{ border:1px solid rgba(239,68,68,.35); background:rgba(239,68,68,.16); color:#fecaca; }
.errorBox{ padding:12px 14px;border-radius:12px;border:1px solid rgba(239,68,68,.35); background:rgba(239,68,68,.10); color:#fecaca; }

/* ✅ LOGIN modal NO transparente */
#loginOverlay{ position:fixed; inset:0; display:none; align-items:center; justify-content:center; background:rgba(0,0,0,.82); z-index:9999; }
#loginCard{
  width:min(440px, calc(100% - 28px));
  background:#0f172a;
  border:1px solid rgba(148,163,184,.40);
  border-radius:16px;
  box-shadow:0 20px 60px rgba(0,0,0,.70);
  padding:18px;
}
.loginTitle{font-size:18px;font-weight:900;margin:0 0 6px}
.loginSub{color:rgba(226,232,240,.85);font-size:13px;margin:0 0 14px}
.loginRow{display:flex;flex-direction:column;gap:6px;margin-bottom:10px}
.loginRow label{font-weight:800;color:#cbd5e1;font-size:12px}
.loginRow input{ padding:10px 12px;border-radius:12px;border:1px solid rgba(148,163,184,.25);background:#0b1220;color:#e5e7eb;font-weight:800; }
.loginActions{display:flex;gap:10px;justify-content:flex-end;margin-top:10px}
</style>
</head>
<body>

<div id="loginOverlay">
  <div id="loginCard">
    <p class="loginTitle">Iniciar sesión</p>
    <p class="loginSub">Usá el <b>username</b> de la tabla <b>app_users</b> (no correo).</p>

    <div class="loginRow">
      <label>Usuario</label>
      <input id="loginUser" autocomplete="username" />
    </div>
    <div class="loginRow">
      <label>Contraseña</label>
      <input id="loginPass" type="password" autocomplete="current-password" />
    </div>

    <div id="loginErr" class="errorBox" style="display:none;margin-top:10px;"></div>

    <div class="loginActions">
      <button class="btn btnDanger" onclick="logout()">Cancelar</button>
      <button class="btn" onclick="doLogin()">Entrar</button>
    </div>
  </div>
</div>

<div class="container">
  <div class="card">
    <div class="header">
      <h1>MP Transferencias · __CUENTA__</h1>
      <div class="muted" id="status">Esperando login…</div>
      <div style="margin-top:12px;">
        <button class="btn" onclick="logout()">Cambiar usuario</button>
      </div>
    </div>

    <div style="padding:18px;">
      <div class="muted" style="margin-bottom:10px;">Debug rápido:</div>
      <pre id="debug" class="muted" style="white-space:pre-wrap;margin:0;"></pre>
    </div>
  </div>
</div>

<script>
const STORE = sessionStorage;
let AUTH = STORE.getItem('basicAuthHeader') || '';
let timer = null;

function stopTimer(){
  if(timer){ clearInterval(timer); timer = null; }
}

function showLogin(msg){
  const ov = document.getElementById('loginOverlay');
  const err = document.getElementById('loginErr');
  if(msg){
    err.style.display='block';
    err.textContent=msg;
  } else {
    err.style.display='none';
    err.textContent='';
  }
  ov.style.display='flex';
  setTimeout(()=>document.getElementById('loginUser')?.focus(), 50);
}

function hideLogin(){
  document.getElementById('loginOverlay').style.display='none';
}

function base64Utf8(str){
  const bytes = new TextEncoder().encode(str);
  let bin = '';
  bytes.forEach(b => bin += String.fromCharCode(b));
  return btoa(bin);
}

function buildBasic(u,p){
  return 'Basic ' + base64Utf8(u + ':' + p);
}

async function api(url, options={}){
  const headers = { ...(options.headers||{}), ...(AUTH ? { Authorization: AUTH } : {}) };
  const res = await fetch(url, { ...options, headers });

  if(res.status === 401){
    stopTimer();
    let msg = 'Credenciales inválidas.';
    try {
      const j = await res.json();
      if(j && j.message) msg = j.message;
    } catch {}
    AUTH='';
    STORE.removeItem('basicAuthHeader');
    showLogin(msg);
    throw new Error('401');
  }
  return res;
}

async function doLogin(){
  const u = (document.getElementById('loginUser').value||'').trim();
  const p = (document.getElementById('loginPass').value||'').trim();
  if(!u || !p) return showLogin('Completá usuario y contraseña.');

  AUTH = buildBasic(u,p);
  STORE.setItem('basicAuthHeader', AUTH);

  try{
    const r = await api('/api/transfers/ping');
    const j = await r.json().catch(()=> ({}));
    if(!r.ok || !j.ok) throw new Error(j.message || 'Login inválido');
    hideLogin();
    document.getElementById('status').textContent = 'Login OK. Actualizando...';
    startPolling();
  } catch(e){
    if(String(e.message||'').includes('Login inválido')) showLogin('Login inválido.');
  }
}

function logout(){
  stopTimer();
  AUTH='';
  STORE.removeItem('basicAuthHeader');
  document.getElementById('status').textContent = 'Esperando login…';
  showLogin('Ingresá tus credenciales.');
}

async function safeJson(res){
  try { return await res.json(); } catch { return null; }
}

async function tick(){
  const dbg = document.getElementById('debug');

  let accountNote = '';
  const accRes = await api('/api/transfers/me/account').catch(()=>null);
  if(accRes){
    if(accRes.status === 404){
      accountNote = '⚠ /me/account = 404 (Render NO está corriendo el controller actualizado)\\n\\n';
    } else {
      const accJ = await safeJson(accRes);
      accountNote = 'me/account: ' + JSON.stringify(accJ, null, 2) + '\\n\\n';
    }
  }

  const p = await api('/api/transfers/pending?limit=20');
  const pj = await safeJson(p);

  const a = await api('/api/transfers/accepted/today');
  const aj = await safeJson(a);

  dbg.textContent =
    accountNote +
    'pending: ' + JSON.stringify(pj, null, 2) + '\\n\\n' +
    'accepted/today: ' + JSON.stringify(aj, null, 2);
}

function startPolling(){
  stopTimer();
  tick();
  timer = setInterval(tick, 8000);
}

(async ()=>{
  if(!AUTH) return showLogin();
  try{
    const r = await api('/api/transfers/ping');
    const j = await r.json().catch(()=> ({}));
    if(!r.ok || !j.ok) throw new Error('bad');
    document.getElementById('status').textContent = 'Login OK. Actualizando...';
    startPolling();
  } catch {
    if(!AUTH) showLogin();
  }
})();
</script>

</body>
</html>
""";

    html = html.Replace("__CUENTA__", cuenta);
    return Results.Content(html, "text/html; charset=utf-8");
});

app.Run();
