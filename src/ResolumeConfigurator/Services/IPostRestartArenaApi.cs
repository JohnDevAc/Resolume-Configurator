using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public interface IPostRestartArenaApi : IDisposable
{
    Func<CancellationToken, Task>? BeforeMutation { get; set; }
    Task<ArenaCompositionState> GetCompositionStateAsync(CancellationToken ct);
    Task ConfigureClipFitAsync(long clipId, CancellationToken ct);
    Task ConfigureVideoRouterFitAsync(long clipId, CancellationToken ct);
    Task ConnectClipAsync(long clipId, CancellationToken ct);
    Task UpdateClipThumbnailAsync(long clipId, CancellationToken ct);
    Task SaveCompositionAsync(string path, CancellationToken ct);
}
