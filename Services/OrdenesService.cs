using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;

namespace ZOne.Api.Services;

public interface IOrdenesService
{
    Task<PaginaDto<OrdenDto>> ListarAsync(Actor actor, string? estado, string? concepto, long? proyectoId, int pagina, int tamano, CancellationToken ct);
    Task<OrdenDto> RegistrarPagoAsync(long id, string? referencia, Actor actor, ContextoPeticion ctx, CancellationToken ct);
}

/// <summary>Cobros de servicios y sesiones extra. El sistema no cobra: el administrador registra el pago recibido.</summary>
public sealed class OrdenesService(ZOneDbContext db, IAuditoriaService auditoria, TimeProvider time) : IOrdenesService
{
    private sealed record Fila(
        long Id, ConceptoOrden Concepto, Guid ClienteId, string Cliente, long? ProyectoId, string? Proyecto, long? SesionId,
        decimal Monto, EstadoOrden Estado, DateTimeOffset VenceEn, DateTimeOffset? PagadaEn, string? Referencia);

    private IQueryable<Fila> Consulta() =>
        from o in db.Ordenes.AsNoTracking()
        join c in db.Clientes.AsNoTracking() on o.ClienteId equals c.Id
        join p in db.Proyectos.AsNoTracking() on o.ProyectoId equals (long?)p.Id into proyectos
        from p in proyectos.DefaultIfEmpty()
        select new Fila(o.Id, o.Concepto, o.ClienteId, c.Nombre, o.ProyectoId, p.Titulo, o.SesionId,
            o.Monto, o.Estado, o.VenceEn, o.PagadaEn, o.ReferenciaPago);

    private static OrdenDto Mapear(Fila f, DateTimeOffset ahora)
    {
        var estado = f.Estado == EstadoOrden.Pendiente && f.VenceEn <= ahora ? EstadoOrden.Expirada : f.Estado;
        return new OrdenDto(f.Id, Convenciones.Api(f.Concepto), f.ClienteId, f.Cliente, f.ProyectoId, f.Proyecto, f.SesionId,
            f.Monto, Convenciones.Api(estado), f.VenceEn, f.PagadaEn, f.Referencia);
    }

    public async Task<PaginaDto<OrdenDto>> ListarAsync(
        Actor actor, string? estado, string? concepto, long? proyectoId, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);
        var yo = await db.ResolverAsync(actor, ct);
        var consulta = Consulta();

        if (!yo.EsAdmin)
        {
            if (yo.ClienteId is not { } mio) throw ApiException.Prohibido("Solo los clientes y el administrador ven los cobros.");
            consulta = consulta.Where(f => f.ClienteId == mio);
        }

        if (!string.IsNullOrWhiteSpace(estado))
        {
            if (!Convenciones.TryParseEnum<EstadoOrden>(estado, out var estadoFiltro))
                throw ApiException.Validacion("estado", "Estado no válido.");
            consulta = consulta.Where(f => f.Estado == estadoFiltro);
        }
        if (!string.IsNullOrWhiteSpace(concepto))
        {
            if (!Convenciones.TryParseEnum<ConceptoOrden>(concepto, out var conceptoFiltro))
                throw ApiException.Validacion("concepto", "Concepto no válido.");
            consulta = consulta.Where(f => f.Concepto == conceptoFiltro);
        }
        if (proyectoId is { } pid) consulta = consulta.Where(f => f.ProyectoId == pid);

        var total = await consulta.CountAsync(ct);
        var filas = await consulta.OrderByDescending(f => f.Id).Skip((pagina - 1) * tamano).Take(tamano).ToListAsync(ct);
        var ahora = time.GetUtcNow();
        return new PaginaDto<OrdenDto>(filas.Select(f => Mapear(f, ahora)).ToList(), total, pagina, tamano);
    }

    public async Task<OrdenDto> RegistrarPagoAsync(long id, string? referencia, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        if (!actor.EsAdmin) throw ApiException.Prohibido("Solo el administrador registra pagos.");

        var referenciaLimpia = Entrada.TextoOpcional(referencia, "referencia", 120);
        await db.Database.ExecuteSqlRawAsync("select public.expirar_vencidos()", ct);

        var orden = await db.Ordenes.FirstOrDefaultAsync(o => o.Id == id, ct)
                    ?? throw ApiException.NoEncontrado("No existe ese cobro.");

        if (orden.Concepto == ConceptoOrden.Alquiler)
            throw ApiException.Conflicto("usa_alquileres", "Los pagos de alquiler se registran en POST /alquileres/{id}/pago.");
        if (orden.Estado == EstadoOrden.Pagada)
            throw ApiException.Conflicto("ya_pagado", "Ese cobro ya tiene el pago registrado.");

        var ahora = time.GetUtcNow();
        if (orden.Estado != EstadoOrden.Pendiente || orden.VenceEn <= ahora)
            throw ApiException.Conflicto("plazo_vencido",
                "El plazo de 24 horas para registrar el pago venció. Si era un servicio, el proyecto quedó cancelado.");

        orden.Estado = EstadoOrden.Pagada;
        orden.PagadaEn = ahora;
        orden.RegistradaPor = actor.UsuarioId;
        orden.ReferenciaPago = referenciaLimpia;

        // Pagar el servicio es lo que deja el proyecto listo para empezar el trabajo.
        if (orden.Concepto == ConceptoOrden.Servicio)
        {
            var proyecto = await db.Proyectos.Include(p => p.Participantes).FirstOrDefaultAsync(p => p.Id == orden.ProyectoId, ct)
                           ?? throw ApiException.NoEncontrado("El cobro no tiene proyecto.");
            if (proyecto.Estado != EstadoProyecto.Pendiente)
                throw ApiException.Conflicto("proyecto_no_pendiente", "El proyecto ya no está pendiente de pago.");
            if (proyecto.Participantes.Any(x => x.Estado == EstadoParticipacion.Pendiente))
                throw ApiException.Conflicto("faltan_respuestas", "Aún hay colaboradores sin responder.");
            proyecto.Estado = EstadoProyecto.Confirmado;
            proyecto.ActualizadoEn = ahora;
        }

        auditoria.Registrar(actor.UsuarioId, "pago_registrado", "orden", id.ToString(),
            new { orden.Concepto, orden.Monto, referencia = referenciaLimpia }, ctx.Ip);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ErroresDb.Traducir(ex) is not null) { throw ErroresDb.Traducir(ex)!; }

        var fila = await Consulta().FirstAsync(f => f.Id == id, ct);
        return Mapear(fila, time.GetUtcNow());
    }
}
