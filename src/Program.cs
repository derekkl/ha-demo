var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

var app = builder.Build();

var startTime = DateTime.UtcNow;
var warmupSeconds = int.TryParse(Environment.GetEnvironmentVariable("WARMUP_SECONDS"), out var ws) ? ws : 10;
var requestCount = 0;
var readyManualOff = 0; // 0 = ready, 1 = manually off

app.MapGet("/", () =>
{
    var n = Interlocked.Increment(ref requestCount);
    var uptime = (int)(DateTime.UtcNow - startTime).TotalSeconds;
    var ready = Interlocked.CompareExchange(ref readyManualOff, 0, 0) == 0 && uptime >= warmupSeconds;
    return Results.Json(new { pod = Environment.MachineName, count = n, uptime, ready });
});

app.MapGet("/healthz", () => Results.Ok("healthy"));

app.MapGet("/readyz", () =>
{
    var uptime = (DateTime.UtcNow - startTime).TotalSeconds;
    return (Interlocked.CompareExchange(ref readyManualOff, 0, 0) == 0 && uptime >= warmupSeconds)
        ? Results.Ok("ready")
        : Results.StatusCode(503);
});

app.MapPost("/ready/off", () => { Interlocked.Exchange(ref readyManualOff, 1); return Results.Ok("readiness disabled"); });
app.MapPost("/ready/on",  () => { Interlocked.Exchange(ref readyManualOff, 0); return Results.Ok("readiness enabled");  });

app.Run();
