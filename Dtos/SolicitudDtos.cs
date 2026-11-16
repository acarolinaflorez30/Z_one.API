namespace ZOne.Api.Dtos;

public sealed record InvitacionRequest(Guid ColaboradorId, string? Rol);

/// <summary>ClienteId solo lo usa el administrador. Sala, fecha y franja son obligatorios.</summary>
public sealed record CrearSolicitudRequest(
    string? Titulo, string? Descripcion, string? Tipo,
    DateOnly? FechaDeseada, string? Franja, List<string>? Partes,
    long? SalaId, long? CancionId, long? ProyectoId,
    List<InvitacionRequest>? Colaboradores, Guid? ClienteId);

public sealed record CambiarEstadoSolicitudRequest(string? Estado);

public sealed record ResponderInvitacionRequest(string? Estado);

public sealed record SolicitudColaboradorDto(
    Guid ColaboradorId, string Nombre, string Perfil, string? Rol,
    string Estado, DateTimeOffset CreadoEn);

public sealed record SolicitudDto(
    long Id, Guid ClienteId, string Cliente,
    long? SalaId, string? Sala,
    long? CancionId, string? Cancion,
    long? ProyectoId, string? Proyecto,
    string Titulo, string? Descripcion, string Tipo,
    DateOnly? FechaDeseada, string? Franja,
    IReadOnlyList<string> Partes, string Estado,
    DateTimeOffset CreadoEn, DateTimeOffset ActualizadoEn,
    IReadOnlyList<SolicitudColaboradorDto> Colaboradores);

public sealed record SolicitudResumenDto(
    long Id, Guid ClienteId, string Cliente,
    long? SalaId, string? Sala, string Titulo, string Tipo,
    DateOnly? FechaDeseada, string? Franja, string Estado,
    int Invitados, int Aceptados,
    DateTimeOffset CreadoEn, DateTimeOffset ActualizadoEn);
