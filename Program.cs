using System.Text;
using System.Threading.RateLimiting;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ZOne.Api.Data;
using ZOne.Api.Domain;
using ZOne.Api.Infrastructure;
using ZOne.Api.Options;
using ZOne.Api.Security;
using ZOne.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuración
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.Section))
    .Validate(o => o.SigningKey.Length >= 32,
        "Jwt:SigningKey debe tener al menos 32 caracteres (usa user-secrets o una variable de entorno).")
    .ValidateOnStart();
builder.Services.AddOptions<AuthOptions>().Bind(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.AddOptions<StorageOptions>().Bind(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.AddOptions<AlquilerOptions>().Bind(builder.Configuration.GetSection(AlquilerOptions.Section));
builder.Services.AddOptions<ProyectoOptions>().Bind(builder.Configuration.GetSection(ProyectoOptions.Section));

// Base de datos
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Falta ConnectionStrings:Default (usa user-secrets o la variable de entorno ConnectionStrings__Default).");

builder.Services.AddDbContext<ZOneDbContext>(o => o.UseNpgsql(connectionString, npg =>
{
    npg.MapEnum<RolUsuario>("rol_usuario");
    npg.MapEnum<TipoCliente>("tipo_cliente");
    npg.MapEnum<PerfilColaborador>("perfil_colaborador");
    npg.MapEnum<EstadoVinculacion>("estado_vinculacion");
    npg.MapEnum<Disponibilidad>("disponibilidad");
    npg.MapEnum<TipoToken>("tipo_token");
    npg.MapEnum<EtapaProyecto>("etapa_proyecto");
    npg.MapEnum<EstadoPortafolio>("estado_portafolio");
    npg.MapEnum<TipoSesion>("tipo_sesion");
    npg.MapEnum<EstadoSesion>("estado_sesion");
    npg.MapEnum<EstadoOrden>("estado_orden");
    npg.MapEnum<ConceptoOrden>("concepto_orden");
    npg.MapEnum<TipoProyecto>("tipo_proyecto");
    npg.MapEnum<EstadoProyecto>("estado_proyecto");
    npg.MapEnum<EstadoParticipacion>("estado_participacion");
    npg.MapEnum<RolParticipante>("rol_participante");
}));

// Almacenamiento de fotos: Supabase Storage por protocolo S3
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var s = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
    var config = new AmazonS3Config
    {
        ServiceURL = s.ServiceUrl,
        ForcePathStyle = true,
        AuthenticationRegion = s.Region,
        // Supabase no es AWS: solo calcular checksums cuando el servicio los exige.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
    };
    return new AmazonS3Client(new BasicAWSCredentials(s.AccessKey, s.SecretKey), config);
});
builder.Services.AddSingleton<IFotoStorage, S3FotoStorage>();

//Servicios
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<AuthCookieWriter>();
builder.Services.AddSingleton<IEmailSender, LogEmailSender>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<IAuditoriaService, AuditoriaService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAdminUsuariosService, AdminUsuariosService>();
builder.Services.AddScoped<IColaboradoresService, ColaboradoresService>();
builder.Services.AddScoped<IPublicoService, PublicoService>();
builder.Services.AddScoped<ICatalogoService, CatalogoService>();
builder.Services.AddScoped<IAlquileresService, AlquileresService>();
builder.Services.AddScoped<IProyectosService, ProyectosService>();
builder.Services.AddScoped<ISesionesProyectoService, SesionesProyectoService>();
builder.Services.AddScoped<IOrdenesService, OrdenesService>();
builder.Services.AddScoped<IAgendaService, AgendaService>();
builder.Services.AddScoped<INotificacionesService, NotificacionesService>();

builder.Services.AddControllers();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Autenticación: JWT leído desde cookie
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((o, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = "role"
        };
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token) &&
                    context.Request.Cookies.TryGetValue(AuthCookieWriter.Access, out var token))
                    context.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// CORS: solo el origen del frontend, con cookies
var authOpciones = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
var origenes = authOpciones.AllowedOrigins.Length > 0
    ? authOpciones.AllowedOrigins
    : new[] { authOpciones.FrontendBaseUrl };

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(origenes)
    .AllowCredentials()
    .WithHeaders("Content-Type", "Authorization", RequireCustomHeaderMiddleware.HeaderName)
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")));

// Límite de peticiones por IP
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            status = 429,
            title = "Demasiadas peticiones. Espera un momento e inténtalo de nuevo.",
            code = "demasiadas_peticiones"
        }, ct);
    };

    void Politica(string nombre, int limite, TimeSpan ventana) =>
        o.AddPolicy(nombre, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "desconocida",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limite, Window = ventana, QueueLimit = 0 }));

    Politica("login", 10, TimeSpan.FromMinutes(1));
    Politica("registro", 5, TimeSpan.FromMinutes(10));
    Politica("recuperacion", 5, TimeSpan.FromMinutes(10));
    Politica("sesion", 30, TimeSpan.FromMinutes(1));
    Politica("publico", 120, TimeSpan.FromMinutes(1));
    Politica("reservas", 20, TimeSpan.FromMinutes(1));
});

// Solo se confía en proxies locales (loopback). Si despliegas detrás de un proxy, configura KnownProxies.
// Detrás de un proveedor de hosting (que pone un proxy delante), activa Proxy:ConfiarEnReenviados.
// Sin eso, todas las peticiones parecerían venir de la misma IP y el límite de peticiones afectaría a todos juntos.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (builder.Configuration.GetValue<bool>("Proxy:ConfiarEnReenviados"))
    {
        o.KnownProxies.Clear();
        o.KnownNetworks.Clear();
    }
});

var app = builder.Build();

// Comando: dotnet run --project ZOne.Api -- crear-admin --email ... --nombre "..."
if (args.Length > 0 && args[0] == "crear-admin")
    return await AdminBootstrap.RunAsync(app.Services, args[1..]);

app.UseForwardedHeaders();
app.UseExceptionHandler();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/auth") || context.Request.Path.StartsWithSegments("/admin"))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});

app.UseCors();
app.UseRateLimiter();
app.UseMiddleware<RequireCustomHeaderMiddleware>();
app.UseAuthentication();
app.UseMiddleware<PasswordChangeRequiredMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();
return 0;
