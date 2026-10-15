using System.Diagnostics;

namespace Nightingale.Desktop.Platform;

public static class DefaultApps
{
    /// <summary>Opens the user-controlled Windows default-app picker. Does not change associations.</summary>
    public static void OpenSettings()
    {
        // registeredAppUser works on supported Windows 11 versions. Older Windows
        // opens the general default-app page when the parameter is not supported.
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=Nightingale")
        {
            UseShellExecute = true
        });
    }
}
