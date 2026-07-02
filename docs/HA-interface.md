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
| `idle_seconds` | int \| null | seconds since last input (null = unknown) |
| `active` | bool | Active session AND idle under threshold |
| `browsers` | string[] | browsers currently running |
| `top_apps` | array | top 5 by memory: `[{ "name": "...", "mem_mb": N }]` |
| `process_count` | int | total processes |
| `poll_interval_min` | int | current sample cadence, minutes |
| `dimmed` | bool | screen-dim active (auto-clears when real input wakes it) |
| `version` | string | agent build (e.g. `0.7.0`) |
| `idle_v2` | bool | real-input auto-wake enabled — required before Screen dim will engage |
| `brightness` | int | current monitor brightness %, or `-1` if unreadable (no DDC / display off) |

## Commands (publish to `<host>/cmd/<name>`)
| name | payload | effect | retain? |
|---|---|---|---|
| `message` | plain text | friendly **toast** | **NO** |
| `message` | `{"text":"...","urgent":true,"title":"Parent"}` | popup if `urgent` (over full-screen), else toast; `title` sets the header/caption (default "Message"); `urgent` defaults false | **NO** |
| `poll_interval` | integer minutes `5`–`60` (snaps to 5) | change sample cadence | yes |
| `dim` | `ON`/`OFF` | screen dim (DDC brightness 0). **Requires `v2` ON**; auto-restores on real input | yes |
| `v2` | `ON`/`OFF` | enable real-input auto-wake (the safety that lets dim be used) | yes |

> Retain rule: **settings** (poll_interval, dim, v2) are retained so setpoints survive a
> reconnect. **Messages must never be retained** — a retained message re-fires on every
> agent restart/reboot.
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
  "reply":  true,                   // true = show a text box
  "buttons":[                       // optional; renders buttons (+ text box if reply:true)
     { "id":"yes","name":"Yes","hint":"hover tooltip" },
     { "id":"no", "name":"No", "hint":"hover tooltip" }
  ]
}
```

**Reply — agent → HA**, published **retained** to `<host>/reply/<id>` (id echoed):
```json
{
  "id":"dinner1", "button":"no", "button_name":"No",
  "text":"pizza", "dismissed":false, "ts":"2026-07-02T21:43:02"
}
```
- `button`/`button_name` = clicked button (or `null`); `text` = typed text (or `""`);
  `dismissed:true` if closed without answering.
- **Retained**, so a bot/task that subscribes later still receives it — no missed replies.
- **The consumer clears it after reading**: publish an empty payload with `retain=true` to
  `<host>/reply/<id>`.
- Also emitted **non-retained** to `<host>/telemetry/reply` as a live event (optional dashboard sensor).
- No `id` on the ask → no reply is published. The reply UI (text box / buttons) requires the
  custom window (in progress); plain toast/popup messaging works today.

## Auto-discovered entities (per device)
- **binary_sensor**: Active
- **sensor**: Idle (unit s, duration), User, Session, Top App (+ full list as `apps` attribute),
  Browsers, Processes, Brightness (%), Agent version (diagnostic)
- **number**: Poll interval (5–60 min)
- **text**: Message (type text and send → toast; for urgent, publish the JSON form)
- **switch**: Screen dim (`switch.<host>_screen_dim`), Idle v2 (`switch.<host>_idle_v2`)

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
