namespace ZOne.Api.Dtos;

public sealed record NotificacionDto(
    long Id, string Tipo, string Titulo, string Mensaje, long? ProyectoId, long? SesionId, bool Leida, DateTimeOffset CreadoEn);

public sealed record NoLeidasDto(int Total);
