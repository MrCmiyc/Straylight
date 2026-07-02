# parental-tools

Optional, **generic** client-side scripts for a parent or operator running a home network.
They are independent of the Straylight agent — plain PowerShell you can run by hand, from a
scheduled task, or **push and run through the agent's command channel once it's installed on a
machine** (that's a good real use of the agent: deliver a one-shot script to an active box and
run it in the user's session or as SYSTEM, without touching the console).

Nothing here is tied to a specific DNS product. These are the *client* half of a
"DNS-blackhole" setup — **bring your own resolver** (Pi-hole, NextDNS, AdGuard Home, Technitium,
a filtering router, whatever). Hostnames are aliased: `pc-2` = a target machine.

## The scripts

- **`Disable-DoH.ps1`** — the important one. A DNS blackhole only works if the machine actually
  asks *your* resolver. Modern Windows and browsers quietly upgrade DNS to **DoH**
  (DNS-over-HTTPS) straight to Cloudflare/Google over 443, bypassing any LAN-level block. This
  turns DoH off machine-wide (Windows resolver + Chrome/Edge/Firefox policy) and can optionally
  pin the adapter DNS to your resolver. HKLM-only, so it's safe to run as SYSTEM/remotely.
  `-Status` to inspect, `-Revert` to undo.
- **`Block-DiscordVoice.ps1`** — example of a scoped, process-level firewall block: kills
  Discord **voice** (UDP) while leaving text chat and games untouched. Re-resolves Discord's
  per-version path each run, so re-apply after updates. A template for "block one app's traffic
  narrowly," not a full solution.
- **`Get-DeviceActivity.ps1`** — agentless presence check over SMB/PsExec: is the box online, is
  someone active (idle time), and what watchlisted apps are running. Read-only. Useful before
  the agent is installed, or as a cross-check. Once the agent is running, its MQTT telemetry
  supersedes this.

## Pattern: scheduling a block

The scripts here are the *client* side. The *enforcement* side — actually toggling the blocklist
on a schedule (e.g. Discord during school/bedtime) — lives on your DNS server and is specific to
whichever resolver you run, so it's intentionally **not** in this repo. The shape is:

1. A scheduled task (on the server, or on a PC via the agent) fires at the block/unblock times.
2. It calls your resolver's API to add/remove domains from a blocked zone (blocking a domain
   blocks its subdomains, covering CDNs and voice endpoints).
3. Store the API token out of source control — DPAPI-encrypted at rest, decrypted at run time.

Keep the resolver-specific glue in your own private location, not here.
