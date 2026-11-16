using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Dtos;
using ZOne.Api.Infrastructure;
using ZOne.Api.Options;

namespace ZOne.Api.Services;

public interface IPreciosService
{
    /// <summary>Valida que cada colaborador se pueda invitar (activo, artista o productor, con el precio que la sesión necesita).</summary>
    Task<IReadOnlyList<PersonaInfo>> InvitablesAsync(IReadOnlyCollection<Guid>? colaboradorIds, ModalidadCobro modalidad, int max, CancellationToken ct);

    /// <summary>Cobro de una sesión con las tarifas de hoy: sala + colaboradores que aceptaron. Requiere Sala y Aceptaciones cargadas. Falla si falta una tarifa.</summary>
    Task<CotizacionDto> CobroDeSesionAsync(Sesion sesion, bool esAdmin, CancellationToken ct);

    /// <summary>
    /// Monto que se muestra de cada sesión: el cobro pagado si ya existe; si no, el cálculo con las tarifas de hoy
    /// (los precios no se congelan mientras el cobro está pendiente). Requiere Sala y Aceptaciones cargadas.
    /// </summary>
    Task<IReadOnlyDictionary<long, decimal>> MontosAsync(IReadOnlyCollection<Sesion> sesiones, CancellationToken ct);

    /// <summary>Precio que ve el cliente antes de programar.</summary>
    Task<CotizacionDto> CotizarAsync(CotizacionRequest r, bool esAdmin, CancellationToken ct);
}

/// <summary>
/// Modelo de cobro por sesión. El cliente paga sala + colaboradores que aceptaron esa sesión:
///   servicio parcial y cada sesión de la canción → precio por sesión de la sala y de cada colaborador;
///   sesión extra y alquiler → precio por hora de la sala y de cada colaborador × horas;
///   sesión de ajuste incluida → gratis.
/// </summary>
public sealed class PreciosService(
    ZOneDbContext db, IOptions<ProyectoOptions> proyectoOpt, IOptions<AlquilerOptions> alquilerOpt) : IPreciosService
{
    private static decimal Redondear(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Lo que cobra un colaborador por una sesión de esa modalidad y duración, o null si no definió el precio necesario.</summary>
    public static decimal? PrecioDe(ModalidadCobro modalidad, decimal minutos, decimal? hora, decimal? sesion) => modalidad switch
    {
        ModalidadCobro.Gratis => 0m,
        ModalidadCobro.PorSesion => sesion is > 0 ? sesion : null,
        _ => hora is > 0 ? Redondear(hora.Value * minutos / 60m) : null
    };

    public static void ValidarSala(Sala sala, ModalidadCobro modalidad)
    {
        if (modalidad == ModalidadCobro.PorSesion && sala.PrecioSesion <= 0)
            throw ApiException.Conflicto("tarifa_no_definida", $"La sala \"{sala.Nombre}\" aún no tiene precio por sesión: no se puede reservar.");
        if (modalidad == ModalidadCobro.PorHora && sala.PrecioHora <= 0)
            throw ApiException.Conflicto("tarifa_no_definida", $"La sala \"{sala.Nombre}\" aún no tiene precio por hora: no se puede reservar.");
    }

    public static CotizacionDto Calcular(
        ModalidadCobro modalidad, Sala sala, decimal minutos, IEnumerable<PersonaInfo> colaboradores, bool estricto)
    {
        var horas = Math.Round(minutos / 60m, 2);
        var lineas = new List<LineaCobroDto>();
        var modalidadTexto = Convenciones.Api(modalidad);
        if (modalidad == ModalidadCobro.Gratis) return new CotizacionDto(modalidadTexto, horas, 0m, lineas);

        var porSesion = modalidad == ModalidadCobro.PorSesion;
        var unidad = porSesion ? "sesion" : "hora";
        var cantidad = porSesion ? 1m : horas;
        if (estricto) ValidarSala(sala, modalidad);

        var precioSala = porSesion ? sala.PrecioSesion : sala.PrecioHora;
        lineas.Add(new LineaCobroDto("sala", sala.Nombre, unidad, cantidad, precioSala,
            porSesion ? precioSala : Redondear(precioSala * minutos / 60m)));

        foreach (var c in colaboradores)
        {
            var unitario = porSesion ? c.PrecioSesion : c.PrecioHora;
            if (unitario is not > 0)
            {
                if (estricto)
                    throw ApiException.Conflicto("tarifa_colaborador_no_definida",
                        $"{c.Nombre} aún no definió su precio {(porSesion ? "por sesión" : "por hora")}.");
                unitario = 0m;
            }
            lineas.Add(new LineaCobroDto("colaborador", c.Nombre, unidad, cantidad, unitario.Value,
                porSesion ? unitario.Value : Redondear(unitario.Value * minutos / 60m)));
        }

        return new CotizacionDto(modalidadTexto, horas, Redondear(lineas.Sum(l => l.Subtotal)), lineas);
    }

    public async Task<IReadOnlyList<PersonaInfo>> InvitablesAsync(
        IReadOnlyCollection<Guid>? colaboradorIds, ModalidadCobro modalidad, int max, CancellationToken ct)
    {
        var ids = colaboradorIds?.ToList() ?? [];
        if (ids.Count == 0) return [];
        if (ids.Count > max) throw ApiException.Validacion("colaboradorIds", $"Una sesión admite como máximo {max} colaboradores.");
        if (ids.Distinct().Count() != ids.Count) throw ApiException.Validacion("colaboradorIds", "No repitas colaboradores.");

        var filas = await db.Colaboradores.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new
            {
                c.Id,
                c.UsuarioId,
                c.Perfil,
                c.Vinculacion,
                UsuarioActivo = c.Usuario.Activo,
                c.NombreArtistico,
                c.PrecioHora,
                c.PrecioSesion
            })
            .ToListAsync(ct);

        var resultado = new List<PersonaInfo>();
        foreach (var id in ids)
        {
            var f = filas.FirstOrDefault(x => x.Id == id)
                    ?? throw ApiException.Validacion("colaboradorIds", "Uno de los colaboradores no existe.");
            var nombre = string.IsNullOrWhiteSpace(f.NombreArtistico) ? "El colaborador" : f.NombreArtistico!;

            if (f.Vinculacion != EstadoVinculacion.Activo || !f.UsuarioActivo)
                throw ApiException.Validacion("colaboradorIds", $"{nombre} no está activo.");
            if (ReglasProyecto.RolDe(f.Perfil) is null)
                throw ApiException.Validacion("colaboradorIds",
                    $"{nombre} tiene perfil {Convenciones.Api(f.Perfil)}: por ahora solo se invitan artistas y productores.");
            if (PrecioDe(modalidad, 60m, f.PrecioHora, f.PrecioSesion) is null)
                throw ApiException.Conflicto("tarifa_colaborador_no_definida",
                    $"{nombre} aún no definió su precio {(modalidad == ModalidadCobro.PorSesion ? "por sesión" : "por hora")}: no se puede invitar.");

            resultado.Add(new PersonaInfo(f.UsuarioId, f.Id, f.Perfil, nombre, f.PrecioHora, f.PrecioSesion));
        }
        return resultado;
    }

    public async Task<CotizacionDto> CobroDeSesionAsync(Sesion s, bool esAdmin, CancellationToken ct)
    {
        var aceptados = ReglasProyecto.Invitados(s).Where(a => a.Estado == EstadoParticipacion.Aceptada).Select(a => a.UsuarioId).ToList();
        var personas = await db.PersonasAsync(aceptados, esAdmin, ct);
        var minutos = (decimal)(s.Fin - s.Inicio).TotalMinutes;
        return Calcular(ReglasProyecto.Modalidad(s), s.Sala, minutos, personas.Values.OrderBy(p => p.Nombre), estricto: true);
    }

    public async Task<IReadOnlyDictionary<long, decimal>> MontosAsync(IReadOnlyCollection<Sesion> sesiones, CancellationToken ct)
    {
        var resultado = new Dictionary<long, decimal>();
        if (sesiones.Count == 0) return resultado;
        var ids = sesiones.Select(s => s.Id).ToList();

        var pagadas = await db.Ordenes.AsNoTracking()
            .Where(o => o.SesionId != null && ids.Contains(o.SesionId.Value) && o.Estado == EstadoOrden.Pagada)
            .Select(o => new { SesionId = o.SesionId!.Value, o.Monto })
            .ToListAsync(ct);
        foreach (var p in pagadas) resultado[p.SesionId] = p.Monto;

        var calcular = sesiones
            .Where(s => !resultado.ContainsKey(s.Id) && s.Estado is not (EstadoSesion.Expirada or EstadoSesion.Cancelada))
            .ToList();
        if (calcular.Count == 0) return resultado;

        var usuarios = calcular.SelectMany(ReglasProyecto.Invitados)
            .Where(a => a.Estado == EstadoParticipacion.Aceptada).Select(a => a.UsuarioId).Distinct().ToList();
        var personas = await db.PersonasAsync(usuarios, esAdmin: false, ct);

        foreach (var s in calcular)
        {
            var aceptados = ReglasProyecto.Invitados(s).Where(a => a.Estado == EstadoParticipacion.Aceptada)
                .Select(a => personas.TryGetValue(a.UsuarioId, out var p) ? p : null).OfType<PersonaInfo>();
            var minutos = (decimal)(s.Fin - s.Inicio).TotalMinutes;
            resultado[s.Id] = Calcular(ReglasProyecto.Modalidad(s), s.Sala, minutos, aceptados, estricto: false).Total;
        }
        return resultado;
    }

    public async Task<CotizacionDto> CotizarAsync(CotizacionRequest r, bool esAdmin, CancellationToken ct)
    {
        var sala = await db.Salas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.SalaId && s.Activo && (s.Publicada || esAdmin), ct)
                   ?? throw ApiException.NoEncontrado("No existe esa sala.");

        var po = proyectoOpt.Value;
        var ao = alquilerOpt.Value;
        var concepto = (r.Concepto ?? "").Trim().ToLowerInvariant();

        ModalidadCobro modalidad;
        decimal minutos;
        switch (concepto)
        {
            case "sesion": modalidad = ModalidadCobro.PorSesion; minutos = po.SesionDuracionMinutos; break;
            case "ajuste": modalidad = ModalidadCobro.Gratis; minutos = po.SesionDuracionMinutos; break;
            case "extra":
            case "alquiler":
                modalidad = ModalidadCobro.PorHora;
                if (r.Horas is not { } horas) throw ApiException.Validacion("horas", "Indica cuántas horas.");
                minutos = horas * 60m;
                var min = concepto == "extra" ? po.ExtraMinMinutos : ao.MinMinutos;
                if (minutos < min || (concepto == "alquiler" && minutos > ao.MaxMinutos))
                    throw ApiException.Validacion("horas", concepto == "extra"
                        ? $"La sesión extra dura mínimo {min / 60m:0.##} h."
                        : $"El alquiler dura entre {min / 60m:0.##} y {ao.MaxMinutos / 60m:0.##} h.");
                if (minutos % po.GranularidadMinutos != 0)
                    throw ApiException.Validacion("horas", $"La duración va en bloques de {po.GranularidadMinutos} minutos.");
                break;
            default:
                throw ApiException.Validacion("concepto", "El concepto debe ser sesion, ajuste, extra o alquiler.");
        }

        var invitables = await InvitablesAsync(r.ColaboradorIds, modalidad, po.MaxInvitadosPorSesion, ct);
        return Calcular(modalidad, sala, minutos, invitables, estricto: true);
    }
}
