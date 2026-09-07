using System.Diagnostics;
using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public sealed class ArenaRestartService
{
    public async Task<IReadOnlyList<DecoderPresetResult>> RestartAsync(string compositionFile, string expectedJobName, IProgress<string>? progress, CancellationToken ct,
        int sourceStartColumn, int sourceCount, string? configuratorUrl, JobIdentity? expectedJob,
        string operationDirectory, string operationId, LocalNdiReadinessService.LocalAgentIdentity expectedAgent)
    {
        if (!File.Exists(compositionFile)) throw new FileNotFoundException("The saved job composition was not found before restart.", compositionFile);
        var companionPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the companion executable path for post-restart activation.");
        ValidateCompanionLaunch();
        var requestFile = Path.Combine(operationDirectory, "worker-request.json");
        var resultFile = Path.Combine(operationDirectory, "worker-result.json");
        await AtomicFile.WriteJsonAsync(requestFile, new RestartRequest(operationId, expectedJobName,
            configuratorUrl ?? throw new InvalidOperationException("A selected configurator is required."),
            expectedJob ?? throw new InvalidOperationException("A job identity is required."), expectedAgent,
            new(compositionFile, sourceStartColumn, sourceCount)), ct);
        await RestartArenaAsync(compositionFile, ct).ConfigureAwait(false);
        progress?.Report("Restarted Arena after saving the composition and active Advanced Output XML.");
        var start = CreateCompanionStartInfo(companionPath);
        start.ArgumentList.Add("--post-restart-request");
        start.ArgumentList.Add(requestFile);
        using var worker = Process.Start(start)
            ?? throw new InvalidOperationException("Windows did not start the post-restart activation worker.");
        progress?.Report("Launched Arena; monitoring the fresh helper until its result is verified.");
        return await WorkerCompletion.WaitAsync(resultFile, operationId, async token =>
        {
            await worker.WaitForExitAsync(token).ConfigureAwait(false);
            return worker.ExitCode;
        }, () => { if (!worker.HasExited) worker.Kill(entireProcessTree: true); },
            WorkerCompletion.WorkerTimeout + TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
    }

    internal static void ValidateCompanionLaunch()
    {
        var path = Environment.ProcessPath;
        if (path is null || !File.Exists(path)) throw new FileNotFoundException("The configurator executable is unavailable for restart recovery.");
        var start = CreateCompanionStartInfo(path);
        if (start.ArgumentList.Count > 0 && !File.Exists(start.ArgumentList[0]))
            throw new FileNotFoundException("The configurator assembly is unavailable for restart recovery.");
    }

    internal static void ValidateRunningArena()
    {
        var processes = Process.GetProcessesByName("Arena");
        try
        {
            if (processes.Length != 1) throw new InvalidOperationException($"Exactly one local Arena process is required; found {processes.Length}.");
            var configured = ArenaPaths.ResolveOverride("RESOLUME_ARENA_EXE");
            if (configured is not null && !string.Equals(configured, processes[0].MainModule?.FileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The running Arena does not match RESOLUME_ARENA_EXE.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static ProcessStartInfo CreateCompanionStartInfo(string companionPath, string? managedAssembly = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = companionPath, WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        if (Path.GetFileNameWithoutExtension(companionPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = managedAssembly ?? Path.Combine(AppContext.BaseDirectory, typeof(ArenaRestartService).Assembly.GetName().Name + ".dll");
            if (string.IsNullOrWhiteSpace(assembly)) throw new InvalidOperationException("Could not resolve the managed configurator assembly for the dotnet host.");
            start.ArgumentList.Add(assembly);
        }
        return start;
    }

    internal async Task RestartArenaAsync(string compositionFile, CancellationToken ct)
    {
        ValidateRunningArena();
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
        string compositionFile, int sourceStartColumn, int sourceCount, string? configuratorUrl = null, JobIdentity? expectedJob = null)
    {
        var workerStartInfo = CreateCompanionStartInfo(companionPath);
        workerStartInfo.ArgumentList.Add("--post-restart");
        workerStartInfo.ArgumentList.Add(expectedJobName);
        workerStartInfo.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        workerStartInfo.ArgumentList.Add(compositionFile);
        workerStartInfo.ArgumentList.Add(sourceStartColumn.ToString(System.Globalization.CultureInfo.InvariantCulture));
        workerStartInfo.ArgumentList.Add(sourceCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (configuratorUrl is not null || expectedJob is not null) workerStartInfo.ArgumentList.Add(configuratorUrl ?? "");
        if (expectedJob is not null)
        {
            workerStartInfo.ArgumentList.Add(expectedJob.ServerId);
            workerStartInfo.ArgumentList.Add(expectedJob.JobId);
            workerStartInfo.ArgumentList.Add(expectedJob.Revision);
        }
        return workerStartInfo;
    }
}
