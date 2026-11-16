using ZOne.Api.Domain;
using ZOne.Api.Services;

namespace ZOne.Api.Tests;

public class ReglasProyectoTests
{
    [Fact]
    public void Paquete_cancion_son_4_sesiones_y_parcial_es_1()
    {
        Assert.Equal(4, ReglasProyecto.SesionesDelPaquete(Datos.NuevoProyecto(TipoProyecto.CancionCompleta)));
        Assert.Equal(1, ReglasProyecto.SesionesDelPaquete(Datos.NuevoProyecto(TipoProyecto.ServicioParcial)));
    }

    [Theory]
    [InlineData(1, EtapaProyecto.Produccion)]
    [InlineData(2, EtapaProyecto.Grabacion)]
    [InlineData(3, EtapaProyecto.EdicionProduccion)]
    [InlineData(4, EtapaProyecto.MezclaMasterizacion)]
    [InlineData(5, EtapaProyecto.Revision)]
    public void Las_etapas_de_la_cancion_van_en_orden(int numero, EtapaProyecto esperada) =>
        Assert.Equal(esperada, ReglasProyecto.EtapaDe(Datos.NuevoProyecto(), numero));

    [Fact]
    public void Servicio_parcial_usa_la_etapa_de_su_servicio()
    {
        var p = Datos.NuevoProyecto(TipoProyecto.ServicioParcial);
        p.Servicio.Etapa = EtapaProyecto.Grabacion;
        Assert.Equal(EtapaProyecto.Grabacion, ReglasProyecto.EtapaDe(p, 1));
    }

    [Theory]
    [InlineData(EstadoSesion.PendienteAceptacion, true)]
    [InlineData(EstadoSesion.PendientePago, true)]
    [InlineData(EstadoSesion.Confirmada, true)]
    [InlineData(EstadoSesion.Cumplida, false)]
    [InlineData(EstadoSesion.Expirada, false)]
    [InlineData(EstadoSesion.Cancelada, false)]
    public void Abierta_retiene_la_sala_solo_en_estados_vigentes(EstadoSesion estado, bool esperado) =>
        Assert.Equal(esperado, ReglasProyecto.Abierta(Datos.NuevaSesion(estado)));

    [Fact]
    public void Viva_incluye_las_cumplidas_pero_no_las_vencidas_ni_canceladas()
    {
        Assert.True(ReglasProyecto.Viva(Datos.NuevaSesion(EstadoSesion.Cumplida)));
        Assert.False(ReglasProyecto.Viva(Datos.NuevaSesion(EstadoSesion.Expirada)));
        Assert.False(ReglasProyecto.Viva(Datos.NuevaSesion(EstadoSesion.Cancelada)));
    }

    // ───────── Cuál es la próxima sesión ─────────

    [Fact]
    public void Proyecto_nuevo_confirmado_empieza_por_la_sesion_1_de_produccion()
    {
        var p = Datos.NuevoProyecto();
        var s = ReglasProyecto.Siguiente(p, []);
        Assert.NotNull(s);
        Assert.Equal(1, s!.Numero);
        Assert.Equal(EtapaProyecto.Produccion, s.Etapa);
        Assert.False(s.EsAjuste);
    }

    [Theory]
    [InlineData(EstadoProyecto.Pendiente)]
    [InlineData(EstadoProyecto.EnRevision)]
    [InlineData(EstadoProyecto.Finalizado)]
    [InlineData(EstadoProyecto.Cancelado)]
    public void No_se_programa_si_el_proyecto_no_esta_confirmado_ni_en_curso(EstadoProyecto estado) =>
        Assert.Null(ReglasProyecto.Siguiente(Datos.NuevoProyecto(estado: estado), []));

    [Fact]
    public void Despues_de_cumplir_la_1_toca_la_2_de_grabacion()
    {
        var p = Datos.NuevoProyecto(estado: EstadoProyecto.EnCurso);
        var s = ReglasProyecto.Siguiente(p, Datos.Cumplidas(p, 1));
        Assert.Equal(2, s!.Numero);
        Assert.Equal(EtapaProyecto.Grabacion, s.Etapa);
    }

    [Fact]
    public void Con_una_sesion_abierta_no_se_programa_otra()
    {
        var p = Datos.NuevoProyecto();
        var sesiones = new List<Sesion> { Datos.NuevaSesion(EstadoSesion.PendientePago, numero: 1, etapa: EtapaProyecto.Produccion, proyecto: p) };
        Assert.Null(ReglasProyecto.Siguiente(p, sesiones));
    }

    [Fact]
    public void Una_sesion_expirada_deja_programar_de_nuevo_la_misma_etapa()
    {
        var p = Datos.NuevoProyecto();
        var sesiones = new List<Sesion> { Datos.NuevaSesion(EstadoSesion.Expirada, numero: 1, etapa: EtapaProyecto.Produccion, proyecto: p) };
        Assert.Equal(1, ReglasProyecto.Siguiente(p, sesiones)!.Numero);
    }

    [Fact]
    public void Con_las_4_cumplidas_y_sin_ajustes_pedidos_no_hay_mas_sesiones()
    {
        var p = Datos.NuevoProyecto(estado: EstadoProyecto.EnCurso, ronda: 0);
        Assert.Null(ReglasProyecto.Siguiente(p, Datos.Cumplidas(p, 4)));
    }

    [Fact]
    public void Con_las_4_cumplidas_y_un_ajuste_pedido_toca_la_sesion_de_ajuste_gratis()
    {
        var p = Datos.NuevoProyecto(estado: EstadoProyecto.EnCurso, ronda: 1);
        var s = ReglasProyecto.Siguiente(p, Datos.Cumplidas(p, 4));
        Assert.NotNull(s);
        Assert.True(s!.EsAjuste);
        Assert.Null(s.Numero);
        Assert.Equal(EtapaProyecto.Revision, s.Etapa);
    }

    [Fact]
    public void El_ajuste_incluido_se_usa_una_sola_vez_aunque_se_pida_otra_ronda()
    {
        var p = Datos.NuevoProyecto(estado: EstadoProyecto.EnCurso, ronda: 2);
        var sesiones = Datos.Cumplidas(p, 4);
        sesiones.Add(Datos.NuevaSesion(EstadoSesion.Cumplida, etapa: EtapaProyecto.Revision, proyecto: p));
        Assert.Equal(1, ReglasProyecto.AjustesPendientes(p, sesiones));       // queda una ronda sin ajuste...
        Assert.Null(ReglasProyecto.Siguiente(p, sesiones));                    // ...pero ya no es gratis: requiere sesión extra
    }

    [Fact]
    public void Servicio_parcial_cumplido_no_tiene_siguiente()
    {
        var p = Datos.NuevoProyecto(TipoProyecto.ServicioParcial, EstadoProyecto.EnCurso);
        Assert.Null(ReglasProyecto.Siguiente(p, Datos.Cumplidas(p, 1)));
    }

    [Fact]
    public void El_contador_de_sesiones_no_cuenta_extras_ni_ajustes()
    {
        var p = Datos.NuevoProyecto();
        var l = Datos.Cumplidas(p, 2);
        l.Add(Datos.NuevaSesion(EstadoSesion.Cumplida, extra: true, proyecto: p));
        l.Add(Datos.NuevaSesion(EstadoSesion.Cumplida, etapa: EtapaProyecto.Revision, proyecto: p));
        l.Add(Datos.NuevaSesion(EstadoSesion.Confirmada, numero: 3, etapa: EtapaProyecto.EdicionProduccion, proyecto: p));
        Assert.Equal(2, ReglasProyecto.Cumplidas(l));
    }

    // ───────── Cobro ─────────

    [Fact]
    public void Modalidad_de_cobro_segun_el_tipo_de_sesion()
    {
        var p = Datos.NuevoProyecto();
        Assert.Equal(ModalidadCobro.PorHora, ReglasProyecto.Modalidad(Datos.NuevaSesion(tipo: TipoSesion.Alquiler)));
        Assert.Equal(ModalidadCobro.PorHora, ReglasProyecto.Modalidad(Datos.NuevaSesion(extra: true, proyecto: p)));
        Assert.Equal(ModalidadCobro.Gratis, ReglasProyecto.Modalidad(Datos.NuevaSesion(etapa: EtapaProyecto.Revision, proyecto: p)));
        Assert.Equal(ModalidadCobro.PorSesion, ReglasProyecto.Modalidad(Datos.NuevaSesion(numero: 1, etapa: EtapaProyecto.Produccion, proyecto: p)));
    }

    // ───────── Invitaciones ─────────

    [Fact]
    public void Las_filas_antiguas_sin_rol_no_son_invitaciones()
    {
        var s = Datos.NuevaSesion(EstadoSesion.PendienteAceptacion);
        s.Aceptaciones.Add(Datos.NuevaInvitacion(EstadoParticipacion.Aceptada, rol: null));
        s.Aceptaciones.Add(Datos.NuevaInvitacion(EstadoParticipacion.Pendiente, RolParticipante.Productor));
        Assert.Single(ReglasProyecto.Invitados(s));
        Assert.False(ReglasProyecto.PuedeConfirmar(s));
    }

    [Fact]
    public void Se_puede_confirmar_cuando_al_menos_uno_acepto()
    {
        var s = Datos.NuevaSesion(EstadoSesion.PendienteAceptacion);
        s.Aceptaciones.Add(Datos.NuevaInvitacion(EstadoParticipacion.Aceptada));
        s.Aceptaciones.Add(Datos.NuevaInvitacion(EstadoParticipacion.Pendiente, RolParticipante.Productor));
        Assert.True(ReglasProyecto.PuedeConfirmar(s));
    }

    [Fact]
    public void No_se_puede_confirmar_si_la_sesion_no_esta_pendiente_de_aceptacion()
    {
        var s = Datos.NuevaSesion(EstadoSesion.Confirmada);
        s.Aceptaciones.Add(Datos.NuevaInvitacion(EstadoParticipacion.Aceptada));
        Assert.False(ReglasProyecto.PuedeConfirmar(s));
    }

    [Fact]
    public void Requiere_decision_cuando_todos_respondieron_y_alguien_rechazo()
    {
        var s = Datos.NuevaSesion(EstadoSesion.PendienteAceptacion);
        s.Aceptaciones.Add(Datos.NuevaInvitacion(EstadoParticipacion.Aceptada));
        s.Aceptaciones.Add(Datos.NuevaInvitacion(EstadoParticipacion.Rechazada, RolParticipante.Productor));
        Assert.True(ReglasProyecto.RequiereDecision(s));
    }

    [Fact]
    public void No_requiere_decision_si_aun_hay_pendientes_o_nadie_rechazo()
    {
        var conPendiente = Datos.NuevaSesion(EstadoSesion.PendienteAceptacion);
        conPendiente.Aceptaciones.Add(Datos.NuevaInvitacion(EstadoParticipacion.Rechazada));
        conPendiente.Aceptaciones.Add(Datos.NuevaInvitacion(EstadoParticipacion.Pendiente, RolParticipante.Productor));
        Assert.False(ReglasProyecto.RequiereDecision(conPendiente));

        var todosAceptan = Datos.NuevaSesion(EstadoSesion.PendienteAceptacion);
        todosAceptan.Aceptaciones.Add(Datos.NuevaInvitacion(EstadoParticipacion.Aceptada));
        Assert.False(ReglasProyecto.RequiereDecision(todosAceptan));

        Assert.False(ReglasProyecto.RequiereDecision(Datos.NuevaSesion(EstadoSesion.PendienteAceptacion)));   // sin invitados
    }

    [Theory]
    [InlineData(PerfilColaborador.Artista, RolParticipante.Artista)]
    [InlineData(PerfilColaborador.Productor, RolParticipante.Productor)]
    public void Artistas_y_productores_se_pueden_invitar(PerfilColaborador perfil, RolParticipante rol) =>
        Assert.Equal(rol, ReglasProyecto.RolDe(perfil));

    [Theory]
    [InlineData(PerfilColaborador.IngenieroSonido)]
    [InlineData(PerfilColaborador.IngenieroMezcla)]
    [InlineData(PerfilColaborador.IngenieroMaster)]
    [InlineData(PerfilColaborador.Otro)]
    public void Los_demas_perfiles_no_se_invitan_por_ahora(PerfilColaborador perfil) =>
        Assert.Null(ReglasProyecto.RolDe(perfil));

    // ───────── Vencimientos, cliente y textos ─────────

    [Fact]
    public void Vencida_solo_si_esta_pendiente_y_paso_su_plazo()
    {
        var ahora = Datos.Base;
        Assert.True(ReglasProyecto.Vencida(Datos.NuevaSesion(EstadoSesion.PendientePago, expiraEn: ahora.AddMinutes(-1)), ahora));
        Assert.True(ReglasProyecto.Vencida(Datos.NuevaSesion(EstadoSesion.PendienteAceptacion, expiraEn: ahora), ahora));
        Assert.False(ReglasProyecto.Vencida(Datos.NuevaSesion(EstadoSesion.PendientePago, expiraEn: ahora.AddMinutes(1)), ahora));
        Assert.False(ReglasProyecto.Vencida(Datos.NuevaSesion(EstadoSesion.Confirmada, expiraEn: ahora.AddDays(-1)), ahora));
        Assert.False(ReglasProyecto.Vencida(Datos.NuevaSesion(EstadoSesion.PendientePago, expiraEn: null), ahora));
    }

    [Fact]
    public void El_cliente_de_un_alquiler_y_de_un_proyecto_se_resuelve_bien()
    {
        Assert.Equal(Datos.Cliente, ReglasProyecto.ClienteIdDe(Datos.NuevaSesion(tipo: TipoSesion.Alquiler)));
        Assert.Equal(Datos.Cliente, ReglasProyecto.ClienteIdDe(Datos.NuevaSesion(proyecto: Datos.NuevoProyecto())));
    }

    [Fact]
    public void Una_sesion_sin_cliente_resoluble_falla_en_vez_de_inventar_uno()
    {
        var alquilerSinCliente = Datos.NuevaSesion(tipo: TipoSesion.Alquiler);
        alquilerSinCliente.ClienteId = null;
        Assert.Throws<InvalidOperationException>(() => ReglasProyecto.ClienteIdDe(alquilerSinCliente));
        Assert.Throws<InvalidOperationException>(() => ReglasProyecto.ClienteIdDe(Datos.NuevaSesion()));
    }

    [Fact]
    public void Las_descripciones_dicen_que_sesion_es()
    {
        var p = Datos.NuevoProyecto();
        var parcial = Datos.NuevoProyecto(TipoProyecto.ServicioParcial);
        Assert.Equal("Alquiler de sala", ReglasProyecto.Descripcion(Datos.NuevaSesion(tipo: TipoSesion.Alquiler)));
        Assert.Equal("Sesión extra", ReglasProyecto.Descripcion(Datos.NuevaSesion(extra: true, proyecto: p)));
        Assert.Equal("Sesión de ajuste", ReglasProyecto.Descripcion(Datos.NuevaSesion(etapa: EtapaProyecto.Revision, proyecto: p)));
        Assert.Equal("Sesión 2 de 4", ReglasProyecto.Descripcion(Datos.NuevaSesion(numero: 2, etapa: EtapaProyecto.Grabacion, proyecto: p)));
        Assert.Equal("Sesión única", ReglasProyecto.Descripcion(Datos.NuevaSesion(numero: 1, etapa: EtapaProyecto.Produccion, proyecto: parcial)));
    }

    [Theory]
    [InlineData(180, 3.0)]
    [InlineData(90, 1.5)]
    [InlineData(60, 1.0)]
    public void Las_horas_salen_de_inicio_y_fin(int minutos, double horas) =>
        Assert.Equal((decimal)horas, ReglasProyecto.Horas(Con(Datos.NuevaSesion(), minutos)));

    private static Sesion Con(Sesion s, int minutos) { s.Fin = s.Inicio.AddMinutes(minutos); return s; }

    [Fact]
    public void Texto_del_tipo_de_proyecto()
    {
        Assert.Equal("canción completa", ReglasProyecto.TextoTipo(TipoProyecto.CancionCompleta));
        Assert.Equal("servicio parcial", ReglasProyecto.TextoTipo(TipoProyecto.ServicioParcial));
    }
}
