using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace Gateway.Web.Identity;

public static class AuthEndpoints
{
    public static void MapLabAuth(this WebApplication app)
    {
        app.MapPost("/auth/login", async (HttpContext context, IAntiforgery antiforgery,
            SignInManager<LabUser> signIn, UserManager<LabUser> users, CancellationToken ct) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(new { code = "InvalidAntiforgeryToken" }); }
            var form = await context.Request.ReadFormAsync(ct);
            var user = await users.FindByNameAsync(form["username"].ToString());
            if (user is null) return Results.LocalRedirect("/login?error=InvalidCredentials");
            var result = await signIn.CheckPasswordSignInAsync(user, form["password"].ToString(), lockoutOnFailure: true);
            if (!result.Succeeded) return Results.LocalRedirect("/login?error=InvalidCredentials");
            await signIn.SignInWithClaimsAsync(user, isPersistent: false,
                [new System.Security.Claims.Claim("sessionExpires", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString())]);
            return Results.LocalRedirect("/");
        }).AllowAnonymous().RequireRateLimiting("credentials");

        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery,
            SignInManager<LabUser> signIn, UserManager<LabUser> users) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(new { code = "InvalidAntiforgeryToken" }); }
            var user = await users.GetUserAsync(context.User);
            if (user is not null) await users.UpdateSecurityStampAsync(user);
            await signIn.SignOutAsync();
            return Results.LocalRedirect("/login");
        }).RequireAuthorization();

        app.MapGet("/auth/me", (HttpContext context) => Results.Ok(new
        {
            userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            name = context.User.Identity?.Name,
            roles = context.User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value)
        })).RequireAuthorization();

        // API-tool login accepts credentials; never accepts a caller-selected role or sub.
        app.MapPost("/auth/token", async (TokenLogin input, SignInManager<LabUser> signIn,
            UserManager<LabUser> users, JwtIssuer issuer, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (string.IsNullOrWhiteSpace(input.Username) || string.IsNullOrWhiteSpace(input.Password)) return Results.Unauthorized();
            var user = await users.FindByNameAsync(input.Username);
            if (user is null) return Results.Unauthorized();
            var result = await signIn.CheckPasswordSignInAsync(user, input.Password, lockoutOnFailure: true);
            if (!result.Succeeded) return Results.Unauthorized();
            return Results.Ok(new { accessToken = issuer.Issue(user.Id, user.UserName ?? "", await users.GetRolesAsync(user)),
                tokenType = "Bearer", expiresIn = 900 });
        }).AllowAnonymous().RequireRateLimiting("credentials");
    }
}

public sealed record TokenLogin(string? Username, string? Password);
