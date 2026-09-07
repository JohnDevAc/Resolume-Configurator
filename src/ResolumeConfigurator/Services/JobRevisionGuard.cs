using ResolumeConfigurator.Models;

namespace ResolumeConfigurator.Services;

public static class JobRevisionGuard
{
    public static void Validate(JobIdentity? expected, JobSnapshot actual)
    {
        if (expected is null || actual.Identity is null)
            throw new InvalidOperationException("Update NDI Job Configurator, then reload this job. A stable server/job revision is required before changing Arena.");
        if (expected != actual.Identity)
            throw new InvalidOperationException("The selected server, job or device configuration changed. Reload the job and review the plan before changing Arena or decoders.");
    }

    public static async Task<JobSnapshot> RefreshAsync(string? address, JobIdentity? expected, CancellationToken ct, bool includeLocalCredentials = false)
    {
        if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("Select a Job Configurator before configuring Arena.");
        var snapshot = await new NdiJobConfiguratorReader(address).ReadAsync(ct, includeLocalCredentials).ConfigureAwait(false);
        Validate(expected, snapshot);
        return snapshot;
    }
}
