using System.Text.Json;
using System.Text.RegularExpressions;

namespace Straylight.Agent;

public sealed class Worker : BackgroundService
{
    readonly AgentConfig _cfg;
    readonly Mqtt _mqtt;
    readonly ILogger<Worker> _log;
    PeriodicTimer? _timer;
    int _pollMinutes;
    bool _dimmed;
    bool _idleV2;
    int _brightness = -1;
    FileSystemWatcher? _dimWatcher;    // instance fields so the GC can't collect the
    FileSystemWatcher? _replyWatcher;  // watchers mid-run (locals would stop raising events)
    static readonly string DimStateFile = @"C:\ProgramData\Straylight\dimmed.state";
    static readonly string DimStopFile = @"C:\ProgramData\Straylight\dim.stop";
    static readonly string V2StateFile = @"C:\ProgramData\Straylight\idlev2.state";
    static readonly string BrightnessNowFile = @"C:\ProgramData\Straylight\brightness.now";
    const string StateDir = @"C:\ProgramData\Straylight";

    public Worker(AgentConfig cfg, Mqtt mqtt, ILogger<Worker> log)
    {
        _cfg = cfg; _mqtt = mqtt; _log = log;
        _pollMinutes = Snap(cfg.IntervalSeconds / 60);
    }

    // clamp to [5,60], snap to nearest 5; invalid -> 15
    static int Snap(int minutes)
    {
        if (minutes <= 0) minutes = 15;
        minutes = Math.Clamp(minutes, 5, 60);
        minutes = (int)(Math.Round(minutes / 5.0) * 5);
        return Math.Clamp(minutes, 5, 60);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Straylight starting: node={Node} interval={Min}min restartHour={Hour}",
            _cfg.NodeId, _pollMinutes, _cfg.NightlyRestartHour);
        _mqtt.OnCommand = HandleCommandAsync;

        // survive a restart while dimmed (e.g. the nightly restart during bedtime): re-apply.
        try { _idleV2 = File.Exists(V2StateFile) && File.ReadAllText(V2StateFile).Trim() == "1"; } catch { }
        try { _dimmed = File.Exists(DimStateFile) && File.ReadAllText(DimStateFile).Trim() == "1"; } catch { }
        if (_dimmed) { try { if (File.Exists(DimStopFile)) File.Delete(DimStopFile); SessionLauncher.Run(Environment.ProcessPath!, "--dim-watch"); } catch { } }

        // reflect auto-wake promptly: the dim-watch writes dimmed.state=0 when real input restores brightness
        try
        {
            _dimWatcher = new FileSystemWatcher(@"C:\ProgramData\Straylight", "dimmed.state")
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
            _dimWatcher.Changed += (_, _) =>
            {
                try { if (File.ReadAllText(DimStateFile).Trim() == "0" && _dimmed) { _dimmed = false; _log.LogInformation("screen auto-woke (real input)"); _ = PublishStateAsync(CancellationToken.None); } }
                catch { }
            };
        }
        catch { }

        // the reply window writes reply-<token>.json when the user answers/dismisses an ask;
        // forward it to MQTT (retained per-id + a live event) and delete the file.
        try
        {
            _replyWatcher = new FileSystemWatcher(StateDir, "reply-*.json")
            { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
            _replyWatcher.Created += (_, e) => _ = HandleReplyFileAsync(e.FullPath);
        }
        catch { }

        // forward any reply files stranded while the service was down (or a missed event)
        try { foreach (var f in Directory.GetFiles(StateDir, "reply-*.json")) _ = HandleReplyFileAsync(f); }
        catch { }

        var startDate = DateTime.Now.Date;
        bool discoverySent = false;
        _timer = new PeriodicTimer(TimeSpan.FromMinutes(_pollMinutes));

        do
        {
            try
            {
                await _mqtt.EnsureConnectedAsync(ct);
                if (!discoverySent) { await _mqtt.PublishDiscoveryAsync(ct); discoverySent = true; }
                try { if (File.Exists(BrightnessNowFile) && int.TryParse(File.ReadAllText(BrightnessNowFile).Trim(), out var b)) _brightness = b; } catch { }
                try { SessionLauncher.Run(Environment.ProcessPath!, "--brightness-get"); } catch { } // refresh reading for next cycle
                await PublishStateAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "telemetry cycle failed; will retry next tick");
                discoverySent = false;
            }

            var now = DateTime.Now;
            if (now.Hour == _cfg.NightlyRestartHour && now.Date > startDate)
            {
                _log.LogInformation("Nightly restart — exiting for SCM to relaunch a clean process");
                Environment.Exit(2);
            }
        }
        while (await WaitNext(_timer, ct));

        _timer.Dispose();
        _log.LogInformation("Straylight stopping");
    }

    Task PublishStateAsync(CancellationToken ct)
        => _mqtt.PublishStateAsync(Telemetry.Collect(_cfg, _pollMinutes, _dimmed, _idleV2, _brightness), ct);

    // Tiny markdown subset for toast/popup: "- "/"* "/"• " -> bullet; strip inline **bold**; keep line breaks.
    static string RenderMarkdown(string s)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^\s*([-*•])\s+(.*)$");
            var ln = m.Success ? "• " + m.Groups[2].Value : lines[i];
            lines[i] = Regex.Replace(ln, @"\*\*(.+?)\*\*", "$1"); // strip bold markers (not renderable)
        }
        return string.Join("\n", lines);
    }

    static bool IsTrue(JsonElement e) =>
        e.ValueKind == JsonValueKind.True
        || (e.ValueKind == JsonValueKind.String && e.GetString()?.Trim().ToLowerInvariant() is "true" or "on" or "1")
        || (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n != 0);

    // Hand an interactive ask to the session helper: write ask-<token>.json, launch --reply-window.
    void LaunchReplyWindow(string id, string title, string text, bool urgent, bool reply, List<Dictionary<string, string?>>? buttons)
    {
        string token = Regex.Replace(id, "[^A-Za-z0-9_-]", "_");
        if (token.Length == 0) token = "ask";
        var ask = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["title"] = title,
            ["text"] = text,
            ["urgent"] = urgent,
            ["reply"] = reply,
            ["buttons"] = buttons,
        };
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(Path.Combine(StateDir, $"ask-{token}.json"), JsonSerializer.Serialize(ask));
            SessionLauncher.Run(Environment.ProcessPath!, $"--reply-window {token}");
        }
        catch (Exception ex) { _log.LogWarning(ex, "failed to launch reply window"); }
    }

    // Read a reply-<token>.json the window dropped and publish it: retained to <base>/reply/<id>
    // (a late poller/bot still gets it) and a live event to <base>/telemetry/reply (dashboard).
    async Task HandleReplyFileAsync(string path)
    {
        try
        {
            string content = "";
            for (int i = 0; i < 10; i++)
            {
                try { content = File.ReadAllText(path); if (content.Trim().Length > 0) break; } catch { }
                await Task.Delay(100);
            }
            if (content.Trim().Length == 0) return;

            string id = "";
            try { if (JsonDocument.Parse(content).RootElement.TryGetProperty("id", out var idp)) id = idp.GetString() ?? ""; }
            catch { }

            await _mqtt.EnsureConnectedAsync(CancellationToken.None);
            if (!string.IsNullOrEmpty(id))
                await _mqtt.PublishAsync($"{_cfg.BaseTopic}/reply/{id}", content, true, CancellationToken.None);
            await _mqtt.PublishAsync($"{_cfg.BaseTopic}/telemetry/reply", content, false, CancellationToken.None);
            _log.LogInformation("reply published (id={Id})", id);
        }
        catch (Exception ex) { _log.LogWarning(ex, "handling reply file failed"); }
        finally { try { File.Delete(path); } catch { } }
    }

    async Task HandleCommandAsync(string topic, string payload)
    {
        var key = topic[(topic.LastIndexOf('/') + 1)..];
        switch (key)
        {
            case "poll_interval":
                int v = int.TryParse(payload?.Trim(), out var p) ? p : 15;
                v = Snap(v);
                if (v != _pollMinutes) _log.LogInformation("poll interval -> {Min} min", v);
                _pollMinutes = v;
                if (_timer is not null) { try { _timer.Period = TimeSpan.FromMinutes(v); } catch { } }
                try { await PublishStateAsync(CancellationToken.None); } // refresh UI now
                catch (Exception ex) { _log.LogWarning(ex, "publish after command failed"); }
                break;

            case "message":
            {
                // payload is either plain text (-> friendly toast) or JSON. JSON with an `id` and
                // `reply`/`buttons` opens the interactive window; otherwise it's a toast/popup.
                // strip a leading BOM/zero-width char too (some publishers prepend one).
                var raw = (payload ?? "").Trim().TrimStart('﻿', '​').Trim();
                if (raw.Length == 0) break;
                string text = raw; bool urgent = false; string title = "Message";
                string? id = null; bool reply = false;
                List<Dictionary<string, string?>>? buttons = null;
                if (raw.StartsWith("{"))
                {
                    try
                    {
                        var root = JsonDocument.Parse(raw).RootElement;
                        if (root.TryGetProperty("text", out var t)) text = t.GetString() ?? "";
                        if (root.TryGetProperty("title", out var ti) && !string.IsNullOrWhiteSpace(ti.GetString())) title = ti.GetString()!;
                        if (root.TryGetProperty("urgent", out var u)) urgent = IsTrue(u);
                        if (root.TryGetProperty("id", out var idp)) id = idp.GetString();
                        if (root.TryGetProperty("reply", out var rp)) reply = IsTrue(rp);
                        if (root.TryGetProperty("buttons", out var bs) && bs.ValueKind == JsonValueKind.Array)
                        {
                            buttons = new();
                            foreach (var b in bs.EnumerateArray())
                                buttons.Add(new()
                                {
                                    ["id"] = b.TryGetProperty("id", out var x) ? x.GetString() : null,
                                    ["name"] = b.TryGetProperty("name", out var y) ? y.GetString() : null,
                                    ["hint"] = b.TryGetProperty("hint", out var z) ? z.GetString() : null,
                                });
                        }
                    }
                    catch { text = raw; urgent = false; id = null; reply = false; buttons = null; }
                }
                if (text.Trim().Length == 0) break;
                text = text.Replace("\\r\\n", "\n").Replace("\\n", "\n"); // single-line box: \n -> line break

                bool interactive = !string.IsNullOrEmpty(id) && (reply || buttons is { Count: > 0 });
                if (interactive)
                {
                    LaunchReplyWindow(id!, title, text, urgent, reply, buttons);
                    _log.LogInformation("interactive ask launched (id={Id} reply={R} buttons={B})", id, reply, buttons?.Count ?? 0);
                }
                else
                {
                    var rendered = RenderMarkdown(text);
                    if (urgent) Notify.Popup(title, rendered);
                    else Notify.Toast(title, rendered);
                    _log.LogInformation("message delivered (urgent={U})", urgent);
                }
                break;
            }

            case "v2":
                _idleV2 = (payload ?? "").Trim().ToUpperInvariant() is "ON" or "1" or "TRUE";
                try { File.WriteAllText(V2StateFile, _idleV2 ? "1" : "0"); } catch { }
                _log.LogInformation("idle v2 -> {On} (detection logic pending build)", _idleV2);
                try { await PublishStateAsync(CancellationToken.None); } catch { }
                break;

            case "dim":
                bool wantDim = (payload ?? "").Trim().ToUpperInvariant() is "ON" or "1" or "TRUE";
                if (wantDim)
                {
                    if (!_idleV2) { _log.LogInformation("dim refused: enable Idle v2 (auto-wake) first"); try { await PublishStateAsync(CancellationToken.None); } catch { } break; }
                    try { if (File.Exists(DimStopFile)) File.Delete(DimStopFile); } catch { }
                    SessionLauncher.Run(Environment.ProcessPath!, "--dim-watch"); // dims + auto-wakes on real input
                    _dimmed = true;
                }
                else
                {
                    try { File.WriteAllText(DimStopFile, "1"); } catch { } // watcher restores + exits
                    _dimmed = false;
                }
                try { File.WriteAllText(DimStateFile, _dimmed ? "1" : "0"); } catch { }
                _log.LogInformation("screen dim -> {On}", _dimmed);
                try { await PublishStateAsync(CancellationToken.None); } catch { }
                break;

            default:
                _log.LogDebug("ignoring unknown command '{Key}'", key);
                break;
        }
    }

    static async Task<bool> WaitNext(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
