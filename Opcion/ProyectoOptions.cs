namespace ZOne.Api.Options;

/// <summary>Plazos y límites de los proyectos. Los valores salen de las decisiones del plan; se ajustan sin tocar código.</summary>
public sealed class ProyectoOptions
{
    public const string Section = "Proyecto";

    /// <summary>Plazo de respuesta de un colaborador a una solicitud (decidido: 72 h).</summary>
    public int ParticipacionHoras { get; set; } = 72;

    /// <summary>Plazo para que todos acepten una sesión de proyecto (retiene la sala).</summary>
    public int SesionAceptacionHoras { get; set; } = 72;

    /// <summary>Plazo para registrar el pago del servicio o de una sesión extra (decidido: 24 h).</summary>
    public int PagoServicioHoras { get; set; } = 24;

    /// <summary>Si es true, la retención de una sesión termina al empezar la sesión si ocurre antes del plazo.</summary>
    public bool LiberarAlInicio { get; set; } = true;

    /// <summary>Plazo del cliente para aprobar el trabajo de una canción completa (decidido: 24 h). Vencido, decide el administrador.</summary>
    public int RevisionClienteHoras { get; set; } = 24;

    public int SesionMinMinutos { get; set; } = 60;
    public int SesionMaxMinutos { get; set; } = 480;
    public int GranularidadMinutos { get; set; } = 30;
    public int MinAnticipacionMinutos { get; set; } = 60;
    public int MaxDiasAnticipacion { get; set; } = 180;

    /// <summary>Tope de proyectos sin confirmar por cliente, para no saturar de solicitudes a los colaboradores.</summary>
    public int MaxProyectosPendientesPorCliente { get; set; } = 5;

    /// <summary>Diferencia horaria local respecto a UTC (Colombia: -5). Se usa para mostrar horas locales en los avisos. Ya no existe la regla de cumplir la sesión el mismo día.</summary>
    public int UtcOffsetHoras { get; set; } = -5;
}
