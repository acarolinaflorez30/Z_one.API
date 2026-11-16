using System.Text;
using Microsoft.EntityFrameworkCore;
using ZOne.Api.Domain;

namespace ZOne.Api.Data;

/// <summary>
/// Mapea las tablas que ya existen en PostgreSQL (el esquema lo gestionan las migraciones SQL de Supabase).
/// Los detalles por perfil de colaborador (artistas, productores...) se leen con SQL parametrizado en DetallePerfil.
/// </summary>
public sealed class ZOneDbContext(DbContextOptions<ZOneDbContext> options, IHttpContextAccessor? accesor = null) : DbContext(options)
{
    /// <summary>Quién hace la petición actual (claim "sub"), o null si no hay sesión (login, tareas internas).</summary>
    private Guid? ActorActual =>
        Guid.TryParse(accesor?.HttpContext?.User.FindFirst("sub")?.Value, out var id) ? id : null;

    /// <summary>
    /// Antes de guardar le dice a PostgreSQL quién es el actor (set_config 'zone.actor', solo dentro de la transacción).
    /// Los triggers del historial del proyecto lo leen para registrar quién hizo cada cambio.
    /// Sin actor (vencimientos automáticos) el historial queda como "sistema".
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (ActorActual is not { } actor)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        var propia = Database.CurrentTransaction is null;
        var tx = propia ? await Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var actorTexto = actor.ToString();
            await Database.ExecuteSqlAsync($"select set_config('zone.actor', {actorTexto}, true)", cancellationToken);
            var n = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (tx is not null) await tx.CommitAsync(cancellationToken);
            return n;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<Colaborador> Colaboradores => Set<Colaborador>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<TokenRecuperacion> TokensRecuperacion => Set<TokenRecuperacion>();
    public DbSet<Auditoria> Auditorias => Set<Auditoria>();
    public DbSet<Sala> Salas => Set<Sala>();
    public DbSet<SalaMedio> SalaMedios => Set<SalaMedio>();
    public DbSet<Servicio> Servicios => Set<Servicio>();
    public DbSet<ColaboradorProyecto> ColaboradorProyectos => Set<ColaboradorProyecto>();
    public DbSet<ColaboradorNota> ColaboradorNotas => Set<ColaboradorNota>();
    public DbSet<Sesion> Sesiones => Set<Sesion>();
    public DbSet<Orden> Ordenes => Set<Orden>();
    public DbSet<Proyecto> Proyectos => Set<Proyecto>();
    public DbSet<HistorialPrecio> HistorialPrecios => Set<HistorialPrecio>();
    public DbSet<ProyectoHistorial> ProyectoHistorial => Set<ProyectoHistorial>();
    public DbSet<AceptacionSesion> Aceptaciones => Set<AceptacionSesion>();
    public DbSet<ComentarioRevision> ComentariosRevision => Set<ComentarioRevision>();
    public DbSet<Notificacion> Notificaciones => Set<Notificacion>();
    public DbSet<CorreoPendiente> CorreosPendientes => Set<CorreoPendiente>();
    public DbSet<AvisoEnviado> AvisosEnviados => Set<AvisoEnviado>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.HasPostgresEnum<RolUsuario>("rol_usuario");
        mb.HasPostgresEnum<TipoCliente>("tipo_cliente");
        mb.HasPostgresEnum<PerfilColaborador>("perfil_colaborador");
        mb.HasPostgresEnum<EstadoVinculacion>("estado_vinculacion");
        mb.HasPostgresEnum<Disponibilidad>("disponibilidad");
        mb.HasPostgresEnum<TipoToken>("tipo_token");
        mb.HasPostgresEnum<EtapaProyecto>("etapa_proyecto");
        mb.HasPostgresEnum<EstadoPortafolio>("estado_portafolio");
        mb.HasPostgresEnum<TipoSesion>("tipo_sesion");
        mb.HasPostgresEnum<EstadoSesion>("estado_sesion");
        mb.HasPostgresEnum<EstadoOrden>("estado_orden");
        mb.HasPostgresEnum<ConceptoOrden>("concepto_orden");
        mb.HasPostgresEnum<TipoProyecto>("tipo_proyecto");
        mb.HasPostgresEnum<EstadoProyecto>("estado_proyecto");
        mb.HasPostgresEnum<EstadoParticipacion>("estado_participacion");
        mb.HasPostgresEnum<RolParticipante>("rol_participante");

        mb.Entity<Usuario>(e =>
        {
            e.ToTable("usuarios");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasOne(x => x.Cliente).WithOne(c => c.Usuario).HasForeignKey<Cliente>(c => c.UsuarioId);
            e.HasOne(x => x.Colaborador).WithOne(c => c.Usuario).HasForeignKey<Colaborador>(c => c.UsuarioId);
        });

        mb.Entity<Cliente>(e => { e.ToTable("clientes"); e.HasKey(x => x.Id); });
        mb.Entity<Colaborador>(e => { e.ToTable("colaboradores"); e.HasKey(x => x.Id); });

        mb.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.Usuario).WithMany().HasForeignKey(x => x.UsuarioId);
        });

        mb.Entity<TokenRecuperacion>(e =>
        {
            e.ToTable("tokens_recuperacion");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.Usuario).WithMany().HasForeignKey(x => x.UsuarioId);
        });

        mb.Entity<Auditoria>(e =>
        {
            e.ToTable("auditoria");
            e.HasKey(x => x.Id);
            e.Property(x => x.Detalle).HasColumnType("jsonb");
            e.HasOne<Usuario>().WithMany().HasForeignKey(x => x.UsuarioId);
        });

        mb.Entity<HistorialPrecio>(e => { e.ToTable("historial_precios"); e.HasKey(x => x.Id); });
        mb.Entity<Sala>(e =>
        {
            e.ToTable("salas");
            e.HasKey(x => x.Id);
            e.HasMany(x => x.Medios).WithOne().HasForeignKey(m => m.SalaId);
        });
        mb.Entity<SalaMedio>(e => { e.ToTable("sala_medios"); e.HasKey(x => x.Id); });
        mb.Entity<Servicio>(e => { e.ToTable("servicios"); e.HasKey(x => x.Id); });

        mb.Entity<ColaboradorProyecto>(e =>
        {
            e.ToTable("colaborador_proyectos");
            e.HasKey(x => x.Id);
            e.HasOne<Colaborador>().WithMany().HasForeignKey(x => x.ColaboradorId);
        });

        mb.Entity<ColaboradorNota>(e =>
        {
            e.ToTable("colaborador_notas");
            e.HasKey(x => x.Id);
            e.HasOne<Colaborador>().WithMany().HasForeignKey(x => x.ColaboradorId);
            e.HasOne<Usuario>().WithMany().HasForeignKey(x => x.AutorId);
        });

        mb.Entity<Sesion>(e =>
        {
            e.ToTable("sesiones");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Sala).WithMany().HasForeignKey(x => x.SalaId);
            e.HasOne(x => x.Cliente).WithMany().HasForeignKey(x => x.ClienteId);
            e.HasOne(x => x.Proyecto).WithMany().HasForeignKey(x => x.ProyectoId);
        });

        mb.Entity<Orden>(e =>
        {
            e.ToTable("ordenes");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Sesion).WithMany().HasForeignKey(x => x.SesionId);
            e.HasOne<Cliente>().WithMany().HasForeignKey(x => x.ClienteId);
            e.HasOne<Proyecto>().WithMany().HasForeignKey(x => x.ProyectoId);
        });

        mb.Entity<Proyecto>(e =>
        {
            e.ToTable("proyectos");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Cliente).WithMany().HasForeignKey(x => x.ClienteId);
            e.HasOne(x => x.Servicio).WithMany().HasForeignKey(x => x.ServicioId);
        });

        mb.Entity<ProyectoHistorial>(e =>
        {
            e.ToTable("proyecto_historial");
            e.HasKey(x => x.Id);
            e.Property(x => x.Detalle).HasColumnType("jsonb");
            e.HasOne<Proyecto>().WithMany().HasForeignKey(x => x.ProyectoId);
        });

        mb.Entity<AceptacionSesion>(e =>
        {
            e.ToTable("aceptaciones_sesion");
            e.HasKey(x => new { x.SesionId, x.UsuarioId });
            e.HasOne(x => x.Sesion).WithMany(s => s.Aceptaciones).HasForeignKey(x => x.SesionId);
            e.HasOne(x => x.Usuario).WithMany().HasForeignKey(x => x.UsuarioId);
        });

        mb.Entity<ComentarioRevision>(e =>
        {
            e.ToTable("comentarios_revision");
            e.HasKey(x => x.Id);
            e.HasOne<Proyecto>().WithMany().HasForeignKey(x => x.ProyectoId);
            e.HasOne<Usuario>().WithMany().HasForeignKey(x => x.AutorId);
        });

        mb.Entity<CorreoPendiente>(e =>
        {
            e.ToTable("correos_pendientes");
            e.HasKey(x => x.Id);
            e.HasOne<Usuario>().WithMany().HasForeignKey(x => x.UsuarioId);
        });

        mb.Entity<AvisoEnviado>(e =>
        {
            e.ToTable("avisos_enviados");
            e.HasKey(x => x.Clave);
        });

        mb.Entity<Notificacion>(e =>
        {
            e.ToTable("notificaciones");
            e.HasKey(x => x.Id);
            e.HasOne<Usuario>().WithMany().HasForeignKey(x => x.UsuarioId);
        });

        // Las columnas de la base están en snake_case (PasswordHash -> password_hash).
        foreach (var entity in mb.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
