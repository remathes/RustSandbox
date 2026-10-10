namespace RustSandbox.Services;

/// <summary>Where the manager keeps its own files when it's installed (database, settings, SteamCMD).</summary>
public static class AppPaths
{
    /// <summary>
    /// The data folder: Manager:DataDir, or C:\ProgramData\RustSandbox when running as a
    /// Windows service. Null when the app runs from its own folder (dotnet run, a plain publish).
    /// </summary>
    public static string? DataDir { get; set; }

    /// <summary>Settings file in the data folder that overrides the bundled appsettings.json.</summary>
    public static string? SettingsFile => DataDir is null ? null : Path.Combine(DataDir, "appsettings.json");
}
