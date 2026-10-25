namespace ZOne.Api.Dtos;

public sealed record PortafolioDto(long Id, string Titulo, string? Descripcion, string? Productora, string? Rol, int? Anio, string Estado);

/// <summary>Vista pública. NUNCA incluye nombre real, fecha de nacimiento, correo ni notas internas.</summary>
public sealed record ColaboradorPublicoDto(
    Guid Id,
    string NombreArtistico,
    string Perfil,
    string? Especialidad,
    string? CiudadOrigen,
    string? Biografia,
    string? Trayectoria,
    string? Productoras,
    string? FotoUrl,
    string Disponibilidad,
    IReadOnlyDictionary<string, string?>? Detalle,
    IReadOnlyList<PortafolioDto>? Portafolio);

public sealed record ColaboradorResumenDto(
    Guid Id,
    string? NombreArtistico,
    string? NombreReal,
    string Email,
    string Perfil,
    string Vinculacion,
    string Disponibilidad,
    bool VisiblePublico,
    string? FotoUrl,
    DateOnly FechaIngreso);

/// <summary>Vista privada: solo para el administrador y el propio colaborador.</summary>
public sealed record ColaboradorDto(
    Guid Id,
    Guid UsuarioId,
    string Email,
    string Perfil,
    string Vinculacion,
    string Disponibilidad,
    bool VisiblePublico,
    string? NombreArtistico,
    string? NombreReal,
    DateOnly? FechaNacimiento,
    string? Biografia,
    string? Trayectoria,
    string? Productoras,
    string? Especialidad,
    string? CiudadOrigen,
    string? FotoUrl,
    DateOnly FechaIngreso,
    DateTimeOffset? DesvinculadoEn,
    IReadOnlyDictionary<string, string?> Detalle,
    IReadOnlyList<PortafolioDto> Portafolio);

/// <summary>Actualización parcial: null = sin cambios; texto vacío = borrar el valor.</summary>
public sealed record ActualizarColaboradorRequest(
    string? NombreArtistico,
    string? NombreReal,
    DateOnly? FechaNacimiento,
    string? Biografia,
    string? Trayectoria,
    string? Productoras,
    string? Especialidad,
    string? CiudadOrigen,
    string? Disponibilidad,
    bool? VisiblePublico);

public sealed record VinculacionRequest(bool Activo);
public sealed record DetalleRequest(Dictionary<string, string?> Campos);
public sealed record PortafolioRequest(string Titulo, string? Descripcion, string? Productora, string? Rol, int? Anio, string? Estado);
public sealed record NotaRequest(string Comentario);
public sealed record NotaDto(long Id, string Comentario, Guid? AutorId, string? AutorNombre, DateTimeOffset CreadoEn);
public sealed record FotoResponse(string FotoUrl);

public sealed record SalaDto(long Id, string Nombre, decimal PrecioHora, bool Activo);
public sealed record SalaRequest(string Nombre, decimal PrecioHora, bool Activo = true);

public sealed record ServicioDto(
    long Id, string Codigo, string Nombre, string? Descripcion, string Categoria,
    string? Etapa, short SesionesIncluidas, decimal? Precio, bool Activo);
public sealed record ServicioRequest(string Nombre, string? Descripcion, decimal? Precio, bool Activo = true);
