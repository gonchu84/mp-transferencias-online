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

        // 🔐 Contraseñas = nombre + 123
        var passAdmin   = BCrypt.Net.BCrypt.HashPassword("admin123");
        var passBan     = BCrypt.Net.BCrypt.HashPassword("Banfield123");
        var passAdr     = BCrypt.Net.BCrypt.HashPassword("Adrogue123");
        var passLom     = BCrypt.Net.BCrypt.HashPassword("Lomas123");
        var passAveLoc  = BCrypt.Net.BCrypt.HashPassword("AveLocal123");
        var passAveSta  = BCrypt.Net.BCrypt.HashPassword("AveStand123");
        var passBrown   = BCrypt.Net.BCrypt.HashPassword("Brown123");
        var passAbasto  = BCrypt.Net.BCrypt.HashPassword("Abasto123");
        var passOeste   = BCrypt.Net.BCrypt.HashPassword("Oeste123");
        var passMarLoc  = BCrypt.Net.BCrypt.HashPassword("MarLocal123");
        var passMarSta  = BCrypt.Net.BCrypt.HashPassword("MarStand123");

        var sql = """
        insert into app_users (username, role, password_hash) values
        ('admin',     'admin',    @pAdmin),
        ('Banfield',  'sucursal', @pBan),
        ('Adrogue',   'sucursal', @pAdr),
        ('Lomas',     'sucursal', @pLom),
        ('AveLocal',  'sucursal', @pAveLoc),
        ('AveStand',  'sucursal', @pAveSta),
        ('Brown',     'sucursal', @pBrown),
        ('Abasto',    'sucursal', @pAbasto),
        ('Oeste',     'sucursal', @pOeste),
        ('MarLocal',  'sucursal', @pMarLoc),
        ('MarStand',  'sucursal', @pMarSta)
        on conflict (username) do update
        set role = excluded.role,
            password_hash = excluded.password_hash;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("pAdmin", passAdmin);
        cmd.Parameters.AddWithValue("pBan", passBan);
        cmd.Parameters.AddWithValue("pAdr", passAdr);
        cmd.Parameters.AddWithValue("pLom", passLom);
        cmd.Parameters.AddWithValue("pAveLoc", passAveLoc);
        cmd.Parameters.AddWithValue("pAveSta", passAveSta);
        cmd.Parameters.AddWithValue("pBrown", passBrown);
        cmd.Parameters.AddWithValue("pAbasto", passAbasto);
        cmd.Parameters.AddWithValue("pOeste", passOeste);
        cmd.Parameters.AddWithValue("pMarLoc", passMarLoc);
        cmd.Parameters.AddWithValue("pMarSta", passMarSta);

        await cmd.ExecuteNonQueryAsync();
        Console.WriteLine("✅ Usuarios cargados / actualizados con clave nombre+123");
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
