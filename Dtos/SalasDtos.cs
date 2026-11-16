namespace ZOne.Api.Dtos;

// ───────────────────────── Administrador ─────────────────────────

/// <summary>
/// Crear o editar una sala (la edición reemplaza todos los datos). Si no envías activo ni publicada se conserva el valor actual (una sala nueva nace en servicio y sin publicar); para publicarla hacen falta
/// capacidad, canales simultáneos, al menos un DAW y precio por hora (son las 4 etiquetas de la web).
/// Backline vacío = la sala no incluye instrumentos.
/// </summary>
public sealed record SalaRequest(
    string Nombre,
    string? Descripcion,
    List<string>? TiposUso,
    decimal? DimensionesM2,
    int? CapacidadMax,
    decimal PrecioHora,
    decimal PrecioSesion,
    string? TratamientoAcustico,
    int CabinasAislamiento,
    bool ConexionVisual,
    int? CanalesSimultaneos,
    List<string>? Daws,
    string? ConsolaTipo,
    string? ConsolaModelo,
    string? Conversores,
    List<string>? Microfonia,
    List<string>? Backline,
    bool MonitoreoPersonal,
    string? MonitoreoSistema,
    List<string>? ServiciosAdicionales,
    bool? Activo = null,
    bool? Publicada = null);

/// <summary>Cambia solo el estado. Activo = en servicio (false: mantenimiento). Publicada = visible en la web. Lo que no se envía no cambia.</summary>
public sealed record EstadoSalaRequest(bool? Activo, bool? Publicada);

public sealed record EnlaceMedioRequest(string Tipo, string Url, string? Titulo);
public sealed record ActualizarMedioRequest(string? Titulo, int? Orden);

public sealed record MedioDto(long Id, string Tipo, string Url, string? Titulo, int Orden);

/// <summary>Fila del listado del administrador.</summary>
public sealed record SalaResumenDto(
    long Id, string Nombre, bool Activo, bool Publicada, int? CapacidadMax, int? CanalesSimultaneos,
    string? DawPrincipal, decimal PrecioHora, decimal PrecioSesion, string? PortadaUrl);

public sealed record SalaAdminDto(
    long Id, string Nombre, string? Descripcion, bool Activo, bool Publicada, DateTimeOffset CreadoEn,
    decimal PrecioHora, decimal PrecioSesion, FichaTecnicaDto Ficha, IReadOnlyList<MedioDto> Medios);

/// <summary>Opciones para los selectores del formulario.</summary>
public sealed record OpcionDto(string Valor, string Etiqueta);
public sealed record SalaOpcionesDto(
    IReadOnlyList<OpcionDto> TiposUso, IReadOnlyList<OpcionDto> TratamientosAcusticos,
    IReadOnlyList<OpcionDto> TiposConsola, IReadOnlyList<string> ServiciosSugeridos, IReadOnlyList<string> BacklineSugerido,
    int MaxFotos, int MaxEnlaces, int MaxFotoMb);

// ───────────────────────── Ficha técnica (admin y vista pública) ─────────────────────────

public sealed record FichaTecnicaDto(
    IReadOnlyList<string> TiposUso,
    decimal? DimensionesM2,
    int? CapacidadMax,
    string? TratamientoAcustico,
    int CabinasAislamiento,
    bool ConexionVisual,
    int? CanalesSimultaneos,
    IReadOnlyList<string> Daws,
    string? ConsolaTipo,
    string? ConsolaModelo,
    string? Conversores,
    IReadOnlyList<string> Microfonia,
    bool TieneBackline,
    IReadOnlyList<string> Backline,
    bool MonitoreoPersonal,
    string? MonitoreoSistema,
    IReadOnlyList<string> ServiciosAdicionales);

// ───────────────────────── Vista pública ─────────────────────────

/// <summary>Las 4 etiquetas rápidas que más busca un cliente.</summary>
public sealed record SalaBadgesDto(int? Capacidad, int? CanalesSimultaneos, string? DawPrincipal, decimal PrecioHora);

/// <summary>Tarjeta de la lista pública. Reservable = en servicio (si está en mantenimiento se ve, pero no se puede reservar).</summary>
public sealed record SalaPublicaDto(
    long Id, string Nombre, string? Descripcion, bool Reservable, SalaBadgesDto Badges, decimal PrecioSesion,
    IReadOnlyList<string> TiposUso, string? PortadaUrl);

/// <summary>Detalle público: etiquetas arriba y la "Ficha técnica completa" en una pestaña desplegable.</summary>
public sealed record SalaPublicaDetalleDto(
    long Id, string Nombre, string? Descripcion, bool Reservable, SalaBadgesDto Badges, decimal PrecioSesion,
    FichaTecnicaDto FichaTecnicaCompleta, IReadOnlyList<MedioDto> Fotos, IReadOnlyList<MedioDto> Muestras);
