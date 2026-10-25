using System.Security.Cryptography;

namespace ZOne.Api.Security;

public static class PasswordPolicy
{
    public const int MaxLength = 128;

    private static readonly string[] Comunes =
        ["password", "contrasena", "contraseña", "12345678", "qwertyui", "zone1234", "admin123", "colab123", "cliente123"];

    public static List<string> Validar(string? password, string? email, string? nombre, int minLength)
    {
        var errores = new List<string>();

        if (string.IsNullOrEmpty(password))
        {
            errores.Add("La contraseña es obligatoria.");
            return errores;
        }

        if (password.Length < minLength) errores.Add($"Debe tener al menos {minLength} caracteres.");
        if (password.Length > MaxLength) errores.Add($"No puede superar {MaxLength} caracteres.");
        if (!password.Any(char.IsLetter)) errores.Add("Debe incluir al menos una letra.");
        if (!password.Any(char.IsDigit)) errores.Add("Debe incluir al menos un número.");
        if (password.Distinct().Count() < 5) errores.Add("Es demasiado repetitiva.");

        var minus = password.ToLowerInvariant();
        if (Comunes.Any(minus.Contains)) errores.Add("Es una contraseña demasiado común.");

        if (!string.IsNullOrWhiteSpace(email))
        {
            var local = email.Split('@')[0];
            if (local.Length >= 4 && minus.Contains(local.ToLowerInvariant()))
                errores.Add("No debe contener tu correo.");
        }

        if (!string.IsNullOrWhiteSpace(nombre))
        {
            var compacto = nombre.Replace(" ", "").ToLowerInvariant();
            if (compacto.Length >= 5 && minus.Contains(compacto))
                errores.Add("No debe contener tu nombre.");
        }

        return errores;
    }

    /// <summary>Contraseña temporal: sin caracteres ambiguos (0/O, 1/l/I) y siempre con mayúscula, minúscula y número.</summary>
    public static string GenerarTemporal(int longitud = 14)
    {
        const string mayus = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string minus = "abcdefghijkmnopqrstuvwxyz";
        const string nums = "23456789";
        const string todo = mayus + minus + nums;

        while (true)
        {
            var chars = new char[longitud];
            for (var i = 0; i < longitud; i++) chars[i] = todo[RandomNumberGenerator.GetInt32(todo.Length)];
            var s = new string(chars);
            if (s.Any(c => mayus.Contains(c)) && s.Any(c => minus.Contains(c)) && s.Any(c => nums.Contains(c)))
                return s;
        }
    }
}
