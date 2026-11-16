using System.Text.Json;
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
    Task<PaginaDto<ProyectoResumenDto>> ListarAsync(Actor actor, string? estado, bool? revisionVencida, int pagina, int tamano, CancellationToken ct);
    Task<ProyectoDto> ObtenerAsync(long id, Actor actor, CancellationToken ct);
    Task<ProyectoDto> CancelarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<ProyectoDto> ComentarAsync(long id, string comentario, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<ProyectoDto> SolicitarAjustesAsync(long id, string? comentario, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<ProyectoDto> AprobarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<IReadOnlyList<HistorialEventoDto>> HistorialAsync(long id, Actor actor, CancellationToken ct);
}

public sealed class ProyectosService(
    ZOneDbContext db,
    IAuditoriaService auditoria,
    IOptions<ProyectoOptions> opciones,
    INotificacionesService notificaciones,
    ISesionesProyectoService sesionesSvc,
    SesionVistaBuilder vistas,
    IPreciosService precios,
    TimeProvider time) : IProyectosService
{
    private readonly ProyectoOptions _o = opciones.Value;

    // ───────────────────────── Utilidades ─────────────────────────

    /// <summary>Aplica los vencimientos (pago a 24 h, invitaciones a 72 h) antes de leer o decidir.</summary>
    private Task LiberarVencidosAsync(CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("select public.expirar_vencidos()", ct);

    private static bool EsDueno(Proyecto p, Identidad yo) => yo.ClienteId is { } c && c == p.ClienteId;

    private async Task<Proyecto> CargarAsync(long id, CancellationToken ct) =>
        await db.Proyectos
            .Include(p => p.Cliente)
            .Include(p => p.Servicio)
            .FirstOrDefaultAsync(p => p.Id == id, ct)
        ?? throw ApiException.NoEncontrado("No existe ese proyecto.");

    /// <summary>El administrador, el cliente dueño y los colaboradores invitados a alguna sesión acceden; los demás reciben 404.</summary>
    private async Task<Identidad> AccesoAsync(Proyecto p, Actor actor, CancellationToken ct)
    {
        var yo = await db.ResolverAsync(actor, ct);
        if (!yo.EsAdmin && !EsDueno(p, yo) && !await db.EsInvitadoAsync(p.Id, actor.UsuarioId, ct))
            throw ApiException.NoEncontrado("No existe ese proyecto.");
        return yo;
    }

    private async Task GuardarAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ErroresDb.Traducir(ex) is not null) { throw ErroresDb.Traducir(ex)!; }
    }

    /// <summary>
    /// El cliente tiene 24 horas para aprobar o pedir ajustes. Pasado el plazo solo decide el administrador,
    /// que puede hacerlo en cualquier momento.
    /// </summary>
    private void ValidarPlazoCliente(Proyecto p, Identidad yo)
    {
        if (yo.EsAdmin) return;
        if (p.RevisionVenceEn is { } vence && vence <= time.GetUtcNow())
            throw ApiException.Conflicto("plazo_vencido",
                "El plazo de 24 horas para decidir venció. El administrador definirá el resultado.");
    }

    // ───────────────────────── Crear (servicio + título + primera sesión) ─────────────────────────

    /// <summary>
    /// Crea el proyecto y su primera sesión en una sola petición. El proyecto queda pendiente hasta que se pague esa
    /// primera sesión; si la sesión expira, se cancela o no se paga en 24 h, el proyecto se cancela y hay que crearlo de nuevo.
    /// </summary>
    public async Task<ProyectoDto> CrearAsync(CrearProyectoRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var yo = await db.ResolverAsync(actor, ct);
        var ahora = time.GetUtcNow();

        Guid clienteId;
        if (yo.EsAdmin)
        {
            if (r.ClienteId is not { } indicado)
                throw ApiException.Validacion("clienteId", "Indica para qué cliente es el proyecto.");
            clienteId = indicado;
        }
        else
        {
            clienteId = yo.ClienteId ?? throw ApiException.Prohibido("Solo los clientes o el administrador crean proyectos.");
        }

        var cliente = await db.Clientes.FirstOrDefaultAsync(c => c.Id == clienteId && c.Activo, ct)
                      ?? throw ApiException.NoEncontrado("No existe ese cliente.");

        var titulo = Entrada.Texto(r.Titulo, "titulo", 2, 150);
        if (r.PrimeraSesion is null)
            throw ApiException.Validacion("primeraSesion", "Indica la sala y el inicio de la primera sesión.");

        var servicio = await db.Servicios.FirstOrDefaultAsync(s => s.Id == r.ServicioId && s.Activo, ct)
                       ?? throw ApiException.NoEncontrado("No existe ese servicio.");

        var tipo = servicio.Categoria switch
        {
            "cancion_completa" => TipoProyecto.CancionCompleta,
            "servicio_parcial" => TipoProyecto.ServicioParcial,
            _ => throw ApiException.Validacion("servicioId", "Elige la canción completa o un servicio parcial.")
        };

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
                    $"Ya tienes {pendientes} proyectos sin confirmar. Paga o cancela alguno antes de crear otro.");
        }

        var proyecto = new Proyecto
        {
            Cliente = cliente,
            Servicio = servicio,
            Titulo = titulo,
            Tipo = tipo,
            EtapaActual = etapaInicial,
            Estado = EstadoProyecto.Pendiente,
            SesionesIncluidas = servicio.SesionesIncluidas,
            RondaRevision = 0,
            CreadoPor = actor.UsuarioId,
            CreadoEn = ahora,
            ActualizadoEn = ahora
        };

        var p1 = r.PrimeraSesion;
        var pedido = new ProgramarSesionRequest(p1.SalaId, p1.Inicio, null, false, p1.ColaboradorIds);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // AgregarAsync enlaza la sesión con el proyecto: al guardar se insertan los dos juntos.
        var sesion = await sesionesSvc.AgregarAsync(proyecto, pedido, esPrimera: true, actor, ctx, ct);
        auditoria.Registrar(actor.UsuarioId, "proyecto_creado", "proyecto", null,
            new { servicioId = servicio.Id, tipo = tipo.ToString(), clienteId }, ctx.Ip);
        await GuardarAsync(ct);
        await sesionesSvc.CompletarProgramacionAsync(sesion, actor, ct);
        await GuardarAsync(ct);
        await tx.CommitAsync(ct);

        return await ObtenerAsync(proyecto.Id, actor, ct);
    }

    // ───────────────────────── Consultas ─────────────────────────

    public async Task<PaginaDto<ProyectoResumenDto>> ListarAsync(Actor actor, string? estado, bool? revisionVencida, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);
        var yo = await db.ResolverAsync(actor, ct);
        var consulta = db.Proyectos.AsNoTracking().AsQueryable();

        if (!yo.EsAdmin)
        {
            var clienteId = yo.ClienteId;
            var colaboradorId = yo.ColaboradorId;
            var usuarioId = actor.UsuarioId;
            consulta = consulta.Where(p =>
                (clienteId != null && p.ClienteId == clienteId) ||
                (colaboradorId != null && db.Aceptaciones.Any(a =>
                    a.UsuarioId == usuarioId && a.Rol != null && a.Sesion.ProyectoId == p.Id)));
        }

        if (!string.IsNullOrWhiteSpace(estado))
        {
            if (!Convenciones.TryParseEnum<EstadoProyecto>(estado, out var estadoFiltro))
                throw ApiException.Validacion("estado", "Estado no válido.");
            consulta = consulta.Where(p => p.Estado == estadoFiltro);
        }

        // Revisiones cuyo plazo de 24 h venció sin respuesta del cliente: las decide el administrador.
        if (revisionVencida == true)
        {
            var corte = time.GetUtcNow();
            consulta = consulta.Where(p => p.Estado == EstadoProyecto.EnRevision && p.RevisionVenceEn <= corte);
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
                p.RevisionVenceEn,
                Cumplidas = db.Sesiones.Count(s => s.ProyectoId == p.Id && !s.EsExtra && s.Etapa != EtapaProyecto.Revision && s.Estado == EstadoSesion.Cumplida)
            })
            .ToListAsync(ct);

        var items = filas.Select(f => new ProyectoResumenDto(
            f.Id, f.Titulo, Convenciones.Api(f.Tipo), f.Servicio, Convenciones.Api(f.Estado),
            f.EtapaActual is null ? null : Convenciones.Api(f.EtapaActual.Value),
            f.ClienteId, f.Cliente, f.SesionesIncluidas, f.Cumplidas, f.CreadoEn, f.RevisionVenceEn)).ToList();

        return new PaginaDto<ProyectoResumenDto>(items, total, pagina, tamano);
    }

    public async Task<ProyectoDto> ObtenerAsync(long id, Actor actor, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        // Con seguimiento: así EF enlaza solo sesion.Proyecto (con su Servicio y Cliente) al cargar las sesiones.
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        return await ConstruirAsync(p, yo, ct);
    }

    private async Task<ProyectoDto> ConstruirAsync(Proyecto p, Identidad yo, CancellationToken ct)
    {
        var ahora = time.GetUtcNow();
        var dueno = EsDueno(p, yo);
        var verDinero = yo.EsAdmin || dueno;

        var sesiones = await db.Sesiones
            .Include(s => s.Sala)
            .Include(s => s.Aceptaciones)
            .Where(s => s.ProyectoId == p.Id)
            .OrderBy(s => s.Inicio)
            .ToListAsync(ct);

        var sesionesDto = await vistas.ConstruirAsync(sesiones, yo, dueno, ct);

        var ordenesDto = new List<OrdenProyectoDto>();
        TotalesProyectoDto? totales = null;
        if (verDinero)
        {
            var ordenes = await db.Ordenes.AsNoTracking()
                .Where(o => o.ProyectoId == p.Id)
                .OrderByDescending(o => o.Id)
                .ToListAsync(ct);

            // Mientras un cobro está pendiente sigue las tarifas de hoy (no se congela); al pagarse queda fijo.
            var montos = await precios.MontosAsync(sesiones, ct);
            decimal MontoVisible(Orden o) =>
                o.Estado == EstadoOrden.Pendiente && o.SesionId is { } sid && montos.TryGetValue(sid, out var m) ? m : o.Monto;
            EstadoOrden EstadoVisible(Orden o) =>
                o.Estado == EstadoOrden.Pendiente && o.VenceEn <= ahora ? EstadoOrden.Expirada : o.Estado;

            ordenesDto = ordenes.Select(o => new OrdenProyectoDto(
                o.Id, o.SesionId, Convenciones.Api(o.Concepto), MontoVisible(o),
                Convenciones.Api(EstadoVisible(o)), o.VenceEn, o.PagadaEn, o.ReferenciaPago)).ToList();

            var pagado = ordenes.Where(o => o.Estado == EstadoOrden.Pagada).Sum(o => o.Monto);
            var pendiente = ordenes.Where(o => EstadoVisible(o) == EstadoOrden.Pendiente).Sum(MontoVisible);
            totales = new TotalesProyectoDto(pagado, pendiente, pagado + pendiente);
        }

        var comentarios = await (
            from c in db.ComentariosRevision.AsNoTracking()
            where c.ProyectoId == p.Id
            join u in db.Usuarios.AsNoTracking() on c.AutorId equals u.Id
            orderby c.Id
            select new { c.Id, c.Ronda, c.AutorId, u.Nombre, u.Rol, c.Comentario, c.CreadoEn }
        ).ToListAsync(ct);

        string Autor(Guid autorId, string nombre, RolUsuario rol) =>
            autorId == p.Cliente.UsuarioId ? p.Cliente.Nombre
            : rol == RolUsuario.Administrador && !yo.EsAdmin ? "Administración"
            : nombre;

        var siguiente = ReglasProyecto.Siguiente(p, sesiones);

        return new ProyectoDto(
            p.Id, p.Titulo, Convenciones.Api(p.Tipo), p.ServicioId, p.Servicio.Nombre, Convenciones.Api(p.Estado),
            p.EtapaActual is null ? null : Convenciones.Api(p.EtapaActual.Value),
            p.ClienteId, p.Cliente.Nombre, p.SesionesIncluidas, ReglasProyecto.Cumplidas(sesiones), p.RondaRevision,
            p.AprobadoEn, p.CanceladoEn, p.CreadoEn,
            p.RevisionVenceEn,
            p.Estado == EstadoProyecto.EnRevision && p.RevisionVenceEn is { } venceRevision && venceRevision <= ahora,
            siguiente is { } sig
                ? new SiguienteSesionDto(sig.Numero is { } numeroSig ? (short?)numeroSig : null, Convenciones.Api(sig.Etapa), sig.EsAjuste)
                : null,
            totales,
            sesionesDto,
            ordenesDto,
            comentarios.Select(c => new ComentarioRevisionDto(
                c.Id, c.Ronda, c.AutorId, Autor(c.AutorId, c.Nombre, c.Rol), c.Comentario, c.CreadoEn)).ToList());
    }

    // ───────────────────────── Trazabilidad ─────────────────────────

    /// <summary>
    /// Todo lo que pasó en el proyecto, del más antiguo al más reciente. Lo ven el cliente dueño, los colaboradores
    /// invitados y el administrador. Los colaboradores no ven los eventos de cobro. "sistema" = vencimiento automático.
    /// </summary>
    public async Task<IReadOnlyList<HistorialEventoDto>> HistorialAsync(long id, Actor actor, CancellationToken ct)
    {
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        var verDinero = yo.EsAdmin || EsDueno(p, yo);

        var consulta = db.ProyectoHistorial.AsNoTracking().Where(h => h.ProyectoId == id);
        if (!verDinero) consulta = consulta.Where(h => !h.Evento.StartsWith("cobro_"));
        var filas = await consulta.OrderBy(h => h.Id).ToListAsync(ct);

        var actores = filas.Where(h => h.ActorId != null).Select(h => h.ActorId!.Value).Distinct().ToList();
        var usuarios = await db.Usuarios.AsNoTracking()
            .Where(u => actores.Contains(u.Id))
            .Select(u => new { u.Id, u.Nombre, u.Rol })
            .ToListAsync(ct);
        var personas = await db.PersonasAsync(actores, yo.EsAdmin, ct);

        string Etiqueta(Guid? actorId)
        {
            if (actorId is not { } a) return "sistema";
            if (a == p.Cliente.UsuarioId) return p.Cliente.Nombre;
            var u = usuarios.FirstOrDefault(x => x.Id == a);
            if (u is null) return "Usuario";
            if (u.Rol == RolUsuario.Administrador) return yo.EsAdmin ? u.Nombre : "Administración";
            return personas.TryGetValue(a, out var per) ? per.Nombre : (yo.EsAdmin ? u.Nombre : "Colaborador");
        }

        return filas.Select(h =>
        {
            JsonElement detalle;
            try { using var doc = JsonDocument.Parse(h.Detalle); detalle = doc.RootElement.Clone(); }
            catch (JsonException) { using var doc = JsonDocument.Parse("{}"); detalle = doc.RootElement.Clone(); }
            return new HistorialEventoDto(h.Id, h.SesionId, h.Evento, h.EstadoAnterior, h.EstadoNuevo, detalle, Etiqueta(h.ActorId), h.CreadoEn);
        }).ToList();
    }

    // ───────────────────────── Cancelar ─────────────────────────

    /// <summary>Solo mientras el proyecto está pendiente (primera sesión sin pagar). Cancela también su sesión, invitaciones y cobro.</summary>
    public async Task<ProyectoDto> CancelarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        await LiberarVencidosAsync(ct);
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        if (!yo.EsAdmin && !EsDueno(p, yo)) throw ApiException.Prohibido("Solo el cliente o el administrador cancelan el proyecto.");

        if (p.Estado != EstadoProyecto.Pendiente)
            throw ApiException.Conflicto("proyecto_confirmado", "Un proyecto confirmado ya no se puede cancelar.");

        var ahora = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var abiertas = await db.Sesiones.Include(s => s.Aceptaciones)
            .Where(s => s.ProyectoId == id && (s.Estado == EstadoSesion.PendienteAceptacion || s.Estado == EstadoSesion.PendientePago))
            .ToListAsync(ct);
        foreach (var s in abiertas)
        {
            s.Estado = EstadoSesion.Cancelada;
            foreach (var a in s.Aceptaciones.Where(a => a.Estado == EstadoParticipacion.Pendiente))
                a.Estado = EstadoParticipacion.Expirada;
        }
        var pendientes = await db.Ordenes.Where(o => o.ProyectoId == id && o.Estado == EstadoOrden.Pendiente).ToListAsync(ct);
        foreach (var o in pendientes) o.Estado = EstadoOrden.Expirada;
        await GuardarAsync(ct);

        p.Estado = EstadoProyecto.Cancelado;
        p.CanceladoEn = ahora;
        p.ActualizadoEn = ahora;
        auditoria.Registrar(actor.UsuarioId, "proyecto_cancelado", "proyecto", id.ToString(), null, ctx.Ip);
        await GuardarAsync(ct);
        await tx.CommitAsync(ct);
        return await ObtenerAsync(id, actor, ct);
    }

    // ───────────────────────── Revisión ─────────────────────────

    public async Task<ProyectoDto> ComentarAsync(long id, string comentario, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        if (!EsDueno(p, yo)) throw ApiException.Prohibido("Solo el cliente del proyecto comenta la revisión.");

        if (p.Estado != EstadoProyecto.EnRevision)
            throw ApiException.Conflicto("fuera_de_revision", "Solo se puede comentar mientras el proyecto está en revisión.");
        ValidarPlazoCliente(p, yo);

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
    /// El cliente NO aprueba el trabajo (dentro de sus 24 h) y pide ajustes: se habilita la sesión de ajuste incluida.
    /// Si esa ya se usó, otra ronda requiere una sesión extra (cobrada por hora). Los servicios parciales no tienen ajustes.
    /// El administrador puede hacerlo por el cliente, también después del plazo.
    /// </summary>
    public async Task<ProyectoDto> SolicitarAjustesAsync(long id, string? comentario, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        if (!yo.EsAdmin && !EsDueno(p, yo)) throw ApiException.Prohibido("Solo el cliente o el administrador piden ajustes.");

        if (p.Tipo == TipoProyecto.ServicioParcial)
            throw ApiException.Conflicto("sin_ajustes_en_parcial",
                "Los servicios parciales no tienen ronda de ajustes: terminan al cumplirse su sesión.");

        if (p.Estado != EstadoProyecto.EnRevision)
            throw ApiException.Conflicto("fuera_de_revision", "Solo se pueden pedir ajustes mientras el proyecto está en revisión.");
        ValidarPlazoCliente(p, yo);

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
        p.RevisionVenceEn = null;
        p.EtapaActual = EtapaProyecto.Revision;
        p.ActualizadoEn = ahora;

        auditoria.Registrar(actor.UsuarioId, "ajustes_solicitados", "proyecto", id.ToString(), new { ronda = p.RondaRevision }, ctx.Ip);
        await notificaciones.NotificarProyectoAsync(p, actor.UsuarioId,
            Destinatarios.Colaboradores | Destinatarios.Administradores | (yo.EsAdmin ? Destinatarios.Cliente : Destinatarios.Ninguno),
            "ajustes_solicitados", "Ajustes solicitados",
            $"El trabajo de \"{p.Titulo}\" no fue aprobado: se habilita la sesión de ajuste.", null, ct);
        await GuardarAsync(ct);
        return await ObtenerAsync(id, actor, ct);
    }

    /// <summary>
    /// Aprobar cierra el proyecto y bloquea los comentarios. El cliente lo hace dentro de sus 24 h;
    /// si no responde, lo hace el administrador desde su panel.
    /// </summary>
    public async Task<ProyectoDto> AprobarAsync(long id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var p = await CargarAsync(id, ct);
        var yo = await AccesoAsync(p, actor, ct);
        if (!yo.EsAdmin && !EsDueno(p, yo)) throw ApiException.Prohibido("Solo el cliente o el administrador aprueban el proyecto.");

        if (p.Estado != EstadoProyecto.EnRevision)
            throw ApiException.Conflicto("fuera_de_revision", "Solo se puede aprobar un proyecto que está en revisión.");
        ValidarPlazoCliente(p, yo);

        var ahora = time.GetUtcNow();
        p.Estado = EstadoProyecto.Finalizado;
        p.AprobadoEn = ahora;
        p.RevisionVenceEn = null;
        p.ActualizadoEn = ahora;

        auditoria.Registrar(actor.UsuarioId, "proyecto_aprobado", "proyecto", id.ToString(),
            new { aprobadoPorAdministrador = yo.EsAdmin && !EsDueno(p, yo) }, ctx.Ip);
        await notificaciones.NotificarProyectoAsync(p, actor.UsuarioId,
            Destinatarios.Colaboradores | Destinatarios.Administradores | (yo.EsAdmin ? Destinatarios.Cliente : Destinatarios.Ninguno),
            "proyecto_aprobado", "Trabajo aprobado",
            $"\"{p.Titulo}\" quedó aprobado y finalizado.", null, ct);
        await GuardarAsync(ct);
        return await ObtenerAsync(id, actor, ct);
    }
}
