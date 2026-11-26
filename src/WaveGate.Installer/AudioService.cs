using System.ServiceProcess;

namespace WaveGate.Installer;

internal static class AudioService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Restarts Audiosrv so audiodg reloads the endpoint's effect chain.</summary>
    public static void Restart()
    {
        using var audio = new ServiceController("Audiosrv");

        // Stop() also stops dependents; remember the running ones so we can bring them back.
        var dependents = audio.DependentServices
            .Where(s => s.Status == ServiceControllerStatus.Running)
            .Select(s => s.ServiceName)
            .ToList();

        if (audio.Status != ServiceControllerStatus.Stopped)
        {
            audio.Stop();
            audio.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
        }

        audio.Start();
        audio.WaitForStatus(ServiceControllerStatus.Running, Timeout);

        foreach (var name in dependents)
        {
            using var dependent = new ServiceController(name);
            if (dependent.Status == ServiceControllerStatus.Stopped) dependent.Start();
        }
    }
}
