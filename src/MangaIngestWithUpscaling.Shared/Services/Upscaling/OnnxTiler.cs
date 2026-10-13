using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MangaIngestWithUpscaling.Shared.Services.GPU;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Direction for tile overlap blending.
/// </summary>
public enum BlendDirection
{
    Horizontal,
    Vertical,
}

/// <summary>
/// Overlap padding dimensions in pixels.
/// </summary>
public readonly record struct TileOverlap(int Start, int End)
{
    public int Total => Start + End;
}

/// <summary>
/// Blends overlapping tiles using half-sine weighting to eliminate visible seams,
/// matching the original MangaJaNaiConverterGui TileBlender implementation.
/// </summary>
public sealed class TileBlender
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _channels;
    private readonly BlendDirection _direction;
    private readonly byte[] _result;
    private int _offset;
    private int _lastEndOverlap;

    public TileBlender(int width, int height, int channels, BlendDirection direction)
    {
        _width = width;
        _height = height;
        _channels = channels;
        _direction = direction;
        _result = new byte[
            OnnxTiler.CheckedCount((long)width * height * channels, "The blended output")
        ];
        _offset = 0;
        _lastEndOverlap = 0;
    }

    public int Width => _width;
    public int Height => _height;
    public int Offset => _offset;

    public void AddTile(byte[] tileBytes, int tileWidth, int tileHeight, TileOverlap overlap)
    {
        TileOverlap o = overlap;

        if (_direction == BlendDirection.Horizontal)
        {
            if (tileHeight != _height)
            {
                throw new ArgumentException(
                    $"Tile height {tileHeight} does not match blender height {_height}."
                );
            }

            int tileXStart = 0;
            int currentTileWidth = tileWidth;

            if (_offset == 0)
            {
                for (int y = 0; y < _height; y++)
                {
                    int srcOff = (y * tileWidth) * _channels;
                    int dstOff = (y * _width) * _channels;
                    Buffer.BlockCopy(
                        tileBytes,
                        srcOff,
                        _result,
                        dstOff,
                        currentTileWidth * _channels
                    );
                }

                _offset += currentTileWidth - o.End;
                _lastEndOverlap = o.End;
            }
            else
            {
                if (_lastEndOverlap < o.Start)
                {
                    int diff = o.Start - _lastEndOverlap;
                    tileXStart = diff;
                    currentTileWidth -= diff;
                    o = new TileOverlap(_lastEndOverlap, o.End);
                }

                RequireBlendRegion(currentTileWidth, o, "wide");

                int nonBlendWidth = currentTileWidth - o.Start * 2;
                if (nonBlendWidth > 0)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        int srcOff = (y * tileWidth + tileXStart + o.Start * 2) * _channels;
                        int dstOff = (y * _width + _offset + o.Start) * _channels;
                        Buffer.BlockCopy(
                            tileBytes,
                            srcOff,
                            _result,
                            dstOff,
                            nonBlendWidth * _channels
                        );
                    }
                }

                int blendSize = o.Start * 2;
                if (blendSize > 0)
                {
                    float[] blend = GetBlendWeights(blendSize);
                    Parallel.For(
                        0,
                        _height,
                        y =>
                        {
                            int dstRowStart = (y * _width + (_offset - o.Start)) * _channels;
                            int srcRowStart = (y * tileWidth + tileXStart) * _channels;

                            for (int x = 0; x < blendSize; x++)
                            {
                                float b = blend[x];
                                float invB = 1.0f - b;
                                int dstIdx = dstRowStart + x * _channels;
                                int srcIdx = srcRowStart + x * _channels;

                                for (int c = 0; c < _channels; c++)
                                {
                                    float val =
                                        _result[dstIdx + c] * invB + tileBytes[srcIdx + c] * b;
                                    _result[dstIdx + c] = (byte)
                                        Math.Clamp((int)(val + 0.5f), 0, 255);
                                }
                            }
                        }
                    );
                }

                _offset += currentTileWidth - o.Total;
                _lastEndOverlap = o.End;
            }
        }
        else // Vertical
        {
            if (tileWidth != _width)
            {
                throw new ArgumentException(
                    $"Tile width {tileWidth} does not match blender width {_width}."
                );
            }

            int tileYStart = 0;
            int currentTileHeight = tileHeight;

            if (_offset == 0)
            {
                Buffer.BlockCopy(tileBytes, 0, _result, 0, _width * currentTileHeight * _channels);
                _offset += currentTileHeight - o.End;
                _lastEndOverlap = o.End;
            }
            else
            {
                if (_lastEndOverlap < o.Start)
                {
                    int diff = o.Start - _lastEndOverlap;
                    tileYStart = diff;
                    currentTileHeight -= diff;
                    o = new TileOverlap(_lastEndOverlap, o.End);
                }

                RequireBlendRegion(currentTileHeight, o, "tall");

                int nonBlendHeight = currentTileHeight - o.Start * 2;
                if (nonBlendHeight > 0)
                {
                    int srcOff = (tileYStart + o.Start * 2) * _width * _channels;
                    int dstOff = (_offset + o.Start) * _width * _channels;
                    int byteCount = nonBlendHeight * _width * _channels;
                    Buffer.BlockCopy(tileBytes, srcOff, _result, dstOff, byteCount);
                }

                int blendSize = o.Start * 2;
                if (blendSize > 0)
                {
                    float[] blend = GetBlendWeights(blendSize);
                    int rowBytes = _width * _channels;

                    Parallel.For(
                        0,
                        blendSize,
                        y =>
                        {
                            float b = blend[y];
                            float invB = 1.0f - b;
                            int dstRowStart = (_offset - o.Start + y) * rowBytes;
                            int srcRowStart = (tileYStart + y) * rowBytes;

                            for (int i = 0; i < rowBytes; i++)
                            {
                                float val =
                                    _result[dstRowStart + i] * invB
                                    + tileBytes[srcRowStart + i] * b;
                                _result[dstRowStart + i] = (byte)
                                    Math.Clamp((int)(val + 0.5f), 0, 255);
                            }
                        }
                    );
                }

                _offset += currentTileHeight - o.Total;
                _lastEndOverlap = o.End;
            }
        }
    }

    public byte[] GetResult()
    {
        return _result;
    }

    /// <summary>
    /// Rejects a tile too narrow (horizontal) or too short (vertical) to carry the blend region.
    /// <para>
    /// The blend reads <c>o.Start * 2</c> columns (or rows) from the tile, and the copy that follows
    /// starts at that same offset, so a tile smaller than the region has no valid reading: the walk
    /// runs past the end of a tile row — blending each row with the pixel after it — and past the end
    /// of the array altogether on the last row. Nothing guaranteed the bound: a manual
    /// <c>TileSize</c> reaches the blender directly, and <see cref="TileSplit.For"/> distributes tiles
    /// evenly, so even a bound above the padding can shrink a page's derived tiles below it.
    /// </para>
    /// </summary>
    /// <param name="tileExtent">The tile's width for a horizontal blender, its height for a vertical one.</param>
    /// <param name="overlap">The overlap actually in force, which may have been narrowed above.</param>
    /// <param name="dimension">"wide" or "tall", for the message.</param>
    private static void RequireBlendRegion(int tileExtent, TileOverlap overlap, string dimension)
    {
        int required = overlap.Start * 2;
        if (tileExtent >= required)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The tile is {tileExtent} px {dimension} but its blend region is {required} px "
                + $"({nameof(TileOverlap.Start)} = {overlap.Start} px per side), so the blend would "
                + "read past the end of the tile. A tile narrower than the blend region cannot be "
                + "blended in this direction. Raise TileSize — it has to stay at least "
                + $"{4 * OnnxTiler.DefaultTilePad} px, twice the {2 * OnnxTiler.DefaultTilePad} px the "
                + $"blend needs (TileSplit.For distributes tiles evenly, so it can shrink a page's "
                + $"tiles below the bound) — or leave it at 0 and let the planner size the grid."
        );
    }

    /// <summary>
    /// Memoized blend weights. The weights are a pure function of the size, the size is the same for
    /// every interior tile of a page (the overlap is constant), and <see cref="AddTile"/> runs once
    /// per tile, so without this every tile of a page allocated the same array again. The cap bounds
    /// a pathological stream of distinct page geometries; clearing costs recomputation, never
    /// correctness. The returned arrays are shared between callers and must not be mutated.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        int,
        float[]
    > BlendWeightCache = new();

    public static float[] GetBlendWeights(int blendSize)
    {
        if (blendSize <= 0)
        {
            return Array.Empty<float>();
        }

        if (BlendWeightCache.Count >= 256)
        {
            BlendWeightCache.Clear();
        }

        return BlendWeightCache.GetOrAdd(
            blendSize,
            static size =>
            {
                if (size == 1)
                {
                    return [0.5f];
                }

                float[] blend = new float[size];
                float denom = size - 1;
                for (int i = 0; i < size; i++)
                {
                    float t = i / denom;
                    blend[i] = HalfSinBlend(t);
                }

                return blend;
            }
        );
    }

    public static float HalfSinBlend(float t)
    {
        float clipped = Math.Clamp(t * 2f - 0.5f, 0f, 1f);
        return (MathF.Sin(clipped * MathF.PI - MathF.PI / 2f) + 1f) / 2f;
    }
}

/// <summary>
/// Known neural network architectures used by MangaJaNai and IllustrationJaNai models.
/// </summary>
public enum ModelArchitecture
{
    Unknown,
    Esrgan,
    Span,
    FdatM,
    FdatXl,
    Dat2,
    HatL,
}

/// <summary>
/// The architecture preference vocabulary: the built-in order, the token parser the configuration
/// validator uses, and the merge that turns a configured list into an order.
/// </summary>
public static class ModelArchitecturePreference
{
    /// <summary>
    /// The built-in order, most preferred first: the fast, GPU-native architectures ahead of the
    /// heavy transformers. This is what <c>Upscaler:PreferredModelArchitectures</c> re-orders, and
    /// what an architecture the setting does not name keeps its place in.
    /// </summary>
    public static IReadOnlyList<ModelArchitecture> Default =>
        [
            ModelArchitecture.FdatM,
            ModelArchitecture.Esrgan,
            ModelArchitecture.Span,
            ModelArchitecture.FdatXl,
            ModelArchitecture.Dat2,
            ModelArchitecture.HatL,
        ];

    /// <summary>
    /// The architecture a configuration token names, or null when it names none. An exact match
    /// against the spellings used here (<c>FDAT_M</c>, <c>FDAT_XL</c>, …), deliberately not
    /// <see cref="OnnxTiler.DetectArchitecture"/>'s substring search: a typo has to fail the boot
    /// rather than silently re-rank the models.
    /// </summary>
    public static ModelArchitecture? Parse(string? token) =>
        token?.Trim().ToUpperInvariant() switch
        {
            "ESRGAN" => ModelArchitecture.Esrgan,
            "SPAN" => ModelArchitecture.Span,
            "FDAT_M" or "FDATM" => ModelArchitecture.FdatM,
            "FDAT_XL" or "FDATXL" => ModelArchitecture.FdatXl,
            "DAT2" => ModelArchitecture.Dat2,
            "HAT_L" or "HATL" => ModelArchitecture.HatL,
            _ => null,
        };

    /// <summary>
    /// The order the configured tokens ask for, with every architecture the setting does not name
    /// appended after them in its <see cref="Default"/> position. A partial list therefore re-ranks
    /// without dropping models, and an empty one is the default order unchanged.
    /// <para>
    /// Unknown tokens are skipped here rather than thrown: the configuration validator rejects them
    /// at startup, so this only meets one when a config was built in code (a test, or a call site
    /// that bypassed the options pipeline).
    /// </para>
    /// </summary>
    public static IReadOnlyList<ModelArchitecture> Resolve(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Default;
        }

        var order = new List<ModelArchitecture>();
        foreach (
            string token in configured.Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (Parse(token) is ModelArchitecture architecture && !order.Contains(architecture))
            {
                order.Add(architecture);
            }
        }

        foreach (ModelArchitecture architecture in Default)
        {
            if (!order.Contains(architecture))
            {
                order.Add(architecture);
            }
        }

        return order;
    }
}

/// <summary>
/// Raised when an inference produces non-finite values instead of an image.
/// <para>
/// Some execution providers do not implement every kernel the transformer architectures need.
/// The WebGPU EP is the known case: <c>FDAT_M</c>, <c>FDAT_XL</c>, <c>DAT2</c> and <c>HAT_L</c>
/// return an all-NaN output there rather than reporting a missing kernel, while the same model
/// runs correctly on the CPU and on CUDA/DirectML. The fp16 tables would map those values to
/// 0 and the page would be written as silent black, so this is thrown before any byte is
/// produced.
/// </para>
/// </summary>
public sealed class NonFiniteModelOutputException : Exception
{
    public NonFiniteModelOutputException()
        : base(
            "The model returned non-finite values (NaN or infinity). The execution provider cannot run this model."
        ) { }

    public NonFiniteModelOutputException(string message)
        : base(message) { }

    public NonFiniteModelOutputException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Raised when a model does not satisfy the contract the engine requires of it: an input named
/// <c>input</c>, output geometry matching the requested scale, and a supported input element type.
/// <para>
/// The dedicated type is what lets the engine condemn the <em>model</em> without also condemning its
/// own configuration. The failures that reach the same catch by their general types —
/// <see cref="ArgumentException"/> from a tiler invariant, <see cref="InvalidOperationException"/>
/// from the tile-too-large guard — say "this run was set up wrongly", and treating those as model
/// faults took a working model out of service for the rest of the process, with a misleading "no
/// model can upscale this page" error at the end.
/// </para>
/// </summary>
public sealed class ModelContractMismatchException : Exception
{
    public ModelContractMismatchException(string message)
        : base(message) { }

    public ModelContractMismatchException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Provides tiling, reflect padding, and output reconstruction for ONNX upscaling models.
/// </summary>
public static class OnnxTiler
{
    /// <summary>
    /// Default tile size constraint. 0 means auto-estimate safe tile size based on the model's
    /// activation footprint and VRAM.
    /// </summary>
    public const int DefaultTileSize = 0;

    /// <summary>
    /// Default overlap padding in pixels between adjacent tiles.
    /// </summary>
    public const int DefaultTilePad = 16;

    /// <summary>
    /// Smallest tile dimension the planner will enumerate. Below this the overlap/blending overhead
    /// per pixel and the loss of receptive-field context on manga screentones outweigh the memory
    /// saving, and driver overhead starts to dominate.
    /// <para>
    /// The emergency fallback returned when no affordable grid exists is not bound by this: it is
    /// <see cref="TileSplit.For"/> at this size, which shrinks each tile to fit the page exactly and
    /// can therefore land below the floor, and the engine halves it again when the driver refuses the
    /// allocation — down to <see cref="EmergencyTileFloor"/> and no further.
    /// </para>
    /// </summary>
    public const int MinimumTileSize = 256;

    /// <summary>
    /// Smallest tile side the memory-pressure descent may reach. Below it a smaller tile is not a
    /// cheaper version of the same inference but a different, much worse one: the model has no
    /// receptive-field context left to work with, so the page is upscaled in postage stamps and the
    /// only thing saved is the failure.
    /// <para>
    /// Reaching the floor therefore fails the page as the memory failure it is, which the worker
    /// classifies as retryable, instead of descending further. Deliberately separate from
    /// <see cref="MinimumTileSize"/>: that one bounds the <em>planned</em> grid and is what the
    /// planner aims for, while this bounds only the emergency descent after the driver has refused an
    /// allocation.
    /// </para>
    /// </summary>
    public const int EmergencyTileFloor = 128;

    /// <summary>
    /// Tile side the models need on both dimensions for full quality, and the side
    /// <see cref="PlanTileSplit"/> maximises the smaller dimension towards.
    /// <para>
    /// Below it a tile is genuinely worse: the model is missing context, and on manga screentones
    /// that shows up as structure loss where a split falls. Above it a larger tile has nothing left
    /// to look at, so extra size buys no quality — the planner therefore stops chasing it and spends
    /// the remaining headroom on the cheapest grid, which means fewer tiles and less total work.
    /// </para>
    /// </summary>
    public const int QualityTilePixels = 512;

    /// <summary>
    /// Alignment (in pixels) the tiler snaps tile dimensions to, so every tile produces the same
    /// uniform tensor shape and the driver never has to re-allocate per tile.
    /// </summary>
    public const int TileAlignment = 64;

    /// <summary>
    /// Device memory reserved for the execution provider's session state and its non-shrinking
    /// arena, independent of tile size. Subtracted from the budget before sizing tiles.
    /// </summary>
    public const long SessionReservationBytes = 384L * 1024 * 1024;

    /// <summary>
    /// VRAM usage at or below which the GPU is treated as quasi-exclusive to the upscaler (driver
    /// plus a display server or a couple of light helpers). Configurable via
    /// <see cref="UpscalerConfig.VramExclusiveThresholdBytes"/>.
    /// </summary>
    public const long DefaultExclusiveThresholdBytes = 350L * 1024 * 1024;

    /// <summary>
    /// A whole image is only run as a single pass when its predicted peak stays below this
    /// fraction of the budget. The headroom covers alignment/padding effects that the quadratic
    /// model does not capture exactly.
    /// </summary>
    public const double SinglePassSafetyFraction = 0.9;

    /// <summary>The tensor name the upscaler expects the single model input to have.</summary>
    private const string ModelInputName = "input";

    /// <summary>The tensor name the upscaler expects the single model output to have.</summary>
    private const string ModelOutputName = "output";

    private static readonly float[] ByteToFloatLut = CreateByteToFloatLut();
    private static readonly Float16[] ByteToFloat16Lut = CreateByteToFloat16Lut();
    private static readonly byte[] Float16ToByteLut = CreateFloat16ToByteLut();

    private static float[] CreateByteToFloatLut()
    {
        float[] lut = new float[256];
        for (int i = 0; i < 256; i++)
        {
            lut[i] = i / 255.0f;
        }
        return lut;
    }

    private static Float16[] CreateByteToFloat16Lut()
    {
        Float16[] lut = new Float16[256];
        for (int i = 0; i < 256; i++)
        {
            lut[i] = (Float16)(i / 255.0f);
        }
        return lut;
    }

    private static byte[] CreateFloat16ToByteLut()
    {
        byte[] lut = new byte[65536];
        for (int i = 0; i < 65536; i++)
        {
            Half h = BitConverter.UInt16BitsToHalf((ushort)i);
            if (IsNonFiniteHalf((ushort)i))
            {
                // An infinity saturates, a NaN has no image value at all. Neither is ever
                // written: <see cref="NonFiniteModelOutputException"/> rejects the whole
                // output before this table is consulted.
                lut[i] = float.IsNaN((float)h) ? (byte)0 : (byte)255;
            }
            else
            {
                // Values below 0 clamp to black and above 1 to white: models legitimately
                // overshoot slightly at high-contrast edges and that overshoot is not an error.
                float val = (float)h * 255.0f;
                lut[i] =
                    val <= 0f ? (byte)0
                    : val >= 255f ? (byte)255
                    : (byte)(val + 0.5f);
            }
        }
        return lut;
    }

    /// <summary>
    /// True for the two value classes a byte table cannot represent: an fp16 with an all-ones
    /// exponent is either a NaN or an infinity.
    /// </summary>
    private static bool IsNonFiniteHalf(ushort bits) => (bits & 0x7C00) == 0x7C00;

    /// <summary>
    /// Detects the neural network model architecture from its name or file path.
    /// </summary>
    public static ModelArchitecture DetectArchitecture(string modelNameOrPath)
    {
        string name = Path.GetFileNameWithoutExtension(modelNameOrPath);
        if (name.Contains("SPAN", StringComparison.OrdinalIgnoreCase))
            return ModelArchitecture.Span;
        if (name.Contains("HAT", StringComparison.OrdinalIgnoreCase))
            return ModelArchitecture.HatL;
        if (
            name.Contains("DAT2", StringComparison.OrdinalIgnoreCase)
            || (
                name.Contains("DAT", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("FDAT", StringComparison.OrdinalIgnoreCase)
            )
        )
            return ModelArchitecture.Dat2;
        if (name.Contains("FDAT_XL", StringComparison.OrdinalIgnoreCase))
            return ModelArchitecture.FdatXl;
        if (name.Contains("FDAT", StringComparison.OrdinalIgnoreCase))
            return ModelArchitecture.FdatM;
        if (
            name.Contains("ESRGAN", StringComparison.OrdinalIgnoreCase)
            || name.Contains("MangaJaNai", StringComparison.OrdinalIgnoreCase)
        )
            return ModelArchitecture.Esrgan;

        return ModelArchitecture.Unknown;
    }

    /// <summary>
    /// Live activation channels at a model's output resolution, for the fp16 and fp32 export of the
    /// architecture.
    /// <para>
    /// These numbers come from a graph-level working-set analysis of every model shipped in
    /// <see cref="MangaJaNaiUpscaler"/> (see <c>docs/UPSCALING_MEMORY_MODEL.md</c>): a sequential
    /// liveness sweep over the ONNX graph shows the peak of one inference is always dominated by a
    /// handful of tensors living at output resolution (upsampler activations and, for the
    /// transformer models, the attention maps). Expressed per input pixel the analytic working set
    /// is <c>channels * scale^2 * elementSize</c> bytes, and the coefficients below are that
    /// analytic value multiplied by the execution-model overhead measured for the ORT WebGPU EP
    /// (fp32 casts, NCHWc re-packing and a non-shrinking arena keep ~5x that set resident), so
    /// <see cref="EstimateActivationBytes"/> matches observed VRAM footprints within a few percent.
    /// </para>
    /// <para>
    /// The two precisions are stored rather than derived from the element size, because the fp16 and
    /// fp32 exports of one architecture are not related by it: the fp16 graphs cast to fp32 around the
    /// upsampler, so their peak is already partly fp32-sized, while an fp32 graph keeps fp32 tensors
    /// throughout. Measured bytes per input pixel on an RX 9070 XT / WebGPU EP, the fp32/fp16 ratio of
    /// this coefficient is 0.50 (SPAN), 0.77 (ESRGAN), 1.28 (FDAT_M), 1.34 (FDAT_XL), 1.10 (DAT2) and
    /// 1.08 (HAT_L). A single fp16-fitted column therefore under-estimated every transformer export
    /// that actually runs on WebGPU — the fp16 ones need bf16 and are skipped — by up to 34%, which is
    /// what let the fp32 FDAT_M fill a 16 GB card.
    /// </para>
    /// <para>
    /// The fp16 column for FDAT_XL, DAT2 and HAT_L is the original measurement: those exports cannot be
    /// re-measured on WebGPU because they need bf16 and return NaN there, the same reason the engine
    /// falls back to their fp32 export.
    /// </para>
    /// </summary>
    public static (double Fp16, double Fp32) GetOutputLiveChannels(
        ModelArchitecture architecture,
        long modelSizeBytes = 0
    ) =>
        architecture switch
        {
            ModelArchitecture.Span => (315.0, 158.0),
            ModelArchitecture.Esrgan => (598.0, 463.0),
            ModelArchitecture.FdatM => (462.0, 590.0),
            ModelArchitecture.FdatXl => (660.0, 882.0),
            ModelArchitecture.Dat2 => (1060.0, 1167.0),
            ModelArchitecture.HatL => (1900.0, 2055.0),
            _ => UnknownChannels(modelSizeBytes),
        };

    /// <summary>
    /// Fallback coefficient for a model whose architecture is not recognised: scale from the parameter
    /// count. The exponent sits between the pure conv nets (sqrt(params) ~ footprint) and the
    /// attention models (attention maps add a fixed multiple), so an unrecognised model is never
    /// under-estimated. The same value is used for both precisions, which over-estimates an fp32 model
    /// — conservative — and an fp32 export is also about twice the file size, which the sqrt reflects.
    /// </summary>
    private static (double Fp16, double Fp32) UnknownChannels(long modelSizeBytes)
    {
        double channels =
            310.0 + 620.0 * Math.Sqrt(Math.Max(modelSizeBytes, 0L) / 1048576.0 / 100.0);
        return (channels, channels);
    }

    /// <summary>
    /// Activation footprint of a model as a coefficient set, so the same numbers can be used for
    /// estimation, for the device benchmark, and for diagnostics.
    /// </summary>
    public readonly record struct ActivationCoefficients(double Fp16Channels, double Fp32Channels)
    {
        /// <summary>
        /// The fp16 coefficient, the single value the tables and diagnostics quote. Planning goes
        /// through <see cref="BytesPerPixel"/>, which selects by precision.
        /// </summary>
        public double OutputLiveChannels => Fp16Channels;

        public static ActivationCoefficients For(string modelNameOrPath, long modelSizeBytes = 0)
        {
            var arch = DetectArchitecture(modelNameOrPath);
            (double fp16, double fp32) = GetOutputLiveChannels(arch, modelSizeBytes);
            return new ActivationCoefficients(fp16, fp32);
        }

        /// <summary>Activation bytes per input pixel for a given scale and precision.</summary>
        public double BytesPerPixel(int scale, bool isFp16) =>
            (isFp16 ? Fp16Channels : Fp32Channels)
            * Math.Max(1, scale)
            * Math.Max(1, scale)
            * (isFp16 ? 2 : 4);
    }

    /// <summary>
    /// Activation working set of a single inference on a tile with <paramref name="tilePixels"/>
    /// input pixels, including the execution-model overhead of the active execution provider.
    /// Scales purely quadratically with tile size — there is no tile-size-dependent cliff, which is
    /// what allows the tile size to be derived instead of capped.
    /// </summary>
    public static long EstimateActivationBytes(
        int tilePixels,
        int scale,
        string modelNameOrPath,
        long modelSizeBytes = 0,
        bool isFp16 = false
    )
    {
        ActivationCoefficients coefficients = ActivationCoefficients.For(
            modelNameOrPath,
            modelSizeBytes
        );
        return (long)(coefficients.BytesPerPixel(scale, isFp16) * tilePixels);
    }

    /// <summary>
    /// Weight (initializer) bytes the model occupies on the device. The ONNX file size is the
    /// straightforward upper bound: the zipped payload is dominated by the fp16/fp32 initializers.
    /// </summary>
    public static long EstimateModelBytes(string modelNameOrPath, long modelSizeBytes = 0)
    {
        if (modelSizeBytes > 0)
        {
            return modelSizeBytes;
        }

        try
        {
            return new FileInfo(modelNameOrPath).Length;
        }
        catch
        {
            return 70L * 1024 * 1024;
        }
    }

    /// <summary>
    /// Estimates peak device memory for upscaling a region of <paramref name="width"/> x
    /// <paramref name="height"/> pixels: model weights plus the activation working set of a single
    /// inference, rounded up to the tiler's uniform-tensor alignment. Edges of tiles carry
    /// <see cref="DefaultTilePad"/> overlap on each side, which <paramref name="includeTilePadding"/>
    /// accounts for.
    /// </summary>
    public static long EstimatePeakMemoryBytes(
        int width,
        int height,
        int scale,
        string modelNameOrPath,
        long modelSizeBytes = 0,
        bool isFp16 = false,
        bool includeTilePadding = true
    )
    {
        long weights = EstimateModelBytes(modelNameOrPath, modelSizeBytes);
        int pad = includeTilePadding ? 2 * DefaultTilePad : 0;
        long pixels = (long)AlignedTileDimension(width + pad) * AlignedTileDimension(height + pad);
        return weights
            + EstimateActivationBytes(
                (int)Math.Min(pixels, int.MaxValue),
                scale,
                modelNameOrPath,
                modelSizeBytes,
                isFp16
            );
    }

    /// <summary>
    /// Rounds a dimension up to the alignment the tiler uses for uniform tensors (64 px), which
    /// keeps per-tile shapes stable and avoids driver re-allocation churn.
    /// </summary>
    public static int AlignedTileDimension(int value)
    {
        if (value <= 0)
        {
            return TileAlignment;
        }

        return ((value + TileAlignment - 1) / TileAlignment) * TileAlignment;
    }

    /// <summary>
    /// How a page of <paramref name="Columns"/> x <paramref name="Rows"/> tiles of
    /// <paramref name="TileWidth"/> x <paramref name="TileHeight"/> splits for a maximum tile size.
    /// Single source of truth for the split, shared by the tiler and the tile-size derivation so
    /// both always agree on what a tile size actually buys.
    /// </summary>
    public readonly record struct TileSplit(int Columns, int Rows, int TileWidth, int TileHeight)
    {
        public static TileSplit For(int width, int height, int tileSize)
        {
            int bound = tileSize > 0 ? tileSize : Math.Max(width, height);
            int columns = Math.Max(1, (width + bound - 1) / bound);
            int rows = Math.Max(1, (height + bound - 1) / bound);
            return new TileSplit(
                columns,
                rows,
                Math.Max(1, (width + columns - 1) / columns),
                Math.Max(1, (height + rows - 1) / rows)
            );
        }

        /// <summary>
        /// Pixels a single tile of this split is really inferred at: the tile plus
        /// <see cref="DefaultTilePad"/> overlap on every side, rounded up to the tensor alignment.
        /// </summary>
        public long PaddedTilePixels
        {
            get
            {
                (int w, int h) = OnnxTiler.UniformTileTensorShape(this);
                return (long)w * h;
            }
        }

        /// <summary>Pixels the whole page is inferred at, overlap and alignment waste included.</summary>
        public long TotalPaddedPixels => PaddedTilePixels * Columns * Rows;
    }

    /// <summary>
    /// The single padded tensor shape every tile of this split is inferred at.
    /// <para>
    /// It follows the tiles the page actually splits into, never the tile size the budget allowed:
    /// a page narrower or shorter than that budget would otherwise be inferred at the budget's shape
    /// and throw away everything between the page edge and the tile edge as reflection padding — an
    /// 800 px wide page inferred as a 1600 px tile pays double for the same output.
    /// </para>
    /// </summary>
    /// <param name="overlap">
    /// The per-side overlap the tiling path will actually use. It defaults to
    /// <see cref="DefaultTilePad"/> and must be passed through from the tiling path, otherwise the
    /// tensor this reports would disagree with the one the tiles are padded to — and, because
    /// <see cref="TileSplit.PaddedTilePixels"/> sizes the budget from <see cref="DefaultTilePad"/>,
    /// a larger overlap would also make the planner under-estimate the peak it exists to bound.
    /// </param>
    public static (int Width, int Height) UniformTileTensorShape(
        TileSplit split,
        int overlap = DefaultTilePad
    ) =>
        (
            Math.Max(TileAlignment, ((split.TileWidth + 2 * overlap + 63) / 64) * 64),
            Math.Max(TileAlignment, ((split.TileHeight + 2 * overlap + 63) / 64) * 64)
        );

    /// <summary>
    /// The two device-specific factors the planner uses: how many times the analytic working set the
    /// execution provider really needs, and the tile-independent session footprint. Both come from
    /// the measured profile where it has a value and from the built-ins otherwise (those are the
    /// WebGPU/AMD values, so they are conservative rather than optimistic elsewhere).
    /// <para>
    /// The activation factor is resolved per model, because it is a property of the model and the
    /// provider together rather than of the device: measured on an RX 9070 XT with the WebGPU EP the
    /// fp16 ESRGAN needs 0.90x its analytic working set, the fp32 ESRGAN 0.69x and the fp32 FDAT_M
    /// 1.28x. A single device-wide factor cannot represent that range, and using one under-estimated
    /// the fp32 transformer enough to fill the card and spill into GTT.
    /// </para>
    /// </summary>
    internal static (double ActivationScale, long SessionReservationBytes) ResolveProfileFactors(
        DeviceMemoryProfile? profile,
        string modelNameOrPath,
        long modelSizeBytes
    )
    {
        double activationScale = 1.0;
        long sessionReservation = SessionReservationBytes;
        if (profile is null)
        {
            return (activationScale, sessionReservation);
        }

        // null means nothing was measured for this model (neither its own entry nor a measured
        // device-wide default), so the built-in stays; a *measured* 0 is a value and is honored.
        double? measuredScale = profile.ScaleFor(modelNameOrPath, modelSizeBytes);
        // The same closed range the calibrator refuses to write outside (DeviceMemoryCalibrator) and
        // the profile reader applies (DeviceMemoryProfile.MinActivationScale/MaxActivationScale): a
        // profile value outside it is corrupt. Kept here too because this method is also the consumer
        // of a profile handed in directly (tests, callers) rather than only through the reader.
        if (
            measuredScale is { } scale
            && scale
                is >= DeviceMemoryProfile.MinActivationScale
                    and <= DeviceMemoryProfile.MaxActivationScale
        )
        {
            activationScale = scale;
        }

        long? measuredReservation = profile.ReservationFor(modelNameOrPath, modelSizeBytes);
        // 0 included: a calibration that measured no tile-independent footprint (the model's
        // session lives inside the activation estimate) is a legitimate answer, and the
        // calibrator writes exactly 0 for one. Rejecting it silently swapped the 384 MiB
        // default in, oversizing every tile of that model for the rest of the profile's life.
        // The upper bound is inclusive, matching the calibrator's cap (it zeroes a reservation
        // only *above* DeviceMemoryProfile.MaxSessionReservationBytes).
        if (
            measuredReservation is { } reservation
            && reservation is >= 0 and <= DeviceMemoryProfile.MaxSessionReservationBytes
        )
        {
            sessionReservation = reservation;
        }

        return (activationScale, sessionReservation);
    }

    /// <summary>
    /// The device memory one tile of <paramref name="split"/> is predicted to need, using the same
    /// factors <see cref="PlanTileSplit"/> fits the budget with. Logged next to the measured peak so
    /// an estimate that is off shows up as a number in the log instead of only as a spill.
    /// </summary>
    public static long PredictTilePeakBytes(
        TileSplit split,
        int scale,
        string modelNameOrPath,
        long modelSizeBytes = 0,
        bool isFp16 = false,
        DeviceMemoryProfile? profile = null
    )
    {
        (double activationScale, long sessionReservation) = ResolveProfileFactors(
            profile,
            modelNameOrPath,
            modelSizeBytes
        );
        double bytesPerPixel =
            ActivationCoefficients.For(modelNameOrPath, modelSizeBytes).BytesPerPixel(scale, isFp16)
            * activationScale;

        return EstimateModelBytes(modelNameOrPath, modelSizeBytes)
            + sessionReservation
            + (long)(bytesPerPixel * split.PaddedTilePixels);
    }

    /// <summary>
    /// Plans the grid a page of <paramref name="width"/> x <paramref name="height"/> is upscaled in
    /// for <paramref name="memoryBudgetBytes"/>: the number of tiles per axis and their size.
    /// <para>
    /// A single tile is run at a time, so a grid is affordable when one of its tiles is. Among the
    /// affordable grids the one with the fewest total padded pixels is planned, because that is the
    /// page for the least inference work — which is throughput, since one inference runs at a time.
    /// The alternative of deriving a square tile side from the budget leaves most of it unused
    /// whenever the page's own aspect does not match: a 1125x1600 4x page at 13 GB splits 2x3 at a
    /// 704 px tile side, while a 2x2 grid covers the same page with fewer, better-fitting tiles.
    /// </para>
    /// <para>
    /// The whole page as one inference is only planned when its footprint stays below
    /// <see cref="SinglePassSafetyFraction"/> of the budget, because a single-pass overflow cannot
    /// be recovered without restarting the page. There is deliberately no upper cap beyond that:
    /// the memory model is quadratic in tile pixels with no cliff, so a large budget on a big card
    /// yields a single pass instead of an arbitrary clamp that left memory and throughput unused.
    /// </para>
    /// </summary>
    public static TileSplit PlanTileSplit(
        int width,
        int height,
        int scale,
        string modelNameOrPath,
        long memoryBudgetBytes,
        long modelSizeBytes = 0,
        bool isFp16 = false,
        DeviceMemoryProfile? profile = null
    )
    {
        long weights = EstimateModelBytes(modelNameOrPath, modelSizeBytes);
        ActivationCoefficients analytic = ActivationCoefficients.For(
            modelNameOrPath,
            modelSizeBytes
        );

        // A measured profile replaces the built-in execution-model factor and session reservation
        // with what this accelerator actually does. See ResolveProfileFactors for why the activation
        // factor is taken per model rather than per device.
        (double activationScale, long sessionReservation) = ResolveProfileFactors(
            profile,
            modelNameOrPath,
            modelSizeBytes
        );

        double bytesPerPixel = analytic.BytesPerPixel(scale, isFp16) * activationScale;

        if (width <= 0 || height <= 0)
        {
            return TileSplit.For(Math.Max(width, 1), Math.Max(height, 1), MinimumTileSize);
        }

        // The session reservation is device memory the tiled path holds just as surely as the
        // single-pass one, so it comes off the budget before either decision is made. Both the
        // constant's own documentation and the memory-model doc promise this; leaving it out of the
        // grid fit test let a grid be planned that overran the budget by exactly this much, which is
        // the case the 0.9 single-pass fraction exists to avoid — and it absorbed it only because the
        // fraction happens to be larger than the reservation on a big budget.
        long sessionBudget = memoryBudgetBytes - sessionReservation;
        if (sessionBudget <= weights)
        {
            // Nothing left for activations even with the reservation accounted for. Splitting at the
            // quality floor is the best that can be done: it protects the output, and the engine
            // halves the tiles again if the driver then refuses the allocation. For a page small
            // enough that the floor still fits it in one tile the fallback is the whole page — the
            // very plan the single-pass gate below rejects — so it takes the same emergency
            // normalisation the grid fallback does.
            return NormaliseEmergencySplit(
                TileSplit.For(width, height, MinimumTileSize),
                width,
                height
            );
        }

        // Whole page as one inference: only claim it when the padded footprint stays comfortably
        // inside the budget, because a single-pass overflow cannot be recovered without restarting
        // the page.
        TileSplit singlePass = TileSplit.For(width, height, 0);
        if (
            weights + (long)(bytesPerPixel * singlePass.PaddedTilePixels)
            <= sessionBudget * SinglePassSafetyFraction
        )
        {
            return singlePass;
        }

        // Floor division, not ceil: ceil(width / MinimumTileSize) columns still allowed the last
        // column count to shrink a tile below the floor (1125 -> 5 columns -> 225), which is exactly
        // the quality the constant exists to protect. Floor division bounds columns so
        // ceil(width / columns) >= MinimumTileSize, and columns = 1 stays reachable, so the largest
        // tile is still considered.
        int maxColumns = Math.Max(1, width / MinimumTileSize);
        int maxRows = Math.Max(1, height / MinimumTileSize);

        // The seed doubles as the fallback returned when no affordable grid was found. Its tiles can
        // be finer than MinimumTileSize (TileSplit.For shrinks them to cover the page exactly), which
        // is the emergency the engine halves again on allocation failure.
        TileSplit best = TileSplit.For(width, height, MinimumTileSize);
        long bestWork = long.MaxValue;
        int bestContext = -1;

        for (int columns = 1; columns <= maxColumns; columns++)
        {
            int tileWidth = (width + columns - 1) / columns;

            for (int rows = 1; rows <= maxRows; rows++)
            {
                int tileHeight = (height + rows - 1) / rows;

                // A single tile covering the page is the single-pass decision, made above. Letting
                // the loop pick it back here would undo the safety margin that decision applied.
                if (columns == 1 && rows == 1)
                {
                    continue;
                }

                var candidate = new TileSplit(columns, rows, tileWidth, tileHeight);

                if (weights + (long)(bytesPerPixel * candidate.PaddedTilePixels) > sessionBudget)
                {
                    continue;
                }

                // Quality target: the smaller side as close to QualityTilePixels as the budget
                // allows, and no further. Below it the model is missing receptive-field context on
                // manga screentones; above it there is nothing left for a larger tile to see, so the
                // size is free and is spent on the cheapest grid instead.
                int context = Math.Min(Math.Min(tileWidth, tileHeight), QualityTilePixels);
                long work = candidate.TotalPaddedPixels;

                if (context > bestContext || (context == bestContext && work < bestWork))
                {
                    best = candidate;
                    bestContext = context;
                    bestWork = work;
                }
            }
        }

        // A page whose both sides are at or below MinimumTileSize has no candidate at all: the
        // enumeration bounds are floor(width / MinimumTileSize) and floor(height / MinimumTileSize),
        // which are 1 and 1, and the (1,1) candidate is skipped above. The seed would then be a single
        // tile covering the page — the exact plan the single-pass gate rejected, at a fraction of the
        // budget it was rejected for. The shared emergency normalisation below turns it into a 2x2
        // grid, its tiles finer than the floor, which is what the engine would have halved down to
        // after the driver refused the allocation anyway.
        best = NormaliseEmergencySplit(best, width, height);

        return best;

        // A whole-page (1,1) split is the plan the single-pass gate above rejected, and must never
        // escape the planner. Every path that can produce one — the grid fallback here and the
        // budget-below-reservation early return above — normalises it through this one helper, so the
        // emergency grid is defined in a single place.
        static TileSplit NormaliseEmergencySplit(TileSplit split, int width, int height) =>
            split.Columns == 1 && split.Rows == 1
                ? new TileSplit(2, 2, Math.Max(1, (width + 1) / 2), Math.Max(1, (height + 1) / 2))
                : split;
    }

    /// <summary>
    /// The budget for a card whose driver reported <c>VK_EXT_memory_budget</c> metrics. Pure, so the
    /// exclusive/shared decision can be pinned without a GPU.
    /// <para>
    /// The driver's budget is total minus what is already in use, with no eviction headroom of its
    /// own. On a card that is otherwise idle that is exactly what should be spent, and an operator who
    /// wants the whole card gets it. On a shared card it is too much: the driver starts evicting into
    /// GTT past ~85-90% utilisation, which is the spill the margin in
    /// <see cref="CalculateVramBudget"/> exists to avoid, so the driver's number is capped by it. The
    /// boundary is the same configurable one the calculated path uses, so a card shared with a partner
    /// that stays bounded can still be budgeted as exclusive.
    /// </para>
    /// </summary>
    internal static long BudgetFromVulkanMetrics(
        VulkanGpuMemory metrics,
        double? utilizationFraction = null,
        long? safetyMarginBytes = null,
        long? exclusiveThresholdBytes = null
    )
    {
        // If explicit overrides were provided, respect them
        if (
            HasExplicitVramBudgetOverride(
                utilizationFraction,
                safetyMarginBytes,
                exclusiveThresholdBytes
            )
        )
        {
            return CalculateVramBudget(
                metrics.TotalVramBytes,
                metrics.UsedVramBytes,
                utilizationFraction,
                safetyMarginBytes,
                exclusiveThresholdBytes
            );
        }

        long exclusiveThreshold = exclusiveThresholdBytes is > 0
            ? exclusiveThresholdBytes.Value
            : DefaultExclusiveThresholdBytes;

        if (metrics.BudgetBytes > 0 && metrics.UsedVramBytes <= exclusiveThreshold)
        {
            return metrics.BudgetBytes;
        }

        long calculated = CalculateVramBudget(
            metrics.TotalVramBytes,
            metrics.UsedVramBytes,
            utilizationFraction,
            safetyMarginBytes,
            exclusiveThresholdBytes
        );

        return metrics.BudgetBytes > 0 ? Math.Min(metrics.BudgetBytes, calculated) : calculated;
    }

    /// <summary>
    /// Calculates the available VRAM budget for tile estimation from total and currently used VRAM.
    /// <para>
    /// At or below <see cref="DefaultExclusiveThresholdBytes"/> of usage (headless/NAS, or a partner
    /// process whose footprint is known to stay bounded) the card is budgeted as quasi-exclusive:
    /// only a minimal reserve of 256 MB is kept. Above it the budget keeps a desktop-sized margin of
    /// 15% of total VRAM (clamped to 1.5-3.0 GiB), because drivers start evicting VRAM into system
    /// memory (GTT) past 85-90% utilisation.
    /// </para>
    /// <para>
    /// <paramref name="exclusiveThresholdBytes"/> overrides where that boundary sits; see
    /// <see cref="UpscalerConfig.VramExclusiveThresholdBytes"/>. A custom utilization fraction or
    /// safety margin still takes precedence over both branches.
    /// </para>
    /// </summary>
    public static long CalculateVramBudget(
        long totalVram,
        long usedVram,
        double? customUtilizationFraction = null,
        long? customSafetyMarginBytes = null,
        long? exclusiveThresholdBytes = null
    )
    {
        long freeVram = Math.Max(0, totalVram - usedVram);
        if (freeVram <= 0)
        {
            return 4L * 1024 * 1024 * 1024; // Safe fallback
        }

        if (customUtilizationFraction.HasValue && customUtilizationFraction.Value > 0)
        {
            double fraction = Math.Clamp(customUtilizationFraction.Value, 0.1, 1.0);
            return (long)(freeVram * fraction);
        }

        if (customSafetyMarginBytes.HasValue && customSafetyMarginBytes.Value >= 0)
        {
            return Math.Max(256L * 1024 * 1024, freeVram - customSafetyMarginBytes.Value);
        }

        // Automatic adaptive budgeting. The boundary is configurable because "is anything else
        // using the card?" is not always a yes/no question: a video transcoder that reliably holds
        // 700 MB still leaves the rest of the card to the upscaler, and budgeting for a full desktop
        // there would throw away several GB of VRAM.
        long exclusiveThreshold = exclusiveThresholdBytes is > 0
            ? exclusiveThresholdBytes.Value
            : DefaultExclusiveThresholdBytes;

        long safetyMargin;
        if (usedVram <= exclusiveThreshold)
        {
            safetyMargin = Math.Min(256L * 1024 * 1024, (long)(freeVram * 0.03));
        }
        else
        {
            safetyMargin = Math.Clamp(
                (long)(totalVram * 0.15),
                1536L * 1024 * 1024,
                3072L * 1024 * 1024
            );
        }

        return Math.Max(512L * 1024 * 1024, freeVram - safetyMargin);
    }

    /// <summary>
    /// True when any of the optional VRAM budget overrides is set to a value
    /// <see cref="CalculateVramBudget"/> would actually act on: a positive utilization fraction, a
    /// non-negative safety margin, or a positive exclusive threshold.
    /// <para>
    /// This is the gate that decides whether an override replaces the driver's own
    /// <c>VK_EXT_memory_budget</c> value or that value is used verbatim. A custom exclusive threshold
    /// is a reason to do the arithmetic here just as much as a custom fraction or margin: it changes
    /// the branch <see cref="CalculateVramBudget"/> takes, so routing a threshold-only setting to the
    /// driver budget would silently ignore it. Values the documentation defines as "use the default"
    /// (a non-positive threshold, a non-positive fraction, a negative margin) deliberately do not
    /// count, so they keep the driver's value.
    /// </para>
    /// </summary>
    internal static bool HasExplicitVramBudgetOverride(
        double? utilizationFraction,
        long? safetyMarginBytes,
        long? exclusiveThresholdBytes
    ) =>
        (utilizationFraction.HasValue && utilizationFraction.Value > 0)
        || (safetyMarginBytes.HasValue && safetyMarginBytes.Value >= 0)
        || (exclusiveThresholdBytes.HasValue && exclusiveThresholdBytes.Value > 0);

    /// <summary>
    /// Discovers available GPU VRAM and computes the safe memory budget for tile size estimation.
    /// Prioritizes Vulkan VK_EXT_memory_budget (driver-calculated memory budget across AMD, NVIDIA, Intel),
    /// falling back to Linux DRM sysfs (AMD/Intel) and nvidia-smi (NVIDIA), and finally a safe default budget.
    /// Automatically adapts to dedicated/NAS vs desktop environments.
    /// <para>
    /// The driver's own <c>heapBudget</c> is used verbatim only while the card counts as quasi-exclusive
    /// (see <see cref="CalculateVramBudget"/> and <c>VramExclusiveThresholdBytes</c>). Once something
    /// else is using the card it is capped by the calculated budget, because the driver's number is
    /// total-minus-used with no eviction headroom and would spend the card to the point where the
    /// driver starts evicting into GTT.
    /// </para>
    /// </summary>
    public static long GetAvailableVramBudget(
        int deviceId = 0,
        double? utilizationFraction = null,
        long? safetyMarginBytes = null,
        long? exclusiveThresholdBytes = null
    )
    {
        // 1. Try Vulkan VK_EXT_memory_budget (primary for all GPUs)
        var vkMem = VulkanMemoryProvider.QueryDevice(deviceId);
        if (vkMem != null && vkMem.TotalVramBytes > 0)
        {
            return BudgetFromVulkanMetrics(
                vkMem,
                utilizationFraction,
                safetyMarginBytes,
                exclusiveThresholdBytes
            );
        }

        // 2. Secondary fallback: DRM sysfs or nvidia-smi
        var (total, used, free) = GetGpuVramInfo(deviceId);
        if (total > 0 && free > 0)
        {
            return CalculateVramBudget(
                total,
                used,
                utilizationFraction,
                safetyMarginBytes,
                exclusiveThresholdBytes
            );
        }

        // Safe fallback for systems where no GPU query succeeded (e.g. CPU): 4 GiB
        return 4L * 1024 * 1024 * 1024;
    }

    /// <summary>
    /// Gets total, used, and free GPU VRAM in bytes for the specified device.
    /// Supports Vulkan VK_EXT_memory_budget (primary), Linux AMD/Intel DRM sysfs, and NVIDIA via nvidia-smi.
    /// Returns (0, 0, 0) if GPU memory cannot be detected.
    /// </summary>
    public static (long Total, long Used, long Free) GetGpuVramInfo(int deviceId = 0)
    {
        // 1. Primary: Vulkan VK_EXT_memory_budget
        var vkMem = VulkanMemoryProvider.QueryDevice(deviceId);
        if (vkMem != null && vkMem.TotalVramBytes > 0)
        {
            long free =
                vkMem.BudgetBytes > 0
                    ? vkMem.BudgetBytes
                    : Math.Max(0, vkMem.TotalVramBytes - vkMem.UsedVramBytes);
            return (vkMem.TotalVramBytes, vkMem.UsedVramBytes, free);
        }

        // 2. Fallback: Linux DRM sysfs (AMD and Intel)
        var drmCards = QueryDrmGpus();
        if (drmCards.Count > 0)
        {
            var selected =
                (deviceId >= 0 && deviceId < drmCards.Count)
                    ? drmCards[deviceId]
                    : drmCards.OrderByDescending(c => c.Total).First();

            long free = Math.Max(0, selected.Total - selected.Used);
            return (selected.Total, selected.Used, free);
        }

        // 3. Fallback: NVIDIA (nvidia-smi)
        var nvidiaInfo = TryGetNvidiaGpuMemory(deviceId);
        if (nvidiaInfo.HasValue && nvidiaInfo.Value.Total > 0)
        {
            long free = Math.Max(0, nvidiaInfo.Value.Total - nvidiaInfo.Value.Used);
            return (nvidiaInfo.Value.Total, nvidiaInfo.Value.Used, free);
        }

        return (0, 0, 0);
    }

    /// <summary>
    /// Reads current VRAM and GTT used in bytes, or returns (0, 0) if unavailable.
    /// </summary>
    public static (long VramUsed, long GttUsed) GetGpuMemoryUsage(int deviceId = 0)
    {
        // Linux DRM sysfs has direct kernel-level mem_info_gtt_used tracking for AMD
        var drmCards = QueryDrmGpus();
        if (drmCards.Count > 0)
        {
            var selected =
                (deviceId >= 0 && deviceId < drmCards.Count)
                    ? drmCards[deviceId]
                    : drmCards.OrderByDescending(c => c.Total).First();

            return (selected.Used, selected.GttUsed);
        }

        // Vulkan memory tracking
        var vkMem = VulkanMemoryProvider.QueryDevice(deviceId);
        if (vkMem != null && vkMem.TotalVramBytes > 0)
        {
            return (vkMem.UsedVramBytes, vkMem.UsedGttBytes);
        }

        var nvidiaInfo = TryGetNvidiaGpuMemory(deviceId);
        if (nvidiaInfo.HasValue)
        {
            return (nvidiaInfo.Value.Used, 0);
        }

        return (0, 0);
    }

    /// <summary>
    /// The sysfs files one DRM card's memory accounting is read from. Which vendor's files a card
    /// uses cannot change once the card is known, so they are resolved once instead of per read:
    /// the calibration sampler reads them every millisecond while an inference is running, and
    /// re-resolving them cost a directory enumeration plus half a dozen existence checks per sample.
    /// </summary>
    private readonly record struct DrmCardFiles(
        string CardPath,
        string Total,
        string Used,
        string GttUsed
    );

    private static readonly Lock DrmCardFilesGate = new();
    private static List<DrmCardFiles>? _drmCardFiles;

    private static List<(string CardPath, long Total, long Used, long GttUsed)> QueryDrmGpus()
    {
        var result = new List<(string CardPath, long Total, long Used, long GttUsed)>();
        foreach (DrmCardFiles card in ResolveDrmCardFiles())
        {
            long total = ReadSysfsLong(card.Total);
            if (total <= 0)
            {
                continue;
            }

            result.Add(
                (card.CardPath, total, ReadSysfsLong(card.Used), ReadSysfsLong(card.GttUsed))
            );
        }

        return result;
    }

    private static long ReadSysfsLong(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return 0;
            }

            return long.TryParse(
                File.ReadAllText(path).Trim(),
                CultureInfo.InvariantCulture,
                out long value
            )
                ? value
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static List<DrmCardFiles> ResolveDrmCardFiles()
    {
        // Cached per process. A card that disappears becomes unreadable rather than wrongly listed,
        // and a card that appears late is picked up because the list starts out empty on a host
        // where nothing was found yet — which is the only transition that matters for a process
        // that would be reading sysfs at all.
        if (_drmCardFiles is { Count: > 0 })
        {
            return _drmCardFiles;
        }

        // The calibration sampler reads the card files from more than one thread, so the first
        // resolution is single-flight: without the gate each racing caller repeated the directory
        // enumeration and every existence check. The list is fully built before it is assigned, so
        // the duplicate work was the only cost — the gate just removes it.
        lock (DrmCardFilesGate)
        {
            // Another caller may have won the race while this one waited for the gate.
            if (_drmCardFiles is { Count: > 0 })
            {
                return _drmCardFiles;
            }

            var files = new List<DrmCardFiles>();
            try
            {
                if (Directory.Exists("/sys/class/drm"))
                {
                    var cardDirs = Directory
                        .GetDirectories("/sys/class/drm", "card*")
                        .Where(d => !Path.GetFileName(d).Contains('-'))
                        .OrderBy(d => d)
                        .ToList();

                    foreach (string card in cardDirs)
                    {
                        string deviceDir = Path.Combine(card, "device");
                        if (!Directory.Exists(deviceDir))
                        {
                            continue;
                        }

                        // AMD GPU sysfs paths
                        string amdTotal = Path.Combine(deviceDir, "mem_info_vram_total");
                        string amdUsed = Path.Combine(deviceDir, "mem_info_vram_used");
                        string amdGtt = Path.Combine(deviceDir, "mem_info_gtt_used");

                        if (File.Exists(amdTotal))
                        {
                            files.Add(new DrmCardFiles(card, amdTotal, amdUsed, amdGtt));
                            continue;
                        }

                        // Intel Xe / i915 sysfs paths. Either family can be present; the first one
                        // that reports a total is the one this card uses.
                        string intelTile0Total = Path.Combine(
                            deviceDir,
                            "tile0",
                            "memory0",
                            "total_bytes"
                        );
                        string intelTile0Alloc = Path.Combine(
                            deviceDir,
                            "tile0",
                            "memory0",
                            "alloc_bytes"
                        );
                        string intelLmemTotal = Path.Combine(deviceDir, "lmem_total_bytes");
                        string intelLmemAlloc = Path.Combine(deviceDir, "lmem_alloc_bytes");

                        if (File.Exists(intelTile0Total))
                        {
                            files.Add(new DrmCardFiles(card, intelTile0Total, intelTile0Alloc, ""));
                        }
                        else if (File.Exists(intelLmemTotal))
                        {
                            files.Add(new DrmCardFiles(card, intelLmemTotal, intelLmemAlloc, ""));
                        }
                    }
                }
            }
            catch
            {
                // Ignore DRM sysfs read errors
            }

            if (files.Count > 0)
            {
                _drmCardFiles = files;
            }

            return files;
        }
    }

    /// <summary>
    /// How long the <c>nvidia-smi</c> probe may take. The process is killed once it expires: a query
    /// against a wedged driver must not outlive the call that asked for it.
    /// </summary>
    private const int NvidiaSmiTimeoutMs = 1000;

    private static (long Total, long Used)? TryGetNvidiaGpuMemory(int deviceId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments =
                    $"--id={deviceId} --query-gpu=memory.total,memory.used --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return null;
            }

            // Both pipes are drained concurrently and the wait is bounded. Draining only stdout let a
            // chatty nvidia-smi fill the stderr pipe and block inside ReadToEnd — with the child never
            // exiting, the wait below was never reached and the caller hung on a wedged driver. The
            // child is killed when the bound expires so it cannot outlive the call, and ExitCode is only
            // read once it has actually exited: reading it after a timed-out wait throws, which the
            // outer catch would have swallowed while leaving the process running.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(NvidiaSmiTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone, or not killable from here; nothing useful left to do.
                }

                return null;
            }

            string output = stdout.GetAwaiter().GetResult();
            stderr.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                return null;
            }

            // Output format: "16384, 2560" (values in MiB)
            var parts = output.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            );
            if (
                parts.Length >= 2
                && long.TryParse(parts[0], CultureInfo.InvariantCulture, out long totalMib)
                && long.TryParse(parts[1], CultureInfo.InvariantCulture, out long usedMib)
            )
            {
                return (totalMib * 1024L * 1024L, usedMib * 1024L * 1024L);
            }
        }
        catch
        {
            // nvidia-smi not available or failed
        }

        return null;
    }

    /// <summary>
    /// Upscales an RGB image represented by raw interleaved RGB byte array.
    /// When tileSize &lt;= 0, attempts whole-image inference in a single pass.
    /// When tileSize &gt; 0, partitions the image into symmetric tiles bounded by tileSize
    /// with smooth half-sine overlap blending to eliminate visible seams.
    /// </summary>
    public static byte[] UpscaleRgb(
        byte[] rgbBytes,
        int width,
        int height,
        int scale,
        InferenceSession session,
        TileSplit split,
        int tilePad = DefaultTilePad,
        CancellationToken cancellationToken = default
    )
    {
        // No halve-and-retry loop here. The caller owns the recovery, because the recovery is not
        // only "make the tiles smaller" but "drop the session whose arena made the allocation fail" —
        // and that flush may only be taken with no inference scope held, which this method is always
        // called inside. A loop here halved the tiles on the very same session, so the arena that
        // caused the OOM stayed resident for the whole descent (a peak-VRAM spike in the path meant to
        // reduce it); and since the caller could not see the descent, it halved its own split and made
        // the tiler re-descend the sizes it had just failed. A memory exception propagates as such;
        // the engine halves the split, releases the session, and calls back in.
        return AutoSplit(
            rgbBytes,
            width,
            height,
            scale,
            session,
            split,
            tilePad,
            cancellationToken
        );
    }

    /// <summary>
    /// <see cref="UpscaleRgb"/> for a maximum tile size, which is how a configured override is
    /// expressed. Anything derived from the budget should plan a <see cref="TileSplit"/> instead,
    /// because a single tile side cannot express grids that split the axes differently (a 4x3 grid
    /// is the cheapest for some pages and is reachable by no tile size at all).
    /// </summary>
    public static byte[] UpscaleRgb(
        byte[] rgbBytes,
        int width,
        int height,
        int scale,
        InferenceSession session,
        int tileSize = DefaultTileSize,
        int tilePad = DefaultTilePad,
        CancellationToken cancellationToken = default
    ) =>
        UpscaleRgb(
            rgbBytes,
            width,
            height,
            scale,
            session,
            TileSplit.For(width, height, tileSize),
            tilePad,
            cancellationToken
        );

    /// <summary>
    /// The same grid with the largest tile side halved, for the driver refusing an allocation.
    /// </summary>
    public static TileSplit HalveSplit(int width, int height, TileSplit split) =>
        TileSplit.For(width, height, Math.Max(split.TileWidth, split.TileHeight) / 2);

    /// <summary>
    /// Whether halving <paramref name="split"/> would still leave a tile at or above
    /// <paramref name="floor"/> on both sides — that is, whether the memory-pressure descent has an
    /// attempt left worth making.
    /// <para>
    /// The bound is tested on the tile the halving would actually produce, not on the size asked of it:
    /// <see cref="TileSplit.For"/> shrinks the tiles so they cover the page exactly, so halving a 225 px
    /// grid asks for 112 px tiles rather than 128. Testing the current sides instead would let the
    /// descent run a step below the floor on every axis the test does not look at.
    /// </para>
    /// </summary>
    public static bool CanHalveWithinFloor(int width, int height, TileSplit split, int floor)
    {
        TileSplit halved = HalveSplit(width, height, split);
        return halved.TileWidth >= floor && halved.TileHeight >= floor;
    }

    private static byte[] AutoSplit(
        byte[] rgbBytes,
        int width,
        int height,
        int scale,
        InferenceSession session,
        TileSplit split,
        int overlap,
        CancellationToken cancellationToken
    )
    {
        int tileCountX = split.Columns;
        int tileCountY = split.Rows;
        int tileSizeX = split.TileWidth;
        int tileSizeY = split.TileHeight;

        int outWidth = width * scale;
        int outHeight = height * scale;

        // One uniform tensor shape for every tile keeps ONNX Runtime (WebGPU / Dawn EP) from
        // re-allocating per tile and accumulating arena memory in Vulkan VRAM/GTT. It follows the
        // tiles this grid produces, so nothing is inferred that the page does not contain.
        (int uniformTargetW, int uniformTargetH) = UniformTileTensorShape(split, overlap);

        if (tileCountX <= 1 && tileCountY <= 1)
        {
            return UpscaleTile(
                rgbBytes,
                width,
                height,
                scale,
                session,
                cancellationToken,
                targetW: uniformTargetW,
                targetH: uniformTargetH
            );
        }

        Type elementType = RequireInputMetadata(session).ElementType;

        var tileJobs = new List<TileJob>(tileCountX * tileCountY);
        for (int y = 0; y < tileCountY; y++)
        {
            int tileY = y * tileSizeY;
            int tileH = Math.Min(tileSizeY, height - tileY);
            int padTop = Math.Min(tileY, overlap);
            int padBottom = Math.Min(height - (tileY + tileH), overlap);
            int paddedH = tileH + padTop + padBottom;

            for (int x = 0; x < tileCountX; x++)
            {
                int tileX = x * tileSizeX;
                int tileW = Math.Min(tileSizeX, width - tileX);
                int padLeft = Math.Min(tileX, overlap);
                int padRight = Math.Min(width - (tileX + tileW), overlap);
                int paddedW = tileW + padLeft + padRight;

                int paddedX = tileX - padLeft;
                int paddedY = tileY - padTop;

                tileJobs.Add(
                    new TileJob(
                        TileW: tileW,
                        TileH: tileH,
                        PadLeft: padLeft,
                        PadRight: padRight,
                        PadTop: padTop,
                        PadBottom: padBottom,
                        PaddedW: paddedW,
                        PaddedH: paddedH,
                        PaddedX: paddedX,
                        PaddedY: paddedY,
                        IsLastInRow: (x == tileCountX - 1)
                    )
                );
            }
        }

        var imageBlender = new TileBlender(outWidth, outHeight, 3, BlendDirection.Vertical);
        TileBlender? currentRowBlender = null;
        TileOverlap currentRowOverlap = default;

        // Pipelined tile execution:
        // 1. While GPU executes inference on Tile i, CPU decodes Tile i-1 on a background thread.
        // 2. CPU pre-fetches and constructs input tensor for Tile i+1 on a background thread.
        // This ensures the GPU remains 100% saturated without stalling for CPU post-processing/blending.
        Task<(NamedOnnxValue InVal, int PaddedW, int PaddedH)> nextInputTask = Task.Run(
            () =>
            {
                byte[] crop = ExtractCrop(
                    rgbBytes,
                    width,
                    tileJobs[0].PaddedX,
                    tileJobs[0].PaddedY,
                    tileJobs[0].PaddedW,
                    tileJobs[0].PaddedH
                );
                return PrepareInputTensor(
                    crop,
                    tileJobs[0].PaddedW,
                    tileJobs[0].PaddedH,
                    uniformTargetW,
                    uniformTargetH,
                    elementType,
                    cancellationToken
                );
            },
            cancellationToken
        );

        Task<byte[]>? prevDecodeTask = null;
        TileJob prevJob = default;

        // The whole pipeline — the loop and the final blend below — runs under one watch. An
        // exception anywhere in it leaves the previous tile's decode running with its hands on
        // that Run's native output collection, and the caller reacts to the exception immediately
        // (the engine's OOM path releases the cached session synchronously). Draining the decode
        // before rethrowing is what keeps the session alive until the decode has finished
        // reading it.
        try
        {
            for (int i = 0; i < tileJobs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentJob = tileJobs[i];

                var (inVal, effPaddedW, effPaddedH) = nextInputTask.GetAwaiter().GetResult();

                if (i + 1 < tileJobs.Count)
                {
                    var nextJob = tileJobs[i + 1];
                    nextInputTask = Task.Run(
                        () =>
                        {
                            byte[] nextCrop = ExtractCrop(
                                rgbBytes,
                                width,
                                nextJob.PaddedX,
                                nextJob.PaddedY,
                                nextJob.PaddedW,
                                nextJob.PaddedH
                            );
                            return PrepareInputTensor(
                                nextCrop,
                                nextJob.PaddedW,
                                nextJob.PaddedH,
                                uniformTargetW,
                                uniformTargetH,
                                elementType,
                                cancellationToken
                            );
                        },
                        cancellationToken
                    );
                }

                IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = RunOnSession(
                    session,
                    inVal
                );

                // Ownership of the native output collection is handed to whichever path gets there
                // first, decided by a single claim. The object is created before the block that hands
                // the collection to the decode task so the catch below can claim it too: a delegate
                // that ran claims it up front (and DecodeOutputTensor disposes the collection), the
                // continuation claims only when the task was cancelled before the delegate started,
                // and an exception before the handoff leaves this frame the owner through an object
                // nobody claimed. A claim — not ONNX Runtime's double-dispose guard — is what makes
                // the owner unambiguous.
                var outputOwnership = new OutputCollectionOwnership();

                // Until the decode task below takes it, the pipeline is the owner of `outputs` and has
                // to release it on every path out. Blending the previous tile can throw
                // NonFiniteModelOutputException — the exact bad-model case this pipeline recovers from —
                // and building the row blender can throw OutOfMemoryException; without this scope the
                // native output tensor of the current tile is leaked for the rest of the candidate.
                try
                {
                    if (prevDecodeTask != null)
                    {
                        byte[] prevBytes = prevDecodeTask.GetAwaiter().GetResult();
                        var prevOverlap = new TileOverlap(
                            prevJob.PadLeft * scale,
                            prevJob.PadRight * scale
                        );
                        currentRowBlender!.AddTile(
                            prevBytes,
                            prevJob.PaddedW * scale,
                            prevJob.PaddedH * scale,
                            prevOverlap
                        );

                        if (prevJob.IsLastInRow)
                        {
                            imageBlender.AddTile(
                                currentRowBlender.GetResult(),
                                outWidth,
                                prevJob.PaddedH * scale,
                                currentRowOverlap
                            );
                            currentRowBlender = null;
                        }
                    }

                    if (currentRowBlender == null)
                    {
                        currentRowBlender = new TileBlender(
                            outWidth,
                            currentJob.PaddedH * scale,
                            3,
                            BlendDirection.Horizontal
                        );
                        currentRowOverlap = new TileOverlap(
                            currentJob.PadTop * scale,
                            currentJob.PadBottom * scale
                        );
                    }

                    prevJob = currentJob;
                    // Ownership of the native output collection is handed to whichever path gets there
                    // first. Task.Run returns an already-cancelled task without running the delegate
                    // when the token is signalled before it starts, so the delegate is not the
                    // guaranteed owner; but the task is *also* cancelled when the delegate ran and
                    // threw OperationCanceledException (DecodeOutputTensor's own check), and the
                    // delegate disposes the collection itself in that case. A flag set by the delegate
                    // before it touches the collection is what tells the continuation which of the two
                    // happened, so exactly one path frees it. (ONNX Runtime's output collections guard
                    // Dispose on an _disposed flag, so a second dispose would be a no-op today, but
                    // the "task is Canceled" test alone cannot tell "never ran" from "ran and
                    // cancelled" — the ownership must not rest on that coincidence.)
                    prevDecodeTask = Task.Run(
                        () =>
                        {
                            // Claimed before anything else touches `outputs`; the continuation runs
                            // after this task completes, so a started delegate is always visible to
                            // it.
                            outputOwnership.TryClaim();
                            return DecodeOutputTensor(
                                outputs,
                                currentJob.PaddedW,
                                currentJob.PaddedH,
                                effPaddedW,
                                effPaddedH,
                                scale,
                                cancellationToken
                            );
                        },
                        cancellationToken
                    );

                    prevDecodeTask.ContinueWith(
                        completed =>
                        {
                            // Claim only when the delegate never ran (a task cancelled before it
                            // started): a delegate that did run already owns the collection through
                            // DecodeOutputTensor.
                            if (completed.IsCanceled && outputOwnership.TryClaim())
                            {
                                outputs.Dispose();
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default
                    );
                }
                catch
                {
                    // Claim before disposing for the same reason the continuation does: a decode
                    // delegate that already ran owns the collection (it disposes it inside
                    // DecodeOutputTensor), so an unconditional dispose here could be a second one.
                    if (outputOwnership.TryClaim())
                    {
                        outputs.Dispose();
                    }

                    ObserveAbandonedInputTask(nextInputTask);
                    throw;
                }
            }

            if (prevDecodeTask != null)
            {
                byte[] lastBytes = prevDecodeTask.GetAwaiter().GetResult();
                var lastOverlap = new TileOverlap(
                    prevJob.PadLeft * scale,
                    prevJob.PadRight * scale
                );
                currentRowBlender!.AddTile(
                    lastBytes,
                    prevJob.PaddedW * scale,
                    prevJob.PaddedH * scale,
                    lastOverlap
                );

                if (prevJob.IsLastInRow)
                {
                    imageBlender.AddTile(
                        currentRowBlender.GetResult(),
                        outWidth,
                        prevJob.PaddedH * scale,
                        currentRowOverlap
                    );
                }
            }
        }
        catch
        {
            // The primary exception is what propagates; what runs here is only what must not be
            // left running behind it. The input prefetch holds no native state, so observing it
            // is enough (see ObserveAbandonedInputTask). The previous tile's decode does hold
            // native state — the previous Run's output collection, which the session owns — so
            // it is waited out rather than only observed.
            ObserveAbandonedInputTask(nextInputTask);
            DrainAbandonedDecodeTask(prevDecodeTask);
            throw;
        }

        return imageBlender.GetResult();
    }

    /// <summary>
    /// Observes the input-prefetch task for the next tile when the pipeline unwinds early. It holds
    /// no native or otherwise disposable state — a <see cref="NamedOnnxValue"/> over a managed
    /// <c>DenseTensor</c> is not itself disposable — but it is never awaited on that path, so its
    /// fault would otherwise surface later as a task-scheduler unobserved exception.
    /// </summary>
    private static void ObserveAbandonedInputTask(
        Task<(NamedOnnxValue InVal, int PaddedW, int PaddedH)> task
    )
    {
        task.ContinueWith(
            completed =>
            {
                if (completed.Status == TaskStatus.Faulted)
                {
                    _ = completed.Exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    /// <summary>
    /// Waits out the still-running decode of the previous tile when the pipeline unwinds early. Its
    /// bytes are not blended — the primary exception is what propagates — but it must be waited for
    /// before that exception reaches the caller: the decode reads the previous Run's native output
    /// collection, and the engine reacts to an OOM from this pipeline by releasing the cached session
    /// synchronously, which would otherwise free that collection while the decode was still reading
    /// it. The decode is bounded managed work (a tensor-to-byte copy), so waiting is safe here. Its
    /// own fault is observed and dropped, because the primary exception is the one that explains the
    /// unwind; without the drop it would surface later as a task-scheduler unobserved exception.
    /// </summary>
    private static void DrainAbandonedDecodeTask(Task<byte[]>? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            _ = task.GetAwaiter().GetResult();
        }
        catch
        {
            // Observed and dropped: the primary exception is what the caller needs to see.
        }
    }

    /// <summary>
    /// Single-owner handoff for a native output collection in the pipelined tiling path. Three paths
    /// have a reason to release it — the decode delegate, the continuation that reacts to its
    /// cancellation, and the catch around the handoff — and exactly one of them may:
    /// <see cref="TryClaim"/> returns true for the first caller only. The delegate claims before it
    /// touches the collection; the continuation claims only when the task was cancelled before the
    /// delegate started; the catch claims only when the exception was thrown before the delegate
    /// started. ONNX Runtime's output collections guard <c>Dispose</c> on their own <c>_disposed</c>
    /// flag, so a second dispose is a no-op today, but that is the runtime's implementation detail
    /// and is not what ownership should rest on.
    /// </summary>
    private sealed class OutputCollectionOwnership
    {
        private int _claimed;

        /// <summary>True for the first caller; false for every later one.</summary>
        public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    }

    private readonly record struct TileJob(
        int TileW,
        int TileH,
        int PadLeft,
        int PadRight,
        int PadTop,
        int PadBottom,
        int PaddedW,
        int PaddedH,
        int PaddedX,
        int PaddedY,
        bool IsLastInRow
    );

    /// <summary>
    /// An element or byte count that does not fit in an int, reported as an out-of-memory condition.
    /// <para>
    /// The buffer sizes in this file are products of image dimensions, so they must be computed in
    /// long: a page whose RGB output exceeds 2 GiB (a long-strip webtoon at 4x, for instance) wrapped
    /// the int arithmetic. A negative product produced an <see cref="OverflowException"/>, which
    /// <see cref="IsMemoryException"/> does not recognise, so the halve-and-retry recovery never ran;
    /// a wrapped-but-positive one sized the array too small and then wrapped every row offset too,
    /// writing into the wrong region before failing. Reported as an
    /// <see cref="OutOfMemoryException"/>, an impossible size takes the same path as a driver that
    /// refuses the allocation.
    /// </para>
    /// </summary>
    internal static int CheckedCount(long count, string what)
    {
        if (count < 0 || count > int.MaxValue)
        {
            throw new OutOfMemoryException(
                $"{what} needs {count} bytes, which is more than one array can hold. Reduce the page "
                    + "size (Upscaler:MaxDimensionBeforeUpscaling) or the tile size."
            );
        }

        return (int)count;
    }

    /// <summary>
    /// True for the provider-level failures that say nothing about the model: an allocation that did
    /// not fit, and a device/driver fault (a lost device, a reset, an illegal memory access). They
    /// must not become a permanent "unusable model" verdict, and they are the shared substrate for
    /// every retryable classification in the codebase (the worker's
    /// <c>IsInfrastructureFailure</c>/<c>ClassifyStreamingFailure</c>, the detector's
    /// <c>IsRetryableDetectionFailure</c>, the engine's <c>IsTransientProbeFailure</c>), which is why
    /// the device-fault tokens live here despite the name: adding one here makes every classifier
    /// treat it as transient, which is the point — a working model was permanently disabled by a
    /// one-off "illegal memory access"/"device lost" because no classifier recognised it.
    /// </summary>
    public static bool IsMemoryException(Exception ex)
    {
        if (ex is OutOfMemoryException)
        {
            return true;
        }

        if (ex is OnnxRuntimeException onnxEx)
        {
            string msg = onnxEx.Message;
            if (
                msg.Contains("allocate", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("cudaMalloc", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("hipMalloc", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("VK_ERROR_OUT_OF", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("VK_ERROR_DEVICE_LOST", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("DEVICE_LOST", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("command submission", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("raw_hash_map", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("allocation failed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("DXGI_ERROR", StringComparison.OrdinalIgnoreCase)
                || msg.Contains(
                    "CL_MEM_OBJECT_ALLOCATION_FAILURE",
                    StringComparison.OrdinalIgnoreCase
                )
                // Device/driver faults: a lost or reset device, an illegal address from a kernel,
                // a suspended GPU. Each is produced by a reset/driver event rather than by the
                // graph, and a fresh session or a retry after the driver recovers runs the same
                // model fine.
                || msg.Contains("device lost", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("illegal memory access", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("cudaerrorillegaladdress", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("device_disconnected", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("device disconnected", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("context lost", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("context is lost", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("device suspended", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("has been suspended", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("driver reset", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("device reset", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("gpu reset", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("will not respond", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        if (ex.InnerException != null && IsMemoryException(ex.InnerException))
        {
            return true;
        }

        return false;
    }

    public static byte[] ExtractCrop(byte[] src, int srcWidth, int x, int y, int w, int h)
    {
        byte[] dst = new byte[CheckedCount((long)w * h * 3, "The tile crop")];
        int rowBytes = w * 3;
        for (int r = 0; r < h; r++)
        {
            int srcOff = ((y + r) * srcWidth + x) * 3;
            int dstOff = r * rowBytes;
            Buffer.BlockCopy(src, srcOff, dst, dstOff, rowBytes);
        }
        return dst;
    }

    /// <summary>
    /// Maps a padded index onto the source row/column by reflecting at the edges. A single reflection
    /// only works while the padding is shorter than the tile; the triangular wave here repeats it, so
    /// a padding longer than the tile still mirrors real pixels instead of degenerating into a run of
    /// the edge pixel. That degenerate case is reachable for a page (or a manually configured
    /// <c>TileSize</c>) smaller than the padding, where most of the tensor would otherwise be a
    /// constant edge value and the model would see no structure to work from.
    /// </summary>
    private static int Reflect(int index, int length)
    {
        if (length <= 1)
        {
            return 0;
        }

        int period = 2 * (length - 1);
        int p = index % period;
        return p < length ? p : period - p;
    }

    public static (NamedOnnxValue InVal, int PaddedW, int PaddedH) PrepareInputTensor(
        byte[] tileBytes,
        int tileW,
        int tileH,
        int targetW,
        int targetH,
        Type elementType,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Rejected here rather than left to ONNX Runtime: a quantized export (int8/uint8) would be
        // handed a float tensor, the runtime would refuse the binding, and the engine would report the
        // model as generically "unusable on this device" with nothing pointing at the dtype. This is
        // the first place the input element type is known, and only float32 and float16 are supported.
        if (elementType != typeof(float) && elementType != typeof(Float16))
        {
            throw new ModelContractMismatchException(
                $"The model's input tensor is {elementType.Name}; only float32 and float16 inputs are supported. "
                    + "A quantized (int8/uint8) export cannot be used — ship the model's fp16 or fp32 copy instead."
            );
        }

        int padH = (16 - (tileH % 16)) % 16;
        int padW = (16 - (tileW % 16)) % 16;
        int paddedW = Math.Max(targetW, tileW + padW);
        int paddedH = Math.Max(targetH, tileH + padH);

        byte[] paddedInput;
        if (paddedH == tileH && paddedW == tileW)
        {
            paddedInput = tileBytes;
        }
        else
        {
            paddedInput = new byte[
                CheckedCount((long)paddedW * paddedH * 3, "The padded tile input")
            ];
            Parallel.For(
                0,
                paddedH,
                py =>
                {
                    int srcY = Reflect(py, tileH);
                    int dstRowOff = py * paddedW * 3;
                    int srcRowOff = srcY * tileW * 3;
                    for (int px = 0; px < paddedW; px++)
                    {
                        int srcX = Reflect(px, tileW);
                        int srcIdx = srcRowOff + srcX * 3;
                        int dstIdx = dstRowOff + px * 3;
                        paddedInput[dstIdx] = tileBytes[srcIdx];
                        paddedInput[dstIdx + 1] = tileBytes[srcIdx + 1];
                        paddedInput[dstIdx + 2] = tileBytes[srcIdx + 2];
                    }
                }
            );
        }

        int planeSize = paddedW * paddedH;
        int rOff = 0;
        int gOff = planeSize;
        int bOff = planeSize * 2;

        cancellationToken.ThrowIfCancellationRequested();

        if (elementType == typeof(Float16))
        {
            Float16[] halfData = new Float16[CheckedCount(3L * planeSize, "The input tensor")];
            Parallel.For(
                0,
                paddedH,
                y =>
                {
                    int rowOff = y * paddedW;
                    int bRowOff = rowOff * 3;
                    for (int x = 0; x < paddedW; x++)
                    {
                        int pIdx = rowOff + x;
                        int bIdx = bRowOff + x * 3;
                        halfData[rOff + pIdx] = ByteToFloat16Lut[paddedInput[bIdx]];
                        halfData[gOff + pIdx] = ByteToFloat16Lut[paddedInput[bIdx + 1]];
                        halfData[bOff + pIdx] = ByteToFloat16Lut[paddedInput[bIdx + 2]];
                    }
                }
            );

            var halfTensor = new DenseTensor<Float16>(halfData, [1, 3, paddedH, paddedW]);
            var inVal = NamedOnnxValue.CreateFromTensor(ModelInputName, halfTensor);
            return (inVal, paddedW, paddedH);
        }
        else
        {
            float[] floatData = new float[CheckedCount(3L * planeSize, "The input tensor")];
            Parallel.For(
                0,
                paddedH,
                y =>
                {
                    int rowOff = y * paddedW;
                    int bRowOff = rowOff * 3;
                    for (int x = 0; x < paddedW; x++)
                    {
                        int pIdx = rowOff + x;
                        int bIdx = bRowOff + x * 3;
                        floatData[rOff + pIdx] = ByteToFloatLut[paddedInput[bIdx]];
                        floatData[gOff + pIdx] = ByteToFloatLut[paddedInput[bIdx + 1]];
                        floatData[bOff + pIdx] = ByteToFloatLut[paddedInput[bIdx + 2]];
                    }
                }
            );

            var floatTensor = new DenseTensor<float>(floatData, [1, 3, paddedH, paddedW]);
            var inVal = NamedOnnxValue.CreateFromTensor(ModelInputName, floatTensor);
            return (inVal, paddedW, paddedH);
        }
    }

    /// <summary>
    /// Converts one inference output into tightly cropped RGB bytes and rejects it when the model
    /// produced non-finite values.
    /// <para>
    /// The rejection is what keeps an unusable model visible: without it the fp16 table maps NaN
    /// to 0 and the page is written as silent black (see
    /// <see cref="NonFiniteModelOutputException"/>).
    /// </para>
    /// </summary>
    public static byte[] DecodeOutputTensor(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int tileW,
        int tileH,
        int paddedW,
        int paddedH,
        int scale,
        CancellationToken cancellationToken
    )
    {
        using (outputs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DisposableNamedOnnxValue outNamedValue = RequireOutputTensor(outputs);
            int outTileW = tileW * scale;
            int outTileH = tileH * scale;
            int outPaddedW = paddedW * scale;
            int outPaddedH = paddedH * scale;
            // Widths and heights are exact multiples of the scale factor, so the plane and the crop are
            // computed in long first: an 8000x8000 page run as one tile (TileSize = -1) is 32000 px on a
            // side, and three bytes each overflows an int. The array below would then be asked for a
            // negative length, which surfaces as an opaque OverflowException instead of the tile being
            // refused for what it is.
            long outPlaneLong = (long)outPaddedW * outPaddedH;
            long outCropLength = 3L * outTileW * outTileH;
            if (outPlaneLong * 3 > int.MaxValue || outCropLength > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"A {tileW}x{tileH} tile at {scale}x decodes to {outCropLength} bytes, which exceeds "
                        + "the maximum array length. Use a smaller TileSize, or leave it at 0 so the "
                        + "grid is derived from the memory budget instead of a single pass."
                );
            }

            int outPlane = (int)outPlaneLong;
            byte[] outCrop = new byte[(int)outCropLength];

            int outROff = 0;
            int outGOff = outPlane;
            int outBOff = outPlane * 2;

            // The offsets used to index the tensor are derived from the REQUESTED scale and assume
            // the output is NCHW with one image and three channels, while the tensor is whatever the
            // model actually produced. The check is in EnsureOutputLayout, next to the NCHW
            // assumptions it enforces; it runs after the non-finite scan, which already walks the
            // buffer safely and is the cheaper failure to report.
            void EnsureOutputGeometry(ReadOnlySpan<int> dimensions) =>
                EnsureOutputLayout(dimensions, outPaddedW, outPaddedH, paddedW, paddedH, scale);

            if (outNamedValue.Value is DenseTensor<Float16> halfOutTensor)
            {
                if (ContainsNonFinite(halfOutTensor.Buffer.Span))
                {
                    throw new NonFiniteModelOutputException();
                }

                EnsureOutputGeometry(halfOutTensor.Dimensions);

                Memory<Float16> halfMem = halfOutTensor.Buffer;
                Parallel.For(
                    0,
                    outTileH,
                    oy =>
                    {
                        ReadOnlySpan<ushort> halfBits = MemoryMarshal.Cast<Float16, ushort>(
                            halfMem.Span
                        );
                        int rowOffset = oy * outPaddedW;
                        int dstRowOffset = oy * outTileW * 3;
                        for (int ox = 0; ox < outTileW; ox++)
                        {
                            int pIdx = rowOffset + ox;
                            int dstIdx = dstRowOffset + ox * 3;

                            outCrop[dstIdx] = Float16ToByteLut[halfBits[outROff + pIdx]];
                            outCrop[dstIdx + 1] = Float16ToByteLut[halfBits[outGOff + pIdx]];
                            outCrop[dstIdx + 2] = Float16ToByteLut[halfBits[outBOff + pIdx]];
                        }
                    }
                );
            }
            else
            {
                var floatOutTensor = (DenseTensor<float>)outNamedValue.AsTensor<float>();
                if (ContainsNonFinite(floatOutTensor.Buffer.Span))
                {
                    throw new NonFiniteModelOutputException();
                }

                EnsureOutputGeometry(floatOutTensor.Dimensions);

                Memory<float> floatMem = floatOutTensor.Buffer;
                Parallel.For(
                    0,
                    outTileH,
                    oy =>
                    {
                        ReadOnlySpan<float> span = floatMem.Span;
                        int rowOffset = oy * outPaddedW;
                        int dstRowOffset = oy * outTileW * 3;
                        for (int ox = 0; ox < outTileW; ox++)
                        {
                            int pIdx = rowOffset + ox;
                            int dstIdx = dstRowOffset + ox * 3;

                            outCrop[dstIdx] = FloatToByte(span[outROff + pIdx]);
                            outCrop[dstIdx + 1] = FloatToByte(span[outGOff + pIdx]);
                            outCrop[dstIdx + 2] = FloatToByte(span[outBOff + pIdx]);
                        }
                    }
                );
            }

            return outCrop;
        }
    }

    /// <summary>
    /// Rejects a model output whose tensor layout is not the NCHW one <see cref="DecodeOutputTensor"/>
    /// indexes. Element count alone cannot tell a correctly shaped output from one that merely has as
    /// many elements: an NHWC tensor of the same size passes a count check, and the decode's plane
    /// offsets then read it as three interleaved planes — a silently wrong image rather than a
    /// failure. That matters because candidate selection deliberately appends every <c>.onnx</c> in
    /// the models directory as a last resort, so a foreign model is a real input here, not a
    /// hypothetical. A model whose real scale differs is the other direction: a lower one runs off the
    /// end of the buffer, a higher one stays in bounds and decodes the wrong-scale tensors at the
    /// requested size. Pinning the dimensions catches both, and every shipped and pinned upscaler
    /// produces <c>[1, 3, outPaddedH, outPaddedW]</c>: a rank-3 or NHWC output is not a layout this
    /// decoder handles.
    /// </summary>
    internal static void EnsureOutputLayout(
        ReadOnlySpan<int> dimensions,
        int outPaddedW,
        int outPaddedH,
        int paddedW,
        int paddedH,
        int scale
    )
    {
        if (
            dimensions.Length == 4
            && dimensions[0] == 1
            && dimensions[1] == 3
            && dimensions[2] == outPaddedH
            && dimensions[3] == outPaddedW
        )
        {
            return;
        }

        throw new ModelContractMismatchException(
            $"Model output has shape [{string.Join(", ", dimensions.ToArray())}] but a "
                + $"[1, 3, {outPaddedH}, {outPaddedW}] (NCHW) tensor was expected for a "
                + $"{paddedW}x{paddedH} tile at {scale}x. The loaded model does not satisfy the "
                + "upscaler contract — a different scale, or a different layout than the NCHW planes "
                + "this engine decodes. The models directory holds a model that does not match this "
                + "scale and layout."
        );
    }

    /// <summary>
    /// A 0-1 model output as a byte. The clamp happens in float space on purpose: casting a float
    /// outside the int range to int is undefined in C#, so a large finite overshoot — which the models
    /// produce legitimately at high-contrast edges — could come back as <c>int.MinValue</c> and clamp
    /// to 0, a black pixel where the value means "brighter than white". The fp16 path saturates
    /// through <see cref="Float16ToByteLut"/>; this is its float counterpart.
    /// </summary>
    private static byte FloatToByte(float value)
    {
        float scaled = value * 255.0f;
        return scaled <= 0f ? (byte)0
            : scaled >= 255f ? (byte)255
            : (byte)(scaled + 0.5f);
    }

    /// <summary>
    /// True when an inference output contains a value no byte can represent — a NaN or an
    /// infinity — which is what a model the execution provider cannot run reports. The WebGPU EP
    /// does this for the transformer architectures (<c>FDAT_M</c>, <c>FDAT_XL</c>, <c>DAT2</c>,
    /// <c>HAT_L</c>) instead of raising a missing-kernel error, while the identical model runs
    /// correctly on the CPU, on CUDA and on DirectML.
    /// <para>
    /// Values outside 0–1 are deliberately not counted: models legitimately overshoot at
    /// high-contrast edges, and that overshoot clamps to 0 or 255.
    /// </para>
    /// </summary>
    public static bool ContainsNonFinite(ReadOnlySpan<Float16> values)
    {
        ReadOnlySpan<ushort> bits = MemoryMarshal.Cast<Float16, ushort>(values);
        foreach (ushort bit in bits)
        {
            if (IsNonFiniteHalf(bit))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc cref="ContainsNonFinite(ReadOnlySpan{Float16})"/>
    public static bool ContainsNonFinite(ReadOnlySpan<float> values)
    {
        foreach (float value in values)
        {
            if (!float.IsFinite(value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The metadata of the model's input, or a clear failure when the model has no input named
    /// <c>input</c>.
    /// <para>
    /// A bare <c>InputMetadata["input"]</c> throws a <see cref="KeyNotFoundException"/> that says
    /// nothing about the model being unusable, so a caller cannot tell "skip this model" apart from a
    /// genuine tiler bug. Models in the wild do use other names, and candidate selection is expected
    /// to reject them here rather than crash with a dictionary lookup.
    /// </para>
    /// </summary>
    private static NodeMetadata RequireInputMetadata(InferenceSession session)
    {
        if (session.InputMetadata.TryGetValue(ModelInputName, out NodeMetadata? metadata))
        {
            return metadata;
        }

        throw new ModelContractMismatchException(
            $"Model has no input named '{ModelInputName}'; its inputs are "
                + $"{FormatNames(session.InputMetadata.Keys)}. The upscaler requires a model whose "
                + "input is named 'input'."
        );
    }

    /// <summary>
    /// The output tensor named <c>output</c>, or a clear failure naming what the model actually
    /// produced instead of the empty <see cref="InvalidOperationException"/> a bare
    /// <c>First</c> would raise.
    /// </summary>
    private static DisposableNamedOnnxValue RequireOutputTensor(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs
    )
    {
        DisposableNamedOnnxValue? named = outputs.FirstOrDefault(o => o.Name == ModelOutputName);
        if (named != null)
        {
            return named;
        }

        throw new ModelContractMismatchException(
            $"Model has no output named '{ModelOutputName}'; its outputs are "
                + $"{FormatNames(outputs.Select(o => o.Name))}. The upscaler requires a model whose "
                + "output is named 'output'."
        );
    }

    internal static string FormatNames(IEnumerable<string> names)
    {
        string[] quoted = names.Select(n => $"'{n}'").ToArray();
        return quoted.Length == 0 ? "(none)" : string.Join(", ", quoted);
    }

    public static byte[] UpscaleTile(
        byte[] tileBytes,
        int tileW,
        int tileH,
        int scale,
        InferenceSession session,
        CancellationToken cancellationToken,
        int targetW = 0,
        int targetH = 0
    )
    {
        Type elementType = RequireInputMetadata(session).ElementType;
        var (inVal, paddedW, paddedH) = PrepareInputTensor(
            tileBytes,
            tileW,
            tileH,
            targetW,
            targetH,
            elementType,
            cancellationToken
        );

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = RunOnSession(
            session,
            inVal
        );

        return DecodeOutputTensor(
            outputs,
            tileW,
            tileH,
            paddedW,
            paddedH,
            scale,
            cancellationToken
        );
    }

    /// <summary>
    /// The tiler's only <c>session.Run</c>, as a seam: tests make a run fail here to pin that the
    /// tiler hands the failure out to the engine instead of recovering internally. That is the
    /// contract the engine's memory descent is built on — it owns the recovery, because halving
    /// alone does not help while the session whose arena failed the allocation is still resident —
    /// and the internal halve loop this migration removed swallowed exactly this failure, hiding
    /// it from the engine. The override is <see cref="AsyncLocal{T}"/> so a test's injection is
    /// visible only to its own logical call chain: a plain static field leaked it into tests
    /// running concurrently in the same process. Production never sets it; the real call always
    /// runs.
    /// </summary>
    internal static AsyncLocal<
        Func<
            InferenceSession,
            NamedOnnxValue,
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue>
        >
    > RunImplOverride { get; } = new();

    private static readonly Func<
        InferenceSession,
        NamedOnnxValue,
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>
    > DefaultRun = static (session, input) => session.Run([input]);

    private static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunOnSession(
        InferenceSession session,
        NamedOnnxValue input
    ) => (RunImplOverride.Value ?? DefaultRun)(session, input);
}
