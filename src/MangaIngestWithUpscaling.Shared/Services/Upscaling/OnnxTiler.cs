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

    public static float[] GetBlendWeights(int blendSize)
    {
        if (blendSize <= 0)
        {
            return Array.Empty<float>();
        }

        if (blendSize == 1)
        {
            return [0.5f];
        }

        float[] blend = new float[blendSize];
        float denom = blendSize - 1;
        for (int i = 0; i < blendSize; i++)
        {
            float t = i / denom;
            blend[i] = HalfSinBlend(t);
        }

        return blend;
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
    /// can therefore land below the floor, and the engine halves it again when the driver refuses
    /// the allocation.
    /// </para>
    /// </summary>
    public const int MinimumTileSize = 256;

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
    private static (double ActivationScale, long SessionReservationBytes) ResolveProfileFactors(
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

        double measuredScale = profile.ScaleFor(modelNameOrPath, modelSizeBytes);
        if (measuredScale is > 0 and < 12)
        {
            activationScale = measuredScale;
        }

        long measuredReservation = profile.ReservationFor(modelNameOrPath, modelSizeBytes);
        if (measuredReservation is > 0 and < 8L * 1024 * 1024 * 1024)
        {
            sessionReservation = measuredReservation;
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
            // halves the tiles again if the driver then refuses the allocation.
            return TileSplit.For(width, height, MinimumTileSize);
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
        // budget it was rejected for. An emergency grid is returned instead, its tiles finer than the
        // floor, which is what the engine would have halved down to after the driver refused the
        // allocation anyway.
        if (best.Columns == 1 && best.Rows == 1)
        {
            best = new TileSplit(2, 2, Math.Max(1, (width + 1) / 2), Math.Max(1, (height + 1) / 2));
        }

        return best;
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

        var files = new List<DrmCardFiles>();
        try
        {
            if (!Directory.Exists("/sys/class/drm"))
            {
                return files;
            }

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

                // Intel Xe / i915 sysfs paths. Either family can be present; the first one that
                // reports a total is the one this card uses.
                string intelTile0Total = Path.Combine(deviceDir, "tile0", "memory0", "total_bytes");
                string intelTile0Alloc = Path.Combine(deviceDir, "tile0", "memory0", "alloc_bytes");
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

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1000);
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

            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run([inVal]);

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
                prevDecodeTask = Task.Run(
                    () =>
                        DecodeOutputTensor(
                            outputs,
                            currentJob.PaddedW,
                            currentJob.PaddedH,
                            effPaddedW,
                            effPaddedH,
                            scale,
                            cancellationToken
                        ),
                    cancellationToken
                );

                // Task.Run returns an already-cancelled task without running the delegate when the
                // token is signalled first, so ownership of `outputs` never reaches
                // DecodeOutputTensor. Release it in that case; when the delegate does run it
                // disposes the collection itself (see DecodeOutputTensor).
                prevDecodeTask.ContinueWith(
                    completed =>
                    {
                        if (completed.IsCanceled)
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
                outputs.Dispose();
                ObserveAbandonedInputTask(nextInputTask);
                throw;
            }
        }

        if (prevDecodeTask != null)
        {
            byte[] lastBytes = prevDecodeTask.GetAwaiter().GetResult();
            var lastOverlap = new TileOverlap(prevJob.PadLeft * scale, prevJob.PadRight * scale);
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

            // The offsets used to index the tensor are derived from the REQUESTED scale, while the
            // tensor is whatever the model actually produced. A model whose real scale differs is only
            // reachable from the last-resort candidate selection in a mis-provisioned models
            // directory, where any model at all can be picked for a page of another scale. Such a
            // model indexes with the wrong stride: a lower real scale runs off the end of the buffer,
            // and a higher one stays in bounds, decodes finite values and passes every other check —
            // so the page is silently assembled from the wrong-scale tensors at the requested size.
            // Element count catches both, because what follows assumes exactly three planes of
            // outPaddedW x outPaddedH. It runs after the non-finite scan, which already walks the
            // buffer safely and is the cheaper failure to report.
            void EnsureOutputGeometry(long actualElements)
            {
                if (actualElements == 3L * outPaddedW * outPaddedH)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Model output is {actualElements} elements but {3L * outPaddedW * outPaddedH} "
                        + $"were expected for a {paddedW}x{paddedH} tile at {scale}x. The loaded model "
                        + "is at a different scale than the requested one; the models directory is "
                        + "incomplete or inconsistent for this scale."
                );
            }

            if (outNamedValue.Value is DenseTensor<Float16> halfOutTensor)
            {
                if (ContainsNonFinite(halfOutTensor.Buffer.Span))
                {
                    throw new NonFiniteModelOutputException();
                }

                EnsureOutputGeometry(halfOutTensor.Length);

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

                EnsureOutputGeometry(floatOutTensor.Length);

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

                            float r = span[outROff + pIdx] * 255.0f;
                            float g = span[outGOff + pIdx] * 255.0f;
                            float b = span[outBOff + pIdx] * 255.0f;

                            outCrop[dstIdx] = (byte)Math.Clamp((int)(r + 0.5f), 0, 255);
                            outCrop[dstIdx + 1] = (byte)Math.Clamp((int)(g + 0.5f), 0, 255);
                            outCrop[dstIdx + 2] = (byte)Math.Clamp((int)(b + 0.5f), 0, 255);
                        }
                    }
                );
            }

            return outCrop;
        }
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

        throw new InvalidOperationException(
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

        throw new InvalidOperationException(
            $"Model has no output named '{ModelOutputName}'; its outputs are "
                + $"{FormatNames(outputs.Select(o => o.Name))}. The upscaler requires a model whose "
                + "output is named 'output'."
        );
    }

    private static string FormatNames(IEnumerable<string> names)
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

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run([inVal]);

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
}
