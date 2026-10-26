using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Options;

namespace ZOne.Api.Services;

public interface ISesionesProyectoService
{
    Task<SesionProyectoDto> ProgramarAsync(long proyectoId, ProgramarSesionRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<SesionProyectoDto> ObtenerAsync(long id, Actor actor, CancellationToken ct);
    Task<SesionProyectoDto> ResponderAsync(long id, bool aceptar, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<SesionProyectoDto> CancelarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<SesionProyectoDto> CumplirAsync(long id, string resena, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<PaginaDto<AceptacionPendienteDto>> PendientesAsync(Actor actor, int pagina, int tamano, CancellationToken ct);
}

public sealed class SesionesProyectoService(
    ZOneDbContext db,
    IAuditoriaService auditoria,
    IOptions<ProyectoOptions> opciones,
    INotificacionesService notificaciones,
    TimeProvider time) : ISesionesProyectoService
{
    private readonly ProyectoOptions _o = opciones.Value;

    // ───────────────────────── Utilidades ─────────────────────────

    private Task LiberarVencidosAsync(CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("select public.expirar_vencidos()", ct);

    private async Task GuardarAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ErroresDb.Traducir(ex) is not null) { throw ErroresDb.Traducir(ex)!; }
    }

    private async Task<Proyecto> CargarProyectoAsync(long id, CancellationToken ct) =>
        await db.Proyectos
            .Include(p => p.Cliente)
            .Include(p => p.Servicio)
            .Include(p => p.Participantes).ThenInclude(x => x.Colaborador).ThenInclude(c => c.Usuario)
            .FirstOrDefaultAsync(p => p.Id == id, ct)
        ?? throw ApiException.NoEncontrado("No existe ese proyecto.");

    private async Task<Sesion> CargarSesionAsync(long id, CancellationToken ct) =>
        await db.Sesiones
            .Include(s => s.Sala)
            .Include(s => s.Aceptaciones)
            .FirstOrDefaultAsync(s => s.Id == id && s.Tipo == TipoSesion.Proyecto, ct)
        ?? throw ApiException.NoEncontrado("No existe esa sesión.");

    private static bool EsDueno(Proyecto p, Identidad yo) => yo.ClienteId is { } c && c == p.ClienteId;

    private static bool EsProductorAceptado(Proyecto p, Identidad yo) =>
        yo.ColaboradorId is { } k && p.Participantes.Any(x =>
            x.ColaboradorId == k && x.Rol == RolParticipante.Productor && x.Estado == EstadoParticipacion.Aceptada);

    private static bool Participa(Proyecto p, Identidad yo) =>
        yo.ColaboradorId is { } k && p.Participantes.Any(x => x.ColaboradorId == k);

    private async Task<(Proyecto Proyecto, Identidad Yo)> AccesoAsync(long proyectoId, Actor actor, CancellationToken ct)
    {
        var p = await CargarProyectoAsync(proyectoId, ct);
        var yo = await db.ResolverAsync(actor, ct);
        if (!yo.EsAdmin && !EsDueno(p, yo) && !Participa(p, yo))
            throw ApiException.NoEncontrado("No existe esa sesión.");
        return (p, yo);
    }

    private async Task<SesionProyectoDto> DtoAsync(long sesionId, Proyecto p, Identidad yo, CancellationToken ct)
    {
        var s = await db.Sesiones.AsNoTracking()
            .Include(x => x.Sala).Include(x => x.Aceptaciones)
            .FirstAsync(x => x.Id == sesionId, ct);
        return ReglasProyecto.MapSesion(s, ReglasProyecto.Nombres(p, yo.EsAdmin), time.GetUtcNow());
    }

    // ───────────────────────── Programar ─────────────────────────

    /// <summary>
    /// La programan el administrador, el productor o el cliente; los demás deben aceptarla (72 h).
    /// Mientras espera, la sala queda retenida. Solo hay una sesión abierta a la vez y se numeran en orden.
    /// </summary>
    public async Task<SesionProyectoDto> ProgramarAsync(long proyectoId, ProgramarSesionRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var (p, yo) = await AccesoAsync(proyectoId, actor, ct);

        if (!yo.EsAdmin && !EsDueno(p, yo) && !EsProductorAceptado(p, yo))
            throw ApiException.Prohibido("Solo el administrador, el productor o el cliente programan sesiones.");

        if (p.Estado != EstadoProyecto.Confirmado && p.Estado != EstadoProyecto.EnCurso)
            throw ApiException.Conflicto("proyecto_no_iniciado",
                "El trabajo inicia cuando todos los participantes aceptan y se registra el pago del servicio.");

        var ahora = time.GetUtcNow();
        var inicio = r.Inicio.ToUniversalTime();
        var fin = r.Fin.ToUniversalTime();
        ValidarVentana(inicio, fin, ahora);

        var sesiones = await db.Sesiones.AsNoTracking().Where(s => s.ProyectoId == p.Id).ToListAsync(ct);
        if (sesiones.Any(ReglasProyecto.Abierta))
            throw ApiException.Conflicto("sesion_abierta",
                "Ya hay una sesión pendiente o confirmada. Debe cumplirse o cancelarse antes de programar otra.");

        short? numero;
        EtapaProyecto etapa;
        var esExtra = r.EsExtra ?? false;

        if (esExtra && p.Tipo == TipoProyecto.ServicioParcial)
            throw ApiException.Conflicto("sin_extra_en_parcial", "Las sesiones extra solo aplican a la canción completa.");

        if (esExtra)
        {
            // La sesión de ajuste incluida no se cobra: mientras esté disponible, no se gasta una extra.
            if (ReglasProyecto.Siguiente(p, sesiones) is { EsAjuste: true })
                throw ApiException.Conflicto("usa_ajuste_incluido",
                    "La sesión de ajuste incluida todavía está disponible: prográmala sin marcarla como extra.");

            var ordenes = await db.Ordenes.AsNoTracking()
                .Where(o => o.ProyectoId == p.Id && o.Concepto == ConceptoOrden.SesionExtra).ToListAsync(ct);
            if (ReglasProyecto.Creditos(ordenes, sesiones) <= 0)
                throw ApiException.Conflicto("sin_credito_extra", "No hay una sesión extra pagada disponible. Solicítala y registra su pago primero.");
            numero = null;
            etapa = p.EtapaActual ?? EtapaProyecto.Revision;
        }
        else
        {
            var siguiente = ReglasProyecto.Siguiente(p, sesiones)
                ?? throw ApiException.Conflicto("sin_sesion_disponible", MotivoSinSiguiente(p, sesiones));
            numero = siguiente.Numero is { } numeroSiguiente ? (short?)numeroSiguiente : null;   // la sesión de ajuste no tiene número
            etapa = siguiente.Etapa;
        }

        var sala = await db.Salas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.SalaId && s.Activo, ct)
                   ?? throw ApiException.NoEncontrado("No existe esa sala o no está disponible.");

        // Por defecto aceptan todos los demás: el cliente y los colaboradores aceptados (menos quien programa).
        var asistentes = new HashSet<Guid> { p.Cliente.UsuarioId };
        foreach (var aceptado in p.Participantes.Where(x => x.Estado == EstadoParticipacion.Aceptada))
            asistentes.Add(aceptado.Colaborador.UsuarioId);
        asistentes.Remove(actor.UsuarioId);
        if (asistentes.Count == 0)
            throw ApiException.Conflicto("sin_asistentes", "No hay nadie más que deba aceptar la sesión.");

        var vence = ahora.AddHours(_o.SesionAceptacionHoras);
        if (_o.LiberarAlInicio && inicio < vence) vence = inicio;

        var sesion = new Sesion
        {
            Tipo = TipoSesion.Proyecto,
            SalaId = sala.Id,
            ProyectoId = p.Id,
            NumeroSesion = numero,
            Etapa = etapa,
            EsExtra = esExtra,
            Inicio = inicio,
            Fin = fin,
            Estado = EstadoSesion.PendienteAceptacion,
            ExpiraEn = vence,
            CreadaPor = actor.UsuarioId,
            CreadoEn = ahora
        };
        foreach (var usuarioId in asistentes)
            sesion.Aceptaciones.Add(new AceptacionSesion { UsuarioId = usuarioId, Estado = EstadoParticipacion.Pendiente });

        db.Sesiones.Add(sesion);
        auditoria.Registrar(actor.UsuarioId, "sesion_programada", "proyecto", p.Id.ToString(),
            new { salaId = sala.Id, inicio, fin, numero, etapa = etapa.ToString(), esExtra }, ctx.Ip);
        await GuardarAsync(ct);

        return await DtoAsync(sesion.Id, p, yo, ct);
    }

    private static string MotivoSinSiguiente(Proyecto p, IReadOnlyCollection<Sesion> sesiones)
    {
        if (ReglasProyecto.Cumplidas(sesiones) >= ReglasProyecto.SesionesDelPaquete(p))
        {
            if (ReglasProyecto.AjustesPendientes(p, sesiones) > 0)
                return "La sesión de ajuste incluida ya se usó. Para otra ronda de ajustes, solicita una sesión extra.";
            return "Ya se cumplieron todas las sesiones del paquete. Para más tiempo, solicita una sesión extra.";
        }
        return "Todavía no toca programar otra sesión.";
    }

    private void ValidarVentana(DateTimeOffset inicio, DateTimeOffset fin, DateTimeOffset ahora)
    {
        if (fin <= inicio)
            throw ApiException.Validacion("fin", "La hora de fin debe ser posterior a la de inicio.");
        if (inicio < ahora.AddMinutes(_o.MinAnticipacionMinutos))
            throw ApiException.Validacion("inicio", $"Programa con al menos {_o.MinAnticipacionMinutos} minutos de anticipación.");
        if (inicio > ahora.AddDays(_o.MaxDiasAnticipacion))
            throw ApiException.Validacion("inicio", $"No se puede programar con más de {_o.MaxDiasAnticipacion} días de anticipación.");

        var minutos = (fin - inicio).TotalMinutes;
        if (minutos < _o.SesionMinMinutos)
            throw ApiException.Validacion("fin", $"La duración mínima es de {_o.SesionMinMinutos / 60.0:0.##} hora(s).");
        if (minutos > _o.SesionMaxMinutos)
            throw ApiException.Validacion("fin", $"La duración máxima es de {_o.SesionMaxMinutos / 60.0:0.##} horas.");

        var bloque = _o.GranularidadMinutos;
        var alineado = inicio.UtcDateTime is { Second: 0, Millisecond: 0 } u && u.Minute % bloque == 0;
        if (!alineado || minutos % bloque != 0)
            throw ApiException.Validacion("inicio", $"Las sesiones se programan en bloques de {bloque} minutos (por ejemplo 3:00 o 3:30).");
    }

    // ───────────────────────── Consultar ─────────────────────────

    public async Task<SesionProyectoDto> ObtenerAsync(long id, Actor actor, CancellationToken ct)
    {
        var s = await db.Sesiones.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Tipo == TipoSesion.Proyecto, ct)
                ?? throw ApiException.NoEncontrado("No existe esa sesión.");
        var (p, yo) = await AccesoAsync(s.ProyectoId ?? 0, actor, ct);
        return await DtoAsync(id, p, yo, ct);
    }

    public async Task<PaginaDto<AceptacionPendienteDto>> PendientesAsync(Actor actor, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);
        var ahora = time.GetUtcNow();

        var consulta =
            from a in db.Aceptaciones.AsNoTracking()
            where a.UsuarioId == actor.UsuarioId && a.Estado == EstadoParticipacion.Pendiente
            join s in db.Sesiones.AsNoTracking() on a.SesionId equals s.Id
            where s.Estado == EstadoSesion.PendienteAceptacion && s.ExpiraEn > ahora
            select new
            {
                SesionId = s.Id,
                ProyectoId = s.ProyectoId ?? 0,
                Proyecto = s.Proyecto!.Titulo,
                s.NumeroSesion,
                s.Etapa,
                s.EsExtra,
                Sala = s.Sala.Nombre,
                s.Inicio,
                s.Fin,
                s.ExpiraEn
            };

        var total = await consulta.CountAsync(ct);
        var filas = await consulta.OrderBy(f => f.ExpiraEn).Skip((pagina - 1) * tamano).Take(tamano).ToListAsync(ct);

        var items = filas.Select(f => new AceptacionPendienteDto(
            f.SesionId, f.ProyectoId, f.Proyecto, f.NumeroSesion,
            f.Etapa is null ? null : Convenciones.Api(f.Etapa.Value),
            f.EsExtra, f.Sala, f.Inicio, f.Fin, f.ExpiraEn)).ToList();

        return new PaginaDto<AceptacionPendienteDto>(items, total, pagina, tamano);
    }

    // ───────────────────────── Aceptar o rechazar ─────────────────────────

    /// <summary>Si todos aceptan, la sesión queda confirmada. Si alguien rechaza, se cancela y la sala se libera.</summary>
    public async Task<SesionProyectoDto> ResponderAsync(long id, bool aceptar, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var sesion = await CargarSesionAsync(id, ct);
        var (p, yo) = await AccesoAsync(sesion.ProyectoId ?? 0, actor, ct);

        var ahora = time.GetUtcNow();
        if (sesion.Estado != EstadoSesion.PendienteAceptacion)
            throw ApiException.Conflicto("sesion_no_pendiente", "Esa sesión ya no está esperando respuestas.");
        if (sesion.ExpiraEn is { } vence && vence <= ahora)
            throw ApiException.Conflicto("plazo_vencido", "El plazo para responder venció y la sala se liberó.");

        var fila = sesion.Aceptaciones.FirstOrDefault(a => a.UsuarioId == actor.UsuarioId)
                   ?? throw ApiException.Prohibido("No eres de quienes deben responder esta sesión.");
        if (fila.Estado != EstadoParticipacion.Pendiente)
            throw ApiException.Conflicto("ya_respondida", "Ya respondiste esta sesión.");

        fila.Estado = aceptar ? EstadoParticipacion.Aceptada : EstadoParticipacion.Rechazada;
        fila.RespondidaEn = ahora;

        if (!aceptar)
        {
            sesion.Estado = EstadoSesion.Cancelada;   // alguien no puede: se libera la sala y se reprograma
        }
        else if (sesion.Aceptaciones.All(a => a.Estado == EstadoParticipacion.Aceptada))
        {
            sesion.Estado = EstadoSesion.Confirmada;
            sesion.ExpiraEn = null;
        }

        auditoria.Registrar(actor.UsuarioId, aceptar ? "sesion_aceptada" : "sesion_rechazada", "sesion", id.ToString(),
            new { estado = sesion.Estado.ToString() }, ctx.Ip);
        await GuardarAsync(ct);
        return await DtoAsync(id, p, yo, ct);
    }

    // ───────────────────────── Cancelar (solo antes de confirmarse) ─────────────────────────

    public async Task<SesionProyectoDto> CancelarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var sesion = await CargarSesionAsync(id, ct);
        var (p, yo) = await AccesoAsync(sesion.ProyectoId ?? 0, actor, ct);

        if (!yo.EsAdmin && sesion.CreadaPor != actor.UsuarioId)
            throw ApiException.Prohibido("Solo quien programó la sesión o el administrador pueden cancelarla.");

        if (sesion.Estado == EstadoSesion.Confirmada)
            throw ApiException.Conflicto("sesion_confirmada", "Una sesión confirmada no se puede cancelar.");
        if (sesion.Estado != EstadoSesion.PendienteAceptacion)
            throw ApiException.Conflicto("sesion_no_vigente", "Esa sesión ya no está vigente.");

        sesion.Estado = EstadoSesion.Cancelada;
        auditoria.Registrar(actor.UsuarioId, "sesion_cancelada", "sesion", id.ToString(), null, ctx.Ip);
        await GuardarAsync(ct);
        return await DtoAsync(id, p, yo, ct);
    }

    // ───────────────────────── Cumplir: aquí se gasta la sesión del paquete ─────────────────────────

    /// <summary>
    /// El productor (o el administrador) marca la sesión como terminada, con una breve reseña, cuando termina su horario.
    /// Se avisa al cliente y al administrador. Con eso se gasta la sesión del paquete y avanza el proyecto:
    ///  · Servicio parcial: queda finalizado. No hay revisión, aprobación ni ajustes.
    ///  · Canción completa, sesiones 1 a 3: pasa a la siguiente etapa.
    ///  · Canción completa, sesión 4 (o la de ajuste, si el cliente la pidió): pasa a revisión y el cliente tiene
    ///    24 horas para aprobar o no. Si no responde, decide el administrador desde su panel.
    ///  · Sesión extra de trabajo: solo se gasta; el proyecto sigue donde estaba.
    /// </summary>
    public async Task<SesionProyectoDto> CumplirAsync(long id, string resena, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var sesion = await CargarSesionAsync(id, ct);
        var (p, yo) = await AccesoAsync(sesion.ProyectoId ?? 0, actor, ct);

        if (!yo.EsAdmin && !EsProductorAceptado(p, yo))
            throw ApiException.Prohibido("Solo el productor del proyecto o el administrador marcan una sesión como cumplida.");

        if (sesion.Estado != EstadoSesion.Confirmada)
            throw ApiException.Conflicto("sesion_no_confirmada", "Solo se marca como cumplida una sesión confirmada.");

        var ahora = time.GetUtcNow();
        if (ahora < sesion.Fin)
            throw ApiException.Conflicto("sesion_en_curso", "La sesión se marca como cumplida cuando termina su horario.");

        sesion.Estado = EstadoSesion.Cumplida;
        sesion.Resena = Entrada.Texto(resena, "resena", 5, 2000);
        sesion.CumplidaEn = ahora;
        sesion.CumplidaPor = actor.UsuarioId;

        // Estado de las sesiones del proyecto ya con ésta cumplida (todavía no se guardó).
        var otras = await db.Sesiones.AsNoTracking().Where(s => s.ProyectoId == p.Id && s.Id != sesion.Id).ToListAsync(ct);
        var todas = otras.Append(sesion).ToList();

        var descripcionSesion = sesion.EsExtra ? "extra"
            : sesion.Etapa == EtapaProyecto.Revision ? "de ajuste"
            : sesion.NumeroSesion?.ToString() ?? "";

        string tipoAviso = "sesion_cumplida";
        string tituloAviso = "Sesión cumplida";
        string mensajeAviso = $"Se cumplió la sesión {descripcionSesion} de \"{p.Titulo}\".";

        if (p.Tipo == TipoProyecto.ServicioParcial)
        {
            // Un servicio parcial termina al cumplirse su única sesión: sin revisión ni ajustes.
            p.Estado = EstadoProyecto.Finalizado;
            tipoAviso = "servicio_finalizado";
            tituloAviso = "Servicio finalizado";
            mensajeAviso = $"La sesión de \"{p.Titulo}\" se cumplió y el servicio quedó finalizado.";
        }
        else
        {
            if (p.Estado == EstadoProyecto.Confirmado) p.Estado = EstadoProyecto.EnCurso;

            var abreRevision = false;
            if (sesion.Etapa == EtapaProyecto.Revision)
            {
                // Terminó una sesión de ajuste (incluida o extra): vuelve a revisión cuando ya no queda ningún ajuste por hacer.
                abreRevision = ReglasProyecto.AjustesPendientes(p, todas) <= 0;
            }
            else if (!sesion.EsExtra)
            {
                var hechas = ReglasProyecto.Cumplidas(todas);
                if (hechas >= ReglasProyecto.SesionesDelPaquete(p)) abreRevision = true;          // terminó la 4.ª etapa
                else p.EtapaActual = ReglasProyecto.EtapaDe(p, hechas + 1);                      // pasa a la siguiente etapa
            }

            if (abreRevision)
            {
                var vence = ahora.AddHours(_o.RevisionClienteHoras);
                p.Estado = EstadoProyecto.EnRevision;
                p.EtapaActual = EtapaProyecto.Revision;
                p.RevisionVenceEn = vence;

                var hora = vence.ToOffset(TimeSpan.FromHours(_o.UtcOffsetHoras)).ToString("dd/MM/yyyy HH:mm");
                tipoAviso = "revision_pendiente";
                tituloAviso = "Trabajo listo para revisión";
                mensajeAviso = $"El trabajo de \"{p.Titulo}\" está listo. El cliente tiene hasta {hora} para aprobarlo o pedir ajustes; " +
                               "si no responde, lo define el administrador.";
            }
        }
        p.ActualizadoEn = ahora;

        auditoria.Registrar(actor.UsuarioId, "sesion_cumplida", "sesion", id.ToString(),
            new { proyectoId = p.Id, numero = sesion.NumeroSesion, extra = sesion.EsExtra, estadoProyecto = p.Estado.ToString() }, ctx.Ip);
        await notificaciones.NotificarProyectoAsync(p, actor.UsuarioId, Destinatarios.Cliente | Destinatarios.Administradores,
            tipoAviso, tituloAviso, mensajeAviso, id, ct);
        await GuardarAsync(ct);
        return await DtoAsync(id, p, yo, ct);
    }
}
