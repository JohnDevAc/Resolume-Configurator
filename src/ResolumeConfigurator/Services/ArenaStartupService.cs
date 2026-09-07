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
            if (running.Length > 1) throw new InvalidOperationException("Close surplus Arena processes before configuring. Exactly one local Arena instance is supported.");
            if (running.Length == 1)
            {
                var configured = ArenaPaths.ResolveOverride("RESOLUME_ARENA_EXE");
                if (configured is not null && !string.Equals(running[0].MainModule?.FileName, configured, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The running Arena does not match RESOLUME_ARENA_EXE. Start the configured installation before continuing.");
                return new ArenaStartupResult(false, running[0].MainModule?.FileName);
            }
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
        var configured = ArenaPaths.ResolveOverride("RESOLUME_ARENA_EXE");
        if (configured is not null)
        {
            if (!File.Exists(configured) || !Path.GetFileName(configured).Equals("Arena.exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("RESOLUME_ARENA_EXE must point to an existing Arena.exe.", configured);
            return configured;
        }
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

        var found = candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (found.Length > 1) throw new InvalidOperationException("Several Arena installations were found. Set RESOLUME_ARENA_EXE to the Arena.exe to use.");
        return found.FirstOrDefault();
    }
}

public sealed record ArenaStartupResult(bool Launched, string? ExecutablePath, bool WebserverReady = true);
