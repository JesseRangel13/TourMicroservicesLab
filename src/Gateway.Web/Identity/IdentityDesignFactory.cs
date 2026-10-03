using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Gateway.Web.Identity;
public sealed class IdentityDesignFactory : IDesignTimeDbContextFactory<IdentityDb>
{
    public IdentityDb CreateDbContext(string[] args)
    {
        // Offline model generation; no credentials and never opened.
        var options = new DbContextOptionsBuilder<IdentityDb>().UseNpgsql("Host=localhost;Database=tourlab",
            pg => pg.MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options;
        return new IdentityDb(options);
    }
}
