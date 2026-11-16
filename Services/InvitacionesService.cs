using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Options;

namespace ZOne.Api.Services;

public interface IInvitacionesService
{
    /// <summary>Crea las filas de invitación de una sesión nueva (sin guardar). Valida que cada colaborador se pueda invitar.</summary>
    Task<List<AceptacionSesion>> CrearInvitacionesAsync(
        IReadOnlyCollection<Guid>? colaboradorIds, ModalidadCobro modalidad, DateTimeOffset ahora, CancellationToken ct);

    /// <summary>Avisa a los colaboradores recién invitados. Se llama después de guardar la sesión (necesita su id).</summary>
    void NotificarInvitaciones(Sesion sesion, IEnumerable<AceptacionSesion> invitaciones, Guid actorUsuarioId);

    /// <summary>
    /// La sesión deja de esperar colaboradores: pasa a pendiente de pago (con su cobro a las tarifas de hoy)
    /// o, si es el ajuste incluido (gratis), directo a confirmada. No guarda. Requiere Sala y Aceptaciones cargadas.
    /// </summary>
    Task AvanzarAsync(Sesion sesion, CancellationToken ct);

    Task<PaginaDto<InvitacionDto>> ListarAsync(Actor actor, string? estado, int pagina, int tamano, CancellationToken ct);
    Task<SesionInvitadosDto> ResponderAsync(long sesionId, bool aceptar, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<SesionInvitadosDto> InvitarAsync(long sesionId, Guid colaboradorId, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<SesionInvitadosDto> ConfirmarAsync(long sesionId, Actor actor, ContextoPeticion ctx, CancellationToken ct);
}

/// <summary>
/// Invitaciones por sesión, iguales para las sesiones de un proyecto y para los alquileres con colaboradores:
///   · cada colaborador acepta o rechaza esa sesión (72 h);
///   · si todos aceptan, la sesión avanza sola a pendiente de pago;
///   · si alguien rechaza, la sesión espera y el cliente (o el administrador) decide: invitar a otro,
///     confirmar con quienes aceptaron (mínimo uno) o cancelar.
/// </summary>
public sealed class InvitacionesService(
    ZOneDbContext db,
    IPreciosService precios,
    INotificacionesService notificaciones,
    IAuditoriaService auditoria,
    IOptions<ProyectoOptions> proyectoOpt,
    IOptions<AlquilerOptions> alquilerOpt,
    TimeProvider time) : IInvitacionesService
{
    private ProyectoOptions Po => proyectoOpt.Value;

    private Task LiberarVencidosAsync(CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("select public.expirar_vencidos()", ct);

    private async Task<Sesion> CargarAsync(long id, CancellationToken ct) =>
        await db.Sesiones
            .Include(s => s.Sala)
            .Include(s => s.Aceptaciones)
            .Include(s => s.Cliente)
            .Include(s => s.Proyecto).ThenInclude(p => p!.Servicio)
            .Include(s => s.Proyecto).ThenInclude(p => p!.Cliente)
            .FirstOrDefaultAsync(s => s.Id == id, ct)
        ?? throw ApiException.NoEncontrado("No existe esa sesión.");

    private static Guid ClienteUsuarioId(Sesion s) =>
        s.Tipo == TipoSesion.Alquiler ? s.Cliente!.UsuarioId : s.Proyecto!.Cliente.UsuarioId;

    private static string ClienteNombre(Sesion s) =>
        s.Tipo == TipoSesion.Alquiler ? s.Cliente!.Nombre : s.Proyecto!.Cliente.Nombre;

    private async Task GuardarAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ErroresDb.Traducir(ex) is not null) { throw ErroresDb.Traducir(ex)!; }
    }

    // ───────────────────────── Crear y avanzar ─────────────────────────

    public async Task<List<AceptacionSesion>> CrearInvitacionesAsync(
        IReadOnlyCollection<Guid>? colaboradorIds, ModalidadCobro modalidad, DateTimeOffset ahora, CancellationToken ct)
    {
        var invitables = await precios.InvitablesAsync(colaboradorIds, modalidad, Po.MaxInvitadosPorSesion, ct);
        return invitables.Select(p => new AceptacionSesion
        {
            UsuarioId = p.UsuarioId,
            Rol = ReglasProyecto.RolDe(p.Perfil),
            Estado = EstadoParticipacion.Pendiente,
            EnviadaEn = ahora
        }).ToList();
    }

    public void NotificarInvitaciones(Sesion s, IEnumerable<AceptacionSesion> invitaciones, Guid actorUsuarioId) =>
        notificaciones.NotificarUsuarios(
            invitaciones.Select(i => i.UsuarioId), actorUsuarioId,
            "invitacion_sesion", "Te invitaron a una sesión",
            $"{ClienteNombre(s)} te invita a {Textos.Sesion(s, Po.UtcOffsetHoras)} Tienes {Po.SesionAceptacionHoras} h para responder.",
            s.ProyectoId, s.Id);

    public async Task AvanzarAsync(Sesion s, CancellationToken ct)
    {
        var ahora = time.GetUtcNow();
        var modalidad = ReglasProyecto.Modalidad(s);

        if (modalidad == ModalidadCobro.Gratis)
        {
            s.Estado = EstadoSesion.Confirmada;
            s.ExpiraEn = null;
            return;
        }

        var cobro = await precios.CobroDeSesionAsync(s, esAdmin: false, ct);
        if (cobro.Total <= 0)
            throw ApiException.Conflicto("tarifa_no_definida", "No se pudo calcular el cobro de la sesión: revisa las tarifas.");

        var alquiler = s.Tipo == TipoSesion.Alquiler;
        var horas = alquiler ? alquilerOpt.Value.RetencionHoras : Po.PagoServicioHoras;
        var liberar = alquiler ? alquilerOpt.Value.LiberarAlInicio : Po.LiberarAlInicio;
        var vence = ahora.AddHours(horas);
        if (liberar && s.Inicio < vence) vence = s.Inicio;
        if (vence <= ahora)
            throw ApiException.Conflicto("sesion_vencida", "La sesión ya está por empezar: reprográmala para poder pagarla.");

        s.Estado = EstadoSesion.PendientePago;
        s.ExpiraEn = vence;
        db.Ordenes.Add(new Orden
        {
            Concepto = alquiler ? ConceptoOrden.Alquiler : s.EsExtra ? ConceptoOrden.SesionExtra : ConceptoOrden.Servicio,
            ClienteId = ReglasProyecto.ClienteIdDe(s),
            Sesion = s,
            ProyectoId = s.ProyectoId,
            Monto = cobro.Total,
            Estado = EstadoOrden.Pendiente,
            VenceEn = vence,
            CreadoEn = ahora,
            ActualizadoEn = ahora
        });
    }

    // ───────────────────────── Bandeja del colaborador ─────────────────────────

    public async Task<PaginaDto<InvitacionDto>> ListarAsync(Actor actor, string? estado, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);
        var ahora = time.GetUtcNow();

        var consulta = db.Aceptaciones.AsNoTracking().Where(a => a.Rol != null);
        if (!actor.EsAdmin) consulta = consulta.Where(a => a.UsuarioId == actor.UsuarioId);

        if (!string.IsNullOrWhiteSpace(estado))
        {
            if (!Convenciones.TryParseEnum<EstadoParticipacion>(estado, out var filtro))
                throw ApiException.Validacion("estado", "Estado no válido: pendiente, aceptada, rechazada o expirada.");

            consulta = filtro switch
            {
                // Pendiente de verdad: la sesión sigue esperando y no se venció el plazo.
                EstadoParticipacion.Pendiente => consulta.Where(a => a.Estado == EstadoParticipacion.Pendiente
                    && a.Sesion.Estado == EstadoSesion.PendienteAceptacion && a.Sesion.ExpiraEn > ahora),
                EstadoParticipacion.Expirada => consulta.Where(a => a.Estado == EstadoParticipacion.Expirada
                    || (a.Estado == EstadoParticipacion.Pendiente
                        && (a.Sesion.Estado != EstadoSesion.PendienteAceptacion || a.Sesion.ExpiraEn <= ahora))),
                _ => consulta.Where(a => a.Estado == filtro)
            };
        }

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .OrderByDescending(a => a.EnviadaEn).ThenByDescending(a => a.SesionId)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .Select(a => new
            {
                a.SesionId,
                a.UsuarioId,
                a.Rol,
                a.Estado,
                a.EnviadaEn,
                a.RespondidaEn,
                SesionEstado = a.Sesion.Estado,
                a.Sesion.ExpiraEn,
                a.Sesion.Tipo,
                a.Sesion.Inicio,
                a.Sesion.Fin,
                a.Sesion.EsExtra,
                a.Sesion.Etapa,
                a.Sesion.NumeroSesion,
                Sala = a.Sesion.Sala.Nombre,
                ProyectoId = a.Sesion.ProyectoId,
                Proyecto = a.Sesion.Proyecto != null ? a.Sesion.Proyecto.Titulo : null,
                ProyectoTipo = a.Sesion.Proyecto != null ? (TipoProyecto?)a.Sesion.Proyecto.Tipo : null,
                Servicio = a.Sesion.Proyecto != null ? a.Sesion.Proyecto.Servicio.Nombre : null,
                Incluidas = a.Sesion.Proyecto != null ? (short?)a.Sesion.Proyecto.SesionesIncluidas : null,
                Cliente = a.Sesion.Proyecto != null ? a.Sesion.Proyecto.Cliente.Nombre : a.Sesion.Cliente!.Nombre
            })
            .ToListAsync(ct);

        var usuarios = filas.Select(f => f.UsuarioId).Distinct().ToList();
        var tarifas = await db.Colaboradores.AsNoTracking()
            .Where(c => usuarios.Contains(c.UsuarioId))
            .Select(c => new { c.UsuarioId, c.PrecioHora, c.PrecioSesion })
            .ToListAsync(ct);

        var items = filas.Select(f =>
        {
            var modalidad = f.Tipo == TipoSesion.Alquiler || f.EsExtra ? ModalidadCobro.PorHora
                : f.Etapa == EtapaProyecto.Revision ? ModalidadCobro.Gratis : ModalidadCobro.PorSesion;
            var minutos = (decimal)(f.Fin - f.Inicio).TotalMinutes;
            var t = tarifas.FirstOrDefault(x => x.UsuarioId == f.UsuarioId);
            var miPrecio = PreciosService.PrecioDe(modalidad, minutos, t?.PrecioHora, t?.PrecioSesion);

            var vigente = f.SesionEstado == EstadoSesion.PendienteAceptacion && f.ExpiraEn > ahora;
            var estadoInv = f.Estado == EstadoParticipacion.Pendiente && !vigente ? EstadoParticipacion.Expirada : f.Estado;

            return new InvitacionDto(
                f.SesionId, f.Tipo == TipoSesion.Alquiler ? "alquiler" : "proyecto",
                f.ProyectoId, f.Proyecto, f.ProyectoTipo is null ? null : Convenciones.Api(f.ProyectoTipo.Value), f.Servicio,
                ReglasProyecto.Descripcion(f.Tipo, f.EsExtra, f.Etapa, f.NumeroSesion, f.ProyectoTipo, f.Incluidas),
                f.NumeroSesion,
                f.ProyectoTipo == TipoProyecto.CancionCompleta ? f.Incluidas : null,
                f.Etapa == EtapaProyecto.Revision && !f.EsExtra, f.EsExtra,
                f.Rol is null ? "" : Convenciones.Api(f.Rol.Value),
                f.Sala, f.Inicio, f.Fin, Math.Round(minutos / 60m, 2),
                f.Cliente, Convenciones.Api(estadoInv), f.EnviadaEn, f.ExpiraEn, f.RespondidaEn, miPrecio);
        }).ToList();

        return new PaginaDto<InvitacionDto>(items, total, pagina, tamano);
    }

    // ───────────────────────── Responder (solo colaboradores) ─────────────────────────

    public async Task<SesionInvitadosDto> ResponderAsync(
        long sesionId, bool aceptar, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        if (actor.EsAdmin)
            throw ApiException.Prohibido("Las invitaciones las responde cada colaborador; el administrador no puede hacerlo por él.");

        await LiberarVencidosAsync(ct);
        var s = await CargarAsync(sesionId, ct);
        var yo = await db.ResolverAsync(actor, ct);

        var fila = ReglasProyecto.Invitados(s).FirstOrDefault(a => a.UsuarioId == actor.UsuarioId);
        if (fila is null || yo.ColaboradorId is null) throw ApiException.NoEncontrado("No existe esa invitación.");

        var ahora = time.GetUtcNow();
        if (s.Estado != EstadoSesion.PendienteAceptacion || s.ExpiraEn <= ahora)
            throw ApiException.Conflicto("invitacion_vencida", "Esta invitación ya no está vigente.");
        if (fila.Estado != EstadoParticipacion.Pendiente)
            throw ApiException.Conflicto("ya_respondida", "Ya respondiste esta invitación.");

        fila.Estado = aceptar ? EstadoParticipacion.Aceptada : EstadoParticipacion.Rechazada;
        fila.RespondidaEn = ahora;

        var invitados = ReglasProyecto.Invitados(s).ToList();
        var todosAceptaron = invitados.All(a => a.Estado == EstadoParticipacion.Aceptada);
        if (aceptar && todosAceptaron) await AvanzarAsync(s, ct);

        var persona = (await db.PersonasAsync([actor.UsuarioId], esAdmin: false, ct)).GetValueOrDefault(actor.UsuarioId)?.Nombre ?? "Un colaborador";
        var cliente = ClienteUsuarioId(s);
        var resumen = Textos.Sesion(s, Po.UtcOffsetHoras);

        notificaciones.NotificarUsuarios([cliente], actor.UsuarioId, "invitacion_respondida",
            aceptar ? "Un colaborador aceptó" : "Un colaborador rechazó",
            $"{persona} {(aceptar ? "aceptó" : "rechazó")} la invitación a {resumen}", s.ProyectoId, s.Id);

        if (s.Estado == EstadoSesion.PendientePago)
            notificaciones.NotificarUsuarios([cliente], actor.UsuarioId, "sesion_pendiente_pago", "Tu sesión está lista para pagar",
                $"Todos aceptaron {resumen} Tienes {Po.PagoServicioHoras} h para pagarla.", s.ProyectoId, s.Id);
        else if (s.Estado == EstadoSesion.Confirmada)
            notificaciones.NotificarUsuarios([cliente], actor.UsuarioId, "sesion_confirmada", "Sesión confirmada",
                $"Todos aceptaron {resumen}", s.ProyectoId, s.Id);
        else if (ReglasProyecto.RequiereDecision(s))
        {
            notificaciones.NotificarUsuarios([cliente], actor.UsuarioId, "sesion_requiere_decision", "Tu sesión necesita una decisión",
                $"Alguien rechazó {resumen} Invita a otro colaborador, confirma con quienes aceptaron o cancela.", s.ProyectoId, s.Id);
            await notificaciones.NotificarAdministradoresAsync(actor.UsuarioId, "sesion_requiere_decision",
                "Una sesión necesita una decisión", $"Alguien rechazó {resumen}", s.ProyectoId, s.Id, ct);
        }

        auditoria.Registrar(actor.UsuarioId, aceptar ? "invitacion_aceptada" : "invitacion_rechazada", "sesion",
            s.Id.ToString(), new { s.ProyectoId }, ctx.Ip);
        await GuardarAsync(ct);
        return await ResultadoAsync(s, yo, ct);
    }

    // ───────────────────────── Decisiones del cliente o del administrador ─────────────────────────

    private async Task<(Sesion Sesion, Identidad Yo)> CargarParaDecidirAsync(long sesionId, Actor actor, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var s = await CargarAsync(sesionId, ct);
        var yo = await db.ResolverAsync(actor, ct);

        var dueno = yo.ClienteId is { } c && c == ReglasProyecto.ClienteIdDe(s);
        if (!yo.EsAdmin && !dueno)
        {
            var invitado = ReglasProyecto.Invitados(s).Any(a => a.UsuarioId == actor.UsuarioId);
            throw invitado
                ? ApiException.Prohibido("Solo el cliente o el administrador deciden sobre la sesión.")
                : ApiException.NoEncontrado("No existe esa sesión.");
        }

        if (s.Estado != EstadoSesion.PendienteAceptacion || s.ExpiraEn <= time.GetUtcNow())
            throw ApiException.Conflicto("sesion_no_pendiente", "La sesión ya no está esperando colaboradores.");
        return (s, yo);
    }

    public async Task<SesionInvitadosDto> InvitarAsync(
        long sesionId, Guid colaboradorId, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var (s, yo) = await CargarParaDecidirAsync(sesionId, actor, ct);
        var ahora = time.GetUtcNow();

        var invitable = (await precios.InvitablesAsync([colaboradorId], ReglasProyecto.Modalidad(s), 1, ct))[0];
        if (s.Aceptaciones.Any(a => a.UsuarioId == invitable.UsuarioId))
            throw ApiException.Conflicto("ya_invitado", "Ese colaborador ya fue invitado a esta sesión.");

        var vivos = ReglasProyecto.Invitados(s).Count(a => a.Estado is EstadoParticipacion.Pendiente or EstadoParticipacion.Aceptada);
        if (vivos >= Po.MaxInvitadosPorSesion)
            throw ApiException.Conflicto("limite_invitados", $"Una sesión admite como máximo {Po.MaxInvitadosPorSesion} colaboradores.");

        var nueva = new AceptacionSesion
        {
            SesionId = s.Id,
            UsuarioId = invitable.UsuarioId,
            Rol = ReglasProyecto.RolDe(invitable.Perfil),
            Estado = EstadoParticipacion.Pendiente,
            EnviadaEn = ahora
        };
        s.Aceptaciones.Add(nueva);
        NotificarInvitaciones(s, [nueva], actor.UsuarioId);

        auditoria.Registrar(actor.UsuarioId, "invitacion_enviada", "sesion", s.Id.ToString(),
            new { colaboradorId, s.ProyectoId }, ctx.Ip);
        await GuardarAsync(ct);
        return await ResultadoAsync(s, yo, ct);
    }

    public async Task<SesionInvitadosDto> ConfirmarAsync(long sesionId, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var (s, yo) = await CargarParaDecidirAsync(sesionId, actor, ct);
        var ahora = time.GetUtcNow();

        var invitados = ReglasProyecto.Invitados(s).ToList();
        if (!invitados.Any(a => a.Estado == EstadoParticipacion.Aceptada))
            throw ApiException.Conflicto("sin_aceptados", "Para confirmar necesitas al menos un colaborador que haya aceptado.");

        // Quienes no respondieron quedan fuera de la sesión: su invitación vence.
        var sinResponder = invitados.Where(a => a.Estado == EstadoParticipacion.Pendiente).ToList();
        foreach (var a in sinResponder) a.Estado = EstadoParticipacion.Expirada;

        await AvanzarAsync(s, ct);

        var resumen = Textos.Sesion(s, Po.UtcOffsetHoras);
        notificaciones.NotificarUsuarios(
            invitados.Where(a => a.Estado == EstadoParticipacion.Aceptada).Select(a => a.UsuarioId), actor.UsuarioId,
            "sesion_confirmada_con_aceptados", "La sesión sigue adelante",
            $"El cliente confirmó {resumen} Quedó pendiente de pago.", s.ProyectoId, s.Id);
        notificaciones.NotificarUsuarios(
            sinResponder.Select(a => a.UsuarioId), actor.UsuarioId,
            "invitacion_expirada", "Tu invitación venció",
            $"La sesión siguió sin tu respuesta: {resumen}", s.ProyectoId, s.Id);
        notificaciones.NotificarUsuarios([ClienteUsuarioId(s)], actor.UsuarioId, "sesion_pendiente_pago",
            "Tu sesión está lista para pagar", $"Se confirmó con quienes aceptaron: {resumen}", s.ProyectoId, s.Id);

        auditoria.Registrar(actor.UsuarioId, "sesion_confirmada_con_aceptados", "sesion", s.Id.ToString(),
            new { s.ProyectoId, sinResponder = sinResponder.Count }, ctx.Ip);
        await GuardarAsync(ct);
        return await ResultadoAsync(s, yo, ct);
    }

    // ───────────────────────── Resultado ─────────────────────────

    private async Task<SesionInvitadosDto> ResultadoAsync(Sesion s, Identidad yo, CancellationToken ct)
    {
        var ahora = time.GetUtcNow();
        var dueno = yo.ClienteId is { } c && c == ReglasProyecto.ClienteIdDe(s);
        var vista = new Vista(yo.UsuarioId, yo.EsAdmin, dueno);
        var personas = await db.PersonasAsync(ReglasProyecto.Invitados(s).Select(a => a.UsuarioId), yo.EsAdmin, ct);

        decimal? monto = null;
        if (vista.VePrecios && (await precios.MontosAsync([s], ct)).TryGetValue(s.Id, out var m)) monto = m;

        var vencida = ReglasProyecto.Vencida(s, ahora);
        return new SesionInvitadosDto(
            s.Id, Convenciones.Api(vencida ? EstadoSesion.Expirada : s.Estado), s.ExpiraEn, monto,
            !vencida && ReglasProyecto.PuedeConfirmar(s), !vencida && ReglasProyecto.RequiereDecision(s),
            ReglasProyecto.MapInvitados(s, personas, vista, ahora));
    }
}
