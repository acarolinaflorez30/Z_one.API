using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZOne.Api.Services;

namespace ZOne.Api.Controllers;

/// <summary>Datos del landing que ve cualquier visitante. Fuente única para la landing pública.</summary>
[ApiController]
[Route("publico")]
[AllowAnonymous]
[EnableRateLimiting("publico")]
public sealed class LandingController(ILandingService landing) : ControllerBase
{
    [HttpGet("landing")]
    public async Task<IActionResult> Landing(CancellationToken ct) =>
        Ok(await landing.ObtenerLandingAsync(ct));
}
