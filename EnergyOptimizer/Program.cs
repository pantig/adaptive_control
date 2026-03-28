using EnergyOptimizer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<PolishCalendarService>();
builder.Services.AddSingleton<SimulationService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/demo/defaults", (SimulationService simulationService) =>
{
    var response = simulationService.BuildDefaultScenario();
    return Results.Ok(response);
});

app.MapPost("/api/demo/simulate", (ScenarioRequest request, SimulationService simulationService) =>
{
    var response = simulationService.Simulate(request);
    return Results.Ok(response);
});

app.MapFallbackToFile("index.html");

app.Run();
