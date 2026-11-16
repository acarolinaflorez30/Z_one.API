namespace ZOne.Api.Domain;

// Coinciden con los enums de PostgreSQL: CancionCompleta -> cancion_completa, EnCurso -> en_curso, etc.
public enum TipoProyecto { CancionCompleta, ServicioParcial }
public enum EstadoProyecto { Pendiente, Confirmado, EnCurso, EnRevision, Finalizado, Cancelado }
public enum EstadoParticipacion { Pendiente, Aceptada, Rechazada, Expirada }
public enum RolParticipante { Artista, Productor }

/// <summary>
/// Una canción completa o un servicio parcial que contrata un cliente.
/// No tiene precio propio: cada sesión se cobra por separado (sala + colaboradores que aceptaron).
/// </summary>
public sealed class Proyecto
{
    public long Id { get; set; }
    public Guid ClienteId { get; set; }
    public long ServicioId { get; set; }
    public string Titulo { get; set; } = "";
    public TipoProyecto Tipo { get; set; }
    public EtapaProyecto? EtapaActual { get; set; }
    public EstadoProyecto Estado { get; set; } = EstadoProyecto.Pendiente;
    public short SesionesIncluidas { get; set; }
    public short RondaRevision { get; set; }
    public DateTimeOffset? AprobadoEn { get; set; }
    public DateTimeOffset? CanceladoEn { get; set; }
    /// <summary>Plazo de 24 h del cliente para aprobar o pedir ajustes. Solo tiene valor mientras el proyecto está en revisión.</summary>
    public DateTimeOffset? RevisionVenceEn { get; set; }
    public Guid? CreadoPor { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
    public DateTimeOffset ActualizadoEn { get; set; }

    public Cliente Cliente { get; set; } = null!;
    public Servicio Servicio { get; set; } = null!;
}

/// <summary>
/// Invitación de una sesión (de proyecto o de alquiler) a un colaborador. Vence con la sesión (72 h).
/// Rol es null solo en filas antiguas, de cuando el cliente también "aceptaba" la sesión: esas no son invitaciones.
/// </summary>
public sealed class AceptacionSesion
{
    public long SesionId { get; set; }
    public Guid UsuarioId { get; set; }
    public RolParticipante? Rol { get; set; }
    public EstadoParticipacion Estado { get; set; } = EstadoParticipacion.Pendiente;
    public DateTimeOffset EnviadaEn { get; set; }
    public DateTimeOffset? RespondidaEn { get; set; }

    public Sesion Sesion { get; set; } = null!;
    public Usuario Usuario { get; set; } = null!;
}

public sealed class ComentarioRevision
{
    public long Id { get; set; }
    public long ProyectoId { get; set; }
    public short Ronda { get; set; }
    public Guid AutorId { get; set; }
    public string Comentario { get; set; } = "";
    public DateTimeOffset CreadoEn { get; set; }
}

/// <summary>Trazabilidad del proyecto. Solo se inserta (lo llenan triggers de la base); ActorId null = lo hizo el sistema.</summary>
public sealed class ProyectoHistorial
{
    public long Id { get; set; }
    public long ProyectoId { get; set; }
    public long? SesionId { get; set; }
    public string Evento { get; set; } = "";
    public string? EstadoAnterior { get; set; }
    public string? EstadoNuevo { get; set; }
    public string Detalle { get; set; } = "{}";
    public Guid? ActorId { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
}
