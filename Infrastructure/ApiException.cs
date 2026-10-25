namespace ZOne.Api.Infrastructure;

/// <summary>Error de negocio o de validación que se devuelve al cliente con un código estable.</summary>
public sealed class ApiException(int status, string code, string message, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;

    public static ApiException Validacion(string campo, string mensaje) =>
        new(400, "validacion", "Hay datos que corregir.", new Dictionary<string, string[]> { [campo] = [mensaje] });

    public static ApiException Validacion(string campo, IEnumerable<string> mensajes) =>
        new(400, "validacion", "Hay datos que corregir.", new Dictionary<string, string[]> { [campo] = mensajes.ToArray() });

    public static ApiException NoAutenticado(string mensaje = "Debes iniciar sesión.") => new(401, "no_autenticado", mensaje);
    public static ApiException CredencialesInvalidas() =>
        new(401, "credenciales_invalidas", "Correo o contraseña incorrectos, o la cuenta está bloqueada temporalmente.");
    public static ApiException Prohibido(string mensaje = "No tienes permiso para esta acción.") => new(403, "prohibido", mensaje);
    public static ApiException NoEncontrado(string mensaje) => new(404, "no_encontrado", mensaje);
    public static ApiException Conflicto(string code, string mensaje) => new(409, code, mensaje);
}
