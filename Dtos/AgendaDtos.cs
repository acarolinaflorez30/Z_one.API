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
    bool RequiereMiRespuesta,       // la sesión espera que yo la acepte o rechace
    bool PorCerrar,                 // confirmada y ya terminó: falta marcarla cumplida con reseña
    string? Resena,
    decimal? Monto,                 // solo alquileres (los ve el dueño y el administrador)
    string? EstadoPago);

/// <summary>Contadores para mostrar avisos en el inicio de cada usuario.</summary>
public sealed record AgendaResumenDto(
    int Proximas7Dias,
    int SesionesPorResponder,
    int SesionesPorCerrar,
    int SolicitudesPendientes,
    int CobrosPendientes,
    AgendaItemDto? Proxima);
