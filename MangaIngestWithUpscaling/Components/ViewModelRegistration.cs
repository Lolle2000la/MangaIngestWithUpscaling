using MangaIngestWithUpscaling.Components.FileSystem;
using MangaIngestWithUpscaling.Components.BackgroundTaskQueue;

namespace MangaIngestWithUpscaling.Components
{
    public static class ViewModelRegistration
    {
        public static void RegisterViewModels(this IServiceCollection services)
        {
            services.AddTransient<FolderPickerViewModel>();
        }
    }
}
