using ReactiveUI;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

public class ProgressInfo : ReactiveObject
{
    private int _current;

    private int _total;
    public bool IsIndeterminate => Total == 0;

    public int Current
    {
        get => _current;
        set => this.RaiseAndSetIfChanged(ref _current, value);
    }

    public int Total
    {
        get => _total;
        set => this.RaiseAndSetIfChanged(ref _total, value);
    }
}
