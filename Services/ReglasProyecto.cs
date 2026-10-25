using ZOne.Api.Domain;
using ZOne.Api.Dtos;

namespace ZOne.Api.Services;

/// <summary>
/// Reglas de negocio de los proyectos, en un solo lugar:
/// canción completa = 4 sesiones (producción, grabación, mezcla, masterización) + 1 de revisión;
/// servicio parcial = 1 sesión. Una sesión se gasta del paquete cuando se marca cumplida.
/// </summary>
public static class ReglasProyecto
{
    private static readonly EtapaProyecto[] EtapasCancion =
        [EtapaProyecto.Produccion, EtapaProyecto.Grabacion, EtapaProyecto.Mezcla, EtapaProyecto.Masterizacion];

    /// <summary>Sesiones de trabajo que deben cumplirse antes de poder pedir la revisión.</summary>
    public static int SesionesAntesDeRevision(Proyecto p) => p.Tipo == TipoProyecto.CancionCompleta ? 4 : 1;

    public static EtapaProyecto EtapaDe(Proyecto p, int numero)
    {
        if (p.Tipo == TipoProyecto.CancionCompleta)
            return numero >= 1 && numero <= EtapasCancion.Length ? EtapasCancion[numero - 1] : EtapaProyecto.Revision;
        return numero <= 1 ? (p.Servicio?.Etapa ?? EtapaProyecto.Produccion) : EtapaProyecto.Revision;
    }

    public static bool Abierta(Sesion s) =>
        s.Estado == EstadoSesion.PendienteAceptacion || s.Estado == EstadoSesion.Confirmada;

    /// <summary>Sesiones del paquete ya cumplidas (las extra no cuentan).</summary>
    public static int Cumplidas(IEnumerable<Sesion> sesiones) =>
        sesiones.Count(s => !s.EsExtra && s.Estado == EstadoSesion.Cumplida);

    /// <summary>Veces que el cliente pidió ajustes y todavía no se hizo la sesión correspondiente.</summary>
    public static int AjustesPendientes(Proyecto p, IEnumerable<Sesion> sesiones) =>
        p.RondaRevision - sesiones.Count(s => s.Etapa == EtapaProyecto.Revision && s.Estado == EstadoSesion.Cumplida);

    /// <summary>Sesiones extra pagadas que aún no se han usado.</summary>
    public static int Creditos(IEnumerable<Orden> ordenes, IEnumerable<Sesion> sesiones)
    {
        var pagadas = ordenes.Count(o => o.Concepto == ConceptoOrden.SesionExtra && o.Estado == EstadoOrden.Pagada);
        var usadas = sesiones.Count(s => s.EsExtra &&
            (s.Estado == EstadoSesion.PendienteAceptacion || s.Estado == EstadoSesion.Confirmada || s.Estado == EstadoSesion.Cumplida));
        return Math.Max(0, pagadas - usadas);
    }

    /// <summary>La próxima sesión del paquete que se puede programar, o null si todavía no toca.</summary>
    public static (int Numero, EtapaProyecto Etapa)? Siguiente(Proyecto p, IReadOnlyCollection<Sesion> sesiones)
    {
        if (p.Estado != EstadoProyecto.Confirmado && p.Estado != EstadoProyecto.EnCurso) return null;
        if (sesiones.Any(Abierta)) return null;

        var hechas = Cumplidas(sesiones);
        if (hechas >= p.SesionesIncluidas) return null;

        var numero = hechas + 1;
        var etapa = EtapaDe(p, numero);
        // La sesión de revisión solo existe si el cliente pidió ajustes.
        if (etapa == EtapaProyecto.Revision && AjustesPendientes(p, sesiones) <= 0) return null;
        return (numero, etapa);
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
