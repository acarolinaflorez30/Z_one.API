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
    ICatalogoService catalogo) : ControllerBase
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

    [HttpGet("salas")]
    public Task<IReadOnlyList<SalaDto>> Salas(CancellationToken ct) =>
        catalogo.ListarSalasAsync(soloActivas: true, ct);

    [HttpGet("servicios")]
    public Task<IReadOnlyList<ServicioDto>> Servicios(CancellationToken ct) =>
        catalogo.ListarServiciosAsync(soloActivos: true, ct);
}
