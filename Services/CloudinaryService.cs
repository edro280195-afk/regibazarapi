using CloudinaryDotNet;
using CloudinaryDotNet.Actions;

namespace EntregasApi.Services;

public class CloudinaryService : ICloudinaryService
{
    private readonly Cloudinary _cloudinary;

    public CloudinaryService(IConfiguration config)
    {
        var section = config.GetSection("Cloudinary");
        var account = new Account(
            section["CloudName"],
            section["ApiKey"],
            section["ApiSecret"]
        );
        _cloudinary = new Cloudinary(account) { Api = { Secure = true } };
    }

    public async Task<string> UploadAsync(Stream stream, string fileName, string folder)
    {
        var publicId = $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid():N}";

        var uploadParams = new ImageUploadParams
        {
            File = new FileDescription(fileName, stream),
            Folder = $"regibazar/{folder}",
            PublicId = publicId,
            Overwrite = false,
            Transformation = new Transformation().Quality("auto").FetchFormat("auto")
        };

        var result = await _cloudinary.UploadAsync(uploadParams);

        if (result.Error != null)
            throw new Exception($"Cloudinary upload error: {result.Error.Message}");

        return result.SecureUrl.ToString();
    }
}
