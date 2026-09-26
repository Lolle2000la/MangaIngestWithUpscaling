using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MangaIngestWithUpscaling.Services.BackgroundTaskQueue.Tasks;

/// <summary>
/// Observable progress information for a running task. This intentionally only implements
/// <see cref="INotifyPropertyChanged"/> instead of deriving from a UI framework type so that it can
/// live in the data layer without pulling in a UI dependency.
/// </summary>
public class ProgressInfo : INotifyPropertyChanged
{
    private int _current;
    private int _total;
    private string? _phase;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsIndeterminate => Total == 0;

    public int Current
    {
        get => _current;
        set => SetField(ref _current, value);
    }

    public int Total
    {
        get => _total;
        set => SetField(ref _total, value);
    }

    public string? Phase
    {
        get => _phase;
        set => SetField(ref _phase, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
