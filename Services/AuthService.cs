using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Options;
using ZOne.Api.Security;

namespace ZOne.Api.Services;

public sealed record ResultadoSesion(
    UsuarioDto Usuario,
    string AccessToken,
    DateTimeOffset AccessExpiraEn,
    string RefreshToken,
    DateTimeOffset RefreshExpiraEn);

public interface IAuthService
{
    Task<ResultadoSesion> RegistrarClienteAsync(RegistroRequest r, ContextoPeticion ctx, CancellationToken ct);
    Task<ResultadoSesion> LoginAsync(LoginRequest r, ContextoPeticion ctx, CancellationToken ct);
    Task<ResultadoSesion> RefrescarAsync(string? refreshToken, ContextoPeticion ctx, CancellationToken ct);
    Task LogoutAsync(string? refreshToken, CancellationToken ct);
    Task LogoutTodasAsync(Guid usuarioId, CancellationToken ct);
    Task<UsuarioDto> ObtenerPerfilAsync(Guid usuarioId, CancellationToken ct);
    Task<ResultadoSesion> CambiarPasswordAsync(Guid usuarioId, CambiarPasswordRequest r, ContextoPeticion ctx, CancellationToken ct);
    Task OlvidePasswordAsync(OlvidePasswordRequest r, ContextoPeticion ctx, CancellationToken ct);
    Task RestablecerPasswordAsync(RestablecerPasswordRequest r, ContextoPeticion ctx, CancellationToken ct);
}

public sealed class AuthService(
    ZOneDbContext db,
    IPasswordHasher hasher,
    ITokenService tokens,
    IAuditoriaService auditoria,
    IEmailSender correo,
    IOptions<AuthOptions> authOpt,
    IOptions<JwtOptions> jwtOpt,
    TimeProvider time) : IAuthService
{
    private readonly AuthOptions _auth = authOpt.Value;
    private readonly JwtOptions _jwt = jwtOpt.Value;

    // ───────────────────────── Registro (solo clientes) ─────────────────────────

    public async Task<ResultadoSesion> RegistrarClienteAsync(RegistroRequest r, ContextoPeticion ctx, CancellationToken ct)
    {
        var nombre = Entrada.Texto(r.Nombre, "nombre", 2, 120);
        var email = Entrada.Email(r.Email);
        var telefono = Entrada.TextoOpcional(r.Telefono, "telefono", 30);

        var errores = PasswordPolicy.Validar(r.Password, email, nombre, _auth.MinPasswordLength);
        if (errores.Count > 0) throw ApiException.Validacion("password", errores);

        var tipo = TipoCliente.Persona;
        if (!string.IsNullOrWhiteSpace(r.Tipo) && (!Enum.TryParse(r.Tipo.Trim(), true, out tipo) || !Enum.IsDefined(tipo)))
            throw ApiException.Validacion("tipo", "El tipo debe ser persona o empresa.");

        if (await db.Usuarios.AnyAsync(u => u.Email == email, ct))
            throw ApiException.Conflicto("email_en_uso", "Ya existe una cuenta con ese correo.");

        var ahora = time.GetUtcNow();
        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            Email = email,
            Rol = RolUsuario.Cliente,          // el rol nunca viene del cliente
            Activo = true,
            Nombre = nombre,
            PasswordHash = hasher.Hash(r.Password),
            CreadoEn = ahora,
            ActualizadoEn = ahora,
            UltimoAcceso = ahora
        };
        usuario.Cliente = new Cliente
        {
            Id = Guid.NewGuid(),
            UsuarioId = usuario.Id,
            Tipo = tipo,
            Nombre = nombre,
            Telefono = telefono,
            EmailContacto = email,
            Activo = true,
            CreadoEn = ahora
        };

        db.Usuarios.Add(usuario);
        var sesion = Emitir(usuario, null, ctx, ahora).Sesion;
        auditoria.Registrar(usuario.Id, "registro_cliente", "usuario", usuario.Id.ToString(), null, ctx.Ip);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.EsViolacionUnica())
        {
            throw ApiException.Conflicto("email_en_uso", "Ya existe una cuenta con ese correo.");
        }

        return sesion;
    }

    // ───────────────────────── Login con bloqueo temporal ─────────────────────────

    public async Task<ResultadoSesion> LoginAsync(LoginRequest r, ContextoPeticion ctx, CancellationToken ct)
    {
        var ahora = time.GetUtcNow();
        var email = Entrada.EmailSuave(r.Email);
        var password = r.Password ?? "";
        if (password.Length > PasswordPolicy.MaxLength) password = "";   // evita gastar CPU con entradas enormes

        var usuario = email is null
            ? null
            : await db.Usuarios
                .Include(u => u.Cliente)
                .Include(u => u.Colaborador)
                .FirstOrDefaultAsync(u => u.Email == email, ct);

        if (usuario is null)
        {
            hasher.Verify(password, hasher.HashFicticio);   // mismo costo que un usuario real
            throw ApiException.CredencialesInvalidas();
        }

        var verificacion = hasher.Verify(password, usuario.PasswordHash);
        var bloqueado = usuario.BloqueadoHasta is { } hasta && hasta > ahora;

        if (!usuario.Activo || bloqueado)
        {
            auditoria.Registrar(usuario.Id, "login_rechazado", "usuario", usuario.Id.ToString(),
                new { motivo = bloqueado ? "bloqueada" : "inactiva" }, ctx.Ip);
            await db.SaveChangesAsync(ct);
            throw ApiException.CredencialesInvalidas();
        }

        if (!verificacion.Ok)
        {
            await RegistrarFalloAsync(usuario, ctx, ahora, ct);
            throw ApiException.CredencialesInvalidas();
        }

        if (verificacion.NecesitaRehash) usuario.PasswordHash = hasher.Hash(password);
        usuario.IntentosFallidos = 0;
        usuario.BloqueadoHasta = null;
        usuario.UltimoAcceso = ahora;

        var sesion = Emitir(usuario, null, ctx, ahora).Sesion;
        auditoria.Registrar(usuario.Id, "login", "usuario", usuario.Id.ToString(), null, ctx.Ip);
        await db.SaveChangesAsync(ct);
        return sesion;
    }

    /// <summary>Incrementa el contador de forma atómica en la base y bloquea al llegar al límite.</summary>
    private async Task RegistrarFalloAsync(Usuario usuario, ContextoPeticion ctx, DateTimeOffset ahora, CancellationToken ct)
    {
        // Si el bloqueo anterior ya venció, el conteo empieza de cero.
        if (usuario.BloqueadoHasta is { } vencido && vencido <= ahora)
        {
            await db.Usuarios.Where(u => u.Id == usuario.Id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IntentosFallidos, (short)0)
                .SetProperty(u => u.BloqueadoHasta, (DateTimeOffset?)null), ct);
        }

        await db.Usuarios.Where(u => u.Id == usuario.Id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.IntentosFallidos, u => (short)(u.IntentosFallidos + 1)), ct);

        var intentos = await db.Usuarios.AsNoTracking()
            .Where(u => u.Id == usuario.Id).Select(u => u.IntentosFallidos).SingleAsync(ct);

        auditoria.Registrar(usuario.Id, "login_fallido", "usuario", usuario.Id.ToString(), new { intentos }, ctx.Ip);

        if (intentos >= _auth.MaxFailedAttempts)
        {
            var hasta = ahora.AddMinutes(_auth.LockoutMinutes);
            await db.Usuarios.Where(u => u.Id == usuario.Id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.BloqueadoHasta, (DateTimeOffset?)hasta), ct);
            auditoria.Registrar(usuario.Id, "cuenta_bloqueada", "usuario", usuario.Id.ToString(),
                new { hasta, minutos = _auth.LockoutMinutes }, ctx.Ip);
        }

        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────── Refresh con rotación y detección de reutilización ─────────────────────────

    public async Task<ResultadoSesion> RefrescarAsync(string? refreshToken, ContextoPeticion ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw ApiException.NoAutenticado();

        var ahora = time.GetUtcNow();
        var hash = TokenHash.Of(refreshToken);

        var actual = await db.RefreshTokens
            .Include(t => t.Usuario).ThenInclude(u => u.Cliente)
            .Include(t => t.Usuario).ThenInclude(u => u.Colaborador)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (actual is null) throw ApiException.NoAutenticado();

        if (actual.RevocadoEn is { } revocado)
        {
            // Un token ya rotado se volvió a usar: posible robo. Se anula toda la familia,
            // salvo una ventana de 10 s para dos pestañas que refrescan casi al mismo tiempo.
            if (actual.ReemplazadoPor is not null && ahora - revocado > TimeSpan.FromSeconds(10))
            {
                await db.RefreshTokens
                    .Where(t => t.Familia == actual.Familia && t.RevocadoEn == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevocadoEn, (DateTimeOffset?)ahora), ct);
                auditoria.Registrar(actual.UsuarioId, "refresh_reutilizado", "usuario", actual.UsuarioId.ToString(),
                    new { familia = actual.Familia }, ctx.Ip);
                await db.SaveChangesAsync(ct);
            }
            throw ApiException.NoAutenticado();
        }

        if (actual.ExpiraEn <= ahora) throw ApiException.NoAutenticado("La sesión venció. Inicia sesión de nuevo.");

        if (!actual.Usuario.Activo)
        {
            await db.RevocarRefreshTokensAsync(actual.UsuarioId, ahora, ct);
            throw ApiException.NoAutenticado();
        }

        var (sesion, nuevo) = Emitir(actual.Usuario, actual.Familia, ctx, ahora);
        actual.RevocadoEn = ahora;
        actual.ReemplazadoPor = nuevo.Id;
        await db.SaveChangesAsync(ct);
        return sesion;
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        var hash = TokenHash.Of(refreshToken);
        var ahora = time.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevocadoEn == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevocadoEn, (DateTimeOffset?)ahora), ct);
    }

    public Task LogoutTodasAsync(Guid usuarioId, CancellationToken ct) =>
        db.RevocarRefreshTokensAsync(usuarioId, time.GetUtcNow(), ct);

    // ───────────────────────── Perfil (/auth/me) ─────────────────────────

    public async Task<UsuarioDto> ObtenerPerfilAsync(Guid usuarioId, CancellationToken ct)
    {
        var u = await db.Usuarios.AsNoTracking()
            .Include(x => x.Cliente)
            .Include(x => x.Colaborador)
            .FirstOrDefaultAsync(x => x.Id == usuarioId, ct);

        if (u is null || !u.Activo) throw ApiException.NoAutenticado();
        return Mapear(u);
    }

    // ───────────────────────── Cambio y recuperación de contraseña ─────────────────────────

    public async Task<ResultadoSesion> CambiarPasswordAsync(Guid usuarioId, CambiarPasswordRequest r, ContextoPeticion ctx, CancellationToken ct)
    {
        var ahora = time.GetUtcNow();
        var usuario = await db.Usuarios
            .Include(u => u.Cliente)
            .Include(u => u.Colaborador)
            .FirstOrDefaultAsync(u => u.Id == usuarioId, ct);

        if (usuario is null || !usuario.Activo) throw ApiException.NoAutenticado();

        var actual = r.PasswordActual ?? "";
        if (actual.Length > PasswordPolicy.MaxLength || !hasher.Verify(actual, usuario.PasswordHash).Ok)
        {
            // Cuenta como intento fallido: una sesión robada no sirve para adivinar la contraseña.
            await RegistrarFalloAsync(usuario, ctx, ahora, ct);
            throw ApiException.Validacion("passwordActual", "La contraseña actual no es correcta.");
        }

        var errores = PasswordPolicy.Validar(r.PasswordNueva, usuario.Email, usuario.Nombre, _auth.MinPasswordLength);
        if (r.PasswordNueva == actual) errores.Add("La nueva contraseña debe ser distinta de la actual.");
        if (errores.Count > 0) throw ApiException.Validacion("passwordNueva", errores);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        usuario.PasswordHash = hasher.Hash(r.PasswordNueva);
        usuario.DebeCambiarPassword = false;
        usuario.IntentosFallidos = 0;
        usuario.BloqueadoHasta = null;

        await db.RevocarRefreshTokensAsync(usuario.Id, ahora, ct);   // cierra las demás sesiones
        var sesion = Emitir(usuario, null, ctx, ahora).Sesion;
        auditoria.Registrar(usuario.Id, "cambio_password", "usuario", usuario.Id.ToString(), null, ctx.Ip);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return sesion;
    }

    public async Task OlvidePasswordAsync(OlvidePasswordRequest r, ContextoPeticion ctx, CancellationToken ct)
    {
        // Siempre responde igual, exista o no la cuenta (no revela qué correos están registrados).
        var email = Entrada.EmailSuave(r.Email);
        if (email is null) return;

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Email == email && u.Activo, ct);
        if (usuario is null) return;

        var ahora = time.GetUtcNow();
        await db.TokensRecuperacion
            .Where(t => t.UsuarioId == usuario.Id && t.Tipo == TipoToken.RecuperacionPassword && t.UsadoEn == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsadoEn, (DateTimeOffset?)ahora), ct);

        var (plano, hash) = tokens.CrearTokenOpaco();
        db.TokensRecuperacion.Add(new TokenRecuperacion
        {
            Id = Guid.NewGuid(),
            UsuarioId = usuario.Id,
            Tipo = TipoToken.RecuperacionPassword,
            TokenHash = hash,
            CreadoEn = ahora,
            ExpiraEn = ahora.AddMinutes(_auth.PasswordResetMinutes)
        });
        auditoria.Registrar(usuario.Id, "recuperacion_solicitada", "usuario", usuario.Id.ToString(), null, ctx.Ip);
        await db.SaveChangesAsync(ct);

        var enlace = $"{_auth.FrontendBaseUrl.TrimEnd('/')}/restablecer-password?token={Uri.EscapeDataString(plano)}";
        await correo.EnviarAsync(
            usuario.Email,
            "Restablece tu contraseña de Z-ONE",
            $"Hola {usuario.Nombre},\n\nPara crear una contraseña nueva abre este enlace (vence en {_auth.PasswordResetMinutes} minutos):\n{enlace}\n\nSi no lo pediste, ignora este mensaje.",
            ct);
    }

    public async Task RestablecerPasswordAsync(RestablecerPasswordRequest r, ContextoPeticion ctx, CancellationToken ct)
    {
        var ahora = time.GetUtcNow();
        var hash = TokenHash.Of(r.Token ?? "");

        var token = await db.TokensRecuperacion
            .Include(t => t.Usuario)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Tipo == TipoToken.RecuperacionPassword, ct);

        if (token is null || token.UsadoEn is not null || token.ExpiraEn <= ahora || !token.Usuario.Activo)
            throw new ApiException(400, "token_invalido", "El enlace no es válido o ya venció. Solicita uno nuevo.");

        var usuario = token.Usuario;
        var errores = PasswordPolicy.Validar(r.PasswordNueva, usuario.Email, usuario.Nombre, _auth.MinPasswordLength);
        if (errores.Count > 0) throw ApiException.Validacion("passwordNueva", errores);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        usuario.PasswordHash = hasher.Hash(r.PasswordNueva);
        usuario.DebeCambiarPassword = false;
        usuario.IntentosFallidos = 0;
        usuario.BloqueadoHasta = null;
        token.UsadoEn = ahora;

        await db.RevocarRefreshTokensAsync(usuario.Id, ahora, ct);
        auditoria.Registrar(usuario.Id, "password_restablecida", "usuario", usuario.Id.ToString(), null, ctx.Ip);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ───────────────────────── Utilidades ─────────────────────────

    private (ResultadoSesion Sesion, RefreshToken Entidad) Emitir(Usuario usuario, Guid? familia, ContextoPeticion ctx, DateTimeOffset ahora)
    {
        var (access, accessExpira) = tokens.CrearAccessToken(usuario, ahora);
        var (plano, hash) = tokens.CrearTokenOpaco();

        var refresh = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UsuarioId = usuario.Id,
            Familia = familia ?? Guid.NewGuid(),
            TokenHash = hash,
            CreadoEn = ahora,
            ExpiraEn = ahora.AddDays(_jwt.RefreshTokenDays),
            Ip = ctx.Ip,
            UserAgent = Entrada.Truncar(ctx.UserAgent, 300)
        };
        db.RefreshTokens.Add(refresh);

        var sesion = new ResultadoSesion(Mapear(usuario), access, accessExpira, plano, refresh.ExpiraEn);
        return (sesion, refresh);
    }

    public static UsuarioDto Mapear(Usuario u) => new(
        u.Id,
        u.Email,
        u.Nombre,
        u.Rol.ToString().ToLowerInvariant(),
        u.DebeCambiarPassword,
        Permisos.Para(u.Rol),
        u.Cliente?.Id,
        u.Colaborador?.Id,
        u.Colaborador?.Perfil.ToString());
}
