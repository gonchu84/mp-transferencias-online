using Microsoft.Extensions.Configuration;

sealed class AuthUser
{
    public string User { get; set; } = "";
    public string Pass { get; set; } = "";
    public string? Role { get; set; }
}

static class AuthHelpers
{
    public static Dictionary<string, AuthUser> LoadAuthUsers(IConfiguration cfg)
    {
        var list = cfg.GetSection("Auth:Users").Get<List<AuthUser>>() ?? new();
        var dict = new Dictionary<string, AuthUser>(StringComparer.OrdinalIgnoreCase);

        foreach (var u in list)
        {
            if (string.IsNullOrWhiteSpace(u.User) || string.IsNullOrWhiteSpace(u.Pass))
                continue;

            dict[u.User] = u;
        }

        return dict;
    }
}
