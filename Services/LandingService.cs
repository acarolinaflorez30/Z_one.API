using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;

namespace ZOne.Api.Services;

public sealed record SalaLandingDto(long Id, string Nombre, string Descripcion, string? ImagenUrl, decimal PrecioHora);
public sealed record ServicioLandingDto(long Id, int Orden, string Titulo, string Descripcion, string Icono, string Color);
public sealed record PasoLandingDto(long Id, int Orden, string Titulo, string Descripcion, string Icono);
public sealed record TestimonioLandingDto(long Id, string Nombre, string Rol, string Texto, string? ImagenUrl, int Calificacion);
public sealed record PreguntaLandingDto(long Id, string Pregunta, string Respuesta);
public sealed record MetricaLandingDto(long Id, int Valor, string Sufijo, string Etiqueta);
public sealed record ArtistaLandingDto(Guid Id, string Nombre, string? Perfil, string? Foto);
public sealed record ConfigSitioDto(
    [property: JsonPropertyName("hero_imagen")] string? HeroImagen,
    [property: JsonPropertyName("horario_estudio")] string HorarioEstudio);

public interface ILandingService
{
    Task<LandingPayloadDto> ObtenerLandingAsync(CancellationToken ct = default);
}

public sealed record LandingPayloadDto(
    IReadOnlyList<SalaLandingDto> Salas,
    IReadOnlyList<ServicioLandingDto> Servicios,
    IReadOnlyList<PasoLandingDto> Pasos,
    IReadOnlyList<TestimonioLandingDto> Testimonios,
    IReadOnlyList<PreguntaLandingDto> Preguntas,
    IReadOnlyList<MetricaLandingDto> Metricas,
    IReadOnlyList<ArtistaLandingDto> Artistas,
    int TotalServicios,
    ConfigSitioDto Config);

public sealed class LandingService(ZOneDbContext db) : ILandingService
{
    public async Task<LandingPayloadDto> ObtenerLandingAsync(CancellationToken ct = default)
    {
        var salas = await db.Database.SqlQueryRaw<SalaLandingDto>(
            @"SELECT id, nombre, COALESCE(descripcion, '') AS descripcion, imagen_url AS ""ImagenUrl"", precio_hora AS ""PrecioHora""
              FROM salas
              WHERE activo = true
              ORDER BY id")
            .ToListAsync(ct);

        var servicios = await db.Database.SqlQueryRaw<ServicioLandingDto>(
            @"SELECT id, orden, titulo, COALESCE(descripcion, '') AS descripcion, icono, color
              FROM servicios_landing
              WHERE activo = true
              ORDER BY orden")
            .ToListAsync(ct);

        var pasos = await db.Database.SqlQueryRaw<PasoLandingDto>(
            @"SELECT id, orden, titulo, COALESCE(descripcion, '') AS descripcion, icono
              FROM pasos_como_funciona
              WHERE activo = true
              ORDER BY orden")
            .ToListAsync(ct);

        var testimonios = await db.Database.SqlQueryRaw<TestimonioLandingDto>(
            @"SELECT id, nombre, COALESCE(rol, '') AS rol, COALESCE(texto, '') AS texto, imagen_url AS ""ImagenUrl"", calificacion
              FROM testimonios
              WHERE activo = true
              ORDER BY orden")
            .ToListAsync(ct);

        var preguntas = await db.Database.SqlQueryRaw<PreguntaLandingDto>(
            @"SELECT id, COALESCE(pregunta, '') AS pregunta, COALESCE(respuesta, '') AS respuesta
              FROM preguntas_frecuentes
              WHERE activo = true
              ORDER BY orden")
            .ToListAsync(ct);

        var metricas = await db.Database.SqlQueryRaw<MetricaLandingDto>(
            @"SELECT id, valor, COALESCE(sufijo, '') AS sufijo, COALESCE(etiqueta, '') AS etiqueta
              FROM metricas_sitio
              WHERE activo = true
              ORDER BY orden")
            .ToListAsync(ct);

        var artistas = await db.Database.SqlQueryRaw<ArtistaLandingDto>(
            @"SELECT c.id AS ""Id"",
                     COALESCE(c.nombre_artistico, c.nombre_real, 'Artista') AS ""Nombre"",
                     c.perfil::text AS ""Perfil"",
                     CASE WHEN c.foto IS NULL OR c.foto = '' THEN NULL
                          ELSE '/publico/colaboradores/' || c.id::text || '/foto?v=' ||
                               regexp_replace(regexp_replace(c.foto, '^.*/', ''), '\.[^.]+$', '')
                     END AS ""Foto""
              FROM colaboradores c
              WHERE c.visible_publico = true
                AND c.perfil = 'artista'
                AND c.desvinculado_en IS NULL
              ORDER BY COALESCE(c.nombre_artistico, c.nombre_real)")
            .ToListAsync(ct);

        var config = await db.Database.SqlQueryRaw<ConfigSitioDto>(
            @"SELECT MAX(valor) FILTER (WHERE clave = 'hero_imagen') AS ""HeroImagen"",
                     COALESCE(MAX(valor) FILTER (WHERE clave = 'horario_estudio'), 'Lun–Dom') AS ""HorarioEstudio""
              FROM configuracion_sitio")
            .FirstOrDefaultAsync(ct)
            ?? new ConfigSitioDto(null, "Lun–Dom");

        return new LandingPayloadDto(
            Salas: salas,
            Servicios: servicios,
            Pasos: pasos,
            Testimonios: testimonios,
            Preguntas: preguntas,
            Metricas: metricas,
            Artistas: artistas,
            TotalServicios: servicios.Count,
            Config: config
        );
    }
}
