using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;

namespace ZOne.Api.Services;

public interface ICatalogoService
{
    Task<IReadOnlyList<SalaDto>> ListarSalasAsync(bool soloActivas, CancellationToken ct);
    Task<SalaDto> CrearSalaAsync(SalaRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task<SalaDto> ActualizarSalaAsync(long id, SalaRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task<IReadOnlyList<ServicioDto>> ListarServiciosAsync(bool soloActivos, CancellationToken ct);
    Task<ServicioDto> ActualizarServicioAsync(long id, ServicioRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
}

public sealed class CatalogoService(ZOneDbContext db, IAuditoriaService auditoria, TimeProvider time) : ICatalogoService
{
    private const decimal PrecioMax = 1_000_000_000m;

    private static SalaDto Mapear(Sala s) => new(s.Id, s.Nombre, s.PrecioHora, s.Activo);

    private static ServicioDto Mapear(Servicio s) => new(
        s.Id, s.Codigo, s.Nombre, s.Descripcion, s.Categoria,
        s.Etapa is null ? null : Convenciones.Api(s.Etapa.Value),
        s.SesionesIncluidas, s.Precio, s.Activo);

    private static decimal ValidarPrecio(decimal precio, string campo)
    {
        if (precio < 0 || precio > PrecioMax)
            throw ApiException.Validacion(campo, "El precio debe ser un valor entre 0 y 1.000.000.000.");
        return precio;
    }

    // ───────────────────────── Salas ─────────────────────────

    public async Task<IReadOnlyList<SalaDto>> ListarSalasAsync(bool soloActivas, CancellationToken ct)
    {
        var consulta = db.Salas.AsNoTracking().AsQueryable();
        if (soloActivas) consulta = consulta.Where(s => s.Activo);
        var filas = await consulta.OrderBy(s => s.Nombre).ToListAsync(ct);
        return filas.Select(Mapear).ToList();
    }

    public async Task<SalaDto> CrearSalaAsync(SalaRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        var sala = new Sala
        {
            Nombre = Entrada.Texto(r.Nombre, "nombre", 2, 80),
            PrecioHora = ValidarPrecio(r.PrecioHora, "precioHora"),
            Activo = r.Activo,
            CreadoEn = time.GetUtcNow()
        };
        db.Salas.Add(sala);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.EsViolacionUnica())
        {
            throw ApiException.Conflicto("sala_duplicada", "Ya existe una sala con ese nombre.");
        }

        auditoria.Registrar(adminId, "sala_creada", "sala", sala.Id.ToString(), new { sala.Nombre, sala.PrecioHora }, ctx.Ip);
        await db.SaveChangesAsync(ct);
        return Mapear(sala);
    }

    public async Task<SalaDto> ActualizarSalaAsync(long id, SalaRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        var sala = await db.Salas.FirstOrDefaultAsync(s => s.Id == id, ct)
                   ?? throw ApiException.NoEncontrado("No existe esa sala.");

        var antes = new { sala.Nombre, sala.PrecioHora, sala.Activo };
        sala.Nombre = Entrada.Texto(r.Nombre, "nombre", 2, 80);
        sala.PrecioHora = ValidarPrecio(r.PrecioHora, "precioHora");
        sala.Activo = r.Activo;

        auditoria.Registrar(adminId, "sala_actualizada", "sala", sala.Id.ToString(),
            new { antes, despues = new { sala.Nombre, sala.PrecioHora, sala.Activo } }, ctx.Ip);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.EsViolacionUnica())
        {
            throw ApiException.Conflicto("sala_duplicada", "Ya existe una sala con ese nombre.");
        }
        return Mapear(sala);
    }

    // ───────────────────────── Servicios (los precios los define otra persona del proyecto) ─────────────────────────

    public async Task<IReadOnlyList<ServicioDto>> ListarServiciosAsync(bool soloActivos, CancellationToken ct)
    {
        var consulta = db.Servicios.AsNoTracking().AsQueryable();
        if (soloActivos) consulta = consulta.Where(s => s.Activo);
        var filas = await consulta.OrderBy(s => s.Id).ToListAsync(ct);
        return filas.Select(Mapear).ToList();
    }

    public async Task<ServicioDto> ActualizarServicioAsync(long id, ServicioRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        var servicio = await db.Servicios.FirstOrDefaultAsync(s => s.Id == id, ct)
                       ?? throw ApiException.NoEncontrado("No existe ese servicio.");

        var antes = new { servicio.Nombre, servicio.Precio, servicio.Activo };
        servicio.Nombre = Entrada.Texto(r.Nombre, "nombre", 2, 120);
        servicio.Descripcion = Entrada.TextoOpcional(r.Descripcion, "descripcion", 1000);
        servicio.Precio = r.Precio is { } p ? ValidarPrecio(p, "precio") : null;
        servicio.Activo = r.Activo;

        auditoria.Registrar(adminId, "servicio_actualizado", "servicio", servicio.Id.ToString(),
            new { antes, despues = new { servicio.Nombre, servicio.Precio, servicio.Activo } }, ctx.Ip);
        await db.SaveChangesAsync(ct);
        return Mapear(servicio);
    }
}
