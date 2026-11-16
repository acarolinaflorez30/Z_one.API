namespace ZOne.Api.Domain;

// El estado se guarda como TEXT en la base, con check. No son enums de PostgreSQL.
public static class EstadosSolicitud
{
    public const string Solicitada = "solicitada";
    public const string PorAutorizar = "por_autorizar";
    public const string PorPagar = "por_pagar";
    public const string Confirmada = "confirmada";
    public const string Rechazada = "rechazada";
    public const string Cancelada = "cancelada";

    public static readonly IReadOnlySet<string> Todos = new HashSet<string>(StringComparer.Ordinal)
    {
        Solicitada, PorAutorizar, PorPagar, Confirmada, Rechazada, Cancelada
    };

    /// <summary>Transiciones permitidas. Los estados terminales no aparecen.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Transiciones =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [Solicitada] = new HashSet<string>(StringComparer.Ordinal)
                { PorAutorizar, Rechazada, Cancelada },
            [PorAutorizar] = new HashSet<string>(StringComparer.Ordinal)
                { PorPagar, Confirmada, Rechazada, Cancelada },
            [PorPagar] = new HashSet<string>(StringComparer.Ordinal)
                { Confirmada, Cancelada },
            [Confirmada] = new HashSet<string>(StringComparer.Ordinal)
                { Cancelada }
        };
}

public static class EstadosInvitacion
{
    public const string Pendiente = "pendiente";
    public const string Aceptada = "aceptada";
    public const string Rechazada = "rechazada";

    public static readonly IReadOnlySet<string> Todos = new HashSet<string>(StringComparer.Ordinal)
    {
        Pendiente, Aceptada, Rechazada
    };
}

public static class TiposSolicitud
{
    public const string Grabacion = "grabacion";
    public const string Mezcla = "mezcla";
    public const string Masterizacion = "masterizacion";
    public const string Ensayo = "ensayo";
    public const string Produccion = "produccion";

    public static readonly IReadOnlySet<string> Todos = new HashSet<string>(StringComparer.Ordinal)
    {
        Grabacion, Mezcla, Masterizacion, Ensayo, Produccion
    };
}

public static class RolesInvitacion
{
    public const string Artista = "artista";
    public const string Productor = "productor";
    public const string Ingeniero = "ingeniero";

    public static readonly IReadOnlySet<string> Todos = new HashSet<string>(StringComparer.Ordinal)
    {
        Artista, Productor, Ingeniero
    };
}

/// <summary>Reserva de sala solicitada por un cliente, con colaboradores invitados.</summary>
public sealed class Solicitud
{
    public long Id { get; set; }
    public Guid ClienteId { get; set; }
    public long? SalaId { get; set; }
    public long? CancionId { get; set; }
    public long? ProyectoId { get; set; }
    public string Titulo { get; set; } = "";
    public string Descripcion { get; set; } = "";
    public string Tipo { get; set; } = TiposSolicitud.Grabacion;
    public DateOnly? FechaDeseada { get; set; }
    public string? Franja { get; set; }
    public List<string>? Partes { get; set; }
    public string Estado { get; set; } = EstadosSolicitud.Solicitada;
    public DateTimeOffset CreadoEn { get; set; }
    public DateTimeOffset ActualizadoEn { get; set; }

    public Cliente Cliente { get; set; } = null!;
    public Sala? Sala { get; set; }
    public Cancion? Cancion { get; set; }
    public Proyecto? Proyecto { get; set; }
    public List<SolicitudColaborador> Colaboradores { get; set; } = [];
}

/// <summary>Invitación de un colaborador a una solicitud. PK compuesta.</summary>
public sealed class SolicitudColaborador
{
    public long SolicitudId { get; set; }
    public Guid ColaboradorId { get; set; }
    public string? Rol { get; set; }
    public string Estado { get; set; } = EstadosInvitacion.Pendiente;
    public DateTimeOffset CreadoEn { get; set; }

    public Solicitud Solicitud { get; set; } = null!;
    public Colaborador Colaborador { get; set; } = null!;
}
