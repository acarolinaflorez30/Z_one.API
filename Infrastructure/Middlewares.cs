using System.Security.Claims;

namespace ZOne.Api.Infrastructure;

/// <summary>
/// Defensa CSRF para autenticación por cookies: toda petición que modifica datos debe traer
/// el encabezado X-Requested-With: zone-web. Un formulario o enlace de otro sitio no puede enviarlo,
/// y un fetch desde otro origen necesita pasar por CORS (que solo permite el origen del frontend).
/// </summary>
public sealed class RequireCustomHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "zone-web";

    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var modifica = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) ||
                       HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

        if (modifica && context.Request.Headers[HeaderName] != HeaderValue)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                status = 403,
                title = "Falta el encabezado de seguridad de la petición.",
                code = "csrf"
            });
            return;
        }

        await next(context);
    }
}

/// <summary>Mientras la contraseña sea temporal solo se permite cambiarla, ver el perfil o cerrar sesión.</summary>
public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    private static readonly string[] Permitidas =
        ["/auth/me", "/auth/cambiar-password", "/auth/logout", "/auth/logout-todas", "/auth/refresh"];

    public async Task InvokeAsync(HttpContext context)
    {
        var debeCambiar = context.User.Identity?.IsAuthenticated == true &&
                          context.User.FindFirst("pwd_change")?.Value == "1";

        if (debeCambiar && !Permitidas.Any(p => context.Request.Path.StartsWithSegments(p)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                status = 403,
                title = "Debes cambiar tu contraseña temporal antes de continuar.",
                code = "cambio_password_requerido"
            });
            return;
        }

        await next(context);
    }
}

public interface ICurrentUser
{
    Guid Id { get; }
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid Id =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirst("sub")?.Value, out var id)
            ? id
            : throw ApiException.NoAutenticado();
}
