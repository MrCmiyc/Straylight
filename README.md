# Straylight

A small Windows agent plus tooling for observing and controlling home PCs over the LAN,
reporting to **Home Assistant** over **MQTT**. Built for parental monitoring/control of a
family gaming PC — presence, gentle messaging, schedule-driven blocking, and screen dimming.

> Hostnames/names in docs are aliased: **`mqtt-host`** = the MQTT/DNS home server,
> **`pc-1`** = the admin box, **`pc-2`/`pc-3`** = target machines.

## Layout

- **`agent/Straylight.Agent/`** — self-contained .NET 10 Windows service (the core):
  - Telemetry every N minutes: logged-in user, session state, idle time, active/away, running
    browsers, top apps by memory, process count, brightness, agent version.
  - **MQTT auto-discovery** → each machine appears in Home Assistant as a device with sensors,
    a Poll-interval number, a Message text box, and Screen-dim / Idle-v2 switches. No HA YAML.
  - **Commands:** set poll interval; send a **toast** (friendly) or **top-most popup** (urgent,
    over full-screen) message with a small markdown subset (bullets, line breaks) and a custom
    title; **screen dim** via DDC/CI brightness that **auto-wakes on real input** (a low-level
    input hook ignores software auto-clickers so an AFK bot can't keep the screen lit).
  - LocalSystem service + per-session helper (`CreateProcessAsUser`) for the session-bound bits
    (DDC, input hook) that a Session-0 service can't do directly.
- **`agent/install-service.ps1`** — install/update the service locally or remotely (PsExec).
- **PowerShell tooling** (repo root) — Technitium DNS block scheduling, DoH disable on a client,
  activity checks, and diagnostics (remote-access scan, AFK-app trace, DDC probes).
- **`homeserver/`** — scripts that run on the MQTT/DNS home server (Technitium block toggling,
  DHCP lease lookups) driven by cron.
- **`docs/`** — [HA-interface.md](docs/HA-interface.md) (the MQTT contract) and
  [INSTALL.md](docs/INSTALL.md) (build + local/remote install).

## Secrets

None are committed. Broker credentials and API tokens live only on the machines
(`C:\ProgramData\Straylight\mqtt.json`, DPAPI-encrypted `*.pass`/token files, or env vars).

## Status

Working: telemetry + HA discovery, poll-interval control, toast/popup messaging (per-message
urgent, multi-line, titles, markdown subset), screen dim with real-input auto-wake, version
stamping. In progress: an interactive reply window (text + buttons) and rich/bold rendering.
