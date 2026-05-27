using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace EntregasApi.Services;

public interface ILiveStorageService
{
    Task<string> UploadVideoAsync(int liveSessionId, string filePath, CancellationToken cancellationToken);
    Task<string> DownloadVideoToTempAsync(string r2Key, string tempDirectory, CancellationToken cancellationToken);
}

public class LiveStorageService : ILiveStorageService
{
    private readonly CloudflareR2Options _options;
    private readonly IAmazonS3 _s3;

    public LiveStorageService(IOptions<CloudflareR2Options> options)
    {
        _options = options.Value;
        ValidateOptions(_options);

        var serviceUrl = string.IsNullOrWhiteSpace(_options.ServiceUrl)
            ? $"https://{_options.AccountId}.r2.cloudflarestorage.com"
            : _options.ServiceUrl;

        var config = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "auto"
        };

        _s3 = new AmazonS3Client(
            new BasicAWSCredentials(_options.AccessKeyId, _options.SecretAccessKey),
            config);
    }

    public async Task<string> UploadVideoAsync(int liveSessionId, string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("No se encontro el video descargado.", filePath);

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".mp4";

        var key = $"{_options.VideoPrefix.TrimEnd('/')}/live_{liveSessionId}_{DateTime.UtcNow:yyyyMMddHHmmss}{extension}";
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            FilePath = filePath,
            ContentType = "video/mp4"
        }, cancellationToken);

        return key;
    }

    public async Task<string> DownloadVideoToTempAsync(string r2Key, string tempDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(tempDirectory);
        var extension = Path.GetExtension(r2Key);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".mp4";

        var filePath = Path.Combine(tempDirectory, $"clip_source_{Guid.NewGuid():N}{extension}");
        using var response = await _s3.GetObjectAsync(_options.BucketName, r2Key, cancellationToken);
        await using var fs = File.Create(filePath);
        await response.ResponseStream.CopyToAsync(fs, cancellationToken);
        return filePath;
    }

    private static void ValidateOptions(CloudflareR2Options options)
    {
        if (string.IsNullOrWhiteSpace(options.BucketName) ||
            string.IsNullOrWhiteSpace(options.AccessKeyId) ||
            string.IsNullOrWhiteSpace(options.SecretAccessKey) ||
            (string.IsNullOrWhiteSpace(options.ServiceUrl) && string.IsNullOrWhiteSpace(options.AccountId)))
        {
            throw new InvalidOperationException(
                "Falta configurar CloudflareR2:AccountId/ServiceUrl, BucketName, AccessKeyId y SecretAccessKey.");
        }
    }
}
