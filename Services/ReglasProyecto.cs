using Microsoft.EntityFrameworkCore;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;

namespace ZOne.Api.Services;

/// <summary>La próxima sesión que se puede programar. Numero es null en la sesión de ajuste.</summary>
public sealed record ProximaSesion(int? Numero, EtapaProyecto Etapa, bool EsAjuste);

/// <summary>Cómo se cobra una sesión: precio por sesión (3 h), por hora, o gratis (ajuste incluido).</summary>
public enum ModalidadCobro { PorSesion, PorHora, Gratis }

/// <summary>Un colaborador tal como se muestra y se cobra dentro de una sesión.</summary>
public sealed record PersonaInfo(
    Guid UsuarioId, Guid ColaboradorId, PerfilColaborador Perfil, string Nombre, decimal? PrecioHora, decimal? PrecioSesion);

/// <summary>Quién mira: define qué precios se muestran (los ven el cliente dueño, el administrador y cada colaborador el suyo).</summary>
public sealed record Vista(Guid UsuarioId, bool EsAdmin, bool EsDueno)
{
    public bool VePrecios => EsAdmin || EsDueno;
}

/// <summary>
/// Reglas de negocio de los proyectos, en un solo lugar.
///
/// Canción completa = 4 sesiones de trabajo (3 h cada una), una por etapa y en este orden:
///   1 producción · 2 grabación · 3 edición y producción · 4 mezcla y masterización.
/// Al cumplirse la 4.ª, el cliente tiene 24 h para aprobar. Si pide ajustes se habilita UNA sesión de ajuste gratis
/// (no cuenta dentro de las 4). Otra ronda de ajustes requiere una sesión extra (cobrada por hora).
///
/// Servicio parcial = 1 sesión (3 h). Al cumplirse queda finalizado: sin revisión, sin aprobación y sin ajustes.
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

    public static int SesionesDelPaquete(Proyecto p) => p.Tipo == TipoProyecto.CancionCompleta ? EtapasCancion.Length : 1;

    public static EtapaProyecto EtapaDe(Proyecto p, int numero)
    {
        if (p.Tipo == TipoProyecto.CancionCompleta)
            return numero >= 1 && numero <= EtapasCancion.Length ? EtapasCancion[numero - 1] : EtapaProyecto.Revision;
        return p.Servicio?.Etapa ?? EtapaProyecto.Produccion;
    }

    /// <summary>Una sesión "abierta" retiene la sala y bloquea programar otra: espera colaboradores, espera el pago o está confirmada.</summary>
    public static bool Abierta(Sesion s) =>
        s.Estado is EstadoSesion.PendienteAceptacion or EstadoSesion.PendientePago or EstadoSesion.Confirmada;

    public static bool Viva(Sesion s) => Abierta(s) || s.Estado == EstadoSesion.Cumplida;

    public static bool EsAjusteIncluido(Sesion s) => !s.EsExtra && s.Etapa == EtapaProyecto.Revision;

    public static bool AjusteIncluidoUsado(IEnumerable<Sesion> sesiones) => sesiones.Any(s => EsAjusteIncluido(s) && Viva(s));

    /// <summary>Sesiones de trabajo ya cumplidas, para el contador "x/4". No cuentan las extra ni la de ajuste.</summary>
    public static int Cumplidas(IEnumerable<Sesion> sesiones) =>
        sesiones.Count(s => !s.EsExtra && s.Etapa != EtapaProyecto.Revision && s.Estado == EstadoSesion.Cumplida);

    public static int AjustesPendientes(Proyecto p, IEnumerable<Sesion> sesiones) =>
        Math.Max(0, p.RondaRevision - sesiones.Count(s => s.Etapa == EtapaProyecto.Revision && s.Estado == EstadoSesion.Cumplida));

    /// <summary>La próxima sesión que se puede programar, o null si todavía no toca.</summary>
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

    // ───────────────────────── Cobro e invitaciones ─────────────────────────

    public static ModalidadCobro Modalidad(Sesion s) =>
        s.Tipo == TipoSesion.Alquiler || s.EsExtra ? ModalidadCobro.PorHora
        : s.Etapa == EtapaProyecto.Revision ? ModalidadCobro.Gratis
        : ModalidadCobro.PorSesion;

    /// <summary>Solo las filas con rol son invitaciones (las antiguas sin rol son de cuando el cliente también "aceptaba").</summary>
    public static IEnumerable<AceptacionSesion> Invitados(Sesion s) => s.Aceptaciones.Where(a => a.Rol != null);

    public static bool PuedeConfirmar(Sesion s) =>
        s.Estado == EstadoSesion.PendienteAceptacion && Invitados(s).Any(a => a.Estado == EstadoParticipacion.Aceptada);

    public static bool RequiereDecision(Sesion s)
    {
        if (s.Estado != EstadoSesion.PendienteAceptacion) return false;
        var inv = Invitados(s).ToList();
        return inv.Count > 0 && inv.All(a => a.Estado != EstadoParticipacion.Pendiente) && inv.Any(a => a.Estado == EstadoParticipacion.Rechazada);
    }

    public static RolParticipante? RolDe(PerfilColaborador perfil) => perfil switch
    {
        PerfilColaborador.Artista => RolParticipante.Artista,
        PerfilColaborador.Productor => RolParticipante.Productor,
        _ => null
    };

    public static bool Vencida(Sesion s, DateTimeOffset ahora) =>
        s.Estado is EstadoSesion.PendienteAceptacion or EstadoSesion.PendientePago && s.ExpiraEn is { } e && e <= ahora;

    public static Guid ClienteIdDe(Sesion s) =>
        s.Tipo == TipoSesion.Alquiler
            ? s.ClienteId ?? throw new InvalidOperationException("El alquiler no tiene cliente.")
            : s.Proyecto?.ClienteId ?? throw new InvalidOperationException("La sesión no tiene proyecto.");

    public static string TextoTipo(TipoProyecto t) => t == TipoProyecto.CancionCompleta ? "canción completa" : "servicio parcial";

    /// <summary>"Sesión 2 de 4", "Sesión única", "Sesión de ajuste", "Sesión extra" o "Alquiler de sala".</summary>
    public static string Descripcion(TipoSesion tipo, bool esExtra, EtapaProyecto? etapa, short? numero, TipoProyecto? tipoProyecto, short? incluidas)
    {
        if (tipo == TipoSesion.Alquiler) return "Alquiler de sala";
        if (esExtra) return "Sesión extra";
        if (etapa == EtapaProyecto.Revision) return "Sesión de ajuste";
        return tipoProyecto == TipoProyecto.CancionCompleta ? $"Sesión {numero} de {incluidas}" : "Sesión única";
    }

    public static string Descripcion(Sesion s) =>
        Descripcion(s.Tipo, s.EsExtra, s.Etapa, s.NumeroSesion, s.Proyecto?.Tipo, s.Proyecto?.SesionesIncluidas);

    public static decimal Horas(Sesion s) => Math.Round((decimal)(s.Fin - s.Inicio).TotalMinutes / 60m, 2);

    // ───────────────────────── Mapeo ─────────────────────────

    public static List<InvitadoDto> MapInvitados(
        Sesion s, IReadOnlyDictionary<Guid, PersonaInfo> personas, Vista yo, DateTimeOffset ahora)
    {
        var vencida = Vencida(s, ahora) || s.Estado is EstadoSesion.Expirada or EstadoSesion.Cancelada;
        var minutos = (decimal)(s.Fin - s.Inicio).TotalMinutes;
        var modalidad = Modalidad(s);

        return Invitados(s).OrderBy(a => a.EnviadaEn).ThenBy(a => a.UsuarioId).Select(a =>
        {
            personas.TryGetValue(a.UsuarioId, out var per);
            decimal? precio = per is not null && (yo.VePrecios || a.UsuarioId == yo.UsuarioId)
                ? PreciosService.PrecioDe(modalidad, minutos, per.PrecioHora, per.PrecioSesion)
                : null;
            var estado = a.Estado == EstadoParticipacion.Pendiente && vencida ? EstadoParticipacion.Expirada : a.Estado;
            return new InvitadoDto(
                a.UsuarioId, per?.ColaboradorId, per?.Nombre ?? "Colaborador",
                per is null ? null : Convenciones.Api(per.Perfil),
                a.Rol is null ? null : Convenciones.Api(a.Rol.Value),
                Convenciones.Api(estado), a.EnviadaEn, a.RespondidaEn, precio);
        }).ToList();
    }

    /// <summary>Sesión de proyecto lista para la API. Requiere Sala, Aceptaciones y Proyecto (con Servicio y Cliente) cargados.</summary>
    public static SesionProyectoDto MapSesion(
        Sesion s, IReadOnlyDictionary<Guid, PersonaInfo> personas, Vista yo,
        decimal? monto, Orden? orden, DateTimeOffset ahora)
    {
        var p = s.Proyecto!;
        var vencida = Vencida(s, ahora);
        var estado = vencida ? EstadoSesion.Expirada : s.Estado;

        string? estadoPago = null;
        if (orden is not null)
            estadoPago = Convenciones.Api(orden.Estado == EstadoOrden.Pendiente && (orden.VenceEn <= ahora || vencida) ? EstadoOrden.Expirada : orden.Estado);

        var verDinero = yo.VePrecios;
        return new SesionProyectoDto(
            s.Id, p.Id, p.Titulo, Convenciones.Api(p.Tipo), p.Servicio.Nombre,
            s.NumeroSesion, p.SesionesIncluidas, Descripcion(s), s.Etapa is null ? null : Convenciones.Api(s.Etapa.Value),
            s.EsExtra, EsAjusteIncluido(s),
            s.SalaId, s.Sala.Nombre, s.Inicio, s.Fin, Horas(s),
            p.ClienteId, p.Cliente.Nombre,
            Convenciones.Api(estado), s.ExpiraEn, s.Resena, s.CumplidaEn,
            verDinero ? monto : null, verDinero ? estadoPago : null, verDinero ? orden?.Id : null,
            !vencida && PuedeConfirmar(s), !vencida && RequiereDecision(s),
            MapInvitados(s, personas, yo, ahora));
    }
}

public static class PersonasExtensions
{
    /// <summary>
    /// Colaboradores por id de usuario. Se muestran con su nombre artístico; el nombre real solo lo ve el administrador.
    /// </summary>
    public static async Task<Dictionary<Guid, PersonaInfo>> PersonasAsync(
        this ZOneDbContext db, IEnumerable<Guid> usuarioIds, bool esAdmin, CancellationToken ct)
    {
        var ids = usuarioIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var filas = await db.Colaboradores.AsNoTracking()
            .Where(c => ids.Contains(c.UsuarioId))
            .Select(c => new
            {
                c.UsuarioId,
                c.Id,
                c.Perfil,
                c.NombreArtistico,
                NombreUsuario = c.Usuario.Nombre,
                c.PrecioHora,
                c.PrecioSesion
            })
            .ToListAsync(ct);

        return filas.ToDictionary(
            f => f.UsuarioId,
            f => new PersonaInfo(
                f.UsuarioId, f.Id, f.Perfil,
                !string.IsNullOrWhiteSpace(f.NombreArtistico) ? f.NombreArtistico! : (esAdmin ? f.NombreUsuario : "Colaborador"),
                f.PrecioHora, f.PrecioSesion));
    }

    /// <summary>
    /// ¿Participa esta persona en el proyecto como colaborador invitado a alguna de sus sesiones?
    /// Solo cuenta con la invitación pendiente o aceptada y con la vinculación activa: quien rechazó, dejó vencer la
    /// invitación o fue desvinculado ya no ve el proyecto.
    /// </summary>
    public static async Task<bool> EsInvitadoAsync(this ZOneDbContext db, long proyectoId, Guid usuarioId, CancellationToken ct)
    {
        var vinculado = await db.Colaboradores.AsNoTracking()
            .AnyAsync(c => c.UsuarioId == usuarioId && c.Vinculacion == EstadoVinculacion.Activo, ct);
        if (!vinculado) return false;

        return await db.Aceptaciones.AsNoTracking().AnyAsync(
            a => a.UsuarioId == usuarioId && a.Rol != null && a.Sesion.ProyectoId == proyectoId
                 && (a.Estado == EstadoParticipacion.Pendiente || a.Estado == EstadoParticipacion.Aceptada), ct);
    }
}
