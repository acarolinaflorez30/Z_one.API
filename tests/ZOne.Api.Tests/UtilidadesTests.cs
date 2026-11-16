using ZOne.Api.Domain;
using ZOne.Api.Infrastructure;
using ZOne.Api.Security;
using ZOne.Api.Services;

namespace ZOne.Api.Tests;

public class ConvencionesTests
{
    [Theory]
    [InlineData(EtapaProyecto.EdicionProduccion, "edicion_produccion")]
    [InlineData(EtapaProyecto.Produccion, "produccion")]
    [InlineData(EtapaProyecto.MezclaMasterizacion, "mezcla_masterizacion")]
    public void Los_enums_se_exponen_en_snake_case(EtapaProyecto valor, string esperado) =>
        Assert.Equal(esperado, Convenciones.Api(valor));

    [Theory]
    [InlineData("ingeniero_sonido")]
    [InlineData("IngenieroSonido")]
    [InlineData("  ingenierosonido ")]
    public void Se_aceptan_varias_escrituras_del_mismo_valor(string texto)
    {
        Assert.True(Convenciones.TryParseEnum<PerfilColaborador>(texto, out var v));
        Assert.Equal(PerfilColaborador.IngenieroSonido, v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2")]
    [InlineData("inexistente")]
    public void Valores_invalidos_o_numeros_se_rechazan(string? texto) =>
        Assert.False(Convenciones.TryParseEnum<PerfilColaborador>(texto, out _));

    [Fact]
    public void El_tipo_de_archivo_sale_de_la_extension()
    {
        Assert.Equal("image/png", Convenciones.MimeDe("a/b.PNG"));
        Assert.Equal("image/webp", Convenciones.MimeDe("x.webp"));
        Assert.Equal("image/jpeg", Convenciones.MimeDe("x.jpg"));
    }
}

public class EntradaTests
{
    [Fact]
    public void Texto_recorta_espacios_y_valida_el_largo()
    {
        Assert.Equal("Hola", Entrada.Texto("  Hola  ", "campo", 2, 10));
        var ex = Assert.Throws<ApiException>(() => Entrada.Texto("a", "nombre", 2, 10));
        Assert.Equal(400, ex.Status);
        Assert.True(ex.Errors!.ContainsKey("nombre"));
        Assert.Throws<ApiException>(() => Entrada.Texto(null, "nombre", 1, 10));
        Assert.Throws<ApiException>(() => Entrada.Texto(new string('x', 11), "nombre", 1, 10));
    }

    [Fact]
    public void Texto_opcional_vacio_es_null_y_respeta_el_maximo()
    {
        Assert.Null(Entrada.TextoOpcional("   ", "c", 5));
        Assert.Null(Entrada.TextoOpcional(null, "c", 5));
        Assert.Equal("abc", Entrada.TextoOpcional(" abc ", "c", 5));
        Assert.Throws<ApiException>(() => Entrada.TextoOpcional("abcdef", "c", 5));
    }

    [Theory]
    [InlineData("  Ana@Correo.COM ", "ana@correo.com")]
    public void El_correo_se_normaliza(string entrada, string esperado) => Assert.Equal(esperado, Entrada.Email(entrada));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sin-arroba")]
    [InlineData("a@b")]
    [InlineData("Ana <ana@correo.com>")]
    public void Un_correo_invalido_se_rechaza(string? entrada)
    {
        Assert.Null(Entrada.EmailSuave(entrada));
        Assert.Throws<ApiException>(() => Entrada.Email(entrada));
    }
}

public class TextosTests
{
    [Fact]
    public void La_hora_se_muestra_en_hora_local()
    {
        var utc = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal("15/01/2026 15:00", Textos.Local(utc, -5));
    }

    [Fact]
    public void El_aviso_de_un_alquiler_dice_sala_fecha_y_duracion()
    {
        var s = Datos.NuevaSesion(tipo: TipoSesion.Alquiler, horas: 2);
        Assert.Equal("Alquiler de sala. Sala Sala A, 15/01/2026 15:00, 2 h.", Textos.Sesion(s, -5));
    }

    [Fact]
    public void El_aviso_de_una_sesion_de_proyecto_dice_proyecto_tipo_y_servicio()
    {
        var p = Datos.NuevoProyecto();
        var s = Datos.NuevaSesion(numero: 2, etapa: EtapaProyecto.Grabacion, proyecto: p, horas: 3);
        Assert.Equal("Sesión 2 de 4 de \"Mi canción\" (canción completa, servicio Producción musical). Sala Sala A, 15/01/2026 15:00, 3 h.",
            Textos.Sesion(s, -5));
    }

    [Fact]
    public void Las_horas_con_decimales_se_muestran_sin_ceros_sobrantes()
    {
        var s = Datos.NuevaSesion(tipo: TipoSesion.Alquiler, horas: 0);
        s.Fin = s.Inicio.AddMinutes(90);
        Assert.Contains(", 1.5 h.", Textos.Sesion(s, -5));
    }
}

public class CorreoTests
{
    [Fact]
    public void Recorte_no_toca_lo_corto_y_corta_lo_largo()
    {
        Assert.Equal("abc", Recorte.Seguro("abc", 3));
        Assert.Equal("ab", Recorte.Seguro("abcdef", 2));
    }

    [Fact]
    public void Recorte_no_parte_un_emoji_por_la_mitad()
    {
        var texto = "ab😀cd";   // el emoji ocupa 2 posiciones (2 y 3)
        var recortado = Recorte.Seguro(texto, 3);
        Assert.Equal("ab", recortado);
        Assert.False(char.IsHighSurrogate(recortado[^1]));
    }

    [Fact]
    public void El_correo_escapa_lo_que_viene_de_los_datos()
    {
        var (texto, html) = CorreoPlantilla.Armar("<b>Ana</b>", "Hola <script>alert(1)</script>", "Linea 1\nLinea <img src=x onerror=1>", "https://app.test/?a=1&b=\"2\"");
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<b>Ana</b>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Linea 1<br>Linea", html);
        Assert.Contains("&amp;b=&quot;2&quot;", html);
        Assert.Contains("<b>Ana</b>", texto);   // la versión de texto no es HTML: va tal cual
    }

    [Fact]
    public void El_saludo_usa_el_nombre_o_un_hola_generico()
    {
        Assert.StartsWith("Hola Ana,", CorreoPlantilla.Armar("  Ana ", "t", "m", "https://x.test").Texto);
        Assert.StartsWith("Hola,", CorreoPlantilla.Armar("", "t", "m", "https://x.test").Texto);
    }

    [Fact]
    public void El_correo_incluye_el_enlace_para_abrir_la_app_y_como_apagar_avisos()
    {
        var (texto, html) = CorreoPlantilla.Armar("Ana", "Titulo", "Mensaje", "https://app.test");
        Assert.Contains("https://app.test", texto);
        Assert.Contains("href=\"https://app.test\"", html);
        Assert.Contains("desactivar los avisos por correo", texto);
    }
}

public class ImagenValidatorTests
{
    private static byte[] Png(int ancho, int alto)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(b, 0);
        "IHDR"u8.ToArray().CopyTo(b, 12);
        b[16] = (byte)(ancho >> 24); b[17] = (byte)(ancho >> 16); b[18] = (byte)(ancho >> 8); b[19] = (byte)ancho;
        b[20] = (byte)(alto >> 24); b[21] = (byte)(alto >> 16); b[22] = (byte)(alto >> 8); b[23] = (byte)alto;
        return b;
    }

    private static byte[] Jpeg(int ancho, int alto)
    {
        var b = new byte[24];
        b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xC0; b[4] = 0; b[5] = 17; b[6] = 8;
        b[7] = (byte)(alto >> 8); b[8] = (byte)alto; b[9] = (byte)(ancho >> 8); b[10] = (byte)ancho;
        return b;
    }

    [Fact]
    public void Un_png_valido_se_acepta_con_sus_medidas()
    {
        var info = ImagenValidator.Validar(Png(1200, 800));
        Assert.Equal("image/png", info.Mime);
        Assert.Equal(".png", info.Extension);
        Assert.Equal(1200, info.Ancho);
        Assert.Equal(800, info.Alto);
    }

    [Fact]
    public void Un_jpeg_valido_se_acepta_con_sus_medidas()
    {
        var info = ImagenValidator.Validar(Jpeg(1600, 900));
        Assert.Equal("image/jpeg", info.Mime);
        Assert.Equal(1600, info.Ancho);
        Assert.Equal(900, info.Alto);
    }

    [Fact]
    public void Un_archivo_que_no_es_imagen_se_rechaza_aunque_se_llame_foto_jpg() =>
        Assert.Throws<ApiException>(() => ImagenValidator.Validar("<html>no soy una foto</html>"u8.ToArray()));

    [Fact]
    public void Un_archivo_vacio_se_rechaza() => Assert.Throws<ApiException>(() => ImagenValidator.Validar([]));

    [Fact]
    public void Una_imagen_con_medidas_absurdas_se_rechaza()
    {
        Assert.Throws<ApiException>(() => ImagenValidator.Validar(Png(ImagenValidator.MaxLado + 1, 100)));
        Assert.Throws<ApiException>(() => ImagenValidator.Validar(Png(0, 100)));
    }

    [Fact]
    public void El_limite_de_tamano_depende_de_donde_se_sube()
    {
        var grande = new byte[ImagenValidator.MaxBytes + 10];
        Png(100, 100).CopyTo(grande, 0);
        Assert.Throws<ApiException>(() => ImagenValidator.Validar(grande));                                      // foto de perfil: 1.5 MB
        Assert.Equal("image/png", ImagenValidator.Validar(grande, ImagenValidator.MaxBytesGaleria).Mime);        // galería de salas: 5 MB
    }

    [Fact]
    public async Task La_lectura_se_corta_en_el_limite_aunque_el_cliente_mienta()
    {
        await using var flujo = new MemoryStream(new byte[2048]);
        await Assert.ThrowsAsync<ApiException>(() => ImagenValidator.LeerAcotadoAsync(flujo, CancellationToken.None, maxBytes: 1024));
    }
}

public class DecisionesDeNegocioTests
{
    /// <summary>Si alguien cambia estos valores por defecto sin querer, esta prueba avisa: son decisiones tomadas con el negocio.</summary>
    [Fact]
    public void Los_plazos_y_duraciones_acordados_siguen_igual()
    {
        var o = new ZOne.Api.Options.ProyectoOptions();
        Assert.Equal(72, o.SesionAceptacionHoras);
        Assert.Equal(24, o.PagoServicioHoras);
        Assert.Equal(24, o.RevisionClienteHoras);
        Assert.Equal(180, o.SesionDuracionMinutos);
        Assert.Equal(60, o.ExtraMinMinutos);
    }
}
