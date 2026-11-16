namespace ZOne.Api.Options;

///Reglas del alquiler de salas. Los valores salen de las decisiones del plan; se pueden ajustar sin tocar código.</summary>
public sealed class AlquilerOptions
{
    public const string Section = "Alquiler";

    /// Horas que una reserva pendiente de pago retiene la sala (decidido: 24).</summary>
    public int RetencionHoras { get; set; } = 24;

    /// Si es true, la retención termina antes si la sesión empieza antes de cumplirse las horas (recomendación #12).</summary>
    public bool LiberarAlInicio { get; set; } = true;

    ///Duración mínima en minutos
    public int MinMinutos { get; set; } = 60;

    /// Duración máxima en minutos
    public int MaxMinutos { get; set; } = 240;

    ///Las reservas empiezan y duran en múltiplos de este bloque
    public int GranularidadMinutos { get; set; } = 30;

    /// Tiempo mínimo entre ahora y el inicio, para que alcance a pagarse
    public int MinAnticipacionMinutos { get; set; } = 60;

    public int MaxDiasAnticipacion { get; set; } = 180;

    /// Tope de reservas sin pagar por cliente, para que nadie acapare la sala gratis
    public int MaxPendientesPorCliente { get; set; } = 3;
}
