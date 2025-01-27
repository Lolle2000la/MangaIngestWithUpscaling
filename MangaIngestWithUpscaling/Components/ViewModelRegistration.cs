using MangaIngestWithUpscaling.Components.FileSystem;

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
