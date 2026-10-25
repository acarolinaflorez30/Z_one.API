using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>Bandeja de solicitudes del colaborador: aceptar o rechazar participar en un proyecto (plazo de 72 horas).</summary>
[ApiController]
[Route("participaciones")]
[Authorize(Roles = "colaborador,administrador")]
public sealed class ParticipacionesController(IProyectosService servicio) : ControllerBase
{
    private Actor Actor => User.ComoActor();

    [HttpGet]
    public Task<PaginaDto<ParticipacionDto>> Listar(
        [FromQuery] string? estado, [FromQuery] int pagina = 1, [FromQuery] int tamano = 25, CancellationToken ct = default) =>
        servicio.ListarParticipacionesAsync(Actor, estado, pagina, tamano, ct);

    [HttpPost("{id:long}/aceptar")]
    [Authorize(Roles = "colaborador")]
    public Task<ProyectoDto> Aceptar(long id, CancellationToken ct) =>
        servicio.ResponderParticipacionAsync(id, aceptar: true, Actor, this.Contexto(), ct);

    [HttpPost("{id:long}/rechazar")]
    [Authorize(Roles = "colaborador")]
    public Task<ProyectoDto> Rechazar(long id, CancellationToken ct) =>
        servicio.ResponderParticipacionAsync(id, aceptar: false, Actor, this.Contexto(), ct);
}
