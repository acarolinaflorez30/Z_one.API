using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;

namespace ZOne.Api.Services;

public interface ICancionesService
{
    Task<List<CancionDto>> ListarPorProyectoAsync(long proyectoId, Actor actor, CancellationToken ct);
    Task<CancionDto> CrearAsync(long proyectoId, CrearCancionRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<CancionDto> ActualizarAsync(long id, ActualizarCancionRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task EliminarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct);
}

public sealed class CancionesService(ZOneDbContext db, IAuditoriaService auditoria, TimeProvider time) : ICancionesService
{
    // ───────────────────────── Utilidades ─────────────────────────

    private static bool EsDueno(Proyecto p, Identidad yo) => yo.ClienteId is { } c && c == p.ClienteId;

    private async Task<Proyecto> CargarProyectoAsync(long id, CancellationToken ct) =>
        await db.Proyectos
            .Include(p => p.Participantes)
            .FirstOrDefaultAsync(p => p.Id == id, ct)
        ?? throw ApiException.NoEncontrado("No existe ese proyecto.");

    private async Task<Cancion> CargarCancionAsync(long id, CancellationToken ct) =>
        await db.Canciones
            .Include(c => c.Proyecto).ThenInclude(p => p.Participantes)
            .FirstOrDefaultAsync(c => c.Id == id, ct)
        ?? throw ApiException.NoEncontrado("No existe esa canción.");

    /// <summary>El administrador, el cliente dueño y los colaboradores invitados acceden; los demás reciben 404.</summary>
    private async Task<Identidad> AccesoAsync(Proyecto p, Actor actor, CancellationToken ct)
    {
        var yo = await db.ResolverAsync(actor, ct);
        var participa = yo.ColaboradorId is { } k && p.Participantes.Any(x => x.ColaboradorId == k);
        if (!yo.EsAdmin && !EsDueno(p, yo) && !participa)
            throw ApiException.NoEncontrado("No existe ese proyecto.");
        return yo;
    }

    private static void ExigirEscritura(Proyecto p, Identidad yo)
    {
        if (!yo.EsAdmin && !EsDueno(p, yo))
            throw ApiException.Prohibido("Solo el cliente dueño del proyecto o el administrador pueden modificar sus canciones.");
    }

    private static string Titulo(string? valor) => Entrada.Texto(valor, "titulo", 2, 150);

    private static int? Duracion(int? seg)
    {
        if (seg is null) return null;
        if (seg <= 0) throw ApiException.Validacion("duracionSeg", "La duración debe ser mayor que cero.");
        if (seg > 24 * 60 * 60) throw ApiException.Validacion("duracionSeg", "La duración no es válida.");
        return seg;
    }

    private static List<string> Partes(IEnumerable<string>? partes)
    {
        var lista = (partes ?? [])
            .Select(p => (p ?? "").Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (lista.Count > 100)
            throw ApiException.Validacion("partes", "No puede tener más de 100 partes.");
        foreach (var parte in lista)
            if (parte.Length > 100)
                throw ApiException.Validacion("partes", "Cada parte puede tener hasta 100 caracteres.");

        return lista;
    }

    private async Task GuardarAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ErroresDb.Traducir(ex) is not null) { throw ErroresDb.Traducir(ex)!; }
    }

    private static CancionDto Map(Cancion c) =>
        new(c.Id, c.ProyectoId, c.Titulo, c.DuracionSeg, c.Partes, c.RequiereEnsayo, c.CreadoEn);

    // ───────────────────────── Consultas ─────────────────────────

    public async Task<List<CancionDto>> ListarPorProyectoAsync(long proyectoId, Actor actor, CancellationToken ct)
    {
        var p = await CargarProyectoAsync(proyectoId, ct);
        await AccesoAsync(p, actor, ct);

        var filas = await db.Canciones.AsNoTracking()
            .Where(c => c.ProyectoId == proyectoId)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        return filas.Select(Map).ToList();
    }

    // ───────────────────────── Escritura ─────────────────────────

    public async Task<CancionDto> CrearAsync(long proyectoId, CrearCancionRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var p = await CargarProyectoAsync(proyectoId, ct);
        var yo = await AccesoAsync(p, actor, ct);
        ExigirEscritura(p, yo);

        var cancion = new Cancion
        {
            ProyectoId = proyectoId,
            Titulo = Titulo(r.Titulo),
            DuracionSeg = Duracion(r.DuracionSeg),
            Partes = Partes(r.Partes),
            RequiereEnsayo = r.RequiereEnsayo == true,
            CreadoEn = time.GetUtcNow()
        };
        db.Canciones.Add(cancion);
        auditoria.Registrar(actor.UsuarioId, "cancion_creada", "cancion", null,
            new { proyectoId, titulo = cancion.Titulo }, ctx.Ip);
        await GuardarAsync(ct);

        return Map(cancion);
    }

    public async Task<CancionDto> ActualizarAsync(long id, ActualizarCancionRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var cancion = await CargarCancionAsync(id, ct);
        var yo = await AccesoAsync(cancion.Proyecto, actor, ct);
        ExigirEscritura(cancion.Proyecto, yo);

        if (r.Titulo is not null) cancion.Titulo = Titulo(r.Titulo);
        if (r.DuracionSeg is not null) cancion.DuracionSeg = Duracion(r.DuracionSeg);
        if (r.Partes is not null) cancion.Partes = Partes(r.Partes);
        if (r.RequiereEnsayo is { } requiere) cancion.RequiereEnsayo = requiere;

        auditoria.Registrar(actor.UsuarioId, "cancion_actualizada", "cancion", id.ToString(), null, ctx.Ip);
        await GuardarAsync(ct);

        return Map(cancion);
    }

    public async Task EliminarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var cancion = await CargarCancionAsync(id, ct);
        var yo = await AccesoAsync(cancion.Proyecto, actor, ct);
        ExigirEscritura(cancion.Proyecto, yo);

        db.Canciones.Remove(cancion);
        auditoria.Registrar(actor.UsuarioId, "cancion_eliminada", "cancion", id.ToString(),
            new { proyectoId = cancion.ProyectoId, titulo = cancion.Titulo }, ctx.Ip);
        await GuardarAsync(ct);
    }
}
