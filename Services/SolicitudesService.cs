using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;

namespace ZOne.Api.Services;

public interface ISolicitudesService
{
    Task<PaginaDto<SolicitudResumenDto>> ListarAsync(Actor actor, string? estado, int pagina, int tamano, CancellationToken ct);
    Task<SolicitudDto> CrearAsync(CrearSolicitudRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<SolicitudDto> ObtenerAsync(long id, Actor actor, CancellationToken ct);
    Task<SolicitudDto> CambiarEstadoAsync(long id, CambiarEstadoSolicitudRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<SolicitudDto> ResponderInvitacionAsync(long id, Guid colaboradorId, ResponderInvitacionRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct);
}

public sealed class SolicitudesService(ZOneDbContext db, IAuditoriaService auditoria, TimeProvider time) : ISolicitudesService
{
    // ───────────────────────── Utilidades ─────────────────────────

    private static bool EsDueno(Solicitud s, Identidad yo) => yo.ClienteId is { } c && c == s.ClienteId;

    private async Task<Solicitud> CargarAsync(long id, CancellationToken ct) =>
        await db.Solicitudes
            .Include(s => s.Cliente)
            .Include(s => s.Sala)
            .Include(s => s.Cancion)
            .Include(s => s.Proyecto)
            .Include(s => s.Colaboradores).ThenInclude(x => x.Colaborador).ThenInclude(c => c.Usuario)
            .FirstOrDefaultAsync(s => s.Id == id, ct)
        ?? throw ApiException.NoEncontrado("No existe esa solicitud.");

    /// <summary>Administrador, cliente dueño y colaboradores invitados; los demás reciben 404.</summary>
    private async Task<Identidad> AccesoAsync(Solicitud s, Actor actor, CancellationToken ct)
    {
        var yo = await db.ResolverAsync(actor, ct);
        if (yo.EsAdmin || EsDueno(s, yo)) return yo;
        if (yo.ColaboradorId is { } colaboradorId
            && s.Estado != EstadosSolicitud.Solicitada
            && s.Colaboradores.Any(x => x.ColaboradorId == colaboradorId))
            return yo;
        throw ApiException.NoEncontrado("No existe esa solicitud.");
    }

    private async Task GuardarAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ErroresDb.Traducir(ex) is not null) { throw ErroresDb.Traducir(ex)!; }
    }

    private static SolicitudDto Map(Solicitud s) =>
        new(
            s.Id, s.ClienteId, s.Cliente.Nombre,
            s.SalaId, s.Sala?.Nombre,
            s.CancionId, s.Cancion?.Titulo,
            s.ProyectoId, s.Proyecto?.Titulo,
            s.Titulo, s.Descripcion, s.Tipo,
            s.FechaDeseada, s.Franja,
            s.Partes ?? [],
            s.Estado, s.CreadoEn, s.ActualizadoEn,
            s.Colaboradores
                .OrderBy(x => x.ColaboradorId)
                .Select(x => new SolicitudColaboradorDto(
                    x.ColaboradorId,
                    x.Colaborador.NombreArtistico ?? x.Colaborador.NombreReal ?? x.Colaborador.Usuario.Nombre,
                    Convenciones.Api(x.Colaborador.Perfil),
                    x.Rol,
                    x.Estado,
                    x.CreadoEn))
                .ToList());

    // ───────────────────────── Validación de valores ─────────────────────────

    private static string NormalizarTipo(string? valor)
    {
        var tipo = (valor ?? "").Trim().ToLowerInvariant();
        if (!TiposSolicitud.Todos.Contains(tipo))
            throw ApiException.Validacion("tipo",
                "El tipo debe ser grabacion, mezcla, masterizacion, ensayo o produccion.");
        return tipo;
    }

    private static string NormalizarEstado(string? valor)
    {
        var estado = (valor ?? "").Trim().ToLowerInvariant();
        if (!EstadosSolicitud.Todos.Contains(estado))
            throw ApiException.Validacion("estado", "Estado de solicitud no válido.");
        return estado;
    }

    private static string NormalizarRespuesta(string? valor)
    {
        var estado = (valor ?? "").Trim().ToLowerInvariant();
        if (estado != EstadosInvitacion.Aceptada && estado != EstadosInvitacion.Rechazada)
            throw ApiException.Validacion("estado", "La respuesta debe ser aceptada o rechazada.");
        return estado;
    }

    private static string? NormalizarRol(string? valor)
    {
        var rol = valor?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(rol)) return null;
        if (!RolesInvitacion.Todos.Contains(rol))
            throw ApiException.Validacion("rol", "El rol debe ser artista, productor o ingeniero.");
        return rol;
    }

    private static string NormalizarFranja(string? valor)
    {
        var texto = (valor ?? "").Trim();
        var partes = texto.Split('-', StringSplitOptions.TrimEntries);
        if (partes.Length != 2
            || !TryHora(partes[0], out var horaInicio, out var minutoInicio)
            || !TryHora(partes[1], out var horaFin, out var minutoFin))
            throw ApiException.Validacion("franja", "La franja debe tener el formato HH:MM-HH:MM.");

        var inicio = horaInicio * 60 + minutoInicio;
        var fin = horaFin * 60 + minutoFin;
        if (fin < inicio) fin += 24 * 60;
        if (fin <= inicio)
            throw ApiException.Validacion("franja", "La hora final debe ser mayor que la inicial.");

        return $"{horaInicio:00}:{minutoInicio:00}-{horaFin:00}:{minutoFin:00}";
    }

    private static bool TryHora(string texto, out int hora, out int minuto)
    {
        hora = 0;
        minuto = 0;
        var partes = texto.Split(':');
        if (partes.Length != 2) return false;
        if (!int.TryParse(partes[0], NumberStyles.None, CultureInfo.InvariantCulture, out hora) || hora is < 0 or > 23)
            return false;
        if (!int.TryParse(partes[1], NumberStyles.None, CultureInfo.InvariantCulture, out minuto) || minuto is < 0 or > 59)
            return false;
        return true;
    }

    private static List<string> NormalizarPartes(IEnumerable<string>? partes)
    {
        var lista = (partes ?? [])
            .Select(p => (p ?? "").Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (lista.Count > 100)
            throw ApiException.Validacion("partes", "No puede tener más de 100 partes.");
        if (lista.Any(p => p.Length > 100))
            throw ApiException.Validacion("partes", "Cada parte puede tener hasta 100 caracteres.");

        return lista;
    }

    private static bool Coincide(PerfilColaborador perfil, string rol) => rol switch
    {
        RolesInvitacion.Artista => perfil == PerfilColaborador.Artista,
        RolesInvitacion.Productor => perfil == PerfilColaborador.Productor,
        RolesInvitacion.Ingeniero => perfil is PerfilColaborador.IngenieroSonido
            or PerfilColaborador.IngenieroMezcla or PerfilColaborador.IngenieroMaster,
        _ => false
    };

    private async Task<List<SolicitudColaborador>> ConstruirInvitacionesAsync(
        IReadOnlyList<InvitacionRequest>? pedidos, DateTimeOffset ahora, CancellationToken ct)
    {
        if (pedidos is null || pedidos.Count is < 1 or > 10)
            throw ApiException.Validacion("colaboradores", "Indica entre 1 y 10 colaboradores.");

        var ids = pedidos.Select(p => p.ColaboradorId).ToList();
        if (ids.Distinct().Count() != ids.Count)
            throw ApiException.Validacion("colaboradores", "No repitas colaboradores.");

        var encontrados = await db.Colaboradores.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Perfil, c.Vinculacion, UsuarioActivo = c.Usuario.Activo })
            .ToListAsync(ct);

        var lista = new List<SolicitudColaborador>();
        foreach (var pedido in pedidos)
        {
            var colaborador = encontrados.FirstOrDefault(x => x.Id == pedido.ColaboradorId)
                ?? throw ApiException.Validacion("colaboradores", "Uno de los colaboradores no existe.");
            if (colaborador.Vinculacion != EstadoVinculacion.Activo || !colaborador.UsuarioActivo)
                throw ApiException.Validacion("colaboradores", "Uno de los colaboradores no está activo.");

            var rol = NormalizarRol(pedido.Rol);
            if (rol is not null && !Coincide(colaborador.Perfil, rol))
                throw ApiException.Validacion("colaboradores",
                    $"Un colaborador con perfil {Convenciones.Api(colaborador.Perfil)} no puede participar como {rol}.");

            lista.Add(new SolicitudColaborador
            {
                ColaboradorId = colaborador.Id,
                Rol = rol,
                Estado = EstadosInvitacion.Pendiente,
                CreadoEn = ahora
            });
        }
        return lista;
    }

    // ───────────────────────── Consultas ─────────────────────────

    public async Task<PaginaDto<SolicitudResumenDto>> ListarAsync(Actor actor, string? estado, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);

        var yo = await db.ResolverAsync(actor, ct);
        var consulta = db.Solicitudes.AsNoTracking().AsQueryable();

        if (!yo.EsAdmin)
        {
            var clienteId = yo.ClienteId;
            var colaboradorId = yo.ColaboradorId;
            if (clienteId is null && colaboradorId is null)
                consulta = consulta.Where(_ => false);
            else
                consulta = consulta.Where(s =>
                    (clienteId != null && s.ClienteId == clienteId) ||
                    (colaboradorId != null && s.Estado != EstadosSolicitud.Solicitada
                        && s.Colaboradores.Any(x => x.ColaboradorId == colaboradorId)));
        }

        if (!string.IsNullOrWhiteSpace(estado))
        {
            var filtro = estado.Trim().ToLowerInvariant();
            if (!EstadosSolicitud.Todos.Contains(filtro))
                throw ApiException.Validacion("estado", "Estado no válido.");
            consulta = consulta.Where(s => s.Estado == filtro);
        }

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .OrderByDescending(s => s.CreadoEn).ThenByDescending(s => s.Id)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .Select(s => new
            {
                s.Id,
                s.ClienteId,
                Cliente = s.Cliente.Nombre,
                s.SalaId,
                Sala = s.Sala == null ? null : s.Sala.Nombre,
                s.Titulo,
                s.Tipo,
                s.FechaDeseada,
                s.Franja,
                s.Estado,
                Invitados = s.Colaboradores.Count,
                Aceptados = s.Colaboradores.Count(x => x.Estado == EstadosInvitacion.Aceptada),
                s.CreadoEn,
                s.ActualizadoEn
            })
            .ToListAsync(ct);

        var items = filas.Select(f => new SolicitudResumenDto(
            f.Id, f.ClienteId, f.Cliente, f.SalaId, f.Sala, f.Titulo, f.Tipo,
            f.FechaDeseada, f.Franja, f.Estado, f.Invitados, f.Aceptados,
            f.CreadoEn, f.ActualizadoEn)).ToList();

        return new PaginaDto<SolicitudResumenDto>(items, total, pagina, tamano);
    }

    public async Task<SolicitudDto> ObtenerAsync(long id, Actor actor, CancellationToken ct)
    {
        var solicitud = await CargarAsync(id, ct);
        await AccesoAsync(solicitud, actor, ct);
        return Map(solicitud);
    }

    // ───────────────────────── Alta ─────────────────────────

    public async Task<SolicitudDto> CrearAsync(CrearSolicitudRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var yo = await db.ResolverAsync(actor, ct);

        Guid clienteId;
        if (yo.EsAdmin)
        {
            if (r.ClienteId is not { } indicado)
                throw ApiException.Validacion("clienteId", "Indica para qué cliente es la solicitud.");
            if (!await db.Clientes.AsNoTracking().AnyAsync(c => c.Id == indicado && c.Activo, ct))
                throw ApiException.NoEncontrado("No existe ese cliente.");
            clienteId = indicado;
        }
        else
        {
            clienteId = yo.ClienteId
                ?? throw ApiException.Prohibido("Solo los clientes o el administrador crean solicitudes.");
        }

        var titulo = Entrada.Texto(r.Titulo, "titulo", 2, 150);
        var descripcion = Entrada.TextoOpcional(r.Descripcion, "descripcion", 2000) ?? string.Empty;
        var tipo = NormalizarTipo(r.Tipo);

        if (r.SalaId is not { } salaId || salaId <= 0)
            throw ApiException.Validacion("salaId", "Indica la sala.");
        if (!await db.Salas.AsNoTracking().AnyAsync(s => s.Id == salaId && s.Activo, ct))
            throw ApiException.NoEncontrado("No existe esa sala.");

        if (r.FechaDeseada is not { } fecha)
            throw ApiException.Validacion("fechaDeseada", "Indica la fecha de la reserva.");

        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZoneInfo.Local).DateTime);
        if (fecha < hoy)
            throw ApiException.Validacion("fechaDeseada", "La fecha no puede ser anterior a hoy.");

        var franja = NormalizarFranja(r.Franja);

        if (r.CancionId is { } cancionId)
        {
            var existe = await db.Canciones.AsNoTracking()
                .AnyAsync(c => c.Id == cancionId && c.Proyecto.ClienteId == clienteId, ct);
            if (!existe)
                throw ApiException.NoEncontrado("No existe esa canción para este cliente.");
        }

        if (r.ProyectoId is { } proyectoId)
        {
            var existe = await db.Proyectos.AsNoTracking()
                .AnyAsync(p => p.Id == proyectoId && p.ClienteId == clienteId, ct);
            if (!existe)
                throw ApiException.NoEncontrado("No existe ese proyecto para este cliente.");
        }

        var ahora = time.GetUtcNow();
        var invitaciones = await ConstruirInvitacionesAsync(r.Colaboradores, ahora, ct);

        var solicitud = new Solicitud
        {
            ClienteId = clienteId,
            SalaId = salaId,
            CancionId = r.CancionId,
            ProyectoId = r.ProyectoId,
            Titulo = titulo,
            Descripcion = descripcion,
            Tipo = tipo,
            FechaDeseada = fecha,
            Franja = franja,
            Partes = NormalizarPartes(r.Partes),
            Estado = EstadosSolicitud.Solicitada,
            CreadoEn = ahora,
            ActualizadoEn = ahora,
            Colaboradores = invitaciones
        };
        db.Solicitudes.Add(solicitud);
        auditoria.Registrar(actor.UsuarioId, "solicitud_creada", "solicitud", null,
            new { clienteId, salaId, tipo, colaboradores = invitaciones.Count }, ctx.Ip);
        await GuardarAsync(ct);

        return await ObtenerAsync(solicitud.Id, actor, ct);
    }

    // ───────────────────────── Cambios de estado ─────────────────────────

    public async Task<SolicitudDto> CambiarEstadoAsync(long id, CambiarEstadoSolicitudRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var solicitud = await CargarAsync(id, ct);
        var yo = await AccesoAsync(solicitud, actor, ct);

        var nuevo = NormalizarEstado(r.Estado);

        if (!EstadosSolicitud.Transiciones.TryGetValue(solicitud.Estado, out var permitidos)
            || !permitidos.Contains(nuevo))
            throw ApiException.Conflicto("transicion_invalida",
                $"No se puede pasar de {solicitud.Estado} a {nuevo}.");

        if (!yo.EsAdmin)
        {
            if (!EsDueno(solicitud, yo))
                throw ApiException.Prohibido("No puedes cambiar el estado de esta solicitud.");
            if (nuevo != EstadosSolicitud.Cancelada)
                throw ApiException.Prohibido("Solo el administrador puede cambiar la solicitud a ese estado.");
        }

        if (nuevo == EstadosSolicitud.PorPagar
            && (solicitud.Colaboradores.Count == 0
                || solicitud.Colaboradores.Any(x => x.Estado != EstadosInvitacion.Aceptada)))
            throw ApiException.Conflicto("invitaciones_pendientes",
                "Todos los colaboradores deben aceptar antes de continuar.");

        var anterior = solicitud.Estado;
        solicitud.Estado = nuevo;
        solicitud.ActualizadoEn = time.GetUtcNow();

        if (nuevo == EstadosSolicitud.Rechazada || nuevo == EstadosSolicitud.Cancelada)
            foreach (var invitacion in solicitud.Colaboradores.Where(x => x.Estado == EstadosInvitacion.Pendiente))
                invitacion.Estado = EstadosInvitacion.Rechazada;

        auditoria.Registrar(actor.UsuarioId, "solicitud_estado", "solicitud", id.ToString(),
            new { anterior, nuevo }, ctx.Ip);
        await GuardarAsync(ct);

        return await ObtenerAsync(id, actor, ct);
    }

    public async Task<SolicitudDto> ResponderInvitacionAsync(long id, Guid colaboradorId, ResponderInvitacionRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var solicitud = await CargarAsync(id, ct);
        var yo = await AccesoAsync(solicitud, actor, ct);

        var nuevo = NormalizarRespuesta(r.Estado);

        if (!yo.EsAdmin && (yo.ColaboradorId is not { } mio || mio != colaboradorId))
            throw ApiException.Prohibido("Solo el colaborador invitado puede responder esta solicitud.");

        var invitacion = solicitud.Colaboradores.FirstOrDefault(x => x.ColaboradorId == colaboradorId)
            ?? throw ApiException.NoEncontrado("No existe esa invitación.");
        if (invitacion.Estado != EstadosInvitacion.Pendiente)
            throw ApiException.Conflicto("ya_respondida", "Esa invitación ya fue respondida.");
        if (solicitud.Estado != EstadosSolicitud.PorAutorizar)
            throw ApiException.Conflicto("solicitud_no_aprobada", "La solicitud aún no está aprobada.");

        invitacion.Estado = nuevo;
        solicitud.ActualizadoEn = time.GetUtcNow();

        // Cascada: al responder la última invitación pendiente.
        if (solicitud.Colaboradores.All(x => x.Estado != EstadosInvitacion.Pendiente))
        {
            solicitud.Estado = solicitud.Colaboradores.All(x => x.Estado == EstadosInvitacion.Aceptada)
                ? EstadosSolicitud.PorPagar
                : EstadosSolicitud.Rechazada;
        }

        auditoria.Registrar(actor.UsuarioId, "invitacion_respondida", "solicitud", id.ToString(),
            new { colaboradorId, estado = nuevo, solicitudEstado = solicitud.Estado }, ctx.Ip);
        await GuardarAsync(ct);

        return await ObtenerAsync(id, actor, ct);
    }
}
