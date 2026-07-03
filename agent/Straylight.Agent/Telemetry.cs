using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Straylight.Agent;

public record AppInfo(string name, long mem_mb);

public record TelemetrySnapshot(
    string ts, bool online, string? user, string state,
    long? idle_seconds, bool active, string[] browsers,
    AppInfo[] top_apps, int process_count, int poll_interval_min, bool dimmed, bool idle_v2, int brightness, string version, bool updating,
    long? idle_real_seconds, string idle_source, long? active_since);

/// <summary>
/// Collects session/idle (via quser — works as SYSTEM and cross-session) and process info.
/// quser is a tiny, instant child; matches exactly what the PowerShell agent reported.
/// </summary>
public static class Telemetry
{
    // Bump on each build so Home Assistant can show which machine runs which build.
    public const string Version = "0.8.8";

    public static TelemetrySnapshot Collect(AgentConfig cfg, int pollMinutes, bool dimmed, bool idleV2, int brightness, bool updating, long? realIdleSeconds)
    {
        var (user, state, idle) = GetSession();
        var (apps, browsers, count) = GetProcesses();
        // With idle_v2 + a live real-input watcher, presence is decided by REAL (non-injected) input,
        // so an autoclicker can't hold the box "active". Otherwise fall back to quser (v1). idle_seconds
        // stays the quser value so the dashboard can see the divergence (quser 0 vs real climbing).
        bool useReal = idleV2 && realIdleSeconds.HasValue;
        long? decisive = useReal ? realIdleSeconds : idle;
        string source = useReal ? "real-input" : "quser";
        bool active = state == "Active" && decisive.HasValue && decisive.Value < cfg.IdleActiveSeconds;
        return new TelemetrySnapshot(
            DateTime.Now.ToString("s"), true,
            string.IsNullOrEmpty(user) ? null : user, state,
            idle, active, browsers, apps, count, pollMinutes, dimmed, idleV2, brightness, Version, updating,
            realIdleSeconds, source, null);   // active_since is stamped by the Worker (needs cross-cycle state)
    }

    // ---- session / idle via quser ----
    static (string user, string state, long? idle) GetSession()
    {
        try
        {
            var psi = new ProcessStartInfo("quser.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return ("", "unknown", null);
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);

            var lines = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2) return ("", "none", null);   // no interactive session

            var row = lines[1].TrimStart('>', ' ');
            var f = Regex.Split(row, @"\s{2,}");
            if (f.Length < 4) return ("", "unknown", null);

            string user = f[0];
            string state = f[^3];          // ...STATE  IDLE  LOGON
            long? idle = ParseIdle(f[^2]);
            return (user, state, idle);
        }
        catch { return ("", "unknown", null); }
    }

    static long? ParseIdle(string s)
    {
        s = s.Trim();
        if (s is "none" or ".") return 0;
        if (long.TryParse(s, out var min)) return min * 60;
        var d = Regex.Match(s, @"^(\d+)\+(\d+):(\d+)$");           // D+HH:MM
        if (d.Success) return (long.Parse(d.Groups[1].Value) * 1440 + long.Parse(d.Groups[2].Value) * 60 + long.Parse(d.Groups[3].Value)) * 60;
        var h = Regex.Match(s, @"^(\d+):(\d+)$");                  // HH:MM
        if (h.Success) return (long.Parse(h.Groups[1].Value) * 60 + long.Parse(h.Groups[2].Value)) * 60;
        return null;
    }

    // ---- processes ----
    static readonly HashSet<string> Deny = new(StringComparer.OrdinalIgnoreCase)
    {
        "System","Idle","Memory Compression","Registry","svchost","dwm","csrss","wininit",
        "winlogon","services","lsass","fontdrvhost","MsMpEng","NisSrv","SecurityHealthService",
        "RuntimeBroker","dllhost","taskhostw","ctfmon","SearchHost","SearchIndexer","sihost",
        "ShellExperienceHost","StartMenuExperienceHost","explorer","smss","spoolsv","conhost",
        "WmiPrvSE","audiodg","PSEXESVC","cmd","powershell","pwsh","WUDFHost","wlanext",
        "OCControl.Service","straylight-agent","vmmemWSL","vmmem"
    };
    static readonly Dictionary<string, string> BrowserMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome", ["msedge"] = "Edge", ["firefox"] = "Firefox", ["opera"] = "Opera",
        ["opera_gx"] = "Opera GX", ["brave"] = "Brave", ["vivaldi"] = "Vivaldi", ["iexplore"] = "IE"
    };

    static (AppInfo[] apps, string[] browsers, int count) GetProcesses()
    {
        var procs = Process.GetProcesses();
        var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in procs)
        {
            string n = p.ProcessName;
            present.Add(n);
            if (!Deny.Contains(n))
            {
                try { byName[n] = byName.GetValueOrDefault(n) + p.WorkingSet64; } catch { }
            }
            p.Dispose();
        }

        var apps = byName.OrderByDescending(kv => kv.Value).Take(5)
            .Select(kv => new AppInfo(kv.Key + ".exe", kv.Value / 1024 / 1024)).ToArray();
        var browsers = BrowserMap.Where(b => present.Contains(b.Key))
            .Select(b => b.Value).Distinct().ToArray();
        return (apps, browsers, procs.Length);
    }
}
