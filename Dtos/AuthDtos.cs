namespace ZOne.Api.Dtos;

public sealed record RegistroRequest(string Nombre, string Email, string Password, string? Tipo, string? Telefono);
public sealed record LoginRequest(string Email, string Password);
public sealed record CambiarPasswordRequest(string PasswordActual, string PasswordNueva);
public sealed record OlvidePasswordRequest(string Email);
public sealed record RestablecerPasswordRequest(string Token, string PasswordNueva);

public sealed record UsuarioDto(
    Guid Id,
    string Email,
    string Nombre,
    string Rol,
    bool DebeCambiarPassword,
    IReadOnlyList<string> Permisos,
    Guid? ClienteId,
    Guid? ColaboradorId,
    string? PerfilColaborador);

public sealed record SesionResponse(UsuarioDto Usuario, DateTimeOffset AccesoExpiraEn);

// Administración
public sealed record CrearColaboradorRequest(
    string Nombre,
    string Email,
    string Perfil,
    string? NombreArtistico,
    string? NombreReal,
    DateOnly? FechaNacimiento,
    string? Especialidad,
    string? CiudadOrigen,
    string? Biografia);

public sealed record CredencialTemporalResponse(
    Guid UsuarioId,
    Guid? ColaboradorId,
    string Email,
    string PasswordTemporal,
    string Aviso);

public sealed record ActivoRequest(bool Activo);

public sealed record UsuarioListaDto(
    Guid Id,
    string Email,
    string Nombre,
    string Rol,
    bool Activo,
    bool DebeCambiarPassword,
    DateTimeOffset? BloqueadoHasta,
    DateTimeOffset? UltimoAcceso,
    DateTimeOffset CreadoEn);

public sealed record PaginaDto<T>(IReadOnlyList<T> Items, int Total, int Pagina, int Tamano);
