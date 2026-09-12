using System.Diagnostics;

namespace ResolumeConfigurator.Services;

internal static class ConfigurationOwnership
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Resolume Arena Configurator");

    internal static IDisposable AcquireParent(string? directory = null)
    {
        var parent = Acquire(directory ?? Root, "configuration.lock");
        try
        {
            using var worker = Acquire(directory ?? Root, "worker.lock");
            return parent;
        }
        catch { parent.Dispose(); throw; }
    }

    internal static IDisposable AcquireWorker(string? directory = null) => Acquire(directory ?? Root, "worker.lock");

    private static FileStream Acquire(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("Another configuration or restart helper is active. Wait for it to finish before retrying.", ex); }
    }
}

internal sealed class ParentLifetime : IAsyncDisposable
{
    private readonly Process _parent;
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _watch;
    internal CancellationToken Token => _lifetime.Token;

    internal ParentLifetime(int processId, long startUtcTicks, CancellationToken ct)
    {
        if (processId <= 0 || startUtcTicks <= 0) throw new InvalidDataException("The helper request is missing its parent identity.");
        _parent = Process.GetProcessById(processId);
        try
        {
            if (_parent.HasExited || _parent.StartTime.ToUniversalTime().Ticks != startUtcTicks)
                throw new InvalidOperationException("The configuration parent has exited or changed. The helper cannot continue.");
        }
        catch { _parent.Dispose(); throw; }
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _watch = WatchAsync();
    }

    private async Task WatchAsync()
    {
        try
        {
            await _parent.WaitForExitAsync(Token).ConfigureAwait(false);
            _lifetime.Cancel();
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
    }

    internal void Validate()
    {
        Token.ThrowIfCancellationRequested();
        if (_parent.HasExited) throw new InvalidOperationException("The configuration parent exited. The helper stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _watch.ConfigureAwait(false);
        _lifetime.Dispose();
        _parent.Dispose();
    }
}
