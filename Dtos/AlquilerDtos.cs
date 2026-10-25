namespace ZOne.Api.Dtos;

/// <summary>Fechas en ISO 8601 con zona, por ejemplo 2026-11-05T15:00:00-05:00. ClienteId solo lo usa el administrador.</summary>
public sealed record CrearAlquilerRequest(long SalaId, DateTimeOffset Inicio, DateTimeOffset Fin, Guid? ClienteId);

public sealed record RegistrarPagoRequest(string? Referencia);

public sealed record AlquilerDto(
    long Id,
    long SalaId,
    string Sala,
    Guid ClienteId,
    string Cliente,
    DateTimeOffset Inicio,
    DateTimeOffset Fin,
    decimal DuracionHoras,
    string Estado,
    DateTimeOffset? ExpiraEn,
    decimal Monto,
    string EstadoPago,
    DateTimeOffset? PagadaEn,
    string? ReferenciaPago,
    bool Cancelable);

/// <summary>Bloque ocupado de una sala. Es público: no incluye ningún dato de la persona que reservó.</summary>
public sealed record BloqueOcupadoDto(DateTimeOffset Inicio, DateTimeOffset Fin, string Estado);

public sealed record DisponibilidadDto(
    long SalaId,
    string Sala,
    DateTimeOffset Desde,
    DateTimeOffset Hasta,
    IReadOnlyList<BloqueOcupadoDto> Ocupado);
