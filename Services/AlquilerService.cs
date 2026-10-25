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

public sealed class AlquileresService(
    ZOneDbContext db,
    IAuditoriaService auditoria,
    IOptions<AlquilerOptions> opciones,
    TimeProvider time) : IAlquileresService
{
    private readonly AlquilerOptions _o = opciones.Value;

    private sealed record Fila(
        long Id, long SalaId, string Sala, Guid ClienteId, string Cliente,
        DateTimeOffset Inicio, DateTimeOffset Fin, EstadoSesion Estado, DateTimeOffset? ExpiraEn,
        decimal Monto, EstadoOrden EstadoPago, DateTimeOffset? PagadaEn, string? Referencia);

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

    private IQueryable<Fila> Consulta() =>
        from s in db.Sesiones.AsNoTracking()
        where s.Tipo == TipoSesion.Alquiler
        join o in db.Ordenes.AsNoTracking() on (long?)s.Id equals o.SesionId
        select new Fila(
            s.Id, s.SalaId, s.Sala.Nombre, s.ClienteId ?? Guid.Empty, s.Cliente!.Nombre,
            s.Inicio, s.Fin, s.Estado, s.ExpiraEn, o.Monto, o.Estado, o.PagadaEn, o.ReferenciaPago);

    /// <summary>Si la retención ya venció pero el cron aún no corrió, se muestra como expirada.</summary>
    private static AlquilerDto Mapear(Fila f, DateTimeOffset ahora)
    {
        var vencida = f.Estado == EstadoSesion.PendientePago && f.ExpiraEn is { } e && e <= ahora;
        var estado = vencida ? EstadoSesion.Expirada : f.Estado;
        var pago = vencida && f.EstadoPago == EstadoOrden.Pendiente ? EstadoOrden.Expirada : f.EstadoPago;
        var cancelable = f.Estado == EstadoSesion.PendientePago && !vencida;

        return new AlquilerDto(
            f.Id, f.SalaId, f.Sala, f.ClienteId, f.Cliente, f.Inicio, f.Fin,
            Math.Round((decimal)(f.Fin - f.Inicio).TotalHours, 2),
            Convenciones.Api(estado), f.ExpiraEn, f.Monto, Convenciones.Api(pago),
            f.PagadaEn, f.Referencia, cancelable);
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
            var existe = await db.Clientes.AsNoTracking().AnyAsync(c => c.Id == indicado && c.Activo, ct);
            if (!existe) throw ApiException.NoEncontrado("No existe ese cliente.");
            clienteId = indicado;
        }
        else
        {
            clienteId = await ClienteIdDeAsync(actor.UsuarioId, ct)
                        ?? throw ApiException.Prohibido("Solo los clientes pueden reservar salas.");
        }

        var sala = await db.Salas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.SalaId && s.Activo, ct)
                   ?? throw ApiException.NoEncontrado("No existe esa sala o no está disponible.");
        if (sala.PrecioHora <= 0)
            throw ApiException.Conflicto("tarifa_no_definida", "La sala aún no tiene tarifa definida.");

        await LiberarVencidosAsync(ct);

        if (!actor.EsAdmin)
        {
            var pendientes = await db.Sesiones.CountAsync(s =>
                s.Tipo == TipoSesion.Alquiler && s.ClienteId == clienteId &&
                s.Estado == EstadoSesion.PendientePago && s.ExpiraEn > ahora, ct);
            if (pendientes >= _o.MaxPendientesPorCliente)
                throw ApiException.Conflicto("demasiadas_pendientes",
                    $"Ya tienes {pendientes} reservas sin pagar. Paga o cancela alguna antes de reservar otra.");
        }

        var minutos = (decimal)(fin - inicio).TotalMinutes;
        var monto = Math.Round(sala.PrecioHora * minutos / 60m, 2, MidpointRounding.AwayFromZero);

        // La sala se retiene hasta que venza el plazo de pago (o hasta que empiece la sesión, lo que ocurra primero).
        var vence = ahora.AddHours(_o.RetencionHoras);
        if (_o.LiberarAlInicio && inicio < vence) vence = inicio;

        var sesion = new Sesion
        {
            Tipo = TipoSesion.Alquiler,
            SalaId = sala.Id,
            ClienteId = clienteId,
            Inicio = inicio,
            Fin = fin,
            Estado = EstadoSesion.PendientePago,
            ExpiraEn = vence,
            CreadaPor = actor.UsuarioId,
            CreadoEn = ahora
        };
        var orden = new Orden
        {
            Concepto = ConceptoOrden.Alquiler,
            ClienteId = clienteId,
            Sesion = sesion,
            Monto = monto,
            Estado = EstadoOrden.Pendiente,
            VenceEn = vence,
            CreadoEn = ahora,
            ActualizadoEn = ahora
        };
        db.Sesiones.Add(sesion);
        db.Ordenes.Add(orden);

        auditoria.Registrar(actor.UsuarioId, "alquiler_creado", "sesion", null,
            new { salaId = sala.Id, inicio, fin, monto, clienteId }, ctx.Ip);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Traducir(ex) is not null) { throw Traducir(ex)!; }

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
        return Mapear(fila, time.GetUtcNow());
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

        var ahora = time.GetUtcNow();
        return new PaginaDto<AlquilerDto>(filas.Select(f => Mapear(f, ahora)).ToList(), total, pagina, tamano);
    }

    // ───────────────────────── Cancelar (solo antes de pagar) ─────────────────────────

    public async Task<AlquilerDto> CancelarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var (sesion, orden) = await CargarAsync(id, actor, ct);

        switch (sesion.Estado)
        {
            case EstadoSesion.Confirmada:
                throw ApiException.Conflicto("alquiler_pagado",
                    "Un alquiler pagado no se puede cancelar y no tiene reembolso.");
            case EstadoSesion.PendientePago:
                break;
            default:
                throw ApiException.Conflicto("reserva_no_vigente", "Esa reserva ya no está vigente.");
        }

        sesion.Estado = EstadoSesion.Cancelada;
        orden.Estado = EstadoOrden.Expirada;
        auditoria.Registrar(actor.UsuarioId, "alquiler_cancelado", "sesion", sesion.Id.ToString(), null, ctx.Ip);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Traducir(ex) is not null) { throw Traducir(ex)!; }

        return await ObtenerAsync(id, actor, ct);
    }

    // ───────────────────────── Registrar el pago (el sistema no cobra) ─────────────────────────

    public async Task<AlquilerDto> RegistrarPagoAsync(long id, string? referencia, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        if (!actor.EsAdmin) throw ApiException.Prohibido("Solo el administrador registra pagos.");

        var referenciaLimpia = Entrada.TextoOpcional(referencia, "referencia", 120);
        await LiberarVencidosAsync(ct);
        var (sesion, orden) = await CargarAsync(id, actor, ct);

        switch (sesion.Estado)
        {
            case EstadoSesion.PendientePago:
                break;
            case EstadoSesion.Confirmada:
                throw ApiException.Conflicto("ya_pagado", "Esa reserva ya tiene el pago registrado.");
            default:
                throw ApiException.Conflicto("plazo_vencido",
                    "El plazo para pagar venció o la reserva fue cancelada; la sala ya se liberó. Debe reservarse de nuevo.");
        }

        var ahora = time.GetUtcNow();
        if (orden.VenceEn <= ahora)
            throw ApiException.Conflicto("plazo_vencido", "El plazo de pago ya venció.");

        orden.Estado = EstadoOrden.Pagada;
        orden.PagadaEn = ahora;
        orden.RegistradaPor = actor.UsuarioId;
        orden.ReferenciaPago = referenciaLimpia;
        sesion.Estado = EstadoSesion.Confirmada;
        sesion.ExpiraEn = null;

        auditoria.Registrar(actor.UsuarioId, "pago_registrado", "sesion", sesion.Id.ToString(),
            new { orden.Monto, referencia = referenciaLimpia }, ctx.Ip);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Traducir(ex) is not null) { throw Traducir(ex)!; }

        return await ObtenerAsync(id, actor, ct);
    }

    private async Task<(Sesion Sesion, Orden Orden)> CargarAsync(long id, Actor actor, CancellationToken ct)
    {
        var sesion = await db.Sesiones.FirstOrDefaultAsync(s => s.Id == id && s.Tipo == TipoSesion.Alquiler, ct)
                     ?? throw ApiException.NoEncontrado("No existe esa reserva.");

        if (!actor.EsAdmin)
        {
            var mio = await ClienteIdDeAsync(actor.UsuarioId, ct);
            if (mio is null || sesion.ClienteId != mio) throw ApiException.NoEncontrado("No existe esa reserva.");
        }

        var orden = await db.Ordenes.FirstOrDefaultAsync(o => o.SesionId == id, ct)
                    ?? throw ApiException.NoEncontrado("La reserva no tiene orden de cobro.");
        return (sesion, orden);
    }

    // ───────────────────────── Disponibilidad pública ─────────────────────────

    public async Task<DisponibilidadDto> DisponibilidadAsync(long salaId, DateTimeOffset desde, DateTimeOffset hasta, CancellationToken ct)
    {
        if (desde == default || hasta == default)
            throw ApiException.Validacion("desde", "Indica 'desde' y 'hasta' en formato ISO 8601, por ejemplo 2026-11-05T00:00:00Z.");

        var d = desde.ToUniversalTime();
        var h = hasta.ToUniversalTime();
        if (h <= d) throw ApiException.Validacion("hasta", "'hasta' debe ser posterior a 'desde'.");
        if (h - d > TimeSpan.FromDays(31)) throw ApiException.Validacion("hasta", "El rango máximo es de 31 días.");

        var sala = await db.Salas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == salaId && s.Activo, ct)
                   ?? throw ApiException.NoEncontrado("No existe esa sala.");

        var ahora = time.GetUtcNow();
        var filas = await db.Sesiones.AsNoTracking()
            .Where(s => s.SalaId == salaId && s.Inicio < h && s.Fin > d)
            .Where(s => s.Estado == EstadoSesion.Confirmada || s.Estado == EstadoSesion.Cumplida
                     || ((s.Estado == EstadoSesion.PendientePago || s.Estado == EstadoSesion.PendienteAceptacion)
                         && s.ExpiraEn > ahora))
            .OrderBy(s => s.Inicio)
            .Select(s => new { s.Inicio, s.Fin, s.Estado })
            .ToListAsync(ct);

        // Solo se publica si el bloque está retenido (a la espera de pago o respuesta) o ya confirmado.
        var bloques = filas.Select(f => new BloqueOcupadoDto(
            f.Inicio, f.Fin,
            (f.Estado is EstadoSesion.PendientePago or EstadoSesion.PendienteAceptacion) ? "retenida" : "confirmada")).ToList();

        return new DisponibilidadDto(sala.Id, sala.Nombre, d, h, bloques);
    }
}
