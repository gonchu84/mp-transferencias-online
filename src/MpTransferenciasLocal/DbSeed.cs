using Npgsql;

static class DbSeed
{
    public static async Task SeedAsync(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs))
        {
            Console.WriteLine("⚠ DbSeed: connection string vacío, no se hace seed.");
            return;
        }

        // Si viene como postgres://... lo convertimos
        if (cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
            cs = ConvertPostgresUrlToConnectionString(cs);

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        // Tokens por ENV (Render -> Environment Variables)
        // Si no están, dejamos la cuenta inactiva con token "PENDING".
        var tokGon  = Environment.GetEnvironmentVariable("MP_TOKEN_MP_GON");
        var tokTaty = Environment.GetEnvironmentVariable("MP_TOKEN_MP_TATY");
        var tokMaty = Environment.GetEnvironmentVariable("MP_TOKEN_MP_MATY");

        string TokenOrPending(string? t) => string.IsNullOrWhiteSpace(t) ? "PENDING" : t!;
        bool ActiveOrNot(string? t) => !string.IsNullOrWhiteSpace(t);

        var sql = """
        -- 1) Usuarios
        insert into app_users (username, role) values
        ('admin', 'admin'),
        ('Banfield', 'sucursal'),
        ('Adrogue', 'sucursal'),
        ('Lomas', 'sucursal'),
        ('AveLocal', 'sucursal'),
        ('AveStand', 'sucursal'),
        ('Brown', 'sucursal'),
        ('Abasto', 'sucursal'),
        ('Oeste', 'sucursal'),
        ('MarLocal', 'sucursal'),
        ('MarStand', 'sucursal')
        on conflict do nothing;

        -- 2) Cuentas MP (multi-cuenta)
        insert into mp_accounts (nombre, access_token, activa) values
        ('MP_GON',  @tokGon,  @actGon),
        ('MP_TATY', @tokTaty, @actTaty),
        ('MP_MATY', @tokMaty, @actMaty)
        on conflict (nombre) do update
        set access_token = excluded.access_token,
            activa = excluded.activa;

        -- 3) Asignación inicial (por ahora TODAS las sucursales -> MP_GON)
        -- (admin no necesita asignación)
        insert into user_mp_account (username, mp_account_id)
        select u.username, a.id
        from app_users u
        join mp_accounts a on a.nombre = 'MP_GON'
        where u.role = 'sucursal'
        on conflict (username) do nothing;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tokGon",  TokenOrPending(tokGon));
        cmd.Parameters.AddWithValue("tokTaty", TokenOrPending(tokTaty));
        cmd.Parameters.AddWithValue("tokMaty", TokenOrPending(tokMaty));
        cmd.Parameters.AddWithValue("actGon",  ActiveOrNot(tokGon));
        cmd.Parameters.AddWithValue("actTaty", ActiveOrNot(tokTaty));
        cmd.Parameters.AddWithValue("actMaty", ActiveOrNot(tokMaty));

        await cmd.ExecuteNonQueryAsync();

        Console.WriteLine("✅ Datos iniciales cargados: usuarios + cuentas MP + asignación inicial");
        if (!ActiveOrNot(tokGon) || !ActiveOrNot(tokTaty) || !ActiveOrNot(tokMaty))
            Console.WriteLine("⚠ Ojo: faltan tokens MP_* en ENV. Se cargaron como PENDING (activa=false).");
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
