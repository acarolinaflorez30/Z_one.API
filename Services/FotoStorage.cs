using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using ZOne.Api.Options;

namespace ZOne.Api.Services;

/// <summary>Almacén de fotos. Hoy es Supabase Storage (protocolo S3); puede cambiarse sin tocar el resto de la API.</summary>
public interface IFotoStorage
{
    Task SubirAsync(string clave, byte[] datos, string contentType, CancellationToken ct);
    Task<byte[]?> ObtenerAsync(string clave, CancellationToken ct);
    Task EliminarAsync(string clave, CancellationToken ct);
}

public sealed class S3FotoStorage(IAmazonS3 s3, IOptions<StorageOptions> options) : IFotoStorage
{
    private string Bucket => options.Value.Bucket;

    public async Task SubirAsync(string clave, byte[] datos, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream(datos);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = clave,
            InputStream = ms,
            ContentType = contentType
        }, ct);
    }

    public async Task<byte[]?> ObtenerAsync(string clave, CancellationToken ct)
    {
        try
        {
            using var respuesta = await s3.GetObjectAsync(Bucket, clave, ct);
            using var ms = new MemoryStream();
            await respuesta.ResponseStream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
        {
            return null;
        }
    }

    public Task EliminarAsync(string clave, CancellationToken ct) =>
        s3.DeleteObjectAsync(Bucket, clave, ct);
}
