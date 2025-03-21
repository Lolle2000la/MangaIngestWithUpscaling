using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue.Tasks;
using Microsoft.AspNetCore.Authorization;

namespace MangaIngestWithUpscaling.Api.Upscaling;

[Authorize]
public partial class UpscalingDistributionService(
    TaskQueue taskQueue,
    DistributedUpscaleTaskProcessor taskProcessor,
    ApplicationDbContext dbContext) : UpscalingService.UpscalingServiceBase
{
    public override async Task<UpscaleTaskDelegationResponse> RequestUpscaleTask(Empty request, ServerCallContext context)
    {
        var task = await taskProcessor.GetTask(context.CancellationToken);
        if (task == null)
        {
            context.Status = new Status(StatusCode.NotFound, "No tasks available");
            return new UpscaleTaskDelegationResponse()
            {
                TaskId = -1,
                UpscalerProfile = null
            };
        }
        else
        {
            var upscaleTask = (UpscaleTask)task.Data;
            var upscalerProfile = await dbContext.UpscalerProfiles.FindAsync(upscaleTask.UpscalerProfileId);

            if (upscalerProfile == null) {
                context.Status = new Status(StatusCode.NotFound, "Upscaler profile not found");
                return new UpscaleTaskDelegationResponse()
                {
                    TaskId = -1,
                    UpscalerProfile = null
                };
            }

            return new UpscaleTaskDelegationResponse()
            {
                TaskId = task.Id,
                UpscalerProfile = new UpscalerProfile()
                {
                    Name = upscalerProfile.Name,
                    UpscalerMethod = upscalerProfile.UpscalerMethod switch
                    {
                        Data.LibraryManagement.UpscalerMethod.MangaJaNai => UpscalerMethod.MangaJaNai,
                        _ => UpscalerMethod.Unspecified
                    },
                    CompressionFormat = upscalerProfile.CompressionFormat switch
                    {
                        Data.LibraryManagement.CompressionFormat.Avif => CompressionFormat.Avif,
                        Data.LibraryManagement.CompressionFormat.Jpg => CompressionFormat.Jpg,
                        Data.LibraryManagement.CompressionFormat.Png => CompressionFormat.Png,
                        Data.LibraryManagement.CompressionFormat.Webp => CompressionFormat.Webp,
                        _ => CompressionFormat.Unspecified
                    },
                    Quality = upscalerProfile.Quality,
                    ScalingFactor = upscalerProfile.ScalingFactor switch
                    {
                        Data.LibraryManagement.ScaleFactor.OneX => ScaleFactor.OneX,
                        Data.LibraryManagement.ScaleFactor.TwoX => ScaleFactor.TwoX,
                        Data.LibraryManagement.ScaleFactor.ThreeX => ScaleFactor.ThreeX,
                        Data.LibraryManagement.ScaleFactor.FourX => ScaleFactor.FourX,
                        _ => ScaleFactor.Unspecified
                    }
                },
            };
        }
    }

    public override async Task<KeepAliveResponse> KeepAlive(KeepAliveRequest request, ServerCallContext context)
    {
        if (taskProcessor.KeepAlive(request.TaskId))
        {
            return new KeepAliveResponse { IsAlive = true };
        }
        else
        {
            context.Status = new Status(StatusCode.NotFound, "Task not found");
            return new KeepAliveResponse { IsAlive = false };
        }
    }

    public override async Task GetCbzFile(CbzToUpscaleRequest request, IServerStreamWriter<CbzFileChunk> responseStream, ServerCallContext context)
    {
        throw new NotImplementedException();
    }

    public override async Task<CbzFileChunk> RequestCbzFileChunk(CbzFileChunkRequest request, ServerCallContext context)
    {
        throw new NotImplementedException();
    }

    public override async Task<Empty> UploadUpscaledCbzFile(IAsyncStreamReader<CbzFileChunk> requestStream, ServerCallContext context)
    {
        throw new NotImplementedException();
    }
}
