namespace EdiToSscs.Core;

/// <summary>Well-known per-user application paths, resolved through the .NET environment API.</summary>
public static class AppPaths
{
    public const string AppFolderName = "EDI-to-SSCS-Converter";

    /// <summary>%LOCALAPPDATA%\EDI-to-SSCS-Converter</summary>
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    /// <summary>%LOCALAPPDATA%\EDI-to-SSCS-Converter\Logs</summary>
    public static string LogsDirectory => Path.Combine(SettingsDirectory, "Logs");
}
