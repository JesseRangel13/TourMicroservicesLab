using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;

namespace Gateway.Web.Identity;

public sealed class LabRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<LabUser>>();
        var user = await manager.GetUserAsync(state.User);
        ct.ThrowIfCancellationRequested();
        if (user is null || !long.TryParse(state.User.FindFirst("sessionExpires")?.Value, out var expires)
            || expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        return state.User.FindFirst("AspNet.Identity.SecurityStamp")?.Value == await manager.GetSecurityStampAsync(user);
    }
}
