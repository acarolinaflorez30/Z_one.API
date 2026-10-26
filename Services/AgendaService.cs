using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;

namespace ZOne.Api.Services;

public interface IAgendaService
{
    Task<PaginaDto<AgendaItemDto>> ListarAsync(Actor actor, string? vista, DateTimeOffset? desde, DateTimeOffset? hasta,
        string? tipo, string? estado, long? salaId, long? proyectoId, Guid? clienteId, int pagina, int tamano, CancellationToken ct);
    Task<AgendaResumenDto> ResumenAsync(Actor actor, CancellationToken ct);
}

/// <summary>
/// Agenda unificada: alquileres y sesiones de proyecto en una sola lista. Cada persona ve solo lo suyo
/// (el cliente, sus alquileres y las sesiones de sus proyectos; el colaborador, las de los proyectos donde participa)
/// y el administrador ve todo. Lo que ocupa una sala de otra persona nunca se muestra aquí: para eso está la
/// disponibilidad pública, que no incluye datos personales.
/// </summary>
public sealed class AgendaService(ZOneDbContext db, TimeProvider time) : IAgendaService
{
    private sealed record Fila(
        long Id, TipoSesion Tipo, long SalaId, string Sala, DateTimeOffset Inicio, DateTimeOffset Fin,
        EstadoSesion Estado, EtapaProyecto? Etapa, short? Numero, bool EsExtra, long? ProyectoId, string? Proyecto,
        Guid? ClienteId, string? Cliente, DateTimeOffset? ExpiraEn, string? Resena,
        bool RequiereMiRespuesta, decimal? Monto, EstadoOrden? EstadoPago);

    // ───────────────────────── Qué puede ver cada quien ─────────────────────────

    private IQueryable<Sesion> Visibles(Identidad yo)
    {
        var consulta = db.Sesiones.AsNoTracking().AsQueryable();
        if (yo.EsAdmin) return consulta;

        var clienteId = yo.ClienteId;
        var colaboradorId = yo.ColaboradorId;

        return consulta.Where(s =>
            (clienteId != null && (
                (s.Tipo == TipoSesion.Alquiler && s.ClienteId == clienteId) ||
                (s.Tipo == TipoSesion.Proyecto && s.Proyecto!.ClienteId == clienteId)))
            ||
            (colaboradorId != null && s.Tipo == TipoSesion.Proyecto &&
                s.Proyecto!.Participantes.Any(x => x.ColaboradorId == colaboradorId && x.Estado == EstadoParticipacion.Aceptada)));
    }

    /// <summary>
    /// proximas: lo que viene y lo que espera cierre; historial: sesiones cumplidas y alquileres ya realizados;
    /// todas: incluye también lo cancelado o expirado.
    /// </summary>
    private static IQueryable<Sesion> PorVista(IQueryable<Sesion> q, string vista, DateTimeOffset ahora) => vista switch
    {
        "historial" => q.Where(s => s.Estado == EstadoSesion.Cumplida ||
                                    (s.Tipo == TipoSesion.Alquiler && s.Estado == EstadoSesion.Confirmada && s.Fin < ahora)),
        "proximas" => q.Where(s =>
            ((s.Estado == EstadoSesion.PendientePago || s.Estado == EstadoSesion.PendienteAceptacion)
                && s.ExpiraEn > ahora && s.Fin >= ahora)
            || (s.Estado == EstadoSesion.Confirmada && (s.Tipo == TipoSesion.Proyecto || s.Fin >= ahora))),
        _ => q
    };

    // ───────────────────────── Listar ─────────────────────────

    public async Task<PaginaDto<AgendaItemDto>> ListarAsync(
        Actor actor, string? vista, DateTimeOffset? desde, DateTimeOffset? hasta,
        string? tipo, string? estado, long? salaId, long? proyectoId, Guid? clienteId,
        int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);
        var ahora = time.GetUtcNow();
        var yo = await db.ResolverAsync(actor, ct);

        var vistaFinal = (vista ?? "proximas").Trim().ToLowerInvariant();
        if (vistaFinal is not ("proximas" or "historial" or "todas"))
            throw ApiException.Validacion("vista", "La vista debe ser proximas, historial o todas.");

        var consulta = PorVista(Visibles(yo), vistaFinal, ahora);

        if (desde is { } d)
        {
            var du = d.ToUniversalTime();
            consulta = consulta.Where(s => s.Fin > du);
        }
        if (hasta is { } h)
        {
            var hu = h.ToUniversalTime();
            consulta = consulta.Where(s => s.Inicio < hu);
        }
        if (desde is { } d2 && hasta is { } h2 && h2 - d2 > TimeSpan.FromDays(366))
            throw ApiException.Validacion("hasta", "El rango máximo es de un año.");

        if (!string.IsNullOrWhiteSpace(tipo))
        {
            if (!Convenciones.TryParseEnum<TipoSesion>(tipo, out var tipoFiltro))
                throw ApiException.Validacion("tipo", "El tipo debe ser alquiler o proyecto.");
            consulta = consulta.Where(s => s.Tipo == tipoFiltro);
        }
        if (!string.IsNullOrWhiteSpace(estado))
        {
            if (!Convenciones.TryParseEnum<EstadoSesion>(estado, out var estadoFiltro))
                throw ApiException.Validacion("estado", "Estado no válido.");
            consulta = consulta.Where(s => s.Estado == estadoFiltro);
        }
        if (salaId is { } sid) consulta = consulta.Where(s => s.SalaId == sid);
        if (proyectoId is { } pid) consulta = consulta.Where(s => s.ProyectoId == pid);

        // Filtrar por cliente es una herramienta del administrador; los demás ya ven solo lo suyo.
        if (clienteId is { } cid && yo.EsAdmin)
            consulta = consulta.Where(s => s.Tipo == TipoSesion.Alquiler
                ? s.ClienteId == cid
                : s.Proyecto!.ClienteId == cid);

        var total = await consulta.CountAsync(ct);

        var ordenada = vistaFinal == "proximas"
            ? consulta.OrderBy(s => s.Inicio).ThenBy(s => s.Id)
            : consulta.OrderByDescending(s => s.Inicio).ThenByDescending(s => s.Id);

        var filas = await Proyectar(ordenada.Skip((pagina - 1) * tamano).Take(tamano), actor.UsuarioId, ahora).ToListAsync(ct);
        return new PaginaDto<AgendaItemDto>(filas.Select(f => Mapear(f, ahora)).ToList(), total, pagina, tamano);
    }

    private IQueryable<Fila> Proyectar(IQueryable<Sesion> q, Guid usuarioId, DateTimeOffset ahora) =>
        q.Select(s => new Fila(
            s.Id, s.Tipo, s.SalaId, s.Sala.Nombre, s.Inicio, s.Fin, s.Estado, s.Etapa, s.NumeroSesion, s.EsExtra,
            s.ProyectoId,
            s.Proyecto != null ? s.Proyecto.Titulo : null,
            s.Tipo == TipoSesion.Alquiler ? s.ClienteId : (s.Proyecto != null ? (Guid?)s.Proyecto.ClienteId : null),
            s.Tipo == TipoSesion.Alquiler
                ? (s.Cliente != null ? s.Cliente.Nombre : null)
                : (s.Proyecto != null ? s.Proyecto.Cliente.Nombre : null),
            s.ExpiraEn, s.Resena,
            s.Estado == EstadoSesion.PendienteAceptacion && s.ExpiraEn > ahora &&
                s.Aceptaciones.Any(a => a.UsuarioId == usuarioId && a.Estado == EstadoParticipacion.Pendiente),
            db.Ordenes.Where(o => o.SesionId == s.Id).Select(o => (decimal?)o.Monto).FirstOrDefault(),
            db.Ordenes.Where(o => o.SesionId == s.Id).Select(o => (EstadoOrden?)o.Estado).FirstOrDefault()));

    private static AgendaItemDto Mapear(Fila f, DateTimeOffset ahora)
    {
        // Si el plazo ya venció pero el cron aún no corrió, se muestra como expirada.
        var vencida = (f.Estado == EstadoSesion.PendientePago || f.Estado == EstadoSesion.PendienteAceptacion)
                      && f.ExpiraEn is { } e && e <= ahora;
        var estado = vencida ? EstadoSesion.Expirada : f.Estado;
        var estadoPago = f.EstadoPago is { } ep
            ? (vencida && ep == EstadoOrden.Pendiente ? EstadoOrden.Expirada : ep)
            : (EstadoOrden?)null;

        var esProyecto = f.Tipo == TipoSesion.Proyecto;
        var titulo = esProyecto
            ? $"{f.Proyecto} · {(f.EsExtra ? "Sesión extra" : f.Etapa == EtapaProyecto.Revision ? "Sesión de ajuste" : $"Sesión {f.Numero}")}"
            : $"Alquiler · {f.Sala}";

        return new AgendaItemDto(
            Convenciones.Api(f.Tipo), f.Id, titulo, f.SalaId, f.Sala, f.Inicio, f.Fin,
            Math.Round((decimal)(f.Fin - f.Inicio).TotalHours, 2), Convenciones.Api(estado),
            f.Etapa is null ? null : Convenciones.Api(f.Etapa.Value), f.Numero, f.EsExtra,
            f.ProyectoId, f.Proyecto, f.ClienteId, f.Cliente, f.ExpiraEn,
            f.RequiereMiRespuesta && !vencida,
            esProyecto && f.Estado == EstadoSesion.Confirmada && f.Fin < ahora,
            f.Resena, f.Monto, estadoPago is { } p ? Convenciones.Api(p) : null);
    }

    // ───────────────────────── Resumen (avisos del inicio) ─────────────────────────

    public async Task<AgendaResumenDto> ResumenAsync(Actor actor, CancellationToken ct)
    {
        var ahora = time.GetUtcNow();
        var limite = ahora.AddDays(7);
        var uid = actor.UsuarioId;
        var yo = await db.ResolverAsync(actor, ct);
        var visibles = Visibles(yo);

        var proximas7 = await PorVista(visibles, "proximas", ahora).CountAsync(s => s.Inicio < limite, ct);

        var porResponder = await visibles.CountAsync(s =>
            s.Estado == EstadoSesion.PendienteAceptacion && s.ExpiraEn > ahora &&
            s.Aceptaciones.Any(a => a.UsuarioId == uid && a.Estado == EstadoParticipacion.Pendiente), ct);

        // Sesiones terminadas que esperan la reseña: las cierra el administrador o el productor del proyecto.
        var porCerrar = 0;
        var colaboradorId = yo.ColaboradorId;
        if (yo.EsAdmin)
        {
            porCerrar = await visibles.CountAsync(s =>
                s.Tipo == TipoSesion.Proyecto && s.Estado == EstadoSesion.Confirmada && s.Fin < ahora, ct);
        }
        else if (colaboradorId != null)
        {
            porCerrar = await visibles.CountAsync(s =>
                s.Tipo == TipoSesion.Proyecto && s.Estado == EstadoSesion.Confirmada && s.Fin < ahora &&
                s.Proyecto!.Participantes.Any(x => x.ColaboradorId == colaboradorId
                    && x.Rol == RolParticipante.Productor && x.Estado == EstadoParticipacion.Aceptada), ct);
        }

        var solicitudes = colaboradorId == null
            ? 0
            : await db.Participantes.CountAsync(x =>
                x.ColaboradorId == colaboradorId && x.Estado == EstadoParticipacion.Pendiente && x.VenceEn > ahora, ct);

        var clienteId = yo.ClienteId;
        var cobros = yo.EsAdmin
            ? await db.Ordenes.CountAsync(o => o.Estado == EstadoOrden.Pendiente && o.VenceEn > ahora, ct)
            : clienteId == null
                ? 0
                : await db.Ordenes.CountAsync(o =>
                    o.ClienteId == clienteId && o.Estado == EstadoOrden.Pendiente && o.VenceEn > ahora, ct);

        // Cliente: trabajos que debe aprobar (dentro de sus 24 h).
        // Administrador: revisiones cuyo plazo venció sin respuesta; las decide él desde su panel.
        var revisiones = 0;
        if (yo.EsAdmin)
            revisiones = await db.Proyectos.CountAsync(p => p.Estado == EstadoProyecto.EnRevision && p.RevisionVenceEn <= ahora, ct);
        else if (clienteId != null)
            revisiones = await db.Proyectos.CountAsync(p =>
                p.ClienteId == clienteId && p.Estado == EstadoProyecto.EnRevision && p.RevisionVenceEn > ahora, ct);

        var sinLeer = await db.Notificaciones.CountAsync(n => n.UsuarioId == uid && n.LeidaEn == null, ct);

        var proxima = await Proyectar(
                PorVista(visibles, "proximas", ahora).OrderBy(s => s.Inicio).ThenBy(s => s.Id).Take(1), uid, ahora)
            .FirstOrDefaultAsync(ct);

        return new AgendaResumenDto(proximas7, porResponder, porCerrar, solicitudes, cobros, revisiones, sinLeer,
            proxima is null ? null : Mapear(proxima, ahora));
    }
}
