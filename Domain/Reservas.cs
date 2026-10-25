namespace ZOne.Api.Domain;

// Coinciden con los enums de PostgreSQL: PendientePago -> pendiente_pago, SesionExtra -> sesion_extra, etc.
public enum TipoSesion { Alquiler, Proyecto }
public enum EstadoSesion { PendientePago, PendienteAceptacion, Confirmada, Cumplida, Expirada, Cancelada }
public enum EstadoOrden { Pendiente, Pagada, Expirada }
public enum ConceptoOrden { Alquiler, Servicio, SesionExtra }

/// <summary>Una reserva de sala: alquiler (cliente) o sesión de un proyecto. Esta etapa usa los alquileres.</summary>
public sealed class Sesion
{
    public long Id { get; set; }
    public TipoSesion Tipo { get; set; }
    public long SalaId { get; set; }
    public Guid? ClienteId { get; set; }
    public long? ProyectoId { get; set; }
    public short? NumeroSesion { get; set; }
    public EtapaProyecto? Etapa { get; set; }
    public bool EsExtra { get; set; }
    public DateTimeOffset Inicio { get; set; }
    public DateTimeOffset Fin { get; set; }
    public EstadoSesion Estado { get; set; }
    public DateTimeOffset? ExpiraEn { get; set; }
    public string? Resena { get; set; }
    public DateTimeOffset? CumplidaEn { get; set; }
    public Guid? CumplidaPor { get; set; }
    public Guid? CreadaPor { get; set; }
    public DateTimeOffset CreadoEn { get; set; }

    public Sala Sala { get; set; } = null!;
    public Cliente? Cliente { get; set; }
    public Proyecto? Proyecto { get; set; }
    public List<AceptacionSesion> Aceptaciones { get; set; } = [];
}

/// <summary>Cobro pendiente o pagado. El sistema solo REGISTRA el pago; no procesa dinero.</summary>
public sealed class Orden
{
    public long Id { get; set; }
    public ConceptoOrden Concepto { get; set; }
    public Guid ClienteId { get; set; }
    public long? SesionId { get; set; }
    public long? ProyectoId { get; set; }
    public decimal Monto { get; set; }
    public EstadoOrden Estado { get; set; } = EstadoOrden.Pendiente;
    public DateTimeOffset VenceEn { get; set; }
    public DateTimeOffset? PagadaEn { get; set; }
    public Guid? RegistradaPor { get; set; }
    public string? ReferenciaPago { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
    public DateTimeOffset ActualizadoEn { get; set; }

    public Sesion? Sesion { get; set; }
}
