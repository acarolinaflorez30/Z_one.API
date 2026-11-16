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
    decimal? PrecioHora,
    decimal? PrecioSesion,
    IReadOnlyDictionary<string, string?> Detalle,
    IReadOnlyList<PortafolioDto> Portafolio);

/// <summary>Actualización parcial: null = sin cambios; texto vacío = borrar el valor. Precios: null = sin cambios; 0 = quitar el precio (queda sin definir).</summary>
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
    bool? VisiblePublico,
    decimal? PrecioHora = null,
    decimal? PrecioSesion = null);

public sealed record VinculacionRequest(bool Activo);
public sealed record DetalleRequest(Dictionary<string, string?> Campos);
public sealed record PortafolioRequest(string Titulo, string? Descripcion, string? Productora, string? Rol, int? Anio, string? Estado);
public sealed record NotaRequest(string Comentario);
public sealed record NotaDto(long Id, string Comentario, Guid? AutorId, string? AutorNombre, DateTimeOffset CreadoEn);
public sealed record FotoResponse(string FotoUrl);

/// <summary>Los servicios ya no tienen precio: se cobra sala + colaboradores de cada sesión.</summary>
public sealed record ServicioDto(
    long Id, string Codigo, string Nombre, string? Descripcion, string Categoria,
    string? Etapa, short SesionesIncluidas, bool Activo);
public sealed record ServicioRequest(string Nombre, string? Descripcion, bool Activo = true);

/// <summary>
/// Cambio de tarifa de una sala, un servicio (histórico) o un colaborador. Salas y servicios traen RegistroId;
/// colaboradores, RegistroUuid. TipoPrecio: hora, sesion o servicio.
/// </summary>
public sealed record HistorialPrecioDto(
    long Id, string Tabla, long? RegistroId, Guid? RegistroUuid, string TipoPrecio, string Nombre,
    decimal? PrecioAnterior, decimal? PrecioNuevo, DateTimeOffset CambiadoEn);

/// <summary>
/// Colaborador invitable, visto por clientes y administrador (requiere sesión). Nunca incluye el nombre real.
/// Un precio null significa que aún no lo definió y no se le puede invitar a ese tipo de sesión.
/// </summary>
public sealed record ColaboradorCatalogoDto(
    Guid Id,
    string Nombre,
    string Perfil,
    string? Especialidad,
    string Disponibilidad,
    decimal? PrecioHora,
    decimal? PrecioSesion);
