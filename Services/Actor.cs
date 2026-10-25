using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using ZOne.Api.Infrastructure;

namespace ZOne.Api.Services;

/// <summary>Quién hace la petición. La API decide con esto; nunca con datos que envía el cliente.</summary>
public sealed record Actor(Guid UsuarioId, bool EsAdmin);

public static class ActorExtensions
{
    public static Actor ComoActor(this ClaimsPrincipal user) =>
        new(Guid.TryParse(user.FindFirst("sub")?.Value, out var id) ? id : throw ApiException.NoAutenticado(),
            user.IsInRole("administrador"));

    public static ContextoPeticion Contexto(this ControllerBase c) =>
        new(c.HttpContext.Connection.RemoteIpAddress, c.Request.Headers.UserAgent.ToString());

    /// <summary>Respuesta de archivo con ETag, para que el navegador no vuelva a descargar la misma foto.</summary>
    public static IActionResult ArchivoConCache(this ControllerBase c, FotoArchivo foto, string cacheControl)
    {
        var etag = $"\"{foto.Version}\"";
        c.Response.Headers.CacheControl = cacheControl;
        c.Response.Headers.ETag = etag;
        if (c.Request.Headers.IfNoneMatch == etag) return c.StatusCode(StatusCodes.Status304NotModified);
        return c.File(foto.Datos, foto.Mime);
    }
}
