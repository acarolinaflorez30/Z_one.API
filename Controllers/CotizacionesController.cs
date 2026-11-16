using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Dtos;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>Cuánto costaría una sesión (sala + colaboradores) antes de pedirla. Solo con sesión iniciada: los precios de colaboradores no son públicos.</summary>
[ApiController]
[Route("cotizaciones")]
[Authorize(Roles = "cliente,administrador")]
public sealed class CotizacionesController(IPreciosService precios) : ControllerBase
{
    [HttpPost]
    public Task<CotizacionDto> Cotizar(CotizacionRequest request, CancellationToken ct) =>
        precios.CotizarAsync(request, User.ComoActor().EsAdmin, ct);
}
