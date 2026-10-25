using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ZOne.Api.Options;

namespace ZOne.Api.Security;

public readonly record struct VerificacionPassword(bool Ok, bool NecesitaRehash);

public interface IPasswordHasher
{
    string Hash(string password);
    VerificacionPassword Verify(string password, string stored);

    /// <summary>Hash válido de una contraseña inexistente. Se verifica cuando el usuario no existe para igualar tiempos de respuesta.</summary>
    string HashFicticio { get; }
}

/// <summary>PBKDF2-HMAC-SHA256 con sal aleatoria. Formato: pbkdf2-sha256$iteraciones$sal$hash (Base64).</summary>
public sealed class Pbkdf2PasswordHasher(IOptions<AuthOptions> options) : IPasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const int SaltSize = 16;
    private const int KeySize = 32;

    private readonly int _iterations = options.Value.Pbkdf2Iterations;
    private readonly Lazy<string> _ficticio = new(() => Crear("contrasena-ficticia-no-valida", options.Value.Pbkdf2Iterations));

    public string HashFicticio => _ficticio.Value;

    public string Hash(string password) => Crear(password, _iterations);

    private static string Crear(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public VerificacionPassword Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out var iterations) || iterations < 1)
            return new VerificacionPassword(false, false);

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return new VerificacionPassword(false, false);
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        var ok = CryptographicOperations.FixedTimeEquals(actual, expected);
        return new VerificacionPassword(ok, ok && iterations < _iterations);
    }
}
