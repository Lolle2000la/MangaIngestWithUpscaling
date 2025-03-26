using MangaIngestWithUpscaling.Api.Upscaling;

namespace MangaIngestWithUpscaling.Api;

public static class ApiMappings
{
    public static void MapApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/grpc")
            .MapGrpcService<UpscalingDistributionService>();
    }
}
