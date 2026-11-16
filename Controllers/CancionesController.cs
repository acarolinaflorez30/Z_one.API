using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// Canciones de un proyecto. Lectura: administrador, cliente dueño y colaboradores invitados.
/// Escritura: solo el cliente dueño del proyecto o el administrador.
[ApiController]
[Authorize]
public sealed class CancionesController(ICancionesService canciones) : ControllerBase
{
    private Actor Actor => User.ComoActor();

    [HttpGet("proyectos/{id:long}/canciones")]
    public Task<List<CancionDto>> Listar(long id, CancellationToken ct) =>
        canciones.ListarPorProyectoAsync(id, Actor, ct);

    [HttpPost("proyectos/{id:long}/canciones")]
    public async Task<ActionResult<CancionDto>> Crear(long id, CrearCancionRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await canciones.CrearAsync(id, request, Actor, this.Contexto(), ct));

    [HttpPatch("canciones/{id:long}")]
    public Task<CancionDto> Actualizar(long id, ActualizarCancionRequest request, CancellationToken ct) =>
        canciones.ActualizarAsync(id, request, Actor, this.Contexto(), ct);

    [HttpDelete("canciones/{id:long}")]
    public async Task<IActionResult> Eliminar(long id, CancellationToken ct)
    {
        await canciones.EliminarAsync(id, Actor, this.Contexto(), ct);
        return NoContent();
    }
}
