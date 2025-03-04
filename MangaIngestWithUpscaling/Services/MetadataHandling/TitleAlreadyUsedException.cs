namespace MangaIngestWithUpscaling.Services.MetadataHandling;

[Serializable]
public class TitleAlreadyUsedException : Exception
{
    public TitleAlreadyUsedException() { }
    public TitleAlreadyUsedException(string message) : base(message) { }
    public TitleAlreadyUsedException(string message, Exception inner) : base(message, inner) { }
    protected TitleAlreadyUsedException(
      System.Runtime.Serialization.SerializationInfo info,
      System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
}