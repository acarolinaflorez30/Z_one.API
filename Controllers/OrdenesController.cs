using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>Cobros. El sistema no cobra: el administrador registra el pago recibido fuera del sistema.</summary>
[ApiController]
[Route("ordenes")]
[Authorize(Roles = "cliente,administrador")]
public sealed class OrdenesController(IOrdenesService servicio) : ControllerBase
{
    private Actor Actor => User.ComoActor();

    [HttpGet]
    public Task<PaginaDto<OrdenDto>> Listar(
        [FromQuery] string? estado, [FromQuery] string? concepto, [FromQuery] long? proyectoId,
        [FromQuery] int pagina = 1, [FromQuery] int tamano = 25, CancellationToken ct = default) =>
        servicio.ListarAsync(Actor, estado, concepto, proyectoId, pagina, tamano, ct);

    /// <summary>Para servicios y sesiones extra. Los alquileres se pagan en /alquileres/{id}/pago.</summary>
    [HttpPost("{id:long}/pago")]
    [Authorize(Roles = "administrador")]
    public Task<OrdenDto> RegistrarPago(long id, RegistrarPagoRequest request, CancellationToken ct) =>
        servicio.RegistrarPagoAsync(id, request.Referencia, Actor, this.Contexto(), ct);
}
