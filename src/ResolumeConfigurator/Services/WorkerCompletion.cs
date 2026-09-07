using System.Text.Json;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

internal sealed record RestartRequest(string OperationId, string JobName, string ConfiguratorUrl, JobIdentity ExpectedJob,
    LocalNdiReadinessService.LocalAgentIdentity Agent, PostRestartComposition Composition);
internal sealed record WorkerOutcome(string OperationId, bool Success, string? Error, IReadOnlyList<DecoderPresetResult> Decoders);

internal static class WorkerCompletion
{
    internal static readonly TimeSpan WorkerTimeout = TimeSpan.FromMinutes(15);

    internal static async Task<IReadOnlyList<DecoderPresetResult>> WaitAsync(string resultFile, string operationId,
        Func<CancellationToken, Task<int>> waitForExit, Action stop, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        int exitCode;
        try { exitCode = await waitForExit(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            stop();
            await waitForExit(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("The Arena helper exceeded its operation deadline and was stopped. Review the recovery record before retrying.");
        }
        if (!File.Exists(resultFile))
            throw new InvalidOperationException($"The Arena helper exited ({exitCode}) without a result. Review the recovery record before retrying.");
        var result = JsonSerializer.Deserialize<WorkerOutcome>(await File.ReadAllTextAsync(resultFile, ct).ConfigureAwait(false));
        if (result is null || result.OperationId != operationId)
            throw new InvalidDataException("The Arena helper result does not match this configuration operation.");
        if (!result.Success || exitCode != 0)
            throw new InvalidOperationException(result.Error ?? $"The Arena helper failed with exit code {exitCode}.");
        return result.Decoders;
    }
}
