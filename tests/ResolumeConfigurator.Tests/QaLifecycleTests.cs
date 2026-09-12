using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using ResolumeConfigurator.Services;
using static QaRemediationTests;

internal static class QaLifecycleTests
{
    private static void Assert(bool condition, string message) => RegressionTests.Assert(condition, message);

    internal static async Task LocalMutationGuardsAsync()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "Job.avc");
        const string original = "<Composition><VideoTrack><Params><ParamRange name=\"Width\" value=\"3840\"/></Params></VideoTrack></Composition>";
        File.WriteAllText(path, original);
        var validations = 0;
        try
        {
            await ArenaConfigurationOrchestrator.PatchCompositionAsync(path, 60, 2, 0, CancellationToken.None,
                _ => { validations++; throw new InvalidOperationException("changed job/Agent"); });
            throw new Exception("patch guard failure was ignored");
        }
        catch (InvalidOperationException) { }
        Assert(validations == 1 && File.ReadAllText(path) == original, "patch must validate after loading and preserve the file on drift");
        await ArenaConfigurationOrchestrator.PatchCompositionAsync(path, 60, 2, 0, CancellationToken.None, _ => Task.CompletedTask);
        Assert(File.ReadAllText(path) != original, "valid patch must still complete");

        foreach (var mode in new[] { "drift", "success", "cancel-after-stop" })
        {
            using var cancellation = new CancellationTokenSource();
            var stops = 0;
            var starts = 0;
            var rejected = false;
            try
            {
                await ArenaRestartService.RestartProcessAsync(_ => mode == "drift" ? Task.FromException(new InvalidOperationException("drift")) : Task.CompletedTask,
                    () => { stops++; if (mode == "cancel-after-stop") cancellation.Cancel(); },
                    token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, () => starts++,
                    (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cancellation.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { rejected = true; }
            Assert(mode == "drift" ? rejected && stops == 0 && starts == 0
                : starts == 1 && stops == 1 && rejected == (mode == "cancel-after-stop"), "restart must validate before stopping and finish relaunch after cancellation");
        }
    }

    internal static async Task OperationOwnershipAsync()
    {
        using var folder = new TempFolder();
        static void Reject(Action action)
        {
            try { action(); throw new Exception("concurrent configuration was accepted"); }
            catch (InvalidOperationException) { }
        }
        using (ConfigurationOwnership.AcquireParent(folder.Path))
        {
            Reject(() => ConfigurationOwnership.AcquireParent(folder.Path).Dispose());
            using (ConfigurationOwnership.AcquireWorker(folder.Path))
                Reject(() => ConfigurationOwnership.AcquireWorker(folder.Path).Dispose());
        }
        using (ConfigurationOwnership.AcquireWorker(folder.Path))
            Reject(() => ConfigurationOwnership.AcquireParent(folder.Path).Dispose());
        using (ConfigurationOwnership.AcquireParent(folder.Path)) { }

        using var owned = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }
        }) ?? throw new Exception("owned lifetime fixture failed to start");
        try
        {
            var ticks = owned.StartTime.ToUniversalTime().Ticks;
            Reject(() => { _ = new ParentLifetime(owned.Id, ticks + 1, CancellationToken.None); });
            await using var parent = new ParentLifetime(owned.Id, ticks, CancellationToken.None);
            parent.Validate();
            owned.Kill(entireProcessTree: true);
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try { await Task.Delay(Timeout.Infinite, parent.Token).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            Assert(parent.Token.IsCancellationRequested, "parent exit must cancel pending helper I/O");
            try { parent.Validate(); throw new Exception("orphan helper remained eligible"); }
            catch (OperationCanceledException) { }
        }
        finally { if (!owned.HasExited) { owned.Kill(entireProcessTree: true); await owned.WaitForExitAsync(); } }
    }

    internal static async Task ReplacementRecoveryAsync()
    {
        using var folder = new TempFolder();
        var paths = ArenaPaths.FromRoot(Path.Combine(folder.Path, "arena-data"));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SimpleOutputPreference)!);
        File.WriteAllText(paths.SimpleOutputPreference, "<SimpleSetup advancedModeEnabled=\"0\"/>");
        var plan = RegressionTests.ValidPlan() with { CompositionDirectory = Path.Combine(folder.Path, "separate-compositions"), PresetDirectory = paths.AdvancedOutputPresets };
        var files = await ConfigurationFiles.PrepareAsync(plan, paths, new("Arena", 7, 27, 0, 0), CancellationToken.None);
        await files.CommitAsync(_ => Task.CompletedTask, CancellationToken.None);
        using var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(files.OperationDirectory, "operation.json")));
        var backup = record.RootElement.GetProperty("Files").EnumerateArray().Single(row => row.GetProperty("Destination").GetString() == paths.SimpleOutputPreference).GetProperty("Backup").GetString()!;
        Assert(Path.GetDirectoryName(backup) == Path.GetDirectoryName(paths.SimpleOutputPreference), "native replacement backup must share the destination directory/volume");
        Assert(!backup.StartsWith(files.OperationDirectory, StringComparison.OrdinalIgnoreCase) && File.ReadAllText(backup).Contains("advancedModeEnabled=\"0\""), "operation record must retain recoverable bytes outside the composition directory");
        Assert(SimpleOutputConfigurationService.PreferenceMatches(paths.SimpleOutputPreference, plan.EnableNdiCompositionSharing), "commit must enable advanced output and retain the requested sharing mode");

        var created = Path.Combine(folder.Path, "concurrent.xml");
        File.WriteAllText(created, "<UserEdit/>");
        try
        {
            await AtomicFile.WriteXmlCheckedAsync(created, new XDocument(new XElement("Replacement")), null, null, CancellationToken.None);
            throw new Exception("concurrent creation was overwritten");
        }
        catch (AtomicFile.FileChangedException) { }
        Assert(File.ReadAllText(created) == "<UserEdit/>", "new concurrent targets must be preserved");
    }
}
