using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;

namespace ZOne.Api.Services;

public interface ICatalogoService
{
    Task<IReadOnlyList<ServicioDto>> ListarServiciosAsync(bool soloActivos, CancellationToken ct);
    Task<ServicioDto> ActualizarServicioAsync(long id, ServicioRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task<PaginaDto<HistorialPrecioDto>> ListarHistorialPreciosAsync(string? tabla, long? registroId, Guid? registroUuid, int pagina, int tamano, CancellationToken ct);
}

public sealed class CatalogoService(ZOneDbContext db, IAuditoriaService auditoria, TimeProvider time) : ICatalogoService
{
    private static ServicioDto Mapear(Servicio s) => new(
        s.Id, s.Codigo, s.Nombre, s.Descripcion, s.Categoria,
        s.Etapa is null ? null : Convenciones.Api(s.Etapa.Value),
        s.SesionesIncluidas, s.Activo);

    // ───────────────────────── Servicios (sin precio: se cobra por sesión según sala y colaboradores) ─────────────────────────

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

        var antes = new { servicio.Nombre, servicio.Activo };
        servicio.Nombre = Entrada.Texto(r.Nombre, "nombre", 2, 120);
        servicio.Descripcion = Entrada.TextoOpcional(r.Descripcion, "descripcion", 1000);
        servicio.Activo = r.Activo;

        auditoria.Registrar(adminId, "servicio_actualizado", "servicio", servicio.Id.ToString(),
            new { antes, despues = new { servicio.Nombre, servicio.Activo } }, ctx.Ip);
        await db.SaveChangesAsync(ct);
        return Mapear(servicio);
    }

    // ───────────────────────── Historial de precios (lo llena la base de datos) ─────────────────────────

    public async Task<PaginaDto<HistorialPrecioDto>> ListarHistorialPreciosAsync(
        string? tabla, long? registroId, Guid? registroUuid, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);
        var consulta = db.HistorialPrecios.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(tabla))
        {
            var t = tabla.Trim().ToLowerInvariant();
            if (t != "salas" && t != "colaboradores" && t != "servicios")
                throw ApiException.Validacion("tabla", "La tabla debe ser salas o colaboradores (servicios ya no tienen precio).");
            consulta = consulta.Where(h => h.Tabla == t);
        }
        if (registroId is { } id) consulta = consulta.Where(h => h.RegistroId == id);
        if (registroUuid is { } uuid) consulta = consulta.Where(h => h.RegistroUuid == uuid);

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .OrderByDescending(h => h.CambiadoEn).ThenByDescending(h => h.Id)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .ToListAsync(ct);

        return new PaginaDto<HistorialPrecioDto>(
            filas.Select(h => new HistorialPrecioDto(h.Id, h.Tabla, h.RegistroId, h.RegistroUuid, h.TipoPrecio, h.Nombre, h.PrecioAnterior, h.PrecioNuevo, h.CambiadoEn)).ToList(),
            total, pagina, tamano);
    }
}
