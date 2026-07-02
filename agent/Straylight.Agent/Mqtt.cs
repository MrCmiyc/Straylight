using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;

namespace Straylight.Agent;

/// <summary>
/// MQTT client (MQTTnet): retained state + HA discovery, Last-Will availability, and a
/// command channel (subscribes &lt;base&gt;/cmd/# and raises OnCommand(topic, payload)).
/// </summary>
public sealed class Mqtt : IAsyncDisposable
{
    readonly AgentConfig _cfg;
    readonly ILogger<Mqtt> _log;
    readonly IMqttClient _client;
    readonly MqttClientOptions _options;

    /// <summary>Raised for each message on &lt;base&gt;/cmd/# . Args: (topic, payload).</summary>
    public Func<string, string, Task>? OnCommand;

    public Mqtt(AgentConfig cfg, ILogger<Mqtt> log)
    {
        _cfg = cfg;
        _log = log;
        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += async e =>
        {
            var h = OnCommand;
            if (h is null) return;
            try { await h(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString() ?? ""); }
            catch (Exception ex) { _log.LogWarning(ex, "command handler threw"); }
        };

        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(cfg.Host, cfg.Port)
            .WithClientId($"straylight-{cfg.NodeId}-{Guid.NewGuid():N}")
            .WithCleanSession(true)
            .WithWillTopic(cfg.StatusTopic)
            .WithWillPayload("offline")
            .WithWillRetain(true);
        if (!string.IsNullOrEmpty(cfg.Username))
            b = b.WithCredentials(cfg.Username, cfg.Password);
        if (cfg.Tls)
            b = b.WithTlsOptions(o => o.UseTls(true));
        _options = b.Build();
    }

    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client.IsConnected) return;
        await _client.ConnectAsync(_options, ct);
        await PublishAsync(_cfg.StatusTopic, "online", true, ct);
        var sub = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic($"{_cfg.BaseTopic}/cmd/#"))
            .Build();
        await _client.SubscribeAsync(sub, ct);
        _log.LogInformation("MQTT connected to {Host}:{Port}, subscribed {Base}/cmd/#", _cfg.Host, _cfg.Port, _cfg.BaseTopic);
    }

    public Task PublishStateAsync(TelemetrySnapshot s, CancellationToken ct)
        => PublishAsync(_cfg.StateTopic, JsonSerializer.Serialize(s), true, ct);

    public async Task PublishDiscoveryAsync(CancellationToken ct)
    {
        foreach (var (comp, id, json) in Discovery.Build(_cfg))
            await PublishAsync($"{_cfg.DiscoveryPrefix}/{comp}/{_cfg.NodeId}/{id}/config", json, true, ct);
        _log.LogInformation("MQTT discovery published ({Node})", _cfg.NodeId);
    }

    async Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).WithRetainFlag(retain).Build();
        await _client.PublishAsync(msg, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
            {
                await PublishAsync(_cfg.StatusTopic, "offline", true, CancellationToken.None);
                await _client.DisconnectAsync();
            }
        }
        catch { /* best effort */ }
        _client.Dispose();
    }
}

internal static class Discovery
{
    public static IEnumerable<(string comp, string id, string json)> Build(AgentConfig c)
    {
        var dev = new Dictionary<string, object>
        {
            ["identifiers"] = new[] { c.NodeId },
            ["name"] = c.DeviceName,
            ["manufacturer"] = "Straylight",
            ["model"] = "agent"
        };
        string cmd = $"{c.BaseTopic}/cmd";

        // NOTE: "name" is the bare label only — HA prefixes the device name automatically,
        // giving e.g. "PC 1 Active" (setting "PC 1 Active" here doubles it).
        (string comp, string id, string name, string tmpl, Dictionary<string, object> extra)[] defs =
        {
            ("binary_sensor","active","Active","{{ 'ON' if value_json.active else 'OFF' }}",
                new(){ ["device_class"]="running", ["payload_on"]="ON", ["payload_off"]="OFF" }),
            ("sensor","idle","Idle","{{ value_json.idle_seconds }}",
                new(){ ["unit_of_measurement"]="s", ["device_class"]="duration", ["icon"]="mdi:timer-sand" }),
            ("sensor","user","User","{{ value_json.user }}", new(){ ["icon"]="mdi:account" }),
            ("sensor","session","Session","{{ value_json.state }}", new(){ ["icon"]="mdi:monitor" }),
            ("sensor","top_app","Top App","{{ value_json.top_apps[0].name if value_json.top_apps else 'none' }}",
                new(){ ["icon"]="mdi:application", ["json_attributes_topic"]=c.StateTopic,
                       ["json_attributes_template"]="{{ {'apps': value_json.top_apps} | tojson }}" }),
            ("sensor","browsers","Browsers","{{ value_json.browsers | join(', ') if value_json.browsers else 'none' }}",
                new(){ ["icon"]="mdi:web" }),
            ("sensor","procs","Processes","{{ value_json.process_count }}", new(){ ["icon"]="mdi:counter" }),
            ("sensor","version","Agent version","{{ value_json.version }}",
                new(){ ["icon"]="mdi:tag", ["entity_category"]="diagnostic" }),
            ("sensor","brightness","Brightness","{{ value_json.brightness }}",
                new(){ ["unit_of_measurement"]="%", ["icon"]="mdi:brightness-6" }),

            // writable control: HA number -> agent. command published RETAINED so we get the
            // last setpoint on connect. One cmd subtopic per setting; same shape for future controls.
            ("number","poll_interval","Poll interval","{{ value_json.poll_interval_min }}",
                new(){ ["command_topic"]=$"{cmd}/poll_interval", ["min"]=5, ["max"]=60, ["step"]=5,
                       ["unit_of_measurement"]="min", ["mode"]="box", ["icon"]="mdi:timer-cog",
                       ["entity_category"]="config", ["retain"]=true }),

            // Control switches. Screen dim is inert until v2 auto-wake is built (cmd/dim no-ops);
            // Idle v2 toggles its flag now (real-input detection logic lands with the v2 build).
            ("switch","screen_dim","Screen dim","{{ 'ON' if value_json.dimmed else 'OFF' }}",
                new(){ ["command_topic"]=$"{cmd}/dim", ["payload_on"]="ON", ["payload_off"]="OFF",
                       ["icon"]="mdi:monitor-off", ["retain"]=true }),
            ("switch","idle_v2","Idle v2","{{ 'ON' if value_json.idle_v2 else 'OFF' }}",
                new(){ ["command_topic"]=$"{cmd}/v2", ["payload_on"]="ON", ["payload_off"]="OFF",
                       ["icon"]="mdi:mouse", ["entity_category"]="config", ["retain"]=true }),
        };

        foreach (var d in defs)
        {
            var o = new Dictionary<string, object>
            {
                ["name"] = d.name,
                ["unique_id"] = $"{c.NodeId}_{d.id}",
                ["state_topic"] = c.StateTopic,
                ["value_template"] = d.tmpl,
                ["availability_topic"] = c.StatusTopic,
                ["payload_available"] = "online",
                ["payload_not_available"] = "offline",
                ["device"] = dev
            };
            foreach (var kv in d.extra) o[kv.Key] = kv.Value;
            yield return (d.comp, d.id, JsonSerializer.Serialize(o));
        }

        // free-text "send the child a message" box (command-only: no state_topic)
        var textCfg = new Dictionary<string, object>
        {
            ["name"] = "Message",
            ["unique_id"] = $"{c.NodeId}_message",
            ["command_topic"] = $"{cmd}/message",
            ["icon"] = "mdi:message-text",
            ["max"] = 200,
            ["availability_topic"] = c.StatusTopic,
            ["payload_available"] = "online",
            ["payload_not_available"] = "offline",
            ["device"] = dev
        };
        yield return ("text", "message", JsonSerializer.Serialize(textCfg));
    }
}
