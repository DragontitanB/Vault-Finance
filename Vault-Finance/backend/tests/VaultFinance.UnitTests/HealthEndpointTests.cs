using Microsoft.AspNetCore.Http;
using VaultFinance.Api.Endpoints;

namespace VaultFinance.UnitTests;

public sealed class HealthEndpointTests
{
    [Fact]
    public void Handle_ReturnsOkWithProcessLivenessStatus()
    {
        var result = HealthEndpoint.Handle();

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.NotNull(result.Value);
        Assert.Equal("ok", result.Value.Status);
    }
}
