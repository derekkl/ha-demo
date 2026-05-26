using System.Runtime.InteropServices;

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

app.MapPost("/oom", (int ms = 10) =>
{
    // Marshal.AllocHGlobal allocates unmanaged (native) memory directly — the
    // .NET GC has no visibility into these bytes and cannot throw managed OOM to
    // stop the loop. This is necessary because the GC derives a heap limit from
    // the cgroup memory limit and refuses new managed allocations before the
    // kernel ever gets involved; unmanaged allocation bypasses that layer entirely.
    // The unsafe page-touch loop forces the kernel to commit physical memory
    // (without it, Linux lazy-allocates and RSS doesn't actually climb).
    Task.Run(() =>
    {
        var ptrs = new List<IntPtr>();
        try
        {
            while (true)
            {
                var ptr = Marshal.AllocHGlobal(4 * 1024 * 1024);
                unsafe
                {
                    var p = (byte*)ptr;
                    for (int i = 0; i < 4 * 1024 * 1024; i += 4096)
                        p[i] = 0xff;
                }
                ptrs.Add(ptr);
                Thread.Sleep(ms);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Allocator died: {ex.GetType().Name}: {ex.Message}");
        }
    });
    return Results.Ok("allocating native memory — OOM kill imminent");
});

app.Run();
