# Quiet Hours

Quiet hours are a configurable local-wall-clock window during which a host **stops starting new
upscaling work**. Work that has already been claimed keeps running and finishes normally — nothing is
interrupted, cancelled or requeued.

Only the upscaling pipeline is paused. Ingest, chapter analysis, split detection bookkeeping and the
rest of the standard background queue keep running as usual: quiet hours exist to stop the expensive
GPU/CPU inference, not to make the application inert overnight.

The window is configured per host and there are two independent instances of it:

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

## Semantics

- The window is `[Start, End)` — `Start` is inclusive, `End` is exclusive.
- Times are the **local wall-clock time of the host that owns the setting** (the server for its local
  upscale processor, the worker for its own fetching loop). They are not UTC.
- When `End` is earlier than `Start` the window **wraps past midnight**, which is the common case:
  `22:00 → 06:00` pauses from 22:00 until 06:00 the next morning.
- Only *starting* new upscaling work is delayed. An upscale that is already `Processing` (locally, or
  streamed by a worker) is untouched and completes. The pause is checked between tasks, never during
  one.
- When the window ends, the paused host resumes claiming on its own; no restart or manual action is
  needed.
- An upscale task enqueued during the window simply waits. It is not dropped, cancelled or reordered.

## Configuration

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `QuietHours:Enabled` | bool | `false` | Turns the window on for this host. |
| `QuietHours:Start` | time of day | `22:00:00` | Start of the window, inclusive. |
| `QuietHours:End` | time of day | `06:00:00` | End of the window, exclusive. May be earlier than `Start` to wrap past midnight. |

Both boundaries are `TimeSpan`s, so they are written as a time of day (`22:00`, `22:00:00`) or with
environment variables as `Ingest_QuietHours__Start=22:00`. The `Ingest_`-prefixed environment form is
the recommended route for Docker deployments and works in both hosts.

```json
{
  "QuietHours": {
    "Enabled": true,
    "Start": "22:00",
    "End": "06:00"
  }
}
```

The equivalent environment variables are `Ingest_QuietHours__Enabled`,
`Ingest_QuietHours__Start` and `Ingest_QuietHours__End`.

With no `QuietHours` section at all, every host behaves exactly as it does without this feature.

## Validation

Configuration is validated at startup, so a mistake fails the boot instead of silently leaving the
upscale queue running (or paused) at the wrong hour:

- `Start` or `End` outside `[00:00, 24:00)` fails, and names the key that is wrong. This is checked even
  when `Enabled` is `false`, because an out-of-range value is a typo waiting to be enabled.
- `Enabled = true` with `Start == End` fails: a zero-length window is ambiguous. Set
  `Enabled = false` to turn the feature off.
- `Enabled = false` with `Start == End` is valid (the values are inert).

## Logging

A host logs one `Information` line when it enters the window —

```
Task claiming paused by quiet hours until 06:00
```

— and one when it leaves it (`Task claiming resumed`). The lines are emitted once per pause episode,
not per poll, so a six-hour window produces two lines, not thousands.

## Note on time zones

The window is read from each host's own local clock, so **a worker's window is in the worker's
time zone**, which may differ from the server's (for example a container that runs with `TZ=UTC`
while the server runs in `Europe/Berlin`). Set the same zone on both hosts, or configure the windows
with that difference in mind.
