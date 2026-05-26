var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

var app = builder.Build();

var startTime = DateTime.UtcNow;
var warmupSeconds = int.TryParse(Environment.GetEnvironmentVariable("WARMUP_SECONDS"), out var ws) ? ws : 10;
var requestCount   = 0;
var readyManualOff  = 0; // 0 = ready,   1 = manually off
var healthManualOff = 0; // 0 = healthy,  1 = manually off

app.MapGet("/", () =>
{
    var n = Interlocked.Increment(ref requestCount);
    var uptime = (int)(DateTime.UtcNow - startTime).TotalSeconds;
    var ready = Interlocked.CompareExchange(ref readyManualOff, 0, 0) == 0 && uptime >= warmupSeconds;
    return Results.Json(new { pod = Environment.MachineName, count = n, uptime, ready });
});

app.MapGet("/healthz", () =>
    Interlocked.CompareExchange(ref healthManualOff, 0, 0) == 0
        ? Results.Ok("healthy")
        : Results.StatusCode(503));

app.MapGet("/readyz", () =>
{
    var uptime = (DateTime.UtcNow - startTime).TotalSeconds;
    return (Interlocked.CompareExchange(ref readyManualOff, 0, 0) == 0 && uptime >= warmupSeconds)
        ? Results.Ok("ready")
        : Results.StatusCode(503);
});

app.MapPost("/ready/off",  () => { Interlocked.Exchange(ref readyManualOff,  1); return Results.Ok("readiness disabled"); });
app.MapPost("/ready/on",   () => { Interlocked.Exchange(ref readyManualOff,  0); return Results.Ok("readiness enabled");  });
app.MapPost("/health/off", () => { Interlocked.Exchange(ref healthManualOff, 1); return Results.Ok("liveness disabled");  });
app.MapPost("/health/on",  () => { Interlocked.Exchange(ref healthManualOff, 0); return Results.Ok("liveness enabled");   });

app.MapPost("/oom", () =>
{
    // Allocates in a background thread so the response returns before the kill.
    // Array.Fill touches every page — defeats virtual-memory lazy allocation so
    // RSS actually climbs and the cgroup limit fires within a few seconds.
    Task.Run(() =>
    {
        var sink = new List<byte[]>();
        while (true)
        {
            var block = new byte[64 * 1024 * 1024]; // 64 MiB per step
            Array.Fill(block, (byte)0xff);
            sink.Add(block);
        }
    });
    return Results.Ok("allocating memory — OOM kill imminent");
});

app.Run();
