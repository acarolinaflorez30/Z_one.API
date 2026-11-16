namespace ZOne.Api.Dtos;

/// <summary>Alta de una canción del proyecto. El título se valida entre 2 y 150 caracteres.</summary>
public sealed record CrearCancionRequest(string? Titulo, int? DuracionSeg, List<string>? Partes, bool? RequiereEnsayo);

/// <summary>Actualización parcial: los campos nulos se dejan como están.</summary>
public sealed record ActualizarCancionRequest(string? Titulo, int? DuracionSeg, List<string>? Partes, bool? RequiereEnsayo);

public sealed record CancionDto(
    long Id, long ProyectoId, string Titulo, int? DuracionSeg,
    IReadOnlyList<string> Partes, bool RequiereEnsayo, DateTimeOffset CreadoEn);
