using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Security;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>
/// Salas de grabación de la productora: solo el administrador las crea, lista, edita, publica u oculta.
/// "activo" = en servicio (false: mantenimiento). "publicada" = visible en la web. Ninguno depende de que la sala esté ocupada.
/// </summary>
[ApiController]
[Route("admin/salas")]
[Authorize(Roles = "administrador")]
public sealed class SalasAdminController(ISalasService salas) : ControllerBase
{
    private Guid AdminId => User.ComoActor().UsuarioId;

    /// <summary>Listado con búsqueda por nombre (q) y filtros opcionales por estado.</summary>
    [HttpGet]
    public Task<PaginaDto<SalaResumenDto>> Listar(
        [FromQuery] string? q, [FromQuery] bool? activo, [FromQuery] bool? publicada,
        [FromQuery] int pagina = 1, [FromQuery] int tamano = 25, CancellationToken ct = default) =>
        salas.ListarAsync(q, activo, publicada, pagina, tamano, ct);

    /// <summary>Valores de los selectores del formulario (tipos de uso, tratamiento acústico, consola, sugerencias).</summary>
    [HttpGet("opciones")]
    public SalaOpcionesDto Opciones() => salas.Opciones();

    [HttpGet("{id:long}")]
    public Task<SalaAdminDto> Obtener(long id, CancellationToken ct) => salas.ObtenerAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<SalaAdminDto>> Crear(SalaRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await salas.CrearAsync(request, AdminId, this.Contexto(), ct));

    /// <summary>Reemplaza todos los datos de la ficha (envía la ficha completa).</summary>
    [HttpPut("{id:long}")]
    public Task<SalaAdminDto> Actualizar(long id, SalaRequest request, CancellationToken ct) =>
        salas.ActualizarAsync(id, request, AdminId, this.Contexto(), ct);

    /// <summary>Publicar u ocultar la sala, o ponerla en mantenimiento, sin tocar el resto de la ficha.</summary>
    [HttpPut("{id:long}/estado")]
    public Task<SalaAdminDto> CambiarEstado(long id, EstadoSalaRequest request, CancellationToken ct) =>
        salas.CambiarEstadoAsync(id, request, AdminId, this.Contexto(), ct);

    // ── Galería ──

    /// <summary>Foto en alta resolución: JPG, PNG o WebP, hasta 5 MB, validada por su contenido real. Campo 'archivo' (y 'titulo' opcional).</summary>
    [HttpPost("{id:long}/fotos")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<ActionResult<MedioDto>> SubirFoto(long id, IFormFile archivo, [FromForm] string? titulo, CancellationToken ct)
    {
        if (archivo is null || archivo.Length == 0)
            throw ApiException.Validacion("archivo", "Envía una imagen en el campo 'archivo'.");
        if (archivo.Length > ImagenValidator.MaxBytesGaleria)
            throw ApiException.Validacion("archivo", "La foto no puede superar 5 MB.");

        await using var flujo = archivo.OpenReadStream();
        var datos = await ImagenValidator.LeerAcotadoAsync(flujo, ct, ImagenValidator.MaxBytesGaleria);
        return StatusCode(StatusCodes.Status201Created,
            await salas.SubirFotoAsync(id, datos, titulo, AdminId, this.Contexto(), ct));
    }

    /// <summary>Enlace https a un audio o video de muestra grabado en la sala.</summary>
    [HttpPost("{id:long}/enlaces")]
    public async Task<ActionResult<MedioDto>> AgregarEnlace(long id, EnlaceMedioRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await salas.AgregarEnlaceAsync(id, request, AdminId, this.Contexto(), ct));

    /// <summary>Cambia el título o el orden de una foto o enlace (la primera foto es la portada).</summary>
    [HttpPut("{id:long}/medios/{medioId:long}")]
    public Task<MedioDto> ActualizarMedio(long id, long medioId, ActualizarMedioRequest request, CancellationToken ct) =>
        salas.ActualizarMedioAsync(id, medioId, request, AdminId, this.Contexto(), ct);

    [HttpDelete("{id:long}/medios/{medioId:long}")]
    public async Task<IActionResult> EliminarMedio(long id, long medioId, CancellationToken ct)
    {
        await salas.EliminarMedioAsync(id, medioId, AdminId, this.Contexto(), ct);
        return NoContent();
    }

    /// <summary>La foto vista por el administrador (aunque la sala aún no esté publicada).</summary>
    [HttpGet("{id:long}/medios/{medioId:long}/archivo")]
    public async Task<IActionResult> Archivo(long id, long medioId, CancellationToken ct)
    {
        var foto = await salas.ObtenerFotoAsync(id, medioId, soloPublico: false, ct);
        return this.ArchivoConCache(foto, "private, max-age=300");
    }
}
