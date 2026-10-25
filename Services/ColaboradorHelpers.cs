using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;

namespace ZOne.Api.Services;

public static class Convenciones
{
    public static string ASnake(string nombre)
    {
        var sb = new StringBuilder(nombre.Length + 4);
        for (var i = 0; i < nombre.Length; i++)
        {
            var c = nombre[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>IngenieroSonido -> ingeniero_sonido</summary>
    public static string Api<TEnum>(TEnum valor) where TEnum : struct, Enum => ASnake(valor.ToString());

    /// <summary>Acepta ingeniero_sonido, IngenieroSonido o ingenierosonido; rechaza números.</summary>
    public static bool TryParseEnum<TEnum>(string? texto, out TEnum valor) where TEnum : struct, Enum
    {
        valor = default;
        if (string.IsNullOrWhiteSpace(texto)) return false;
        var limpio = texto.Trim();
        if (char.IsDigit(limpio[0])) return false;
        return Enum.TryParse(limpio.Replace("_", ""), true, out valor) && Enum.IsDefined(valor);
    }

    public static string? FotoUrl(Guid colaboradorId, string? clave, bool publica) =>
        string.IsNullOrEmpty(clave)
            ? null
            : $"{(publica ? "/publico" : "")}/colaboradores/{colaboradorId}/foto?v={Path.GetFileNameWithoutExtension(clave)}";

    public static string MimeDe(string clave) => Path.GetExtension(clave).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    /// <summary>Solo los colaboradores activos, visibles y con nombre artístico pueden mostrarse al público.</summary>
    public static bool EsPublico(Colaborador c) =>
        c.Vinculacion == EstadoVinculacion.Activo && c.VisiblePublico && c.Usuario.Activo &&
        !string.IsNullOrWhiteSpace(c.NombreArtistico);
}

public static class Mapeos
{
    public static PortafolioDto Portafolio(ColaboradorProyecto p) =>
        new(p.Id, p.Titulo, p.Descripcion, p.Productora, p.Rol, p.Anio, Convenciones.Api(p.Estado));
}

/// <summary>
/// Cada perfil tiene su propia tabla de detalle. Los nombres de tabla y columna salen de esta lista fija
/// (nunca del cliente) y los valores siempre van como parámetros.
/// </summary>
public static class DetallePerfil
{
    public static readonly IReadOnlyDictionary<PerfilColaborador, (string Tabla, string[] Campos)> Mapa =
        new Dictionary<PerfilColaborador, (string Tabla, string[] Campos)>
        {
            [PerfilColaborador.Artista] = ("colaborador_artistas", new[] { "generoMusical", "disquera", "instrumentos", "redes" }),
            [PerfilColaborador.Productor] = ("colaborador_productores", new[] { "generosProduce", "dawPrincipal", "creditos" }),
            [PerfilColaborador.IngenieroSonido] = ("colaborador_ingenieros_sonido", new[] { "areasGrabacion", "certificaciones", "equipos" }),
            [PerfilColaborador.IngenieroMezcla] = ("colaborador_ingenieros_mezcla", new[] { "dawPrincipal", "plugins", "creditos" }),
            [PerfilColaborador.IngenieroMaster] = ("colaborador_ingenieros_master", new[] { "estudio", "equipos", "creditos" }),
            [PerfilColaborador.Otro] = ("colaborador_otros", new[] { "area", "descripcion" })
        };

    public static async Task<Dictionary<string, string?>> LeerAsync(
        ZOneDbContext db, Guid colaboradorId, PerfilColaborador perfil, CancellationToken ct)
    {
        var (tabla, campos) = Mapa[perfil];
        var resultado = campos.ToDictionary(c => c, _ => (string?)null);

        var sql = $"select to_jsonb(t)::text as \"Value\" from public.{tabla} t where t.colaborador_id = {{0}}";
        var json = await db.Database.SqlQueryRaw<string>(sql, colaboradorId).FirstOrDefaultAsync(ct);
        if (json is null) return resultado;

        using var doc = JsonDocument.Parse(json);
        foreach (var campo in campos)
        {
            if (doc.RootElement.TryGetProperty(Convenciones.ASnake(campo), out var elemento) &&
                elemento.ValueKind == JsonValueKind.String)
                resultado[campo] = elemento.GetString();
        }
        return resultado;
    }

    public static Task GuardarAsync(
        ZOneDbContext db, Guid colaboradorId, PerfilColaborador perfil,
        IReadOnlyDictionary<string, string?> valores, CancellationToken ct)
    {
        var (tabla, campos) = Mapa[perfil];
        var columnas = campos.Select(Convenciones.ASnake).ToArray();

        var lista = string.Join(", ", columnas);
        var marcadores = string.Join(", ", columnas.Select((_, i) => $"{{{i + 1}}}::text"));
        var actualizar = string.Join(", ", columnas.Select(c => $"{c} = excluded.{c}"));
        var sql = $"insert into public.{tabla} (colaborador_id, {lista}) values ({{0}}, {marcadores}) " +
                  $"on conflict (colaborador_id) do update set {actualizar}";

        var parametros = new List<object> { colaboradorId };
        foreach (var campo in campos)
            parametros.Add(valores.TryGetValue(campo, out var v) && v is not null ? (object)v : DBNull.Value);

        return db.Database.ExecuteSqlRawAsync(sql, parametros, ct);
    }
}
