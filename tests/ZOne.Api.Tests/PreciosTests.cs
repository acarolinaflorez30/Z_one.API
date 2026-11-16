using ZOne.Api.Domain;
using ZOne.Api.Infrastructure;
using ZOne.Api.Services;

namespace ZOne.Api.Tests;

public class PreciosTests
{
    private static Sala Sala(decimal hora = 50_000, decimal sesion = 300_000) =>
        new() { Id = 1, Nombre = "Sala A", PrecioHora = hora, PrecioSesion = sesion };

    private static PersonaInfo Persona(string nombre, decimal? hora, decimal? sesion, PerfilColaborador perfil = PerfilColaborador.Artista) =>
        new(Guid.NewGuid(), Guid.NewGuid(), perfil, nombre, hora, sesion);

    // ───────── Precio de un colaborador ─────────

    [Fact]
    public void Por_sesion_se_usa_el_precio_de_sesion_sin_importar_los_minutos() =>
        Assert.Equal(100_000m, PreciosService.PrecioDe(ModalidadCobro.PorSesion, 180, 30_000, 100_000));

    [Fact]
    public void Por_hora_se_multiplica_por_las_horas_y_se_redondea()
    {
        Assert.Equal(30_000m, PreciosService.PrecioDe(ModalidadCobro.PorHora, 90, 20_000, null));
        Assert.Equal(3_333.67m, PreciosService.PrecioDe(ModalidadCobro.PorHora, 20, 10_001, null));
    }

    [Fact]
    public void Gratis_siempre_es_cero() =>
        Assert.Equal(0m, PreciosService.PrecioDe(ModalidadCobro.Gratis, 180, null, null));

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, 0.0)]
    public void Sin_el_precio_que_la_modalidad_necesita_devuelve_null(double? hora, double? sesion)
    {
        decimal? h = hora is null ? null : (decimal)hora, s = sesion is null ? null : (decimal)sesion;
        Assert.Null(PreciosService.PrecioDe(ModalidadCobro.PorSesion, 180, h, s));
        Assert.Null(PreciosService.PrecioDe(ModalidadCobro.PorHora, 60, h, s));
    }

    [Fact]
    public void Solo_define_el_precio_de_la_otra_modalidad_no_sirve()
    {
        Assert.Null(PreciosService.PrecioDe(ModalidadCobro.PorSesion, 180, 20_000, null));
        Assert.Null(PreciosService.PrecioDe(ModalidadCobro.PorHora, 60, null, 100_000));
    }

    // ───────── Cotización: sala + colaboradores que aceptaron ─────────

    [Fact]
    public void Sesion_estandar_suma_sala_y_colaboradores_por_sesion()
    {
        var c = PreciosService.Calcular(ModalidadCobro.PorSesion, Sala(), 180,
            [Persona("Ana", 20_000, 100_000), Persona("Beto", 25_000, 120_000, PerfilColaborador.Productor)], estricto: true);

        Assert.Equal("por_sesion", c.Modalidad);
        Assert.Equal(3m, c.Horas);
        Assert.Equal(520_000m, c.Total);
        Assert.Equal(3, c.Lineas.Count);
        Assert.All(c.Lineas, l => Assert.Equal("sesion", l.Unidad));
        Assert.Equal("sala", c.Lineas[0].Tipo);
        Assert.Equal(300_000m, c.Lineas[0].Subtotal);
    }

    [Fact]
    public void Por_hora_multiplica_sala_y_colaboradores_por_las_horas()
    {
        var c = PreciosService.Calcular(ModalidadCobro.PorHora, Sala(), 90, [Persona("Ana", 20_000, null)], estricto: true);

        Assert.Equal("por_hora", c.Modalidad);
        Assert.Equal(1.5m, c.Horas);
        Assert.Equal(75_000m + 30_000m, c.Total);
        Assert.All(c.Lineas, l => { Assert.Equal("hora", l.Unidad); Assert.Equal(1.5m, l.Cantidad); });
    }

    [Fact]
    public void El_total_es_la_suma_de_los_subtotales_ya_redondeados()
    {
        var c = PreciosService.Calcular(ModalidadCobro.PorHora, Sala(10_001), 20, [Persona("Ana", 10_001, null)], estricto: true);
        Assert.Equal(3_333.67m, c.Lineas[0].Subtotal);
        Assert.Equal(6_667.34m, c.Total);
    }

    [Fact]
    public void El_ajuste_incluido_es_gratis_y_no_lista_cobros()
    {
        var c = PreciosService.Calcular(ModalidadCobro.Gratis, Sala(), 180, [Persona("Ana", 1, 1)], estricto: true);
        Assert.Equal("gratis", c.Modalidad);
        Assert.Equal(0m, c.Total);
        Assert.Empty(c.Lineas);
    }

    [Fact]
    public void Un_alquiler_sin_colaboradores_cobra_solo_la_sala()
    {
        var c = PreciosService.Calcular(ModalidadCobro.PorHora, Sala(), 120, [], estricto: true);
        Assert.Equal(100_000m, c.Total);
        Assert.Single(c.Lineas);
    }

    [Fact]
    public void Al_cobrar_de_verdad_falta_de_tarifa_de_un_colaborador_es_un_error()
    {
        var ex = Assert.Throws<ApiException>(() =>
            PreciosService.Calcular(ModalidadCobro.PorSesion, Sala(), 180, [Persona("Ana", 20_000, null)], estricto: true));
        Assert.Equal(409, ex.Status);
        Assert.Equal("tarifa_colaborador_no_definida", ex.Code);
    }

    [Fact]
    public void Al_cotizar_sin_exigir_la_tarifa_faltante_cuenta_como_cero()
    {
        var c = PreciosService.Calcular(ModalidadCobro.PorSesion, Sala(), 180, [Persona("Ana", null, null)], estricto: false);
        Assert.Equal(300_000m, c.Total);
        Assert.Equal(0m, c.Lineas[1].Subtotal);
    }

    [Theory]
    [InlineData(ModalidadCobro.PorSesion)]
    [InlineData(ModalidadCobro.PorHora)]
    public void Una_sala_sin_precio_no_se_puede_reservar(ModalidadCobro modalidad)
    {
        var ex = Assert.Throws<ApiException>(() => PreciosService.ValidarSala(Sala(0, 0), modalidad));
        Assert.Equal("tarifa_no_definida", ex.Code);
        Assert.Throws<ApiException>(() => PreciosService.Calcular(modalidad, Sala(0, 0), 180, [], estricto: true));
    }

    [Fact]
    public void La_sala_solo_necesita_el_precio_de_la_modalidad_que_se_usa()
    {
        PreciosService.ValidarSala(Sala(hora: 0, sesion: 300_000), ModalidadCobro.PorSesion);
        PreciosService.ValidarSala(Sala(hora: 50_000, sesion: 0), ModalidadCobro.PorHora);
        PreciosService.ValidarSala(Sala(0, 0), ModalidadCobro.Gratis);
    }
}
