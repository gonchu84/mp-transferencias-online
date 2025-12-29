using Npgsql;

static class DbBootstrap
{
    using Npgsql;

static class DbBootstrap
{
    public static async Task EnsureCreatedAsync(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Db");

        // Render suele dar postgres://user:pass@host:port/db
        // Npgsql a veces necesita formato "Host=...;Username=...;Password=...;Database=...".
        if (!string.IsNullOrWhiteSpace(cs) && cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
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
}

    private static string ConvertPostgresUrlToConnectionString(string url)
    {
        // postgres://user:pass@host:port/dbname
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
            // SSL requerido en Render normalmente
            SslMode = SslMode.Require,
            TrustServerCertificate = true
        };

        return builder.ConnectionString;
    }
}
