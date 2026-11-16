namespace ZOne.Api.Options;

/// <summary>Recordatorios y avisos de vencimiento automáticos. Se ajustan en appsettings (sección "Avisos") sin tocar código.</summary>
public sealed class AvisosOptions
{
    public const string Section = "Avisos";

    /// <summary>false = no se generan recordatorios ni avisos de vencimiento (los demás avisos siguen funcionando).</summary>
    public bool Habilitado { get; set; } = true;

    public int IntervaloMinutos { get; set; } = 5;

    /// <summary>Se recuerda la invitación pendiente cuando faltan estas horas o menos para que venza.</summary>
    public int RecordatorioInvitacionHoras { get; set; } = 24;

    /// <summary>Se recuerda el pago pendiente cuando faltan estas horas o menos.</summary>
    public int RecordatorioPagoHoras { get; set; } = 6;

    /// <summary>Se recuerda la sesión confirmada cuando faltan estas horas o menos para que empiece.</summary>
    public int RecordatorioSesionHoras { get; set; } = 24;

    /// <summary>Se recuerda al cliente la revisión de su proyecto cuando faltan estas horas o menos.</summary>
    public int RecordatorioRevisionHoras { get; set; } = 6;

    /// <summary>Una sesión confirmada que terminó hace más de estas horas y no se ha cerrado se avisa al administrador.</summary>
    public int SesionSinCerrarHoras { get; set; } = 2;

    /// <summary>Tope de avisos por ciclo (el resto sale en el siguiente), para no saturar el correo.</summary>
    public int MaxPorCiclo { get; set; } = 200;

    /// <summary>Días que se conserva el registro de avisos ya enviados. Debe ser mayor a 14.</summary>
    public int RetencionDias { get; set; } = 30;
}
