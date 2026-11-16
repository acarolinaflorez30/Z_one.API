using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.Extensions.Options;
using ZOne.Api.Options;

namespace ZOne.Api.Services;

/// <summary>Envío por SMTP (pensado para una cuenta Gmail dedicada con contraseña de aplicación; sirve con cualquier SMTP).</summary>
public sealed class SmtpEmailSender(IOptions<CorreoOptions> opciones, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    /// <summary>
    /// Recuperación de contraseña: se envía en segundo plano. Así la petición responde igual de rápido exista o no la cuenta
    /// (el tiempo de respuesta no delata si el correo está registrado) y un fallo del SMTP no se propaga.
    /// </summary>
    public Task EnviarAsync(string para, string asunto, string cuerpo, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try { await EnviarInternoAsync(para, asunto, cuerpo, null, CancellationToken.None); }
            catch (Exception ex) { logger.LogError("No se pudo enviar el correo '{Asunto}': {Tipo}", asunto, ex.GetType().Name); }
        });
        return Task.CompletedTask;
    }

    public Task EnviarHtmlAsync(string para, string asunto, string texto, string html, CancellationToken ct) =>
        EnviarInternoAsync(para, asunto, texto, html, ct);

    private async Task EnviarInternoAsync(string para, string asunto, string texto, string? html, CancellationToken ct)
    {
        var o = opciones.Value;
        var remitente = string.IsNullOrWhiteSpace(o.Remitente) ? o.Usuario : o.Remitente;

        using var mensaje = new MailMessage
        {
            From = new MailAddress(remitente, o.NombreRemitente, Encoding.UTF8),
            // Sin saltos de línea: evita inyectar cabeceras desde el asunto
            Subject = asunto.Replace('\r', ' ').Replace('\n', ' ').Trim(),
            SubjectEncoding = Encoding.UTF8,
            Body = texto,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false
        };
        mensaje.To.Add(new MailAddress(para));
        if (html is not null)
            mensaje.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html));

        using var smtp = new SmtpClient(o.Host, o.Puerto)
        {
            EnableSsl = true,   // STARTTLS en el puerto 587
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            // Google muestra la contraseña de aplicación en grupos de 4 con espacios: se ignoran
            Credentials = new NetworkCredential(o.Usuario, o.Clave.Replace(" ", "")),
            Timeout = 20_000
        };
        // Tope duro por mensaje (30 s): una conexión colgada no bloquea la cola
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TimeSpan.FromSeconds(30));
        await smtp.SendMailAsync(mensaje).WaitAsync(limite.Token);
    }
}
