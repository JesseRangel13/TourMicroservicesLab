using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Catalog.Api.Images;

public sealed class ImageStorageOptions
{
    public bool Enabled { get; set; }
    public bool Local { get; set; } = true;
    public string Bucket { get; set; } = "tourlab-images";
    public string Region { get; set; } = "us-east-1";
    public string Endpoint { get; set; } = "http://s3:9000";
    public string BrowserEndpoint { get; set; } = "http://localhost:9000";
}
public sealed record StoredImage(long Size, string? Sha256);
public interface IImageStorage
{
    Task PutAsync(string key, Stream content, string contentType, string sha256, CancellationToken ct);
    Task<StoredImage?> HeadAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task<string> ReadUrlAsync(string key, CancellationToken ct);
}
public sealed class S3ImageStorage : IImageStorage, IDisposable
{
    private readonly ImageStorageOptions options;
    private readonly IAmazonS3 client;
    private readonly IAmazonS3 browser;
    public S3ImageStorage(IOptions<ImageStorageOptions> value)
    {
        options = value.Value;
        client = Create(options, options.Endpoint);
        browser = options.Local ? Create(options, options.BrowserEndpoint) : client;
    }
    public static IAmazonS3 Create(ImageStorageOptions o, string endpoint)
    {
        var config = new AmazonS3Config { MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(15), RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region) };
        if (!o.Local) return new AmazonS3Client(config);
        config.ServiceURL = endpoint; config.ForcePathStyle = true; config.AuthenticationRegion = o.Region;
        return new AmazonS3Client(new BasicAWSCredentials("local-simulation", "local-simulation"), config);
    }
    public async Task PutAsync(string key, Stream content, string contentType, string sha256, CancellationToken ct)
    {
        var request = new PutObjectRequest { BucketName = options.Bucket, Key = key, InputStream = content,
            ContentType = contentType, AutoCloseStream = false, UseChunkEncoding = false };
        request.Metadata.Add("sha256", sha256);
        await client.PutObjectAsync(request, ct);
    }
    public async Task<StoredImage?> HeadAsync(string key, CancellationToken ct)
    {
        try {
            var result = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = options.Bucket, Key = key }, ct);
            return new(result.ContentLength, result.Metadata["x-amz-meta-sha256"]);
        } catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }
    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = options.Bucket, Key = key }, ct);
    public Task<string> ReadUrlAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return browser.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = options.Bucket, Key = key,
            Verb = HttpVerb.GET, Protocol = options.Local ? Protocol.HTTP : Protocol.HTTPS, Expires = DateTime.UtcNow.AddSeconds(60) });
    }
    public void Dispose() { if (!ReferenceEquals(client, browser)) browser.Dispose(); client.Dispose(); }
}
