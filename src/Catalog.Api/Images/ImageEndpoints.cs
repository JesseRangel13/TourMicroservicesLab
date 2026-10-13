using Amazon.S3;
using Catalog.Api.Application;
using Shared.Contracts;

namespace Catalog.Api.Images;

public static class ImageEndpoints
{
    public static void AddTourImages(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ImageStorageOptions>().Bind(configuration.GetSection("ImageStorage"))
            .Validate(o => !o.Enabled || o.Bucket.StartsWith("tourlab-", StringComparison.Ordinal) &&
                (!o.Local || Uri.TryCreate(o.Endpoint, UriKind.Absolute, out _) && Uri.TryCreate(o.BrowserEndpoint, UriKind.Absolute, out _)), "Owned image bucket/local endpoints required").ValidateOnStart();
        services.AddSingleton<ImageUploadLimit>(); services.AddSingleton<IImageStorage, S3ImageStorage>();
        services.AddScoped<TourImages>(); services.AddHostedService<ImageRecoveryWorker>();
    }
    public static void MapTourImages(this WebApplication app)
    {
        var api = app.MapGroup("/v1").AddEndpointFilter(async (ctx, next) => {
            try { return await next(ctx); }
            catch (CatalogProblem e) { return Results.Problem(statusCode: e.Status, title: e.Code, extensions: new Dictionary<string, object?> { ["code"] = e.Code }); }
            catch (AmazonS3Exception) { return Results.Problem(statusCode: 503, title: "ImageStorageUnavailable"); }
        });
        api.MapGet("/tours/{tour:guid}/images", (Guid tour, HttpContext ctx, TourImages handler, CancellationToken ct) =>
            handler.ListAsync(tour, ctx.User.IsInRole("Admin"), false, ct)).RequireAuthorization("Api");
        api.MapGet("/admin/tours/{tour:guid}/images", (Guid tour, TourImages handler, CancellationToken ct) =>
            handler.ListAsync(tour, true, true, ct)).RequireAuthorization("AdminApi");
        api.MapPut("/admin/tours/{tour:guid}/images/{id:guid}", async (Guid tour, Guid id, HttpContext ctx, TourImages handler, CancellationToken ct) => {
            if (ctx.Request.ContentLength > ValidatedImage.MaximumBytes) return Results.StatusCode(413);
            return Results.Ok(await handler.UploadAsync(tour, id, ctx.Request.Body, ct));
        }).RequireAuthorization("AdminApi").RequireRateLimiting("catalog-lab-admin");
        api.MapDelete("/admin/tours/{tour:guid}/images/{id:guid}", async (Guid tour, Guid id, TourImages handler, CancellationToken ct) => {
            await handler.DeleteAsync(tour, id, ct); return Results.Accepted();
        }).RequireAuthorization("AdminApi");
    }
}
