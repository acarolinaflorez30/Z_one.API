using System.Globalization;
using ZOne.Api.Domain;

namespace ZOne.Api.Services;

/// <summary>Textos de los avisos: siempre dicen de qué sesión se trata, con sala, fecha y duración.</summary>
public static class Textos
{
    public static string Local(DateTimeOffset d, int offsetHoras) =>
        d.ToOffset(TimeSpan.FromHours(offsetHoras)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Requiere Sala cargada y, si es de proyecto, Proyecto con Servicio.</summary>
    public static string Sesion(Sesion s, int offsetHoras)
    {
        var desc = ReglasProyecto.Descripcion(s);
        var contexto = s.Proyecto is { } p
            ? $" de \"{p.Titulo}\" ({ReglasProyecto.TextoTipo(p.Tipo)}, servicio {p.Servicio.Nombre})"
            : "";
        var horas = ReglasProyecto.Horas(s).ToString("0.##", CultureInfo.InvariantCulture);
        return $"{desc}{contexto}. Sala {s.Sala.Nombre}, {Local(s.Inicio, offsetHoras)}, {horas} h.";
    }
}
