using System.Security.Claims;
using System.Text;
using Npgsql;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// ================= HOST =================
var port = Environment.GetEnvironmentVariable("PORT") ?? "5286";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ================= CONFIG =================
builder.Configuration.AddEnvironmentVariables();

// ================= SERVICES =================
builder.Services.AddControllers();
builder.Services.AddAuthorization();

// 🔹 HttpClient para MpPollingService
builder.Services.AddHttpClient();

// 🔹 IMPORTANTE: si el polling falla, NO tirar abajo la app
builder.Services.Configure<HostOptions>(opts =>
{
    opts.BackgroundServiceExceptionBehavior =
        BackgroundServiceExceptionBehavior.Ignore;
});

builder.Services.AddHostedService<MpPollingService>();

// ================= HELPERS =================
static bool TryGetBasicCredentials(string authHeader, out string user, out string pass)
{
    user = ""; pass = "";
    if (string.IsNullOrWhiteSpace(authHeader)) return false;
    if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;

    try
    {
        var decoded = Encoding.UTF8.GetString(
            Convert.FromBase64String(authHeader["Basic ".Length..])
        );
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
    var cs = cfg.GetConnectionString("Db") ?? cfg["DATABASE_URL"];
    if (string.IsNullOrWhiteSpace(cs))
        throw new Exception("NO_CONN_STRING");

    cs = cs.Trim().Trim('"');

    if (cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        cs.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(cs);
        var userInfo = uri.UserInfo.Split(':', 2);

        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
            Database = uri.AbsolutePath.TrimStart('/'),
            SslMode = SslMode.Require,
            Timeout = 10,
            CommandTimeout = 10
        }.ConnectionString;
    }

    return new NpgsqlConnectionStringBuilder(cs)
    {
        SslMode = SslMode.Require,
        Timeout = 10,
        CommandTimeout = 10
    }.ConnectionString;
}

static async Task<(bool ok, string? role, string reason)> ValidateUserAsync(
    IConfiguration cfg, string username, string password)
{
    try
    {
        var cs = NormalizeConnString(cfg);
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        var cmd = new NpgsqlCommand("""
            select role, password_hash
            from app_users
            where username = @u
            limit 1
        """, conn);

        cmd.Parameters.AddWithValue("u", username);

        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
            return (false, null, "Usuario inexistente");

        var role = rd.IsDBNull(0) ? null : rd.GetString(0);
        var hash = rd.GetString(1);

        return BCrypt.Net.BCrypt.Verify(password, hash)
            ? (true, role, "OK")
            : (false, role, "Clave incorrecta");
    }
    catch (Exception ex)
    {
        return (false, null, "DB_ERROR: " + ex.Message);
    }
}

static async Task Json401(HttpContext ctx, string msg)
{
    ctx.Response.StatusCode = 401;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync($$"""{"ok":false,"message":"{{msg}}"}""");
}

// ================= APP =================
var app = builder.Build();

app.UseRouting();

// ================= AUTH MANUAL (ÚNICO SISTEMA) =================
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    var authHeader = ctx.Request.Headers.Authorization.ToString();

    if (!TryGetBasicCredentials(authHeader, out var u, out var p))
    {
        await Json401(ctx, "Falta Authorization");
        return;
    }

    var (ok, role, reason) = await ValidateUserAsync(app.Configuration, u, p);
    if (!ok)
    {
        await Json401(ctx, reason);
        return;
    }

    ctx.User = new ClaimsPrincipal(
        new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, u),
            new Claim(ClaimTypes.Role, role ?? "sucursal")
        }, "Manual")
    );

    await next();
});

app.UseAuthorization();

app.MapControllers();

// ================= HOME + HTML =================
app.MapGet("/", (IConfiguration cfg) =>
{
    var cuenta = cfg["Sucursal"] ?? "Cuenta";

    var html = $$"""
<!doctype html>
<html lang="es">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>MP Transferencias · {cuenta}</title>
  <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet">
  <style>
    body { background:#0b1220; }
    .card { background:#0f172a; border:1px solid rgba(255,255,255,.08); }
    .muted { color:#94a3b8; }
    .mono { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace; }
    .table { --bs-table-bg: transparent; }
    .table td, .table th { color:#e5e7eb; border-color: rgba(255,255,255,.08); }
    .badge-soft { background: rgba(34,197,94,.15); color:#22c55e; border:1px solid rgba(34,197,94,.35); }
    .badge-soft2{ background: rgba(59,130,246,.15); color:#60a5fa; border:1px solid rgba(59,130,246,.35); }
  </style>
</head>
<body class="text-light">

<nav class="navbar navbar-dark" style="background:#0f172a; border-bottom:1px solid rgba(255,255,255,.08)">
  <div class="container-fluid">
    <span class="navbar-brand mb-0 h1">MP Transferencias · <span class="muted">{cuenta}</span></span>
    <div class="d-flex gap-2">
      <button class="btn btn-outline-light btn-sm" onclick="refreshAll()">Actualizar</button>
      <button class="btn btn-outline-warning btn-sm" onclick="logout()">Salir</button>
    </div>
  </div>
</nav>

<div class="container py-4">
  <div class="row g-3">
    <div class="col-lg-4">
      <div class="card p-3">
        <div class="d-flex align-items-center justify-content-between mb-2">
          <h5 class="mb-0">Mi cuenta</h5>
          <span id="accActive" class="badge badge-soft2">—</span>
        </div>
        <div class="muted small mb-2">Asignación de cuenta Mercado Pago</div>
        <div class="mono small" id="meBox">Cargando…</div>
      </div>

      <div class="card p-3 mt-3">
        <h5 class="mb-2">Aceptadas hoy</h5>
        <div class="d-flex justify-content-between">
          <div class="muted">Cantidad</div>
          <div class="mono" id="accCount">—</div>
        </div>
        <div class="d-flex justify-content-between">
          <div class="muted">Total</div>
          <div class="mono" id="accTotal">—</div>
        </div>
        <div class="small muted mt-2">Por usuario logueado.</div>
      </div>
    </div>

    <div class="col-lg-8">
      <div class="card p-3">
        <div class="d-flex align-items-center justify-content-between">
          <h5 class="mb-0">Pendientes (últimos 20)</h5>
          <span class="badge badge-soft" id="pendingCount">—</span>
        </div>
        <div class="table-responsive mt-3">
          <table class="table table-sm align-middle">
            <thead>
              <tr>
                <th>Fecha (UTC)</th>
                <th>Payment</th>
                <th class="text-end">Monto</th>
                <th>Tipo</th>
                <th>Estado</th>
              </tr>
            </thead>
            <tbody id="pendingRows"></tbody>
          </table>
        </div>
      </div>

      <div class="card p-3 mt-3">
        <h5 class="mb-0">Historial aceptadas hoy</h5>
        <div class="table-responsive mt-3">
          <table class="table table-sm align-middle">
            <thead>
              <tr>
                <th>Fecha (UTC)</th>
                <th>Payment</th>
                <th class="text-end">Monto</th>
                <th>Tipo</th>
                <th>Aceptado por</th>
              </tr>
            </thead>
            <tbody id="acceptedRows"></tbody>
          </table>
        </div>
      </div>

    </div>
  </div>
</div>

<!-- Login Modal -->
<div class="modal fade" id="loginModal" tabindex="-1" aria-hidden="true">
  <div class="modal-dialog">
    <div class="modal-content" style="background:#0f172a;border:1px solid rgba(255,255,255,.08)">
      <div class="modal-header">
        <h5 class="modal-title">Iniciar sesión</h5>
      </div>
      <div class="modal-body">
        <input id="u" class="form-control mb-2" placeholder="Usuario">
        <input id="p" class="form-control" type="password" placeholder="Contraseña">
        <div id="error" class="text-danger small mt-2"></div>
        <div class="muted small mt-2">Tip: usuario y contraseña según lo cargado en <span class="mono">app_users</span>.</div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-success" onclick="login()">Entrar</button>
      </div>
    </div>
  </div>
</div>

<script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js"></script>
<script>
let AUTH = sessionStorage.getItem('AUTH') || '';
let loginModal;

function b64(s){ return btoa(unescape(encodeURIComponent(s))); }

function moneyAR(n){
  try { return new Intl.NumberFormat('es-AR', { style:'currency', currency:'ARS' }).format(n); }
  catch { return '$' + n; }
}

async function api(url){
  const r = await fetch(url, { headers: AUTH ? {Authorization:AUTH}:{ } });
  if(r.status===401){
    AUTH='';
    sessionStorage.removeItem('AUTH');
    showLogin('Credenciales inválidas');
    throw '401';
  }
  if(!r.ok){
    const t = await r.text().catch(()=> '');
    throw (r.status + ' ' + t);
  }
  return r.json();
}

function showLogin(msg){
  document.getElementById('error').innerText = msg || '';
  if(!loginModal) loginModal = new bootstrap.Modal(document.getElementById('loginModal'), {backdrop:'static', keyboard:false});
  loginModal.show();
}

function logout(){
  AUTH='';
  sessionStorage.removeItem('AUTH');
  showLogin('');
}

async function login(){
  const u=document.getElementById('u').value.trim();
  const p=document.getElementById('p').value;
  AUTH='Basic '+b64(u+':'+p);
  sessionStorage.setItem('AUTH',AUTH);

  try{
    await api('/api/transfers/ping');
    loginModal.hide();
    refreshAll();
  }catch(e){
    showLogin('No pude loguear: ' + e);
  }
}

function fillPending(data){
  document.getElementById('pendingCount').innerText = data?.count ?? 0;
  const tbody = document.getElementById('pendingRows');
  tbody.innerHTML = '';
  (data?.items || []).forEach(x=>{
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td class="mono small">${x.fecha_utc || ''}</td>
      <td class="mono small">${x.payment_id || ''}</td>
      <td class="text-end mono">${moneyAR(x.monto || 0)}</td>
      <td class="small">${x.payment_type || ''}</td>
      <td class="small">${x.status || ''}</td>
    `;
    tbody.appendChild(tr);
  });
}

function fillAccepted(data){
  document.getElementById('accCount').innerText = data?.count ?? 0;
  document.getElementById('accTotal').innerText = moneyAR(data?.total ?? 0);

  const tbody = document.getElementById('acceptedRows');
  tbody.innerHTML = '';
  (data?.items || []).forEach(x=>{
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td class="mono small">${x.fecha_utc || ''}</td>
      <td class="mono small">${x.payment_id || ''}</td>
      <td class="text-end mono">${moneyAR(x.monto || 0)}</td>
      <td class="small">${x.payment_type || ''}</td>
      <td class="mono small">${x.accepted_by || ''}</td>
    `;
    tbody.appendChild(tr);
  });
}

async function refreshAll(){
  try{
    const me = await api('/api/transfers/me/account');
    document.getElementById('meBox').innerText = JSON.stringify(me, null, 2);
    document.getElementById('accActive').innerText = me?.activa ? 'Activa' : 'Inactiva';
  }catch(e){
    document.getElementById('meBox').innerText = 'Error: ' + e;
  }

  try{
    const p = await api('/api/transfers/pending?limit=20');
    fillPending(p);
  }catch(e){
    document.getElementById('pendingRows').innerHTML = `<tr><td colspan="5" class="text-danger small">Error: ${e}</td></tr>`;
    document.getElementById('pendingCount').innerText = '—';
  }

  try{
    const a = await api('/api/transfers/accepted/today');
    fillAccepted(a);
  }catch(e){
    document.getElementById('acceptedRows').innerHTML = `<tr><td colspan="5" class="text-danger small">Error: ${e}</td></tr>`;
    document.getElementById('accCount').innerText = '—';
    document.getElementById('accTotal').innerText = '—';
  }
}

(async function init(){
  if(AUTH){
    try{
      await api('/api/transfers/ping');
      refreshAll();
      return;
    }catch(e){}
  }
  showLogin('');
})();
</script>
</body>
</html>
""";


    return Results.Content(html, "text/html; charset=utf-8");
});

app.Run();
