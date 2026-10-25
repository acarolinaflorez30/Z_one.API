using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Security;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

[ApiController]
[Route("colaboradores")]
[Authorize]
public sealed class ColaboradoresController(IColaboradoresService servicio) : ControllerBase
{
    private Actor Actor => User.ComoActor();

    [HttpGet]
    [Authorize(Roles = "administrador")]
    public Task<PaginaDto<ColaboradorResumenDto>> Listar(
        [FromQuery] string? perfil, [FromQuery] string? vinculacion, [FromQuery] string? q,
        [FromQuery] int pagina = 1, [FromQuery] int tamano = 25, CancellationToken ct = default) =>
        servicio.ListarAsync(perfil, vinculacion, q, pagina, tamano, ct);

    /// <summary>El perfil del colaborador que inició sesión.</summary>
    [HttpGet("me")]
    public async Task<ColaboradorDto> Me(CancellationToken ct)
    {
        var actor = Actor;
        var id = await servicio.IdPropioAsync(actor.UsuarioId, ct);
        return await servicio.ObtenerAsync(id, actor, ct);
    }

    [HttpGet("{id:guid}")]
    public Task<ColaboradorDto> Obtener(Guid id, CancellationToken ct) =>
        servicio.ObtenerAsync(id, Actor, ct);

    [HttpPatch("{id:guid}")]
    public Task<ColaboradorDto> Actualizar(Guid id, ActualizarColaboradorRequest request, CancellationToken ct) =>
        servicio.ActualizarAsync(id, request, Actor, this.Contexto(), ct);

    [HttpPut("{id:guid}/vinculacion")]
    [Authorize(Roles = "administrador")]
    public async Task<IActionResult> Vinculacion(Guid id, VinculacionRequest request, CancellationToken ct)
    {
        await servicio.CambiarVinculacionAsync(id, request.Activo, Actor, this.Contexto(), ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/detalle")]
    public Task<IReadOnlyDictionary<string, string?>> Detalle(Guid id, DetalleRequest request, CancellationToken ct) =>
        servicio.ActualizarDetalleAsync(id, request.Campos, Actor, this.Contexto(), ct);

    // ── Portafolio ──

    [HttpPost("{id:guid}/portafolio")]
    public async Task<ActionResult<PortafolioDto>> AgregarPortafolio(Guid id, PortafolioRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await servicio.AgregarPortafolioAsync(id, request, Actor, ct));

    [HttpPut("{id:guid}/portafolio/{itemId:long}")]
    public Task<PortafolioDto> EditarPortafolio(Guid id, long itemId, PortafolioRequest request, CancellationToken ct) =>
        servicio.EditarPortafolioAsync(id, itemId, request, Actor, ct);

    [HttpDelete("{id:guid}/portafolio/{itemId:long}")]
    public async Task<IActionResult> EliminarPortafolio(Guid id, long itemId, CancellationToken ct)
    {
        await servicio.EliminarPortafolioAsync(id, itemId, Actor, ct);
        return NoContent();
    }

    // ── Notas internas (solo administrador) ──

    [HttpGet("{id:guid}/notas")]
    [Authorize(Roles = "administrador")]
    public Task<IReadOnlyList<NotaDto>> Notas(Guid id, CancellationToken ct) =>
        servicio.ListarNotasAsync(id, Actor, ct);

    [HttpPost("{id:guid}/notas")]
    [Authorize(Roles = "administrador")]
    public async Task<ActionResult<NotaDto>> AgregarNota(Guid id, NotaRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await servicio.AgregarNotaAsync(id, request.Comentario, Actor, ct));

    // ── Foto: JPG, PNG o WebP, máximo 1.5 MB, validada por su contenido real ──

    [HttpPut("{id:guid}/foto")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<ActionResult<FotoResponse>> SubirFoto(Guid id, IFormFile archivo, CancellationToken ct)
    {
        if (archivo is null || archivo.Length == 0)
            throw ApiException.Validacion("archivo", "Envía una imagen en el campo 'archivo'.");
        if (archivo.Length > ImagenValidator.MaxBytes)
            throw ApiException.Validacion("archivo", "La foto no puede superar 1.5 MB.");

        await using var flujo = archivo.OpenReadStream();
        var datos = await ImagenValidator.LeerAcotadoAsync(flujo, ct);
        var url = await servicio.SubirFotoAsync(id, datos, Actor, this.Contexto(), ct);
        return new FotoResponse(url);
    }

    [HttpDelete("{id:guid}/foto")]
    public async Task<IActionResult> EliminarFoto(Guid id, CancellationToken ct)
    {
        await servicio.EliminarFotoAsync(id, Actor, this.Contexto(), ct);
        return NoContent();
    }

    /// <summary>La foto vista por el administrador o el propio colaborador (aunque su perfil aún no sea público).</summary>
    [HttpGet("{id:guid}/foto")]
    public async Task<IActionResult> Foto(Guid id, CancellationToken ct)
    {
        var foto = await servicio.ObtenerFotoAsync(id, soloPublico: false, Actor, ct);
        return this.ArchivoConCache(foto, "private, max-age=300");
    }
}
