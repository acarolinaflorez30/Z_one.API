using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Security;

namespace ZOne.Api.Services;

public interface ISalasService
{
    // Administrador
    Task<PaginaDto<SalaResumenDto>> ListarAsync(string? q, bool? activo, bool? publicada, int pagina, int tamano, CancellationToken ct);
    Task<SalaAdminDto> ObtenerAsync(long id, CancellationToken ct);
    SalaOpcionesDto Opciones();
    Task<SalaAdminDto> CrearAsync(SalaRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task<SalaAdminDto> ActualizarAsync(long id, SalaRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task<SalaAdminDto> CambiarEstadoAsync(long id, EstadoSalaRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task<MedioDto> SubirFotoAsync(long id, byte[] datos, string? titulo, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task<MedioDto> AgregarEnlaceAsync(long id, EnlaceMedioRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task<MedioDto> ActualizarMedioAsync(long id, long medioId, ActualizarMedioRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task EliminarMedioAsync(long id, long medioId, Guid adminId, ContextoPeticion ctx, CancellationToken ct);
    Task<FotoArchivo> ObtenerFotoAsync(long id, long medioId, bool soloPublico, CancellationToken ct);

    // Público
    Task<IReadOnlyList<SalaPublicaDto>> ListarPublicasAsync(string? q, CancellationToken ct);
    Task<SalaPublicaDetalleDto> ObtenerPublicaAsync(long id, CancellationToken ct);
}

/// <summary>
/// Salas de grabación de la productora. Solo el administrador las crea y edita. "Activo" (en servicio o en mantenimiento)
/// y "Publicada" (se muestra en la web) son decisiones del administrador, independientes de que la sala esté ocupada en un horario.
/// </summary>
public sealed class SalasService(
    ZOneDbContext db,
    IFotoStorage storage,
    IAuditoriaService auditoria,
    ILogger<SalasService> logger,
    TimeProvider time) : ISalasService
{
    private const decimal PrecioMax = 1_000_000_000m;
    private const int MaxFotos = 12;
    private const int MaxEnlaces = 6;

    private static readonly OpcionDto[] TiposUsoCatalogo =
    [
        new("grabacion_bandas", "Grabación de bandas completas"),
        new("grabacion_vocal_urbana", "Grabación vocal / Música urbana"),
        new("mezcla_masterizacion", "Mezcla y masterización"),
        new("doblaje_podcast", "Doblaje / Podcast"),
        new("otro", "Otro")
    ];

    private static readonly OpcionDto[] TratamientosCatalogo =
    [
        new("seca", "Acústica seca / control total"),
        new("viva", "Acústica viva / natural"),
        new("variable", "Acústica variable con paneles móviles")
    ];

    private static readonly OpcionDto[] ConsolasCatalogo = [new("analogica", "Analógica"), new("digital", "Digital")];

    private static readonly string[] ServiciosSugeridos =
    [
        "Aire acondicionado silencioso", "Sala de descanso (lounge)",
        "Acceso a nivel de calle para carga de equipos pesados", "Wi-Fi de alta velocidad"
    ];

    private static readonly string[] BacklineSugerido =
        ["Batería acústica", "Piano de cola", "Amplificadores de guitarra", "Amplificadores de bajo", "Sintetizadores"];

    public SalaOpcionesDto Opciones() => new(
        TiposUsoCatalogo, TratamientosCatalogo, ConsolasCatalogo, ServiciosSugeridos, BacklineSugerido,
        MaxFotos, MaxEnlaces, ImagenValidator.MaxBytesGaleria / 1_048_576);

    // ───────────────────────── Mapeos ─────────────────────────

    private static string FotoUrl(SalaMedio m, bool publica)
    {
        var v = Path.GetFileNameWithoutExtension(m.Clave ?? "");
        return publica
            ? $"/publico/salas/{m.SalaId}/medios/{m.Id}?v={v}"
            : $"/admin/salas/{m.SalaId}/medios/{m.Id}/archivo?v={v}";
    }

    private static MedioDto Mapear(SalaMedio m, bool publica) =>
        new(m.Id, m.Tipo, m.Tipo == "foto" ? FotoUrl(m, publica) : m.Url ?? "", m.Titulo, m.Orden);

    private static IEnumerable<SalaMedio> Ordenados(IEnumerable<SalaMedio> medios) =>
        medios.OrderBy(m => m.Orden).ThenBy(m => m.Id);

    private static string? Portada(Sala s, bool publica) =>
        Ordenados(s.Medios).Where(m => m.Tipo == "foto").Select(m => FotoUrl(m, publica)).FirstOrDefault();

    private static FichaTecnicaDto Ficha(Sala s) => new(
        s.TiposUso, s.DimensionesM2, s.CapacidadMax, s.TratamientoAcustico, s.CabinasAislamiento, s.ConexionVisual,
        s.CanalesSimultaneos, s.Daws, s.ConsolaTipo, s.ConsolaModelo, s.Conversores, s.Microfonia,
        s.Backline.Count > 0, s.Backline, s.MonitoreoPersonal, s.MonitoreoSistema, s.ServiciosAdicionales);

    private static SalaBadgesDto Badges(Sala s) =>
        new(s.CapacidadMax, s.CanalesSimultaneos, s.Daws.FirstOrDefault(), s.PrecioHora);

    private static SalaAdminDto MapearAdmin(Sala s) => new(
        s.Id, s.Nombre, s.Descripcion, s.Activo, s.Publicada, s.CreadoEn, s.PrecioHora, s.PrecioSesion, Ficha(s),
        Ordenados(s.Medios).Select(m => Mapear(m, publica: false)).ToList());

    // ───────────────────────── Validación ─────────────────────────

    private static decimal ValidarPrecio(decimal precio, string campo)
    {
        if (precio < 0 || precio > PrecioMax)
            throw ApiException.Validacion(campo, "El precio debe ser un valor entre 0 y 1.000.000.000.");
        return Math.Round(precio, 2);
    }

    private static List<string> Etiquetas(List<string>? entrada, string campo, int maxItems, int maxLargo = 100)
    {
        var resultado = new List<string>();
        foreach (var e in entrada ?? new List<string>())
        {
            var t = e?.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (t.Length > maxLargo)
                throw ApiException.Validacion(campo, $"Cada elemento admite hasta {maxLargo} caracteres.");
            if (!resultado.Contains(t, StringComparer.OrdinalIgnoreCase)) resultado.Add(t);
        }
        if (resultado.Count > maxItems)
            throw ApiException.Validacion(campo, $"Máximo {maxItems} elementos.");
        return resultado;
    }

    private static string? DeCatalogo(string? valor, OpcionDto[] catalogo, string campo)
    {
        var t = valor?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(t)) return null;
        if (!catalogo.Any(o => o.Valor == t))
            throw ApiException.Validacion(campo, $"Valor no válido. Opciones: {string.Join(", ", catalogo.Select(o => o.Valor))}.");
        return t;
    }

    private static void Aplicar(Sala s, SalaRequest r)
    {
        s.Nombre = Entrada.Texto(r.Nombre, "nombre", 2, 80);
        s.Descripcion = Entrada.TextoOpcional(r.Descripcion, "descripcion", 2000);

        var usos = Etiquetas(r.TiposUso, "tiposUso", TiposUsoCatalogo.Length, 40).Select(u => u.ToLowerInvariant()).Distinct().ToList();
        var invalido = usos.FirstOrDefault(u => TiposUsoCatalogo.All(o => o.Valor != u));
        if (invalido is not null)
            throw ApiException.Validacion("tiposUso",
                $"'{invalido}' no es un tipo de uso válido. Opciones: {string.Join(", ", TiposUsoCatalogo.Select(o => o.Valor))}.");
        s.TiposUso = usos;

        if (r.DimensionesM2 is { } m2 && (m2 <= 0 || m2 > 100_000))
            throw ApiException.Validacion("dimensionesM2", "Las dimensiones deben ser mayores que 0 m².");
        s.DimensionesM2 = r.DimensionesM2 is { } d ? Math.Round(d, 2) : null;

        if (r.CapacidadMax is { } cap && (cap < 1 || cap > 500))
            throw ApiException.Validacion("capacidadMax", "La capacidad debe estar entre 1 y 500 personas.");
        s.CapacidadMax = r.CapacidadMax;

        s.PrecioHora = ValidarPrecio(r.PrecioHora, "precioHora");
        s.PrecioSesion = ValidarPrecio(r.PrecioSesion, "precioSesion");

        s.TratamientoAcustico = DeCatalogo(r.TratamientoAcustico, TratamientosCatalogo, "tratamientoAcustico");

        if (r.CabinasAislamiento < 0 || r.CabinasAislamiento > 50)
            throw ApiException.Validacion("cabinasAislamiento", "Las cabinas de aislamiento deben ser entre 0 y 50.");
        s.CabinasAislamiento = r.CabinasAislamiento;
        s.ConexionVisual = r.ConexionVisual;

        if (r.CanalesSimultaneos is { } can && (can < 1 || can > 1024))
            throw ApiException.Validacion("canalesSimultaneos", "Los canales simultáneos deben estar entre 1 y 1024.");
        s.CanalesSimultaneos = r.CanalesSimultaneos;

        s.Daws = Etiquetas(r.Daws, "daws", 30);     // el primero es el principal
        s.ConsolaTipo = DeCatalogo(r.ConsolaTipo, ConsolasCatalogo, "consolaTipo");
        s.ConsolaModelo = Entrada.TextoOpcional(r.ConsolaModelo, "consolaModelo", 200);
        s.Conversores = Entrada.TextoOpcional(r.Conversores, "conversores", 200);
        s.Microfonia = Etiquetas(r.Microfonia, "microfonia", 100, 120);
        s.Backline = Etiquetas(r.Backline, "backline", 50);   // vacío = la sala no incluye instrumentos

        s.MonitoreoPersonal = r.MonitoreoPersonal;
        s.MonitoreoSistema = r.MonitoreoPersonal ? Entrada.TextoOpcional(r.MonitoreoSistema, "monitoreoSistema", 200) : null;
        s.ServiciosAdicionales = Etiquetas(r.ServiciosAdicionales, "serviciosAdicionales", 30, 120);

        // Si no se envían, se conserva el valor actual (una sala nueva nace en servicio y sin publicar).
        s.Activo = r.Activo ?? s.Activo;
        s.Publicada = r.Publicada ?? s.Publicada;
        ExigirFichaParaPublicar(s);
    }

    /// <summary>Para publicar hacen falta los 4 datos de las etiquetas de la web.</summary>
    private static void ExigirFichaParaPublicar(Sala s)
    {
        if (!s.Publicada) return;
        var faltan = new List<string>();
        if (s.CapacidadMax is null) faltan.Add("capacidad de personas");
        if (s.CanalesSimultaneos is null) faltan.Add("canales simultáneos");
        if (s.Daws.Count == 0) faltan.Add("DAW principal");
        if (s.PrecioHora <= 0) faltan.Add("precio por hora");
        if (faltan.Count > 0)
            throw ApiException.Conflicto("ficha_incompleta", $"Para publicar la sala faltan: {string.Join(", ", faltan)}.");
    }

    private static string Patron(string q) =>
        "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private async Task GuardarAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.EsViolacionUnica())
        {
            throw ApiException.Conflicto("sala_duplicada", "Ya existe una sala con ese nombre.");
        }
    }

    private async Task<Sala> CargarAsync(long id, bool conMedios, CancellationToken ct)
    {
        var consulta = db.Salas.AsQueryable();
        if (conMedios) consulta = consulta.Include(s => s.Medios);
        return await consulta.FirstOrDefaultAsync(s => s.Id == id, ct)
               ?? throw ApiException.NoEncontrado("No existe esa sala.");
    }

    // ───────────────────────── Administrador: listar y editar ─────────────────────────

    public async Task<PaginaDto<SalaResumenDto>> ListarAsync(
        string? q, bool? activo, bool? publicada, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);
        var consulta = db.Salas.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var patron = Patron(q);
            consulta = consulta.Where(s => EF.Functions.ILike(s.Nombre, patron));
        }
        if (activo is { } a) consulta = consulta.Where(s => s.Activo == a);
        if (publicada is { } p) consulta = consulta.Where(s => s.Publicada == p);

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .Include(s => s.Medios)
            .OrderBy(s => s.Nombre).ThenBy(s => s.Id)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .ToListAsync(ct);

        var items = filas.Select(s => new SalaResumenDto(
            s.Id, s.Nombre, s.Activo, s.Publicada, s.CapacidadMax, s.CanalesSimultaneos,
            s.Daws.FirstOrDefault(), s.PrecioHora, s.PrecioSesion, Portada(s, publica: false))).ToList();
        return new PaginaDto<SalaResumenDto>(items, total, pagina, tamano);
    }

    public async Task<SalaAdminDto> ObtenerAsync(long id, CancellationToken ct)
    {
        var s = await db.Salas.AsNoTracking().Include(x => x.Medios).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw ApiException.NoEncontrado("No existe esa sala.");
        return MapearAdmin(s);
    }

    public async Task<SalaAdminDto> CrearAsync(SalaRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        var sala = new Sala { CreadoEn = time.GetUtcNow(), Activo = true, Publicada = false };
        Aplicar(sala, r);
        db.Salas.Add(sala);
        await GuardarAsync(ct);

        auditoria.Registrar(adminId, "sala_creada", "sala", sala.Id.ToString(),
            new { sala.Nombre, sala.Activo, sala.Publicada, sala.PrecioHora, sala.PrecioSesion }, ctx.Ip);
        await db.SaveChangesAsync(ct);
        return MapearAdmin(sala);
    }

    public async Task<SalaAdminDto> ActualizarAsync(long id, SalaRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        var sala = await CargarAsync(id, conMedios: true, ct);
        var antes = new { sala.Nombre, sala.Activo, sala.Publicada, sala.PrecioHora, sala.PrecioSesion };
        Aplicar(sala, r);

        auditoria.Registrar(adminId, "sala_actualizada", "sala", sala.Id.ToString(),
            new { antes, despues = new { sala.Nombre, sala.Activo, sala.Publicada, sala.PrecioHora, sala.PrecioSesion } }, ctx.Ip);
        await GuardarAsync(ct);
        return MapearAdmin(sala);
    }

    public async Task<SalaAdminDto> CambiarEstadoAsync(long id, EstadoSalaRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        if (r.Activo is null && r.Publicada is null)
            throw ApiException.Validacion("estado", "Indica activo, publicada o ambos.");

        var sala = await CargarAsync(id, conMedios: true, ct);
        var antes = new { sala.Activo, sala.Publicada };
        if (r.Activo is { } activo) sala.Activo = activo;
        if (r.Publicada is { } publicada) sala.Publicada = publicada;
        if (r.Publicada == true) ExigirFichaParaPublicar(sala);   // poner en mantenimiento no exige la ficha

        auditoria.Registrar(adminId, "sala_estado_cambiado", "sala", sala.Id.ToString(),
            new { antes, despues = new { sala.Activo, sala.Publicada } }, ctx.Ip);
        await db.SaveChangesAsync(ct);
        return MapearAdmin(sala);
    }

    // ───────────────────────── Galería ─────────────────────────

    public async Task<MedioDto> SubirFotoAsync(long id, byte[] datos, string? titulo, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        if (!await db.Salas.AnyAsync(s => s.Id == id, ct)) throw ApiException.NoEncontrado("No existe esa sala.");

        var fotos = await db.SalaMedios.CountAsync(m => m.SalaId == id && m.Tipo == "foto", ct);
        if (fotos >= MaxFotos)
            throw ApiException.Conflicto("limite_fotos", $"Una sala admite hasta {MaxFotos} fotos. Elimina alguna para subir otra.");

        var tituloLimpio = Entrada.TextoOpcional(titulo, "titulo", 150);   // antes de subir nada a Storage
        var imagen = ImagenValidator.Validar(datos, ImagenValidator.MaxBytesGaleria);   // contenido real, no la extensión
        var clave = $"salas/{id}/{Guid.NewGuid():N}{imagen.Extension}";
        await storage.SubirAsync(clave, datos, imagen.Mime, ct);

        var siguiente = await db.SalaMedios.Where(m => m.SalaId == id).Select(m => (int?)m.Orden).MaxAsync(ct) ?? -1;
        var medio = new SalaMedio
        {
            SalaId = id,
            Tipo = "foto",
            Clave = clave,
            Titulo = tituloLimpio,
            Orden = siguiente + 1,
            CreadoEn = time.GetUtcNow()
        };
        db.SalaMedios.Add(medio);
        auditoria.Registrar(adminId, "sala_foto_agregada", "sala", id.ToString(),
            new { imagen.Ancho, imagen.Alto, bytes = datos.Length }, ctx.Ip);

        try { await db.SaveChangesAsync(ct); }
        catch
        {
            await IntentarEliminarAsync(clave);   // no dejar archivos huérfanos
            throw;
        }
        return Mapear(medio, publica: false);
    }

    public async Task<MedioDto> AgregarEnlaceAsync(long id, EnlaceMedioRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        if (!await db.Salas.AnyAsync(s => s.Id == id, ct)) throw ApiException.NoEncontrado("No existe esa sala.");

        var tipo = (r.Tipo ?? "").Trim().ToLowerInvariant();
        if (tipo is not ("audio" or "video"))
            throw ApiException.Validacion("tipo", "El tipo debe ser audio o video.");

        var url = (r.Url ?? "").Trim();
        if (url.Length is 0 or > 500 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) || url.Any(char.IsWhiteSpace)
            || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo))
            throw ApiException.Validacion("url", "El enlace debe ser una dirección https válida (máximo 500 caracteres).");

        var enlaces = await db.SalaMedios.CountAsync(m => m.SalaId == id && m.Tipo != "foto", ct);
        if (enlaces >= MaxEnlaces)
            throw ApiException.Conflicto("limite_enlaces", $"Una sala admite hasta {MaxEnlaces} enlaces de muestra.");

        var siguiente = await db.SalaMedios.Where(m => m.SalaId == id).Select(m => (int?)m.Orden).MaxAsync(ct) ?? -1;
        var medio = new SalaMedio
        {
            SalaId = id,
            Tipo = tipo,
            Url = url,
            Titulo = Entrada.TextoOpcional(r.Titulo, "titulo", 150),
            Orden = siguiente + 1,
            CreadoEn = time.GetUtcNow()
        };
        db.SalaMedios.Add(medio);
        auditoria.Registrar(adminId, "sala_enlace_agregado", "sala", id.ToString(), new { tipo, uri.Host }, ctx.Ip);
        await db.SaveChangesAsync(ct);
        return Mapear(medio, publica: false);
    }

    public async Task<MedioDto> ActualizarMedioAsync(long id, long medioId, ActualizarMedioRequest r, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        var medio = await db.SalaMedios.FirstOrDefaultAsync(m => m.Id == medioId && m.SalaId == id, ct)
                    ?? throw ApiException.NoEncontrado("No existe ese elemento de la galería.");

        if (r.Titulo is not null) medio.Titulo = Entrada.TextoOpcional(r.Titulo, "titulo", 150);
        if (r.Orden is { } orden)
        {
            if (orden < 0 || orden > 1000) throw ApiException.Validacion("orden", "El orden debe estar entre 0 y 1000.");
            medio.Orden = orden;
        }

        auditoria.Registrar(adminId, "sala_medio_actualizado", "sala", id.ToString(), new { medioId }, ctx.Ip);
        await db.SaveChangesAsync(ct);
        return Mapear(medio, publica: false);
    }

    public async Task EliminarMedioAsync(long id, long medioId, Guid adminId, ContextoPeticion ctx, CancellationToken ct)
    {
        var medio = await db.SalaMedios.FirstOrDefaultAsync(m => m.Id == medioId && m.SalaId == id, ct)
                    ?? throw ApiException.NoEncontrado("No existe ese elemento de la galería.");

        var clave = medio.Clave;
        db.SalaMedios.Remove(medio);
        auditoria.Registrar(adminId, "sala_medio_eliminado", "sala", id.ToString(), new { medioId, medio.Tipo }, ctx.Ip);
        await db.SaveChangesAsync(ct);
        if (!string.IsNullOrEmpty(clave)) await IntentarEliminarAsync(clave);
    }

    public async Task<FotoArchivo> ObtenerFotoAsync(long id, long medioId, bool soloPublico, CancellationToken ct)
    {
        var fila = await (
            from m in db.SalaMedios.AsNoTracking()
            join s in db.Salas.AsNoTracking() on m.SalaId equals s.Id
            where m.Id == medioId && m.SalaId == id && m.Tipo == "foto" && (!soloPublico || s.Publicada)
            select m.Clave).FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(fila)) throw ApiException.NoEncontrado("No hay foto.");
        var datos = await storage.ObtenerAsync(fila, ct) ?? throw ApiException.NoEncontrado("No hay foto.");
        return new FotoArchivo(datos, Convenciones.MimeDe(fila), Path.GetFileNameWithoutExtension(fila));
    }

    private async Task IntentarEliminarAsync(string clave)
    {
        if (!clave.StartsWith("salas/", StringComparison.Ordinal)) return;
        try { await storage.EliminarAsync(clave, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "No se pudo borrar la foto {Clave} de Storage.", clave); }
    }

    // ───────────────────────── Público ─────────────────────────

    public async Task<IReadOnlyList<SalaPublicaDto>> ListarPublicasAsync(string? q, CancellationToken ct)
    {
        var consulta = db.Salas.AsNoTracking().Where(s => s.Publicada);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var patron = Patron(q);
            consulta = consulta.Where(s => EF.Functions.ILike(s.Nombre, patron));
        }

        var filas = await consulta.Include(s => s.Medios).OrderBy(s => s.Nombre).ThenBy(s => s.Id).ToListAsync(ct);
        return filas.Select(s => new SalaPublicaDto(
            s.Id, s.Nombre, s.Descripcion, s.Activo, Badges(s), s.PrecioSesion, s.TiposUso, Portada(s, publica: true))).ToList();
    }

    public async Task<SalaPublicaDetalleDto> ObtenerPublicaAsync(long id, CancellationToken ct)
    {
        var s = await db.Salas.AsNoTracking().Include(x => x.Medios).FirstOrDefaultAsync(x => x.Id == id && x.Publicada, ct)
                ?? throw ApiException.NoEncontrado("No existe esa sala.");

        var medios = Ordenados(s.Medios).ToList();
        return new SalaPublicaDetalleDto(
            s.Id, s.Nombre, s.Descripcion, s.Activo, Badges(s), s.PrecioSesion, Ficha(s),
            medios.Where(m => m.Tipo == "foto").Select(m => Mapear(m, publica: true)).ToList(),
            medios.Where(m => m.Tipo != "foto").Select(m => Mapear(m, publica: true)).ToList());
    }
}
