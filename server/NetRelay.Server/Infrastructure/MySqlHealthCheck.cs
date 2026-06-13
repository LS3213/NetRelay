using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRelay.Server.Data;

namespace NetRelay.Server.Infrastructure;

public sealed class MySqlHealthCheck(NetRelayDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("MySQL connection failed.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("MySQL connection failed.", exception);
        }
    }
}
