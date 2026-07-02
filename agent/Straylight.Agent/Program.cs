using Straylight.Agent;

// Helper mode: the service re-invokes its own exe into the user session for session-bound
// work (DDC brightness), then it exits. Not a service run.
if (args.Length >= 1 && args[0] == "--brightness")
{
    const string save = @"C:\ProgramData\Straylight\brightness.sav";
    if (args.Length >= 2 && args[1] == "restore") Monitors.Restore(save);
    else Monitors.Dim(save);
    return;
}
if (args.Length >= 1 && args[0] == "--dim-watch") { DimWatch.Run(); return; }
if (args.Length >= 1 && args[0] == "--brightness-get")
{
    try { File.WriteAllText(@"C:\ProgramData\Straylight\brightness.now", Monitors.GetBrightnessPercent().ToString()); } catch { }
    return;
}

const string ConfigPath = @"C:\ProgramData\Straylight\mqtt.json";

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "Straylight");
builder.Logging.AddEventLog(s => s.SourceName = "Straylight");

AgentConfig cfg;
try
{
    cfg = AgentConfig.Load(ConfigPath);
}
catch (Exception ex)
{
    try
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, "agent-fatal.log"),
            $"{DateTime.Now:s} config load failed ({ConfigPath}): {ex}\n");
    }
    catch { }
    throw;
}

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<Mqtt>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
