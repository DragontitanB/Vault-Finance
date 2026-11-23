using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace VaultFinance.IntegrationTests.Authentication;

public sealed class AuthenticationSecurityTests
{
    [Theory]
    [InlineData("register", false)]
    [InlineData("login", false)]
    [InlineData("logout", false)]
    [InlineData("register", true)]
    [InlineData("login", true)]
    [InlineData("logout", true)]
    public async Task Mutations_RejectMissingOrInvalidCsrfBeforeResolvingPersistence(string route, bool invalidToken)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        if (invalidToken)
        {
            await client.GetAsync("/api/v1/auth/csrf");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid-token");
        }

        using var response = await client.PostAsJsonAsync($"/api/v1/auth/{route}", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_csrf_token", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Csrf_UsesSecureCookieNoCacheAndNoCrossOriginCredentials()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example.invalid");

        using var response = await client.GetAsync("/api/v1/auth/csrf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Select(value => SetCookieHeaderValue.Parse(value)).Single(value => value.Name == "__Host-VaultFinance.Antiforgery");
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal("Strict", cookie.SameSite.ToString());
        Assert.Equal("/", cookie.Path.Value);
        Assert.False(cookie.Domain.HasValue);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(!string.IsNullOrWhiteSpace(body.RootElement.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task Csrf_RequestTokenWithoutMatchingBrowserCookieIsRejected()
    {
        using var factory = CreateFactory();
        using var first = CreateClient(factory);
        using var second = CreateClient(factory);
        using var tokenResponse = await first.GetAsync("/api/v1/auth/csrf");
        using var body = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        second.DefaultRequestHeaders.Add("X-CSRF-TOKEN", body.RootElement.GetProperty("token").GetString());

        using var response = await second.PostAsJsonAsync("/api/v1/auth/register", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithoutSession_ReturnsProblem401WithoutDatabase()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("login", 5)]
    [InlineData("register", 3)]
    public async Task AuthRoutes_RateLimitBeforeCsrfOrDatabaseAccess(string route, int permits)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        for (var attempt = 0; attempt < permits; attempt++)
        {
            using var allowed = await client.PostAsJsonAsync($"/api/v1/auth/{route}", new { });
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        using var rejected = await client.PostAsJsonAsync($"/api/v1/auth/{route}", new { });

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("/register")]
    [InlineData("/login")]
    [InlineData("/api/v1/auth/manage/info")]
    public async Task DefaultIdentityRoutes_AreNotExposed(string path)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services => services.AddDataProtection().UseEphemeralDataProtectionProvider());
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = null
                }));
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
}
