using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Security;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

[ApiController]
[Route("auth")]
public sealed class AuthController(IAuthService auth, ICurrentUser actual, AuthCookieWriter cookies) : ControllerBase
{
    private ContextoPeticion Contexto() =>
        new(HttpContext.Connection.RemoteIpAddress, Request.Headers.UserAgent.ToString());

    private ActionResult<SesionResponse> Sesion(ResultadoSesion s, int status = StatusCodes.Status200OK)
    {
        cookies.Establecer(Response, s);
        return StatusCode(status, new SesionResponse(s.Usuario, s.AccessExpiraEn));
    }

    /// <summary>Registro autónomo: solo crea clientes. Los colaboradores los crea el administrador.</summary>
    [HttpPost("registro")]
    [AllowAnonymous]
    [EnableRateLimiting("registro")]
    public async Task<ActionResult<SesionResponse>> Registro(RegistroRequest request, CancellationToken ct) =>
        Sesion(await auth.RegistrarClienteAsync(request, Contexto(), ct), StatusCodes.Status201Created);

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<SesionResponse>> Login(LoginRequest request, CancellationToken ct) =>
        Sesion(await auth.LoginAsync(request, Contexto(), ct));

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("sesion")]
    public async Task<ActionResult<SesionResponse>> Refresh(CancellationToken ct)
    {
        try
        {
            return Sesion(await auth.RefrescarAsync(Request.Cookies[AuthCookieWriter.Refresh], Contexto(), ct));
        }
        catch (ApiException)
        {
            cookies.Limpiar(Response);   // una cookie inválida no debe quedarse en el navegador
            throw;
        }
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(Request.Cookies[AuthCookieWriter.Refresh], ct);
        cookies.Limpiar(Response);
        return NoContent();
    }

    [HttpPost("logout-todas")]
    [Authorize]
    public async Task<IActionResult> LogoutTodas(CancellationToken ct)
    {
        await auth.LogoutTodasAsync(actual.Id, ct);
        cookies.Limpiar(Response);
        return NoContent();
    }

    /// <summary>Usuario actual y sus permisos. El frontend los usa solo para mostrar u ocultar opciones.</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UsuarioDto>> Me(CancellationToken ct) =>
        await auth.ObtenerPerfilAsync(actual.Id, ct);

    [HttpPost("cambiar-password")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<SesionResponse>> CambiarPassword(CambiarPasswordRequest request, CancellationToken ct) =>
        Sesion(await auth.CambiarPasswordAsync(actual.Id, request, Contexto(), ct));

    /// <summary>Siempre responde 202, exista o no el correo.</summary>
    [HttpPost("olvide-password")]
    [AllowAnonymous]
    [EnableRateLimiting("recuperacion")]
    public async Task<IActionResult> OlvidePassword(OlvidePasswordRequest request, CancellationToken ct)
    {
        await auth.OlvidePasswordAsync(request, Contexto(), ct);
        return Accepted(new { mensaje = "Si el correo está registrado, recibirás un enlace para restablecer la contraseña." });
    }

    [HttpPost("restablecer-password")]
    [AllowAnonymous]
    [EnableRateLimiting("recuperacion")]
    public async Task<IActionResult> RestablecerPassword(RestablecerPasswordRequest request, CancellationToken ct)
    {
        await auth.RestablecerPasswordAsync(request, Contexto(), ct);
        return NoContent();
    }
}
