using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Infrastructure;
using ZOne.Api.Options;

namespace ZOne.Api.Services;

/// <summary>
/// Cada pocos minutos revisa lo que está por vencer y lo que ya venció, y avisa (en la web y por correo).
/// Cada aviso sale UNA sola vez: se registra en avisos_enviados con una clave "tipo:id:usuario" en la misma
/// transacción que crea la notificación, así que reinicios o varias copias de la API no lo duplican.
///
///  rec_invitacion  colaboradores con una invitación sin responder que vence pronto
///  rec_pago        cliente con un pago que vence pronto
///  rec_sesion      cliente y colaboradores que aceptaron: la sesión confirmada empieza pronto
///  rec_revision    cliente: se acaba su plazo para aprobar o pedir ajustes
///  revision_vencida administradores: el cliente no respondió la revisión a tiempo
///  cerrar_sesion   administradores y productor: la sesión terminó y nadie la marcó como cumplida
///  sesion_vencida  cliente y colaboradores: venció el plazo de pago o de respuesta y la reserva se perdió
/// </summary>
public sealed class AvisosWorker(
    IServiceScopeFactory scopes,
    IOptions<AvisosOptions> opciones,
    IOptions<ProyectoOptions> proyecto,
    TimeProvider time,
    ILogger<AvisosWorker> logger) : BackgroundService
{
    private sealed record Aviso(string Tipo, string Ref, Guid UsuarioId, string Titulo, string Mensaje, long? ProyectoId, long? SesionId)
    {
        public string Clave => $"{Tipo}:{Ref}:{UsuarioId}";
    }

    // Ventanas hacia atrás: lo más viejo no se avisa (evita una avalancha de avisos antiguos la primera vez que corre)
    private static readonly TimeSpan AtrasVencimientos = TimeSpan.FromDays(2);
    private static readonly TimeSpan AtrasRevision = TimeSpan.FromDays(3);
    private static readonly TimeSpan AtrasSesionSinCerrar = TimeSpan.FromDays(3);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = opciones.Value;
        if (!o.Habilitado)
        {
            logger.LogInformation("Recordatorios y avisos de vencimiento desactivados (Avisos:Habilitado=false).");
            return;
        }
        logger.LogInformation("Recordatorios y avisos de vencimiento activos (cada {Min} min).", o.IntervaloMinutos);

        try { await Task.Delay(TimeSpan.FromSeconds(20), stop); }
        catch (OperationCanceledException) { return; }

        var ultimaLimpieza = DateTimeOffset.MinValue;
        while (!stop.IsCancellationRequested)
        {
            var espera = TimeSpan.FromMinutes(Math.Max(1, o.IntervaloMinutos));
            try
            {
                var enviados = await CicloAsync(stop);
                if (enviados >= o.MaxPorCiclo) espera = TimeSpan.FromSeconds(10);   // quedan más: seguir pronto

                if (time.GetUtcNow() - ultimaLimpieza > TimeSpan.FromHours(6))
                {
                    await LimpiarAsync(stop);
                    ultimaLimpieza = time.GetUtcNow();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falló un ciclo de recordatorios y avisos de vencimiento.");
                espera = TimeSpan.FromMinutes(2);
            }

            try { await Task.Delay(espera, stop); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<int> CicloAsync(CancellationToken ct)
    {
        var o = opciones.Value;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZOneDbContext>();
        var notificaciones = scope.ServiceProvider.GetRequiredService<INotificacionesService>();

        // Que la base marque primero lo vencido (también lo hace cada minuto por su cuenta)
        try { await db.Database.ExecuteSqlRawAsync("select public.expirar_vencidos()", ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning("No se pudo ejecutar expirar_vencidos: {Error}", ex.GetType().Name); }

        var ahora = time.GetUtcNow();
        var off = proyecto.Value.UtcOffsetHoras;
        var admins = await db.Usuarios.AsNoTracking()
            .Where(u => u.Rol == RolUsuario.Administrador && u.Activo)
            .Select(u => u.Id).ToListAsync(ct);

        // Cada regla va aparte: si una falla, las demás siguen
        var candidatos = new List<Aviso>();
        var reglas = new (string Nombre, Func<Task<List<Aviso>>> Buscar)[]
        {
            ("invitaciones por vencer", () => InvitacionesPorVencerAsync(db, ahora, off, ct)),
            ("pagos por vencer", () => PagosPorVencerAsync(db, ahora, off, ct)),
            ("sesiones próximas", () => SesionesProximasAsync(db, ahora, off, ct)),
            ("revisiones", () => RevisionesAsync(db, admins, ahora, off, ct)),
            ("sesiones sin cerrar", () => SesionesSinCerrarAsync(db, admins, ahora, off, ct)),
            ("vencimientos", () => VencimientosAsync(db, ahora, off, ct)),
        };
        foreach (var (nombre, buscar) in reglas)
        {
            try { candidatos.AddRange(await buscar()); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Falló la revisión de {Regla}.", nombre); }
        }
        if (candidatos.Count == 0) return 0;

        // Solo personas activas (los administradores ya vienen filtrados)
        var destinatarios = candidatos.Select(a => a.UsuarioId).Distinct().ToList();
        var activos = new HashSet<Guid>();
        foreach (var lote in destinatarios.Chunk(300))
        {
            var ids = lote.ToList();
            activos.UnionWith(await db.Usuarios.AsNoTracking().Where(u => u.Activo && ids.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct));
        }

        // Quitar los que ya se enviaron antes
        candidatos = candidatos.Where(a => activos.Contains(a.UsuarioId)).DistinctBy(a => a.Clave).ToList();
        var ya = new HashSet<string>();
        foreach (var lote in candidatos.Select(a => a.Clave).Chunk(300))
        {
            var claves = lote.ToList();
            ya.UnionWith(await db.AvisosEnviados.AsNoTracking().Where(a => claves.Contains(a.Clave)).Select(a => a.Clave).ToListAsync(ct));
        }

        var enviados = 0;
        foreach (var a in candidatos.Where(a => !ya.Contains(a.Clave)))
        {
            if (enviados >= o.MaxPorCiclo) break;

            // Aviso + notificación + correo en una sola transacción: o salen los tres o ninguno
            db.AvisosEnviados.Add(new AvisoEnviado { Clave = a.Clave, CreadoEn = ahora });
            notificaciones.NotificarUsuarios([a.UsuarioId], Guid.Empty, a.Tipo, a.Titulo, a.Mensaje, a.ProyectoId, a.SesionId);
            try
            {
                await db.SaveChangesAsync(ct);
                enviados++;
            }
            catch (DbUpdateException ex)
            {
                db.ChangeTracker.Clear();
                // Clave repetida: otra copia de la API lo envió justo ahora. Es normal; se descarta y se sigue.
                if (!ex.EsViolacionUnica())
                    logger.LogWarning("No se registró el aviso {Tipo}: {Error}", a.Tipo, ex.InnerException?.Message ?? ex.GetType().Name);
            }
        }

        if (enviados > 0) logger.LogInformation("Se enviaron {N} recordatorios y avisos de vencimiento.", enviados);
        return enviados;
    }

    // ───────────────────────── Reglas ─────────────────────────

    private static IQueryable<Sesion> Cargar(ZOneDbContext db) =>
        db.Sesiones.AsNoTracking()
            .Include(s => s.Sala)
            .Include(s => s.Proyecto).ThenInclude(p => p!.Servicio)
            .Include(s => s.Aceptaciones);

    private static Guid? ClienteDe(Sesion s) => s.Tipo == TipoSesion.Alquiler ? s.ClienteId : s.Proyecto?.ClienteId;

    private static async Task<Dictionary<Guid, Guid>> UsuariosDeClientesAsync(ZOneDbContext db, IEnumerable<Guid> clienteIds, CancellationToken ct)
    {
        var ids = clienteIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.Clientes.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.UsuarioId, ct);
    }

    /// <summary>R1. Colaboradores con invitación sin responder cuando faltan pocas horas para que venza.</summary>
    private async Task<List<Aviso>> InvitacionesPorVencerAsync(ZOneDbContext db, DateTimeOffset ahora, int off, CancellationToken ct)
    {
        var limite = ahora.AddHours(opciones.Value.RecordatorioInvitacionHoras);
        var creadaAntes = ahora.AddMinutes(-60);   // no recordar algo que se acaba de enviar
        var sesiones = await Cargar(db)
            .Where(s => s.Estado == EstadoSesion.PendienteAceptacion && s.ExpiraEn > ahora && s.ExpiraEn <= limite && s.CreadoEn <= creadaAntes)
            .OrderBy(s => s.ExpiraEn).Take(300).ToListAsync(ct);

        var r = new List<Aviso>();
        foreach (var s in sesiones)
            foreach (var inv in ReglasProyecto.Invitados(s).Where(a => a.Estado == EstadoParticipacion.Pendiente))
                r.Add(new("rec_invitacion", s.Id.ToString(), inv.UsuarioId,
                    "Recuerda responder tu invitación",
                    $"Tienes pendiente responder esta invitación: {Textos.Sesion(s, off)} Vence el {Textos.Local(s.ExpiraEn!.Value, off)}; si no respondes antes, se pierde y la sala se libera.",
                    s.ProyectoId, s.Id));
        return r;
    }

    /// <summary>R2. Cliente con un pago pendiente que vence pronto.</summary>
    private async Task<List<Aviso>> PagosPorVencerAsync(ZOneDbContext db, DateTimeOffset ahora, int off, CancellationToken ct)
    {
        var limite = ahora.AddHours(opciones.Value.RecordatorioPagoHoras);
        var creadaAntes = ahora.AddMinutes(-60);
        var sesiones = await Cargar(db)
            .Where(s => s.Estado == EstadoSesion.PendientePago && s.ExpiraEn > ahora && s.ExpiraEn <= limite && s.CreadoEn <= creadaAntes)
            .OrderBy(s => s.ExpiraEn).Take(300).ToListAsync(ct);
        var usuarios = await UsuariosDeClientesAsync(db, sesiones.Select(ClienteDe).OfType<Guid>(), ct);

        var r = new List<Aviso>();
        foreach (var s in sesiones)
        {
            if (ClienteDe(s) is not { } c || !usuarios.TryGetValue(c, out var usuario)) continue;
            r.Add(new("rec_pago", s.Id.ToString(), usuario,
                "Falta el pago de tu reserva",
                $"El pago de esta reserva vence el {Textos.Local(s.ExpiraEn!.Value, off)}: {Textos.Sesion(s, off)} Si no se registra antes, la reserva se pierde y la sala se libera.",
                s.ProyectoId, s.Id));
        }
        return r;
    }

    /// <summary>R3. Sesión confirmada que empieza pronto: cliente y colaboradores que aceptaron.</summary>
    private async Task<List<Aviso>> SesionesProximasAsync(ZOneDbContext db, DateTimeOffset ahora, int off, CancellationToken ct)
    {
        var limite = ahora.AddHours(opciones.Value.RecordatorioSesionHoras);
        var sesiones = await Cargar(db)
            .Where(s => s.Estado == EstadoSesion.Confirmada && s.Inicio > ahora && s.Inicio <= limite)
            .OrderBy(s => s.Inicio).Take(300).ToListAsync(ct);
        var usuarios = await UsuariosDeClientesAsync(db, sesiones.Select(ClienteDe).OfType<Guid>(), ct);

        var r = new List<Aviso>();
        foreach (var s in sesiones)
        {
            var destinos = new HashSet<Guid>(ReglasProyecto.Invitados(s).Where(a => a.Estado == EstadoParticipacion.Aceptada).Select(a => a.UsuarioId));
            if (ClienteDe(s) is { } c && usuarios.TryGetValue(c, out var usuario)) destinos.Add(usuario);
            foreach (var u in destinos)
                r.Add(new("rec_sesion", s.Id.ToString(), u,
                    "Recordatorio de sesión",
                    $"Te recordamos que tu sesión es pronto: {Textos.Sesion(s, off)}",
                    s.ProyectoId, s.Id));
        }
        return r;
    }

    /// <summary>R4. Revisión de una canción completa: recordatorio al cliente y aviso al administrador si venció sin respuesta.</summary>
    private async Task<List<Aviso>> RevisionesAsync(ZOneDbContext db, List<Guid> admins, DateTimeOffset ahora, int off, CancellationToken ct)
    {
        var limite = ahora.AddHours(opciones.Value.RecordatorioRevisionHoras);
        var desde = ahora - AtrasRevision;
        var proyectos = await db.Proyectos.AsNoTracking()
            .Where(p => p.Estado == EstadoProyecto.EnRevision && p.RevisionVenceEn != null
                        && p.RevisionVenceEn <= limite && p.RevisionVenceEn > desde)
            .OrderByDescending(p => p.RevisionVenceEn).Take(300).ToListAsync(ct);
        var usuarios = await UsuariosDeClientesAsync(db, proyectos.Select(p => p.ClienteId), ct);

        var r = new List<Aviso>();
        foreach (var p in proyectos)
        {
            var vence = p.RevisionVenceEn!.Value;
            var referencia = $"{p.Id}r{p.RondaRevision}";   // una por ronda de revisión
            if (vence > ahora)
            {
                if (usuarios.TryGetValue(p.ClienteId, out var usuario))
                    r.Add(new("rec_revision", referencia, usuario,
                        "Revisa tu proyecto",
                        $"Tu plazo para aprobar o pedir ajustes de \"{p.Titulo}\" vence el {Textos.Local(vence, off)}. Después de esa hora decide el administrador.",
                        p.Id, null));
            }
            else
            {
                foreach (var admin in admins)
                    r.Add(new("revision_vencida", referencia, admin,
                        "Revisión vencida sin respuesta",
                        $"El cliente no respondió a tiempo la revisión de \"{p.Titulo}\" (venció el {Textos.Local(vence, off)}). Te toca decidir si se aprueba o se piden ajustes.",
                        p.Id, null));
            }
        }
        return r;
    }

    /// <summary>R5. Sesión de proyecto confirmada que ya terminó y nadie marcó como cumplida.</summary>
    private async Task<List<Aviso>> SesionesSinCerrarAsync(ZOneDbContext db, List<Guid> admins, DateTimeOffset ahora, int off, CancellationToken ct)
    {
        var termino = ahora.AddHours(-opciones.Value.SesionSinCerrarHoras);
        var desde = ahora - AtrasSesionSinCerrar;
        var sesiones = await Cargar(db)
            .Where(s => s.Tipo == TipoSesion.Proyecto && s.Estado == EstadoSesion.Confirmada
                        && s.Fin < termino && s.Fin > desde)
            .OrderByDescending(s => s.Fin).Take(300).ToListAsync(ct);

        var r = new List<Aviso>();
        foreach (var s in sesiones)
        {
            var destinos = new HashSet<Guid>(admins);
            foreach (var a in ReglasProyecto.Invitados(s).Where(a => a.Rol == RolParticipante.Productor && a.Estado == EstadoParticipacion.Aceptada))
                destinos.Add(a.UsuarioId);
            foreach (var u in destinos)
                r.Add(new("cerrar_sesion", s.Id.ToString(), u,
                    "Sesión sin cerrar",
                    $"Esta sesión ya terminó y todavía no está marcada como cumplida: {Textos.Sesion(s, off)}",
                    s.ProyectoId, s.Id));
        }
        return r;
    }

    /// <summary>R6. Reservas que la base marcó como vencidas (plazo de pago o de respuesta de colaboradores).</summary>
    private async Task<List<Aviso>> VencimientosAsync(ZOneDbContext db, DateTimeOffset ahora, int off, CancellationToken ct)
    {
        var desde = ahora - AtrasVencimientos;
        var sesiones = await Cargar(db)
            .Where(s => s.Estado == EstadoSesion.Expirada && s.ExpiraEn <= ahora && s.ExpiraEn > desde)
            .OrderByDescending(s => s.ExpiraEn).Take(300).ToListAsync(ct);
        if (sesiones.Count == 0) return [];

        var ids = sesiones.Select(s => s.Id).ToList();
        var conCobro = (await db.Ordenes.AsNoTracking().Where(o => o.SesionId != null && ids.Contains(o.SesionId.Value))
            .Select(o => o.SesionId).ToListAsync(ct)).OfType<long>().ToHashSet();
        var usuarios = await UsuariosDeClientesAsync(db, sesiones.Select(ClienteDe).OfType<Guid>(), ct);

        var r = new List<Aviso>();
        foreach (var s in sesiones)
        {
            var pago = conCobro.Contains(s.Id);
            var destinos = new HashSet<Guid>();
            Guid? usuarioCliente = null;
            if (ClienteDe(s) is { } c && usuarios.TryGetValue(c, out var usuario)) { destinos.Add(usuario); usuarioCliente = usuario; }
            // Con cobro vencido avisamos a quienes ya habían aceptado; con respuestas vencidas, también a quienes no respondieron
            foreach (var a in ReglasProyecto.Invitados(s).Where(a => a.Estado == EstadoParticipacion.Aceptada || (!pago && a.Estado == EstadoParticipacion.Expirada)))
                destinos.Add(a.UsuarioId);

            var canceladoProyecto = s.Proyecto?.Estado == EstadoProyecto.Cancelado ? " El proyecto también quedó cancelado." : "";
            var detalle = $"Reserva: {Textos.Sesion(s, off)}";
            foreach (var u in destinos)
            {
                // El motivo del pago solo lo ve el cliente; los colaboradores reciben un texto neutro
                var esCliente = usuarioCliente == u;
                var titulo = esCliente ? (pago ? "Venció el plazo de pago" : "Venció el plazo para responder") : "Sesión cancelada";
                var causa = esCliente
                    ? (pago ? "No se registró el pago a tiempo, la reserva se canceló y la sala quedó libre."
                            : "No todos los colaboradores respondieron a tiempo, la reserva se canceló y la sala quedó libre.")
                    : "La sesión no se confirmó a tiempo, se canceló y la sala quedó libre.";
                r.Add(new("sesion_vencida", s.Id.ToString(), u, titulo, $"{causa}{canceladoProyecto} {detalle}", s.ProyectoId, s.Id));
            }
        }
        return r;
    }

    private async Task LimpiarAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZOneDbContext>();
        var corte = time.GetUtcNow().AddDays(-Math.Max(15, opciones.Value.RetencionDias));
        var n = await db.AvisosEnviados.Where(a => a.CreadoEn < corte).ExecuteDeleteAsync(ct);
        if (n > 0) logger.LogInformation("Se limpiaron {N} registros viejos de avisos.", n);
    }
}
