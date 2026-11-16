using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Options;

namespace ZOne.Api.Services;

public interface IAlquileresService
{
    Task<AlquilerDto> CrearAsync(CrearAlquilerRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<AlquilerDto> ObtenerAsync(long id, Actor actor, CancellationToken ct);
    Task<PaginaDto<AlquilerDto>> ListarAsync(Actor actor, string? estado, long? salaId, Guid? clienteId,
        DateTimeOffset? desde, DateTimeOffset? hasta, int pagina, int tamano, CancellationToken ct);
    Task<AlquilerDto> CancelarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<AlquilerDto> RegistrarPagoAsync(long id, string? referencia, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<DisponibilidadDto> DisponibilidadAsync(long salaId, DateTimeOffset desde, DateTimeOffset hasta, CancellationToken ct);
}

/// <summary>
/// Alquiler de salas (por hora, de 1 a 4 h). Sin colaboradores: queda pendiente de pago al crearlo.
/// Con colaboradores: primero esperan su respuesta (pendiente_aceptacion), el cliente decide y luego queda pendiente de pago.
/// El monto sigue las tarifas actuales mientras no se pague; al registrar el pago queda fijo.
/// </summary>
public sealed class AlquileresService(
    ZOneDbContext db,
    IAuditoriaService auditoria,
    IOptions<AlquilerOptions> opciones,
    IOptions<ProyectoOptions> proyectoOpt,
    IPreciosService precios,
    IInvitacionesService invitaciones,
    INotificacionesService notificaciones,
    TimeProvider time) : IAlquileresService
{
    private readonly AlquilerOptions _o = opciones.Value;

    private sealed record Fila(
        long Id, long SalaId, string Sala, Guid ClienteId, string Cliente,
        DateTimeOffset Inicio, DateTimeOffset Fin, EstadoSesion Estado, DateTimeOffset? ExpiraEn,
        decimal? MontoOrden, EstadoOrden? EstadoPago, DateTimeOffset? PagadaEn, string? Referencia);

    // ───────────────────────── Utilidades ─────────────────────────

    /// <summary>
    /// Pasa a expiradas las reservas vencidas (y sus órdenes) y libera la sala al instante.
    /// El cron de la base lo hace cada minuto; esto evita esperar ese minuto justo al reservar o pagar.
    /// </summary>
    private Task LiberarVencidosAsync(CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("select public.expirar_vencidos()", ct);

    private async Task<Guid?> ClienteIdDeAsync(Guid usuarioId, CancellationToken ct) =>
        await db.Clientes.AsNoTracking()
            .Where(c => c.UsuarioId == usuarioId && c.Activo)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>LEFT JOIN con el cobro: mientras espera a los colaboradores todavía no existe.</summary>
    private IQueryable<Fila> Consulta() =>
        from s in db.Sesiones.AsNoTracking()
        where s.Tipo == TipoSesion.Alquiler
        join o in db.Ordenes.AsNoTracking() on (long?)s.Id equals o.SesionId into ordenes
        from o in ordenes.DefaultIfEmpty()
        select new Fila(
            s.Id, s.SalaId, s.Sala.Nombre, s.ClienteId ?? Guid.Empty, s.Cliente!.Nombre,
            s.Inicio, s.Fin, s.Estado, s.ExpiraEn,
            (decimal?)o.Monto, (EstadoOrden?)o.Estado, o.PagadaEn, o.ReferenciaPago);

    private async Task<List<AlquilerDto>> ArmarAsync(List<Fila> filas, Actor actor, CancellationToken ct)
    {
        if (filas.Count == 0) return [];
        var ahora = time.GetUtcNow();
        var ids = filas.Select(f => f.Id).ToList();

        var sesiones = await db.Sesiones.AsNoTracking()
            .Include(s => s.Sala).Include(s => s.Aceptaciones)
            .Where(s => ids.Contains(s.Id))
            .ToListAsync(ct);
        var porId = sesiones.ToDictionary(s => s.Id);
        var montos = await precios.MontosAsync(sesiones, ct);
        var personas = await db.PersonasAsync(
            sesiones.SelectMany(ReglasProyecto.Invitados).Select(a => a.UsuarioId), actor.EsAdmin, ct);
        var vista = new Vista(actor.UsuarioId, actor.EsAdmin, EsDueno: true);   // solo el dueño y el administrador llegan aquí

        return filas.Select(f =>
        {
            var s = porId[f.Id];
            var vencida = ReglasProyecto.Vencida(s, ahora);
            var estado = vencida ? EstadoSesion.Expirada : f.Estado;
            var pago = f.EstadoPago is { } ep
                ? (ep == EstadoOrden.Pendiente && (vencida || s.Estado is EstadoSesion.Expirada or EstadoSesion.Cancelada) ? EstadoOrden.Expirada : ep)
                : (EstadoOrden?)null;
            decimal? monto = montos.TryGetValue(f.Id, out var m) ? m : f.MontoOrden;
            var cancelable = s.Estado is EstadoSesion.PendienteAceptacion or EstadoSesion.PendientePago && !vencida;

            return new AlquilerDto(
                f.Id, f.SalaId, f.Sala, f.ClienteId, f.Cliente, f.Inicio, f.Fin,
                Math.Round((decimal)(f.Fin - f.Inicio).TotalHours, 2),
                Convenciones.Api(estado), f.ExpiraEn, monto, pago is null ? null : Convenciones.Api(pago.Value),
                f.PagadaEn, f.Referencia, cancelable,
                !vencida && ReglasProyecto.PuedeConfirmar(s), !vencida && ReglasProyecto.RequiereDecision(s),
                ReglasProyecto.MapInvitados(s, personas, vista, ahora));
        }).ToList();
    }

    private async Task GuardarAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Traducir(ex) is not null) { throw Traducir(ex)!; }
    }

    /// <summary>Traduce los errores de las reglas de la base de datos a respuestas claras.</summary>
    private static ApiException? Traducir(DbUpdateException ex) => ex.InnerException switch
    {
        PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } =>
            ApiException.Conflicto("sala_ocupada", "La sala ya está reservada en ese horario."),
        PostgresException { SqlState: PostgresErrorCodes.CheckViolation } =>
            ApiException.Validacion("reserva", "La reserva no cumple las reglas del alquiler (por ejemplo, máximo 4 horas)."),
        PostgresException { SqlState: PostgresErrorCodes.RaiseException } pg =>
            ApiException.Conflicto("regla_negocio", pg.MessageText),
        _ => null
    };

    // ───────────────────────── Crear la reserva ─────────────────────────

    public async Task<AlquilerDto> CrearAsync(CrearAlquilerRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var ahora = time.GetUtcNow();
        var inicio = r.Inicio.ToUniversalTime();
        var fin = r.Fin.ToUniversalTime();
        ValidarVentana(inicio, fin, ahora);

        // A quién se le reserva: el cliente a sí mismo; el administrador, a quien indique.
        Guid clienteId;
        if (actor.EsAdmin)
        {
            if (r.ClienteId is not { } indicado)
                throw ApiException.Validacion("clienteId", "Indica para qué cliente es la reserva.");
            clienteId = indicado;
        }
        else
        {
            clienteId = await ClienteIdDeAsync(actor.UsuarioId, ct)
                        ?? throw ApiException.Prohibido("Solo los clientes pueden reservar salas.");
        }
        var cliente = await db.Clientes.FirstOrDefaultAsync(c => c.Id == clienteId && c.Activo, ct)
                      ?? throw ApiException.NoEncontrado("No existe ese cliente.");

        var sala = await db.Salas.FirstOrDefaultAsync(s => s.Id == r.SalaId && s.Activo && (s.Publicada || actor.EsAdmin), ct)
                   ?? throw ApiException.NoEncontrado("No existe esa sala o no está disponible.");
        PreciosService.ValidarSala(sala, ModalidadCobro.PorHora);

        await LiberarVencidosAsync(ct);

        if (!actor.EsAdmin)
        {
            // Cuentan las reservas que esperan colaboradores y las que esperan el pago.
            var pendientes = await db.Sesiones.CountAsync(s =>
                s.Tipo == TipoSesion.Alquiler && s.ClienteId == clienteId &&
                (s.Estado == EstadoSesion.PendientePago || s.Estado == EstadoSesion.PendienteAceptacion) && s.ExpiraEn > ahora, ct);
            if (pendientes >= _o.MaxPendientesPorCliente)
                throw ApiException.Conflicto("demasiadas_pendientes",
                    $"Ya tienes {pendientes} reservas sin confirmar o sin pagar. Paga o cancela alguna antes de reservar otra.");
        }

        var invitadas = await invitaciones.CrearInvitacionesAsync(r.ColaboradorIds, ModalidadCobro.PorHora, ahora, ct);
        var po = proyectoOpt.Value;

        var sesion = new Sesion
        {
            Tipo = TipoSesion.Alquiler,
            Sala = sala,
            Cliente = cliente,
            Inicio = inicio,
            Fin = fin,
            CreadaPor = actor.UsuarioId,
            CreadoEn = ahora
        };

        if (invitadas.Count == 0)
        {
            // Solo la sala: queda pendiente de pago. La sala se retiene hasta que venza el plazo de pago
            // (o hasta que empiece la sesión, lo que ocurra primero).
            var minutos = (decimal)(fin - inicio).TotalMinutes;
            var monto = PreciosService.Calcular(ModalidadCobro.PorHora, sala, minutos, [], estricto: true).Total;
            var vence = ahora.AddHours(_o.RetencionHoras);
            if (_o.LiberarAlInicio && inicio < vence) vence = inicio;

            sesion.Estado = EstadoSesion.PendientePago;
            sesion.ExpiraEn = vence;
            db.Sesiones.Add(sesion);
            db.Ordenes.Add(new Orden
            {
                Concepto = ConceptoOrden.Alquiler,
                ClienteId = clienteId,
                Sesion = sesion,
                Monto = monto,
                Estado = EstadoOrden.Pendiente,
                VenceEn = vence,
                CreadoEn = ahora,
                ActualizadoEn = ahora
            });
        }
        else
        {
            // Con colaboradores: esperan su respuesta (72 h, reteniendo la sala) antes de pasar al pago.
            var vence = ahora.AddHours(po.SesionAceptacionHoras);
            if (_o.LiberarAlInicio && inicio < vence) vence = inicio;

            sesion.Estado = EstadoSesion.PendienteAceptacion;
            sesion.ExpiraEn = vence;
            foreach (var inv in invitadas) sesion.Aceptaciones.Add(inv);
            db.Sesiones.Add(sesion);
        }

        auditoria.Registrar(actor.UsuarioId, "alquiler_creado", "sesion", null,
            new { salaId = sala.Id, inicio, fin, clienteId, colaboradores = invitadas.Count }, ctx.Ip);
        await GuardarAsync(ct);

        if (invitadas.Count > 0)
        {
            invitaciones.NotificarInvitaciones(sesion, invitadas, actor.UsuarioId);
            await GuardarAsync(ct);
        }

        return await ObtenerAsync(sesion.Id, actor, ct);
    }

    private void ValidarVentana(DateTimeOffset inicio, DateTimeOffset fin, DateTimeOffset ahora)
    {
        if (fin <= inicio)
            throw ApiException.Validacion("fin", "La hora de fin debe ser posterior a la de inicio.");
        if (inicio < ahora.AddMinutes(_o.MinAnticipacionMinutos))
            throw ApiException.Validacion("inicio", $"Reserva con al menos {_o.MinAnticipacionMinutos} minutos de anticipación, para alcanzar a pagar.");
        if (inicio > ahora.AddDays(_o.MaxDiasAnticipacion))
            throw ApiException.Validacion("inicio", $"No se puede reservar con más de {_o.MaxDiasAnticipacion} días de anticipación.");

        var minutos = (fin - inicio).TotalMinutes;
        if (minutos < _o.MinMinutos)
            throw ApiException.Validacion("fin", $"La duración mínima es de {_o.MinMinutos / 60.0:0.##} hora(s).");
        if (minutos > _o.MaxMinutos)
            throw ApiException.Validacion("fin", $"La duración máxima es de {_o.MaxMinutos / 60.0:0.##} horas.");

        var bloque = _o.GranularidadMinutos;
        var alineado = inicio.UtcDateTime is { Second: 0, Millisecond: 0 } u && u.Minute % bloque == 0;
        if (!alineado || minutos % bloque != 0)
            throw ApiException.Validacion("inicio", $"Las reservas se hacen en bloques de {bloque} minutos (por ejemplo 3:00 o 3:30).");
    }

    // ───────────────────────── Consultas ─────────────────────────

    public async Task<AlquilerDto> ObtenerAsync(long id, Actor actor, CancellationToken ct)
    {
        var consulta = Consulta().Where(f => f.Id == id);
        if (!actor.EsAdmin)
        {
            var mio = await ClienteIdDeAsync(actor.UsuarioId, ct);
            consulta = consulta.Where(f => f.ClienteId == mio);   // otro cliente recibe 404: no se revela que existe
        }

        var fila = await consulta.FirstOrDefaultAsync(ct)
                   ?? throw ApiException.NoEncontrado("No existe esa reserva.");
        return (await ArmarAsync([fila], actor, ct))[0];
    }

    public async Task<PaginaDto<AlquilerDto>> ListarAsync(
        Actor actor, string? estado, long? salaId, Guid? clienteId,
        DateTimeOffset? desde, DateTimeOffset? hasta, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);
        var consulta = Consulta();

        if (actor.EsAdmin)
        {
            if (clienteId is { } c) consulta = consulta.Where(f => f.ClienteId == c);
        }
        else
        {
            var mio = await ClienteIdDeAsync(actor.UsuarioId, ct);
            consulta = consulta.Where(f => f.ClienteId == mio);
        }

        if (!string.IsNullOrWhiteSpace(estado))
        {
            if (!Convenciones.TryParseEnum<EstadoSesion>(estado, out var estadoFiltro))
                throw ApiException.Validacion("estado", "Estado no válido.");
            consulta = consulta.Where(f => f.Estado == estadoFiltro);
        }
        if (salaId is { } s) consulta = consulta.Where(f => f.SalaId == s);
        if (desde is { } d) { var du = d.ToUniversalTime(); consulta = consulta.Where(f => f.Fin > du); }
        if (hasta is { } h) { var hu = h.ToUniversalTime(); consulta = consulta.Where(f => f.Inicio < hu); }

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .OrderByDescending(f => f.Inicio).ThenByDescending(f => f.Id)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .ToListAsync(ct);

        return new PaginaDto<AlquilerDto>(await ArmarAsync(filas, actor, ct), total, pagina, tamano);
    }

    // ───────────────────────── Cancelar (solo antes de pagar) ─────────────────────────

    public async Task<AlquilerDto> CancelarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var sesion = await CargarAsync(id, actor, ct);

        switch (sesion.Estado)
        {
            case EstadoSesion.Confirmada:
                throw ApiException.Conflicto("alquiler_pagado",
                    "Un alquiler pagado no se puede cancelar y no tiene reembolso.");
            case EstadoSesion.PendientePago:
            case EstadoSesion.PendienteAceptacion:
                break;
            default:
                throw ApiException.Conflicto("reserva_no_vigente", "Esa reserva ya no está vigente.");
        }

        sesion.Estado = EstadoSesion.Cancelada;
        foreach (var a in sesion.Aceptaciones.Where(a => a.Estado == EstadoParticipacion.Pendiente))
            a.Estado = EstadoParticipacion.Expirada;
        var cobros = await db.Ordenes.Where(o => o.SesionId == id && o.Estado == EstadoOrden.Pendiente).ToListAsync(ct);
        foreach (var o in cobros) o.Estado = EstadoOrden.Expirada;

        notificaciones.NotificarUsuarios(
            ReglasProyecto.Invitados(sesion).Where(a => a.Estado is EstadoParticipacion.Aceptada or EstadoParticipacion.Expirada).Select(a => a.UsuarioId),
            actor.UsuarioId, "sesion_cancelada", "Alquiler cancelado",
            $"El cliente canceló {Textos.Sesion(sesion, proyectoOpt.Value.UtcOffsetHoras)}", null, sesion.Id);
        auditoria.Registrar(actor.UsuarioId, "alquiler_cancelado", "sesion", sesion.Id.ToString(), null, ctx.Ip);
        await GuardarAsync(ct);

        return await ObtenerAsync(id, actor, ct);
    }

    // ───────────────────────── Registrar el pago (el sistema no cobra) ─────────────────────────

    public async Task<AlquilerDto> RegistrarPagoAsync(long id, string? referencia, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        if (!actor.EsAdmin) throw ApiException.Prohibido("Solo el administrador registra pagos.");

        var referenciaLimpia = Entrada.TextoOpcional(referencia, "referencia", 120);
        await LiberarVencidosAsync(ct);
        var sesion = await CargarAsync(id, actor, ct);

        switch (sesion.Estado)
        {
            case EstadoSesion.PendientePago:
                break;
            case EstadoSesion.Confirmada:
                throw ApiException.Conflicto("ya_pagado", "Esa reserva ya tiene el pago registrado.");
            case EstadoSesion.PendienteAceptacion:
                throw ApiException.Conflicto("esperando_colaboradores", "La reserva todavía espera la respuesta de los colaboradores.");
            default:
                throw ApiException.Conflicto("plazo_vencido",
                    "El plazo para pagar venció o la reserva fue cancelada; la sala ya se liberó. Debe reservarse de nuevo.");
        }

        var orden = await db.Ordenes.FirstOrDefaultAsync(o => o.SesionId == id && o.Estado == EstadoOrden.Pendiente, ct)
                    ?? throw ApiException.NoEncontrado("La reserva no tiene un cobro pendiente.");

        var ahora = time.GetUtcNow();
        if (orden.VenceEn <= ahora)
            throw ApiException.Conflicto("plazo_vencido", "El plazo de pago ya venció.");

        // Los precios no se congelan: al registrar el pago se recalcula con las tarifas de hoy y queda fijo.
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

        var resumen = Textos.Sesion(sesion, proyectoOpt.Value.UtcOffsetHoras);
        notificaciones.NotificarUsuarios(
            ReglasProyecto.Invitados(sesion).Where(a => a.Estado == EstadoParticipacion.Aceptada).Select(a => a.UsuarioId)
                .Append(sesion.Cliente!.UsuarioId),
            actor.UsuarioId, "sesion_confirmada", "Alquiler confirmado", $"Quedó confirmado: {resumen}", null, sesion.Id);
        auditoria.Registrar(actor.UsuarioId, "pago_registrado", "sesion", sesion.Id.ToString(),
            new { orden.Monto, referencia = referenciaLimpia }, ctx.Ip);
        await GuardarAsync(ct);

        return await ObtenerAsync(id, actor, ct);
    }

    /// <summary>Carga el alquiler con todo lo necesario para decidir y calcular. El cliente solo accede a lo suyo (404 si no).</summary>
    private async Task<Sesion> CargarAsync(long id, Actor actor, CancellationToken ct)
    {
        var sesion = await db.Sesiones
            .Include(s => s.Sala).Include(s => s.Aceptaciones).Include(s => s.Cliente)
            .FirstOrDefaultAsync(s => s.Id == id && s.Tipo == TipoSesion.Alquiler, ct)
            ?? throw ApiException.NoEncontrado("No existe esa reserva.");

        if (!actor.EsAdmin)
        {
            var mio = await ClienteIdDeAsync(actor.UsuarioId, ct);
            if (mio is null || sesion.ClienteId != mio) throw ApiException.NoEncontrado("No existe esa reserva.");
        }
        return sesion;
    }

    // ───────────────────────── Disponibilidad pública ─────────────────────────

    /// <summary>Público: solo dice qué horas están "ocupado". No distingue reservas pendientes de confirmadas ni de qué tipo son.</summary>
    public async Task<DisponibilidadDto> DisponibilidadAsync(long salaId, DateTimeOffset desde, DateTimeOffset hasta, CancellationToken ct)
    {
        if (desde == default || hasta == default)
            throw ApiException.Validacion("desde", "Indica 'desde' y 'hasta' en formato ISO 8601, por ejemplo 2026-11-05T00:00:00Z.");

        var d = desde.ToUniversalTime();
        var h = hasta.ToUniversalTime();
        if (h <= d) throw ApiException.Validacion("hasta", "'hasta' debe ser posterior a 'desde'.");
        if (h - d > TimeSpan.FromDays(31)) throw ApiException.Validacion("hasta", "El rango máximo es de 31 días.");

        // Público: solo se ve lo que viene. Lo ya ocurrido (ocupación histórica) no se expone.
        var ahora = time.GetUtcNow();
        var limiteInferior = ahora.AddDays(-1);
        var limiteSuperior = ahora.AddDays(_o.MaxDiasAnticipacion);

        var sala = await db.Salas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == salaId && s.Publicada, ct)
                   ?? throw ApiException.NoEncontrado("No existe esa sala.");

        d = d < limiteInferior ? limiteInferior : d;
        h = h > limiteSuperior ? limiteSuperior : h;
        if (h <= d) return new DisponibilidadDto(sala.Id, sala.Nombre, d, d, []);

        var filas = await db.Sesiones.AsNoTracking()
            .Where(s => s.SalaId == salaId && s.Inicio < h && s.Fin > d)
            .Where(s => s.Estado == EstadoSesion.Confirmada || s.Estado == EstadoSesion.Cumplida
                     || ((s.Estado == EstadoSesion.PendientePago || s.Estado == EstadoSesion.PendienteAceptacion)
                         && s.ExpiraEn > ahora))
            .OrderBy(s => s.Inicio)
            .Select(s => new { s.Inicio, s.Fin })
            .ToListAsync(ct);

        var bloques = filas.Select(f => new BloqueOcupadoDto(f.Inicio, f.Fin, "ocupado")).ToList();
        return new DisponibilidadDto(sala.Id, sala.Nombre, d, h, bloques);
    }
}
