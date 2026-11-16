using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>Sesiones de un proyecto: consultarlas, cancelarlas antes de confirmarse y marcarlas como cumplidas. Las invitaciones están en /sesiones.</summary>
[ApiController]
[Route("sesiones-proyecto")]
[Authorize]
public sealed class SesionesProyectoController(ISesionesProyectoService servicio) : ControllerBase
{
    private Actor Actor => User.ComoActor();

    [HttpGet("{id:long}")]
    public Task<SesionProyectoDto> Obtener(long id, CancellationToken ct) => servicio.ObtenerAsync(id, Actor, ct);

    /// <summary>Solo el cliente dueño o el administrador, y solo antes de confirmarse.</summary>
    [HttpPost("{id:long}/cancelar")]
    [Authorize(Roles = "cliente,administrador")]
    public Task<SesionProyectoDto> Cancelar(long id, CancellationToken ct) =>
        servicio.CancelarAsync(id, Actor, this.Contexto(), ct);

    /// <summary>
    /// El productor que aceptó esa sesión (o el administrador) la marca como terminada, con una breve reseña, cuando termina su horario.
    /// Se avisa al cliente y al administrador. En la canción completa, la 4.ª sesión abre las 24 h de revisión del cliente;
    /// en el servicio parcial el proyecto queda finalizado.
    /// </summary>
    [HttpPost("{id:long}/cumplir")]
    [Authorize(Roles = "colaborador,administrador")]
    public Task<SesionProyectoDto> Cumplir(long id, CumplirSesionRequest request, CancellationToken ct) =>
        servicio.CumplirAsync(id, request.Resena, Actor, this.Contexto(), ct);
}
