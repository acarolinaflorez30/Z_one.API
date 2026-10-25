using ZOne.Api.Infrastructure;

namespace ZOne.Api.Security;

public readonly record struct ImagenInfo(string Mime, string Extension, int Ancho, int Alto);

/// <summary>
/// Valida fotos por su contenido real (firma binaria y cabecera), sin confiar en la extensión
/// ni en el Content-Type que declara el navegador. Permite JPG, PNG y WebP de hasta 1.5 MB.
/// </summary>
public static class ImagenValidator
{
    public const int MaxBytes = 1_572_864;   // 1.5 MB
    public const int MaxLado = 6000;         // píxeles por lado

    private static readonly byte[] PngFirma = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Lee el flujo sin pasar del límite, aunque el cliente mienta sobre el tamaño.</summary>
    public static async Task<byte[]> LeerAcotadoAsync(Stream origen, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int leidos;
        while ((leidos = await origen.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + leidos > MaxBytes) throw Error("La foto no puede superar 1.5 MB.");
            ms.Write(buffer, 0, leidos);
        }
        return ms.ToArray();
    }

    public static ImagenInfo Validar(byte[] b)
    {
        if (b.Length == 0) throw Error("El archivo está vacío.");
        if (b.Length > MaxBytes) throw Error("La foto no puede superar 1.5 MB.");

        string mime, extension;
        (int Ancho, int Alto)? dimensiones;

        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            mime = "image/jpeg"; extension = ".jpg"; dimensiones = DimensionesJpeg(b);
        }
        else if (Coincide(b, 0, PngFirma))
        {
            mime = "image/png"; extension = ".png"; dimensiones = DimensionesPng(b);
        }
        else if (b.Length >= 30 && Coincide(b, 0, "RIFF"u8) && Coincide(b, 8, "WEBP"u8))
        {
            mime = "image/webp"; extension = ".webp"; dimensiones = DimensionesWebp(b);
        }
        else
        {
            throw Error("Formato no permitido. Usa una imagen JPG, PNG o WebP.");
        }

        if (dimensiones is not { } d || d.Ancho <= 0 || d.Alto <= 0)
            throw Error("La imagen está dañada o no es válida.");
        if (d.Ancho > MaxLado || d.Alto > MaxLado)
            throw Error($"La imagen no puede superar {MaxLado} píxeles por lado.");

        return new ImagenInfo(mime, extension, d.Ancho, d.Alto);
    }

    private static ApiException Error(string mensaje) => ApiException.Validacion("archivo", mensaje);

    private static bool Coincide(byte[] b, int desde, ReadOnlySpan<byte> esperado) =>
        b.Length >= desde + esperado.Length && b.AsSpan(desde, esperado.Length).SequenceEqual(esperado);

    private static (int Ancho, int Alto)? DimensionesPng(byte[] b)
    {
        if (b.Length < 24 || !Coincide(b, 12, "IHDR"u8)) return null;
        var ancho = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
        var alto = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
        return (ancho, alto);
    }

    private static (int Ancho, int Alto)? DimensionesJpeg(byte[] b)
    {
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marcador = b[i + 1];
            if (marcador == 0xFF) { i++; continue; }
            if (marcador == 0xD8 || marcador == 0x01 || (marcador >= 0xD0 && marcador <= 0xD7)) { i += 2; continue; }

            var largo = (b[i + 2] << 8) | b[i + 3];
            if (largo < 2) return null;

            // SOF0..SOF15 (excepto DHT=C4, JPG=C8, DAC=CC) llevan el alto y el ancho.
            if (marcador >= 0xC0 && marcador <= 0xCF && marcador != 0xC4 && marcador != 0xC8 && marcador != 0xCC)
            {
                var alto = (b[i + 5] << 8) | b[i + 6];
                var ancho = (b[i + 7] << 8) | b[i + 8];
                return (ancho, alto);
            }
            i += 2 + largo;
        }
        return null;
    }

    private static (int Ancho, int Alto)? DimensionesWebp(byte[] b)
    {
        if (Coincide(b, 12, "VP8X"u8))
        {
            var ancho = 1 + (b[24] | (b[25] << 8) | (b[26] << 16));
            var alto = 1 + (b[27] | (b[28] << 8) | (b[29] << 16));
            return (ancho, alto);
        }
        if (Coincide(b, 12, "VP8L"u8) && b[20] == 0x2F)
        {
            var ancho = 1 + (((b[22] & 0x3F) << 8) | b[21]);
            var alto = 1 + (((b[24] & 0x0F) << 10) | (b[23] << 2) | ((b[22] & 0xC0) >> 6));
            return (ancho, alto);
        }
        if (Coincide(b, 12, "VP8 "u8) && b[23] == 0x9D && b[24] == 0x01 && b[25] == 0x2A)
        {
            var ancho = (b[26] | (b[27] << 8)) & 0x3FFF;
            var alto = (b[28] | (b[29] << 8)) & 0x3FFF;
            return (ancho, alto);
        }
        return null;
    }
}
