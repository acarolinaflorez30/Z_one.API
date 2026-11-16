using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using Microsoft.Extensions.Options;
using ZOne.Api.Infrastructure;
using ZOne.Api.Options;

namespace ZOne.Api.Services;

[Flags]
public enum Destinatarios
{
    Ninguno = 0,
    Cliente = 1,
    /// <summary>Los colaboradores que aceptaron participar en el proyecto.</summary>
    Colaboradores = 2,
    Administradores = 4,
    Todos = Cliente | Colaboradores | Administradores
}

public interface INotificacionesService
{
    /// <summary>
    /// Agrega los avisos al contexto; se guardan junto con los demás cambios de quien llama (en la misma transacción).
    /// Quien ejecuta la acción nunca se avisa a sí mismo. Requiere el proyecto con Cliente cargado.
    /// Colaboradores = los que aceptaron alguna sesión del proyecto.
    /// </summary>
    Task NotificarProyectoAsync(Proyecto proyecto, Guid actorUsuarioId, Destinatarios para,
        string tipo, string titulo, string mensaje, long? sesionId, CancellationToken ct);

    /// <summary>Avisa a personas concretas (por ejemplo, a los colaboradores invitados a una sesión). No se avisa al actor.</summary>
    void NotificarUsuarios(IEnumerable<Guid> usuarioIds, Guid actorUsuarioId,
        string tipo, string titulo, string mensaje, long? proyectoId, long? sesionId);

    Task NotificarAdministradoresAsync(Guid actorUsuarioId,
        string tipo, string titulo, string mensaje, long? proyectoId, long? sesionId, CancellationToken ct);

    Task<PaginaDto<NotificacionDto>> ListarAsync(Guid usuarioId, bool soloNoLeidas, int pagina, int tamano, CancellationToken ct);
    Task<int> ContarNoLeidasAsync(Guid usuarioId, CancellationToken ct);
    Task MarcarLeidaAsync(long id, Guid usuarioId, CancellationToken ct);
    Task MarcarTodasLeidasAsync(Guid usuarioId, CancellationToken ct);

    Task<PreferenciasNotificacionDto> ObtenerPreferenciasAsync(Guid usuarioId, CancellationToken ct);
    Task<PreferenciasNotificacionDto> GuardarPreferenciasAsync(Guid usuarioId, bool correo, CancellationToken ct);
}

/// <summary>
/// Avisos dentro del sistema. Si el envío de correos está activado, cada aviso deja además un correo en la cola
/// (en la misma transacción); CorreoWorker lo envía después y reintenta si el proveedor falla.
/// </summary>
public sealed class NotificacionesService(ZOneDbContext db, TimeProvider time, IOptions<CorreoOptions> correo) : INotificacionesService
{
    public async Task NotificarProyectoAsync(Proyecto proyecto, Guid actorUsuarioId, Destinatarios para,
        string tipo, string titulo, string mensaje, long? sesionId, CancellationToken ct)
    {
        var usuarios = new HashSet<Guid>();

        if (para.HasFlag(Destinatarios.Cliente))
            usuarios.Add(proyecto.Cliente.UsuarioId);

        if (para.HasFlag(Destinatarios.Colaboradores))
        {
            var aceptados = await db.Aceptaciones.AsNoTracking()
                .Where(a => a.Sesion.ProyectoId == proyecto.Id && a.Rol != null && a.Estado == EstadoParticipacion.Aceptada)
                .Select(a => a.UsuarioId)
                .Distinct()
                .ToListAsync(ct);
            foreach (var id in aceptados) usuarios.Add(id);
        }

        if (para.HasFlag(Destinatarios.Administradores))
            foreach (var id in await IdsAdministradoresAsync(ct)) usuarios.Add(id);

        NotificarUsuarios(usuarios, actorUsuarioId, tipo, titulo, mensaje, proyecto.Id, sesionId);
    }

    public async Task NotificarAdministradoresAsync(Guid actorUsuarioId,
        string tipo, string titulo, string mensaje, long? proyectoId, long? sesionId, CancellationToken ct) =>
        NotificarUsuarios(await IdsAdministradoresAsync(ct), actorUsuarioId, tipo, titulo, mensaje, proyectoId, sesionId);

    private Task<List<Guid>> IdsAdministradoresAsync(CancellationToken ct) =>
        db.Usuarios.AsNoTracking()
            .Where(u => u.Rol == RolUsuario.Administrador && u.Activo)
            .Select(u => u.Id)
            .ToListAsync(ct);

    public void NotificarUsuarios(IEnumerable<Guid> usuarioIds, Guid actorUsuarioId,
        string tipo, string titulo, string mensaje, long? proyectoId, long? sesionId)
    {
        var ahora = time.GetUtcNow();
        foreach (var usuarioId in usuarioIds.Distinct().Where(id => id != actorUsuarioId))
        {
            db.Notificaciones.Add(new Notificacion
            {
                UsuarioId = usuarioId,
                Tipo = tipo,
                Titulo = titulo,
                Mensaje = mensaje,
                ProyectoId = proyectoId,
                SesionId = sesionId,
                CreadoEn = ahora
            });

            if (correo.Value.Habilitado)
            {
                var asunto = (string.IsNullOrWhiteSpace(titulo) ? "Aviso" : titulo).Replace('\r', ' ').Replace('\n', ' ').Trim();
                db.CorreosPendientes.Add(new CorreoPendiente
                {
                    UsuarioId = usuarioId,
                    Tipo = tipo,
                    Asunto = Recorte.Seguro(asunto, 200),
                    Mensaje = Recorte.Seguro(mensaje, 4000),
                    ProyectoId = proyectoId,
                    SesionId = sesionId,
                    ProximoIntento = ahora,
                    CreadoEn = ahora
                });
            }
        }
    }

    public async Task<PreferenciasNotificacionDto> ObtenerPreferenciasAsync(Guid usuarioId, CancellationToken ct)
    {
        var recibe = await db.Usuarios.AsNoTracking().Where(u => u.Id == usuarioId).Select(u => (bool?)u.RecibirCorreos).FirstOrDefaultAsync(ct)
                     ?? throw ApiException.NoEncontrado("No existe el usuario.");
        return new PreferenciasNotificacionDto(recibe, correo.Value.Habilitado);
    }

    public async Task<PreferenciasNotificacionDto> GuardarPreferenciasAsync(Guid usuarioId, bool recibir, CancellationToken ct)
    {
        var n = await db.Usuarios.Where(u => u.Id == usuarioId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.RecibirCorreos, recibir), ct);
        if (n == 0) throw ApiException.NoEncontrado("No existe el usuario.");
        return new PreferenciasNotificacionDto(recibir, correo.Value.Habilitado);
    }

    public async Task<PaginaDto<NotificacionDto>> ListarAsync(Guid usuarioId, bool soloNoLeidas, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);

        var consulta = db.Notificaciones.AsNoTracking().Where(n => n.UsuarioId == usuarioId);
        if (soloNoLeidas) consulta = consulta.Where(n => n.LeidaEn == null);

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .OrderByDescending(n => n.CreadoEn).ThenByDescending(n => n.Id)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .ToListAsync(ct);

        var items = filas.Select(n => new NotificacionDto(
            n.Id, n.Tipo, n.Titulo, n.Mensaje, n.ProyectoId, n.SesionId, n.LeidaEn != null, n.CreadoEn)).ToList();
        return new PaginaDto<NotificacionDto>(items, total, pagina, tamano);
    }

    public Task<int> ContarNoLeidasAsync(Guid usuarioId, CancellationToken ct) =>
        db.Notificaciones.CountAsync(n => n.UsuarioId == usuarioId && n.LeidaEn == null, ct);

    public async Task MarcarLeidaAsync(long id, Guid usuarioId, CancellationToken ct)
    {
        var existe = await db.Notificaciones.AnyAsync(n => n.Id == id && n.UsuarioId == usuarioId, ct);
        if (!existe) throw ApiException.NoEncontrado("No existe esa notificación.");

        var ahora = time.GetUtcNow();
        await db.Notificaciones
            .Where(n => n.Id == id && n.UsuarioId == usuarioId && n.LeidaEn == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.LeidaEn, (DateTimeOffset?)ahora), ct);
    }

    public Task MarcarTodasLeidasAsync(Guid usuarioId, CancellationToken ct)
    {
        var ahora = time.GetUtcNow();
        return db.Notificaciones
            .Where(n => n.UsuarioId == usuarioId && n.LeidaEn == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.LeidaEn, (DateTimeOffset?)ahora), ct);
    }
}
