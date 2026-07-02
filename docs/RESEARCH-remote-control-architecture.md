# Remotely observing & controlling pc-2 — architecture research

Goal: graduate from agentless PowerShell (remote scheduled-task screenshots, PsExec,
`taskkill /s pc-2 /u localadmin /p`, `msg`) to a **persistent, silently-installed agent +
self-hosted web dashboard** for parental observation and control of a Windows 11 PC on
the home LAN. Parent has a local admin account; the child logs in interactively as a
*different, non-admin* user.

Confidence legend: **[V]** = verified 3-0 against Microsoft primary docs in the research
pass. **[K]** = established from general expertise / fetched-but-not-vote-verified sources;
treat as reliable but worth a quick test before depending on it.

---

## 1. The one constraint that shapes everything: Session 0 isolation

**[V]** Since Windows Vista, only system processes and services run in non-interactive
**Session 0**. The interactive user (the child) logs on to **Session 1+**. A service therefore
has **no user desktop**. Window messages (`SendMessage`/`PostMessage`) only pass between
processes on the *same* desktop, so a service cannot directly:

- take a screenshot of the child's screen,
- send `SC_MONITORPOWER` to turn his monitor off,
- read his idle time, or
- pop a message box on his desktop.

**[V]** Even impersonating the child's token (`WTSQueryUserToken`/`DuplicateTokenEx`) does
**not** move the service out of Session 0. A service calling `GetLastInputInfo` always
reports "no input since boot" — useless for idle detection. (Raymond Chen, *The Old New
Thing*, 2026-06-18, confirmed this verbatim.)

### The canonical solution: service + per-session helper + IPC

**[V]** Microsoft's documented pattern (AskPerf "Session 0 Isolation" whitepaper;
`murrayju/CreateProcessAsUser` reference implementation):

```
[ LocalSystem service, Session 0 ]                 [ Helper agent, the child's Session ]
  - persistent, auto-start                            - launched BY the service
  - holds SE_TCB_NAME privilege                        - lives ON winsta0\default
  - exposes local HTTP/WebSocket API   <-- named -->   - GetLastInputInfo (idle)
  - talks to your web dashboard            pipe        - screen capture
                                                        - SC_MONITORPOWER (monitor off)
                                                        - message popups
```

The service launches the helper into the active console session with this exact API
chain **[V]**:

1. `WTSGetActiveConsoleSessionId()` — session ID attached to the physical console (the child's).
2. `WTSQueryUserToken(sessionId, &token)` — **requires running as LocalSystem +
   SE_TCB_NAME**; an elevated admin account is *not* sufficient.
3. `DuplicateTokenEx(...)` — make a primary token; set `TokenSessionId`,
   `lpDesktop = "winsta0\\default"`.
4. `CreateEnvironmentBlock(...)` then `CreateProcessAsUser(...)` — spawn the helper in
   the child's session.

Everything session-bound (idle, screenshot, monitor-off, popups) happens **in the
helper**; the service is the privileged brain and the network endpoint. They talk over a
**named pipe** (Microsoft explicitly recommends RPC or named pipes for service<->app IPC;
window messages fail across the boundary) **[V]**.

This is the single most important takeaway: **build the service+helper split once and it
solves screenshots, idle detection, monitor control, and messaging together.**

---

## 2. Capability-by-capability mechanisms

### Presence — is the machine on / is someone using it
- **On/off from your PC:** `Test-Connection pc-2` (ping). To *turn it on*: Wake-on-LAN
  magic packet (needs WoL enabled in BIOS/NIC). **[K]**
- **Who's logged in / lock state:** the helper registers `WTSRegisterSessionNotification`
  and handles `WM_WTSSESSION_CHANGE`; event codes `WTS_SESSION_LOGON (0x5)`,
  `LOGOFF (0x6)`, `LOCK (0x7)`, `UNLOCK (0x8)`. Map session ID -> user via
  `WTSQuerySessionInformation(WTSUserName)`. **[V]**
- **Idle time:** helper calls `GetLastInputInfo` *in the child's session* and reports the tick
  delta to the service over the pipe. **[V]** (Cannot be done from the service.)

### Forcing monitors off (idle or scheduled)
- **Turn off:** helper sends `WM_SYSCOMMAND (0x0112)` with `wParam = SC_MONITORPOWER
  (0xF170)`, `lParam = 2` (full power-off; `1` is unreliable). Must originate from a window
  in the child's session. **[V] mechanism / [K] lParam=2 detail.**
- **Reality check:** any mouse/keyboard input wakes the monitor back instantly. You cannot
  "hold" a monitor off against an active user by re-sending — it just flickers. For real
  enforcement during bedtime, **lock the session** (`LockWorkStation` from the helper) or
  **log him off** (`WTSLogoffSession`), then optionally monitor-off on top. **[K]**
- **Keep awake (opposite need):** `SetThreadExecutionState(ES_CONTINUOUS |
  ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED)` keeps display/system on; clear with
  `ES_CONTINUOUS` alone. This **cannot** turn a monitor off — different mechanism. **[V]**

### Screenshots from the service
- Helper-in-session captures the desktop (`CopyFromScreen`, same code as your current
  `Ss.ps1`) and streams the PNG back over the pipe. Far more reliable than today's
  schtasks/VBS dance, and works with no one at the keyboard. **[K]**
- **Blocked cases:** the **secure desktop** — UAC prompts, Ctrl-Alt-Del, and the lock
  screen — cannot be captured (by design); you'll get black. **[K]** Detect lock via the
  `WM_WTSSESSION_CHANGE` lock event and surface "screen locked" in the dashboard instead of
  a black image.
- Multi-monitor: capture `SystemInformation.VirtualScreen` (you already do this in
  `Get-RemoteScreenshot.ps1`).

### Firewall / per-app network blocking
- **Per-app outbound block [K]:**
  ```powershell
  New-NetFirewallRule -DisplayName "block-discord" -Direction Outbound `
    -Program "C:\Users\the-child\AppData\Local\Discord\app-1.0.9999\Discord.exe" `
    -Action Block -Profile Any
  ```
- **The Discord problem:** its exe lives under per-user `AppData\Local\Discord\app-<version>\`
  and the version folder changes on every update, so a fixed `-Program` path rots. Options:
  (a) the agent re-scans and rewrites the rule on a schedule; (b) block by **domain/DNS**
  instead (more robust). UWP/Store apps: use `-Package <AppSID>` rather than a path. **[K]**
- **DNS-based blocking (recommended complement):** Pi-hole or NextDNS, or router-level
  rules, block Discord/Roblox/etc. by domain regardless of where the exe lives or how it
  updates — and cover phones/tablets too. Pair app-path blocks (local, instant) with DNS
  blocks (robust, network-wide). **[K]**
- **Schedule-driven:** enable/disable named rules on a timer from the agent
  (`Enable-NetFirewallRule`/`Disable-NetFirewallRule -DisplayName ...`) for school hours /
  bedtime. **[K]**

### Parental controls that survive relaunch
Your current pain: `taskkill` kills the app, it just reopens. Better:
- **Image File Execution Options "debugger" trick [K]:** set
  `HKLM\...\Image File Execution Options\Discord.exe\Debugger` to a no-op
  (e.g. a stub that exits, or a "blocked" notifier). The OS refuses to launch the named exe
  at all. HKLM = a non-admin child can't undo it. Lightweight, no Enterprise SKU needed.
  Caveat: matches by exe *filename*, easy to bypass if the child renames/relocates the exe.
- **AppLocker / WDAC [K]:** policy-grade allow/deny by path/publisher/hash. Stronger and
  centrally enforced, but AppLocker wants Enterprise/Education; WDAC is broadly available
  but heavier to author. Best if you want a true allow-list ("only these apps run").
- **Remote lock/logoff:** `LockWorkStation`, `shutdown /l`, or `WTSLogoffSession` from the
  helper; expose as dashboard buttons.
- **Microsoft Family Safety:** built-in app/time/web limits and reports for a child MS
  account — but it's cloud-tied, sometimes flaky, and you don't control it; usable as a
  backstop, not the core of a custom system. **[K]**

---

## 3. Silent install & re-triggerable deployment

- **Install/launch as SYSTEM remotely [V]:** `PsExec -s` runs the remote process in the
  LocalSystem account; `-i <session>` makes it interact with a session's desktop
  (defaults to the console session — exactly the child's). Admin on the target is still required
  to deploy.
- **Register the service:** ship a small installer (or `sc.exe \\pc-2 create ...`,
  or a WiX/MSI with `msiexec /qn`). MSI gives you clean repair/upgrade/uninstall and
  silent `/qn`. **[K]**
- **Auto-restart / tamper resilience [V]:** `sc failure <svc> reset= 0 actions=
  restart/5000/restart/5000/restart/10000` — up to three actions for the 1st/2nd/3rd
  failure. Combine with `sc sdset` to deny the non-admin child `STOP`/`DELETE` on the
  service. **[K]**
- **Re-trigger installation:** keep the installer on a share; a scheduled task or a tiny
  watchdog re-runs it if the service is missing. MSI self-repair (`msiexec /fa`) also works.
- **Code signing / SmartScreen [V]:** **EV certs no longer bypass SmartScreen** (the OIDs
  were removed in Aug 2024) — don't pay the EV premium for that reason. A freshly signed
  binary still warns until its hash/publisher reputation accrues (no fixed threshold; weeks
  + hundreds of clean installs). For a single home PC this is a non-issue — dismiss one
  prompt. If you sign at all, use a **consistent OV cert** so reputation carries across
  versions; unsigned rebuilds reputation from zero each version.

---

## 4. Build vs buy

### Option A — Extend MeshCentral (recommended starting point) [K]
[MeshCentral](https://github.com/Ylianst/MeshCentral) is an open-source, self-hostable
(Node.js) remote management server with a **web dashboard** and a Windows **agent that
installs as a service and already solves Session 0** for you: live remote desktop, remote
screenshot, file transfer, remote terminal/PowerShell, presence (online/offline, logged-in
user), and wake-on-LAN. LAN-only / fully private if you want. It gives you **~70% of the
"observe + control + deploy" substrate for free** and you never write the service+helper
plumbing.

What it does **not** do: parental policy — app/firewall blocking, monitor-off schedules,
bedtime enforcement, time accounting. But MeshCentral can **run remote commands/scripts**
on the agent, so you layer your policy as scripts it triggers:
- bedtime/school -> MeshCentral runs your firewall + IFEO + lock/logoff scripts,
- screenshots & presence -> native MeshCentral,
- dashboard -> MeshCentral's UI, optionally plus a thin custom page that calls its API.

This is the **lowest-effort path to everything you asked for**, and it's the build-vs-buy
fork the research explicitly left open (no verified product claims — verify by standing it
up on the LAN).

Adjacent tools: **Tactical RMM** (heavier, MeshCentral-based agent + more automation),
**RustDesk**/**Guacamole** (remote desktop only, no policy). **NextDNS/Pi-hole** for the
DNS layer. **Microsoft Family Safety** as a cloud backstop.

### Option B — Build a custom service+helper+dashboard
Full control, no dependency, exactly the features you want, and a genuinely good learning
project given you already write C# P/Invoke and PowerShell. But you re-implement the
Session 0 plumbing, IPC, deployment, and a dashboard from scratch. Stack:
**C#/.NET worker service** (the LocalSystem brain) + **WinForms/WPF helper** (per-session)
+ **named-pipe IPC** + an **ASP.NET minimal-API + WebSocket** local endpoint that your web
dashboard talks to. `murrayju/CreateProcessAsUser` is your reference for the helper launch.

**Recommendation:** stand up MeshCentral on the LAN this week and see how much of the list
it covers out of the box. Keep the custom service on the table for the parental-control
policy layer (firewall scheduling, IFEO blocks, monitor-off/lock) that MeshCentral won't
do — triggered *through* MeshCentral rather than reinvented around it.

---

## 5. Security & hygiene (fix these regardless of path)

- **Kill the plaintext credentials.** `localadmin`/`REDACTED` hardcoded in `bedtime.ps1` /
  `during-school.ps1` is the biggest current liability. The agent model removes the need
  entirely: the service runs as LocalSystem locally, and your dashboard authenticates to
  the service with a token, not a Windows password sent over the wire.
- **Least privilege + tamper resistance:** service as LocalSystem; child stays non-admin;
  `sc sdset` to deny him STOP/DELETE; firewall/IFEO live in HKLM he can't edit; `sc failure`
  auto-restart if he force-kills the helper.
- **Audit logging:** log every action (screenshot taken, app blocked, monitor-off, message
  sent) to a file/event log the child can't clear — useful for both debugging and
  accountability.
- **Transparency/consent:** monitoring your own minor child's device is legal and normal,
  but covert total surveillance of a teen erodes trust fast. Norm is to be open that the PC
  is managed (time limits, bedtime, some app blocks) even if you don't advertise every
  capability. Your call as the parent; worth a deliberate decision rather than a default.

---

## 6. Verified-source appendix

All **[V]** items trace to Microsoft primary docs, each confirmed 3-0 in adversarial
verification:

- Session 0 isolation & service+helper pattern — AskPerf "Session 0 Isolation" whitepaper;
  `github.com/murrayju/CreateProcessAsUser`.
- `WTSQueryUserToken` requires LocalSystem + SE_TCB_NAME — MS Learn (updated 2025-07-01).
- `WTSGetActiveConsoleSessionId`, `WM_WTSSESSION_CHANGE` codes, `WTSSendMessage` — MS Learn.
- `GetLastInputInfo` is session-specific; impersonation doesn't escape Session 0 —
  MS Learn + Raymond Chen, The Old New Thing (2026-06-18).
- `SetThreadExecutionState` flags/semantics — MS Learn (cannot power monitor off).
- `PsExec -s`/`-i`, `sc failure` recovery, SmartScreen/EV reputation reality — MS Learn /
  Sysinternals.

**Not verified in this pass (treat as [K], confirm by testing):** MeshCentral/Tactical RMM
capabilities, `New-NetFirewallRule` app-blocking specifics, IFEO/AppLocker relaunch
prevention, screenshot secure-desktop behavior, dashboard stack. The mechanisms are
well-established; they simply weren't among the 25 claims sampled for 3-vote verification.
