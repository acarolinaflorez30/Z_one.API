namespace ZOne.Api.Options;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "zone-api";
    public string Audience { get; set; } = "zone-web";

    /// <summary>Clave de firma (mínimo 32 caracteres). Va en user-secrets o variable de entorno, nunca en el repositorio.</summary>
    public string SigningKey { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int MinPasswordLength { get; set; } = 10;
    public int PasswordResetMinutes { get; set; } = 30;
    public int Pbkdf2Iterations { get; set; } = 600_000;

    public bool CookieSecure { get; set; } = true;

    /// <summary>Lax, Strict o None. None exige HTTPS y solo se usa si frontend y API están en sitios distintos.</summary>
    public string CookieSameSite { get; set; } = "Lax";

    public string FrontendBaseUrl { get; set; } = "http://localhost:5173";
    public string[] AllowedOrigins { get; set; } = [];
}
