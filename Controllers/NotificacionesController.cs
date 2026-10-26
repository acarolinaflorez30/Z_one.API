using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>Avisos de cada persona (sesión cumplida, revisión pendiente, ajustes pedidos...). Solo ve los suyos.</summary>
[ApiController]
[Route("notificaciones")]
[Authorize]
public sealed class NotificacionesController(INotificacionesService servicio) : ControllerBase
{
    private Guid UsuarioId => User.ComoActor().UsuarioId;

    [HttpGet]
    public Task<PaginaDto<NotificacionDto>> Listar(
        [FromQuery] bool soloNoLeidas = false, [FromQuery] int pagina = 1, [FromQuery] int tamano = 25, CancellationToken ct = default) =>
        servicio.ListarAsync(UsuarioId, soloNoLeidas, pagina, tamano, ct);

    [HttpGet("no-leidas")]
    public async Task<NoLeidasDto> NoLeidas(CancellationToken ct) =>
        new(await servicio.ContarNoLeidasAsync(UsuarioId, ct));

    [HttpPost("{id:long}/leida")]
    public async Task<IActionResult> MarcarLeida(long id, CancellationToken ct)
    {
        await servicio.MarcarLeidaAsync(id, UsuarioId, ct);
        return NoContent();
    }

    [HttpPost("leidas")]
    public async Task<IActionResult> MarcarTodasLeidas(CancellationToken ct)
    {
        await servicio.MarcarTodasLeidasAsync(UsuarioId, ct);
        return NoContent();
    }
}
