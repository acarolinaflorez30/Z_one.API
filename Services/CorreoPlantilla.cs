using System.Net;
using System.Text;

namespace ZOne.Api.Services;

public static class Recorte
{
    /// <summary>Recorta a máximo max caracteres sin partir un emoji (par sustituto) por la mitad.</summary>
    public static string Seguro(string texto, int max)
    {
        if (texto.Length <= max) return texto;
        var fin = max;
        if (fin > 0 && char.IsHighSurrogate(texto[fin - 1])) fin--;
        return texto[..fin];
    }
}

/// <summary>Arma el correo de un aviso: versión de texto y versión HTML sencilla. Todo lo que viene de los datos se escapa.</summary>
public static class CorreoPlantilla
{
    public static (string Texto, string Html) Armar(string nombre, string titulo, string mensaje, string urlApp)
    {
        var saludo = string.IsNullOrWhiteSpace(nombre) ? "Hola" : $"Hola {nombre.Trim()}";
        const string pie = "Recibes este correo porque tienes una cuenta en Z-ONE. Puedes desactivar los avisos por correo desde tus ajustes de notificaciones.";

        var texto = $"{saludo},\n\n{titulo}\n\n{mensaje}\n\nAbrir Z-ONE: {urlApp}\n\n--\n{pie}";

        static string h(string valor) => WebUtility.HtmlEncode(valor);
        var cuerpo = new StringBuilder();
        cuerpo.Append("<!doctype html><html lang=\"es\"><body style=\"margin:0;background:#f4f4f5;font-family:Arial,Helvetica,sans-serif;color:#18181b\">");
        cuerpo.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr><td align=\"center\" style=\"padding:24px 12px\">");
        cuerpo.Append("<table role=\"presentation\" width=\"560\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;width:100%;background:#ffffff;border-radius:8px\">");
        cuerpo.Append("<tr><td style=\"padding:20px 28px;background:#18181b;color:#ffffff;font-size:18px;font-weight:bold;border-radius:8px 8px 0 0\">Z-ONE</td></tr>");
        cuerpo.Append("<tr><td style=\"padding:28px\">");
        cuerpo.Append($"<p style=\"margin:0 0 12px\">{h(saludo)},</p>");
        cuerpo.Append($"<h1 style=\"margin:0 0 16px;font-size:20px\">{h(titulo)}</h1>");
        cuerpo.Append($"<p style=\"margin:0 0 24px;line-height:1.5\">{h(mensaje).Replace("\n", "<br>")}</p>");
        cuerpo.Append($"<a href=\"{h(urlApp)}\" style=\"display:inline-block;background:#18181b;color:#ffffff;text-decoration:none;padding:12px 20px;border-radius:6px\">Abrir Z-ONE</a>");
        cuerpo.Append("</td></tr>");
        cuerpo.Append($"<tr><td style=\"padding:16px 28px;font-size:12px;color:#71717a;border-top:1px solid #e4e4e7\">{h(pie)}</td></tr>");
        cuerpo.Append("</table></td></tr></table></body></html>");

        return (texto, cuerpo.ToString());
    }
}
