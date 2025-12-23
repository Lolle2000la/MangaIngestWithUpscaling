namespace MangaIngestWithUpscaling.Services.MetadataHandling;

public class TitleAlreadyUsedException : Exception
{
    public TitleAlreadyUsedException() { }

    public TitleAlreadyUsedException(string message)
        : base(message) { }

    public TitleAlreadyUsedException(string message, Exception inner)
        : base(message, inner) { }
}
