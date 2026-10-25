using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>Administración del catálogo. Aquí se cargan los precios, que define otra persona del proyecto.</summary>
[ApiController]
[Route("admin")]
[Authorize(Roles = "administrador")]
public sealed class CatalogoAdminController(ICatalogoService catalogo) : ControllerBase
{
    private Guid AdminId => User.ComoActor().UsuarioId;

    [HttpGet("salas")]
    public Task<IReadOnlyList<SalaDto>> Salas(CancellationToken ct) =>
        catalogo.ListarSalasAsync(soloActivas: false, ct);

    [HttpPost("salas")]
    public async Task<ActionResult<SalaDto>> CrearSala(SalaRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await catalogo.CrearSalaAsync(request, AdminId, this.Contexto(), ct));

    [HttpPut("salas/{id:long}")]
    public Task<SalaDto> ActualizarSala(long id, SalaRequest request, CancellationToken ct) =>
        catalogo.ActualizarSalaAsync(id, request, AdminId, this.Contexto(), ct);

    [HttpGet("servicios")]
    public Task<IReadOnlyList<ServicioDto>> Servicios(CancellationToken ct) =>
        catalogo.ListarServiciosAsync(soloActivos: false, ct);

    [HttpPut("servicios/{id:long}")]
    public Task<ServicioDto> ActualizarServicio(long id, ServicioRequest request, CancellationToken ct) =>
        catalogo.ActualizarServicioAsync(id, request, AdminId, this.Contexto(), ct);
}
