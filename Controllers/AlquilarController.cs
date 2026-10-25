using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>Alquiler de salas. El cliente ve y gestiona lo suyo; el administrador ve todo y registra los pagos.</summary>
[ApiController]
[Route("alquileres")]
[Authorize(Roles = "cliente,administrador")]
public sealed class AlquileresController(IAlquileresService servicio) : ControllerBase
{
    private Actor Actor => User.ComoActor();

    [HttpPost]
    [EnableRateLimiting("reservas")]
    public async Task<ActionResult<AlquilerDto>> Crear(CrearAlquilerRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await servicio.CrearAsync(request, Actor, this.Contexto(), ct));

    [HttpGet]
    public Task<PaginaDto<AlquilerDto>> Listar(
        [FromQuery] string? estado, [FromQuery] long? salaId, [FromQuery] Guid? clienteId,
        [FromQuery] DateTimeOffset? desde, [FromQuery] DateTimeOffset? hasta,
        [FromQuery] int pagina = 1, [FromQuery] int tamano = 25, CancellationToken ct = default) =>
        servicio.ListarAsync(Actor, estado, salaId, clienteId, desde, hasta, pagina, tamano, ct);

    [HttpGet("{id:long}")]
    public Task<AlquilerDto> Obtener(long id, CancellationToken ct) =>
        servicio.ObtenerAsync(id, Actor, ct);

    /// <summary>Solo se puede cancelar antes de pagar. Una vez pagado no se cancela y no hay reembolso.</summary>
    [HttpPost("{id:long}/cancelar")]
    public Task<AlquilerDto> Cancelar(long id, CancellationToken ct) =>
        servicio.CancelarAsync(id, Actor, this.Contexto(), ct);

    /// <summary>El administrador registra que el pago se recibió (fuera del sistema). Confirma la reserva.</summary>
    [HttpPost("{id:long}/pago")]
    [Authorize(Roles = "administrador")]
    public Task<AlquilerDto> RegistrarPago(long id, RegistrarPagoRequest request, CancellationToken ct) =>
        servicio.RegistrarPagoAsync(id, request.Referencia, Actor, this.Contexto(), ct);
}
