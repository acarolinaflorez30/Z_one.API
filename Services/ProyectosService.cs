using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Options;

namespace ZOne.Api.Services;

public interface IProyectosService
{
    Task<ProyectoDto> CrearAsync(CrearProyectoRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<PaginaDto<ProyectoResumenDto>> ListarAsync(Actor actor, string? estado, int pagina, int tamano, CancellationToken ct);
    Task<ProyectoDto> ObtenerAsync(long id, Actor actor, CancellationToken ct);
    Task<ProyectoDto> AgregarParticipanteAsync(long id, ParticipanteRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<ProyectoDto> ContinuarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<ProyectoDto> CancelarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<ProyectoDto> ListoParaRevisionAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<ProyectoDto> ComentarAsync(long id, string comentario, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<ProyectoDto> SolicitarAjustesAsync(long id, string? comentario, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<ProyectoDto> AprobarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<ProyectoDto> SolicitarSesionExtraAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<PaginaDto<ParticipacionDto>> ListarParticipacionesAsync(Actor actor, string? estado, int pagina, int tamano, CancellationToken ct);
    Task<ProyectoDto> ResponderParticipacionAsync(long participacionId, bool aceptar, Actor actor, ContextoPeticion ctx, CancellationToken ct);
}

public sealed class ProyectosService(
    ZOneDbContext db,
    IAuditoriaService auditoria,
    IOptions<ProyectoOptions> opciones,
    TimeProvider time) : IProyectosService
{
    private readonly ProyectoOptions _o = opciones.Value;

    // ───────────────────────── Utilidades ─────────────────────────

    /// <summary>Aplica los vencimientos (pago a 24 h, solicitudes a 72 h) antes de leer o decidir.</summary>
    private Task LiberarVencidosAsync(CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("select public.expirar_vencidos()", ct);

    private static bool EsDueno(Proyecto p, Identidad yo) => yo.ClienteId is { } c && c == p.ClienteId;

    private static bool EsProductorAceptado(Proyecto p, Identidad yo) =>
        yo.ColaboradorId is { } k && p.Participantes.Any(x =>
            x.ColaboradorId == k && x.Rol == RolParticipante.Productor && x.Estado == EstadoParticipacion.Aceptada);

    private async Task<Proyecto> CargarAsync(long id, CancellationToken ct) =>
        await db.Proyectos
            .Include(p => p.Cliente)
            .Include(p => p.Servicio)
            .Include(p => p.Participantes).ThenInclude(x => x.Colaborador).ThenInclude(c => c.Usuario)
            .FirstOrDefaultAsync(p => p.Id == id, ct)
        ?? throw ApiException.NoEncontrado("No existe ese proyecto.");

    /// <summary>El administrador, el cliente dueño y los colaboradores invitados acceden; los demás reciben 404.</summary>
    private async Task<Identidad> AccesoAsync(Proyecto p, Actor actor, CancellationToken ct)
    {
        var yo = await db.ResolverAsync(actor, ct);
        var participa = yo.ColaboradorId is { } k && p.Participantes.Any(x => x.ColaboradorId == k);
        if (!yo.EsAdmin && !EsDueno(p, yo) && !participa)
            throw ApiException.NoEncontrado("No existe ese proyecto.");
        return yo;
    }

    private async Task GuardarAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ErroresDb.Traducir(ex) is not null) { throw ErroresDb.Traducir(ex)!; }
    }

    // ───────────────────────── Crear ─────────────────────────

    public async Task<ProyectoDto> CrearAsync(CrearProyectoRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var yo = await db.ResolverAsync(actor, ct);
        var ahora = time.GetUtcNow();

        Guid clienteId;
        if (yo.EsAdmin)
        {
            if (r.ClienteId is not { } indicado)
                throw ApiException.Validacion("clienteId", "Indica para qué cliente es el proyecto.");
            if (!await db.Clientes.AsNoTracking().AnyAsync(c => c.Id == indicado && c.Activo, ct))
                throw ApiException.NoEncontrado("No existe ese cliente.");
            clienteId = indicado;
        }
        else
        {
            clienteId = yo.ClienteId ?? throw ApiException.Prohibido("Solo los clientes o el administrador crean proyectos.");
        }

        var titulo = Entrada.Texto(r.Titulo, "titulo", 2, 150);

        var servicio = await db.Servicios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.ServicioId && s.Activo, ct)
                       ?? throw ApiException.NoEncontrado("No existe ese servicio.");

        var tipo = servicio.Categoria switch
        {
            "cancion_completa" => TipoProyecto.CancionCompleta,
            "servicio_parcial" => TipoProyecto.ServicioParcial,
            _ => throw ApiException.Validacion("servicioId", "Elige la canción completa o un servicio parcial.")
        };

        if (servicio.Precio is not { } precio || precio <= 0)
            throw ApiException.Conflicto("precio_no_definido", "Ese servicio aún no tiene precio definido.");
        if (servicio.SesionesIncluidas <= 0)
            throw ApiException.Conflicto("servicio_mal_configurado", "El servicio no tiene sesiones incluidas.");

        var etapaInicial = tipo == TipoProyecto.CancionCompleta
            ? EtapaProyecto.Produccion
            : (servicio.Etapa ?? throw ApiException.Conflicto("servicio_mal_configurado", "El servicio parcial no tiene etapa."));

        if (!yo.EsAdmin)
        {
            var pendientes = await db.Proyectos.CountAsync(p => p.ClienteId == clienteId && p.Estado == EstadoProyecto.Pendiente, ct);
            if (pendientes >= _o.MaxProyectosPendientesPorCliente)
                throw ApiException.Conflicto("demasiados_pendientes",
                    $"Ya tienes {pendientes} proyectos sin confirmar. Espera la respuesta de los colaboradores o cancela alguno.");
        }

        var participantes = await ConstruirParticipantesAsync(r.Participantes ?? new List<ParticipanteRequest>(), ahora, ct);

        var proyecto = new Proyecto
        {
            ClienteId = clienteId,
            ServicioId = servicio.Id,
            Titulo = titulo,
            Tipo = tipo,
            EtapaActual = etapaInicial,
            Estado = EstadoProyecto.Pendiente,
            SesionesIncluidas = servicio.SesionesIncluidas,
            PrecioAcordado = precio,   // la tarifa puede cambiar después; este proyecto conserva el precio que vio el cliente
            RondaRevision = 0,
            CreadoPor = actor.UsuarioId,
            CreadoEn = ahora,
            ActualizadoEn = ahora,
            Participantes = participantes
        };
        db.Proyectos.Add(proyecto);
        auditoria.Registrar(actor.UsuarioId, "proyecto_creado", "proyecto", null,
            new { servicioId = servicio.Id, tipo = tipo.ToString(), clienteId, colaboradores = participantes.Count }, ctx.Ip);
        await GuardarAsync(ct);

        return await ObtenerAsync(proyecto.Id, actor, ct);
    }

    /// <summary>Valida y arma las solicitudes: el colaborador debe estar activo y su perfil debe coincidir con el rol pedido.</summary>
    private async Task<List<ParticipanteProyecto>> ConstruirParticipantesAsync(
        IReadOnlyList<ParticipanteRequest> pedidos, DateTimeOffset ahora, CancellationToken ct)
    {
        if (pedidos.Count < 1 || pedidos.Count > 10)
            throw ApiException.Validacion("participantes", "Indica entre 1 y 10 colaboradores (artistas o productores).");

        var ids = pedidos.Select(p => p.ColaboradorId).ToList();
        if (ids.Distinct().Count() != ids.Count)
            throw ApiException.Validacion("participantes", "No repitas colaboradores.");

        var colaboradores = await db.Colaboradores.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Perfil, c.Vinculacion, UsuarioActivo = c.Usuario.Activo })
            .ToListAsync(ct);

        var lista = new List<ParticipanteProyecto>();
        foreach (var pedido in pedidos)
        {
            var c = colaboradores.FirstOrDefault(x => x.Id == pedido.ColaboradorId)
                    ?? throw ApiException.Validacion("participantes", "Uno de los colaboradores no existe.");
            if (c.Vinculacion != EstadoVinculacion.Activo || !c.UsuarioActivo)
                throw ApiException.Validacion("participantes", "Uno de los colaboradores no está activo.");

            if (!Convenciones.TryParseEnum<RolParticipante>(pedido.Rol, out var rol))
                throw ApiException.Validacion("participantes", "El rol debe ser artista o productor.");

            var coincide = (rol == RolParticipante.Artista && c.Perfil == PerfilColaborador.Artista)
                        || (rol == RolParticipante.Productor && c.Perfil == PerfilColaborador.Productor);
            if (!coincide)
                throw ApiException.Validacion("participantes",
                    $"Un colaborador con perfil {Convenciones.Api(c.Perfil)} no puede participar como {Convenciones.Api(rol)}.");

            lista.Add(new ParticipanteProyecto
            {
                ColaboradorId = c.Id,
                Rol = rol,
                Estado = EstadoParticipacion.Pendiente,
                EnviadaEn = ahora,
                VenceEn = ahora.AddHours(_o.ParticipacionHoras)
            });
        }
        return lista;
    }

    // ───────────────────────── Consultas ─────────────────────────

    public async Task<PaginaDto<ProyectoResumenDto>> ListarAsync(Actor actor, string? estado, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);
        var yo = await db.ResolverAsync(actor, ct);
        var consulta = db.Proyectos.AsNoTracking().AsQueryable();

        if (!yo.EsAdmin)
        {
            var clienteId = yo.ClienteId;
            var colaboradorId = yo.ColaboradorId;
            consulta = consulta.Where(p =>
                (clienteId != null && p.ClienteId == clienteId) ||
                (colaboradorId != null && p.Participantes.Any(x => x.ColaboradorId == colaboradorId)));
        }

        if (!string.IsNullOrWhiteSpace(estado))
        {
            if (!Convenciones.TryParseEnum<EstadoProyecto>(estado, out var estadoFiltro))
                throw ApiException.Validacion("estado", "Estado no válido.");
            consulta = consulta.Where(p => p.Estado == estadoFiltro);
        }

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .OrderByDescending(p => p.CreadoEn).ThenByDescending(p => p.Id)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .Select(p => new
            {
                p.Id,
                p.Titulo,
                p.Tipo,
                Servicio = p.Servicio.Nombre,
                p.Estado,
                p.EtapaActual,
                p.ClienteId,
                Cliente = p.Cliente.Nombre,
                p.SesionesIncluidas,
                p.CreadoEn,
                Cumplidas = db.Sesiones.Count(s => s.ProyectoId == p.Id && !s.EsExtra && s.Estado == EstadoSesion.Cumplida)
            })
            .ToListAsync(ct);

        var items = filas.Select(f => new ProyectoResumenDto(
            f.Id, f.Titulo, Convenciones.Api(f.Tipo), f.Servicio, Convenciones.Api(f.Estado),
            f.EtapaActual is null ? null : Convenciones.Api(f.EtapaActual.Value),
            f.ClienteId, f.Cliente, f.SesionesIncluidas, f.Cumplidas, f.CreadoEn)).ToList();

        return new PaginaDto<ProyectoResumenDto>(items, total, pagina, tamano);
    }

    public async Task<ProyectoDto> ObtenerAsync(long id, Actor actor, CancellationToken ct)
    {
        var p = await db.Proyectos.AsNoTracking()
            .Include(x => x.Cliente)
            .Include(x => x.Servicio)
            .Include(x => x.Participantes).ThenInclude(x => x.Colaborador).ThenInclude(c => c.Usuario)
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw ApiException.NoEncontrado("No existe ese proyecto.");

        var yo = await AccesoAsync(p, actor, ct);
        return await ConstruirAsync(p, yo, ct);
    }

    private async Task<ProyectoDto> ConstruirAsync(Proyecto p, Identidad yo, CancellationToken ct)
    {
        var ahora = time.GetUtcNow();
        var nombres = ReglasProyecto.Nombres(p, yo.EsAdmin);

        var sesiones = await db.Sesiones.AsNoTracking()
            .Include(s => s.Sala)
            .Include(s => s.Aceptaciones)
            .Where(s => s.ProyectoId == p.Id)
            .OrderBy(s => s.Inicio)
            .ToListAsync(ct);

        var ordenes = await db.Ordenes.AsNoTracking()
            .Where(o => o.ProyectoId == p.Id)
            .OrderByDescending(o => o.Id)
            .ToListAsync(ct);

        var comentarios = await (
            from c in db.ComentariosRevision.AsNoTracking()
            where c.ProyectoId == p.Id
            join u in db.Usuarios.AsNoTracking() on c.AutorId equals u.Id
            orderby c.Id
            select new { c.Id, c.Ronda, c.AutorId, u.Nombre, c.Comentario, c.CreadoEn }
        ).ToListAsync(ct);

        var siguiente = ReglasProyecto.Siguiente(p, sesiones);

        var participantes = p.Participantes.OrderBy(x => x.Id).Select(x =>
        {
            var vencida = x.Estado == EstadoParticipacion.Pendiente && x.VenceEn <= ahora;
            return new ParticipanteDto(
                x.Id, x.ColaboradorId,
                nombres.TryGetValue(x.Colaborador.UsuarioId, out var n) ? n : "Colaborador",
                Convenciones.Api(x.Colaborador.Perfil), Convenciones.Api(x.Rol),
                Convenciones.Api(vencida ? EstadoParticipacion.Expirada : x.Estado),
                x.EnviadaEn, x.VenceEn, x.RespondidaEn);
        }).ToList();

        return new ProyectoDto(
            p.Id, p.Titulo, Convenciones.Api(p.Tipo), p.ServicioId, p.Servicio.Nombre, Convenciones.Api(p.Estado),
            p.EtapaActual is null ? null : Convenciones.Api(p.EtapaActual.Value),
            p.ClienteId, p.Cliente.Nombre, p.PrecioAcordado, p.SesionesIncluidas, ReglasProyecto.Cumplidas(sesiones), p.RondaRevision,
            p.AprobadoEn, p.CanceladoEn, p.CreadoEn,
            siguiente is { } sig ? new SiguienteSesionDto((short)sig.Numero, Convenciones.Api(sig.Etapa)) : null,
            ReglasProyecto.Creditos(ordenes, sesiones),
            participantes,
            sesiones.Select(s => ReglasProyecto.MapSesion(s, nombres, ahora)).ToList(),
            ordenes.Select(o => new OrdenProyectoDto(
                o.Id, Convenciones.Api(o.Concepto), o.Monto,
                Convenciones.Api(o.Estado == EstadoOrden.Pendiente && o.VenceEn <= ahora ? EstadoOrden.Expirada : o.Estado),
                o.VenceEn, o.PagadaEn, o.ReferenciaPago)).ToList(),
            comentarios.Select(c => new ComentarioRevisionDto(
                c.Id, c.Ronda, c.AutorId, nombres.TryGetValue(c.AutorId, out var autor) ? autor : c.Nombre,
                c.Comentario, c.CreadoEn)).ToList());
    }

    // ───────────────────────── Participantes y confirmación ─────────────────────────

    /// <summary>Envía la solicitud a otro colaborador (por ejemplo, si uno rechazó o no respondió). Solo antes de confirmar.</summary>
    public async Task<ProyectoDto> AgregarParticipanteAsync(long id, ParticipanteRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        if (!yo.EsAdmin && !EsDueno(p, yo)) throw ApiException.Prohibido("Solo el cliente o el administrador gestionan los participantes.");

        if (p.Estado != EstadoProyecto.Pendiente)
            throw ApiException.Conflicto("proyecto_confirmado", "Solo se pueden cambiar los participantes mientras el proyecto no esté confirmado.");

        var enPago = await db.Ordenes.AnyAsync(o => o.ProyectoId == id && o.Concepto == ConceptoOrden.Servicio
            && (o.Estado == EstadoOrden.Pendiente || o.Estado == EstadoOrden.Pagada), ct);
        if (enPago)
            throw ApiException.Conflicto("ya_en_pago", "El proyecto ya está esperando el pago; no se pueden agregar participantes.");

        var activos = p.Participantes.Count(x => x.Estado == EstadoParticipacion.Pendiente || x.Estado == EstadoParticipacion.Aceptada);
        if (activos >= 10) throw ApiException.Conflicto("limite_participantes", "El proyecto ya tiene 10 participantes.");

        if (p.Participantes.Any(x => x.ColaboradorId == r.ColaboradorId &&
                (x.Estado == EstadoParticipacion.Pendiente || x.Estado == EstadoParticipacion.Aceptada)))
            throw ApiException.Conflicto("participante_repetido", "Ese colaborador ya participa o tiene una solicitud pendiente.");

        var nuevos = await ConstruirParticipantesAsync([r], time.GetUtcNow(), ct);
        foreach (var n in nuevos) p.Participantes.Add(n);

        auditoria.Registrar(actor.UsuarioId, "participante_agregado", "proyecto", id.ToString(),
            new { colaboradorId = r.ColaboradorId, rol = r.Rol }, ctx.Ip);
        await GuardarAsync(ct);
        return await ObtenerAsync(id, actor, ct);
    }

    /// <summary>
    /// Cuando alguien rechazó o no respondió, el cliente decide seguir sin esa persona.
    /// Con todos los demás aceptados se genera el cobro del servicio (24 h para registrar el pago).
    /// </summary>
    public async Task<ProyectoDto> ContinuarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        if (!yo.EsAdmin && !EsDueno(p, yo)) throw ApiException.Prohibido("Solo el cliente o el administrador pueden continuar el proyecto.");

        if (p.Estado != EstadoProyecto.Pendiente)
            throw ApiException.Conflicto("proyecto_confirmado", "El proyecto ya no está pendiente.");

        var ahora = time.GetUtcNow();
        if (p.Participantes.Any(x => x.Estado == EstadoParticipacion.Pendiente && x.VenceEn > ahora))
            throw ApiException.Conflicto("faltan_respuestas", "Aún hay colaboradores que no han respondido.");
        if (!p.Participantes.Any(x => x.Estado == EstadoParticipacion.Aceptada))
            throw ApiException.Conflicto("sin_aceptaciones", "Ningún colaborador ha aceptado todavía.");

        await CrearOrdenServicioAsync(p, ahora, ct);
        auditoria.Registrar(actor.UsuarioId, "proyecto_continuado", "proyecto", id.ToString(), null, ctx.Ip);
        await GuardarAsync(ct);
        return await ObtenerAsync(id, actor, ct);
    }

    private async Task CrearOrdenServicioAsync(Proyecto p, DateTimeOffset ahora, CancellationToken ct)
    {
        var existe = await db.Ordenes.AnyAsync(o => o.ProyectoId == p.Id && o.Concepto == ConceptoOrden.Servicio
            && (o.Estado == EstadoOrden.Pendiente || o.Estado == EstadoOrden.Pagada), ct);
        if (existe) return;

        db.Ordenes.Add(new Orden
        {
            Concepto = ConceptoOrden.Servicio,
            ClienteId = p.ClienteId,
            ProyectoId = p.Id,
            Monto = p.PrecioAcordado,   // el precio acordado al crear el proyecto, no la tarifa de hoy
            Estado = EstadoOrden.Pendiente,
            VenceEn = ahora.AddHours(_o.PagoServicioHoras),
            CreadoEn = ahora,
            ActualizadoEn = ahora
        });
    }

    public async Task<ProyectoDto> CancelarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        if (!yo.EsAdmin && !EsDueno(p, yo)) throw ApiException.Prohibido("Solo el cliente o el administrador cancelan el proyecto.");

        if (p.Estado != EstadoProyecto.Pendiente)
            throw ApiException.Conflicto("proyecto_confirmado", "Un proyecto confirmado ya no se puede cancelar.");

        var ahora = time.GetUtcNow();
        p.Estado = EstadoProyecto.Cancelado;
        p.CanceladoEn = ahora;
        p.ActualizadoEn = ahora;

        foreach (var pendiente in p.Participantes.Where(x => x.Estado == EstadoParticipacion.Pendiente))
            pendiente.Estado = EstadoParticipacion.Expirada;

        var pendientes = await db.Ordenes.Where(o => o.ProyectoId == id && o.Estado == EstadoOrden.Pendiente).ToListAsync(ct);
        foreach (var o in pendientes) o.Estado = EstadoOrden.Expirada;

        auditoria.Registrar(actor.UsuarioId, "proyecto_cancelado", "proyecto", id.ToString(), null, ctx.Ip);
        await GuardarAsync(ct);
        return await ObtenerAsync(id, actor, ct);
    }

    // ───────────────────────── Solicitudes a colaboradores ─────────────────────────

    public async Task<PaginaDto<ParticipacionDto>> ListarParticipacionesAsync(Actor actor, string? estado, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);
        var yo = await db.ResolverAsync(actor, ct);
        var consulta = db.Participantes.AsNoTracking().AsQueryable();

        if (!yo.EsAdmin)
        {
            var colaboradorId = yo.ColaboradorId;
            consulta = consulta.Where(x => colaboradorId != null && x.ColaboradorId == colaboradorId);
        }

        if (!string.IsNullOrWhiteSpace(estado))
        {
            if (!Convenciones.TryParseEnum<EstadoParticipacion>(estado, out var estadoFiltro))
                throw ApiException.Validacion("estado", "Estado no válido.");
            consulta = consulta.Where(x => x.Estado == estadoFiltro);
        }

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .OrderByDescending(x => x.EnviadaEn).ThenByDescending(x => x.Id)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .Select(x => new
            {
                x.Id,
                x.ProyectoId,
                Proyecto = x.Proyecto.Titulo,
                Cliente = x.Proyecto.Cliente.Nombre,
                Servicio = x.Proyecto.Servicio.Nombre,
                x.Rol,
                x.Estado,
                x.EnviadaEn,
                x.VenceEn,
                x.RespondidaEn
            })
            .ToListAsync(ct);

        var ahora = time.GetUtcNow();
        var items = filas.Select(f => new ParticipacionDto(
            f.Id, f.ProyectoId, f.Proyecto, f.Cliente, f.Servicio, Convenciones.Api(f.Rol),
            Convenciones.Api(f.Estado == EstadoParticipacion.Pendiente && f.VenceEn <= ahora ? EstadoParticipacion.Expirada : f.Estado),
            f.EnviadaEn, f.VenceEn, f.RespondidaEn)).ToList();

        return new PaginaDto<ParticipacionDto>(items, total, pagina, tamano);
    }

    public async Task<ProyectoDto> ResponderParticipacionAsync(long participacionId, bool aceptar, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var yo = await db.ResolverAsync(actor, ct);

        var part = await db.Participantes
            .Include(x => x.Proyecto).ThenInclude(p => p.Servicio)
            .Include(x => x.Proyecto).ThenInclude(p => p.Participantes)
            .FirstOrDefaultAsync(x => x.Id == participacionId, ct)
            ?? throw ApiException.NoEncontrado("No existe esa solicitud.");

        if (yo.ColaboradorId is not { } mio || mio != part.ColaboradorId)
            throw ApiException.NoEncontrado("No existe esa solicitud.");

        var ahora = time.GetUtcNow();
        if (part.Estado != EstadoParticipacion.Pendiente)
            throw ApiException.Conflicto("ya_respondida", "Esa solicitud ya no está pendiente.");
        if (part.VenceEn <= ahora)
            throw ApiException.Conflicto("plazo_vencido", "El plazo de 72 horas para responder venció.");
        if (part.Proyecto.Estado != EstadoProyecto.Pendiente)
            throw ApiException.Conflicto("proyecto_no_pendiente", "El proyecto ya no está esperando respuestas.");

        part.Estado = aceptar ? EstadoParticipacion.Aceptada : EstadoParticipacion.Rechazada;
        part.RespondidaEn = ahora;

        // Todos deben aceptar. Si nadie rechazó ni dejó vencer su solicitud, se genera el cobro de inmediato.
        // Si hubo rechazos o vencimientos, el cliente decide: enviar a otro colaborador, continuar sin esa persona o cancelar.
        if (aceptar)
        {
            var todos = part.Proyecto.Participantes;
            var todosAceptaron = todos.Count > 0 && todos.All(x => x.Estado == EstadoParticipacion.Aceptada);
            if (todosAceptaron) await CrearOrdenServicioAsync(part.Proyecto, ahora, ct);
        }

        auditoria.Registrar(actor.UsuarioId, aceptar ? "participacion_aceptada" : "participacion_rechazada",
            "proyecto", part.ProyectoId.ToString(), new { participacionId }, ctx.Ip);
        await GuardarAsync(ct);
        return await ObtenerAsync(part.ProyectoId, actor, ct);
    }

    // ───────────────────────── Revisión ─────────────────────────

    /// <summary>El estudio (productor o administrador) avisa que el trabajo está listo para que el cliente lo revise.</summary>
    public async Task<ProyectoDto> ListoParaRevisionAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        if (!yo.EsAdmin && !EsProductorAceptado(p, yo))
            throw ApiException.Prohibido("Solo el productor del proyecto o el administrador marcan el trabajo como listo para revisión.");

        if (p.Estado != EstadoProyecto.EnCurso)
            throw ApiException.Conflicto("proyecto_no_en_curso", "El proyecto debe estar en curso.");

        var sesiones = await db.Sesiones.AsNoTracking().Where(s => s.ProyectoId == id).ToListAsync(ct);
        if (sesiones.Any(ReglasProyecto.Abierta))
            throw ApiException.Conflicto("sesion_abierta", "Hay una sesión pendiente o confirmada que debe cumplirse o cancelarse primero.");

        var trabajo = sesiones.Count(s => !s.EsExtra && s.Estado == EstadoSesion.Cumplida && s.Etapa != EtapaProyecto.Revision);
        if (trabajo < ReglasProyecto.SesionesAntesDeRevision(p))
            throw ApiException.Conflicto("faltan_sesiones", "Aún faltan sesiones de trabajo por cumplir.");
        if (ReglasProyecto.AjustesPendientes(p, sesiones) > 0)
            throw ApiException.Conflicto("faltan_ajustes", "Falta realizar la sesión de ajustes que pidió el cliente.");

        p.Estado = EstadoProyecto.EnRevision;
        p.ActualizadoEn = time.GetUtcNow();
        auditoria.Registrar(actor.UsuarioId, "listo_para_revision", "proyecto", id.ToString(), null, ctx.Ip);
        await GuardarAsync(ct);
        return await ObtenerAsync(id, actor, ct);
    }

    public async Task<ProyectoDto> ComentarAsync(long id, string comentario, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        if (!EsDueno(p, yo)) throw ApiException.Prohibido("Solo el cliente del proyecto comenta la revisión.");

        if (p.Estado != EstadoProyecto.EnRevision)
            throw ApiException.Conflicto("fuera_de_revision", "Solo se puede comentar mientras el proyecto está en revisión.");

        db.ComentariosRevision.Add(new ComentarioRevision
        {
            ProyectoId = id,
            Ronda = p.RondaRevision,
            AutorId = actor.UsuarioId,
            Comentario = Entrada.Texto(comentario, "comentario", 1, 2000),
            CreadoEn = time.GetUtcNow()
        });
        auditoria.Registrar(actor.UsuarioId, "comentario_revision", "proyecto", id.ToString(), null, ctx.Ip);
        await GuardarAsync(ct);
        return await ObtenerAsync(id, actor, ct);
    }

    /// <summary>
    /// El cliente pide ajustes. Si el paquete aún tiene la sesión de revisión, esa se usa;
    /// si ya se gastó (o es un servicio parcial), se necesita una sesión extra pagada.
    /// </summary>
    public async Task<ProyectoDto> SolicitarAjustesAsync(long id, string? comentario, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        if (!yo.EsAdmin && !EsDueno(p, yo)) throw ApiException.Prohibido("Solo el cliente o el administrador piden ajustes.");

        if (p.Tipo == TipoProyecto.ServicioParcial)
            throw ApiException.Conflicto("sin_ajustes_en_parcial",
                "Los servicios parciales se aprueban directamente: no tienen ronda de ajustes.");

        if (p.Estado != EstadoProyecto.EnRevision)
            throw ApiException.Conflicto("fuera_de_revision", "Solo se pueden pedir ajustes mientras el proyecto está en revisión.");

        var ahora = time.GetUtcNow();
        var texto = Entrada.TextoOpcional(comentario, "comentario", 2000);
        if (texto is not null)
        {
            db.ComentariosRevision.Add(new ComentarioRevision
            {
                ProyectoId = id,
                Ronda = p.RondaRevision,
                AutorId = actor.UsuarioId,
                Comentario = texto,
                CreadoEn = ahora
            });
        }

        p.RondaRevision++;
        p.Estado = EstadoProyecto.EnCurso;
        p.EtapaActual = EtapaProyecto.Revision;
        p.ActualizadoEn = ahora;
        auditoria.Registrar(actor.UsuarioId, "ajustes_solicitados", "proyecto", id.ToString(), new { ronda = p.RondaRevision }, ctx.Ip);
        await GuardarAsync(ct);
        return await ObtenerAsync(id, actor, ct);
    }

    /// <summary>Aprobar cierra el proyecto y bloquea los comentarios.</summary>
    public async Task<ProyectoDto> AprobarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        if (!yo.EsAdmin && !EsDueno(p, yo)) throw ApiException.Prohibido("Solo el cliente o el administrador aprueban el proyecto.");

        if (p.Estado != EstadoProyecto.EnRevision)
            throw ApiException.Conflicto("fuera_de_revision", "Solo se puede aprobar un proyecto que está en revisión.");

        var ahora = time.GetUtcNow();
        p.Estado = EstadoProyecto.Finalizado;
        p.AprobadoEn = ahora;
        p.ActualizadoEn = ahora;
        auditoria.Registrar(actor.UsuarioId, "proyecto_aprobado", "proyecto", id.ToString(), null, ctx.Ip);
        await GuardarAsync(ct);
        return await ObtenerAsync(id, actor, ct);
    }

    // ───────────────────────── Sesión extra (cobro adicional) ─────────────────────────

    /// <summary>El productor, el cliente o el administrador piden una sesión extra: se genera un cobro de 24 h. Pagado, queda un crédito para programarla.</summary>
    public async Task<ProyectoDto> SolicitarSesionExtraAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        if (!yo.EsAdmin && !EsDueno(p, yo) && !EsProductorAceptado(p, yo))
            throw ApiException.Prohibido("Solo el productor, el cliente o el administrador piden una sesión extra.");

        if (p.Tipo == TipoProyecto.ServicioParcial)
            throw ApiException.Conflicto("sin_extra_en_parcial", "Las sesiones extra solo aplican a la canción completa.");

        if (p.Estado != EstadoProyecto.Confirmado && p.Estado != EstadoProyecto.EnCurso)
            throw ApiException.Conflicto("proyecto_no_activo", "Solo se piden sesiones extra mientras el proyecto está en marcha.");

        var pendiente = await db.Ordenes.AnyAsync(o => o.ProyectoId == id && o.Concepto == ConceptoOrden.SesionExtra
            && o.Estado == EstadoOrden.Pendiente, ct);
        if (pendiente)
            throw ApiException.Conflicto("extra_pendiente", "Ya hay un cobro de sesión extra esperando el pago.");

        var servicioExtra = await db.Servicios.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Categoria == "sesion_extra" && s.Activo, ct);
        if (servicioExtra?.Precio is not { } precio || precio <= 0)
            throw ApiException.Conflicto("precio_no_definido", "La sesión extra aún no tiene precio definido.");

        var ahora = time.GetUtcNow();
        db.Ordenes.Add(new Orden
        {
            Concepto = ConceptoOrden.SesionExtra,
            ClienteId = p.ClienteId,
            ProyectoId = id,
            Monto = precio,
            Estado = EstadoOrden.Pendiente,
            VenceEn = ahora.AddHours(_o.PagoServicioHoras),
            CreadoEn = ahora,
            ActualizadoEn = ahora
        });
        auditoria.Registrar(actor.UsuarioId, "sesion_extra_solicitada", "proyecto", id.ToString(), new { monto = precio }, ctx.Ip);
        await GuardarAsync(ct);
        return await ObtenerAsync(id, actor, ct);
    }
}
