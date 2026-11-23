using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace VaultFinance.IntegrationTests.Accounts;

public sealed class AccountAuthorizationTests
{
    [Theory]
    [InlineData("POST", "/api/v1/accounts")]
    [InlineData("GET", "/api/v1/accounts")]
    [InlineData("GET", "/api/v1/accounts/00000000-0000-0000-0000-000000000001")]
    [InlineData("PATCH", "/api/v1/accounts/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/v1/accounts/00000000-0000-0000-0000-000000000001/archive")]
    public async Task AnonymousRequests_Return401WithoutResolvingPersistence(string method, string path)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services => services.AddDataProtection().UseEphemeralDataProtectionProvider());
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = null
                }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PATCH")
        {
            request.Content = JsonContent.Create(new { name = "Test account", currency = "USD" });
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
