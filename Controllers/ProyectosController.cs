using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// Proyectos: una canción completa (4 sesiones + ajuste opcional) o un servicio parcial (1 sesión).
/// Los crea el cliente o el administrador; los colaboradores invitados los ven y trabajan en ellos.
[ApiController]
[Route("proyectos")]
[Authorize]
public sealed class ProyectosController(IProyectosService proyectos, ISesionesProyectoService sesiones) : ControllerBase
{
    private Actor Actor => User.ComoActor();

    /// <param name="revisionVencida">true = revisiones cuyo plazo de 24 h venció sin respuesta del cliente (las decide el administrador).</param>
    [HttpGet]
    public Task<PaginaDto<ProyectoResumenDto>> Listar(
        [FromQuery] string? estado, [FromQuery] bool? revisionVencida,
        [FromQuery] int pagina = 1, [FromQuery] int tamano = 25, CancellationToken ct = default) =>
        proyectos.ListarAsync(Actor, estado, revisionVencida, pagina, tamano, ct);

    [HttpPost]
    [Authorize(Roles = "cliente,administrador")]
    [EnableRateLimiting("reservas")]
    public async Task<ActionResult<ProyectoDto>> Crear(CrearProyectoRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await proyectos.CrearAsync(request, Actor, this.Contexto(), ct));

    [HttpGet("{id:long}")]
    public Task<ProyectoDto> Obtener(long id, CancellationToken ct) => proyectos.ObtenerAsync(id, Actor, ct);

    // Colaboradores

    /// Envía la solicitud a otro colaborador (si uno rechazó o no respondió). Solo antes de confirmar
    [HttpPost("{id:long}/participantes")]
    [Authorize(Roles = "cliente,administrador")]
    public Task<ProyectoDto> AgregarParticipante(long id, ParticipanteRequest request, CancellationToken ct) =>
        proyectos.AgregarParticipanteAsync(id, request, Actor, this.Contexto(), ct);

    /// Continúa con los colaboradores que ya aceptaron (sin quien rechazó o no respondió) y genera el cobro.
    [HttpPost("{id:long}/continuar")]
    [Authorize(Roles = "cliente,administrador")]
    public Task<ProyectoDto> Continuar(long id, CancellationToken ct) =>
        proyectos.ContinuarAsync(id, Actor, this.Contexto(), ct);

    /// Solo mientras el proyecto no esté confirmado
    [HttpPost("{id:long}/cancelar")]
    [Authorize(Roles = "cliente,administrador")]
    public Task<ProyectoDto> Cancelar(long id, CancellationToken ct) =>
        proyectos.CancelarAsync(id, Actor, this.Contexto(), ct);

    // Revisión del cliente (solo canción completa)

    [HttpPost("{id:long}/comentarios")]
    [Authorize(Roles = "cliente")]
    public Task<ProyectoDto> Comentar(long id, ComentarioRequest request, CancellationToken ct) =>
        proyectos.ComentarAsync(id, request.Comentario, Actor, this.Contexto(), ct);

    /// El cliente NO aprueba: se habilita la sesión de ajuste. El administrador puede hacerlo por él
    [HttpPost("{id:long}/ajustes")]
    [Authorize(Roles = "cliente,administrador")]
    public Task<ProyectoDto> SolicitarAjustes(long id, AjustesRequest? request, CancellationToken ct) =>
        proyectos.SolicitarAjustesAsync(id, request?.Comentario, Actor, this.Contexto(), ct);

    /// El cliente aprueba (24 h de plazo). Si no responde, lo hace el administrador
    [HttpPost("{id:long}/aprobar")]
    [Authorize(Roles = "cliente,administrador")]
    public Task<ProyectoDto> Aprobar(long id, CancellationToken ct) =>
        proyectos.AprobarAsync(id, Actor, this.Contexto(), ct);

    /// El productor, el cliente o el administrador piden una sesión extra: genera un cobro
    [HttpPost("{id:long}/sesion-extra")]
    public Task<ProyectoDto> SolicitarSesionExtra(long id, CancellationToken ct) =>
        proyectos.SolicitarSesionExtraAsync(id, Actor, this.Contexto(), ct);

    // Sesiones del proyecto

    ///Programa la próxima sesión (numerada, o la de ajuste). Los demás involucrados deben aceptarla
    [HttpPost("{id:long}/sesiones")]
    [EnableRateLimiting("reservas")]
    public async Task<ActionResult<SesionProyectoDto>> ProgramarSesion(long id, ProgramarSesionRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await sesiones.ProgramarAsync(id, request, Actor, this.Contexto(), ct));
}
