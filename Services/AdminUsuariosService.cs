using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Options;
using ZOne.Api.Security;

namespace ZOne.Api.Services;

public interface IAdminUsuariosService
{
    Task<CredencialTemporalResponse> CrearColaboradorAsync(CrearColaboradorRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task<CredencialTemporalResponse> RestablecerTemporalAsync(Guid usuarioId, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task DesbloquearAsync(Guid usuarioId, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task CambiarActivoAsync(Guid usuarioId, bool activo, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task<PaginaDto<UsuarioListaDto>> ListarAsync(string? rol, string? q, int pagina, int tamano, CancellationToken ct);
    Task CrearPrimerAdministradorAsync(string nombre, string email, string password, CancellationToken ct);
}

public sealed class AdminUsuariosService(
    ZOneDbContext db,
    IPasswordHasher hasher,
    IAuditoriaService auditoria,
    IOptions<AuthOptions> authOpt,
    TimeProvider time) : IAdminUsuariosService
{
    private const string AvisoTemporal =
        "Esta contraseña se muestra una sola vez. Entrégala de forma segura: la persona deberá cambiarla en su primer ingreso.";

    // ───────────────────────── Colaboradores (los crea el administrador) ─────────────────────────

    public async Task<CredencialTemporalResponse> CrearColaboradorAsync(
        CrearColaboradorRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        var nombre = Entrada.Texto(r.Nombre, "nombre", 2, 120);
        var email = Entrada.Email(r.Email);

        if (!Enum.TryParse<PerfilColaborador>((r.Perfil ?? "").Replace("_", ""), true, out var perfil) || !Enum.IsDefined(perfil))
            throw ApiException.Validacion("perfil", "El perfil debe ser artista, productor, ingeniero_sonido, ingeniero_mezcla, ingeniero_master u otro.");

        if (r.FechaNacimiento is { } nac && (nac > DateOnly.FromDateTime(DateTime.UtcNow) || nac.Year < 1900))
            throw ApiException.Validacion("fechaNacimiento", "La fecha de nacimiento no es válida.");

        if (await db.Usuarios.AnyAsync(u => u.Email == email, ct))
            throw ApiException.Conflicto("email_en_uso", "Ya existe una cuenta con ese correo.");

        var ahora = time.GetUtcNow();
        var temporal = PasswordPolicy.GenerarTemporal();

        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            Email = email,
            Rol = RolUsuario.Colaborador,
            Activo = true,
            Nombre = nombre,
            PasswordHash = hasher.Hash(temporal),
            DebeCambiarPassword = true,
            CreadoEn = ahora,
            ActualizadoEn = ahora
        };
        var colaborador = new Colaborador
        {
            Id = Guid.NewGuid(),
            UsuarioId = usuario.Id,
            Perfil = perfil,
            Vinculacion = EstadoVinculacion.Activo,
            Disponibilidad = Disponibilidad.Disponible,
            VisiblePublico = false,     // el administrador decide cuándo publicarlo
            NombreArtistico = Entrada.TextoOpcional(r.NombreArtistico, "nombreArtistico", 120),
            NombreReal = Entrada.TextoOpcional(r.NombreReal, "nombreReal", 120),
            FechaNacimiento = r.FechaNacimiento,
            Especialidad = Entrada.TextoOpcional(r.Especialidad, "especialidad", 120),
            CiudadOrigen = Entrada.TextoOpcional(r.CiudadOrigen, "ciudadOrigen", 120),
            Biografia = Entrada.TextoOpcional(r.Biografia, "biografia", 4000),
            FechaIngreso = DateOnly.FromDateTime(ahora.UtcDateTime.AddHours(-5)),   // Colombia (UTC-5, sin horario de verano)
            CreadoEn = ahora
        };

        db.Usuarios.Add(usuario);
        db.Colaboradores.Add(colaborador);
        // Nunca se audita la contraseña temporal.
        auditoria.Registrar(adminId, "colaborador_creado", "usuario", usuario.Id.ToString(),
            new { perfil = perfil.ToString(), colaboradorId = colaborador.Id }, ctx.Ip);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.EsViolacionUnica())
        {
            throw ApiException.Conflicto("email_en_uso", "Ya existe una cuenta con ese correo.");
        }

        return new CredencialTemporalResponse(usuario.Id, colaborador.Id, email, temporal, AvisoTemporal);
    }

    public async Task<CredencialTemporalResponse> RestablecerTemporalAsync(
        Guid usuarioId, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        if (usuarioId == adminId)
            throw ApiException.Prohibido("Para cambiar tu propia contraseña usa la opción de tu perfil.");

        var usuario = await db.Usuarios.Include(u => u.Colaborador).FirstOrDefaultAsync(u => u.Id == usuarioId, ct)
                      ?? throw ApiException.NoEncontrado("No existe ese usuario.");

        var ahora = time.GetUtcNow();
        var temporal = PasswordPolicy.GenerarTemporal();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        usuario.PasswordHash = hasher.Hash(temporal);
        usuario.DebeCambiarPassword = true;
        usuario.IntentosFallidos = 0;
        usuario.BloqueadoHasta = null;
        await db.RevocarRefreshTokensAsync(usuario.Id, ahora, ct);
        auditoria.Registrar(adminId, "password_temporal_generada", "usuario", usuario.Id.ToString(), null, ctx.Ip);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new CredencialTemporalResponse(usuario.Id, usuario.Colaborador?.Id, usuario.Email, temporal, AvisoTemporal);
    }

    public async Task DesbloquearAsync(Guid usuarioId, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId, ct)
                      ?? throw ApiException.NoEncontrado("No existe ese usuario.");

        usuario.IntentosFallidos = 0;
        usuario.BloqueadoHasta = null;
        auditoria.Registrar(adminId, "usuario_desbloqueado", "usuario", usuario.Id.ToString(), null, ctx.Ip);
        await db.SaveChangesAsync(ct);
    }

    public async Task CambiarActivoAsync(Guid usuarioId, bool activo, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        if (usuarioId == adminId)
            throw ApiException.Prohibido("No puedes desactivar tu propia cuenta.");

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId, ct)
                      ?? throw ApiException.NoEncontrado("No existe ese usuario.");

        var ahora = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        usuario.Activo = activo;
        if (!activo) await db.RevocarRefreshTokensAsync(usuario.Id, ahora, ct);
        auditoria.Registrar(adminId, activo ? "usuario_activado" : "usuario_desactivado", "usuario", usuario.Id.ToString(), null, ctx.Ip);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ───────────────────────── Listado ─────────────────────────

    public async Task<PaginaDto<UsuarioListaDto>> ListarAsync(string? rol, string? q, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);

        var consulta = db.Usuarios.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(rol))
        {
            if (!Enum.TryParse<RolUsuario>(rol, true, out var rolFiltro) || !Enum.IsDefined(rolFiltro))
                throw ApiException.Validacion("rol", "El rol debe ser administrador, cliente o colaborador.");
            consulta = consulta.Where(u => u.Rol == rolFiltro);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var patron = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            consulta = consulta.Where(u => EF.Functions.ILike(u.Email, patron) || EF.Functions.ILike(u.Nombre, patron));
        }

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .OrderBy(u => u.Nombre).ThenBy(u => u.Id)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .Select(u => new { u.Id, u.Email, u.Nombre, u.Rol, u.Activo, u.DebeCambiarPassword, u.BloqueadoHasta, u.UltimoAcceso, u.CreadoEn })
            .ToListAsync(ct);

        var items = filas.Select(f => new UsuarioListaDto(
            f.Id, f.Email, f.Nombre, f.Rol.ToString().ToLowerInvariant(),
            f.Activo, f.DebeCambiarPassword, f.BloqueadoHasta, f.UltimoAcceso, f.CreadoEn)).ToList();

        return new PaginaDto<UsuarioListaDto>(items, total, pagina, tamano);
    }

    // ───────────────────────── Primer administrador (script seguro) ─────────────────────────

    public async Task CrearPrimerAdministradorAsync(string nombre, string email, string password, CancellationToken ct)
    {
        if (await db.Usuarios.AnyAsync(u => u.Rol == RolUsuario.Administrador, ct))
            throw ApiException.Conflicto("admin_existente", "Ya existe un administrador. Este comando solo crea el primero.");

        nombre = Entrada.Texto(nombre, "nombre", 2, 120);
        email = Entrada.Email(email);

        var errores = PasswordPolicy.Validar(password, email, nombre, Math.Max(authOpt.Value.MinPasswordLength, 12));
        if (errores.Count > 0) throw ApiException.Validacion("password", errores);

        var ahora = time.GetUtcNow();
        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            Email = email,
            Rol = RolUsuario.Administrador,
            Activo = true,
            Nombre = nombre,
            PasswordHash = hasher.Hash(password),
            DebeCambiarPassword = false,
            CreadoEn = ahora,
            ActualizadoEn = ahora
        };
        db.Usuarios.Add(usuario);
        auditoria.Registrar(null, "primer_admin_creado", "usuario", usuario.Id.ToString(), new { email }, IPAddress.Loopback);
        await db.SaveChangesAsync(ct);
    }
}
