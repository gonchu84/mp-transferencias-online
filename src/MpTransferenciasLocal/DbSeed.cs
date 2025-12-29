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

        var sql = """
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

        -- OJO: para usar on conflict do nothing acá,
        -- mp_accounts necesita una restricción unique por nombre.
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();

        Console.WriteLine("✅ Datos iniciales (usuarios) cargados");
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
