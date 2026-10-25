using System.Net;

namespace ZOne.Api.Domain;

public sealed class Usuario
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public RolUsuario Rol { get; set; } = RolUsuario.Cliente;
    public bool Activo { get; set; } = true;
    public DateTimeOffset CreadoEn { get; set; }
    public string Nombre { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool DebeCambiarPassword { get; set; }
    public short IntentosFallidos { get; set; }
    public DateTimeOffset? BloqueadoHasta { get; set; }
    public DateTimeOffset? UltimoAcceso { get; set; }
    public DateTimeOffset? EmailVerificadoEn { get; set; }
    public DateTimeOffset ActualizadoEn { get; set; }

    public Cliente? Cliente { get; set; }
    public Colaborador? Colaborador { get; set; }
}

public sealed class Cliente
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public TipoCliente Tipo { get; set; } = TipoCliente.Persona;
    public string Nombre { get; set; } = "";
    public string? Documento { get; set; }
    public string? Direccion { get; set; }
    public string? Telefono { get; set; }
    public string? EmailContacto { get; set; }
    public string? DatosFacturacion { get; set; }
    public bool Activo { get; set; } = true;
    public DateTimeOffset CreadoEn { get; set; }

    public Usuario Usuario { get; set; } = null!;
}

public sealed class Colaborador
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public PerfilColaborador Perfil { get; set; }
    public EstadoVinculacion Vinculacion { get; set; } = EstadoVinculacion.Activo;
    public Disponibilidad Disponibilidad { get; set; } = Disponibilidad.Disponible;
    public bool VisiblePublico { get; set; }
    public string? NombreArtistico { get; set; }
    public string? NombreReal { get; set; }
    public DateOnly? FechaNacimiento { get; set; }
    public string? Biografia { get; set; }
    public string? Trayectoria { get; set; }
    public string? Productoras { get; set; }
    public string? Foto { get; set; }
    public DateOnly FechaIngreso { get; set; }
    public DateTimeOffset? DesvinculadoEn { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
    public string? Especialidad { get; set; }
    public string? CiudadOrigen { get; set; }

    public Usuario Usuario { get; set; } = null!;
}

public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid Familia { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreadoEn { get; set; }
    public DateTimeOffset ExpiraEn { get; set; }
    public DateTimeOffset? RevocadoEn { get; set; }
    public Guid? ReemplazadoPor { get; set; }
    public IPAddress? Ip { get; set; }
    public string? UserAgent { get; set; }

    public Usuario Usuario { get; set; } = null!;
}

public sealed class TokenRecuperacion
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public TipoToken Tipo { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreadoEn { get; set; }
    public DateTimeOffset ExpiraEn { get; set; }
    public DateTimeOffset? UsadoEn { get; set; }

    public Usuario Usuario { get; set; } = null!;
}

public sealed class Auditoria
{
    public long Id { get; set; }
    public Guid? UsuarioId { get; set; }
    public string Accion { get; set; } = "";
    public string Entidad { get; set; } = "";
    public string? EntidadId { get; set; }
    public string Detalle { get; set; } = "{}";
    public IPAddress? Ip { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
}
