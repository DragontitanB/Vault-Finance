using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VaultFinance.IntegrationTests;

public sealed class HealthEndpointTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task GetHealth_WithoutCredentials_ReturnsProcessLiveness(string environment)
    {
        using var factory = CreateFactory(environment);
        using var client = CreateHttpsClient(factory);

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var status = Assert.Single(body.RootElement.EnumerateObject());
        Assert.Equal("status", status.Name);
        Assert.Equal("ok", status.Value.GetString());
    }

    [Theory]
    [InlineData("Development", HttpStatusCode.OK)]
    [InlineData("Production", HttpStatusCode.NotFound)]
    public async Task GetOpenApi_IsAvailableOnlyInDevelopment(string environment, HttpStatusCode expected)
    {
        using var factory = CreateFactory(environment);
        using var client = CreateHttpsClient(factory);

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(expected, response.StatusCode);

        if (response.IsSuccessStatusCode)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("paths").TryGetProperty("/health", out _));
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment(environment));

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
}
