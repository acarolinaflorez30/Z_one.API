using System.Net;
using System.Net.Mail;
using System.Text;

namespace ZOne.Api.Services;

/// <summary>
/// Envía correos por SMTP de Gmail. El envío se ejecuta en segundo plano para no
/// retrasar la petición que lo origina; ante cualquier fallo solo se registra el
/// tipo de excepción, sin la clave, el cuerpo ni el enlace.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private const string Host = "smtp.gmail.com";
    private const int Puerto = 587;
    private static readonly TimeSpan TiempoLimite = TimeSpan.FromSeconds(15);

    private readonly ILogger<SmtpEmailSender> _logger;
    private readonly string _usuario;
    private readonly string _clave;
    private readonly string _remitente;

    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger)
    {
        _logger = logger;
        _usuario = configuration["Smtp:Usuario"] ?? "";
        _clave = configuration["Smtp:Clave"] ?? "";

        var remitente = configuration["Smtp:Remitente"];
        _remitente = string.IsNullOrWhiteSpace(remitente) ? _usuario : remitente;
    }

    public Task EnviarAsync(string para, string asunto, string cuerpo, CancellationToken ct)
    {
        // Se ignora el ct de la petición a propósito: al responder, ese token se
        // cancelaría y abortaría el envío que corre en segundo plano.
        _ = Task.Run(async () =>
        {
            try
            {
                using var limite = new CancellationTokenSource(TiempoLimite);

                using var mensaje = new MailMessage
                {
                    From = new MailAddress(_remitente),
                    Subject = asunto,
                    SubjectEncoding = Encoding.UTF8,
                    Body = cuerpo,
                    BodyEncoding = Encoding.UTF8,
                    IsBodyHtml = false
                };
                mensaje.To.Add(para);

                using var cliente = new SmtpClient(Host, Puerto)
                {
                    EnableSsl = true,
                    UseDefaultCredentials = false,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Credentials = new NetworkCredential(_usuario, _clave),
                    Timeout = (int)TiempoLimite.TotalMilliseconds
                };

                await cliente.SendMailAsync(mensaje, limite.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Sin clave, sin cuerpo y sin enlace: solo un mensaje genérico y el tipo.
                _logger.LogError("No se pudo enviar el correo de recuperación ({Tipo}).", ex.GetType().Name);
            }
        });

        return Task.CompletedTask;
    }
}
