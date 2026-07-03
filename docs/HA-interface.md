# Straylight agent — Home Assistant / MQTT interface

Spec for building HA dashboards/automations against the Straylight PC agents.

**Broker:** Mosquitto on `mqtt-host:1883` (anonymous, no TLS).
**Devices:** one per PC, **auto-created via MQTT discovery** — no manual YAML needed; they
appear under Settings → Devices. Per-host slug replaces `<host>` below:
`pc-1`, `pc-2`, `pc-3`.

---

## Topics
| purpose | topic | notes |
|---|---|---|
| State | `<host>/telemetry/state` | JSON, **retained** |
| Availability | `<host>/telemetry/status` | `online` / `offline` (MQTT Last-Will), retained |
| Discovery | `homeassistant/<component>/<host>/<id>/config` | retained, auto |
| Commands | `<host>/cmd/<name>` | you publish these |

## State JSON (`<host>/telemetry/state`)
| field | type | meaning |
|---|---|---|
| `ts` | string | ISO local timestamp of the sample |
| `online` | bool | true while publishing |
| `user` | string \| null | logged-in user (null if none) |
| `state` | string | session state: `Active`, `Disc` (disconnected), `Idle`, ... |
| `idle_seconds` | int \| null | seconds since last input per **`quser`** (v1). Counts *injected* input, so an autoclicker keeps this near 0. |
| `idle_real_seconds` | int \| null | seconds since last **real (non-injected) input**, from the idle_v2 watcher. `null` when v2 is off or the watcher isn't alive. An autoclicker does NOT reset this. |
| `idle_source` | string | which idle drove `active`: `real-input` (v2 on + watcher live) or `quser` (fallback) |
| `active` | bool | Active session AND the **decisive** idle (real-input when available, else quser) under threshold |
| `active_since` | int \| null | epoch seconds when the current `active` streak began; persisted so it survives self-updates / nightly restarts (null when inactive) |
| `browsers` | string[] | browsers currently running |
| `top_apps` | array | top 5 by memory: `[{ "name": "...", "mem_mb": N }]` |
| `process_count` | int | total processes |
| `poll_interval_min` | int | current sample cadence, minutes |
| `dimmed` | bool | screen-dim active (auto-clears when real input wakes it) |
| `version` | string | agent build (e.g. `0.8.3`) |
| `updating` | bool | true while a self-update is downloading/swapping (drives the HA update card's "Installing…") |
| `max_version` | string | version pin — the max this box will self-update to (`""` = no cap) |
| `update_latest` | string | pin-aware "latest for this host"; the update card's `latest_version` reads this |
| `idle_v2` | bool | when ON, a persistent low-level hook tracks real (non-injected) input → drives `idle_real_seconds`/`active` and enables Screen dim's auto-wake |
| `brightness` | int | current monitor brightness %, or `-1` if unreadable (no DDC / display off) |

## Commands (publish to `<host>/cmd/<name>`)
| name | payload | effect | retain? |
|---|---|---|---|
| `message` | plain text | friendly **toast** | **NO** |
| `message` | `{"text":"...","urgent":true,"title":"Parent"}` | popup if `urgent` (over full-screen), else toast; `title` sets the header/caption (default "Message"); `urgent` defaults false | **NO** |
| `poll_interval` | integer minutes `5`–`60` (snaps to 5) | change sample cadence | yes |
| `dim` | `ON`/`OFF` | screen dim (DDC brightness 0). **Requires `v2` ON**; auto-restores on real input | yes |
| `v2` | `ON`/`OFF` | enable real-input auto-wake (the safety that lets dim be used) | yes |
| `update` | `go` | download + verify + self-install the latest build (see **Self-update**) | **NO** |
| `pin` | version string (or empty) | cap self-updates at this max version; empty clears the cap | **yes** (retained) |

> Retain rule: **settings** (poll_interval, dim, v2) are retained so setpoints survive a
> reconnect. **Messages must never be retained** — a retained message re-fires on every
> agent restart/reboot.
>
> **Delivery (0.8.2+):** the agent subscribes `<host>/cmd/#` at **QoS1** with a **persistent
> session** (stable client id `straylight-<node>`, clean-session off), so a command published while
> it's briefly offline (nightly restart, reconnect) is queued by the broker and delivered on
> return. **Publish commands with `qos: 1`** for that guarantee; QoS0 still works while connected.
>
> **Message formatting (0.6.0+) — tiny markdown subset:**
> - `\n` → line break (type literal `\n` in HA's single-line box).
> - Line starting with `- `, `* `, or `• ` (marker + a space) → bullet, rendered as `• `.
> - `**bold**` → **stripped** (markers removed, text shown plain). Inline bold is NOT renderable
>   in a Windows toast or the standard popup (MessageBox). True bold/rich text would require
>   replacing the popup with a custom formatted window (separate agent task).
>
> Rendering by type: **toast** (`urgent:false`) shows bullets + line breaks (compact toast shows
> ~3–4 lines, full text in the Action Center); **popup** (`urgent:true`) shows all lines +
> bullets. Neither shows inline bold today.
>
> Core HA MQTT `text` is single-line only; a true multi-line input box needs a **custom Lovelace
> card** with a `<textarea>` calling `mqtt.publish` (front-end task).

## Interactive messages & replies (request/response)

A prompt can ask for an answer; the reply is delivered back durably so a poller/bot that
connects *later* still gets it (retained, per-`id`).

**Ask — HA → agent**, publish to `<host>/cmd/message` (not retained). Plain text = friendly
toast, no reply. Or JSON:
```json
{
  "id":     "dinner1",              // correlation id — REQUIRED to get a reply
  "title":  "Parent",                  // optional header/caption (default "Message")
  "text":   "What's for dinner?",   // body; supports \n and the markdown subset
  "urgent": false,                  // true = forced popup, false = toast
  "reply":  true,                   // free-text box (IGNORED when buttons are given)
  "buttons":[                       // a fixed choice; when present there is NO text box
     { "id":"yes","name":"Yes","hint":"hover tooltip" },
     { "id":"no", "name":"No", "hint":"hover tooltip" }
  ]
}
```

**Reply — agent → HA**, published **retained** to `<host>/reply/<id>` (id echoed):
```json
{
  "id":"dinner1", "question":"What's for dinner?", "title":"Parent",
  "button":"no", "button_name":"No", "text":"pizza",
  "dismissed":false, "ts":"2026-07-02T21:43:02"
}
```
- `question` = the ask's text echoed back, so a log/voice reads "What's for dinner? → pizza"
  without a lookup; `title` = the ask's title.
- `button`/`button_name` = clicked button (or `null`); `text` = typed text (or `""`);
  `dismissed:true` if closed without answering.
- **Retained**, so a bot/task that subscribes later still receives it — no missed replies.
- **The consumer clears it after reading**: publish an empty payload with `retain=true` to
  `<host>/reply/<id>`.
- Also emitted **non-retained** to `<host>/telemetry/reply` as a live event (optional dashboard sensor).
- No `id` on the ask → no reply is published.
- **The reply window (built, 0.8.2+):** non-urgent asks render as a **toast** bottom-right that
  does not steal focus; `urgent:true` centers it and grabs focus instead. **Buttons are a fixed
  choice — no text box**; a text box appears only for a free-text ask (`reply:true` with no
  buttons). Renders real `**bold**` + bullets.

## Self-update (0.8.3+)

The agent updates itself from a LAN release host, driven by HA's native **update** entity.

- **Release host** (e.g. `http://mqtt-host/straylight/`): serves `straylight-agent.exe`. Each
  agent's `update_base` in `mqtt.json` points at it.
- **Announce (retained):** publish `straylight/latest` =
  `{"version":"0.8.4","sha256":"<hex>","notes":"…"}`. HA's update entity compares each host's
  installed `version` against this; the sha is the integrity anchor. Use monotonic/semver versions.
- **Install:** HA's Install button publishes `go` to `<host>/cmd/update` (qos 1).
- **Agent applies:** downloads `update_base/straylight-agent.exe`, verifies its SHA-256 against the
  sha from the **MQTT** `straylight/latest` (a *different* trust domain than the web host — so a
  compromised web host alone can't push a malicious build), then a detached helper stops the
  service, backs up the current exe (`.bak`), swaps in the new one, restarts, and **rolls back** if
  it doesn't reach RUNNING.
- **Idempotent:** already at `latest.version` → no-op, so a late-delivered (QoS1) update command is
  safe. Progress surfaces via the `updating` telemetry field.
- **Version pin:** publish a max version (retained) to `<host>/cmd/pin` to cap a box's self-updates
  (staged rollout / hold a box back); empty clears it. The agent refuses to go above the pin and
  reports `update_latest` (pin-aware) so the update card shows the truth, not a phantom update.

## Auto-discovered entities (per device)
- **binary_sensor**: Active
- **sensor**: Idle (unit s, quser/v1), Real idle (unit s, real-input when v2 on), Idle source, User,
  Session, Top App (+ full list as `apps` attribute), Browsers, Processes, Brightness (%),
  Agent version (diagnostic)
- **number**: Poll interval (5–60 min)
- **text**: Message (type text and send → toast; for urgent, publish the JSON form); Max version
  (`text.<host>_max_version`) — type a version to cap self-updates, clear for no cap
- **switch**: Screen dim (`switch.<host>_screen_dim`), Idle v2 (`switch.<host>_idle_v2`)
- **update**: Agent update (`update.<host>_update`) — installed `version` vs `straylight/latest`, Install → `cmd/update`

Entity IDs follow `<component>.<host>_<id>`, e.g. `text.pc_2_message`,
`number.pc_2_poll_interval`, `binary_sensor.pc_2_active`, `sensor.pc_2_version`.

## Sending a message from an automation (example)
```yaml
# friendly toast
- service: mqtt.publish
  data: { topic: "pc-2/cmd/message", payload: "dinner in 10" }
# urgent popup
- service: mqtt.publish
  data: { topic: "pc-2/cmd/message", payload: '{"text":"bed now, save your game","urgent":true}' }
```
