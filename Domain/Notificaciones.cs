namespace ZOne.Api.Domain;

/// <summary>Aviso dentro del sistema para una persona (sesión cumplida, revisión pendiente, ajustes pedidos...).</summary>
public sealed class Notificacion
{
    public long Id { get; set; }
    public Guid UsuarioId { get; set; }
    public string Tipo { get; set; } = "";
    public string Titulo { get; set; } = "";
    public string Mensaje { get; set; } = "";
    public long? ProyectoId { get; set; }
    public long? SesionId { get; set; }
    public DateTimeOffset? LeidaEn { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
}

/// <summary>
/// Correo por enviar. Nace en la misma transacción que el aviso que lo origina; un proceso de la API lo envía y reintenta.
/// Estado: pendiente | enviado | omitido (la persona apagó los correos o ya no está activa) | fallido (agotó los intentos).
/// </summary>
public sealed class CorreoPendiente
{
    public long Id { get; set; }
    public Guid UsuarioId { get; set; }
    public string Tipo { get; set; } = "";
    public string Asunto { get; set; } = "";
    public string Mensaje { get; set; } = "";
    public long? ProyectoId { get; set; }
    public long? SesionId { get; set; }
    public string Estado { get; set; } = "pendiente";
    public int Intentos { get; set; }
    public DateTimeOffset ProximoIntento { get; set; }
    public string? UltimoError { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
    public DateTimeOffset? EnviadoEn { get; set; }
}

/// <summary>Registro de un recordatorio o aviso automático ya enviado. La clave ("tipo:id:usuario") evita enviarlo dos veces.</summary>
public sealed class AvisoEnviado
{
    public string Clave { get; set; } = "";
    public DateTimeOffset CreadoEn { get; set; }
}
