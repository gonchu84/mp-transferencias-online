using BCrypt.Net;
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

        if (cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            cs.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            cs = ConvertPostgresUrlToConnectionString(cs);

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        // ✅ Contraseñas (vos definilas)
        var passAdmin = BCrypt.Net.BCrypt.HashPassword("TU_PASS_ADMIN");
        var passSuc = BCrypt.Net.BCrypt.HashPassword("TU_PASS_SUCURSAL");

        var sql = """
        insert into app_users (username, role, password_hash) values
        ('admin', 'admin', @pAdmin),
        ('Banfield', 'sucursal', @pSuc),
        ('Adrogue', 'sucursal', @pSuc),
        ('Lomas', 'sucursal', @pSuc),
        ('AveLocal', 'sucursal', @pSuc),
        ('AveStand', 'sucursal', @pSuc),
        ('Brown', 'sucursal', @pSuc),
        ('Abasto', 'sucursal', @pSuc),
        ('Oeste', 'sucursal', @pSuc),
        ('MarLocal', 'sucursal', @pSuc),
        ('MarStand', 'sucursal', @pSuc)
        on conflict (username) do update
        set role = excluded.role,
            password_hash = coalesce(app_users.password_hash, excluded.password_hash);
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("pAdmin", passAdmin);
        cmd.Parameters.AddWithValue("pSuc", passSuc);

        await cmd.ExecuteNonQueryAsync();
        Console.WriteLine("✅ Usuarios iniciales cargados (con password_hash)");
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
            SslMode = SslMode.Require
        };
        return builder.ConnectionString;
    }
}
