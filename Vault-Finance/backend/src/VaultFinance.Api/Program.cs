using VaultFinance.Api.Endpoints;
using VaultFinance.Infrastructure;
using VaultFinance.Api.Security;
using VaultFinance.Application.Accounts;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddProblemDetails();
builder.Services.AddAuthenticationRateLimits();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16_384);

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<CsrfProtectionMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();
app.MapAuthenticationEndpoints();
app.MapAccountEndpoints();

app.MapGet("/health", HealthEndpoint.Handle)
    .WithName("GetHealth")
    .WithSummary("Reports API process liveness only.")
    .AllowAnonymous()
    .ShortCircuit();

app.Run();

// Makes the entry point accessible to WebApplicationFactory.
public partial class Program { }
