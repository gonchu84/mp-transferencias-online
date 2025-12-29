using Npgsql;

static class DbBootstrap
{
    public static async Task EnsureCreatedAsync(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Db");
        if (string.IsNullOrWhiteSpace(cs))
        {
            Console.WriteLine("⚠ ConnectionStrings:Db vacío. No se crea DB.");
            return;
        }

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        var sql = """
        create table if not exists mp_accounts (
            id              serial primary key,
            nombre          text not null,
            access_token    text not null,
            activa          boolean not null default true
        );

        create table if not exists app_users (
            username    text primary key,
            role        text not null default 'sucursal'
        );

        create table if not exists user_mp_account (
            username        text primary key references app_users(username) on delete cascade,
            mp_account_id   int not null references mp_accounts(id) on delete cascade
        );

        create table if not exists transfers (
            id              bigserial primary key,
            mp_account_id   int not null references mp_accounts(id) on delete cascade,
            payment_id      text not null,
            fecha_utc       timestamptz not null,
            monto           numeric(18,2) not null,
            status          text null,
            payment_type    text null,
            json_raw        jsonb null,
            created_at      timestamptz not null default now(),
            unique (mp_account_id, payment_id)
        );

        create table if not exists transfer_ack (
            id              bigserial primary key,
            transfer_id     bigint not null references transfers(id) on delete cascade,
            username        text not null references app_users(username) on delete restrict,
            ack_at_utc      timestamptz not null default now(),
            ack_date_ar     date not null,
            unique (transfer_id)
        );

        create index if not exists ix_transfers_fecha on transfers(fecha_utc desc);
        create index if not exists ix_transfers_account on transfers(mp_account_id, fecha_utc desc);
        create index if not exists ix_ack_date_user on transfer_ack(ack_date_ar, username);
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();

        Console.WriteLine("✅ DB ready: tablas creadas/verificadas");
    }
}
