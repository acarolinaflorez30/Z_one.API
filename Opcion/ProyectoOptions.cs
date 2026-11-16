namespace ZOne.Api.Options;

/// <summary>Plazos y límites de los proyectos y de sus sesiones. Se ajustan en appsettings sin tocar código.</summary>
public sealed class ProyectoOptions
{
    public const string Section = "Proyecto";

    /// <summary>Plazo para que los colaboradores respondan la invitación de una sesión (retiene la sala). Decidido: 72 h.</summary>
    public int SesionAceptacionHoras { get; set; } = 72;

    /// <summary>Plazo para registrar el pago de una sesión (retiene la sala). Decidido: 24 h.</summary>
    public int PagoServicioHoras { get; set; } = 24;

    /// <summary>Si es true, la retención termina al empezar la sesión cuando eso ocurre antes del plazo.</summary>
    public bool LiberarAlInicio { get; set; } = true;

    /// <summary>Plazo del cliente para aprobar el trabajo de una canción completa. Vencido, decide el administrador.</summary>
    public int RevisionClienteHoras { get; set; } = 24;

    /// <summary>Duración fija de una sesión de grabación del proyecto (servicio parcial, las 4 de la canción y el ajuste incluido). Decidido: 3 h.</summary>
    public int SesionDuracionMinutos { get; set; } = 180;

    /// <summary>Duración mínima de una sesión extra (sin máximo). Decidido: 1 h.</summary>
    public int ExtraMinMinutos { get; set; } = 60;

    public int GranularidadMinutos { get; set; } = 30;
    public int MinAnticipacionMinutos { get; set; } = 60;
    public int MaxDiasAnticipacion { get; set; } = 180;

    /// <summary>Tope de proyectos sin confirmar por cliente.</summary>
    public int MaxProyectosPendientesPorCliente { get; set; } = 5;

    /// <summary>Máximo de colaboradores invitados a una misma sesión.</summary>
    public int MaxInvitadosPorSesion { get; set; } = 10;

    /// <summary>Diferencia horaria local respecto a UTC (Colombia: -5). Se usa para mostrar horas locales en los avisos.</summary>
    public int UtcOffsetHoras { get; set; } = -5;
}
