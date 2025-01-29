using ReactiveUI;

namespace MangaIngestWithUpscaling.Components;

public abstract class ViewModelBase : ReactiveObject, IActivatableViewModel
{
    public ViewModelActivator Activator { get; } = new ViewModelActivator();
}
