using System.Security.Cryptography;
using System.Buffers.Binary;
using StbImageSharp;
using Catalog.Api.Application;

namespace Catalog.Api.Images;

public sealed class ImageUploadLimit : IDisposable
{
    public SemaphoreSlim Gate { get; } = new(2, 2);
    public void Dispose() => Gate.Dispose();
}
public sealed class ValidatedImage : IAsyncDisposable
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public FileStream Content { get; }
    public string ContentType { get; }
    public string Sha256 { get; }
    private ValidatedImage(FileStream content, string type, string hash) { Content = content; ContentType = type; Sha256 = hash; }
    public static async Task<ValidatedImage> ReadAsync(Stream source, CancellationToken ct)
    {
        var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose };
        if (OperatingSystem.IsLinux()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(Path.Combine(Path.GetTempPath(), "tourlab-image-" + Guid.NewGuid().ToString("N")), settings);
        try {
            var buffer = new byte[64 * 1024]; int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0) {
                if (stream.Length + read > MaximumBytes) throw new CatalogProblem(413, "ImageTooLarge");
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (stream.Length < 24) throw new CatalogProblem(400, "InvalidImage");
            stream.Position = 0; var signature = new byte[8]; await stream.ReadExactlyAsync(signature, ct);
            var png = signature.AsSpan().SequenceEqual(new byte[] {137,80,78,71,13,10,26,10});
            var jpeg = signature[0] == 255 && signature[1] == 216 && signature[2] == 255;
            if (!png && !jpeg) throw new CatalogProblem(415, "UnsupportedImage");
            if (png) await ValidatePngAsync(stream, ct);
            stream.Position = 0; var info = ImageInfo.FromStream(stream);
            if (info is null || info.Value.Width is < 1 or > 2048 || info.Value.Height is < 1 or > 2048)
                throw new CatalogProblem(400, "ImageDimensionsExceeded");
            stream.Position = 0; var decoded = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (decoded.Width != info.Value.Width || decoded.Height != info.Value.Height) throw new CatalogProblem(400, "InvalidImage");
            ct.ThrowIfCancellationRequested();
            stream.Position = stream.Length - (png ? 12 : 2);
            var ending = new byte[png ? 12 : 2]; await stream.ReadExactlyAsync(ending, ct);
            if (png ? !ending.AsSpan().SequenceEqual(new byte[] {0,0,0,0,73,69,78,68,174,66,96,130}) : ending[0] != 255 || ending[1] != 217)
                throw new CatalogProblem(400, "InvalidImageEnding");
            stream.Position = 0; var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)); stream.Position = 0;
            return new(stream, png ? "image/png" : "image/jpeg", hash);
        } catch (Exception e) when (e is not CatalogProblem && e is not OperationCanceledException && (e is not IOException || e is EndOfStreamException)) {
            await stream.DisposeAsync(); throw new CatalogProblem(400, "InvalidImage");
        } catch { await stream.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => Content.DisposeAsync();
    private static async Task ValidatePngAsync(FileStream stream, CancellationToken ct)
    {
        stream.Position = 8; var header = new byte[8]; var checksum = new byte[4]; var buffer = new byte[32 * 1024];
        var chunks = 0; var ihdr = false; var idat = false; var ended = false;
        while (stream.Position < stream.Length) {
            await stream.ReadExactlyAsync(header, ct); var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            if (++chunks > 2048 || length > stream.Length - stream.Position - 4 || type == "acTL"
                || chunks == 1 && (type != "IHDR" || length != 13) || type == "IHDR" && ihdr)
                throw new CatalogProblem(400, "InvalidPngStructure");
            if (type == "IHDR") ihdr = true;
            if (type == "IDAT") idat = true;
            uint crc = uint.MaxValue;
            foreach (var value in header.AsSpan(4).ToArray()) crc = CrcByte(crc, value);
            var remaining = (long)length;
            while (remaining > 0) {
                var count = (int)Math.Min(buffer.Length, remaining); await stream.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                for (var i = 0; i < count; i++) crc = CrcByte(crc, buffer[i]); remaining -= count;
            }
            await stream.ReadExactlyAsync(checksum, ct);
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(checksum)) throw new CatalogProblem(400, "InvalidPngChecksum");
            if (type == "IEND") { ended = length == 0 && stream.Position == stream.Length; break; }
        }
        if (!ihdr || !idat || !ended) throw new CatalogProblem(400, "InvalidPngStructure");
    }
    private static uint CrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0);
        return crc;
    }
}
