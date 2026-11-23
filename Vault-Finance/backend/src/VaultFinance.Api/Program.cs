using VaultFinance.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/health", HealthEndpoint.Handle)
    .WithName("GetHealth")
    .WithSummary("Reports API process liveness only.")
    .AllowAnonymous();

app.Run();

// Makes the entry point accessible to WebApplicationFactory.
public partial class Program { }
