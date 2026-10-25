namespace ZOne.Api.Dtos;

public sealed record ParticipanteRequest(Guid ColaboradorId, string Rol);

/// <summary>ClienteId solo lo usa el administrador. Participantes: de 1 a 10 artistas o productores.</summary>
public sealed record CrearProyectoRequest(long ServicioId, string Titulo, List<ParticipanteRequest> Participantes, Guid? ClienteId);

public sealed record ComentarioRequest(string Comentario);
public sealed record AjustesRequest(string? Comentario);
public sealed record ProgramarSesionRequest(long SalaId, DateTimeOffset Inicio, DateTimeOffset Fin, bool? EsExtra);
public sealed record CumplirSesionRequest(string Resena);

public sealed record ParticipanteDto(
    long Id, Guid ColaboradorId, string Nombre, string Perfil, string Rol,
    string Estado, DateTimeOffset EnviadaEn, DateTimeOffset VenceEn, DateTimeOffset? RespondidaEn);

public sealed record AceptacionDto(Guid UsuarioId, string Nombre, string Estado, DateTimeOffset? RespondidaEn);

public sealed record SesionProyectoDto(
    long Id, long ProyectoId, short? Numero, string? Etapa, bool EsExtra,
    long SalaId, string Sala, DateTimeOffset Inicio, DateTimeOffset Fin,
    string Estado, DateTimeOffset? ExpiraEn, string? Resena, DateTimeOffset? CumplidaEn,
    IReadOnlyList<AceptacionDto> Aceptaciones);

public sealed record OrdenProyectoDto(
    long Id, string Concepto, decimal Monto, string Estado,
    DateTimeOffset VenceEn, DateTimeOffset? PagadaEn, string? ReferenciaPago);

public sealed record ComentarioRevisionDto(long Id, short Ronda, Guid AutorId, string Autor, string Comentario, DateTimeOffset CreadoEn);

/// <summary>La sesión que corresponde programar ahora (numerada y con su etapa), o null si todavía no toca.</summary>
public sealed record SiguienteSesionDto(short Numero, string Etapa);

public sealed record ProyectoDto(
    long Id, string Titulo, string Tipo, long ServicioId, string Servicio, string Estado, string? EtapaActual,
    Guid ClienteId, string Cliente, decimal PrecioAcordado, short SesionesIncluidas, int SesionesCumplidas, short RondaRevision,
    DateTimeOffset? AprobadoEn, DateTimeOffset? CanceladoEn, DateTimeOffset CreadoEn,
    SiguienteSesionDto? Siguiente, int CreditosExtra,
    IReadOnlyList<ParticipanteDto> Participantes,
    IReadOnlyList<SesionProyectoDto> Sesiones,
    IReadOnlyList<OrdenProyectoDto> Ordenes,
    IReadOnlyList<ComentarioRevisionDto> Comentarios);

public sealed record ProyectoResumenDto(
    long Id, string Titulo, string Tipo, string Servicio, string Estado, string? EtapaActual,
    Guid ClienteId, string Cliente, short SesionesIncluidas, int SesionesCumplidas, DateTimeOffset CreadoEn);

/// <summary>Bandeja del colaborador: solicitudes para participar en proyectos.</summary>
public sealed record ParticipacionDto(
    long Id, long ProyectoId, string Proyecto, string Cliente, string Servicio, string Rol,
    string Estado, DateTimeOffset EnviadaEn, DateTimeOffset VenceEn, DateTimeOffset? RespondidaEn);

/// <summary>Sesiones de proyecto que esperan la respuesta de la persona que consulta.</summary>
public sealed record AceptacionPendienteDto(
    long SesionId, long ProyectoId, string Proyecto, short? Numero, string? Etapa, bool EsExtra,
    string Sala, DateTimeOffset Inicio, DateTimeOffset Fin, DateTimeOffset? ExpiraEn);

public sealed record OrdenDto(
    long Id, string Concepto, Guid ClienteId, string Cliente, long? ProyectoId, string? Proyecto, long? SesionId,
    decimal Monto, string Estado, DateTimeOffset VenceEn, DateTimeOffset? PagadaEn, string? ReferenciaPago);
