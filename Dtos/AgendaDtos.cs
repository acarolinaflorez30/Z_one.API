namespace ZOne.Api.Dtos;

/// <summary>Un bloque de la agenda: un alquiler de sala o una sesión de proyecto.</summary>
public sealed record AgendaItemDto(
    string Tipo,                    // alquiler | proyecto
    long SesionId,
    string Titulo,
    long SalaId,
    string Sala,
    DateTimeOffset Inicio,
    DateTimeOffset Fin,
    decimal DuracionHoras,
    string Estado,
    string? Etapa,
    short? Numero,
    bool EsExtra,
    long? ProyectoId,
    string? Proyecto,
    Guid? ClienteId,
    string? Cliente,
    DateTimeOffset? ExpiraEn,
    bool RequiereMiRespuesta,       // colaborador: me invitaron y falta mi respuesta · cliente/admin: la sesión espera mi decisión (invitados que rechazaron)
    bool PorCerrar,                 // confirmada y ya terminó: falta marcarla cumplida con reseña
    string? Resena,
    decimal? Monto,                 // lo ven solo el cliente dueño y el administrador (al estar pendiente sigue las tarifas actuales)
    string? EstadoPago);

/// <summary>Contadores para mostrar avisos en el inicio de cada usuario.</summary>
public sealed record AgendaResumenDto(
    int Proximas7Dias,
    int SesionesPorResponder,        // colaborador: invitaciones por responder
    int SesionesPorCerrar,
    int SesionesPorDecidir,          // cliente/admin: sesiones donde alguien rechazó y toca invitar a otro, confirmar o cancelar
    int CobrosPendientes,
    int RevisionesPorDecidir,       // cliente: trabajos que debe aprobar; administrador: revisiones cuyo plazo de 24 h venció sin respuesta
    int NotificacionesSinLeer,
    AgendaItemDto? Proxima);
