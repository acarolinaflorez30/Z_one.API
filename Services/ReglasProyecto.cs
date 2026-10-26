using ZOne.Api.Domain;
using ZOne.Api.Dtos;

namespace ZOne.Api.Services;

/// <summary>La próxima sesión que se puede programar. Numero es null en la sesión de ajuste.</summary>
public sealed record ProximaSesion(int? Numero, EtapaProyecto Etapa, bool EsAjuste);

/// <summary>
/// Reglas de negocio de los proyectos, en un solo lugar.
///
/// Canción completa = 4 sesiones de trabajo, una por etapa y en este orden:
///   1 producción · 2 grabación · 3 edición y producción · 4 mezcla y masterización.
/// Al cumplirse la 4.ª, el cliente tiene 24 h para aprobar. Si pide ajustes se habilita UNA sesión de ajuste
/// (no cuenta dentro de las 4 y no siempre llega a ocurrir). Otra ronda de ajustes requiere una sesión extra pagada.
///
/// Servicio parcial = 1 sesión. Al cumplirse queda finalizado: sin revisión, sin aprobación y sin ajustes.
///
/// Una sesión se gasta del paquete cuando se marca cumplida.
/// </summary>
public static class ReglasProyecto
{
    private static readonly EtapaProyecto[] EtapasCancion =
    [
        EtapaProyecto.Produccion, EtapaProyecto.Grabacion,
        EtapaProyecto.EdicionProduccion, EtapaProyecto.MezclaMasterizacion
    ];

    /// <summary>Sesiones de trabajo del paquete (la de ajuste y las extra no cuentan aquí).</summary>
    public static int SesionesDelPaquete(Proyecto p) => p.Tipo == TipoProyecto.CancionCompleta ? EtapasCancion.Length : 1;

    public static EtapaProyecto EtapaDe(Proyecto p, int numero)
    {
        if (p.Tipo == TipoProyecto.CancionCompleta)
            return numero >= 1 && numero <= EtapasCancion.Length ? EtapasCancion[numero - 1] : EtapaProyecto.Revision;
        return p.Servicio?.Etapa ?? EtapaProyecto.Produccion;
    }

    public static bool Abierta(Sesion s) =>
        s.Estado == EstadoSesion.PendienteAceptacion || s.Estado == EstadoSesion.Confirmada;

    /// <summary>Sesión de ajuste incluida en el paquete (la que se habilita cuando el cliente no aprueba).</summary>
    public static bool EsAjusteIncluido(Sesion s) => !s.EsExtra && s.Etapa == EtapaProyecto.Revision;

    public static bool AjusteIncluidoUsado(IEnumerable<Sesion> sesiones) =>
        sesiones.Any(s => EsAjusteIncluido(s) &&
            (s.Estado == EstadoSesion.PendienteAceptacion || s.Estado == EstadoSesion.Confirmada || s.Estado == EstadoSesion.Cumplida));

    /// <summary>Sesiones de trabajo ya cumplidas, para el contador "x/4". No cuentan las extra ni la de ajuste.</summary>
    public static int Cumplidas(IEnumerable<Sesion> sesiones) =>
        sesiones.Count(s => !s.EsExtra && s.Etapa != EtapaProyecto.Revision && s.Estado == EstadoSesion.Cumplida);

    /// <summary>Veces que el cliente pidió ajustes y todavía no se cumplió la sesión correspondiente (incluida o extra).</summary>
    public static int AjustesPendientes(Proyecto p, IEnumerable<Sesion> sesiones) =>
        Math.Max(0, p.RondaRevision - sesiones.Count(s => s.Etapa == EtapaProyecto.Revision && s.Estado == EstadoSesion.Cumplida));

    /// <summary>Sesiones extra pagadas que aún no se han usado.</summary>
    public static int Creditos(IEnumerable<Orden> ordenes, IEnumerable<Sesion> sesiones)
    {
        var pagadas = ordenes.Count(o => o.Concepto == ConceptoOrden.SesionExtra && o.Estado == EstadoOrden.Pagada);
        var usadas = sesiones.Count(s => s.EsExtra &&
            (s.Estado == EstadoSesion.PendienteAceptacion || s.Estado == EstadoSesion.Confirmada || s.Estado == EstadoSesion.Cumplida));
        return Math.Max(0, pagadas - usadas);
    }

    /// <summary>
    /// La próxima sesión que se puede programar, o null si todavía no toca:
    /// una sesión de trabajo mientras falten de las 4, o la sesión de ajuste incluida si el cliente pidió ajustes.
    /// </summary>
    public static ProximaSesion? Siguiente(Proyecto p, IReadOnlyCollection<Sesion> sesiones)
    {
        if (p.Estado != EstadoProyecto.Confirmado && p.Estado != EstadoProyecto.EnCurso) return null;
        if (sesiones.Any(Abierta)) return null;

        var hechas = Cumplidas(sesiones);
        if (hechas < SesionesDelPaquete(p))
            return new ProximaSesion(hechas + 1, EtapaDe(p, hechas + 1), false);

        if (p.Tipo == TipoProyecto.CancionCompleta && AjustesPendientes(p, sesiones) > 0 && !AjusteIncluidoUsado(sesiones))
            return new ProximaSesion(null, EtapaProyecto.Revision, true);

        return null;
    }

    /// <summary>
    /// Nombres que se muestran dentro de un proyecto. Un colaborador aparece con su nombre artístico;
    /// su nombre real solo lo ve el administrador.
    /// </summary>
    public static Dictionary<Guid, string> Nombres(Proyecto p, bool esAdmin)
    {
        var d = new Dictionary<Guid, string> { [p.Cliente.UsuarioId] = p.Cliente.Nombre };
        foreach (var x in p.Participantes)
        {
            var artistico = x.Colaborador.NombreArtistico;
            d[x.Colaborador.UsuarioId] = !string.IsNullOrWhiteSpace(artistico)
                ? artistico
                : (esAdmin ? x.Colaborador.Usuario.Nombre : "Colaborador");
        }
        return d;
    }

    public static SesionProyectoDto MapSesion(Sesion s, IReadOnlyDictionary<Guid, string> nombres, DateTimeOffset ahora)
    {
        var vencida = s.Estado == EstadoSesion.PendienteAceptacion && s.ExpiraEn is { } e && e <= ahora;
        var estado = vencida ? EstadoSesion.Expirada : s.Estado;

        var aceptaciones = s.Aceptaciones.Select(a => new AceptacionDto(
            a.UsuarioId,
            nombres.TryGetValue(a.UsuarioId, out var nombre) ? nombre : "Participante",
            Convenciones.Api(a.Estado),
            a.RespondidaEn)).ToList();

        return new SesionProyectoDto(
            s.Id, s.ProyectoId ?? 0, s.NumeroSesion,
            s.Etapa is null ? null : Convenciones.Api(s.Etapa.Value),
            s.EsExtra, s.SalaId, s.Sala.Nombre, s.Inicio, s.Fin,
            Convenciones.Api(estado), s.ExpiraEn, s.Resena, s.CumplidaEn, aceptaciones);
    }
}
