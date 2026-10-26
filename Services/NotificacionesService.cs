using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;

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
    /// Quien ejecuta la acción nunca se avisa a sí mismo. Requiere el proyecto con Cliente y Participantes.Colaborador cargados.
    /// </summary>
    Task NotificarProyectoAsync(Proyecto proyecto, Guid actorUsuarioId, Destinatarios para,
        string tipo, string titulo, string mensaje, long? sesionId, CancellationToken ct);

    Task<PaginaDto<NotificacionDto>> ListarAsync(Guid usuarioId, bool soloNoLeidas, int pagina, int tamano, CancellationToken ct);
    Task<int> ContarNoLeidasAsync(Guid usuarioId, CancellationToken ct);
    Task MarcarLeidaAsync(long id, Guid usuarioId, CancellationToken ct);
    Task MarcarTodasLeidasAsync(Guid usuarioId, CancellationToken ct);
}

/// <summary>
/// Avisos dentro del sistema. El envío por correo se agregará cuando exista un proveedor
/// (hoy IEmailSender solo escribe en el log).
/// </summary>
public sealed class NotificacionesService(ZOneDbContext db, TimeProvider time) : INotificacionesService
{
    public async Task NotificarProyectoAsync(Proyecto proyecto, Guid actorUsuarioId, Destinatarios para,
        string tipo, string titulo, string mensaje, long? sesionId, CancellationToken ct)
    {
        var usuarios = new HashSet<Guid>();

        if (para.HasFlag(Destinatarios.Cliente))
            usuarios.Add(proyecto.Cliente.UsuarioId);

        if (para.HasFlag(Destinatarios.Colaboradores))
            foreach (var x in proyecto.Participantes.Where(x => x.Estado == EstadoParticipacion.Aceptada))
                usuarios.Add(x.Colaborador.UsuarioId);

        if (para.HasFlag(Destinatarios.Administradores))
        {
            var admins = await db.Usuarios.AsNoTracking()
                .Where(u => u.Rol == RolUsuario.Administrador && u.Activo)
                .Select(u => u.Id)
                .ToListAsync(ct);
            foreach (var id in admins) usuarios.Add(id);
        }

        usuarios.Remove(actorUsuarioId);

        var ahora = time.GetUtcNow();
        foreach (var usuarioId in usuarios)
        {
            db.Notificaciones.Add(new Notificacion
            {
                UsuarioId = usuarioId,
                Tipo = tipo,
                Titulo = titulo,
                Mensaje = mensaje,
                ProyectoId = proyecto.Id,
                SesionId = sesionId,
                CreadoEn = ahora
            });
        }
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
