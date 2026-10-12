# Upscaling Memory Model & Tile Size Derivation

This document explains how the upscaler decides how large a tile it may run, and where the
numbers come from. It exists so the tile split is a *derived* quantity instead of an arbitrary
constant, and so the derivation can be re-checked when the model set changes.

Everything here was measured on the hardware this repository develops against:

| | |
|---|---|
| CPU | 32 cores |
| RAM | 64 GB |
| GPU | AMD Radeon RX 9070 XT (Navi 48, gfx1201) |
| VRAM | 16 GB (17095983104 B) |
| GTT | 32 GB (33664528384 B) |
| Execution provider | ONNX Runtime 1.29 + WebGPU EP (Dawn/Vulkan, RADV) |
| Models | fp16 ONNX exports (the `*_fp16` / `*_bf16` files all have `tensor(float16)` I/O) |

Note that the *fp32* ships only as the `..._ONNX.zip` variants, while the local install here holds
the fp16 set; the fp32 path is still supported by the formula (it just doubles the element size).

---

## 1. What actually costs memory

For one inference of a single tile, device memory consists of three parts:

```
peak(model, tile) = W                  model weights (initializers)
                  + A(n)              activation working set of one tile with n pixels
                  + C                 session/arena reservation of the execution provider
```

`W` and `C` do not depend on the tile size. `A` does, and it is the only term we can trade tile
size against. Historically the code *estimated* `A` with per-architecture "multipliers" that were
never validated, then clamped the result to 512/640/1024 px so that a wrong estimate could not
blow up. The clamp threw away throughput: a 16 GB GPU never got to run a 4x model with tiles
larger than 512 px, which is roughly a quarter of the tile area the card can actually host.

### 1.1 The activation working set is quadratic — always

A graph-level liveness sweep over each shipped model (below) shows that the peak of one inference
is always reached at **output resolution**: in the ESRGAN-family models it is the upsampler
activations (64 channels at `scale²·n` pixels), in the transformer models (DAT2/HAT/FDAT) it is the
attention maps and the residual stream at output resolution. Because every one of those tensors
scales with `n · scale²`, the whole set does too:

```
A(n, scale) = C_arch · scale² · n · elementSize
```

There is no term that only scales with `n`, no cliff at some magic tile size, and no plat —
so the price of a tile is a pure quadratic, and the largest tile that fits a budget is just the
square root of the budget. That is what makes removing the clamp safe: the estimate is not
"roughly right up to 512 px and wrong after that", it is right at every size.

`C_arch` is a **live-channel count at output resolution**. The table in §3 gives the measured
value per architecture, which already folds in the execution-model overhead of the WebGPU EP
described next.

### 1.2 The execution model adds a constant multiple

The analytic working set is a lower bound. ORT's WebGPU EP runs the graph with extra buffers that
a pure dataflow sweep does not model:

* the exports contain `Cast` to fp32 around `Resize`, so the upsampler holds **two** copies of the
  same large tensor (fp16 + fp32);
* ORT re-packs activations to NCHWc layout (`InsertedPrecisionFreeCast` kernels), which allocates
  a second buffer per repack;
* the owners of those buffers do not release them promptly: the ONNX Runtime arenas keep their
  high-water mark, and on the CUDA provider the BFC arena's default extension strategy doubles each
  allocation. (`arena_extend_strategy` is set to `kSameAsRequested` on the CUDA provider; the
  session-level equivalent that used to be set here did nothing, because `AddSessionConfigEntry` does
  not feed provider options. The shrinkage key is a `RunOptions` key and is only passed where a
  `RunOptions` already exists — the detector — while the upscaler frees the whole session at the
  chapter boundary and on a tile-shape change.)

Measured against the analytic sweep, the observed footprint is a stable multiple:

| architecture | analytic `C_arch` (live ch) | measured fp16 | measured fp32 |
|---|---|---|---|
| SPAN | 96 | 315 | 158 |
| ESRGAN | 128 | 598 | 463 |
| FDAT_M | 90 | 462 | 590 |
| DAT2 | 203 | 1060 | 1167 |
| FDAT_XL | 135 | 660 | 882 |

The ratio is constant over three orders of magnitude of tile size, so instead of modelling each
source separately the code stores the **measured** `C_arch` (§3) and documents the analytic
sweep as the derivation behind it. There are two columns because the fp16 and fp32 exports of one
architecture are not related by the element size alone (see §3).

### 1.3 Session reservation `C`

A freshly created session reserves ~380 MB of VRAM on this card before any inference runs (ORT
WebGPU EP arenas, Dawn/Vulkan driver-side allocations and pipeline caches). It is subtracted from
the budget once, as `OnnxTiler.SessionReservationBytes` (384 MiB).

### 1.4 One session at a time

`OnnxSessionFactory` caches exactly one model (`MaxCachedSessions = 1`), because a second resident
model doubles `C` for no gain while consecutive chapters are the common case. The page-break
detector shares the same factory, so on a worker that runs detection and upscaling at the same time
the two evict each other's model: every switch pays a reload. That is a throughput cost, not a
correctness one — `EnterInferenceScope` takes a **read** lock, so the two do run concurrently rather
than being serialised, and the eviction is made safe by retiring the session and disposing it only
once every scope has drained (`OnnxSessionFactory.RetireLocked` / `DrainRetired`) — and it is the
deliberate price of keeping `C` to a single model.

---

## 2. How the split is derived

A tile *size* is only ever a way of selecting a **grid**: `ceil(width / n)` columns and
`ceil(height / n)` rows, each then shrinking back to `ceil(width / columns)`. The budget is therefore
spent on the grid itself, not on a square tile side.

```csharp
long weights = EstimateModelBytes(modelNameOrPath, modelSizeBytes);
double bpp   = C_arch · scale² · (isFp16 ? 2 : 4);      // bytes per input pixel
long budget  = memoryBudgetBytes − sessionReservation;   // the session holds this either way

// the whole page in one inference, only with headroom
if (weights + bpp · paddedTilePixels(1x1) <= 0.9 · budget) return 1x1;

// otherwise: every grid whose single tile fits the budget ...
// floor, not ceil, so the last column cannot shrink a tile below the quality floor (see Notes)
for (columns = 1 .. floor(w / MinimumTileSize))
for (rows    = 1 .. floor(h / MinimumTileSize))
{
    tile = (ceil(w / columns), ceil(h / rows));
    if (weights + bpp · paddedTilePixels(tile) > budget) continue;

    context = min(min(tile.width, tile.height), QualityTilePixels);  // 512 px = full quality
    work    = columns · rows · paddedTilePixels(tile);                // overlap + alignment included

    // most context first, then the cheapest grid that delivers it
    if (context > bestContext || (context == bestContext && work < bestWork))
        best = (columns, rows);
}
return best ?? grid at MinimumTileSize;                    // quality floor when nothing fits
```

Notes:

* **One tile runs at a time**, so a grid is affordable exactly when a single tile of it is. The whole
  page as one inference is only planned when its footprint stays below
  `SinglePassSafetyFraction = 0.9` of the budget, because a single-pass overflow cannot be recovered
  without restarting the page. The session reservation `C` is subtracted from the budget first, so
  neither decision can plan past the memory a live session already holds.
* **The objective is context first, then throughput.** The models need roughly
  `QualityTilePixels = 512` px on both sides to see their full receptive field; below that a split
  loses context on screentones, and above it a larger tile has nothing left to look at. The planner
  therefore gives the smaller side all the context the budget allows, capped at 512, and among the
  grids that tie on that context takes the one with the fewest total padded pixels. That is what
  makes the search pick 2x2 (563x800 tiles) over 4x3 (282x534 tiles) on a 1125x1600 4x page at
  ~13 GB: only the 2x2 grid reaches the 512 px context target.
* **No upper cap.** On this card (16 GB, ~2 GB used by the desktop, so a ~13.4 GB Vulkan budget) a 4x
  model plans 563x800 tiles and a 2x model runs the whole 1125x1600 page in a single pass. The old
  `maxCap = { ≥4 => 512, 3 => 640, _ => 1024 }` is gone.
* **`MinimumTileSize = 256`** remains the quality floor (receptive field on screentones), and the grid
  never gets finer than it. When even that grid does not fit the budget, the engine's existing
  `OnnxTiler.IsMemoryException` path halves it further — down to **`EmergencyTileFloor = 128`**, and no
  further. Below that a tile is not a cheaper version of the same inference but a different, much
  worse one, so reaching the floor fails the page (as a memory failure, which the worker requeues)
  rather than upscaling it in postage stamps. The floor is judged on the tile the halving would
  actually produce, not on the size asked of it: `TileSplit.For` shrinks tiles to cover the page
  exactly, so halving a 188 px grid asks for 94 px and stops one step earlier.
* Tiles are inferred at **one uniform shape per page** (a multiple of 64), which keeps the driver from
  re-allocating per tile. It is derived from the grid the page actually has, *not* from the budget's
  tile side: an 800 px wide page inferred as a 1600 px tile computes every tile at twice the pixels
  the page contains, for identical output.
* **The enumeration bound is a floor, not a ceiling.** The pseudo-code uses
  `floor(w / MinimumTileSize)`, matching the implementation: `ceil(w / MinimumTileSize)` columns still
  allowed the last column to shrink a tile below the floor (1125 px → 5 columns → 225 px), which is
  exactly the quality `MinimumTileSize` exists to protect. Floor division bounds the column count so
  `ceil(w / columns) >= MinimumTileSize`, while `columns = 1` stays reachable for the near-single-pass
  case.

### 2.1 What the padding actually costs

`UniformTileTensorShape` is what a page is *really* inferred at, per tile:

| page | bound | grid | tensor per tile | vs. the page |
|---|---|---|---|---|
| 1125x1600 | 1125 | 1x1 | 1216x1664 = 2.02 MP | +12% |
| 1125x1600 | 800 | 2x2 | 640x832 = 0.53 MP x4 | +18% |
| 1125x1600 | 704 | 2x3 | 640x576 = 0.37 MP x6 | +23% |
| 800x5685 | 1536 | 1x4 | 832x1472 = 1.22 MP x4 | +8% |
| 8000x200 | 286 | 28x1 | 320x256 = 0.08 MP x28 | +40% |

The first two rows are the single-pass and 2x2 case of a normal page; the third is what a square-derived
704 px tile side used to buy. The 8000x200 row is a stitched webtoon strip, where the 16 px overlap on
both axes is a large fraction of a 200 px tall tile — unavoidable, and irrelevant because such strips
are not the common case.

---

## 3. Coefficients (`OnnxTiler.GetOutputLiveChannels`)

Two coefficients per architecture, because the fp16 and fp32 exports of one architecture are not
related by the element size alone: the fp16 graphs cast to fp32 around the upsampler, so their peak
is already partly fp32-sized, while an fp32 graph keeps fp32 tensors throughout.

| `ModelArchitecture` | `C_arch` fp16 | `C_arch` fp32 | representative models measured |
|---|---|---|---|
| `Span` | **315** | **158** | `2x_IllustrationJaNai_V3{detail,denoise}_SPAN_S_*` |
| `Esrgan` | **598** | **463** | `2x/4x_MangaJaNai_*_V1_ESRGAN_*`, `*_IllustrationJaNai_V1_ESRGAN_*` |
| `FdatM` | **462** | **590** | `*_V2standard_FDAT_M_*`, `*_V3{detail,denoise}_FDAT_M_*` |
| `FdatXl` | **660** | **882** | `*_FDAT_XL_*` |
| `Dat2` | **1060** | **1167** | `*_DAT2_*` |
| `HatL` | **1900** | **2055** | `4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16` / `_fp32` |
| unknown | `310 + 620·sqrt(MB/100)` | same | fallback: parameter-count scaling |

Each is the value at which the predicted footprint matches the measured VRAM peak of a real
single-tile inference, fitted through the origin over 3-6 tile sizes per model.

The **fp32 column is the one that runs on WebGPU**: the fp16/bf16 transformer exports need bf16 and
return NaN there, so the engine falls back to their fp32 export (candidate selection orders the fp32
copy last and the per-tile NaN guard skips the fp16 one). The fp16 column for FDAT_XL, DAT2 and HAT_L
is the original measurement — those exports cannot be re-measured on this EP, for the same reason
they are skipped. (The fp16 FDAT_M only produces valid output up to 128 px, which is exactly why the
device probe runs at 256 px.)

A single fp16-fitted column — the previous table, 310/660/460/660/1060/1900 — under-estimated every
transformer export that actually runs on WebGPU (FDAT_M by 28%, FDAT_XL by 34%) and over-estimated
the fp16 conv nets. That is what let a 13.1 GB budget plan a page that reached 16.2 GB of VRAM.

Measured bytes per input pixel, and the coefficient that follows from the fit:

| model | measured B/px | `C_arch` from the fit |
|---|---|---|
| `2x_..._SPAN_S_40k_fp16` | 2 524 | 315 |
| `2x_..._SPAN_S_40k_fp32` | 2 536 | 158 |
| `4x_MangaJaNai_1600p_V1_ESRGAN_70k` (fp16) | 19 140 | 598 |
| `4x_MangaJaNai_1600p_V1_ESRGAN_70k_fp32` | 29 604 | 463 |
| `4x_..._FDAT_M_40k_fp16` (64 and 128 px only) | 14 788 | 462 |
| `4x_..._FDAT_M_40k_fp32` | 37 751 | 590 |
| `4x_..._FDAT_XL_27k_fp32` | 56 452 | 882 |
| `4x_..._DAT2_28k_fp32` | 74 709 | 1 167 |
| `4x_..._HAT_L_28k_fp32` | 131 524 | 2 055 |

Predictions sit within a few percent of measurement at every size. The end-to-end table in §4.1 is
the stronger check for the fp16 conv nets; the fp32 transformers are checked in §6.2.

The unknown-architecture fallback is deliberately elastic: it scales with `sqrt(modelSize)` so a
model nobody has classified still lands in the right ballpark, and the engine halves the tile on
allocation failure if it lands low — stopping at `EmergencyTileFloor` (128 px), where the page is
failed as a memory failure instead of being split further.

---

## 4. Per-model graph analysis

The table below is the full inventory of models the app downloads
(`MangaJaNaiUpscaler.Fp16ModelPackages` / `Fp32ModelPackages` / `PageBreakDetectorPackage`), with
the weight size and the activation footprint derived from the graphs.

Methodology for each model:

1. Load the ONNX file, replace the graph input with concrete dims `[1, 3, T, T]`.
2. Run ONNX shape inference, resolve every value's element count (mask/index subgraphs included),
   then sweep node order with a liveness set: a value is live from its producing node to its last
   consumer. The peak of that sweep is the analytic working set.
3. Do the same for `T ∈ {64,128,192,256,384,512}` and check that `peak(T)` tracks `T²`.
4. Independently, run one real single-tile inference on the GPU, sampling
   `/sys/class/drm/card1/device/mem_info_vram_used` and `mem_info_gtt_used` at ~1 ms, and take
   `peakVRAM − sessionVRAM` as the measured footprint.

Only the peak values that matter are shown; the *architecture* is what drives the formula, and
all 32 shipped models fall into 6 architectures plus the page-break detector (which runs on the
CPU/short-lived session and is not tiled).

| model | arch | weights | `4x` Δ VRAM @256 | `2x` Δ VRAM @256 | live set at peak |
|---|---|---|---|---|---|
| `4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16` | HAT | 163.9 MB | 3.80 GB | – | attention maps + residual stream |
| `4x_IllustrationJaNai_V1_DAT2_190k` | DAT2 | 31.9 MB | 2.11 GB | – | QKᵀ map (201 MB @256) + stream |
| `4x_IllustrationJaNai_V3detail_FDAT_XL_27k_bf16` | FDAT_XL | 49.1 MB | 1.28 GB | – | `upsampler.1` fp32 cast (94 MB @256) |
| `4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16` | FDAT_M | 9.1 MB | 0.89 GB | – | `upsampler.1` fp32 cast (63 MB @256) |
| `4x_MangaJaNai_1600p_V1_ESRGAN_70k` | ESRGAN | 33.9 MB | 1.19 GB | – | `model.5/Resize` fp16+fp32 (134 MB each) |
| `4x_MangaJaNai_1200p…2048p_V1_ESRGAN_*` | ESRGAN | 33.9 MB | same | – | identical graphs, differing only in weights |
| `2x_IllustrationJaNai_V3detail_FDAT_M_unshuffle_40k_fp16` | FDAT_M | 9.2 MB | – | 0.24 GB | `upsampler.1` fp32 cast |
| `2x_MangaJaNai_*_V1_ESRGAN_*` | ESRGAN | 33.9 MB | – | 0.34 GB | `model.5/Resize` fp16+fp32 |
| `2x_IllustrationJaNai_V3{detail,denoise}_SPAN_S_*` | SPAN | 0.8 MB | – | 0.16 GB | 96-ch concat at 1× (6.3 MB @256) |
| `page_break_detector` | – | 2.3 MB | – | – | page-split detector, small, never tiled |

The per-model codebook (`W`, `C_arch`, `scale`) is what `OnnxUpscaleEngine` feeds into
`OnnxTiler.PlanTileSplit`. Because all models of an architecture share a graph, the per-model
differences reduce to the weight size, which the formula reads off the file.

### 4.1 Measurement of the real GPU peak vs prediction

`UpscaleProfiler` (`~/mangamem/profiler`) reproduces the engine's exact path —
`OnnxTiler.UpscaleRgb` with the planned grid — and samples VRAM/GTT around it. 1125×1600 page
(typical manga page after smart downscale), 12 GB budget, fp16:

```
model                                           tile  peakVRAM  peakGTT  tiles       ms  verdict
2x_IllustrationJaNai_V3detail_SPAN_S_40k_fp16      0    6905MB    311MB      1      467  single-pass
2x_MangaJaNai_1600p_V1_ESRGAN_90k                  0   12441MB    311MB      1     1986  single-pass
2x_IllustrationJaNai_V3detail_FDAT_M_unshuffle     0    9160MB    299MB      1     2886  single-pass
4x_MangaJaNai_1600p_V1_ESRGAN_70k                704   12855MB    310MB      6    11707  OK
4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp16    832   12971MB    331MB      4    17054  OK
4x_IllustrationJaNai_V3detail_FDAT_XL_27k_bf16   704   14064MB    310MB      6    85644  OK
4x_IllustrationJaNai_V1_DAT2_190k                512   12816MB    296MB     12    52830  OK
4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16     384   13839MB    279MB     15    69925  OK
```

(`peakVRAM` is absolute and includes the ~1.5 GB the desktop already had resident; `peakGTT` is
what would reveal spill into system memory.)

Three things this shows:

1. **No GTT spill.** GTT stays at its ~300 MB baseline for every model. Before the derivation, a
   512 px 4x tile already pushed 6.5 GB into GTT on this card.
2. **2x models now run the whole page in one pass** at a 12 GB budget — previously they were
   tiled at 1024 px regardless of how much memory was free.
3. **4x models get tiles well past the old 512 px clamp** — 704–832 px for the conv nets, which
   is 1.9–2.6× the tile area and a corresponding drop in tile count (6 instead of 9).

FDAT_XL overshoots the 12 GB budget by ~4 % (12.56 GB above baseline, no spill). The estimate is
otherwise within a few percent; the small residual is the arena high-water mark, which the
formula does not try to model exactly.

---

### 4.2 Sharing the card with a bounded partner

`CalculateVramBudget` decides between a quasi-exclusive and a desktop budget by looking at how much
VRAM is already used. Below the threshold it keeps only a 256 MB reserve; above it, the driver
eviction margin of 15% of total VRAM (1.5–3 GiB) kicks in, because drivers start evicting into GTT
past 85–90% utilisation.

The driver's own `VK_EXT_memory_budget` figure is *not* that number: RADV reports total-minus-used
with no eviction headroom of its own (16 304 MB card, 3 190 MB used → 13 113 MB "budget", against a
calculated 10 668 MB). Using it verbatim therefore spends the card to the point where the driver
starts evicting: measured on this card a page reached 16.2 GB VRAM and 275 MB of GTT against the
13.1 GB driver budget, and the fp32 FDAT_M plan that fitted in 3x3 tiles did not in 2x3. The driver
figure is used verbatim only while the card counts as quasi-exclusive (nothing else using it, or a
bounded partner below `VramExclusiveThresholdBytes`); otherwise it is capped by the calculated
budget. At the calculated 10.7 GB both models planned a grid that stayed out of GTT.

**Known limitation: the device index is not cross-checked between Vulkan and the execution provider.**
`VulkanMemoryProvider` resolves `SelectedDeviceIndex` by counting the non-CPU devices *Vulkan*
enumerates, while WebGPU, DirectML and CUDA each enumerate in their own order. On a single-GPU host
the two agree by construction; on a mixed-vendor host (or one with an integrated and a discrete GPU
from different vendors) the VRAM budget, the FP16 capability answer and the calibration fingerprint
can therefore be read for a different device than the one inferring. The index is not matched by
LUID/UUID — pin the device with `PreferredGpuBackend` and confirm the logged device name when this
matters.

The default threshold is 350 MB — driver plus a display server. That is pessimistic for a card that
*is* shared, but with a partner whose footprint is known and bounded (a video transcoder holding
700 MB, a second inference server). `VramExclusiveThresholdBytes` moves the boundary:

| setting | 12 GB card, 700 MB used | budget |
|---|---|---|
| default (350 MB) | desktop branch | 9.5 GB |
| `VramExclusiveThresholdBytes = 1073741824` (1 GiB) | exclusive branch | 11.1 GB |

That is ~1.6 GB of VRAM handed back to tile sizing. The comparison is inclusive — usage *equal* to
the threshold still counts as exclusive — and an explicit `VramSafetyMarginBytes` or
`VramUtilizationFraction` still takes precedence over both branches. A value of 0 or negative falls
back to the default rather than making every card exclusive.

### 4.3 Human-readable size strings

Every memory setting accepts either a plain byte count or a size string, so a configuration file no
longer has to spell out `367001600`:

| Setting | Examples |
|---|---|
| `MemoryBudgetBytes` | `4GiB`, `12288MiB`, `4294967296`, unset |
| `VramSafetyMarginBytes` | `512MiB`, `1.5 GiB`, unset |
| `VramExclusiveThresholdBytes` | `700MiB`, `1 GiB`, `350MiB`, unset |
| `MaxSpoolBytesPerTask` | `8 GiB`, `2GiB`, `8589934592` |

Decimal suffixes are powers of 1000, binary ones powers of 1024 — `1GB` is 1000000000 and `1GiB` is
1073741824. The distinction exists precisely because the arithmetic in this document is binary, so
pasting the wrong unit cannot silently mean a different number. Bare byte counts keep working, so
existing configuration files do not have to change.

```yaml
environment:
  Ingest_Upscaler__MemoryBudgetBytes: "12 GiB"          # fixed budget, no auto-detection
  Ingest_Upscaler__VramExclusiveThresholdBytes: "1GiB"  # a transcoder owns the first GiB
  Ingest_Upscaler__VramSafetyMarginBytes: "512MiB"      # instead of the automatic margin
```

`ByteSize.ToString()` renders values back with binary units, which is what the logs use.

A value that is *present but unparseable* (`4GiBX`, `12 Gigabytes`) aborts startup with
`OptionsValidationException` naming the setting and the value, because silently falling back to
"unset" would drop an explicit override — the very thing the setting exists to express:

```
Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException:
Upscaler:MemoryBudgetBytes = "12 Gigabytes": expected a byte count or a size such as 4GiB, 700MiB or 1024
```

Unset values remain valid (they mean "detect automatically") and a fully unconfigured `Upscaler`
section still starts, which is what the unmodified container deployment does.

## 5. Why this is better than the old caps

Old behaviour, 4x ESRGAN fp16, 1125×1600 page, real weight size:

* the estimate said a 512 px tile needed 2.9 GB, then the cap forced 512 px anyway — so a 16 GB
  budget produced 12 tiles and the card sat at roughly a fifth of its usable VRAM.

New behaviour (same page, same models, planned grids — RX 9070 XT, WebGPU). 4x ESRGAN fp16,
1125×1600, so the enumeration bounds are `maxColumns = floor(1125/256) = 4` and
`maxRows = floor(1600/256) = 6`:

| budget | old tiles | planned grid | tensor per tile |
|---|---|---|---|
| 8 GB | 12 x 512 | 2x3 | 640x576 |
| 12 GB | 12 x 512 | 2x2 | 640x832 |
| 13.4 GB | 12 x 512 | 1x3 | 1216x576 |
| 16 GB | 12 x 512 | 1x3 | 1216x576 |

From 12 GB the page is four tiles where the old cap used twelve, and at 13.4 GB and above it is one
column of three. (An earlier revision of this table listed 5x7 and 4x3 rows. Five columns was never
reachable — the column count is bounded by `floor(width / MinimumTileSize)`, which is 4 here — so
those numbers predated that bound, and the rest predate the per-precision coefficients of §3.)

and the 2x models run the whole page in a single pass from a 12 GB budget upwards — the old cap held
them at 1024 px no matter how much memory was free.

The planner optimises padded work, which is throughput only where per-pixel efficiency is flat in the
tile size. It is not for every model, which is worth writing down rather than hiding: `FDAT_XL` is
measurably slower per pixel on larger tiles (18.6 vs 20.5 ns/px on 640x832 vs 640x576, repeated), so
at 13.4 GB its 2x2 grid is a few percent slower than the 2x3 the old cap produced, even though it does
less padded work. ESRGAN, DAT2 and HAT_L all get *faster* per pixel as the tile grows, so for them the
least-work grid is also the fastest. Per-pixel efficiency is a model property the planner does not
model, and the difference between the two behaviours is a few percent.

---

## 6. Per-model calibration (per device)

The coefficients above are generic. The two numbers that are *not* derivable from a model graph —
how many times the analytic working set this particular accelerator actually needs, and how much
memory a live session holds — are measured and cached **per model and per device**.

### 6.1 What gets measured, and why per model

Two inferences of the model itself give two readings:

```
peak(n) = reservation + scale · analytic · n²
```

| measurement | gives |
|---|---|
| inference at n₁ and n₂ | `scale` — the per-pixel slope, divided by the analytic one |
| the two points together | `reservation` — the fixed part, with the weights taken out |

`scale` is a property of the model *and* the execution provider together, not of the device, so one
device-wide factor cannot represent it. Measured on an RX 9070 XT / WebGPU EP:

| model | analytic B/px | measured B/px | ratio |
|---|---|---|---|
| `4x_MangaJaNai_1600p_V1_ESRGAN_70k` (fp16) | 21 120 | 19 140 | **0.90** |
| `4x_MangaJaNai_1600p_V1_ESRGAN_70k_fp32` | 42 240 | 29 320 | **0.69** |
| `4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32` | 29 440 | 37 700 | **1.28** |

The spread between the extremes is a factor of 1.9. The previous design fitted a single device
factor (1.02, from the fp16 ESRGAN) and applied it everywhere, which under-estimated the fp32
transformer by ~25% — enough to fill a 16 GB card and push the page into GTT — while
over-estimating the conv nets.

**Each benchmark tile runs on its own session.** The WebGPU EP caches its buffers per tensor shape
and exposes no way to release them, so measuring two sizes in one session makes the larger reading
include the smaller one's cached buffers. On this card that inflated the fitted slope from
19 140 to 21 465 B/px (+12%), and the tile plan with it.

Each benchmark is two inferences plus two session builds (~1 s). It runs the first time a model is
used on a device and only when the device, execution provider, ONNX Runtime build or calibration
version changes.

### 6.2 The result on this machine

```
device : AMD Radeon RX 9070 XT (RADV GFX1201) (WebGpu)
model  : 4x_MangaJaNai_1600p_V1_ESRGAN_70k           scale 0.90x
model  : 4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32 scale 1.28x
```

A per-model measurement of ~0.9x on the fp16 conv net is the expected outcome here: the table in §3
*was* measured on this card, so the benchmark confirms it. The value of the benchmark is on any
*other* device, where the same code would report e.g. 0.3x on a CUDA card or 2x on an integrated GPU,
and tile sizing would follow.

Verified end-to-end through `OnnxUpscaleEngine` (real manga page, 13 GB budget, fp32 FDAT_M): the
model's own factor planned a 3x3 grid of 448x576-padded tiles and the page stayed out of GTT, where
the device-wide factor planned 2x3 of 640x576 and reached 16.2 GB VRAM plus 275 MB of GTT.

### 6.3 Where it is stored

`<models directory>/device-memory-profile.json`, one file holding a **map of device fingerprint to
profile**, and inside each profile a map of model key to measurement:

```json
{
  "webgpu|0|AMD Radeon RX 9070 XT (RADV GFX1201)|17095983104|1.29.0|v3": {
    "fingerprint": "webgpu|0|AMD Radeon RX 9070 XT (RADV GFX1201)|17095983104|1.29.0|v3",
    "provider": 2,
    "deviceName": "AMD Radeon RX 9070 XT (RADV GFX1201)",
    "totalDeviceMemoryBytes": 17095983104,
    "activationScale": 0.902,
    "sessionReservationBytes": 126316544,
    "models": {
      "4x_IllustrationJaNai_V3detail_FDAT_M_40k_fp32.onnx|16761523": {
        "activationScale": 1.28,
        "sessionReservationBytes": 9437184,
        "measuredTiles": [320, 640],
        "measuredPeakBytes": [2100000000, 13100000000],
        "calibratedAtUtc": "2026-10-12T01:20:00Z"
      }
    }
  }
}
```

A model key is `file name|size`: the fp16 and fp32 exports of one model differ only in size and
precision, and a re-exported file can keep its name while its graph changes, so the size is what
stops a stale measurement from being applied to a new file.

Because every profile is keyed by fingerprint, one file can carry several devices: switching the
GPU, swapping the execution provider, or pointing the models directory at a shared volume used by
two hosts, keeps every calibration. A fingerprint is

```
execution provider | device index | device name | total device memory | ONNX Runtime version | calibration version
webgpu|0|AMD Radeon RX 9070 XT (RADV GFX1201)|17095983104|1.29.0|v3
cpu|0|AMD Radeon RX 9070 XT (RADV GFX1201)|17095983104|1.29.0|v3
```

The provider is **not** probed independently: it comes from `IOnnxSessionFactory.GetEffectiveBackend()`,
the same method that configures the session, so it honours `PreferredGpuBackend`, `UseCPU` and
`SelectedDeviceIndex`. A calibration measured on WebGPU is therefore never applied to a CUDA
session (and vice versa) just because both happen to run on the same card.

### 6.4 Behaviour and fallbacks

* First use of a model on an unseen device + provider → that model's benchmark runs (logged at
  `Information`), then the cached measurement is used for the rest of the process lifetime.
* A model with no measurement of its own falls back to the device-wide default, and then to the
  built-in coefficients.
* `RecalibrateDeviceMemory = true` forces a re-measure on the next upscale.
* `UseCPU = true` skips calibration entirely (there is no device budget to fill).
* A scale outside `0.2…12` is discarded and the built-in coefficients are used — that is what a
  distorted measurement (another process on the device, driver eviction) looks like.
* If the models directory is read-only or the benchmark throws, nothing breaks: the built-in
  coefficients apply, which are the WebGPU/AMD values and therefore conservative.
* A model whose own benchmark cannot run is not retried for every page: the failure is remembered
  per model for the process.

### 6.5 The per-shape buffer cache (why a chapter is not one long session)

The WebGPU EP caches its buffers per tensor shape and has no API to release them. A page whose
padded tile shape differs from the previous page's therefore adds a *second* set to the cache
instead of reusing the first. Measured on an RX 9070 XT with the fp16 ESRGAN at a 13 GB budget:

| session | page | shape | peak VRAM | GTT spill | page time |
|---|---|---|---|---|---|
| fresh | 1 | 640x832 | 12.2 GB | 82 MB | 7.0 s |
| continued | 2 | 640x576 | 15.8 GB | 3.9 GB | 6.8 s |
| continued | 3 | 448x576 | 16.1 GB | 9.6 GB | 24.8 s |
| continued | 4+ | 640x832 | 16.1 GB | 9.6 GB (plateau) | 7.0 s |

Five *same-shape* pages in one session stay at 82-89 MB and 7 s each, so the growth is shape churn,
not "any second page". The engine therefore releases the cached session when the padded shape
changes; a rebuild costs ~290 ms, which is far cheaper than the 9.6 GB and 25 s a churned session
pays. The absolute `gttAfter > 1 GB` trigger it replaces fired on essentially every page (the GTT
baseline alone is ~1 GB on a desktop), which turned the cache's own reuse into a rebuild per page.


## 7. Reproducing the numbers

The scripts live outside the repository (`~/mangamem/work`) because they need the model files and
are measurement tools, not part of the app:

| script | purpose |
|---|---|
| `shape_peaks.py` | analytic working set per model via shape inference + liveness |
| `single.sh` + `profiler/Program.cs` | GPU measurement of one inference at a fixed tile size |
| `join.py` | joins both into the `C_arch` table |
| `profiler/validate/` | end-to-end check: plan a grid, run a page, verify VRAM/GTT |

To force a re-measurement in the app, set `Ingest_Upscaler__RecalibrateDeviceMemory=true` for one
upscale and delete `<models>/device-memory-profile.json` (or just leave it — the fingerprint
decides). Use `ulimit -v` around the Python step — making every intermediate a graph output is
convenient for shape extraction but allocates tens of gigabytes on the big transformer models.

To re-derive after adding a model: run `shape_peaks.py <model.onnx> 64,128`, then
`profiler single <model.onnx> <scale> <n>` for a few `n`, then `join.py` and update
`GetOutputLiveChannels`.
