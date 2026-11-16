using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>Datos que ve cualquier visitante, sin iniciar sesión.</summary>
[ApiController]
[Route("publico")]
[AllowAnonymous]
[EnableRateLimiting("publico")]
public sealed class PublicoController(
    IPublicoService publico,
    IColaboradoresService colaboradores,
    ICatalogoService catalogo,
    ISalasService salas) : ControllerBase
{
    [HttpGet("colaboradores/destacados")]
    public Task<IReadOnlyList<ColaboradorPublicoDto>> Destacados(
        [FromQuery] string? perfil, [FromQuery] int limite = 12, CancellationToken ct = default) =>
        publico.DestacadosAsync(perfil, limite, ct);

    [HttpGet("colaboradores/{id:guid}")]
    public Task<ColaboradorPublicoDto> Colaborador(Guid id, CancellationToken ct) =>
        publico.ObtenerAsync(id, ct);

    [HttpGet("colaboradores/{id:guid}/foto")]
    public async Task<IActionResult> Foto(Guid id, CancellationToken ct)
    {
        var foto = await colaboradores.ObtenerFotoAsync(id, soloPublico: true, actor: null, ct);
        return this.ArchivoConCache(foto, "public, max-age=3600");
    }

    /// <summary>Salas publicadas, con las 4 etiquetas rápidas (capacidad, canales, DAW principal y precio por hora). q busca por nombre.</summary>
    [HttpGet("salas")]
    public Task<IReadOnlyList<SalaPublicaDto>> Salas([FromQuery] string? q, CancellationToken ct) =>
        salas.ListarPublicasAsync(q, ct);

    /// <summary>Detalle de una sala publicada: etiquetas, ficha técnica completa, fotos y muestras de audio o video.</summary>
    [HttpGet("salas/{id:long}")]
    public Task<SalaPublicaDetalleDto> Sala(long id, CancellationToken ct) =>
        salas.ObtenerPublicaAsync(id, ct);

    [HttpGet("salas/{id:long}/medios/{medioId:long}")]
    public async Task<IActionResult> FotoSala(long id, long medioId, CancellationToken ct)
    {
        var foto = await salas.ObtenerFotoAsync(id, medioId, soloPublico: true, ct);
        return this.ArchivoConCache(foto, "public, max-age=3600");
    }

    [HttpGet("servicios")]
    public Task<IReadOnlyList<ServicioDto>> Servicios(CancellationToken ct) =>
        catalogo.ListarServiciosAsync(soloActivos: true, ct);
}
