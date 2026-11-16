using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// Solicitudes de reserva. Las crea el cliente (o el administrador para un cliente);
/// los colaboradores invitados responden y el administrador autoriza.
[ApiController]
[Route("solicitudes")]
[Authorize]
public sealed class SolicitudesController(ISolicitudesService solicitudes) : ControllerBase
{
    private Actor Actor => User.ComoActor();

    [HttpGet]
    public Task<PaginaDto<SolicitudResumenDto>> Listar(
        [FromQuery] string? estado, [FromQuery] int pagina = 1, [FromQuery] int tamano = 25,
        CancellationToken ct = default) =>
        solicitudes.ListarAsync(Actor, estado, pagina, tamano, ct);

    [HttpPost]
    [Authorize(Roles = "cliente,administrador")]
    [EnableRateLimiting("reservas")]
    public async Task<ActionResult<SolicitudDto>> Crear(CrearSolicitudRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await solicitudes.CrearAsync(request, Actor, this.Contexto(), ct));

    [HttpGet("{id:long}")]
    public Task<SolicitudDto> Obtener(long id, CancellationToken ct) =>
        solicitudes.ObtenerAsync(id, Actor, ct);

    [HttpPatch("{id:long}/estado")]
    public Task<SolicitudDto> CambiarEstado(long id, CambiarEstadoSolicitudRequest request, CancellationToken ct) =>
        solicitudes.CambiarEstadoAsync(id, request, Actor, this.Contexto(), ct);

    [HttpPatch("{id:long}/colaboradores/{colaboradorId:guid}")]
    public Task<SolicitudDto> ResponderInvitacion(long id, Guid colaboradorId, ResponderInvitacionRequest request, CancellationToken ct) =>
        solicitudes.ResponderInvitacionAsync(id, colaboradorId, request, Actor, this.Contexto(), ct);
}
