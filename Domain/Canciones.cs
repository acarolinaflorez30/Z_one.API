namespace ZOne.Api.Domain;

/// <summary>Una canción que pertenece a un proyecto.</summary>
public sealed class Cancion
{
    public long Id { get; set; }
    public long ProyectoId { get; set; }
    public string Titulo { get; set; } = "";
    public int? DuracionSeg { get; set; }
    public List<string> Partes { get; set; } = [];
    public bool RequiereEnsayo { get; set; }
    public DateTimeOffset CreadoEn { get; set; }

    public Proyecto Proyecto { get; set; } = null!;
}
