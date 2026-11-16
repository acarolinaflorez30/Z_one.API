namespace ZOne.Api.Dtos;

/// <summary>CorreoDisponible = el servidor tiene el envío de correos activado. Correo = la persona quiere recibirlos.</summary>
public sealed record PreferenciasNotificacionDto(bool Correo, bool CorreoDisponible);

public sealed record ActualizarPreferenciasRequest(bool? Correo);
