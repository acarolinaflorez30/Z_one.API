using ZOne.Api.Domain;
using ZOne.Api.Services;

namespace ZOne.Api.Tests;

/// <summary>Constructores cortos de datos de prueba (nada toca la base de datos).</summary>
internal static class Datos
{
    public static readonly Guid Cliente = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly DateTimeOffset Base = new(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);   // 15:00 en Colombia

    public static Proyecto NuevoProyecto(
        TipoProyecto tipo = TipoProyecto.CancionCompleta, EstadoProyecto estado = EstadoProyecto.Confirmado, short ronda = 0) =>
        new()
        {
            Id = 1, ClienteId = Cliente, Titulo = "Mi canción", Tipo = tipo, Estado = estado, RondaRevision = ronda,
            SesionesIncluidas = (short)(tipo == TipoProyecto.CancionCompleta ? 4 : 1),
            Servicio = new Servicio { Id = 1, Nombre = "Producción musical" }
        };

    public static Sesion NuevaSesion(
        EstadoSesion estado = EstadoSesion.Confirmada, TipoSesion tipo = TipoSesion.Proyecto, short? numero = null,
        EtapaProyecto? etapa = null, bool extra = false, int horas = 3, Proyecto? proyecto = null, DateTimeOffset? inicio = null,
        DateTimeOffset? expiraEn = null) =>
        new()
        {
            Id = 1, Tipo = tipo, Estado = estado, NumeroSesion = numero, Etapa = etapa, EsExtra = extra,
            Inicio = inicio ?? Base, Fin = (inicio ?? Base).AddHours(horas), ExpiraEn = expiraEn,
            Proyecto = proyecto, ProyectoId = proyecto?.Id,
            ClienteId = tipo == TipoSesion.Alquiler ? Cliente : null,
            Sala = new Sala { Id = 1, Nombre = "Sala A", PrecioHora = 50_000, PrecioSesion = 300_000 }
        };

    public static AceptacionSesion NuevaInvitacion(EstadoParticipacion estado, RolParticipante? rol = RolParticipante.Artista) =>
        new() { SesionId = 1, UsuarioId = Guid.NewGuid(), Rol = rol, Estado = estado };

    /// <summary>Lista de sesiones cumplidas de trabajo (1..n) de un proyecto.</summary>
    public static List<Sesion> Cumplidas(Proyecto p, int cuantas)
    {
        var l = new List<Sesion>();
        for (var i = 1; i <= cuantas; i++)
            l.Add(NuevaSesion(EstadoSesion.Cumplida, numero: (short)i, etapa: ReglasProyecto.EtapaDe(p, i), proyecto: p));
        return l;
    }
}
