using System.ComponentModel;
using System.Diagnostics;
using Kugutsu.Contracts;

namespace Kugutsu.App;

internal static class PowerShellTargetInfo
{
    internal static PowerShellFlavor Read(RegisteredLaunchTarget target)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(target.ExecutablePath);
            if (version.FileDescription?.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) != true)
            { throw new WindowReferenceException(ErrorCode.POWERSHELL_NOT_FOUND); }
            return Path.GetFileName(target.ExecutablePath).ToLowerInvariant() switch
            {
                "powershell.exe" => PowerShellFlavor.WindowsPowerShell,
                "pwsh.exe" when version.FileMajorPart == 7 => PowerShellFlavor.PowerShell7,
                _ => throw new WindowReferenceException(ErrorCode.POWERSHELL_NOT_FOUND)
            };
        }
        catch (Exception error) when (error is Win32Exception or FileNotFoundException)
        { throw new WindowReferenceException(ErrorCode.POWERSHELL_NOT_FOUND); }
    }
}
