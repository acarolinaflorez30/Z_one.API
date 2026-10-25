namespace ZOne.Api.Options;

/// <summary>Reglas del alquiler de salas. Los valores salen de las decisiones del plan; se pueden ajustar sin tocar código.</summary>
public sealed class AlquilerOptions
{
    public const string Section = "Alquiler";

    /// <summary>Horas que una reserva pendiente de pago retiene la sala (decidido: 24).</summary>
    public int RetencionHoras { get; set; } = 24;

    /// <summary>Si es true, la retención termina antes si la sesión empieza antes de cumplirse las horas (recomendación #12).</summary>
    public bool LiberarAlInicio { get; set; } = true;

    /// <summary>Duración mínima en minutos (propuesta #6: 1 hora).</summary>
    public int MinMinutos { get; set; } = 60;

    /// <summary>Duración máxima en minutos (decidido: 4 horas; la base de datos también lo exige).</summary>
    public int MaxMinutos { get; set; } = 240;

    /// <summary>Las reservas empiezan y duran en múltiplos de este bloque.</summary>
    public int GranularidadMinutos { get; set; } = 30;

    /// <summary>Tiempo mínimo entre ahora y el inicio, para que alcance a pagarse.</summary>
    public int MinAnticipacionMinutos { get; set; } = 60;

    public int MaxDiasAnticipacion { get; set; } = 180;

    /// <summary>Tope de reservas sin pagar por cliente, para que nadie acapare la sala gratis.</summary>
    public int MaxPendientesPorCliente { get; set; } = 3;
}
