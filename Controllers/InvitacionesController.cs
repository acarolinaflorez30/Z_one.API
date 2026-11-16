using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>
/// Invitaciones por sesión (de proyecto o de alquiler). El colaborador las responde; el cliente dueño y el administrador
/// invitan a otro o confirman con quienes aceptaron.
/// </summary>
[ApiController]
[Route("sesiones")]
[Authorize]
public sealed class InvitacionesController(IInvitacionesService servicio) : ControllerBase
{
    private Actor Actor => User.ComoActor();

    /// <summary>Mis invitaciones (colaborador). estado: pendiente | aceptada | rechazada | expirada.</summary>
    [HttpGet("invitaciones")]
    [Authorize(Roles = "colaborador")]
    public Task<PaginaDto<InvitacionDto>> Listar(
        [FromQuery] string? estado, [FromQuery] int pagina = 1, [FromQuery] int tamano = 25, CancellationToken ct = default) =>
        servicio.ListarAsync(Actor, estado, pagina, tamano, ct);

    /// <summary>Si todos los invitados aceptan, la sesión pasa sola a pendiente de pago.</summary>
    [HttpPost("{id:long}/aceptar")]
    [Authorize(Roles = "colaborador")]
    public Task<SesionInvitadosDto> Aceptar(long id, CancellationToken ct) =>
        servicio.ResponderAsync(id, aceptar: true, Actor, this.Contexto(), ct);

    /// <summary>Si alguien rechaza, la sesión espera la decisión del cliente o del administrador.</summary>
    [HttpPost("{id:long}/rechazar")]
    [Authorize(Roles = "colaborador")]
    public Task<SesionInvitadosDto> Rechazar(long id, CancellationToken ct) =>
        servicio.ResponderAsync(id, aceptar: false, Actor, this.Contexto(), ct);

    /// <summary>Invita a otro colaborador (artista o productor con precio definido) a una sesión que sigue esperando respuestas.</summary>
    [HttpPost("{id:long}/invitar")]
    [Authorize(Roles = "cliente,administrador")]
    public Task<SesionInvitadosDto> Invitar(long id, InvitarRequest request, CancellationToken ct) =>
        servicio.InvitarAsync(id, request.ColaboradorId, Actor, this.Contexto(), ct);

    /// <summary>Continúa con quienes ya aceptaron (al menos uno); las invitaciones pendientes se dan por vencidas.</summary>
    [HttpPost("{id:long}/confirmar")]
    [Authorize(Roles = "cliente,administrador")]
    public Task<SesionInvitadosDto> Confirmar(long id, CancellationToken ct) =>
        servicio.ConfirmarAsync(id, Actor, this.Contexto(), ct);
}
