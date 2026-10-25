using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>
/// Agenda por rol: cada persona ve lo suyo y el administrador ve todo.
/// Las sesiones cumplidas (y los alquileres ya realizados) quedan en la vista "historial".
/// </summary>
[ApiController]
[Route("agenda")]
[Authorize]
public sealed class AgendaController(IAgendaService servicio) : ControllerBase
{
    private Actor Actor => User.ComoActor();

    /// <param name="vista">proximas (por defecto), historial o todas.</param>
    /// <param name="desde">Fechas en ISO 8601, por ejemplo 2026-11-01T00:00:00-05:00.</param>
    /// <param name="tipo">alquiler o proyecto.</param>
    /// <param name="clienteId">Solo lo usa el administrador.</param>
    [HttpGet]
    public Task<PaginaDto<AgendaItemDto>> Listar(
        [FromQuery] string? vista, [FromQuery] DateTimeOffset? desde, [FromQuery] DateTimeOffset? hasta,
        [FromQuery] string? tipo, [FromQuery] string? estado, [FromQuery] long? salaId,
        [FromQuery] long? proyectoId, [FromQuery] Guid? clienteId,
        [FromQuery] int pagina = 1, [FromQuery] int tamano = 50, CancellationToken ct = default) =>
        servicio.ListarAsync(Actor, vista, desde, hasta, tipo, estado, salaId, proyectoId, clienteId, pagina, tamano, ct);

    /// <summary>Contadores y la próxima sesión, para el inicio de cada usuario.</summary>
    [HttpGet("resumen")]
    public Task<AgendaResumenDto> Resumen(CancellationToken ct) => servicio.ResumenAsync(Actor, ct);
}
