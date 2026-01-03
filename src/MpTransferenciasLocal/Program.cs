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
app.UseAuthorization();

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

// ================= API =================
app.MapGet("/api/transfers/ping", () => Results.Ok(new { ok = true }));
app.MapControllers();

// ================= HOME + HTML =================
app.MapGet("/", (IConfiguration cfg) =>
{
    var cuenta = cfg["Sucursal"] ?? "Cuenta";

    var html = $$"""
<!doctype html>
<html lang="es">
<head>
<meta charset="utf-8">
<title>MP Transferencias</title>
<style>
body{margin:0;font-family:Arial;background:#0b1220;color:#e5e7eb}
#loginOverlay{position:fixed;inset:0;background:rgba(0,0,0,.85);display:flex;align-items:center;justify-content:center}
#loginCard{background:#0f172a;padding:20px;border-radius:12px;width:320px}
input,button{width:100%;padding:10px;margin-top:8px;border-radius:6px;border:none}
button{background:#22c55e;color:#000;font-weight:bold;cursor:pointer}
#error{color:#f87171;margin-top:10px}
pre{white-space:pre-wrap}
</style>
</head>
<body>

<div id="loginOverlay">
  <div id="loginCard">
    <h3>Iniciar sesión</h3>
    <input id="u" placeholder="Usuario">
    <input id="p" type="password" placeholder="Contraseña">
    <button onclick="login()">Entrar</button>
    <div id="error"></div>
  </div>
</div>

<h2 style="padding:10px">MP Transferencias · {{cuenta}}</h2>
<pre id="out" style="padding:10px"></pre>

<script>
let AUTH = sessionStorage.getItem('AUTH') || '';

function b64(s){
  return btoa(unescape(encodeURIComponent(s)));
}

async function api(url){
  const r = await fetch(url,{
    headers: AUTH ? {Authorization:AUTH}:{}
  });
  if(r.status===401){
    AUTH='';
    sessionStorage.removeItem('AUTH');
    document.getElementById('loginOverlay').style.display='flex';
    document.getElementById('error').innerText='Credenciales inválidas';
    throw '401';
  }
  return r.json();
}

async function login(){
  const u=document.getElementById('u').value;
  const p=document.getElementById('p').value;
  AUTH='Basic '+b64(u+':'+p);
  sessionStorage.setItem('AUTH',AUTH);

  try{
    await api('/api/transfers/ping');
    document.getElementById('loginOverlay').style.display='none';
    load();
  }catch{}
}

async function load(){
  const out=document.getElementById('out');
  const a=await api('/api/transfers/me/account').catch(()=>null);
  const p=await api('/api/transfers/pending?limit=20').catch(()=>null);
  const t=await api('/api/transfers/accepted/today').catch(()=>null);
  out.textContent=
    'me/account:\\n'+JSON.stringify(a,null,2)+'\\n\\n'+
    'pending:\\n'+JSON.stringify(p,null,2)+'\\n\\n'+
    'accepted/today:\\n'+JSON.stringify(t,null,2);
}

if(AUTH){
  api('/api/transfers/ping').then(()=>{
    document.getElementById('loginOverlay').style.display='none';
    load();
  }).catch(()=>{});
}
</script>

</body>
</html>
""";

    return Results.Content(html, "text/html; charset=utf-8");
});

app.Run();
