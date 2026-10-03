using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Web.Identity;

public static class IdentityRegistration
{
    public static void AddLabIdentity(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<IdentityDb>(o => o.UseNpgsql(connectionString,
            pg => pg.MigrationsHistoryTable("__EFMigrationsHistory", "identity")));
        // Factory also registers a scoped context; stores are used only in short HTTP/tool scopes.
        services.AddIdentity<LabUser, IdentityRole>(o =>
        {
            o.Password.RequiredLength = 12;
            o.User.RequireUniqueEmail = true;
            o.Lockout.MaxFailedAccessAttempts = 5;
            o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        }).AddEntityFrameworkStores<IdentityDb>().AddDefaultTokenProviders();
        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "__Host-tourlab";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            o.SlidingExpiration = false;
            o.LoginPath = "/login";
            o.AccessDeniedPath = "/access-denied";
        });
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
    }
}
