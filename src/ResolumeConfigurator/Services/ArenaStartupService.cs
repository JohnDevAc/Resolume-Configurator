using System.Diagnostics;
using System.Net.Http;
using Microsoft.Win32;

namespace ResolumeConfigurator.Services;

public sealed class ArenaStartupService
{
    public async Task<ArenaStartupResult> EnsureRunningAsync(CancellationToken ct)
    {
        var running = Process.GetProcessesByName("Arena");
        try
        {
            if (running.Length > 0) return new ArenaStartupResult(false, null);
        }
        finally
        {
            foreach (var runningProcess in running) runningProcess.Dispose();
        }

        var executable = ResolveExecutable()
            ?? throw new FileNotFoundException("Resolume Arena is not running and Arena.exe could not be found in its installed location.");
        var launchedProcess = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Windows did not start Resolume Arena.");
        launchedProcess.Dispose();

        using var api = new ResolumeApiClient(timeout: TimeSpan.FromMilliseconds(750));
        var ready = await WaitForWebserverAsync(async token =>
        {
            try { return (await api.GetProductAsync(token).ConfigureAwait(false)).Name.Equals("Arena", StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException) { return false; }
        }, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        return new ArenaStartupResult(true, executable, ready);
    }

    internal static async Task<bool> WaitForWebserverAsync(Func<CancellationToken, Task<bool>> probe,
        TimeSpan timeout, TimeSpan interval, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                if (await probe(deadline.Token).ConfigureAwait(false)) return true;
                await Task.Delay(interval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private static string? ResolveExecutable()
    {
        var candidates = new List<string>();
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(root, "Resolume Arena", "Arena.exe"));
            try
            {
                candidates.AddRange(Directory.EnumerateDirectories(root, "Resolume Arena*")
                    .Select(directory => Path.Combine(directory, "Arena.exe")));
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Arena.exe");
                    if (key?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value)) candidates.Add(value.Trim('"'));
                }
                catch (UnauthorizedAccessException) { }
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}

public sealed record ArenaStartupResult(bool Launched, string? ExecutablePath, bool WebserverReady = true);
