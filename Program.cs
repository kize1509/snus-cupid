if (args.Contains("--person", StringComparer.OrdinalIgnoreCase))
{
    await PersonConsoleClient.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<CupidRegistry>();
builder.Services.AddHostedService<CupidBackgroundService>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "Cupidon",
    endpoints = new[]
    {
        "POST /people/register",
        "POST /people/init-single-person",
        "GET /people/{username}/letters/next",
        "POST /people/{username}/letters/ack",
        "POST /people/{username}/block/{blockedUsername}",
        "POST /cupid/tick"
    }
}));

var people = app.MapGroup("/people");

people.MapPost("/register", (RegisterPersonRequest request, CupidRegistry registry) =>
{
    var result = registry.InitSinglePerson(request);
    return result.ToHttpResult();
});

people.MapPost("/init-single-person", (RegisterPersonRequest request, CupidRegistry registry) =>
{
    var result = registry.InitSinglePerson(request);
    return result.ToHttpResult();
});

people.MapGet("/{username}/letters/next", async (
    string username,
    int? timeoutSeconds,
    CupidRegistry registry,
    HttpContext context) =>
{
    var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? 65, 1, 300));
    var result = await registry.WaitForNextLetterAsync(username, timeout, context.RequestAborted);
    return result.ToHttpResult();
});

people.MapPost("/{username}/letters/ack", (string username, CupidRegistry registry) =>
{
    var result = registry.AcknowledgeLetter(username);
    return result.ToHttpResult();
});

people.MapPost("/{username}/block/{blockedUsername}", (
    string username,
    string blockedUsername,
    CupidRegistry registry) =>
{
    var result = registry.BlockUser(username, blockedUsername);
    return result.ToHttpResult();
});

people.MapGet("/{username}", (string username, CupidRegistry registry) =>
{
    var result = registry.GetPerson(username);
    return result.ToHttpResult();
});

var cupid = app.MapGroup("/cupid");

cupid.MapPost("/tick", (CupidRegistry registry) =>
{
    var result = registry.SendLetters();
    return Results.Ok(result);
});

cupid.MapGet("/people", (CupidRegistry registry) => Results.Ok(registry.GetPeople()));

app.Run();
