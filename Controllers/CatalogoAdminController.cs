using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>Administración del catálogo. Servicios y historial de precios. Las salas se administran en /admin/salas.</summary>
[ApiController]
[Route("admin")]
[Authorize(Roles = "administrador")]
public sealed class CatalogoAdminController(ICatalogoService catalogo) : ControllerBase
{
    private Guid AdminId => User.ComoActor().UsuarioId;

    [HttpGet("servicios")]
    public Task<IReadOnlyList<ServicioDto>> Servicios(CancellationToken ct) =>
        catalogo.ListarServiciosAsync(soloActivos: false, ct);

    /// <summary>Todos los cambios de tarifa de salas y colaboradores, del más reciente al más antiguo.</summary>
    [HttpGet("precios/historial")]
    public Task<PaginaDto<HistorialPrecioDto>> HistorialPrecios(
        [FromQuery] string? tabla, [FromQuery] long? registroId, [FromQuery] Guid? registroUuid,
        [FromQuery] int pagina = 1, [FromQuery] int tamano = 50, CancellationToken ct = default) =>
        catalogo.ListarHistorialPreciosAsync(tabla, registroId, registroUuid, pagina, tamano, ct);

    [HttpPut("servicios/{id:long}")]
    public Task<ServicioDto> ActualizarServicio(long id, ServicioRequest request, CancellationToken ct) =>
        catalogo.ActualizarServicioAsync(id, request, AdminId, this.Contexto(), ct);
}
