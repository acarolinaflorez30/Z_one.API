using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>Sesiones de un proyecto: aceptarlas, cancelarlas antes de confirmarse y marcarlas como cumplidas.</summary>
[ApiController]
[Route("sesiones-proyecto")]
[Authorize]
public sealed class SesionesProyectoController(ISesionesProyectoService servicio) : ControllerBase
{
    private Actor Actor => User.ComoActor();

    /// <summary>Sesiones que esperan mi respuesta.</summary>
    [HttpGet("pendientes")]
    public Task<PaginaDto<AceptacionPendienteDto>> Pendientes(
        [FromQuery] int pagina = 1, [FromQuery] int tamano = 25, CancellationToken ct = default) =>
        servicio.PendientesAsync(Actor, pagina, tamano, ct);

    [HttpGet("{id:long}")]
    public Task<SesionProyectoDto> Obtener(long id, CancellationToken ct) => servicio.ObtenerAsync(id, Actor, ct);

    /// <summary>Todos los involucrados deben aceptar; si todos aceptan, la sesión queda confirmada.</summary>
    [HttpPost("{id:long}/aceptar")]
    public Task<SesionProyectoDto> Aceptar(long id, CancellationToken ct) =>
        servicio.ResponderAsync(id, aceptar: true, Actor, this.Contexto(), ct);

    /// <summary>Si alguien rechaza, la sesión se cancela y la sala se libera para reprogramar.</summary>
    [HttpPost("{id:long}/rechazar")]
    public Task<SesionProyectoDto> Rechazar(long id, CancellationToken ct) =>
        servicio.ResponderAsync(id, aceptar: false, Actor, this.Contexto(), ct);

    /// <summary>Solo quien la programó o el administrador, y solo antes de confirmarse.</summary>
    [HttpPost("{id:long}/cancelar")]
    public Task<SesionProyectoDto> Cancelar(long id, CancellationToken ct) =>
        servicio.CancelarAsync(id, Actor, this.Contexto(), ct);

    /// <summary>
    /// El productor (o el administrador) marca la sesión como terminada, con una breve reseña, cuando termina su horario.
    /// Se avisa al cliente y al administrador. En la canción completa, la 4.ª sesión abre las 24 h de revisión del cliente;
    /// en el servicio parcial el servicio queda finalizado.
    /// </summary>
    [HttpPost("{id:long}/cumplir")]
    [Authorize(Roles = "colaborador,administrador")]
    public Task<SesionProyectoDto> Cumplir(long id, CumplirSesionRequest request, CancellationToken ct) =>
        servicio.CumplirAsync(id, request.Resena, Actor, this.Contexto(), ct);
}
