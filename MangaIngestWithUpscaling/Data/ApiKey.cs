using System.ComponentModel.DataAnnotations.Schema;

namespace MangaIngestWithUpscaling.Data;

public class ApiKey
{
    public int Id { get; set; }
    public required string Key { get; set; }
    public required string UserId { get; set; }
    public DateTime? Expiration { get; set; }
    public bool IsActive { get; set; }

    [ForeignKey(nameof(UserId))]
    public virtual ApplicationUser User { get; set; } = null!;
}