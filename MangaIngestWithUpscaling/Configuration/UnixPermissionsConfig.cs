namespace MangaIngestWithUpscaling.Configuration;

public record UnixPermissionsConfig
{
    public const string Position = "UnixPermissions";

    public uint? UserId { get; set; }
    public uint? GroupId { get; set; }
}
