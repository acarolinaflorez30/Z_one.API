using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;

namespace ZOne.Api.Services;

public interface IPublicoService
{
    Task<IReadOnlyList<ColaboradorPublicoDto>> DestacadosAsync(string? perfil, int limite, CancellationToken ct);
    Task<ColaboradorPublicoDto> ObtenerAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// Datos que ve cualquier visitante. Las consultas proyectan solo columnas públicas: el nombre real,
/// la fecha de nacimiento y el correo ni siquiera se leen de la base.
/// </summary>
public sealed class PublicoService(ZOneDbContext db) : IPublicoService
{
    private static IQueryable<Colaborador> Publicables(IQueryable<Colaborador> q) =>
        q.Where(c => c.Vinculacion == EstadoVinculacion.Activo
                     && c.VisiblePublico
                     && c.Usuario.Activo
                     && c.NombreArtistico != null && c.NombreArtistico != "");

    public async Task<IReadOnlyList<ColaboradorPublicoDto>> DestacadosAsync(string? perfil, int limite, CancellationToken ct)
    {
        limite = Math.Clamp(limite, 1, 50);
        var consulta = Publicables(db.Colaboradores.AsNoTracking());

        if (!string.IsNullOrWhiteSpace(perfil))
        {
            if (!Convenciones.TryParseEnum<PerfilColaborador>(perfil, out var perfilFiltro))
                throw ApiException.Validacion("perfil", "Perfil no válido.");
            consulta = consulta.Where(c => c.Perfil == perfilFiltro);
        }

        var filas = await consulta
            .OrderBy(c => c.NombreArtistico).ThenBy(c => c.Id)
            .Take(limite)
            .Select(c => new
            {
                c.Id,
                c.NombreArtistico,
                c.Perfil,
                c.Especialidad,
                c.CiudadOrigen,
                c.Biografia,
                c.Trayectoria,
                c.Productoras,
                c.Foto,
                c.Disponibilidad
            })
            .ToListAsync(ct);

        return filas.Select(f => new ColaboradorPublicoDto(
            f.Id, f.NombreArtistico!, Convenciones.Api(f.Perfil), f.Especialidad, f.CiudadOrigen,
            f.Biografia, f.Trayectoria, f.Productoras, Convenciones.FotoUrl(f.Id, f.Foto, publica: true),
            Convenciones.Api(f.Disponibilidad), null, null)).ToList();
    }

    public async Task<ColaboradorPublicoDto> ObtenerAsync(Guid id, CancellationToken ct)
    {
        var f = await Publicables(db.Colaboradores.AsNoTracking())
            .Where(c => c.Id == id)
            .Select(c => new
            {
                c.Id,
                c.NombreArtistico,
                c.Perfil,
                c.Especialidad,
                c.CiudadOrigen,
                c.Biografia,
                c.Trayectoria,
                c.Productoras,
                c.Foto,
                c.Disponibilidad
            })
            .FirstOrDefaultAsync(ct)
            ?? throw ApiException.NoEncontrado("No existe ese perfil.");

        var detalle = await DetallePerfil.LeerAsync(db, f.Id, f.Perfil, ct);
        var portafolio = await db.ColaboradorProyectos.AsNoTracking()
            .Where(p => p.ColaboradorId == f.Id)
            .OrderByDescending(p => p.Anio).ThenByDescending(p => p.Id)
            .ToListAsync(ct);

        return new ColaboradorPublicoDto(
            f.Id, f.NombreArtistico!, Convenciones.Api(f.Perfil), f.Especialidad, f.CiudadOrigen,
            f.Biografia, f.Trayectoria, f.Productoras, Convenciones.FotoUrl(f.Id, f.Foto, publica: true),
            Convenciones.Api(f.Disponibilidad), detalle, portafolio.Select(Mapeos.Portafolio).ToList());
    }
}
