using Microsoft.EntityFrameworkCore;
using Npgsql;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Infrastructure;

namespace ZOne.Api.Services;

/// <summary>Qué es la persona que hace la petición: administrador, cliente y/o colaborador activo.</summary>
public sealed record Identidad(Actor Actor, Guid? ClienteId, Guid? ColaboradorId, PerfilColaborador? Perfil)
{
    public bool EsAdmin => Actor.EsAdmin;
    public Guid UsuarioId => Actor.UsuarioId;
}

public static class IdentidadExtensions
{
    public static async Task<Identidad> ResolverAsync(this ZOneDbContext db, Actor actor, CancellationToken ct)
    {
        var cliente = await db.Clientes.AsNoTracking()
            .Where(c => c.UsuarioId == actor.UsuarioId && c.Activo)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);

        var colaborador = await db.Colaboradores.AsNoTracking()
            .Where(c => c.UsuarioId == actor.UsuarioId && c.Vinculacion == EstadoVinculacion.Activo)
            .Select(c => new { c.Id, c.Perfil })
            .FirstOrDefaultAsync(ct);

        return new Identidad(actor, cliente, colaborador?.Id, colaborador?.Perfil);
    }
}

/// <summary>Traduce los errores de las reglas de la base de datos a respuestas claras.</summary>
public static class ErroresDb
{
    public static ApiException? Traducir(DbUpdateException ex) => ex.InnerException switch
    {
        PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } =>
            ApiException.Conflicto("sala_ocupada", "La sala ya está reservada en ese horario."),
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } =>
            ApiException.Conflicto("duplicado", "Ese registro ya existe."),
        PostgresException { SqlState: PostgresErrorCodes.CheckViolation } =>
            ApiException.Validacion("regla", "Los datos no cumplen las reglas del sistema."),
        PostgresException { SqlState: PostgresErrorCodes.RaiseException } pg =>
            ApiException.Conflicto("regla_negocio", pg.MessageText),
        _ => null
    };
}
