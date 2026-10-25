using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Infrastructure;

namespace ZOne.Api.Services;

public sealed record ContextoPeticion(IPAddress? Ip, string? UserAgent);

/// <summary>Validaciones y normalización de datos de entrada. La API repite siempre las validaciones del frontend.</summary>
public static class Entrada
{
    public static string Texto(string? valor, string campo, int min, int max)
    {
        var t = (valor ?? "").Trim();
        if (t.Length < min || t.Length > max)
            throw ApiException.Validacion(campo, $"Debe tener entre {min} y {max} caracteres.");
        return t;
    }

    public static string? TextoOpcional(string? valor, string campo, int max)
    {
        var t = valor?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (t.Length > max) throw ApiException.Validacion(campo, $"No puede superar {max} caracteres.");
        return t;
    }

    /// <summary>Devuelve el correo en minúsculas o null si no es válido (para el login, que no debe revelar nada).</summary>
    public static string? EmailSuave(string? valor)
    {
        var e = (valor ?? "").Trim().ToLowerInvariant();
        if (e.Length is 0 or > 254) return null;
        if (!MailAddress.TryCreate(e, out var m) || m.Address != e || !e.Contains('.', StringComparison.Ordinal)) return null;
        return e;
    }

    public static string Email(string? valor) =>
        EmailSuave(valor) ?? throw ApiException.Validacion("email", "El correo no es válido.");

    public static string? Truncar(string? valor, int max) =>
        valor is null ? null : valor.Length <= max ? valor : valor[..max];
}

public static class DbExtensions
{
    public static Task<int> RevocarRefreshTokensAsync(this ZOneDbContext db, Guid usuarioId, DateTimeOffset ahora, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.UsuarioId == usuarioId && t.RevocadoEn == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevocadoEn, (DateTimeOffset?)ahora), ct);

    public static bool EsViolacionUnica(this DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };
}

public interface IAuditoriaService
{
    /// <summary>Agrega el registro al contexto; se guarda junto con el resto de cambios del llamador.</summary>
    void Registrar(Guid? usuarioId, string accion, string entidad, string? entidadId, object? detalle, IPAddress? ip);
}

public sealed class AuditoriaService(ZOneDbContext db, TimeProvider time) : IAuditoriaService
{
    public void Registrar(Guid? usuarioId, string accion, string entidad, string? entidadId, object? detalle, IPAddress? ip) =>
        db.Auditorias.Add(new Auditoria
        {
            UsuarioId = usuarioId,
            Accion = accion,
            Entidad = entidad,
            EntidadId = entidadId,
            Detalle = detalle is null ? "{}" : JsonSerializer.Serialize(detalle),
            Ip = ip,
            CreadoEn = time.GetUtcNow()
        });
}

public interface IEmailSender
{
    Task EnviarAsync(string para, string asunto, string cuerpo, CancellationToken ct);
}

/// <summary>
/// Implementación provisional: no envía correos. En desarrollo escribe el mensaje (con el enlace) en el log
/// para poder probar la recuperación de contraseña; en producción NO registra el contenido.
/// Se reemplaza por un proveedor real (SMTP, SendGrid, etc.) cuando se defina.
/// </summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger, IHostEnvironment env) : IEmailSender
{
    public Task EnviarAsync(string para, string asunto, string cuerpo, CancellationToken ct)
    {
        if (env.IsDevelopment())
            logger.LogInformation("[CORREO SIMULADO] Para: {Para} | Asunto: {Asunto}\n{Cuerpo}", para, asunto, cuerpo);
        else
            logger.LogWarning("No hay proveedor de correo configurado: no se envió el mensaje '{Asunto}'.", asunto);
        return Task.CompletedTask;
    }
}
