using System.Diagnostics;
using System.IO;

namespace Nightingale.Desktop.Platform;

public static class ShellIntegration
{
    public static void RevealFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        if (Directory.Exists(fullPath)) startInfo.ArgumentList.Add(fullPath);
        else
        {
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(fullPath);
        }
        Process.Start(startInfo);
    }
}
