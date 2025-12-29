using Npgsql;

static class DbBootstrap
{
    public static async Task EnsureCreatedAsync(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Db");

        // Render suele dar postgres://user:pass@host:port/db
        // Npgsql necesita connection string estilo Host=...;Username=...;Password=...;Database=...
        if (!string.IsNullOrWhiteSpace(cs) &&
            cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        {
            cs = ConvertPostgresUrlToConnectionString(cs);
        }

        if (string.IsNullOrWhiteSpace(cs))
        {
            Console.WriteLine("⚠ ConnectionStrings:Db vacío. No se crea DB.");
            return;
        }

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        var sql = """
create table if not exists mp_accounts (
    id serial primary key,
    nombre text not null unique,
    access_token text not null,
    activa boolean not null default true
);


create table if not exists app_users (
    username    text primary key,
    role        text not null default 'sucursal',
    created_at  timestamptz not null default now()
);

-- Relación: qué cuenta MP ve cada usuario
create table if not exists user_mp_account (
    username        text primary key references app_users(username) on delete cascade,
    mp_account_id   bigint not null references mp_accounts(id) on delete cascade
);

-- Transferencias detectadas (por cuenta MP)
create table if not exists transfers (
    id              bigserial primary key,
    mp_account_id   bigint not null references mp_accounts(id) on delete cascade,
    payment_id      text not null,
    fecha_utc       timestamptz not null,          -- guardamos UTC
    monto           numeric(18,2) not null,
    status          text null,
    payment_type    text null,
    json_raw        jsonb null,
    created_at      timestamptz not null default now(),
    unique (mp_account_id, payment_id)
);

-- “Aceptación”: una transferencia sólo puede ser aceptada 1 vez
create table if not exists transfer_ack (
    transfer_id     bigint primary key references transfers(id) on delete cascade,
    username        text not null references app_users(username) on delete restrict,
    ack_at_utc      timestamptz not null default now(),
    ack_date_ar     date not null                  -- para reportes por día local
);

create index if not exists ix_transfers_fecha on transfers(fecha_utc desc);
create index if not exists ix_transfers_account_fecha on transfers(mp_account_id, fecha_utc desc);
create index if not exists ix_ack_user_date on transfer_ack(username, ack_date_ar);
""";


        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();

        Console.WriteLine("✅ DB ready: tablas creadas/verificadas");
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
