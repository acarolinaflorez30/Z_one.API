using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ZOne.Api.Domain;
using ZOne.Api.Options;

namespace ZOne.Api.Security;

public static class TokenHash
{
    /// <summary>SHA-256 en hexadecimal. Los tokens opacos solo se guardan hasheados.</summary>
    public static string Of(string valor) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(valor)));
}

public interface ITokenService
{
    (string Token, DateTimeOffset ExpiraEn) CrearAccessToken(Usuario usuario, DateTimeOffset ahora);

    /// <summary>Token aleatorio de 256 bits para refresh y recuperación. Devuelve el valor en claro y su hash.</summary>
    (string Plano, string Hash) CrearTokenOpaco();
}

public sealed class TokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTimeOffset ExpiraEn) CrearAccessToken(Usuario usuario, DateTimeOffset ahora)
    {
        var jwt = options.Value;
        var expira = ahora.AddMinutes(jwt.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            NotBefore = ahora.UtcDateTime,
            IssuedAt = ahora.UtcDateTime,
            Expires = expira.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = usuario.Id.ToString(),
                ["jti"] = Guid.NewGuid().ToString("N"),
                ["email"] = usuario.Email,
                ["name"] = usuario.Nombre,
                ["role"] = usuario.Rol.ToString().ToLowerInvariant(),
                ["pwd_change"] = usuario.DebeCambiarPassword ? "1" : "0"
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                SecurityAlgorithms.HmacSha256)
        };

        return (_handler.CreateToken(descriptor), expira);
    }

    public (string Plano, string Hash) CrearTokenOpaco()
    {
        var plano = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (plano, TokenHash.Of(plano));
    }
}
