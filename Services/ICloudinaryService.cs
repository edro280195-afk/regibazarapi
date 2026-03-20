namespace EntregasApi.Services;

public interface ICloudinaryService
{
    /// <summary>Sube un archivo y devuelve la URL permanente (HTTPS).</summary>
    Task<string> UploadAsync(Stream stream, string fileName, string folder);
}
