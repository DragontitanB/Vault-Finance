using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using VaultFinance.Infrastructure.Identity;
using VaultFinance.Infrastructure.Persistence;
using VaultFinance.IntegrationTests.Persistence;

namespace VaultFinance.IntegrationTests.Authentication;

public sealed class AuthenticationApiTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    private readonly string _email = $"auth-{Guid.NewGuid():N}@example.invalid";
    private readonly string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
    private SqlConnection? _connection;
    private SqlTransaction? _transaction;
    private AuthenticationApiFactory? _factory;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("VAULTFINANCE_SQL_TESTS") != "1") { return; }
        await using var scope = fixture.Provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        _connection = new SqlConnection(database.Database.GetConnectionString());
        await _connection.OpenAsync();
        _transaction = (SqlTransaction)await _connection.BeginTransactionAsync();
        _factory = new AuthenticationApiFactory(_connection, _transaction);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) { await _factory.DisposeAsync(); }
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
        }
        if (_connection is not null) { await _connection.DisposeAsync(); }
    }

    [SqlServerFact]
    public async Task Register_NormalizesEmailAndStoresOnlyIdentityPasswordHashWithoutLoggingIn()
    {
        await RefreshCsrfAsync();
        using var response = await RegisterAsync($"  {_email}  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        await AssertMinimalUserAsync(response);
        await using var scope = _factory!.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await manager.FindByEmailAsync(_email);
        Assert.NotNull(user);
        Assert.Equal(manager.NormalizeEmail(_email), user.NormalizedEmail);
        Assert.Equal(user.NormalizedEmail, user.NormalizedUserName);
        Assert.True(await manager.CheckPasswordAsync(user, _password));
        Assert.False(user.PasswordHash == _password);
        using var me = await _client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [SqlServerFact]
    public async Task Register_DuplicateEmailWithDifferentCase_ReturnsConsistentConflict()
    {
        await RefreshCsrfAsync();
        using var first = await RegisterAsync(_email);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await RegisterAsync(_email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("duplicate_email", body.RootElement.GetProperty("code").GetString());
    }

    [SqlServerTheory]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    public async Task Register_RejectsInvalidPassword(string password)
    {
        await RefreshCsrfAsync();

        using var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email = _email, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("password", out _));
    }

    [SqlServerTheory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task Register_RejectsInvalidEmail(string email)
    {
        await RefreshCsrfAsync();

        using var response = await RegisterAsync(email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SqlServerFact]
    public async Task Login_EmitsSecureSessionAndMeReturnsOnlyAllowedData()
    {
        await RegisterAndLoginAsync();

        using var me = await _client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.True(me.Headers.CacheControl?.NoStore);
        await AssertMinimalUserAsync(me);
    }

    [SqlServerFact]
    public async Task Login_UnknownUserAndWrongPasswordHaveSameResponse()
    {
        await RefreshCsrfAsync();
        using var created = await RegisterAsync(_email);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var unknown = await LoginAsync($"missing-{Guid.NewGuid():N}@example.invalid", _password);
        using var wrong = await LoginAsync(_email, Guid.NewGuid().ToString("N"));

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var first = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        using var second = JsonDocument.Parse(await wrong.Content.ReadAsStringAsync());
        Assert.Equal(first.RootElement.GetProperty("title").GetString(), second.RootElement.GetProperty("title").GetString());
        Assert.Equal("invalid_credentials", second.RootElement.GetProperty("code").GetString());
    }

    [SqlServerFact]
    public async Task Login_FailuresCauseTemporaryLockoutAndSuccessfulLoginResumesAfterExpiry()
    {
        await RefreshCsrfAsync();
        using var created = await RegisterAsync(_email);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            SetClientIp($"192.0.2.{attempt + 1}");
            using var failed = await LoginAsync(_email, Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        SetClientIp("192.0.2.20");
        using var locked = await LoginAsync(_email, _password);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await manager.FindByEmailAsync(_email))!;
            Assert.True(await manager.IsLockedOutAsync(user));
            Assert.True(user.LockoutEnd > DateTimeOffset.UtcNow);
            Assert.True((await manager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(-1))).Succeeded);
        }

        using var resumed = await LoginAsync(_email, _password);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
    }

    [SqlServerFact]
    public async Task Logout_RequiresFreshIdentityBoundCsrfAndInvalidatesBrowserSession()
    {
        var anonymousToken = await RefreshCsrfAsync();
        using var registered = await RegisterAsync(_email);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        using var loggedIn = await LoginAsync(_email, _password);
        Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);

        _client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        _client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", anonymousToken);
        using var stale = await _client.PostAsJsonAsync("/api/v1/auth/logout", new { });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);

        var authenticatedToken = await RefreshCsrfAsync();
        using var loggedOut = await _client.PostAsJsonAsync("/api/v1/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        using var me = await _client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);

        _client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        _client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", authenticatedToken);
        using var staleAfterLogout = await LoginAsync(_email, _password);
        Assert.Equal(HttpStatusCode.BadRequest, staleAfterLogout.StatusCode);
        await RefreshCsrfAsync();
        using var again = await LoginAsync(_email, _password);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [SqlServerFact]
    public async Task Csrf_AnonymousCookieAndTokenCannotBeReplayedAfterAuthentication()
    {
        using var bootstrap = await _client.GetAsync("/api/v1/auth/csrf");
        var originalCookie = bootstrap.Headers.GetValues("Set-Cookie")
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(value => value.Name == "__Host-VaultFinance.Antiforgery");
        using var body = JsonDocument.Parse(await bootstrap.Content.ReadAsStringAsync());
        var token = body.RootElement.GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        using var registered = await RegisterAsync(_email);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        using var login = await LoginAsync(_email, _password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var authCookie = login.Headers.GetValues("Set-Cookie").Select(value => SetCookieHeaderValue.Parse(value))
            .Single(value => value.Name == "__Host-VaultFinance");
        using var replay = _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        replay.DefaultRequestHeaders.Add("Cookie",
            $"{authCookie.Name}={authCookie.Value}; {originalCookie.Name}={originalCookie.Value}");
        replay.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        using var rejected = await replay.PostAsJsonAsync("/api/v1/auth/logout", new { });

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var stillAuthenticated = await _client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, stillAuthenticated.StatusCode);
    }

    [SqlServerFact]
    public async Task Login_RejectsMissingCsrfWithoutIncrementingFailedAttempts()
    {
        await RegisterAndLoginAsync();
        await RefreshCsrfAsync();
        _client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        using var rejected = await LoginAsync(_email, Guid.NewGuid().ToString("N"));

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        await using var scope = _factory!.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await manager.FindByEmailAsync(_email))!;
        Assert.Equal(0, user.AccessFailedCount);
    }

    private async Task RegisterAndLoginAsync()
    {
        await RefreshCsrfAsync();
        using var registered = await RegisterAsync(_email);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        using var login = await LoginAsync(_email.ToUpperInvariant(), _password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Select(value => SetCookieHeaderValue.Parse(value))
            .Single(value => value.Name == "__Host-VaultFinance");
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal("Lax", cookie.SameSite.ToString());
        Assert.Equal("/", cookie.Path.Value);
        Assert.False(cookie.Domain.HasValue);
        Assert.False(cookie.Expires.HasValue);
    }

    private Task<HttpResponseMessage> RegisterAsync(string email) =>
        _client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = _password });

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

    private async Task<string> RefreshCsrfAsync()
    {
        using var response = await _client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = body.RootElement.GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        _client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        return token;
    }

    private async Task AssertMinimalUserAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "email", "id" }, body.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.True(Guid.TryParse(body.RootElement.GetProperty("id").GetString(), out _));
        Assert.Equal(_email, body.RootElement.GetProperty("email").GetString());
    }

    private void SetClientIp(string ip)
    {
        _client.DefaultRequestHeaders.Remove("X-Test-Client-IP");
        _client.DefaultRequestHeaders.Add("X-Test-Client-IP", ip);
    }
}
