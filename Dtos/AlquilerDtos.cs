namespace ZOne.Api.Dtos;

/// <summary>
/// Fechas en ISO 8601 con zona, por ejemplo 2026-11-05T15:00:00-05:00. ClienteId solo lo usa el administrador.
/// ColaboradorIds es opcional: sin colaboradores solo se alquila la sala.
/// </summary>
public sealed record CrearAlquilerRequest(long SalaId, DateTimeOffset Inicio, DateTimeOffset Fin, Guid? ClienteId, List<Guid>? ColaboradorIds = null);

public sealed record RegistrarPagoRequest(string? Referencia);

/// <summary>
/// Monto: mientras esté pendiente sigue las tarifas actuales; al registrar el pago queda fijo.
/// ExpiraEn es el vencimiento de la espera de colaboradores o del pago, según el estado.
/// </summary>
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
    decimal? Monto,
    string? EstadoPago,
    DateTimeOffset? PagadaEn,
    string? ReferenciaPago,
    bool Cancelable,
    bool PuedeConfirmar,
    bool RequiereDecision,
    IReadOnlyList<InvitadoDto> Invitados);

/// <summary>Bloque ocupado de una sala. Es público: solo dice "ocupado", sin ningún dato de la persona ni del tipo de reserva.</summary>
public sealed record BloqueOcupadoDto(DateTimeOffset Inicio, DateTimeOffset Fin, string Estado);

public sealed record DisponibilidadDto(
    long SalaId,
    string Sala,
    DateTimeOffset Desde,
    DateTimeOffset Hasta,
    IReadOnlyList<BloqueOcupadoDto> Ocupado);
