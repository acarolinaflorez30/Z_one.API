using System.Text;
using ZOne.Api.Infrastructure;

namespace ZOne.Api.Services;

/// <summary>
/// Comando: dotnet run --project ZOne.Api -- crear-admin --email correo@dominio.com --nombre "Nombre Apellido"
/// La contraseña se lee de la variable ZONE_ADMIN_PASSWORD o se pide por consola sin mostrarla.
/// No existe ninguna contraseña por defecto y el comando solo funciona mientras no haya administradores.
/// </summary>
public static class AdminBootstrap
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var email = Valor(args, "--email");
        var nombre = Valor(args, "--nombre");
        if (email is null || nombre is null)
        {
            Console.Error.WriteLine("Uso: dotnet run --project ZOne.Api -- crear-admin --email correo@dominio.com --nombre \"Nombre Apellido\"");
            return 1;
        }

        var password = Environment.GetEnvironmentVariable("ZONE_ADMIN_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            password = LeerOculta("Contraseña (mínimo 12 caracteres): ");
            if (password != LeerOculta("Repite la contraseña: "))
            {
                Console.Error.WriteLine("Las contraseñas no coinciden.");
                return 1;
            }
        }

        using var scope = services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAdminUsuariosService>();
        try
        {
            await admin.CrearPrimerAdministradorAsync(nombre, email, password, CancellationToken.None);
            Console.WriteLine($"Administrador creado: {email.Trim().ToLowerInvariant()}");
            return 0;
        }
        catch (ApiException ex)
        {
            Console.Error.WriteLine(ex.Message);
            if (ex.Errors is not null)
                foreach (var (campo, mensajes) in ex.Errors)
                    foreach (var m in mensajes) Console.Error.WriteLine($"  - {campo}: {m}");
            return 1;
        }
    }

    private static string? Valor(string[] args, string nombre)
    {
        var i = Array.IndexOf(args, nombre);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string LeerOculta(string pregunta)
    {
        Console.Write(pregunta);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";

        var sb = new StringBuilder();
        while (true)
        {
            var tecla = Console.ReadKey(intercept: true);
            if (tecla.Key == ConsoleKey.Enter) break;
            if (tecla.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(tecla.KeyChar)) sb.Append(tecla.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
