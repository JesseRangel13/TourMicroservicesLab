using System.Net;
using System.Net.Http.Headers;
using Amazon.S3.Model;
using Catalog.Api.Application;
using Catalog.Api.Images;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.Contracts;

namespace Lab.Tests;

[Collection("Local lab")]
public sealed class ImageExtensionTests
{
    internal static byte[] Png => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    [Fact]
    public async Task ContentDecodingRejectsUnsupportedCorruptAndOversizedBodies()
    {
        await using var valid = await ValidatedImage.ReadAsync(new MemoryStream(Png), default);
        Assert.Equal("image/png", valid.ContentType);
        await using var jpeg = await ValidatedImage.ReadAsync(new MemoryStream(ImageFixtures.Jpeg), default);
        Assert.Equal("image/jpeg", jpeg.ContentType);
        var dimensions = ImageFixtures.Jpeg;
        for (var i = 0; i < dimensions.Length - 8; i++) {
            if (dimensions[i] == 255 && dimensions[i + 1] is 0xc0 or 0xc2) { dimensions[i + 7] = 8; dimensions[i + 8] = 1; break; }
        }
        Assert.Equal("ImageDimensionsExceeded", (await Assert.ThrowsAsync<CatalogProblem>(() => ValidatedImage.ReadAsync(new MemoryStream(dimensions), default))).Code);
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>bad</script></svg>");
        Assert.Equal(415, (await Assert.ThrowsAsync<CatalogProblem>(() => ValidatedImage.ReadAsync(new MemoryStream(svg), default))).Status);
        var corrupt = Png; corrupt[40] ^= 1;
        Assert.Equal(400, (await Assert.ThrowsAsync<CatalogProblem>(() => ValidatedImage.ReadAsync(new MemoryStream(corrupt), default))).Status);
        Assert.Equal(413, (await Assert.ThrowsAsync<CatalogProblem>(() => ValidatedImage.ReadAsync(new MemoryStream(new byte[ValidatedImage.MaximumBytes + 1]), default))).Status);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ValidatedImage.ReadAsync(new MemoryStream(Png), cancelled.Token));
    }
    [ExtensionFact]
    public async Task UploadIsPrivateBoundedIdempotentAndEnforcesOwnedTourIds()
    {
        await using var db = ReservationTestSupport.Catalog(); var session = await ReservationTestSupport.SessionAsync();
        var o = new ImageStorageOptions { Local = true, Endpoint = "http://localhost:9000", Bucket = "tourlab-test-" + Guid.NewGuid().ToString("N") };
        using var initializer = S3ImageStorage.Create(o, o.Endpoint); await initializer.PutBucketAsync(new PutBucketRequest { BucketName = o.Bucket });
        using var storage = new S3ImageStorage(Options.Create(o)); using var limit = new ImageUploadLimit();
        var handler = new TourImages(db, storage, new Clock(), limit); var id = Guid.NewGuid();
        await handler.UploadAsync(session.TourId, id, new MemoryStream(Png), default);
        var duplicate = await handler.UploadAsync(session.TourId, id, new MemoryStream(Png), default);
        Assert.Equal("Ready", duplicate.Status); Assert.Equal(1, await db.Images.CountAsync(x => x.TourId == session.TourId));
        Assert.Equal(409, (await Assert.ThrowsAsync<CatalogProblem>(() => handler.UploadAsync(session.TourId, id, new MemoryStream(ImageFixtures.Jpeg), default))).Status);
        var visible = Assert.Single(await handler.ListAsync(session.TourId, false, false, default)); Assert.NotNull(visible.Url);
        using var anonymous = new HttpClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync($"http://localhost:9000/{o.Bucket}/tours/{session.TourId:N}/{id:N}")).StatusCode);
        using var read = await anonymous.GetAsync(visible.Url); Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Equal(Png, await read.Content.ReadAsByteArrayAsync());
        await handler.UploadAsync(session.TourId, Guid.NewGuid(), new MemoryStream(ImageFixtures.Jpeg), default);
        await handler.UploadAsync(session.TourId, Guid.NewGuid(), new MemoryStream(Png), default);
        Assert.Equal(409, (await Assert.ThrowsAsync<CatalogProblem>(() => handler.UploadAsync(session.TourId, Guid.NewGuid(), new MemoryStream(Png), default))).Status);
        var other = await ReservationTestSupport.SessionAsync();
        Assert.Equal(404, (await Assert.ThrowsAsync<CatalogProblem>(() => handler.DeleteAsync(other.TourId, id, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<CatalogProblem>(() => handler.UploadAsync(other.TourId, id, new MemoryStream(Png), default))).Status);
    }
    [ExtensionFact]
    public async Task StorageSuccessThenMetadataFailureAndDeletionFailureRecoverFromOwnedIntents()
    {
        await using var db = ReservationTestSupport.Catalog(); var session = await ReservationTestSupport.SessionAsync();
        var o = new ImageStorageOptions { Local = true, Endpoint = "http://localhost:9000", Bucket = "tourlab-test-" + Guid.NewGuid().ToString("N") };
        using var initializer = S3ImageStorage.Create(o, o.Endpoint); await initializer.PutBucketAsync(new PutBucketRequest { BucketName = o.Bucket });
        using var actual = new S3ImageStorage(Options.Create(o)); var boundary = new FaultStorage(actual); using var limit = new ImageUploadLimit();
        var clock = new Clock(); var handler = new TourImages(db, boundary, clock, limit); var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.UploadAsync(session.TourId, id, new MemoryStream(Png), default,
            _ => throw new InvalidOperationException("Injected after object success, before metadata commit")));
        db.ChangeTracker.Clear(); Assert.Equal("Pending", (await db.Images.SingleAsync(x => x.Id == id)).Status);
        clock.Advance(TimeSpan.FromMinutes(3)); Assert.True(await handler.ReconcileAsync(default, id));
        db.ChangeTracker.Clear(); Assert.Equal("Ready", (await db.Images.SingleAsync(x => x.Id == id)).Status);
        await handler.DeleteAsync(session.TourId, id, default); await handler.DeleteAsync(session.TourId, id, default);
        Assert.Empty(await handler.ListAsync(session.TourId, false, false, default));
        clock.Advance(TimeSpan.FromMinutes(3)); boundary.DeleteFails = true;
        await Assert.ThrowsAsync<IOException>(() => handler.ReconcileAsync(default, id));
        boundary.DeleteFails = false; clock.Advance(TimeSpan.FromMinutes(3)); Assert.True(await handler.ReconcileAsync(default, id));
        db.ChangeTracker.Clear(); var row = await db.Images.SingleAsync(x => x.Id == id); Assert.Equal("Deleted", row.Status);
        Assert.Null(await actual.HeadAsync(row.ObjectKey, default));
        await handler.DeleteAsync(session.TourId, id, default); Assert.False(await handler.ReconcileAsync(default, id));
        // Repair an uncertain late PUT without resurrecting metadata or scanning the bucket.
        await actual.PutAsync(row.ObjectKey, new MemoryStream(Png), "image/png", row.Sha256, default);
        clock.Advance(TimeSpan.FromHours(2)); Assert.True(await handler.ReconcileAsync(default, id));
        Assert.Null(await actual.HeadAsync(row.ObjectKey, default));
    }
    [ExtensionFact]
    public async Task FailureBeforeStorageAndAbandonedPendingCleanupDoNotDisplayOrPurgeUnrelatedObjects()
    {
        await using var db = ReservationTestSupport.Catalog(); var session = await ReservationTestSupport.SessionAsync();
        var o = new ImageStorageOptions { Local = true, Endpoint = "http://localhost:9000", Bucket = "tourlab-test-" + Guid.NewGuid().ToString("N") };
        using var initializer = S3ImageStorage.Create(o, o.Endpoint); await initializer.PutBucketAsync(new PutBucketRequest { BucketName = o.Bucket });
        using var actual = new S3ImageStorage(Options.Create(o)); var boundary = new FaultStorage(actual) { PutFails = true }; using var limit = new ImageUploadLimit();
        var clock = new Clock(); var handler = new TourImages(db, boundary, clock, limit); var id = Guid.NewGuid();
        await actual.PutAsync("unrelated-test-marker", new MemoryStream(Png), "image/png", "marker", default);
        await Assert.ThrowsAsync<IOException>(() => handler.UploadAsync(session.TourId, id, new MemoryStream(Png), default));
        Assert.Empty(await handler.ListAsync(session.TourId, false, false, default));
        clock.Advance(TimeSpan.FromMinutes(20)); Assert.True(await handler.ReconcileAsync(default, id));
        clock.Advance(TimeSpan.FromMinutes(3)); Assert.True(await handler.ReconcileAsync(default, id));
        Assert.NotNull(await actual.HeadAsync("unrelated-test-marker", default));
    }
    [ExtensionFact]
    public async Task HttpAdminAuthorizationAndArbitraryIdsAreEnforced()
    {
        using var client = IntegrationTests.CreateClient(); var session = await ReservationTestSupport.SessionAsync();
        var alice = await ReservationTestSupport.IdentityAsync("Alice");
        using var upload = new HttpRequestMessage(HttpMethod.Put, $"https://localhost:8444/v1/admin/tours/{session.TourId}/images/{Guid.NewGuid()}") { Content = new ByteArrayContent(Png) };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alice.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(upload)).StatusCode);
        using var anonymous = await client.GetAsync($"https://localhost:8444/v1/tours/{session.TourId}/images"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:8444/v1/images?key=arbitrary"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alice.Token);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
        var admin = await ReservationTestSupport.IdentityAsync("Admin");
        using var invalid = new HttpRequestMessage(HttpMethod.Put, $"https://localhost:8444/v1/admin/tours/{session.TourId}/images/{Guid.NewGuid()}") { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("<svg><script>unsupported image content</script></svg>")) };
        invalid.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.Token);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.SendAsync(invalid)).StatusCode);
        using var large = new HttpRequestMessage(HttpMethod.Put, $"https://localhost:8444/v1/admin/tours/{session.TourId}/images/{Guid.NewGuid()}") { Content = new ByteArrayContent(new byte[ValidatedImage.MaximumBytes + 1]) };
        large.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(large)).StatusCode);
    }
    [ExtensionFact]
    public async Task DeletionBetweenObjectSuccessAndMetadataCommitCannotResurrectReadyImage()
    {
        await using var db = ReservationTestSupport.Catalog(); await using var deleting = ReservationTestSupport.Catalog();
        var session = await ReservationTestSupport.SessionAsync(); var o = new ImageStorageOptions { Local = true, Endpoint = "http://localhost:9000", Bucket = "tourlab-test-" + Guid.NewGuid().ToString("N") };
        using var initializer = S3ImageStorage.Create(o, o.Endpoint); await initializer.PutBucketAsync(new PutBucketRequest { BucketName = o.Bucket });
        using var actual = new S3ImageStorage(Options.Create(o)); using var limit = new ImageUploadLimit(); var clock = new Clock();
        var uploader = new TourImages(db, actual, clock, limit); var deleter = new TourImages(deleting, actual, clock, limit); var id = Guid.NewGuid();
        var result = await uploader.UploadAsync(session.TourId, id, new MemoryStream(Png), default, ct => deleter.DeleteAsync(session.TourId, id, ct));
        Assert.Equal("DeletePending", result.Status); Assert.Empty(await uploader.ListAsync(session.TourId, false, false, default));
        Assert.False(await deleter.ReconcileAsync(default, id)); // upload lease protects against a late PUT racing deletion
        clock.Advance(TimeSpan.FromMinutes(3)); Assert.True(await deleter.ReconcileAsync(default, id));
        Assert.Null(await actual.HeadAsync($"tours/{session.TourId:N}/{id:N}", default));
    }
    internal sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
    private sealed class FaultStorage(IImageStorage actual) : IImageStorage
    {
        public bool PutFails { get; set; }
        public bool DeleteFails { get; set; }
        public Task PutAsync(string key, Stream body, string type, string hash, CancellationToken ct) => PutFails ? throw new IOException("Injected storage outage") : actual.PutAsync(key, body, type, hash, ct);
        public Task DeleteAsync(string key, CancellationToken ct) => DeleteFails ? throw new IOException("Injected delete outage") : actual.DeleteAsync(key, ct);
        public Task<StoredImage?> HeadAsync(string key, CancellationToken ct) => actual.HeadAsync(key, ct);
        public Task<string> ReadUrlAsync(string key, CancellationToken ct) => actual.ReadUrlAsync(key, ct);
    }
}
