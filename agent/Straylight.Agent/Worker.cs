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
    bool _updating;                    // true while a self-update is downloading/swapping
    static readonly string DimStateFile = @"C:\ProgramData\Straylight\dimmed.state";
    static readonly string DimStopFile = @"C:\ProgramData\Straylight\dim.stop";
    static readonly string V2StateFile = @"C:\ProgramData\Straylight\idlev2.state";
    static readonly string BrightnessNowFile = @"C:\ProgramData\Straylight\brightness.now";
    const string StateDir = @"C:\ProgramData\Straylight";
    long? _activeSince;                 // epoch secs when the current active streak began (survives restarts)
    static readonly string RealInputFile = @"C:\ProgramData\Straylight\realinput.now";
    static readonly string HbFile = @"C:\ProgramData\Straylight\realinput.hb";
    static readonly string IdleWatchStopFile = @"C:\ProgramData\Straylight\idlewatch.stop";
    static readonly string ActiveSinceFile = @"C:\ProgramData\Straylight\active_since.state";

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
        try { if (long.TryParse(File.Exists(ActiveSinceFile) ? File.ReadAllText(ActiveSinceFile).Trim() : "", out var av)) _activeSince = av; } catch { }
        if (_idleV2) { try { if (File.Exists(IdleWatchStopFile)) File.Delete(IdleWatchStopFile); } catch { } try { SessionLauncher.Run(Environment.ProcessPath!, "--idle-watch"); } catch { } }
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
    {
        EnsureIdleWatch();
        var snap = Telemetry.Collect(_cfg, _pollMinutes, _dimmed, _idleV2, _brightness, _updating, RealIdleSeconds());
        // restart-robust active_since: stamp when the streak begins, keep it (incl. across restarts),
        // clear when inactive. Persisted so a self-update / nightly restart doesn't reset the session.
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        if (snap.active) { if (_activeSince is null) { _activeSince = now; PersistActiveSince(); } }
        else if (_activeSince is not null) { _activeSince = null; PersistActiveSince(); }
        return _mqtt.PublishStateAsync(snap with { active_since = _activeSince }, ct);
    }

    void PersistActiveSince() { try { File.WriteAllText(ActiveSinceFile, _activeSince?.ToString() ?? ""); } catch { } }

    // Seconds since the last REAL (non-injected) input, from the idle-watch helper; null when v2 is
    // off or the watcher isn't alive (heartbeat stale) so telemetry falls back to quser.
    long? RealIdleSeconds()
    {
        if (!_idleV2) return null;
        try
        {
            long now = DateTimeOffset.Now.ToUnixTimeSeconds();
            if (!File.Exists(HbFile) || !long.TryParse(File.ReadAllText(HbFile).Trim(), out var hb) || now - hb > 12) return null;
            if (!File.Exists(RealInputFile) || !long.TryParse(File.ReadAllText(RealInputFile).Trim(), out var last)) return null;
            return Math.Max(0, now - last);
        }
        catch { return null; }
    }

    // Keep the real-input watcher alive while v2 is on: relaunch if the heartbeat is stale. The
    // helper's named mutex makes a redundant launch a no-op, so this is safe to call each cycle.
    void EnsureIdleWatch()
    {
        if (!_idleV2) return;
        bool alive = false;
        try { alive = File.Exists(HbFile) && long.TryParse(File.ReadAllText(HbFile).Trim(), out var hb) && DateTimeOffset.Now.ToUnixTimeSeconds() - hb <= 12; } catch { }
        if (!alive)
        {
            try { if (File.Exists(IdleWatchStopFile)) File.Delete(IdleWatchStopFile); } catch { }
            try { SessionLauncher.Run(Environment.ProcessPath!, "--idle-watch"); } catch { }
        }
    }

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

    // Self-update: verify the download against the sha in the *MQTT* straylight/latest (a different
    // trust domain than the web host), then hand off to the detached swapper. Idempotent — a no-op
    // if we're already at the latest version, so a late-delivered QoS1 update command is safe.
    async Task UpdateAsync()
    {
        try
        {
            var decision = UpdateLogic.Plan(Telemetry.Version, _mqtt.Latest, _cfg.UpdateBase);
            if (!decision.ShouldProceed) { _log.LogInformation("update: {Reason}", decision.Reason); return; }

            _updating = true;
            try { await PublishStateAsync(CancellationToken.None); } catch { }   // HA shows "Installing…"

            string newExe = Path.Combine(StateDir, "straylight-agent.new.exe");
            _log.LogInformation("update: downloading {Url}", decision.Url);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                await File.WriteAllBytesAsync(newExe, await http.GetByteArrayAsync(decision.Url!));

            if (!UpdateLogic.ShaMatches(await File.ReadAllBytesAsync(newExe), decision.ExpectedSha))
            {
                _log.LogError("update: sha256 mismatch; aborting");
                try { File.Delete(newExe); } catch { }
                _updating = false; try { await PublishStateAsync(CancellationToken.None); } catch { }
                return;
            }

            _log.LogInformation("update: sha verified, launching swap {From} -> {To}", Telemetry.Version, _mqtt.Latest!.Version);
            if (!Updater.Run(newExe))
            {
                _log.LogError("update: failed to launch swapper");
                _updating = false; try { await PublishStateAsync(CancellationToken.None); } catch { }
            }
            // success: the detached swapper stops this service and swaps the exe; nothing more here.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "update failed");
            _updating = false; try { await PublishStateAsync(CancellationToken.None); } catch { }
        }
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
                if (_idleV2)
                {
                    try { if (File.Exists(IdleWatchStopFile)) File.Delete(IdleWatchStopFile); } catch { }
                    try { SessionLauncher.Run(Environment.ProcessPath!, "--idle-watch"); } catch { }   // real-input idle tracker
                }
                else { try { File.WriteAllText(IdleWatchStopFile, "1"); } catch { } }                  // stop the tracker
                _log.LogInformation("idle v2 -> {On} (real-input idle tracker)", _idleV2);
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

            case "update":
                _ = UpdateAsync();   // fire-and-forget; handles its own errors + progress flag
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
