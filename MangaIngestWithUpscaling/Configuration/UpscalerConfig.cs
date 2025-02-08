
public record UpscalerConfig
{
    public const string Position = "Upscaler";

    public bool UseFp16 { get; set; } = true;
    public bool UseCPU { get; set; } = false;
    public int SelectedDeviceIndex = 0;
}