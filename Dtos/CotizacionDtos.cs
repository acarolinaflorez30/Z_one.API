namespace ZOne.Api.Dtos;

/// <summary>
/// Concepto: sesion (servicio parcial o una de las 4 de la canción: 3 h, precio por sesión) · ajuste (gratis) ·
/// extra (por hora, mínimo 1 h) · alquiler (por hora, de 1 a 4 h). Horas solo se usa en extra y alquiler.
/// </summary>
public sealed record CotizacionRequest(string Concepto, long SalaId, decimal? Horas, List<Guid>? ColaboradorIds);

/// <summary>Unidad: sesion u hora. Cantidad: 1 sesión o las horas.</summary>
public sealed record LineaCobroDto(string Tipo, string Nombre, string Unidad, decimal Cantidad, decimal PrecioUnitario, decimal Subtotal);

/// <summary>Modalidad: por_sesion, por_hora o gratis.</summary>
public sealed record CotizacionDto(string Modalidad, decimal Horas, decimal Total, IReadOnlyList<LineaCobroDto> Lineas);

/// <summary>Lo que ve un colaborador invitado: qué es la sesión y cuándo, con todos los datos para decidir.</summary>
public sealed record InvitacionDto(
    long SesionId, string Origen, long? ProyectoId, string? Proyecto, string? TipoProyecto, string? Servicio,
    string Descripcion, short? Numero, short? TotalSesiones, bool EsAjuste, bool EsExtra,
    string Rol, string Sala, DateTimeOffset Inicio, DateTimeOffset Fin, decimal DuracionHoras,
    string Cliente, string Estado, DateTimeOffset EnviadaEn, DateTimeOffset? ExpiraEn, DateTimeOffset? RespondidaEn,
    decimal? MiPrecio);
