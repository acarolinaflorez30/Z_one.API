namespace ZOne.Api.Domain;

public sealed class Sala
{
    public long Id { get; set; }
    public string Nombre { get; set; } = "";
    /// <summary>Tarifa por hora (alquiler y sesiones extra). La define solo el administrador.</summary>
    public decimal PrecioHora { get; set; }
    /// <summary>Tarifa de una sesión estándar de grabación de 3 horas (servicio parcial y cada sesión de la canción completa).</summary>
    public decimal PrecioSesion { get; set; }
    /// <summary>En servicio (true) o fuera de servicio, por ejemplo en mantenimiento (false). No es lo mismo que estar ocupada.</summary>
    public bool Activo { get; set; } = true;
    /// <summary>Se muestra en la web pública. La decide el administrador, independiente de Activo y de la ocupación.</summary>
    public bool Publicada { get; set; }
    public DateTimeOffset CreadoEn { get; set; }

    // ── Ficha técnica ──
    public string? Descripcion { get; set; }
    /// <summary>grabacion_bandas | grabacion_vocal_urbana | mezcla_masterizacion | doblaje_podcast | otro</summary>
    public List<string> TiposUso { get; set; } = [];
    public decimal? DimensionesM2 { get; set; }
    public int? CapacidadMax { get; set; }
    /// <summary>seca | viva | variable</summary>
    public string? TratamientoAcustico { get; set; }
    public int CabinasAislamiento { get; set; }
    /// <summary>Visor acústico hacia el control room o circuito cerrado de video.</summary>
    public bool ConexionVisual { get; set; }
    public int? CanalesSimultaneos { get; set; }
    /// <summary>Software instalado; el primero es el principal.</summary>
    public List<string> Daws { get; set; } = [];
    /// <summary>analogica | digital</summary>
    public string? ConsolaTipo { get; set; }
    public string? ConsolaModelo { get; set; }
    public string? Conversores { get; set; }
    public List<string> Microfonia { get; set; } = [];
    /// <summary>Instrumentos en sala. Vacío = la sala no incluye ninguno.</summary>
    public List<string> Backline { get; set; } = [];
    public bool MonitoreoPersonal { get; set; }
    public string? MonitoreoSistema { get; set; }
    public List<string> ServiciosAdicionales { get; set; } = [];

    public List<SalaMedio> Medios { get; set; } = [];
}

/// <summary>Galería de una sala: fotos guardadas en Storage (Clave) o enlaces https a un audio o video de muestra (Url).</summary>
public sealed class SalaMedio
{
    public long Id { get; set; }
    public long SalaId { get; set; }
    /// <summary>foto | audio | video</summary>
    public string Tipo { get; set; } = "foto";
    public string? Clave { get; set; }
    public string? Url { get; set; }
    public string? Titulo { get; set; }
    public int Orden { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
}

/// <summary>Un servicio ya no tiene precio propio: el cobro sale de la sala y de los colaboradores de cada sesión.</summary>
public sealed class Servicio
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? Descripcion { get; set; }
    public string Categoria { get; set; } = "";
    public EtapaProyecto? Etapa { get; set; }
    public short SesionesIncluidas { get; set; }
    public bool Activo { get; set; } = true;
    public DateTimeOffset CreadoEn { get; set; }
}

public sealed class ColaboradorProyecto
{
    public long Id { get; set; }
    public Guid ColaboradorId { get; set; }
    public string Titulo { get; set; } = "";
    public string? Descripcion { get; set; }
    public string? Productora { get; set; }
    public string? Rol { get; set; }
    public int? Anio { get; set; }
    public EstadoPortafolio Estado { get; set; } = EstadoPortafolio.Finalizado;
    public DateTimeOffset CreadoEn { get; set; }
}

public sealed class ColaboradorNota
{
    public long Id { get; set; }
    public Guid ColaboradorId { get; set; }
    public Guid? AutorId { get; set; }
    public string Comentario { get; set; } = "";
    public DateTimeOffset CreadoEn { get; set; }
}

/// <summary>
/// Cada cambio de tarifa de una sala, un servicio o un colaborador. Lo llena la base de datos automáticamente.
/// Salas y servicios usan RegistroId (numérico); los colaboradores usan RegistroUuid.
/// </summary>
public sealed class HistorialPrecio
{
    public long Id { get; set; }
    public string Tabla { get; set; } = "";
    public long? RegistroId { get; set; }
    public Guid? RegistroUuid { get; set; }
    /// <summary>hora | sesion | servicio</summary>
    public string TipoPrecio { get; set; } = "hora";
    public string Nombre { get; set; } = "";
    public decimal? PrecioAnterior { get; set; }
    public decimal? PrecioNuevo { get; set; }
    public DateTimeOffset CambiadoEn { get; set; }
}
