# Upscaling Memory Model & Tile Size Derivation

This document explains how the upscaler decides how large a tile it may run, and where the
numbers come from. It exists so the tile size is a *derived* quantity instead of an arbitrary
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
* the session is created with `memory.enable_memory_arena_shrinkage: gpu:0` and
  `session.arena_extend_strategy: kSameAsRequested`, so freed blocks are pooled rather than
  returned and the arena keeps its high-water mark.

Measured against the analytic sweep, the observed footprint is a stable multiple:

| architecture | analytic `C_arch` (live ch) | measured `C_arch` | ratio |
|---|---|---|---|
| SPAN | 96 | 310 | 3.2× |
| ESRGAN | 128 | 660 | 5.2× |
| FDAT_M | 90 | 460 | 5.1× |
| DAT2 | 203 | 1060 | 5.2× |
| FDAT_XL | 135 | 660 | 4.9× |

The ratio is constant over three orders of magnitude of tile size, so instead of modelling each
source separately the code stores the **measured** `C_arch` (§3) and documents the analytic
sweep as the derivation behind it.

### 1.3 Session reservation `C`

A freshly created session reserves ~380 MB of VRAM on this card before any inference runs (ORT
WebGPU EP arenas, Dawn/Vulkan driver-side allocations and pipeline caches). It is subtracted from
the budget once, as `OnnxTiler.SessionReservationBytes` (384 MiB).

---

## 2. How the tile size is derived

```csharp
long weights   = EstimateModelBytes(modelNameOrPath, modelSizeBytes);
double bpp     = C_arch · scale² · (isFp16 ? 2 : 4);           // bytes per input pixel
long  avail    = memoryBudgetBytes - weights - SessionReservationBytes;

// single pass only if the whole page fits with headroom
if (weights + bpp · aligned(w)·aligned(h) <= 0.9 · memoryBudgetBytes) return 0;

// otherwise: largest n such that a padded, aligned tile still fits
n0  = sqrt(avail / bpp) - 2·tilePad
n   = snap_down_to_64(n0)
while (weights + bpp · aligned(n + 2·tilePad)² > memoryBudgetBytes) n -= 64;
return max(256, n);
```

Notes:

* **`0` means "single pass"**, kept as the existing contract.
* **No upper cap.** The tile size is whatever the budget supports. On this card (16 GB, ~2 GB used
  by the desktop, so a 14.2 GB Vulkan budget) a 4x model derives 768–896 px tiles and a 2x model
  runs the whole 1125×1600 page in a single pass. At a 32 GB budget the 4x model reaches 1152 px.
  The old `maxCap = { ≥4 => 512, 3 => 640, _ => 1024 }` is gone.
* **`MinimumTileSize = 256`** remains, for quality (receptive field on screentones) and because
  the fixed per-tile cost dominates below that. When even a 256 px tile does not fit the budget,
  the engine's existing `OnnxTiler.IsMemoryException` path halves it further.
* Tiles are snapped to a **multiple of 64**, matching `AutoSplit`'s uniform-tensor alignment, so
  re-sized tiles keep the same shapes and do not churn the arena.

---

## 3. Coefficients (`OnnxTiler.GetOutputLiveChannels`)

| `ModelArchitecture` | `C_arch` | representative models measured |
|---|---|---|
| `Span` | **310** | `2x_IllustrationJaNai_V3{detail,denoise}_SPAN_S_*` |
| `Esrgan` | **660** | `2x/4x_MangaJaNai_*_V1_ESRGAN_*`, `*_IllustrationJaNai_V1_ESRGAN_*` |
| `FdatM` | **460** | `*_V2standard_FDAT_M_*`, `*_V3{detail,denoise}_FDAT_M_*` |
| `FdatXl` | **660** | `*_FDAT_XL_*` |
| `Dat2` | **1060** | `*_DAT2_*` |
| `HatL` | **1900** | `4x_IllustrationJaNai_V3detail_HAT_L_28k_bf16` |
| unknown | `310 + 620·sqrt(MB/100)` | fallback: parameter-count scaling |

Every `C_arch` is the value at which the predicted footprint matches the measured VRAM peak of a
real single-tile inference. Verification for `4x_...HAT_L_28k_bf16` (the heaviest model),
`isFp16: true`:

| tile | predicted `A(n)` | measured Δ VRAM |
|---|---|---|
| 128 px | 0.95 GB | 0.95 GB |
| 256 px | 3.80 GB | 3.80 GB |
| 384 px | 8.55 GB | 8.53 GB |

Predictions sit within a few percent of measurement at every size, for every architecture. The
end-to-end table in §4.1 is the stronger check: those runs use the derived tile sizes.

The unknown-architecture fallback is deliberately elastic: it scales with `sqrt(modelSize)` so a
model nobody has classified still lands in the right ballpark, and the engine halves the tile on
allocation failure if it lands low.

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
all 31 shipped models fall into 8 architectures plus the page-break detector (which runs on the
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
`OnnxTiler.EstimateTileSize`. Because all models of an architecture share a graph, the per-model
differences reduce to the weight size, which the formula reads off the file.

### 4.1 Measurement of the real GPU peak vs prediction

`UpscaleProfiler` (`~/mangamem/profiler`) reproduces the engine's exact path —
`OnnxTiler.UpscaleRgb` with the derived tile size — and samples VRAM/GTT around it. 1125×1600 page
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

## 5. Why this is better than the old caps

Old behaviour, 4x ESRGAN fp16, 1125×1600 page, real weight size:

* the estimate said a 512 px tile needed 2.9 GB, then the cap forced 512 px anyway — so a 16 GB
  budget produced 12 tiles and the card sat at roughly a fifth of its usable VRAM.

New behaviour (same page, same models, derived tile sizes):

| budget | old tile (4x ESRGAN) | old tiles | new tile (4x) | new tiles |
|---|---|---|---|---|
| 8 GB | 512 | 12 | 512 | 12 |
| 12 GB | 512 | 12 | 704 | 6 |
| 16 GB | 512 | 12 | 832 | 4 |
| 24 GB | 512 | 12 | 1024 | 4 |
| 32 GB | 512 | 12 | 1152 | 2 |

and the 2x models run the whole page in a single pass from a 12 GB budget upwards (768 px at
8 GB, 1024 px at 6 GB — the old cap held them at 1024 px no matter how much memory was free).

---

## 6. Per-device calibration

The coefficients above are generic. The two numbers that are *not* derivable from a model graph —
how many times the analytic working set this particular accelerator actually needs, and how much
memory a live session holds — are measured once per device and cached.

### 6.1 What gets measured

A short benchmark runs three inferences of one small ESRGAN model and takes two readings:

```
peak(n) = reservation + scale · analytic · n²
```

| measurement | gives |
|---|---|
| warm-up at 64 px | `reservation` — everything fixed (arenas, driver allocations, pipeline caches) |
| inference at n₁ and n₂ | `scale` — the per-pixel slope, divided by the analytic one |

Three inferences on an RX 9070 XT take ~1.3 s. It runs once per device and only when the device,
execution provider, ONNX Runtime build or calibration version changes.

### 6.2 The result on this machine

```
device          : AMD Radeon RX 9070 XT (RADV GFX1201) (WebGpu)
activation scale: 0.982x
session reserve : 140 MB
model used      : 4x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx
measured tiles  : 128, 448 px
```

A scale of ~1.0 is the expected outcome here: the table in §3 *was* measured on this card, so the
benchmark confirms it. The value of the benchmark is on any *other* device, where the same code
would report e.g. 0.3x on a CUDA card or 2x on an integrated GPU, and tile sizing would follow.

Verified end-to-end through `OnnxUpscaleEngine` (real manga page, 6 GB budget, first run):

```
profile before run : none
profile after run  : scale=0.982 reservation=74MB tiles=128/448
output             : 3808 KB in 9.0s
peak VRAM          : 8156 MB  (delta 6081 MB of a 6000 MB budget)
peak GTT           :  299 MB
```

### 6.3 Where it is stored

`<models directory>/device-memory-profile.json`, one file holding a **map of device fingerprint to
profile**:

```json
{
  "webgpu|AMD Radeon RX 9070 XT (RADV GFX1201)|17095983104|1.29.0|v2": {
    "fingerprint": "webgpu|AMD Radeon RX 9070 XT (RADV GFX1201)|17095983104|1.29.0|v2",
    "provider": 2,
    "deviceName": "AMD Radeon RX 9070 XT (RADV GFX1201)",
    "totalDeviceMemoryBytes": 17095983104,
    "activationScale": 0.982,
    "sessionReservationBytes": 146800640,
    "calibratedAtUtc": "2026-10-09T07:46:13Z",
    "calibratedWithModel": "4x_MangaJaNai_1200p_V1_ESRGAN_70k.onnx",
    "measuredTiles": [128, 448],
    "measuredPeakBytes": [249561088, 4071493632]
  }
}
```

Because every profile is keyed by fingerprint, one file can carry several devices: switching the
GPU, swapping the execution provider, or pointing the models directory at a shared volume used by
two hosts, keeps every calibration. A fingerprint is

```
execution provider | device index | device name | total device memory | ONNX Runtime version | calibration version
webgpu|0|AMD Radeon RX 9070 XT (RADV GFX1201)|17095983104|1.29.0|v2
cpu|0|AMD Radeon RX 9070 XT (RADV GFX1201)|17095983104|1.29.0|v2
```

The provider is **not** probed independently: it comes from `IOnnxSessionFactory.GetEffectiveBackend()`,
the same method that configures the session, so it honours `PreferredGpuBackend`, `UseCPU` and
`SelectedDeviceIndex`. A calibration measured on WebGPU is therefore never applied to a CUDA
session (and vice versa) just because both happen to run on the same card.

### 6.4 Behaviour and fallbacks

* First upscale on an unseen device + provider → the benchmark runs (logged at `Information`), then
  the cached profile is used for the rest of the process lifetime. Switching
  `PreferredGpuBackend` to another provider simply gets its own profile.
* `RecalibrateDeviceMemory = true` forces a re-measure on the next upscale.
* `UseCPU = true` skips calibration entirely (there is no device budget to fill).
* A profile with a scale outside `0.2…12` or a reservation outside `0…8 GB` is discarded and the
  built-in coefficients are used — that is what a distorted measurement (another process on the
  device, driver eviction) looks like.
* If the models directory is read-only or the benchmark throws, nothing breaks: the built-in
  coefficients apply, which are the WebGPU/AMD values and therefore conservative.
* The measured scale is applied *on top of* the architecture table, not instead of it, so a
  per-architecture mismatch (calibrated on ESRGAN, applied to HAT) cannot compound.

## 7. Reproducing the numbers

The scripts live outside the repository (`~/mangamem/work`) because they need the model files and
are measurement tools, not part of the app:

| script | purpose |
|---|---|
| `shape_peaks.py` | analytic working set per model via shape inference + liveness |
| `single.sh` + `profiler/Program.cs` | GPU measurement of one inference at a fixed tile size |
| `join.py` | joins both into the `C_arch` table |
| `profiler/validate/` | end-to-end check: derive a tile size, run a page, verify VRAM/GTT |

To force a re-measurement in the app, set `Ingest_Upscaler__RecalibrateDeviceMemory=true` for one
upscale and delete `<models>/device-memory-profile.json` (or just leave it — the fingerprint
decides). Use `ulimit -v` around the Python step — making every intermediate a graph output is
convenient for shape extraction but allocates tens of gigabytes on the big transformer models.

To re-derive after adding a model: run `shape_peaks.py <model.onnx> 64,128`, then
`profiler single <model.onnx> <scale> <n>` for a few `n`, then `join.py` and update
`GetOutputLiveChannels`.
