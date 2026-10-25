namespace ZOne.Api.Domain;

// Coinciden con los enums de PostgreSQL: CancionCompleta -> cancion_completa, EnCurso -> en_curso, etc.
public enum TipoProyecto { CancionCompleta, ServicioParcial }
public enum EstadoProyecto { Pendiente, Confirmado, EnCurso, EnRevision, Finalizado, Cancelado }
public enum EstadoParticipacion { Pendiente, Aceptada, Rechazada, Expirada }
public enum RolParticipante { Artista, Productor }

/// <summary>Una canción completa o un servicio parcial que contrata un cliente.</summary>
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

    /// <summary>Precio que vio el cliente al pedir el proyecto. No cambia aunque la tarifa se modifique después.</summary>
    public decimal PrecioAcordado { get; set; }

    public short RondaRevision { get; set; }
    public DateTimeOffset? AprobadoEn { get; set; }
    public DateTimeOffset? CanceladoEn { get; set; }
    public Guid? CreadoPor { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
    public DateTimeOffset ActualizadoEn { get; set; }

    public Cliente Cliente { get; set; } = null!;
    public Servicio Servicio { get; set; } = null!;
    public List<ParticipanteProyecto> Participantes { get; set; } = [];
}

/// <summary>Solicitud a un colaborador para participar en un proyecto. Vence a las 72 horas.</summary>
public sealed class ParticipanteProyecto
{
    public long Id { get; set; }
    public long ProyectoId { get; set; }
    public Guid ColaboradorId { get; set; }
    public RolParticipante Rol { get; set; }
    public EstadoParticipacion Estado { get; set; } = EstadoParticipacion.Pendiente;
    public DateTimeOffset EnviadaEn { get; set; }
    public DateTimeOffset VenceEn { get; set; }
    public DateTimeOffset? RespondidaEn { get; set; }

    public Proyecto Proyecto { get; set; } = null!;
    public Colaborador Colaborador { get; set; } = null!;
}

/// <summary>Quién debe aceptar una sesión de proyecto y si ya lo hizo.</summary>
public sealed class AceptacionSesion
{
    public long SesionId { get; set; }
    public Guid UsuarioId { get; set; }
    public EstadoParticipacion Estado { get; set; } = EstadoParticipacion.Pendiente;
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
