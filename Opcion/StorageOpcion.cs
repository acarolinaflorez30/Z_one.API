namespace ZOne.Api.Options;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    public string ServiceUrl { get; set; } = "";
    public string Region { get; set; } = "sa-east-1";
    public string Bucket { get; set; } = "fotos";

    /// <summary>Claves S3 de Supabase Storage. Van en user-secrets o variables de entorno.</summary>
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
}
