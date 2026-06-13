using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NetRelay.Server.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NetRelayDbContext>
{
    public NetRelayDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NETRELAY_MIGRATION_CONNECTION") ??
            "Server=localhost;Port=3306;Database=netrelay;User=netrelay;Password=design-time-only";
        var options = new DbContextOptionsBuilder<NetRelayDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0)))
            .Options;
        return new NetRelayDbContext(options);
    }
}
