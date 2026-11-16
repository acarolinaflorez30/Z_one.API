using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;

namespace ZOne.Api.Services;

/// <summary>Arma las sesiones de un proyecto para la API (invitados, montos y cobros), con pocas consultas.</summary>
public sealed class SesionVistaBuilder(ZOneDbContext db, IPreciosService precios, TimeProvider time)
{
    /// <summary>
    /// Cada sesión debe traer Sala, Aceptaciones y Proyecto (con Servicio y Cliente). Si se cargan con seguimiento
    /// y el proyecto ya está cargado, EF enlaza el Proyecto solo.
    /// </summary>
    public async Task<List<SesionProyectoDto>> ConstruirAsync(
        IReadOnlyCollection<Sesion> sesiones, Identidad yo, bool dueno, CancellationToken ct)
    {
        if (sesiones.Count == 0) return [];
        var ahora = time.GetUtcNow();
        var ids = sesiones.Select(s => s.Id).ToList();

        var personas = await db.PersonasAsync(
            sesiones.SelectMany(ReglasProyecto.Invitados).Select(a => a.UsuarioId), yo.EsAdmin, ct);
        var montos = await precios.MontosAsync(sesiones, ct);
        var ordenes = await db.Ordenes.AsNoTracking()
            .Where(o => o.SesionId != null && ids.Contains(o.SesionId.Value))
            .ToListAsync(ct);
        var vista = new Vista(yo.UsuarioId, yo.EsAdmin, dueno);

        return sesiones.OrderBy(s => s.Inicio).ThenBy(s => s.Id).Select(s =>
        {
            var orden = ordenes.Where(o => o.SesionId == s.Id)
                .OrderByDescending(o => o.Estado == EstadoOrden.Pagada).ThenByDescending(o => o.Id).FirstOrDefault();
            decimal? monto = montos.TryGetValue(s.Id, out var m) ? m : null;
            return ReglasProyecto.MapSesion(s, personas, vista, monto, orden, ahora);
        }).ToList();
    }
}
