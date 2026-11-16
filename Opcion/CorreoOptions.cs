namespace ZOne.Api.Options;

/// <summary>
/// Envío de avisos por correo. Con Gmail: cuenta dedicada con verificación en 2 pasos y una "contraseña de aplicación".
/// La Clave va en variable de entorno (Correo__Clave) o user-secrets, nunca en el repositorio.
/// </summary>
public sealed class CorreoOptions
{
    public const string Section = "Correo";

    /// <summary>false = no se envía nada (los avisos siguen apareciendo dentro de la web).</summary>
    public bool Habilitado { get; set; }

    public string Host { get; set; } = "smtp.gmail.com";
    public int Puerto { get; set; } = 587;
    public string Usuario { get; set; } = "";
    public string Clave { get; set; } = "";

    /// <summary>Con Gmail debe ser la propia cuenta (o un alias suyo). Vacío = igual a Usuario.</summary>
    public string Remitente { get; set; } = "";
    public string NombreRemitente { get; set; } = "Z-ONE";

    /// <summary>Tope de correos cada 24 h. Gmail permite unos 500; se deja margen.</summary>
    public int MaxPorDia { get; set; } = 400;

    public int MaxIntentos { get; set; } = 5;
    public int LoteTamano { get; set; } = 20;
    public int SegundosEntreLotes { get; set; } = 15;

    /// <summary>Días que se conservan los correos ya procesados en la cola.</summary>
    public int RetencionDias { get; set; } = 30;
}
