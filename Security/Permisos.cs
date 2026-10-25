using ZOne.Api.Domain;

namespace ZOne.Api.Security;

/// <summary>
/// Matriz inicial de permisos por rol. La API decide; el frontend solo los recibe en /auth/me
/// para mostrar u ocultar opciones. Se amplía con cada etapa.
/// </summary>
public static class Permisos
{
    private static readonly string[] Admin =
    [
        "usuarios.gestionar", "colaboradores.gestionar", "salas.gestionar", "servicios.gestionar",
        "proyectos.ver_todos", "proyectos.crear", "sesiones.programar", "sesiones.cumplir",
        "ordenes.registrar_pago", "alquileres.reservar", "alquileres.gestionar", "agenda.ver_todo", "estadisticas.ver", "auditoria.ver"
    ];

    private static readonly string[] Cliente =
    [
        "proyectos.crear", "proyectos.ver_propios", "proyectos.aprobar", "revision.comentar",
        "alquileres.reservar", "sesiones.programar", "agenda.ver_propia", "perfil.editar"
    ];

    private static readonly string[] Colaborador =
    [
        "solicitudes.responder", "proyectos.ver_asignados", "sesiones.aceptar", "sesiones.programar",
        "sesiones.cumplir", "sesiones.solicitar_extra", "agenda.ver_propia", "perfil.editar", "portafolio.editar"
    ];

    public static IReadOnlyList<string> Para(RolUsuario rol) => rol switch
    {
        RolUsuario.Administrador => Admin,
        RolUsuario.Cliente => Cliente,
        RolUsuario.Colaborador => Colaborador,
        _ => []
    };
}
