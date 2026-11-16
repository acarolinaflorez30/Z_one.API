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
    Task<SesionProyectoDto> CancelarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<SesionProyectoDto> CumplirAsync(long id, string resena, Actor actor, ContextoPeticion ctx, CancellationToken ct);

    /// <summary>
    /// Valida y arma una sesión de proyecto con sus invitaciones y la agrega al contexto (NO guarda).
    /// La usan ProgramarAsync y la creación del proyecto (primera sesión). El proyecto debe traer Servicio y Cliente.
    /// </summary>
    Task<Sesion> AgregarAsync(Proyecto p, ProgramarSesionRequest r, bool esPrimera, Actor actor, ContextoPeticion ctx, CancellationToken ct);

    /// <summary>Después de guardar la sesión: avisa a los invitados o, si no hay ninguno, la hace avanzar (no guarda).</summary>
    Task CompletarProgramacionAsync(Sesion sesion, Actor actor, CancellationToken ct);
}

public sealed class SesionesProyectoService(
    ZOneDbContext db,
    IAuditoriaService auditoria,
    IOptions<ProyectoOptions> opciones,
    INotificacionesService notificaciones,
    IInvitacionesService invitaciones,
    SesionVistaBuilder vistas,
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
            .FirstOrDefaultAsync(p => p.Id == id, ct)
        ?? throw ApiException.NoEncontrado("No existe ese proyecto.");

    private async Task<Sesion> CargarSesionAsync(long id, CancellationToken ct) =>
        await db.Sesiones
            .Include(s => s.Sala)
            .Include(s => s.Aceptaciones)
            .FirstOrDefaultAsync(s => s.Id == id && s.Tipo == TipoSesion.Proyecto, ct)
        ?? throw ApiException.NoEncontrado("No existe esa sesión.");

    private static bool EsDueno(Proyecto p, Identidad yo) => yo.ClienteId is { } c && c == p.ClienteId;

    private async Task<(Proyecto Proyecto, Identidad Yo)> AccesoAsync(long proyectoId, Actor actor, CancellationToken ct)
    {
        var p = await CargarProyectoAsync(proyectoId, ct);
        var yo = await db.ResolverAsync(actor, ct);
        if (!yo.EsAdmin && !EsDueno(p, yo) && !await db.EsInvitadoAsync(p.Id, actor.UsuarioId, ct))
            throw ApiException.NoEncontrado("No existe esa sesión.");
        return (p, yo);
    }

    private async Task<SesionProyectoDto> DtoAsync(long sesionId, Proyecto p, Identidad yo, CancellationToken ct)
    {
        // Con seguimiento: el proyecto ya está cargado y EF enlaza sesion.Proyecto solo.
        var s = await db.Sesiones.Include(x => x.Sala).Include(x => x.Aceptaciones).FirstAsync(x => x.Id == sesionId, ct);
        return (await vistas.ConstruirAsync([s], yo, EsDueno(p, yo), ct))[0];
    }

    // ───────────────────────── Programar ─────────────────────────

    /// <summary>
    /// La programan solo el cliente dueño o el administrador. Dura 3 horas desde el inicio (la extra es de duración variable).
    /// Los colaboradores se invitan en esta misma sesión; la sala queda retenida mientras responden (72 h).
    /// </summary>
    public async Task<SesionProyectoDto> ProgramarAsync(long proyectoId, ProgramarSesionRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var (p, yo) = await AccesoAsync(proyectoId, actor, ct);

        if (!yo.EsAdmin && !EsDueno(p, yo))
            throw ApiException.Prohibido("Solo el cliente o el administrador programan sesiones.");

        if (p.Estado != EstadoProyecto.Confirmado && p.Estado != EstadoProyecto.EnCurso)
            throw ApiException.Conflicto("proyecto_no_iniciado",
                "El trabajo inicia cuando se registra el pago de la primera sesión.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sesion = await AgregarAsync(p, r, esPrimera: false, actor, ctx, ct);
        await GuardarAsync(ct);
        await CompletarProgramacionAsync(sesion, actor, ct);
        await GuardarAsync(ct);
        await tx.CommitAsync(ct);

        return await DtoAsync(sesion.Id, p, yo, ct);
    }

    public async Task<Sesion> AgregarAsync(
        Proyecto p, ProgramarSesionRequest r, bool esPrimera, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var ahora = time.GetUtcNow();
        var inicio = r.Inicio.ToUniversalTime();
        var esExtra = r.EsExtra ?? false;

        // Duración: 3 h fijas; solo la sesión extra es variable (mínimo 1 h, sin máximo) y exige la hora de fin.
        DateTimeOffset fin;
        if (esExtra)
        {
            if (r.Fin is not { } finExtra) throw ApiException.Validacion("fin", "La sesión extra necesita la hora de fin.");
            fin = finExtra.ToUniversalTime();
        }
        else
        {
            fin = inicio.AddMinutes(_o.SesionDuracionMinutos);
            if (r.Fin is { } finEnviado && finEnviado.ToUniversalTime() != fin)
                throw ApiException.Validacion("fin", $"Estas sesiones duran {_o.SesionDuracionMinutos / 60m:0.##} horas desde el inicio: no envíes la hora de fin.");
        }
        ValidarVentana(inicio, fin, ahora, esExtra);

        var sesiones = new List<Sesion>();
        if (!esPrimera)
            sesiones = await db.Sesiones.AsNoTracking().Where(s => s.ProyectoId == p.Id).ToListAsync(ct);
        if (sesiones.Any(ReglasProyecto.Abierta))
            throw ApiException.Conflicto("sesion_abierta",
                "Ya hay una sesión pendiente o confirmada. Debe cumplirse o cancelarse antes de programar otra.");

        short? numero;
        EtapaProyecto etapa;

        if (esPrimera)
        {
            if (esExtra) throw ApiException.Validacion("esExtra", "La primera sesión del proyecto no puede ser extra.");
            numero = 1;
            etapa = p.EtapaActual ?? ReglasProyecto.EtapaDe(p, 1);
        }
        else if (esExtra)
        {
            if (p.Tipo == TipoProyecto.ServicioParcial)
                throw ApiException.Conflicto("sin_extra_en_parcial", "Las sesiones extra solo aplican a la canción completa.");
            // La sesión de ajuste incluida es gratis: mientras esté disponible, no se programa una extra.
            if (ReglasProyecto.Siguiente(p, sesiones) is { EsAjuste: true })
                throw ApiException.Conflicto("usa_ajuste_incluido",
                    "La sesión de ajuste incluida todavía está disponible: prográmala sin marcarla como extra.");
            numero = null;
            etapa = p.EtapaActual ?? EtapaProyecto.Revision;
        }
        else
        {
            var siguiente = ReglasProyecto.Siguiente(p, sesiones)
                ?? throw ApiException.Conflicto("sin_sesion_disponible", MotivoSinSiguiente(p, sesiones));
            numero = siguiente.Numero is { } n ? (short?)n : null;   // la sesión de ajuste no tiene número
            etapa = siguiente.Etapa;
        }

        var modalidad = esExtra ? ModalidadCobro.PorHora
            : etapa == EtapaProyecto.Revision ? ModalidadCobro.Gratis
            : ModalidadCobro.PorSesion;

        var sala = await db.Salas.FirstOrDefaultAsync(s => s.Id == r.SalaId && s.Activo && (s.Publicada || actor.EsAdmin), ct)
                   ?? throw ApiException.NoEncontrado("No existe esa sala o no está disponible.");
        PreciosService.ValidarSala(sala, modalidad);

        var invitadas = await invitaciones.CrearInvitacionesAsync(r.ColaboradorIds, modalidad, ahora, ct);
        // Solo el alquiler de sala puede ir sin colaboradores: toda sesión de un proyecto necesita al menos uno.
        if (invitadas.Count == 0)
            throw ApiException.Validacion("colaboradorIds",
                "Toda sesión de un proyecto necesita al menos un colaborador (artista o productor).");

        var vence = ahora.AddHours(_o.SesionAceptacionHoras);
        if (_o.LiberarAlInicio && inicio < vence) vence = inicio;

        var sesion = new Sesion
        {
            Tipo = TipoSesion.Proyecto,
            Sala = sala,
            Proyecto = p,
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
        foreach (var inv in invitadas) sesion.Aceptaciones.Add(inv);

        db.Sesiones.Add(sesion);
        auditoria.Registrar(actor.UsuarioId, "sesion_programada", "proyecto", p.Id.ToString(),
            new { salaId = sala.Id, inicio, fin, numero, etapa = etapa.ToString(), esExtra, invitados = invitadas.Count }, ctx.Ip);
        return sesion;
    }

    public async Task CompletarProgramacionAsync(Sesion sesion, Actor actor, CancellationToken ct)
    {
        // AgregarAsync ya garantiza al menos un invitado en las sesiones de proyecto.
        var invitados = ReglasProyecto.Invitados(sesion).ToList();
        invitaciones.NotificarInvitaciones(sesion, invitados, actor.UsuarioId);
        await Task.CompletedTask;
    }

    private static string MotivoSinSiguiente(Proyecto p, IReadOnlyCollection<Sesion> sesiones)
    {
        if (ReglasProyecto.Cumplidas(sesiones) >= ReglasProyecto.SesionesDelPaquete(p))
        {
            if (ReglasProyecto.AjustesPendientes(p, sesiones) > 0)
                return "La sesión de ajuste incluida ya se usó. Para otra ronda de ajustes, programa una sesión extra.";
            return "Ya se cumplieron todas las sesiones del paquete. Para más tiempo, programa una sesión extra.";
        }
        return "Todavía no toca programar otra sesión.";
    }

    private void ValidarVentana(DateTimeOffset inicio, DateTimeOffset fin, DateTimeOffset ahora, bool esExtra)
    {
        if (fin <= inicio)
            throw ApiException.Validacion("fin", "La hora de fin debe ser posterior a la de inicio.");
        if (inicio < ahora.AddMinutes(_o.MinAnticipacionMinutos))
            throw ApiException.Validacion("inicio", $"Programa con al menos {_o.MinAnticipacionMinutos} minutos de anticipación.");
        if (inicio > ahora.AddDays(_o.MaxDiasAnticipacion))
            throw ApiException.Validacion("inicio", $"No se puede programar con más de {_o.MaxDiasAnticipacion} días de anticipación.");

        var minutos = (fin - inicio).TotalMinutes;
        if (esExtra && minutos < _o.ExtraMinMinutos)
            throw ApiException.Validacion("fin", $"La sesión extra dura mínimo {_o.ExtraMinMinutos / 60.0:0.##} hora(s).");

        var bloque = _o.GranularidadMinutos;
        var alineado = inicio.UtcDateTime is { Second: 0, Millisecond: 0 } u && u.Minute % bloque == 0;
        if (!alineado || minutos % bloque != 0)
            throw ApiException.Validacion("inicio", $"Las sesiones se programan en bloques de {bloque} minutos (por ejemplo 3:00 o 3:30).");
    }

    // ───────────────────────── Consultar ─────────────────────────

    public async Task<SesionProyectoDto> ObtenerAsync(long id, Actor actor, CancellationToken ct)
    {
        var proyectoId = await db.Sesiones.AsNoTracking()
            .Where(x => x.Id == id && x.Tipo == TipoSesion.Proyecto)
            .Select(x => x.ProyectoId)
            .FirstOrDefaultAsync(ct)
            ?? throw ApiException.NoEncontrado("No existe esa sesión.");
        var (p, yo) = await AccesoAsync(proyectoId, actor, ct);
        return await DtoAsync(id, p, yo, ct);
    }

    // ───────────────────────── Cancelar (solo antes de pagar) ─────────────────────────

    /// <summary>
    /// El cliente dueño o el administrador cancelan una sesión que espera colaboradores o espera el pago.
    /// Una vez pagada (confirmada) no se cancela. Si era la primera sesión de un proyecto pendiente, el proyecto se cancela.
    /// </summary>
    public async Task<SesionProyectoDto> CancelarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var sesion = await CargarSesionAsync(id, ct);
        var (p, yo) = await AccesoAsync(sesion.ProyectoId ?? 0, actor, ct);

        if (!yo.EsAdmin && !EsDueno(p, yo))
            throw ApiException.Prohibido("Solo el cliente o el administrador cancelan sesiones.");

        if (sesion.Estado == EstadoSesion.Confirmada)
            throw ApiException.Conflicto("sesion_confirmada", "Una sesión confirmada (ya pagada) no se puede cancelar.");
        if (sesion.Estado is not (EstadoSesion.PendienteAceptacion or EstadoSesion.PendientePago))
            throw ApiException.Conflicto("sesion_no_vigente", "Esa sesión ya no está vigente.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        sesion.Estado = EstadoSesion.Cancelada;
        foreach (var a in sesion.Aceptaciones.Where(a => a.Estado == EstadoParticipacion.Pendiente))
            a.Estado = EstadoParticipacion.Expirada;
        var cobros = await db.Ordenes.Where(o => o.SesionId == sesion.Id && o.Estado == EstadoOrden.Pendiente).ToListAsync(ct);
        foreach (var o in cobros) o.Estado = EstadoOrden.Expirada;

        var ahora = time.GetUtcNow();
        var aviso = $"El cliente canceló {Textos.Sesion(sesion, _o.UtcOffsetHoras)}";
        notificaciones.NotificarUsuarios(
            ReglasProyecto.Invitados(sesion).Where(a => a.Estado is EstadoParticipacion.Aceptada or EstadoParticipacion.Expirada).Select(a => a.UsuarioId),
            actor.UsuarioId, "sesion_cancelada", "Sesión cancelada", aviso, p.Id, sesion.Id);
        auditoria.Registrar(actor.UsuarioId, "sesion_cancelada", "sesion", id.ToString(), null, ctx.Ip);
        await GuardarAsync(ct);

        // El proyecto que todavía no empezó (primera sesión sin pagar) se cancela con ella: hay que crearlo de nuevo.
        if (p.Estado == EstadoProyecto.Pendiente)
        {
            p.Estado = EstadoProyecto.Cancelado;
            p.CanceladoEn = ahora;
            p.ActualizadoEn = ahora;
            await GuardarAsync(ct);
        }

        await tx.CommitAsync(ct);
        return await DtoAsync(id, p, yo, ct);
    }

    // ───────────────────────── Cumplir: aquí se gasta la sesión del paquete ─────────────────────────

    /// <summary>
    /// El productor que aceptó ESA sesión (o el administrador, siempre) la marca como terminada, con una breve reseña,
    /// cuando termina su horario. Si la sesión no tiene productor, solo la cierra el administrador. Se avisa al cliente
    /// y al administrador. Con eso se gasta la sesión del paquete y avanza el proyecto:
    ///  · Servicio parcial: queda finalizado. No hay revisión, aprobación ni ajustes.
    ///  · Canción completa, sesiones 1 a 3: pasa a la siguiente etapa.
    ///  · Canción completa, sesión 4 (o la de ajuste): pasa a revisión y el cliente tiene 24 horas para aprobar o no.
    ///  · Sesión extra de trabajo: solo se gasta; el proyecto sigue donde estaba.
    /// </summary>
    public async Task<SesionProyectoDto> CumplirAsync(long id, string resena, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var sesion = await CargarSesionAsync(id, ct);
        var (p, yo) = await AccesoAsync(sesion.ProyectoId ?? 0, actor, ct);

        var esProductorDeLaSesion = sesion.Aceptaciones.Any(a =>
            a.UsuarioId == actor.UsuarioId && a.Rol == RolParticipante.Productor && a.Estado == EstadoParticipacion.Aceptada);
        if (!yo.EsAdmin && !esProductorDeLaSesion)
            throw ApiException.Prohibido("Solo el productor de esta sesión o el administrador la marcan como cumplida.");

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

                var hora = Textos.Local(vence, _o.UtcOffsetHoras);
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
