namespace MangaIngestWithUpscaling.Components.FileSystem
{
    public class DirectoryItem
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public bool HasChildren { get; set; }
        public bool IsExpanded { get; set; }
        public bool ChildrenLoading { get; set; }
        public List<DirectoryItem> Children { get; set; } = new();
    }
}
