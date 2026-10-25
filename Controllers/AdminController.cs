using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

[ApiController]
[Route("admin")]
[Authorize(Roles = "administrador")]
public sealed class AdminController(IAdminUsuariosService admin, ICurrentUser actual) : ControllerBase
{
    private ContextoPeticion Contexto() =>
        new(HttpContext.Connection.RemoteIpAddress, Request.Headers.UserAgent.ToString());

    [HttpGet("usuarios")]
    public Task<PaginaDto<UsuarioListaDto>> Listar(
        [FromQuery] string? rol, [FromQuery] string? q, [FromQuery] int pagina = 1, [FromQuery] int tamano = 25,
        CancellationToken ct = default) =>
        admin.ListarAsync(rol, q, pagina, tamano, ct);

    /// <summary>Crea la cuenta del colaborador con una contraseña temporal que se muestra una sola vez.</summary>
    [HttpPost("colaboradores")]
    public async Task<ActionResult<CredencialTemporalResponse>> CrearColaborador(CrearColaboradorRequest request, CancellationToken ct)
    {
        var resultado = await admin.CrearColaboradorAsync(request, actual.Id, Contexto(), ct);
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status201Created, resultado);
    }

    [HttpPost("usuarios/{id:guid}/restablecer-password")]
    public async Task<ActionResult<CredencialTemporalResponse>> RestablecerPassword(Guid id, CancellationToken ct)
    {
        var resultado = await admin.RestablecerTemporalAsync(id, actual.Id, Contexto(), ct);
        Response.Headers.CacheControl = "no-store";
        return resultado;
    }

    [HttpPost("usuarios/{id:guid}/desbloquear")]
    public async Task<IActionResult> Desbloquear(Guid id, CancellationToken ct)
    {
        await admin.DesbloquearAsync(id, actual.Id, Contexto(), ct);
        return NoContent();
    }

    [HttpPut("usuarios/{id:guid}/activo")]
    public async Task<IActionResult> CambiarActivo(Guid id, ActivoRequest request, CancellationToken ct)
    {
        await admin.CambiarActivoAsync(id, request.Activo, actual.Id, Contexto(), ct);
        return NoContent();
    }
}
