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
