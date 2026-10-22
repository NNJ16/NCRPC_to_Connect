using NcrpToConnect.Cli;

namespace NcrpToConnect.PortalReporter;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length == 3 && string.Equals(args[0], "--export", StringComparison.OrdinalIgnoreCase))
        {
            var report = new PortalExportReader().Read(args[1]);
            PortalExcelReportWriter.Write(report, args[2]);
            return;
        }

        Application.Run(new MainForm());
    }
}
