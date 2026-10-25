using Microsoft.Extensions.Options;
using ZOne.Api.Options;
using ZOne.Api.Services;

namespace ZOne.Api.Security;

/// <summary>Emite y borra las cookies HttpOnly de sesión. JavaScript nunca puede leer los tokens.</summary>
public sealed class AuthCookieWriter(IOptions<AuthOptions> options)
{
    public const string Access = "zone_access";
    public const string Refresh = "zone_refresh";

    private CookieOptions Crear(string path, DateTimeOffset? expira = null)
    {
        var o = options.Value;
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = o.CookieSecure,
            SameSite = Enum.TryParse<SameSiteMode>(o.CookieSameSite, true, out var s) ? s : SameSiteMode.Lax,
            Path = path,
            Expires = expira,
            IsEssential = true
        };
    }

    public void Establecer(HttpResponse response, ResultadoSesion sesion)
    {
        response.Cookies.Append(Access, sesion.AccessToken, Crear("/", sesion.AccessExpiraEn));
        // El refresh solo viaja a /auth/*, no a cada petición de la API.
        response.Cookies.Append(Refresh, sesion.RefreshToken, Crear("/auth", sesion.RefreshExpiraEn));
    }

    public void Limpiar(HttpResponse response)
    {
        response.Cookies.Delete(Access, Crear("/"));
        response.Cookies.Delete(Refresh, Crear("/auth"));
    }
}
