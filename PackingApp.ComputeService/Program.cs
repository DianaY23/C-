using PackingApp.Shared;
using PackingApp.ComputeService;
using System.Text.Json;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
var app = builder.Build();

// Основной эндпоинт с SSE (для реального времени)
app.MapPost("/api/packing/solve", async (List<Rect> shapes, HttpContext context) =>
{
    if (shapes == null || shapes.Count == 0) return Results.Ok(new Individual());

    int popSize = 100;
    int gens = 500;

    if (context.Request.Query.TryGetValue("popSize", out var popStr) && int.TryParse(popStr, out var p)) popSize = p;
    if (context.Request.Query.TryGetValue("gens", out var genStr) && int.TryParse(genStr, out var g)) gens = g;

    var solver = new GeneticSolver(shapes, popSize, gens);
    
    var channel = Channel.CreateUnbounded<ProgressReport>();
    var progress = new Progress<ProgressReport>(report => channel.Writer.TryWrite(report));

    _ = Task.Run(async () =>
    {
        await solver.SolveAsync(progress, context.RequestAborted);
        channel.Writer.Complete();
    });

    context.Response.Headers.Append("Content-Type", "text/event-stream");
    
    await foreach (var report in channel.Reader.ReadAllAsync(context.RequestAborted))
    {
        var json = JsonSerializer.Serialize(report);
        await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync();
    }

    return Results.Empty;
});

// Тестовый эндпоинт без SSE (возвращает чистый JSON)
app.MapPost("/api/packing/solve/test", async (List<Rect> shapes, HttpContext context) =>
{
    if (shapes == null || shapes.Count == 0) return Results.Ok(new Individual());

    var solver = new GeneticSolver(shapes, popSize: 10, gens: 10);
    var result = await solver.SolveAsync(null, context.RequestAborted);
    return Results.Ok(result);
});

app.MapGet("/", () => "Сервис работает! Отправляйте POST на /api/packing/solve");
app.Run();

// Эта строчка нужна для тестов!
public partial class Program { }
