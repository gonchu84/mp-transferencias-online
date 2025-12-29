using Npgsql;

static class DbSeed
{
    public static async Task SeedAsync(string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        var sql = @"
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

        insert into mp_accounts (nombre, access_token, activa) values
        ('MP_GON', 'TOKEN_GON', true),
        ('MP_TATY', 'TOKEN_TATY', true),
        ('MP_MATY', 'TOKEN_MATY', true)
        on conflict do nothing;
        ";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();

        Console.WriteLine("✅ Datos iniciales cargados");
    }
}
