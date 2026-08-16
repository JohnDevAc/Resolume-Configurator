using System.Diagnostics;

namespace ResolumeConfigurator.Services;

public sealed class ArenaRestartService
{
    public async Task RestartAsync(string compositionFile, string expectedCompositionName, IProgress<string>? progress, CancellationToken ct)
    {
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
        progress?.Report("Stopped Arena after saving the composition and active Advanced Output XML.");
        await Task.Delay(1200, ct).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            UseShellExecute = true
        };
        var restartedArena = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start Resolume Arena.");
        restartedArena.Dispose();

        var companionPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the companion executable path for post-restart activation.");
        var workerStartInfo = new ProcessStartInfo
        {
            FileName = companionPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        workerStartInfo.ArgumentList.Add("--post-restart");
        workerStartInfo.ArgumentList.Add(expectedCompositionName);
        var worker = Process.Start(workerStartInfo)
            ?? throw new InvalidOperationException("Windows did not start the post-restart activation worker.");
        worker.Dispose();
        progress?.Report("Launched Arena and handed decoder activation to a fresh helper process.");
    }
}
