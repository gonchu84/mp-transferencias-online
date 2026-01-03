using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Hosting;
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

// ✅ Env vars pisan JSON
builder.Configuration.AddEnvironmentVariables();

// ================= SERVICES =================
builder.Services.AddControllers();
builder.Services.AddAuthorization();

// ✅ NECESARIO para MpPollingService si usa IHttpClientFactory
builder.Services.AddHttpClient("MP", c =>
{
    c.BaseAddress = new Uri("https://api.mercadopago.com/");
});

// ✅ si el polling falla, NO tirar abajo la app
builder.Services.Configure<HostOptions>(opts =>
{
    opts.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
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
            SslMode = SslMode.Require,
            Timeout = 10,
            CommandTimeout = 10
        };

        return b.ConnectionString;
    }

    // si ya viene en formato Npgsql
    return new NpgsqlConnectionStringBuilder(cs)
    {
        SslMode = SslMode.Require,
        Timeout = 10,
        CommandTimeout = 10
    }.ConnectionString;
}

// ✅ Valida usuario/clave contra DB usando BCrypt
static async Task<(bool ok, string? role, string reason)> ValidateUserFromDbAsync(
    IConfiguration cfg,
    string username,
    string password)
{
    try
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
            return (false, null, "Usuario inexistente");

        var role = rd.IsDBNull(0) ? null : rd.GetString(0);
        var hash = rd.IsDBNull(1) ? "" : rd.GetString(1);

        if (string.IsNullOrWhiteSpace(hash))
            return (false, role, "Usuario sin clave");

        var ok = BCrypt.Net.BCrypt.Verify(password, hash);
        return ok ? (true, role, "OK") : (false, role, "Clave incorrecta");
    }
    catch (Exception ex)
    {
        return (false, null, "DB_ERROR: " + ex.Message);
    }
}

static async Task Json401(HttpContext ctx, string msg)
{
    // ⚠ IMPORTANTE: NO mandar WWW-Authenticate -> NO hay popup del navegador
    ctx.Response.StatusCode = 401;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    await ctx.Response.WriteAsync($$"""{"ok":false,"message":"{{msg}}"}""");
}

// ================= APP =================
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

// ✅ LOGS
app.Use(async (ctx, next) =>
{
    var hasAuth = ctx.Request.Headers.ContainsKey("Authorization");
    Console.WriteLine($"REQ  {ctx.Request.Method} {ctx.Request.Path}  AuthHdr={(hasAuth ? "YES" : "NO")}");
    await next();
    Console.WriteLine($"RESP {ctx.Response.StatusCode} {ctx.Request.Method} {ctx.Request.Path}");
});

// ✅ AUTH SOLO PARA /api (así la home NO dispara popup)
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var authHeader = ctx.Request.Headers["Authorization"].ToString();

    if (!TryGetBasicCredentials(authHeader, out var user, out var pass))
    {
        await Json401(ctx, "Falta Authorization");
        return;
    }

    var (ok, role, reason) = await ValidateUserFromDbAsync(app.Configuration, user, pass);
    if (!ok)
    {
        await Json401(ctx, reason);
        return;
    }

    ctx.User = new ClaimsPrincipal(
        new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, user),
            new Claim(ClaimTypes.Role, string.IsNullOrWhiteSpace(role) ? "sucursal" : role!)
        }, "ManualBasic")
    );

    await next();
});

app.UseAuthorization();

// ✅ Evita 404 de favicon (y evita requests raros)
app.MapGet("/favicon.ico", () => Results.NoContent());

// ================= LOG =================
Console.WriteLine("========================================");
Console.WriteLine("MPTransferenciasLocal iniciado");
Console.WriteLine("ContentRootPath: " + builder.Environment.ContentRootPath);
Console.WriteLine("Config usado: " + (configUsado ?? "(ninguno)"));
Console.WriteLine("Auth: UI login (modal) + Authorization header a /api");
Console.WriteLine("========================================");

// ================= ENDPOINTS =================
app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.Now }));

// ✅ IMPORTANTE: sin esto /api/transfers/* da 404
app.MapControllers();

// ================= HOME (Bootstrap como antes + funciones actuales) =================
app.MapGet("/", (IConfiguration cfg) =>
{
    var cuenta = cfg["Sucursal"] ?? cfg["Cuenta"] ?? "Cuenta";

    var html = $$"""
<!doctype html>
<html lang="es">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>MP Transferencias · {{cuenta}}</title>
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
    .btn-soft-danger{ background: rgba(239,68,68,.12); border:1px solid rgba(239,68,68,.35); color:#fecaca; }
    pre { white-space: pre-wrap; word-break: break-word; }
  </style>
</head>
<body class="text-light">

<nav class="navbar navbar-dark" style="background:#0f172a; border-bottom:1px solid rgba(255,255,255,.08)">
  <div class="container-fluid">
    <span class="navbar-brand mb-0 h1">MP Transferencias · <span class="muted">{{cuenta}}</span></span>
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
        <div class="muted small mb-2">Cuenta MP asignada a esta sucursal</div>
        <div class="mono small" id="meBox">Cargando…</div>
      </div>

      <div class="card p-3 mt-3">
        <h5 class="mb-2">Aceptadas hoy (por vos)</h5>
        <div class="d-flex justify-content-between">
          <div class="muted">Cantidad</div>
          <div class="mono" id="accCount">—</div>
        </div>
        <div class="d-flex justify-content-between">
          <div class="muted">Total</div>
          <div class="mono" id="accTotal">—</div>
        </div>
        <div class="small muted mt-2">Se calcula por el usuario logueado.</div>
      </div>

      <!-- ADMIN -->
      <div class="card p-3 mt-3" id="adminCard" style="display:none;">
        <h5 class="mb-2">ADMIN · Asignar cuenta a sucursal</h5>
        <div class="muted small mb-2" id="assignStatus">—</div>

        <label class="small muted">Sucursal</label>
        <select id="assignSucursal" class="form-select form-select-sm mb-2"></select>

        <label class="small muted">Cuenta MP</label>
        <select id="assignMpAccount" class="form-select form-select-sm mb-2"></select>

        <button class="btn btn-success btn-sm" onclick="assignMpAccountToSucursal()">Asignar</button>

        <hr class="border border-secondary-subtle my-3" />

        <h6 class="mb-2">ADMIN · Aceptadas por día</h6>
        <div class="d-flex justify-content-between small">
          <div class="muted">Cant</div><div class="mono" id="adminCant">0</div>
        </div>
        <div class="d-flex justify-content-between small mb-2">
          <div class="muted">Total</div><div class="mono" id="adminTotal">$0</div>
        </div>

        <input id="adminDate" type="date" class="form-control form-control-sm mb-2" />
        <select id="adminSucursal" class="form-select form-select-sm mb-2"></select>
        <button class="btn btn-outline-light btn-sm" onclick="loadAdminAcceptedByDay()">Buscar</button>
        <div class="muted small mt-2" id="adminHint"></div>
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
                <th>Acción</th>
                <th>Fecha (AR)</th>
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
        <h5 class="mb-0">Historial aceptadas hoy (por vos)</h5>
        <div class="table-responsive mt-3">
          <table class="table table-sm align-middle">
            <thead>
              <tr>
                <th>Hora (AR)</th>
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

      <div class="card p-3 mt-3" id="adminTableCard" style="display:none;">
        <h5 class="mb-0">ADMIN · Aceptadas (tabla)</h5>
        <div class="table-responsive mt-3">
          <table class="table table-sm align-middle">
            <thead>
              <tr>
                <th>Hora (AR)</th>
                <th>Payment</th>
                <th class="text-end">Monto</th>
                <th>Tipo</th>
                <th>Estado</th>
                <th>Aceptada por</th>
              </tr>
            </thead>
            <tbody id="adminAcceptedRows"></tbody>
          </table>
        </div>
      </div>

    </div>
  </div>
</div>

<!-- Login Modal (Bootstrap) -->
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
        <div class="muted small mt-2">Se guarda la sesión en este navegador.</div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-success" onclick="login()">Entrar</button>
      </div>
    </div>
  </div>
</div>

<footer class="py-3 text-center muted small">By <b class="text-light">PS3 Larroque</b></footer>

<script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js"></script>
<script>
let AUTH = sessionStorage.getItem('AUTH') || '';
let loginModal;

function b64(s){ return btoa(unescape(encodeURIComponent(s))); }

function moneyAR(n){
  try { return new Intl.NumberFormat('es-AR', { style:'currency', currency:'ARS' }).format(n); }
  catch { return '$' + n; }
}

function toArDate(iso){
  try{
    const d = new Date(iso);
    const pad = (x)=> String(x).padStart(2,'0');
    return pad(d.getDate())+'/'+pad(d.getMonth()+1)+' '+pad(d.getHours())+':'+pad(d.getMinutes())+':'+pad(d.getSeconds());
  }catch{ return iso || ''; }
}

async function api(url, opts){
  opts = opts || {};
  opts.headers = opts.headers || {};
  if(AUTH) opts.headers['Authorization'] = AUTH;

  const r = await fetch(url, opts);

  if(r.status===401){
    AUTH='';
    sessionStorage.removeItem('AUTH');
    showLogin('Credenciales inválidas');
    throw new Error('401');
  }

  let j = null;
  try { j = await r.json(); } catch { j = null; }

  if(!r.ok){
    const msg = j?.message || (await r.text().catch(()=>'')) || ('HTTP ' + r.status);
    throw new Error(msg);
  }

  // si tu API usa {ok:false,...}
  if(j && j.ok === false){
    throw new Error(j.message || 'Error');
  }

  return j;
}

function showLogin(msg){
  document.getElementById('error').innerText = msg || '';
  if(!loginModal){
    loginModal = new bootstrap.Modal(document.getElementById('loginModal'), {backdrop:'static', keyboard:false});
  }
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
    await refreshAll();
    setInterval(refreshAll, 8000);
  }catch(e){
    showLogin('No pude loguear: ' + (e?.message || e));
  }
}

async function ackTransfer(id, btn){
  try{
    btn.disabled = true;

    const r = await fetch('/api/transfers/' + id + '/ack', {
      method: 'POST',
      headers: AUTH ? { Authorization: AUTH } : {}
    });

    if(r.status===401){
      AUTH='';
      sessionStorage.removeItem('AUTH');
      showLogin('Credenciales inválidas');
      return;
    }

    if(r.status===409){
      btn.classList.remove('btn-success');
      btn.classList.add('btn-soft-danger');
      btn.innerText = 'Ya tomada';
      await refreshAll();
      return;
    }

    if(!r.ok){
      const t = await r.text().catch(()=> '');
      throw new Error(t || ('HTTP ' + r.status));
    }

    await refreshAll();
  }catch(e){
    btn.disabled = false;
    alert('Error al aceptar: ' + (e?.message || e));
  }
}

function fillPending(data){
  document.getElementById('pendingCount').innerText = data?.count ?? (data?.items?.length ?? 0);
  const tbody = document.getElementById('pendingRows');
  tbody.innerHTML = '';

  const items = data?.items || [];
  if(items.length===0){
    tbody.innerHTML = `<tr><td colspan="6" class="text-warning small">No hay pendientes.</td></tr>`;
    return;
  }

  items.forEach(x=>{
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td><button class="btn btn-success btn-sm" onclick="ackTransfer(${x.id}, this)">Aceptar</button></td>
      <td class="mono small">${toArDate(x.fecha_utc || '')}</td>
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

  const items = data?.items || [];
  if(items.length===0){
    tbody.innerHTML = `<tr><td colspan="5" class="text-warning small">Todavía no aceptaste transferencias hoy.</td></tr>`;
    return;
  }

  items.forEach(x=>{
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td class="mono small">${toArDate(x.fecha_utc || '')}</td>
      <td class="mono small">${x.payment_id || ''}</td>
      <td class="text-end mono">${moneyAR(x.monto || 0)}</td>
      <td class="small">${x.payment_type || ''}</td>
      <td class="mono small">${x.accepted_by || ''}</td>
    `;
    tbody.appendChild(tr);
  });
}

// ===== ADMIN =====
function setAdminDefaultDateToday(){
  const input = document.getElementById('adminDate');
  if(!input) return;
  const now = new Date();
  const yyyy = now.getFullYear();
  const mm = String(now.getMonth()+1).padStart(2,'0');
  const dd = String(now.getDate()).padStart(2,'0');
  input.value = `${yyyy}-${mm}-${dd}`;
}

async function loadAdminSucursales(){
  try{
    const j = await api('/api/transfers/admin/sucursales');
    const items = j?.items || [];
    const sel1 = document.getElementById('adminSucursal');
    const sel2 = document.getElementById('assignSucursal');
    if(sel1){
      sel1.innerHTML = `<option value="">Todas</option>` + items.map(u=> `<option value="${u}">${u}</option>`).join('');
    }
    if(sel2){
      sel2.innerHTML = `<option value="">Elegí sucursal…</option>` + items.map(u=> `<option value="${u}">${u}</option>`).join('');
    }
  }catch{}
}

async function loadMpAccounts(){
  try{
    const j = await api('/api/transfers/admin/mp-accounts');
    const items = j?.items || [];
    const sel = document.getElementById('assignMpAccount');
    if(!sel) return;

    sel.innerHTML = `<option value="">Elegí cuenta…</option>` + items.map(x=>{
      const label = `${x.id} · ${x.nombre}${x.activa ? '' : ' (inactiva)'}`;
      return `<option value="${x.id}">${label}</option>`;
    }).join('');
  }catch{}
}

async function assignMpAccountToSucursal(){
  const suc = document.getElementById('assignSucursal')?.value || '';
  const acc = document.getElementById('assignMpAccount')?.value || '';
  const status = document.getElementById('assignStatus');

  if(!suc || !acc){
    if(status) status.innerText = 'Elegí sucursal y cuenta';
    return;
  }

  try{
    if(status) status.innerText = 'Asignando...';

    await api('/api/transfers/admin/assign-mp-account', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: suc, mpAccountId: Number(acc) })
    });

    if(status) status.innerText = `OK · ${suc} → cuenta ${acc}`;
    await refreshAll();
  }catch(e){
    if(status) status.innerText = 'Error: ' + (e?.message || e);
  }
}

async function loadAdminAcceptedByDay(){
  const date = document.getElementById('adminDate')?.value || '';
  const suc = document.getElementById('adminSucursal')?.value || '';
  const hint = document.getElementById('adminHint');
  const tbody = document.getElementById('adminAcceptedRows');
  const card = document.getElementById('adminTableCard');

  if(!date){
    if(hint) hint.innerText = 'Elegí una fecha.';
    return;
  }
  if(hint) hint.innerText = '';

  try{
    if(tbody){
      tbody.innerHTML = `<tr><td colspan="6" class="text-warning small">Cargando…</td></tr>`;
    }

    let url = `/api/transfers/admin/accepted/by-day?date=${encodeURIComponent(date)}`;
    if(suc) url += `&sucursal=${encodeURIComponent(suc)}`;

    const j = await api(url);

    document.getElementById('adminCant').innerText = j?.count ?? 0;
    document.getElementById('adminTotal').innerText = moneyAR(j?.total ?? 0);

    if(card) card.style.display = 'block';

    const items = j?.items || [];
    if(!tbody) return;

    if(items.length===0){
      tbody.innerHTML = `<tr><td colspan="6" class="text-warning small">No hay aceptadas para ese filtro.</td></tr>`;
      return;
    }

    tbody.innerHTML = items.map(x=>`
      <tr>
        <td class="mono small">${toArDate(x.fecha_utc || '')}</td>
        <td class="mono small">${x.payment_id || ''}</td>
        <td class="text-end mono">${moneyAR(x.monto || 0)}</td>
        <td class="small">${x.payment_type || ''}</td>
        <td class="small">${x.status || ''}</td>
        <td class="mono small">${x.accepted_by || ''}</td>
      </tr>
    `).join('');
  }catch(e){
    if(hint) hint.innerText = 'Error: ' + (e?.message || e);
  }
}

async function refreshAll(){
  // 1) mi cuenta
  try{
    const me = await api('/api/transfers/me/account');
    document.getElementById('meBox').innerHTML = `
      <div><span class="muted">Nombre:</span> <span class="mono">${me?.nombre || '—'}</span></div>
      <div><span class="muted">Alias:</span> <span class="mono">${me?.alias || '—'}</span></div>
      <div><span class="muted">CVU:</span> <span class="mono">${me?.cvu || '—'}</span></div>
    `;
    document.getElementById('accActive').innerText = me?.activa ? 'Activa' : 'Inactiva';
  }catch(e){
    document.getElementById('meBox').innerText = 'Error: ' + (e?.message || e);
    document.getElementById('accActive').innerText = '—';
  }

  // 2) pendientes
  try{
    const p = await api('/api/transfers/pending?limit=20');
    fillPending(p);
  }catch(e){
    document.getElementById('pendingRows').innerHTML = `<tr><td colspan="6" class="text-danger small">Error: ${e?.message || e}</td></tr>`;
    document.getElementById('pendingCount').innerText = '—';
  }

  // 3) aceptadas hoy
  try{
    const a = await api('/api/transfers/accepted/today');
    fillAccepted(a);
  }catch(e){
    document.getElementById('acceptedRows').innerHTML = `<tr><td colspan="5" class="text-danger small">Error: ${e?.message || e}</td></tr>`;
    document.getElementById('accCount').innerText = '—';
    document.getElementById('accTotal').innerText = '—';
  }

  // 4) admin: si responde, mostramos panel
  try{
    await api('/api/transfers/admin/sucursales');
    document.getElementById('adminCard').style.display = 'block';
    loadAdminSucursales();
    loadMpAccounts();
    setAdminDefaultDateToday();
  }catch{
    // si no sos admin, lo ocultamos
    document.getElementById('adminCard').style.display = 'none';
    document.getElementById('adminTableCard').style.display = 'none';
  }
}

(async function init(){
  if(AUTH){
    try{
      await api('/api/transfers/ping');
      await refreshAll();
      setInterval(refreshAll, 8000);
      return;
    }catch{}
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
