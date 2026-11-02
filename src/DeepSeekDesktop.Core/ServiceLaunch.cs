namespace DeepSeekDesktop.Core;

public static class ServiceLaunch
{
    public const string StartServiceArgument = "--start-service", TrayArgument = "--tray";
    public static bool Has(IEnumerable<string> args, string argument) => args.Contains(argument, StringComparer.OrdinalIgnoreCase);
    public static bool ShouldStart(bool? desired, bool startArgument, bool updateResume) => startArgument || updateResume || desired == true;
    // Settings saved before this field existed have no record, but a service resumed by an update had been started by the user.
    public static bool? AfterUpdateResume(bool? desired, bool updateResume) => desired ?? (updateResume ? true : null);
}
