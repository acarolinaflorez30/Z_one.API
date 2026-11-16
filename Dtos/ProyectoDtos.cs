using System.Text.Json;

namespace ZOne.Api.Dtos;

/// <summary>La primera sesión se pide al crear el proyecto. Los colaboradores se invitan por sesión (artistas o productores); en un proyecto se exige al menos uno.</summary>
public sealed record PrimeraSesionRequest(long SalaId, DateTimeOffset Inicio, List<Guid>? ColaboradorIds);

/// <summary>Servicio + título + primera sesión, en una sola petición. ClienteId solo lo usa el administrador.</summary>
public sealed record CrearProyectoRequest(long ServicioId, string Titulo, Guid? ClienteId, PrimeraSesionRequest PrimeraSesion);

public sealed record ComentarioRequest(string Comentario);
public sealed record AjustesRequest(string? Comentario);

/// <summary>
/// Sesión de un proyecto ya creado. Dura 3 horas desde Inicio (no envíes Fin).
/// Solo la sesión extra (EsExtra = true) es de duración variable: exige Fin, mínimo 1 hora y sin máximo.
/// </summary>
public sealed record ProgramarSesionRequest(long SalaId, DateTimeOffset Inicio, DateTimeOffset? Fin, bool? EsExtra, List<Guid>? ColaboradorIds);

public sealed record CumplirSesionRequest(string Resena);
public sealed record InvitarRequest(Guid ColaboradorId);

/// <summary>Una invitación de una sesión. Precio solo lo ven el cliente, el administrador y el propio colaborador.</summary>
public sealed record InvitadoDto(
    Guid UsuarioId, Guid? ColaboradorId, string Nombre, string? Perfil, string? Rol,
    string Estado, DateTimeOffset EnviadaEn, DateTimeOffset? RespondidaEn, decimal? Precio);

/// <summary>
/// Estado de las invitaciones de una sesión (de proyecto o de alquiler).
/// PuedeConfirmar: hay al menos un aceptado y la sesión espera a los demás. RequiereDecision: nadie falta por responder y alguien rechazó.
/// </summary>
public sealed record SesionInvitadosDto(
    long SesionId, string Estado, DateTimeOffset? ExpiraEn, decimal? Monto,
    bool PuedeConfirmar, bool RequiereDecision, IReadOnlyList<InvitadoDto> Invitados);

public sealed record SesionProyectoDto(
    long Id, long ProyectoId, string Proyecto, string TipoProyecto, string Servicio,
    short? Numero, short SesionesIncluidas, string Descripcion, string? Etapa, bool EsExtra, bool EsAjuste,
    long SalaId, string Sala, DateTimeOffset Inicio, DateTimeOffset Fin, decimal DuracionHoras,
    Guid ClienteId, string Cliente,
    string Estado, DateTimeOffset? ExpiraEn, string? Resena, DateTimeOffset? CumplidaEn,
    decimal? Monto, string? EstadoPago, long? OrdenId,
    bool PuedeConfirmar, bool RequiereDecision,
    IReadOnlyList<InvitadoDto> Invitados);

public sealed record OrdenProyectoDto(
    long Id, long? SesionId, string Concepto, decimal Monto, string Estado,
    DateTimeOffset VenceEn, DateTimeOffset? PagadaEn, string? ReferenciaPago);

/// <summary>Pagado = cobros ya pagados. Pendiente = cobros por pagar (a las tarifas de hoy). Total = suma de ambos.</summary>
public sealed record TotalesProyectoDto(decimal Pagado, decimal Pendiente, decimal Total);

public sealed record ComentarioRevisionDto(long Id, short Ronda, Guid AutorId, string Autor, string Comentario, DateTimeOffset CreadoEn);

/// <summary>
/// La sesión que corresponde programar ahora, o null si todavía no toca.
/// Numero es null en la sesión de ajuste (EsAjuste = true), que no cuenta dentro de las 4 del paquete.
/// </summary>
public sealed record SiguienteSesionDto(short? Numero, string Etapa, bool EsAjuste);

/// <summary>Totales y Ordenes solo los ven el cliente dueño y el administrador (los colaboradores no ven dinero).</summary>
public sealed record ProyectoDto(
    long Id, string Titulo, string Tipo, long ServicioId, string Servicio, string Estado, string? EtapaActual,
    Guid ClienteId, string Cliente, short SesionesIncluidas, int SesionesCumplidas, short RondaRevision,
    DateTimeOffset? AprobadoEn, DateTimeOffset? CanceladoEn, DateTimeOffset CreadoEn,
    DateTimeOffset? RevisionVenceEn, bool RevisionVencida,
    SiguienteSesionDto? Siguiente,
    TotalesProyectoDto? Totales,
    IReadOnlyList<SesionProyectoDto> Sesiones,
    IReadOnlyList<OrdenProyectoDto> Ordenes,
    IReadOnlyList<ComentarioRevisionDto> Comentarios);

public sealed record ProyectoResumenDto(
    long Id, string Titulo, string Tipo, string Servicio, string Estado, string? EtapaActual,
    Guid ClienteId, string Cliente, short SesionesIncluidas, int SesionesCumplidas, DateTimeOffset CreadoEn,
    DateTimeOffset? RevisionVenceEn);

/// <summary>Un evento de la trazabilidad del proyecto. Actor = "sistema" cuando lo hizo un vencimiento automático.</summary>
public sealed record HistorialEventoDto(
    long Id, long? SesionId, string Evento, string? EstadoAnterior, string? EstadoNuevo,
    JsonElement Detalle, string Actor, DateTimeOffset CreadoEn);

public sealed record OrdenDto(
    long Id, string Concepto, Guid ClienteId, string Cliente, long? ProyectoId, string? Proyecto, long? SesionId,
    decimal Monto, string Estado, DateTimeOffset VenceEn, DateTimeOffset? PagadaEn, string? ReferenciaPago);
