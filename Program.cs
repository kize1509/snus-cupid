if (args.Contains("--person", StringComparer.OrdinalIgnoreCase))
{
    await PersonConsoleClient.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<CupidRegistry>();
builder.Services.AddSingleton<CupidPubSubHub>();
builder.Services.AddHostedService<CupidBackgroundService>();

var app = builder.Build();
app.UseWebSockets();

var people = app.MapGroup("/people");

people.MapPost("/init-single-person", (RegisterPersonRequest request, CupidRegistry registry) =>
{
    var result = registry.InitSinglePerson(request);
    return result.ToHttpResult();
});

people.MapGet("/{personId:guid}/events", async (
    Guid personId,
    CupidRegistry registry,
    HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new ApiError("This endpoint requires a WebSocket request."));
        return;
    }

    if (!registry.PersonExists(personId))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new ApiError("Person is not registered."));
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await registry.SubscribeAsync(personId, socket, context.RequestAborted);
});

app.Run();
