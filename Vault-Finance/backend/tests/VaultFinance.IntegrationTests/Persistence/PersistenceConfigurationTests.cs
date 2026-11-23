using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.ValueObjects;
using VaultFinance.Infrastructure;
using VaultFinance.Infrastructure.Identity;
using VaultFinance.Infrastructure.Persistence;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed class PersistenceConfigurationTests
{
    [Fact]
    public async Task Health_RemainsAvailableWithoutAConnectionStringEvenWithAnAuthenticationCookie()
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

        using var response = await client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());

        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
            IdentityConstants.ApplicationScheme));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(15)
        }, IdentityConstants.ApplicationScheme);
        var cookie = options.TicketDataFormat.Protect(ticket);
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={cookie}");

        using var authenticatedRequest = await client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, authenticatedRequest.StatusCode);
    }

    [Fact]
    public void Model_MapsAccountAndIdentityWithoutInventingMoneyStorage()
    {
        using var provider = CreateProvider("VaultFinanceDb");
        using var scope = provider.CreateScope();
        using var context = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        var account = context.Model.FindEntityType(typeof(Account))!;

        Assert.Equal("Accounts", account.GetTableName());
        Assert.Equal(Account.MaximumNameLength, account.FindProperty(nameof(Account.Name))!.GetMaxLength());
        Assert.Equal("varchar(3)", account.FindProperty(nameof(Account.Currency))!.GetColumnType());
        Assert.Equal(PropertySaveBehavior.Throw, account.FindProperty(nameof(Account.Currency))!.GetAfterSaveBehavior());
        Assert.Equal(PropertySaveBehavior.Throw, account.FindProperty(nameof(Account.UserId))!.GetAfterSaveBehavior());
        var ownership = Assert.Single(account.GetForeignKeys());
        Assert.Equal(typeof(ApplicationUser), ownership.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, ownership.DeleteBehavior);
        Assert.Equal(typeof(Guid), context.Model.FindEntityType(typeof(ApplicationUser))!.FindPrimaryKey()!.Properties.Single().ClrType);
        Assert.Null(context.Model.FindEntityType(typeof(Money)));
    }

    [Fact]
    public void Identity_UsesSecureCookiesAndExplicitAntiforgeryHeader()
    {
        using var provider = CreateProvider("VaultFinanceDb");
        var cookie = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var antiforgery = provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;

        Assert.True(cookie.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Lax, cookie.Cookie.SameSite);
        Assert.Equal("__Host-VaultFinance", cookie.Cookie.Name);
        Assert.Equal("/", cookie.Cookie.Path);
        Assert.Null(cookie.Cookie.Domain);
        Assert.Equal(TimeSpan.FromMinutes(30), cookie.ExpireTimeSpan);
        Assert.Equal("X-CSRF-TOKEN", antiforgery.HeaderName);
        Assert.True(antiforgery.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, antiforgery.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, antiforgery.Cookie.SameSite);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("OtherDatabase")]
    public void Persistence_RejectsAnyOtherDatabaseBeforeOpeningConnection(string database)
    {
        using var provider = CreateProvider(database);
        using var scope = provider.CreateScope();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>());

        Assert.Contains("must target the VaultFinanceDb database", exception.Message);
    }

    [Fact]
    public void MissingConnection_DoesNotPreventRegistrationButFailsWhenPersistenceIsRequested()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>());

        Assert.Contains("DefaultConnection is required", exception.Message);
    }

    private static ServiceProvider CreateProvider(string database)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] =
                $"Server=configuration-only.invalid;Database={database};Integrated Security=True;Encrypt=True"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
