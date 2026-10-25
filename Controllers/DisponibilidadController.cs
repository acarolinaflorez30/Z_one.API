using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>Horarios ocupados de una sala. Público y sin datos personales: sirve para elegir una hora libre.</summary>
[ApiController]
[Route("publico/salas")]
[AllowAnonymous]
[EnableRateLimiting("publico")]
public sealed class DisponibilidadController(IAlquileresService servicio) : ControllerBase
{
    /// <example>/publico/salas/1/disponibilidad?desde=2026-11-05T00:00:00Z&amp;hasta=2026-11-12T00:00:00Z</example>
    [HttpGet("{id:long}/disponibilidad")]
    public Task<DisponibilidadDto> Disponibilidad(
        long id, [FromQuery] DateTimeOffset desde, [FromQuery] DateTimeOffset hasta, CancellationToken ct) =>
        servicio.DisponibilidadAsync(id, desde, hasta, ct);
}
