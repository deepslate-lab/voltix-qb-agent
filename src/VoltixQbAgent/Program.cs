using VoltixQbAgent.Forms;

namespace VoltixQbAgent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One agent per machine — a second instance would double-claim outbox
        // jobs and double-poll QuickBooks.
        using var mutex = new Mutex(initiallyOwned: true, "Global\\VoltixQbAgent", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("Voltix QB Agent is already running (check the system tray).",
                "Voltix QB Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Log.Info($"Voltix QB Agent v{Voltix.VoltixClient.AgentVersion} starting.");
        Application.Run(new MainForm(AppConfig.Load()));
    }
}
