using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Options;

namespace ZOne.Api.Services;

/// <summary>
/// Envía en segundo plano los correos de la cola (tabla correos_pendientes). Reintenta con espera creciente
/// (1, 5, 15, 60 y 240 min) y respeta el tope diario del proveedor. Un fallo de correo nunca afecta a una reserva.
/// </summary>
public sealed class CorreoWorker(
    IServiceScopeFactory scopes,
    IOptions<CorreoOptions> opciones,
    IOptions<AuthOptions> auth,
    TimeProvider time,
    ILogger<CorreoWorker> logger) : BackgroundService
{
    private static readonly int[] EsperaMinutos = [1, 5, 15, 60, 240];

    private sealed record Resultado(int Procesados, bool TopeDiario);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = opciones.Value;
        logger.LogInformation("Envío de correos activo (cuenta {Usuario}, tope {Max} por día).", o.Usuario, o.MaxPorDia);

        try { await Task.Delay(TimeSpan.FromSeconds(5), stop); }
        catch (OperationCanceledException) { return; }

        var ultimaLimpieza = DateTimeOffset.MinValue;
        while (!stop.IsCancellationRequested)
        {
            var espera = TimeSpan.FromSeconds(Math.Max(1, o.SegundosEntreLotes));
            try
            {
                var r = await ProcesarLoteAsync(stop);
                if (r.TopeDiario) espera = TimeSpan.FromMinutes(10);
                else if (r.Procesados > 0) espera = TimeSpan.FromSeconds(1);   // quedan más: seguir sin esperar

                if (time.GetUtcNow() - ultimaLimpieza > TimeSpan.FromHours(6))
                {
                    await LimpiarAsync(stop);
                    ultimaLimpieza = time.GetUtcNow();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falló un ciclo del envío de correos.");
                espera = TimeSpan.FromSeconds(30);
            }

            try { await Task.Delay(espera, stop); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<Resultado> ProcesarLoteAsync(CancellationToken ct)
    {
        var o = opciones.Value;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZOneDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var ahora = time.GetUtcNow();

        // Tope diario del proveedor: lo que sobre queda pendiente para después
        var enviados24 = await db.CorreosPendientes.CountAsync(c => c.Estado == "enviado" && c.EnviadoEn > ahora.AddHours(-24), ct);
        var cupo = o.MaxPorDia - enviados24;
        if (cupo <= 0)
        {
            logger.LogWarning("Se alcanzó el tope de {Max} correos en 24 h; los demás esperan.", o.MaxPorDia);
            return new Resultado(0, true);
        }
        var tamano = Math.Clamp(Math.Min(o.LoteTamano, cupo), 1, 200);

        // 1) Reservar el lote (SKIP LOCKED: dos instancias de la API no toman el mismo correo).
        //    Si el proceso se cae a mitad, el lote vuelve a intentarse a los 15 minutos (cabe el peor caso: 20 correos × 30 s).
        List<CorreoPendiente> lote;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            lote = await db.CorreosPendientes.FromSqlInterpolated($"""
                select * from public.correos_pendientes
                where estado = 'pendiente' and proximo_intento <= {ahora}
                order by id
                limit {tamano}
                for update skip locked
                """).ToListAsync(ct);
            foreach (var c in lote) c.ProximoIntento = ahora.AddMinutes(15);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        if (lote.Count == 0) return new Resultado(0, false);

        var ids = lote.Select(c => c.UsuarioId).Distinct().ToList();
        var usuarios = await db.Usuarios.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Email, u.Nombre, u.Activo, u.RecibirCorreos })
            .ToDictionaryAsync(u => u.Id, ct);

        var urlApp = auth.Value.FrontendBaseUrl.TrimEnd('/');

        // 2) Enviar uno por uno y guardar el resultado de cada uno
        var procesados = 0;
        var problemaDelProveedor = false;
        foreach (var c in lote)
        {
            ct.ThrowIfCancellationRequested();

            if (problemaDelProveedor)
            {
                // El proveedor está fallando (clave mala, cuota agotada, sin conexión): lo que queda del lote espera, sin gastar intentos
                c.ProximoIntento = time.GetUtcNow().AddMinutes(30);
                await db.SaveChangesAsync(ct);
                continue;
            }

            if (!usuarios.TryGetValue(c.UsuarioId, out var u) || !u.Activo || !u.RecibirCorreos || string.IsNullOrWhiteSpace(u.Email))
            {
                c.Estado = "omitido";
                await db.SaveChangesAsync(ct);
                continue;
            }

            try
            {
                var (texto, html) = CorreoPlantilla.Armar(u.Nombre, c.Asunto, c.Mensaje, urlApp);
                await sender.EnviarHtmlAsync(u.Email, $"Z-ONE · {c.Asunto}", texto, html, ct);
                c.Estado = "enviado";
                c.EnviadoEn = time.GetUtcNow();
                c.UltimoError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Solo se guarda el tipo de error (el mensaje puede traer direcciones o respuestas del servidor)
                var tipo = ex is SmtpException se ? $"{ex.GetType().Name} {se.StatusCode}" : ex.GetType().Name;
                c.UltimoError = Recortar(tipo, 300);

                if (EsFalloDelDestinatario(ex))
                {
                    c.Intentos++;
                    if (c.Intentos >= o.MaxIntentos) c.Estado = "fallido";
                    else c.ProximoIntento = time.GetUtcNow().AddMinutes(EsperaMinutos[Math.Min(c.Intentos - 1, EsperaMinutos.Length - 1)]);
                    logger.LogWarning("No se pudo enviar el correo {Id} (intento {Intentos}): {Tipo}", c.Id, c.Intentos, tipo);
                }
                else
                {
                    // Contraseña errónea, cuota diaria agotada, sin conexión...: no es culpa de este correo; se reintenta más tarde
                    problemaDelProveedor = true;
                    c.ProximoIntento = time.GetUtcNow().AddMinutes(30);
                    logger.LogError("El proveedor de correo está fallando ({Tipo}). Se pausa el envío 30 minutos; revisa Correo:Usuario y Correo:Clave.", tipo);
                }
            }
            await db.SaveChangesAsync(ct);
            procesados++;
        }

        return new Resultado(procesados, problemaDelProveedor);
    }

    /// <summary>true = el problema es de ese destinatario (buzón inexistente o lleno, dirección inválida); false = del proveedor o la red.</summary>
    private static bool EsFalloDelDestinatario(Exception ex) => ex switch
    {
        SmtpFailedRecipientException => true,
        FormatException => true,
        SmtpException se => se.StatusCode is SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.MailboxNameNotAllowed
            or SmtpStatusCode.UserNotLocalTryAlternatePath or SmtpStatusCode.ExceededStorageAllocation,
        _ => false
    };

    private async Task LimpiarAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZOneDbContext>();
        var ahora = time.GetUtcNow();

        // Lo ya procesado se conserva RetencionDias (mínimo 2, para que el tope diario siempre vea las últimas 24 h)
        var limite = ahora.AddDays(-Math.Max(2, opciones.Value.RetencionDias));
        var n = await db.CorreosPendientes
            .Where(c => c.Estado != "pendiente" && (c.EnviadoEn ?? c.CreadoEn) < limite)
            .ExecuteDeleteAsync(ct);
        if (n > 0) logger.LogInformation("Se limpiaron {N} correos antiguos de la cola.", n);

        // Un aviso de hace más de 7 días ya no sirve: no se envía de golpe si el correo estuvo apagado o caído
        var viejos = ahora.AddDays(-7);
        await db.CorreosPendientes
            .Where(c => c.Estado == "pendiente" && c.CreadoEn < viejos)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Estado, "omitido"), ct);
    }

    private static string Recortar(string texto, int max) => Recorte.Seguro(texto, max);
}
