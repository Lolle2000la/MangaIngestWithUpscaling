# Quiet Hours

Quiet hours are a configurable local-wall-clock schedule during which a host **stops starting new
upscaling work**. Work that has already been claimed keeps running and finishes normally — nothing is
interrupted, cancelled or requeued.

Only the upscaling pipeline is paused. Ingest, chapter analysis, split detection bookkeeping and the
rest of the standard background queue keep running as usual: quiet hours exist to stop the expensive
GPU/CPU inference, not to make the application inert overnight.

The schedule is configured per host and there are two independent instances of it:

| Half | Configured in | What pauses |
| --- | --- | --- |
| Main app (server) | `src/MangaIngestWithUpscaling/appsettings.json` | Starting **upscale tasks that run on the server itself** (the local upscale processor, including the locally-required tasks it drains when the local ML backend is disabled). |
| Remote worker | `src/MangaIngestWithUpscaling.RemoteWorker/appsettings.json` | That **worker's** fetching loop: it stops asking the server for new upscale tasks. |

**The server's window does not block remote workers.** The gRPC task-serving path that hands upscale
tasks to workers stays open through the server's window, so a worker whose own quiet hours are not
active keeps fetching and running tasks while the server's local pipeline sleeps. That split is the
point of the feature: a NAS can stop doing local GPU work overnight while a dedicated GPU machine
keeps upscaling.

Each worker decides for itself: two workers may have different windows, or none at all.

The tasks page (`/tasks`) shows a *Quiet hours — paused until 06:00* badge while the server's own
schedule is open, so a queue that is not moving is explained rather than mysterious. The time is the end
of the current pause, and the badge disappears within 30 seconds of the pause ending. A schedule that
covers every hour has no resume time at all, so the badge then reads just *Quiet hours — paused*.

## Schedule shapes

The daily window is the simple form, and it stays two keys:

```json
"QuietHours": { "Enabled": true, "Start": "22:00", "End": "06:00" }
```

Several windows at once, for example a night window every day plus a longer break at the weekend:

```json
"QuietHours": {
  "Enabled": true,
  "TimeZone": "Europe/Berlin",
  "Windows": [
    { "Start": "22:00", "End": "06:00" },
    { "Start": "12:00", "End": "13:00", "Days": ["Saturday", "Sunday"] }
  ]
}
```

Per-weekday scheduling is `Days` on a window, and it composes with the wrap rule below:

```json
"QuietHours": {
  "Enabled": true,
  "Windows": [
    { "Start": "22:00", "End": "06:00", "Days": ["Friday", "Saturday"] }
  ]
}
```

`Start`/`End` and `Windows` are alternatives: setting both is rejected, and so is setting only one
half of the daily pair. Windows may overlap — they combine as a union — and an empty `Days` list means
every day. Overlapping or touching windows are one continuous pause: with `22:00-06:00` plus
`05:00-07:00`, the page reports "paused until 07:00", not 06:00, because that is when work resumes.

## Semantics

- Every window is `[Start, End)` — `Start` is inclusive, `End` is exclusive.
- Times are local wall-clock times in the host's own zone, or in `TimeZone` when it is set (below).
- When `End` is earlier than `Start` the window **wraps past midnight**, which is the common case:
  `22:00 → 06:00` pauses from 22:00 until 06:00 the next morning.
- **A wrapping window belongs to the day it starts on.** `22:00 → 06:00` with
  `"Days": ["Friday"]` covers Friday 22:00–24:00 *and* Saturday 00:00–06:00. It does not cover
  Saturday evening, and it does not cover Saturday morning under a window that starts on Saturday.
  A non-wrapping window has no such spill: `09:00 → 17:00` on `["Friday"]` is Friday only.
- Only *starting* new upscaling work is delayed. An upscale that is already `Processing` (locally, or
  streamed by a worker) is untouched and completes. The pause is checked between tasks, never during
  one.
- When the schedule opens again, the paused host resumes claiming on its own; no restart or manual
  action is needed.
- An upscale task enqueued during the window simply waits. It is not dropped, cancelled or reordered.
- Daylight saving follows the wall clock, not the elapsed time: a window is read as the configured
  hours on each side of a transition. A window that falls in a spring-forward gap is honoured as
  configured (the skipped hour simply does not exist that day); nothing special-cases it.

## Time zone

```json
"QuietHours": { "Enabled": true, "TimeZone": "Europe/Berlin", "Start": "22:00", "End": "06:00" }
```

`TimeZone` takes an IANA id (`Europe/Berlin`, `Asia/Tokyo`) or a Windows id (`W. Europe Standard
Time`); the host resolves it with `TimeZoneInfo.FindSystemTimeZoneById`, and an id it cannot use fails
the boot. Unset or empty means the host's own local zone.

**The container trap:** a container without `TZ` set runs in UTC, so a `22:00 → 06:00` window silently
becomes 22:00–06:00 **UTC** — it would pause during the local afternoon in Europe and never during the
local night. Setting `TimeZone` in the configuration makes the schedule independent of the host's
environment. A worker's zone may differ from the server's, so set it on each host (or deliberately
rely on their local zones).

## Configuration

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `QuietHours:Enabled` | bool | `false` | Turns the schedule on for this host. |
| `QuietHours:TimeZone` | string | unset (host local) | Zone id the wall clock is read in. |
| `QuietHours:Start` | time of day | unset | Start of the daily window, inclusive. Requires `End`. |
| `QuietHours:End` | time of day | unset | End of the daily window, exclusive. Requires `Start`; earlier than `Start` wraps past midnight. |
| `QuietHours:Windows` | list | empty | Explicit windows, each with `Start`, `End` and optional `Days`. |

Every time of day is a `TimeSpan`, so it is written as `22:00` or `22:00:00`. With environment
variables the same settings are `Ingest_QuietHours__Enabled`, `Ingest_QuietHours__Start`,
`Ingest_QuietHours__End`, `Ingest_QuietHours__TimeZone`, `Ingest_QuietHours__Windows__0__Start`, … —
the recommended route for Docker deployments, and they work in both hosts.

```json
{
  "QuietHours": {
    "Enabled": true,
    "TimeZone": "Europe/Berlin",
    "Start": "22:00",
    "End": "06:00"
  }
}
```

With no `QuietHours` section at all, every host behaves exactly as it does without this feature.

## Validation

Configuration is validated at startup, so a mistake fails the boot instead of silently leaving the
upscale queue running (or paused) at the wrong hour:

- `Start` or `End` outside `[00:00, 24:00)` fails, naming the key — for the daily pair and for every
  window (`QuietHours:Windows[1].End`), even when `Enabled` is `false`. The same goes for a
  zero-length window, which can never be active.
- `Start` without `End` (or the reverse) fails: the pair is one window.
- Setting both `Start`/`End` **and** a non-empty `Windows` list fails: specify one shape or the other.
- `Enabled` with no window at all fails: an enabled-but-inert schedule is a configuration bug.
- An unreadable `TimeZone` fails, echoing the id and the resolver's message.

## Logging

A host logs one `Information` line when it enters the schedule —

```
Task claiming paused by quiet hours until 06:00
```

— and one when it leaves it (`Task claiming resumed`). When the schedule has no end (every hour is
covered) the entry line drops the time: `Task claiming paused by quiet hours`. The lines are emitted once per pause episode,
not per poll, so a six-hour window produces two lines, not thousands.
