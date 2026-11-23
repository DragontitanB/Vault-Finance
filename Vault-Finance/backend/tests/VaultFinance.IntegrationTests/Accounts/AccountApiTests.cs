using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VaultFinance.Application.Accounts;
using VaultFinance.Domain.Accounts;
using VaultFinance.Infrastructure.Identity;
using VaultFinance.Infrastructure.Persistence;
using VaultFinance.IntegrationTests.Authentication;
using VaultFinance.IntegrationTests.Persistence;

namespace VaultFinance.IntegrationTests.Accounts;

public sealed class AccountApiTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    private readonly Guid _ownerA = Guid.NewGuid();
    private readonly Guid _ownerB = Guid.NewGuid();
    private SqlConnection? _connection;
    private SqlTransaction? _transaction;
    private AuthenticationApiFactory? _factory;
    private HttpClient _a = null!;
    private HttpClient _b = null!;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("VAULTFINANCE_SQL_TESTS") != "1") { return; }
        await using var scope = fixture.Provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        _connection = new SqlConnection(database.Database.GetConnectionString());
        await _connection.OpenAsync();
        _transaction = (SqlTransaction)await _connection.BeginTransactionAsync();
        _factory = new AuthenticationApiFactory(_connection, _transaction);
        _a = CreateClient();
        _b = CreateClient();
        await SignInAsync(_a, _ownerA);
        await SignInAsync(_b, _ownerB);
    }

    public async Task DisposeAsync()
    {
        _a?.Dispose();
        _b?.Dispose();
        if (_factory is not null) { await _factory.DisposeAsync(); }
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
        }
        if (_connection is not null) { await _connection.DisposeAsync(); }
    }

    [SqlServerTheory]
    [InlineData(" dop ", "DOP")]
    [InlineData("usd", "USD")]
    public async Task Create_UsesAuthenticatedOwnerAndReturnsOnlyAccountDto(string currency, string expected)
    {
        using var response = await _a.PostAsJsonAsync("/api/v1/accounts", new { name = "  Emergency fund  ", currency });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
        Assert.NotEqual(Guid.Empty, account.Id);
        Assert.Equal("Emergency fund", account.Name);
        Assert.Equal(expected, account.Currency);
        Assert.Equal("Active", account.Status);
        Assert.Equal($"/api/v1/accounts/{account.Id}", response.Headers.Location?.OriginalString);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "currency", "id", "name", "status" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        await using var scope = _factory!.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        var persisted = await context.Accounts.AsNoTracking().SingleAsync(current => current.Id == account.Id);
        Assert.Equal(_ownerA, persisted.UserId);
    }

    [SqlServerTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public async Task Create_RejectsInvalidName(string? name)
    {
        using var response = await _a.PostAsJsonAsync("/api/v1/accounts", new { name, currency = "USD" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SqlServerFact]
    public async Task Create_ValidatesTrimmedNameLengthBoundary()
    {
        var maximum = new string('A', 100);
        var accepted = await CreateAsync(_a, $"  {maximum}  ");
        Assert.Equal(maximum, accepted.Name);

        using var rejected = await _a.PostAsJsonAsync("/api/v1/accounts",
            new { name = new string('A', 101), currency = "DOP" });

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [SqlServerTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EUR")]
    [InlineData("USDD")]
    public async Task Create_RejectsInvalidCurrency(string? currency)
    {
        using var response = await _a.PostAsJsonAsync("/api/v1/accounts", new { name = "Cash", currency });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SqlServerTheory]
    [InlineData("ownerId")]
    [InlineData("OwnerId")]
    [InlineData("userId")]
    [InlineData("id")]
    [InlineData("status")]
    [InlineData("balance")]
    public async Task Create_RejectsMassAssignmentFields(string field)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = "Rejected", ["currency"] = "DOP", [field] = _ownerB.ToString()
        };

        using var response = await _a.PostAsJsonAsync("/api/v1/accounts", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await ListAsync(_a)).TotalCount);
    }

    [SqlServerFact]
    public async Task List_ContainsOnlyCurrentOwnersAccountsWithStateFilter()
    {
        var active = await CreateAsync(_a, "Active DOP");
        var archived = await CreateAsync(_a, "Archived USD", "USD");
        using var archivedResponse = await _a.PostAsJsonAsync($"/api/v1/accounts/{archived.Id}/archive", new { });
        Assert.Equal(HttpStatusCode.OK, archivedResponse.StatusCode);
        var other = await CreateAsync(_b, "Other user's account");

        var allA = await ListAsync(_a);
        var activeA = await ListAsync(_a, "?status=active");
        var archivedA = await ListAsync(_a, "?status=Archived");
        var allB = await ListAsync(_b);

        Assert.Equal(2, allA.TotalCount);
        Assert.Equal(new[] { active.Id }, activeA.Items.Select(account => account.Id).ToArray());
        Assert.Equal(new[] { archived.Id }, archivedA.Items.Select(account => account.Id).ToArray());
        Assert.Equal(new[] { other.Id }, allB.Items.Select(account => account.Id).ToArray());
    }

    [SqlServerTheory]
    [InlineData("GET")]
    [InlineData("PATCH")]
    [InlineData("ARCHIVE")]
    public async Task ForeignAccount_IsIndistinguishableFromMissingAndCannotBeChanged(string operation)
    {
        var own = await CreateAsync(_a);
        using var foreign = await SendOperationAsync(_b, operation, own.Id);
        using var missing = await SendOperationAsync(_b, operation, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var first = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync());
        using var second = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        Assert.Equal("account_not_found", first.RootElement.GetProperty("code").GetString());
        Assert.Equal(first.RootElement.GetProperty("title").GetString(), second.RootElement.GetProperty("title").GetString());
        var unchanged = await GetAsync(_a, own.Id);
        Assert.Equal(own, unchanged);
    }

    [SqlServerFact]
    public async Task Rename_UsesDomainRulesAndPreservesCurrencyAndOwner()
    {
        var account = await CreateAsync(_a, "Old name", "USD");

        using var response = await _a.PatchAsJsonAsync($"/api/v1/accounts/{account.Id}", new { name = "  New name  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
        Assert.Equal("New name", changed.Name);
        Assert.Equal("USD", changed.Currency);
        Assert.Equal("Active", changed.Status);
        Assert.Equal(account.Id, changed.Id);
        await using var scope = _factory!.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        Assert.Equal(_ownerA, (await database.Accounts.AsNoTracking().SingleAsync(current => current.Id == account.Id)).UserId);
    }

    [SqlServerTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Rename_RejectsInvalidNameWithoutChangingTheAccount(string? name)
    {
        var account = await CreateAsync(_a);

        using var response = await _a.PatchAsJsonAsync($"/api/v1/accounts/{account.Id}", new { name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(account, await GetAsync(_a, account.Id));
    }

    [SqlServerTheory]
    [InlineData("ownerId")]
    [InlineData("userId")]
    [InlineData("currency")]
    [InlineData("status")]
    public async Task Rename_RejectsSensitiveAndNonEditableFields(string field)
    {
        var account = await CreateAsync(_a);
        var payload = new Dictionary<string, object?> { ["name"] = "Rejected", [field] = "USD" };

        using var response = await _a.PatchAsJsonAsync($"/api/v1/accounts/{account.Id}", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(account, await GetAsync(_a, account.Id));
    }

    [SqlServerFact]
    public async Task Archive_IsIdempotentAcceptsNoBodyAndBlocksRenameWithoutDeleting()
    {
        var account = await CreateAsync(_a);
        using var first = await _a.PostAsync($"/api/v1/accounts/{account.Id}/archive", null);
        using var second = await _a.PostAsJsonAsync($"/api/v1/accounts/{account.Id}/archive", new { });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstAccount = (await first.Content.ReadFromJsonAsync<AccountResponse>())!;
        var secondAccount = (await second.Content.ReadFromJsonAsync<AccountResponse>())!;
        Assert.Equal("Archived", firstAccount.Status);
        Assert.Equal(firstAccount, secondAccount);

        using var rename = await _a.PatchAsJsonAsync($"/api/v1/accounts/{account.Id}", new { name = "Forbidden" });

        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        var persisted = await GetAsync(_a, account.Id);
        Assert.Equal(account.Name, persisted.Name);
        Assert.Equal("Archived", persisted.Status);
        Assert.Equal(1, (await ListAsync(_a)).TotalCount);
    }

    [SqlServerFact]
    public async Task Archive_RejectsOwnerInput()
    {
        var account = await CreateAsync(_a);

        using var response = await _a.PostAsJsonAsync($"/api/v1/accounts/{account.Id}/archive", new { ownerId = _ownerB });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Active", (await GetAsync(_a, account.Id)).Status);
    }

    [SqlServerTheory]
    [InlineData("CREATE", false)]
    [InlineData("PATCH", false)]
    [InlineData("ARCHIVE", false)]
    [InlineData("CREATE", true)]
    [InlineData("PATCH", true)]
    [InlineData("ARCHIVE", true)]
    public async Task Mutations_RejectMissingOrInvalidCsrfWithoutChangingData(string operation, bool invalid)
    {
        var account = await CreateAsync(_a);
        _a.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        if (invalid) { _a.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid-token"); }

        using var response = operation == "CREATE"
            ? await _a.PostAsJsonAsync("/api/v1/accounts", new { name = "Rejected", currency = "USD" })
            : await SendOperationAsync(_a, operation, account.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_csrf_token", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(account, await GetAsync(_a, account.Id));
        Assert.Equal(1, (await ListAsync(_a)).TotalCount);
    }

    [SqlServerFact]
    public async Task List_PaginatesInStableNameAndIdOrderWithoutTrackingEntities()
    {
        await CreateAsync(_a, "Same");
        await CreateAsync(_a, "Same");
        await CreateAsync(_a, "Same");
        var full = await ListAsync(_a);
        var first = await ListAsync(_a, "?page=1&pageSize=2");
        var second = await ListAsync(_a, "?page=2&pageSize=2");
        var repeated = await ListAsync(_a, "?page=2&pageSize=2");

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(1, first.Page);
        Assert.Equal(2, first.PageSize);
        Assert.Equal(2, first.Items.Count);
        Assert.Single(second.Items);
        Assert.Equal(full.Items.Select(item => item.Id),
            first.Items.Concat(second.Items).Select(item => item.Id));
        Assert.Equal(second.Items.Select(item => item.Id), repeated.Items.Select(item => item.Id));

        await using var scope = _factory!.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var database = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        await store.ListOwnedAsync(_ownerA, new AccountListQuery(), CancellationToken.None);
        await store.FindOwnedAsync(_ownerA, full.Items[0].Id, CancellationToken.None);
        Assert.Empty(database.ChangeTracker.Entries<Account>());
    }

    [SqlServerFact]
    public async Task List_SupportsDescendingNamesAndEmptyPageWithinBounds()
    {
        await CreateAsync(_a, "Alpha");
        await CreateAsync(_a, "Zulu");

        var descending = await ListAsync(_a, "?sort=name_desc");
        var empty = await ListAsync(_a, "?page=10000&pageSize=100");

        Assert.Equal(new[] { "Zulu", "Alpha" }, descending.Items.Select(item => item.Name));
        Assert.Empty(empty.Items);
        Assert.Equal(2, empty.TotalCount);
    }

    [SqlServerTheory]
    [InlineData("?page=0")]
    [InlineData("?page=-1")]
    [InlineData("?page=10001")]
    [InlineData("?page=2147483647")]
    [InlineData("?page=invalid")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?pageSize=invalid")]
    [InlineData("?status=Deleted")]
    [InlineData("?status=1")]
    [InlineData("?status=")]
    [InlineData("?sort=currency")]
    [InlineData("?sort=")]
    [InlineData("?ownerId=untrusted")]
    [InlineData("?userId=untrusted")]
    [InlineData("?page=1&page=2")]
    public async Task List_RejectsInvalidUnknownAndRepeatedQueryParameters(string query)
    {
        using var response = await _a.GetAsync("/api/v1/accounts" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SqlServerFact]
    public async Task SqlRenameGuard_RejectsStaleActiveSnapshotAfterArchive()
    {
        var created = await CreateAsync(_a);
        await using var scope = _factory!.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var stale = (await store.FindOwnedAsync(_ownerA, created.Id, CancellationToken.None))!;
        Assert.Equal(AccountStatus.Active, stale.Status);
        using var archived = await _a.PostAsync($"/api/v1/accounts/{created.Id}/archive", null);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        stale.Rename("Stale rename");

        var updated = await store.TryRenameOwnedAsync(stale, CancellationToken.None);

        Assert.False(updated);
        var actual = await GetAsync(_a, created.Id);
        Assert.Equal("Archived", actual.Status);
        Assert.Equal(created.Name, actual.Name);
    }

    private HttpClient CreateClient() => _factory!.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });

    private async Task SignInAsync(HttpClient client, Guid userId)
    {
        var email = $"accounts-{userId:N}@example.invalid";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await manager.CreateAsync(
                new ApplicationUser { Id = userId, UserName = email, Email = email }, password)).Succeeded);
        }
        await RefreshCsrfAsync(client);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await RefreshCsrfAsync(client);
    }

    private static async Task RefreshCsrfAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.RootElement.GetProperty("token").GetString());
    }

    private static async Task<AccountResponse> CreateAsync(HttpClient client, string name = "Cash", string currency = "DOP")
    {
        using var response = await client.PostAsJsonAsync("/api/v1/accounts", new { name, currency });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
    }

    private static async Task<AccountResponse> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"/api/v1/accounts/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
    }

    private static async Task<AccountPage> ListAsync(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync("/api/v1/accounts" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountPage>())!;
    }

    private static Task<HttpResponseMessage> SendOperationAsync(HttpClient client, string operation, Guid id) => operation switch
    {
        "GET" => client.GetAsync($"/api/v1/accounts/{id}"),
        "PATCH" => client.PatchAsJsonAsync($"/api/v1/accounts/{id}", new { name = "Changed" }),
        "ARCHIVE" => client.PostAsJsonAsync($"/api/v1/accounts/{id}/archive", new { }),
        _ => throw new InvalidOperationException("Unknown test operation.")
    };
}
