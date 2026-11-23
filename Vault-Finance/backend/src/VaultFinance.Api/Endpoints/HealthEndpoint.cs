using Microsoft.AspNetCore.Http.HttpResults;

namespace VaultFinance.Api.Endpoints;

public static class HealthEndpoint
{
    public static Ok<HealthResponse> Handle() => TypedResults.Ok(new HealthResponse("ok"));
}

public sealed record HealthResponse(string Status);
