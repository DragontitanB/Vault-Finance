using VaultFinance.Api.Endpoints;
using VaultFinance.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health", HealthEndpoint.Handle)
    .WithName("GetHealth")
    .WithSummary("Reports API process liveness only.")
    .AllowAnonymous()
    .ShortCircuit();

app.Run();

// Makes the entry point accessible to WebApplicationFactory.
public partial class Program { }
