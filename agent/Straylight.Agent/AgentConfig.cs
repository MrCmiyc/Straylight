using System.Text.Json;

namespace Straylight.Agent;

/// <summary>
/// Reads C:\ProgramData\Straylight\mqtt.json (same schema the PowerShell agent used,
/// with a few optional service-only fields added). Keys are snake_case.
/// </summary>
public sealed class AgentConfig
{
    public string Host { get; set; } = "mqtt-host";
    public int Port { get; set; } = 1883;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";   // usually empty (anonymous broker)
    public bool Tls { get; set; } = false;
    public string BaseTopic { get; set; } = "straylight";
    public string NodeId { get; set; } = "straylight";
    public string DeviceName { get; set; } = "Straylight";
    public string DiscoveryPrefix { get; set; } = "homeassistant";

    // service-only knobs (optional in json; defaults below)
    public int IntervalSeconds { get; set; } = 900;       // 15 min
    public int IdleActiveSeconds { get; set; } = 300;     // <5 min idle = active
    public int NightlyRestartHour { get; set; } = 4;      // self-restart ~4am to shed leaks

    public string StateTopic => $"{BaseTopic}/telemetry/state";
    public string StatusTopic => $"{BaseTopic}/telemetry/status";

    public static AgentConfig Load(string path)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), opts)
               ?? throw new InvalidDataException("config deserialized to null");
    }
}
