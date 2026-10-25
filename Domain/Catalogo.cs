namespace ZOne.Api.Domain;

public sealed class Sala
{
    public long Id { get; set; }
    public string Nombre { get; set; } = "";
    public decimal PrecioHora { get; set; }
    public bool Activo { get; set; } = true;
    public DateTimeOffset CreadoEn { get; set; }
}

public sealed class Servicio
{
    public long Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? Descripcion { get; set; }
    public string Categoria { get; set; } = "";
    public EtapaProyecto? Etapa { get; set; }
    public short SesionesIncluidas { get; set; }
    public decimal? Precio { get; set; }
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

/// <summary>Cada cambio de tarifa de una sala o de un servicio. Lo llena la base de datos automáticamente.</summary>
public sealed class HistorialPrecio
{
    public long Id { get; set; }
    public string Tabla { get; set; } = "";
    public long RegistroId { get; set; }
    public string Nombre { get; set; } = "";
    public decimal? PrecioAnterior { get; set; }
    public decimal? PrecioNuevo { get; set; }
    public DateTimeOffset CambiadoEn { get; set; }
}
