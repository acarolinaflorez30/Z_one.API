using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Security;

namespace ZOne.Api.Services;

public sealed record FotoArchivo(byte[] Datos, string Mime, string Version);

public interface IColaboradoresService
{
    Task<PaginaDto<ColaboradorResumenDto>> ListarAsync(string? perfil, string? vinculacion, string? q, int pagina, int tamano, CancellationToken ct);
    Task<Guid> IdPropioAsync(Guid usuarioId, CancellationToken ct);
    Task<ColaboradorDto> ObtenerAsync(Guid id, Actor actor, CancellationToken ct);
    Task<ColaboradorDto> ActualizarAsync(Guid id, ActualizarColaboradorRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task CambiarVinculacionAsync(Guid id, bool activo, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string?>> ActualizarDetalleAsync(Guid id, Dictionary<string, string?> campos, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<PortafolioDto> AgregarPortafolioAsync(Guid id, PortafolioRequest r, Actor actor, CancellationToken ct);
    Task<PortafolioDto> EditarPortafolioAsync(Guid id, long itemId, PortafolioRequest r, Actor actor, CancellationToken ct);
    Task EliminarPortafolioAsync(Guid id, long itemId, Actor actor, CancellationToken ct);
    Task<IReadOnlyList<NotaDto>> ListarNotasAsync(Guid id, Actor actor, CancellationToken ct);
    Task<NotaDto> AgregarNotaAsync(Guid id, string comentario, Actor actor, CancellationToken ct);
    Task<string> SubirFotoAsync(Guid id, byte[] datos, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task EliminarFotoAsync(Guid id, Actor actor, ContextoPeticion ctx, CancellationToken ct);
    Task<FotoArchivo> ObtenerFotoAsync(Guid id, bool soloPublico, Actor? actor, CancellationToken ct);
}

public sealed class ColaboradoresService(
    ZOneDbContext db,
    IFotoStorage storage,
    IAuditoriaService auditoria,
    ILogger<ColaboradoresService> logger,
    TimeProvider time) : IColaboradoresService
{
    // ───────────────────────── Acceso ─────────────────────────

    /// <summary>El administrador accede a todo; el colaborador solo a lo suyo. Los demás reciben 404 (no se revela que existe).</summary>
    private async Task<Colaborador> CargarAsync(Guid id, Actor actor, bool soloAdmin, CancellationToken ct)
    {
        var c = await db.Colaboradores.Include(x => x.Usuario).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw ApiException.NoEncontrado("No existe ese colaborador.");

        if (!actor.EsAdmin)
        {
            if (c.UsuarioId != actor.UsuarioId) throw ApiException.NoEncontrado("No existe ese colaborador.");
            if (soloAdmin) throw ApiException.Prohibido("Esta acción solo la realiza el administrador.");
        }
        return c;
    }

    // ───────────────────────── Consulta ─────────────────────────

    public async Task<PaginaDto<ColaboradorResumenDto>> ListarAsync(
        string? perfil, string? vinculacion, string? q, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);
        var consulta = db.Colaboradores.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(perfil))
        {
            if (!Convenciones.TryParseEnum<PerfilColaborador>(perfil, out var perfilFiltro))
                throw ApiException.Validacion("perfil", "Perfil no válido.");
            consulta = consulta.Where(c => c.Perfil == perfilFiltro);
        }

        if (!string.IsNullOrWhiteSpace(vinculacion))
        {
            if (!Convenciones.TryParseEnum<EstadoVinculacion>(vinculacion, out var vinculacionFiltro))
                throw ApiException.Validacion("vinculacion", "La vinculación debe ser activo o desvinculado.");
            consulta = consulta.Where(c => c.Vinculacion == vinculacionFiltro);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var patron = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            consulta = consulta.Where(c =>
                (c.NombreArtistico != null && EF.Functions.ILike(c.NombreArtistico, patron)) ||
                (c.NombreReal != null && EF.Functions.ILike(c.NombreReal, patron)) ||
                EF.Functions.ILike(c.Usuario.Email, patron));
        }

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .OrderBy(c => c.NombreArtistico ?? c.Usuario.Nombre).ThenBy(c => c.Id)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .Select(c => new
            {
                c.Id,
                c.NombreArtistico,
                c.NombreReal,
                c.Usuario.Email,
                c.Perfil,
                c.Vinculacion,
                c.Disponibilidad,
                c.VisiblePublico,
                c.Foto,
                c.FechaIngreso
            })
            .ToListAsync(ct);

        var items = filas.Select(f => new ColaboradorResumenDto(
            f.Id, f.NombreArtistico, f.NombreReal, f.Email,
            Convenciones.Api(f.Perfil), Convenciones.Api(f.Vinculacion), Convenciones.Api(f.Disponibilidad),
            f.VisiblePublico, Convenciones.FotoUrl(f.Id, f.Foto, publica: false), f.FechaIngreso)).ToList();

        return new PaginaDto<ColaboradorResumenDto>(items, total, pagina, tamano);
    }

    public async Task<Guid> IdPropioAsync(Guid usuarioId, CancellationToken ct)
    {
        var id = await db.Colaboradores.AsNoTracking()
            .Where(c => c.UsuarioId == usuarioId)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
        return id ?? throw ApiException.NoEncontrado("Tu cuenta no tiene perfil de colaborador.");
    }

    public async Task<ColaboradorDto> ObtenerAsync(Guid id, Actor actor, CancellationToken ct) =>
        await ConstruirAsync(await CargarAsync(id, actor, false, ct), ct);

    private async Task<ColaboradorDto> ConstruirAsync(Colaborador c, CancellationToken ct)
    {
        var detalle = await DetallePerfil.LeerAsync(db, c.Id, c.Perfil, ct);
        var portafolio = await db.ColaboradorProyectos.AsNoTracking()
            .Where(p => p.ColaboradorId == c.Id)
            .OrderByDescending(p => p.Anio).ThenByDescending(p => p.Id)
            .ToListAsync(ct);

        return new ColaboradorDto(
            c.Id, c.UsuarioId, c.Usuario.Email,
            Convenciones.Api(c.Perfil), Convenciones.Api(c.Vinculacion), Convenciones.Api(c.Disponibilidad),
            c.VisiblePublico, c.NombreArtistico, c.NombreReal, c.FechaNacimiento, c.Biografia, c.Trayectoria,
            c.Productoras, c.Especialidad, c.CiudadOrigen, Convenciones.FotoUrl(c.Id, c.Foto, publica: false),
            c.FechaIngreso, c.DesvinculadoEn, detalle, portafolio.Select(Mapeos.Portafolio).ToList());
    }

    // ───────────────────────── Edición del perfil ─────────────────────────

    private static string? Parcial(string? nuevo, string? actual, string campo, int max)
    {
        if (nuevo is null) return actual;
        var t = nuevo.Trim();
        if (t.Length == 0) return null;
        if (t.Length > max) throw ApiException.Validacion(campo, $"No puede superar {max} caracteres.");
        return t;
    }

    public async Task<ColaboradorDto> ActualizarAsync(
        Guid id, ActualizarColaboradorRequest r, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var c = await CargarAsync(id, actor, false, ct);

        // Estos datos los define solo el administrador.
        var soloAdmin = r.NombreReal is not null || r.FechaNacimiento is not null || r.VisiblePublico is not null;
        if (soloAdmin && !actor.EsAdmin)
            throw ApiException.Prohibido("El nombre real, la fecha de nacimiento y la visibilidad pública los define el administrador.");

        var cambios = new List<string>();
        void Marca(string campo, bool cambio) { if (cambio) cambios.Add(campo); }

        var nombreArtistico = Parcial(r.NombreArtistico, c.NombreArtistico, "nombreArtistico", 120);
        Marca("nombreArtistico", nombreArtistico != c.NombreArtistico); c.NombreArtistico = nombreArtistico;

        var biografia = Parcial(r.Biografia, c.Biografia, "biografia", 4000);
        Marca("biografia", biografia != c.Biografia); c.Biografia = biografia;

        var trayectoria = Parcial(r.Trayectoria, c.Trayectoria, "trayectoria", 4000);
        Marca("trayectoria", trayectoria != c.Trayectoria); c.Trayectoria = trayectoria;

        var productoras = Parcial(r.Productoras, c.Productoras, "productoras", 1000);
        Marca("productoras", productoras != c.Productoras); c.Productoras = productoras;

        var especialidad = Parcial(r.Especialidad, c.Especialidad, "especialidad", 120);
        Marca("especialidad", especialidad != c.Especialidad); c.Especialidad = especialidad;

        var ciudad = Parcial(r.CiudadOrigen, c.CiudadOrigen, "ciudadOrigen", 120);
        Marca("ciudadOrigen", ciudad != c.CiudadOrigen); c.CiudadOrigen = ciudad;

        if (r.NombreReal is not null)
        {
            var nombreReal = Parcial(r.NombreReal, c.NombreReal, "nombreReal", 120);
            Marca("nombreReal", nombreReal != c.NombreReal); c.NombreReal = nombreReal;
        }

        if (r.FechaNacimiento is { } nacimiento)
        {
            if (nacimiento > DateOnly.FromDateTime(DateTime.UtcNow) || nacimiento.Year < 1900)
                throw ApiException.Validacion("fechaNacimiento", "La fecha de nacimiento no es válida.");
            Marca("fechaNacimiento", nacimiento != c.FechaNacimiento); c.FechaNacimiento = nacimiento;
        }

        if (r.Disponibilidad is not null)
        {
            if (!Convenciones.TryParseEnum<Disponibilidad>(r.Disponibilidad, out var disponibilidad))
                throw ApiException.Validacion("disponibilidad", "Debe ser disponible o no_disponible.");
            Marca("disponibilidad", disponibilidad != c.Disponibilidad); c.Disponibilidad = disponibilidad;
        }

        if (r.VisiblePublico is { } visible)
        {
            Marca("visiblePublico", visible != c.VisiblePublico); c.VisiblePublico = visible;
        }

        // Regla del plan: lo público exige colaborador activo y nunca expone el nombre real.
        if (c.VisiblePublico)
        {
            if (c.Vinculacion != EstadoVinculacion.Activo || !c.Usuario.Activo)
                throw ApiException.Validacion("visiblePublico", "Solo un colaborador activo puede mostrarse al público.");
            if (string.IsNullOrWhiteSpace(c.NombreArtistico))
                throw ApiException.Validacion("nombreArtistico", "Para ser público necesita un nombre artístico: el nombre real nunca se muestra.");
        }

        if (cambios.Count > 0)
        {
            auditoria.Registrar(actor.UsuarioId, "colaborador_actualizado", "colaborador", c.Id.ToString(),
                new { campos = cambios }, ctx.Ip);
            await db.SaveChangesAsync(ct);
        }

        return await ConstruirAsync(c, ct);
    }

    public async Task CambiarVinculacionAsync(Guid id, bool activo, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var c = await CargarAsync(id, actor, soloAdmin: true, ct);

        if (activo)
        {
            c.Vinculacion = EstadoVinculacion.Activo;
            c.DesvinculadoEn = null;
        }
        else
        {
            c.Vinculacion = EstadoVinculacion.Desvinculado;
            c.DesvinculadoEn = time.GetUtcNow();
            c.VisiblePublico = false;                       // un desvinculado nunca aparece en público
            c.Disponibilidad = Disponibilidad.NoDisponible;
        }

        auditoria.Registrar(actor.UsuarioId, activo ? "colaborador_vinculado" : "colaborador_desvinculado",
            "colaborador", c.Id.ToString(), null, ctx.Ip);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, string?>> ActualizarDetalleAsync(
        Guid id, Dictionary<string, string?> campos, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var c = await CargarAsync(id, actor, false, ct);
        var (_, permitidos) = DetallePerfil.Mapa[c.Perfil];
        var entrada = campos ?? new Dictionary<string, string?>();

        var actual = await DetallePerfil.LeerAsync(db, c.Id, c.Perfil, ct);
        foreach (var (clave, valor) in entrada)
        {
            if (!permitidos.Contains(clave))
                throw ApiException.Validacion("campos", $"El campo '{clave}' no existe para el perfil {Convenciones.Api(c.Perfil)}.");

            var t = valor?.Trim();
            if (t is { Length: > 2000 }) throw ApiException.Validacion(clave, "No puede superar 2000 caracteres.");
            actual[clave] = string.IsNullOrEmpty(t) ? null : t;
        }

        await DetallePerfil.GuardarAsync(db, c.Id, c.Perfil, actual, ct);
        auditoria.Registrar(actor.UsuarioId, "colaborador_detalle_actualizado", "colaborador", c.Id.ToString(),
            new { campos = entrada.Keys.ToArray() }, ctx.Ip);
        await db.SaveChangesAsync(ct);
        return actual;
    }

    // ───────────────────────── Portafolio ─────────────────────────

    private void AplicarPortafolio(ColaboradorProyecto p, PortafolioRequest r)
    {
        p.Titulo = Entrada.Texto(r.Titulo, "titulo", 1, 150);
        p.Descripcion = Entrada.TextoOpcional(r.Descripcion, "descripcion", 2000);
        p.Productora = Entrada.TextoOpcional(r.Productora, "productora", 120);
        p.Rol = Entrada.TextoOpcional(r.Rol, "rol", 120);

        if (r.Anio is { } anio)
        {
            if (anio < 1950 || anio > time.GetUtcNow().Year + 1)
                throw ApiException.Validacion("anio", "El año no es válido.");
            p.Anio = anio;
        }
        else p.Anio = null;

        if (r.Estado is null) p.Estado = EstadoPortafolio.Finalizado;
        else if (Convenciones.TryParseEnum<EstadoPortafolio>(r.Estado, out var estado)) p.Estado = estado;
        else throw ApiException.Validacion("estado", "El estado debe ser finalizado o activo.");
    }

    public async Task<PortafolioDto> AgregarPortafolioAsync(Guid id, PortafolioRequest r, Actor actor, CancellationToken ct)
    {
        var c = await CargarAsync(id, actor, false, ct);
        var item = new ColaboradorProyecto { ColaboradorId = c.Id, CreadoEn = time.GetUtcNow() };
        AplicarPortafolio(item, r);
        db.ColaboradorProyectos.Add(item);
        await db.SaveChangesAsync(ct);
        return Mapeos.Portafolio(item);
    }

    public async Task<PortafolioDto> EditarPortafolioAsync(Guid id, long itemId, PortafolioRequest r, Actor actor, CancellationToken ct)
    {
        var c = await CargarAsync(id, actor, false, ct);
        var item = await db.ColaboradorProyectos.FirstOrDefaultAsync(p => p.Id == itemId && p.ColaboradorId == c.Id, ct)
                   ?? throw ApiException.NoEncontrado("No existe ese trabajo del portafolio.");
        AplicarPortafolio(item, r);
        await db.SaveChangesAsync(ct);
        return Mapeos.Portafolio(item);
    }

    public async Task EliminarPortafolioAsync(Guid id, long itemId, Actor actor, CancellationToken ct)
    {
        var c = await CargarAsync(id, actor, false, ct);
        var item = await db.ColaboradorProyectos.FirstOrDefaultAsync(p => p.Id == itemId && p.ColaboradorId == c.Id, ct)
                   ?? throw ApiException.NoEncontrado("No existe ese trabajo del portafolio.");
        db.ColaboradorProyectos.Remove(item);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────── Notas internas (solo administrador) ─────────────────────────

    public async Task<IReadOnlyList<NotaDto>> ListarNotasAsync(Guid id, Actor actor, CancellationToken ct)
    {
        var c = await CargarAsync(id, actor, soloAdmin: true, ct);

        var filas = await (
            from n in db.ColaboradorNotas.AsNoTracking()
            where n.ColaboradorId == c.Id
            join u in db.Usuarios.AsNoTracking() on n.AutorId equals u.Id into autores
            from autor in autores.DefaultIfEmpty()
            orderby n.CreadoEn descending
            select new { n.Id, n.Comentario, n.AutorId, AutorNombre = autor.Nombre, n.CreadoEn }
        ).ToListAsync(ct);

        return filas.Select(f => new NotaDto(f.Id, f.Comentario, f.AutorId, f.AutorNombre, f.CreadoEn)).ToList();
    }

    public async Task<NotaDto> AgregarNotaAsync(Guid id, string comentario, Actor actor, CancellationToken ct)
    {
        var c = await CargarAsync(id, actor, soloAdmin: true, ct);
        var texto = Entrada.Texto(comentario, "comentario", 1, 2000);

        var nota = new ColaboradorNota
        {
            ColaboradorId = c.Id,
            AutorId = actor.UsuarioId,
            Comentario = texto,
            CreadoEn = time.GetUtcNow()
        };
        db.ColaboradorNotas.Add(nota);
        await db.SaveChangesAsync(ct);

        var autor = await db.Usuarios.AsNoTracking().Where(u => u.Id == actor.UsuarioId).Select(u => u.Nombre).FirstOrDefaultAsync(ct);
        return new NotaDto(nota.Id, nota.Comentario, nota.AutorId, autor, nota.CreadoEn);
    }

    // ───────────────────────── Fotos ─────────────────────────

    public async Task<string> SubirFotoAsync(Guid id, byte[] datos, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var c = await CargarAsync(id, actor, false, ct);
        var imagen = ImagenValidator.Validar(datos);   // contenido real, no la extensión

        var clave = $"colaboradores/{c.Id}/{Guid.NewGuid():N}{imagen.Extension}";
        await storage.SubirAsync(clave, datos, imagen.Mime, ct);

        var anterior = c.Foto;
        c.Foto = clave;
        auditoria.Registrar(actor.UsuarioId, "foto_actualizada", "colaborador", c.Id.ToString(),
            new { imagen.Ancho, imagen.Alto, bytes = datos.Length }, ctx.Ip);

        try { await db.SaveChangesAsync(ct); }
        catch
        {
            await IntentarEliminarAsync(clave);   // no dejar archivos huérfanos
            throw;
        }

        if (!string.IsNullOrEmpty(anterior)) await IntentarEliminarAsync(anterior);
        return Convenciones.FotoUrl(c.Id, clave, publica: false)!;
    }

    public async Task EliminarFotoAsync(Guid id, Actor actor, ContextoPeticion ctx, CancellationToken ct)
    {
        var c = await CargarAsync(id, actor, false, ct);
        var anterior = c.Foto;
        if (string.IsNullOrEmpty(anterior)) return;

        c.Foto = null;
        auditoria.Registrar(actor.UsuarioId, "foto_eliminada", "colaborador", c.Id.ToString(), null, ctx.Ip);
        await db.SaveChangesAsync(ct);
        await IntentarEliminarAsync(anterior);
    }

    public async Task<FotoArchivo> ObtenerFotoAsync(Guid id, bool soloPublico, Actor? actor, CancellationToken ct)
    {
        var c = await db.Colaboradores.AsNoTracking().Include(x => x.Usuario).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw ApiException.NoEncontrado("No hay foto.");

        var permitido = soloPublico
            ? Convenciones.EsPublico(c)
            : actor is not null && (actor.EsAdmin || actor.UsuarioId == c.UsuarioId);

        if (!permitido || string.IsNullOrEmpty(c.Foto)) throw ApiException.NoEncontrado("No hay foto.");

        var datos = await storage.ObtenerAsync(c.Foto, ct) ?? throw ApiException.NoEncontrado("No hay foto.");
        return new FotoArchivo(datos, Convenciones.MimeDe(c.Foto), Path.GetFileNameWithoutExtension(c.Foto));
    }

    private async Task IntentarEliminarAsync(string clave)
    {
        if (!clave.StartsWith("colaboradores/", StringComparison.Ordinal)) return;
        try { await storage.EliminarAsync(clave, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "No se pudo borrar la foto {Clave} de Storage.", clave); }
    }
}
