using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MangaIngestWithUpscaling.Services.BackqroundTaskQueue;
using Microsoft.AspNetCore.Authorization;

namespace MangaIngestWithUpscaling.Api.Upscaling;

[Authorize]
public partial class UpscalingDistributionService(
    TaskQueue taskQueue) : UpscalingService.UpscalingServiceBase
{
    public override async Task GetCbzFile(CbzToUpscaleRequest request, IServerStreamWriter<CbzFileChunk> responseStream, ServerCallContext context)
    {
        throw new NotImplementedException();
    }

    public override async Task<KeepAliveResponse> KeepAlive(KeepAliveRequest request, ServerCallContext context)
    {
        throw new NotImplementedException();
    }

    public override async Task<CbzFileChunk> RequestCbzFileChunk(CbzFileChunkRequest request, ServerCallContext context)
    {
        throw new NotImplementedException();
    }

    public override async Task<UpscaleTaskDelegationResponse> RequestUpscaleTask(Empty request, ServerCallContext context)
    {
        throw new NotImplementedException();
    }

    public override async Task<Empty> UploadUpscaledCbzFile(IAsyncStreamReader<CbzFileChunk> requestStream, ServerCallContext context)
    {
        throw new NotImplementedException();
    }
}
