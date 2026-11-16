using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Options;

namespace ZOne.Api.Services;

public interface IOrdenesService
{
    Task<PaginaDto<OrdenDto>> ListarAsync(Actor actor, string? estado, string? concepto, long? proyectoId, int pagina, int tamano, CancellationToken ct);
    Task<OrdenDto> RegistrarPagoAsync(long id, string? referencia, Actor actor, ContextoPeticion ctx, CancellationToken ct);
}

/// <summary>
/// Cobros de sesiones de proyecto y sesiones extra. El sistema no cobra: el administrador registra el pago recibido.
/// Mientras está pendiente, el monto sigue las tarifas actuales; al registrar el pago se recalcula y queda fijo.
/// </summary>
public sealed class OrdenesService(
    ZOneDbContext db,
    IAuditoriaService auditoria,
    IPreciosService precios,
    INotificacionesService notificaciones,
    IOptions<ProyectoOptions> proyectoOpt,
    TimeProvider time) : IOrdenesService
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

    private static OrdenDto Mapear(Fila f, DateTimeOffset ahora, IReadOnlyDictionary<long, decimal> montos)
    {
        var estado = f.Estado == EstadoOrden.Pendiente && f.VenceEn <= ahora ? EstadoOrden.Expirada : f.Estado;
        // Pendiente: sigue las tarifas actuales. Pagada o vencida: el monto guardado.
        var monto = estado == EstadoOrden.Pendiente && f.SesionId is { } sid && montos.TryGetValue(sid, out var vivo) ? vivo : f.Monto;
        return new OrdenDto(f.Id, Convenciones.Api(f.Concepto), f.ClienteId, f.Cliente, f.ProyectoId, f.Proyecto, f.SesionId,
            monto, Convenciones.Api(estado), f.VenceEn, f.PagadaEn, f.Referencia);
    }

    private async Task<IReadOnlyDictionary<long, decimal>> MontosVivosAsync(IEnumerable<Fila> filas, CancellationToken ct)
    {
        var ids = filas.Where(f => f.Estado == EstadoOrden.Pendiente && f.SesionId != null)
            .Select(f => f.SesionId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, decimal>();

        var sesiones = await db.Sesiones.AsNoTracking()
            .Include(s => s.Sala).Include(s => s.Aceptaciones)
            .Where(s => ids.Contains(s.Id))
            .ToListAsync(ct);
        return await precios.MontosAsync(sesiones, ct);
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
        var montos = await MontosVivosAsync(filas, ct);
        return new PaginaDto<OrdenDto>(filas.Select(f => Mapear(f, ahora, montos)).ToList(), total, pagina, tamano);
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
                "El plazo para registrar el pago venció y la sesión se liberó. Debe programarse de nuevo.");

        var sesion = await db.Sesiones
            .Include(s => s.Sala).Include(s => s.Aceptaciones).Include(s => s.Cliente)
            .FirstOrDefaultAsync(s => s.Id == orden.SesionId, ct)
            ?? throw ApiException.NoEncontrado("El cobro no tiene sesión.");
        if (sesion.Estado != EstadoSesion.PendientePago)
            throw ApiException.Conflicto("sesion_no_pendiente_pago", "La sesión ya no está pendiente de pago.");

        // Los precios no se congelan: se recalcula con las tarifas de hoy (sala + colaboradores que aceptaron) y queda fijo.
        var cobro = await precios.CobroDeSesionAsync(sesion, esAdmin: true, ct);
        if (cobro.Total <= 0)
            throw ApiException.Conflicto("tarifa_no_definida", "No se pudo calcular el cobro: revisa las tarifas.");

        orden.Monto = cobro.Total;
        orden.Estado = EstadoOrden.Pagada;
        orden.PagadaEn = ahora;
        orden.RegistradaPor = actor.UsuarioId;
        orden.ReferenciaPago = referenciaLimpia;
        orden.ActualizadoEn = ahora;

        sesion.Estado = EstadoSesion.Confirmada;
        sesion.ExpiraEn = null;

        // Pagar la primera sesión es lo que deja el proyecto confirmado.
        long? proyectoId = orden.ProyectoId;
        if (proyectoId is { } pid)
        {
            var proyecto = await db.Proyectos.FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (proyecto is { Estado: EstadoProyecto.Pendiente })
            {
                proyecto.Estado = EstadoProyecto.Confirmado;
                proyecto.ActualizadoEn = ahora;
            }
        }

        var resumen = Textos.Sesion(sesion, proyectoOpt.Value.UtcOffsetHoras);
        notificaciones.NotificarUsuarios(
            ReglasProyecto.Invitados(sesion).Where(a => a.Estado == EstadoParticipacion.Aceptada).Select(a => a.UsuarioId)
                .Append(sesion.Cliente!.UsuarioId),
            actor.UsuarioId, "sesion_confirmada", "Sesión confirmada", $"Quedó confirmada: {resumen}", proyectoId, sesion.Id);

        auditoria.Registrar(actor.UsuarioId, "pago_registrado", "orden", id.ToString(),
            new { orden.Concepto, orden.Monto, referencia = referenciaLimpia }, ctx.Ip);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ErroresDb.Traducir(ex) is not null) { throw ErroresDb.Traducir(ex)!; }

        var fila = await Consulta().FirstAsync(f => f.Id == id, ct);
        return Mapear(fila, time.GetUtcNow(), new Dictionary<long, decimal>());
    }
}
