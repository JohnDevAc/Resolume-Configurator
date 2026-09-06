using System.Diagnostics;

namespace ResolumeConfigurator.Services;

public sealed class ArenaRestartService
{
    public async Task RestartAsync(string compositionFile, string expectedJobName, IProgress<string>? progress, CancellationToken ct,
        int sourceStartColumn, int sourceCount)
    {
        if (!File.Exists(compositionFile)) throw new FileNotFoundException("The saved job composition was not found before restart.", compositionFile);
        var companionPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the companion executable path for post-restart activation.");
        await RestartArenaAsync(compositionFile, ct).ConfigureAwait(false);
        progress?.Report("Restarted Arena after saving the composition and active Advanced Output XML.");
        using var worker = Process.Start(CreateWorkerStartInfo(companionPath, expectedJobName, Environment.ProcessId,
            compositionFile, sourceStartColumn, sourceCount))
            ?? throw new InvalidOperationException("Windows did not start the post-restart activation worker.");
        progress?.Report("Launched Arena and handed decoder activation to a fresh helper process.");
    }

    internal async Task RestartArenaAsync(string compositionFile, CancellationToken ct)
    {
        if (!File.Exists(compositionFile)) throw new FileNotFoundException("The saved composition was not found before restart.", compositionFile);
        var arenaProcesses = Process.GetProcessesByName("Arena");
        if (arenaProcesses.Length != 1)
        {
            foreach (var process in arenaProcesses) process.Dispose();
            throw new InvalidOperationException($"Expected one running Resolume Arena process before restart; found {arenaProcesses.Length}.");
        }

        string executablePath;
        using (var arena = arenaProcesses[0])
        {
            executablePath = arena.MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine the running Arena executable path.");

            // Arena shows an interactive quit dialog for a normal close and does
            // not hot-reload AdvancedOutput.xml. All composition work is saved and
            // backed up before this point, so stop the process before it can write
            // its stale in-memory Advanced Output state back over the new XML.
            arena.Kill(entireProcessTree: true);
            await arena.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        await Task.Delay(1200, ct).ConfigureAwait(false);

        using var restartedArena = Process.Start(CreateArenaStartInfo(executablePath, compositionFile))
            ?? throw new InvalidOperationException("Windows did not start Resolume Arena.");
    }

    internal static ProcessStartInfo CreateArenaStartInfo(string executablePath, string compositionFile)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(compositionFile);
        return startInfo;
    }

    internal static ProcessStartInfo CreateWorkerStartInfo(string companionPath, string expectedJobName, int parentProcessId,
        string compositionFile, int sourceStartColumn, int sourceCount)
    {
        var workerStartInfo = new ProcessStartInfo
        {
            FileName = companionPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        workerStartInfo.ArgumentList.Add("--post-restart");
        workerStartInfo.ArgumentList.Add(expectedJobName);
        workerStartInfo.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        workerStartInfo.ArgumentList.Add(compositionFile);
        workerStartInfo.ArgumentList.Add(sourceStartColumn.ToString(System.Globalization.CultureInfo.InvariantCulture));
        workerStartInfo.ArgumentList.Add(sourceCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return workerStartInfo;
    }
}
