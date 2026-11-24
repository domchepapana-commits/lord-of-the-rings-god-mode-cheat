using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace WitchKingTrainer
{
    /// <summary>Handles "LordOfTheRingsGodModeCheat.exe --uninstall" (the command registered in Windows Settings > Apps).</summary>
    internal static class Uninstaller
    {
        public static int Run(bool quiet)
        {
            if (!quiet && MessageBox.Show("Remove " + InstallInfo.AppName + " from this computer?\n\nThis deletes the trainer, its shortcuts and its settings. The game, your saves and Patch 2.02 are not touched.",
                    "Uninstall " + InstallInfo.AppName, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return 1;

            if (!InstallInfo.CloseRunningTrainer(8000))
            {
                if (!quiet) MessageBox.Show("The trainer is still running. Close it (CLOSE button or tray icon > Close trainer) and try again.", "Uninstall " + InstallInfo.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                return 2;
            }

            Settings.StartWithWindows = false;
            foreach (string link in new[] { InstallInfo.DesktopLink, InstallInfo.StartMenuLink }) { try { if (File.Exists(link)) File.Delete(link); } catch { } }
            try { Registry.CurrentUser.DeleteSubKeyTree(InstallInfo.UninstallKey, false); } catch { }
            try { if (Directory.Exists(InstallInfo.SettingsDir)) Directory.Delete(InstallInfo.SettingsDir, true); } catch { }

            // A program cannot delete its own folder while it runs from it. The plain, transparent way that installers use:
            // a copy of this program is put in the temp folder and started with "--cleanup"; it waits until this process has
            // ended, removes the program folder and exits. No command shell and no script is involved.
            string exe = Assembly.GetExecutingAssembly().Location, dir = Path.GetDirectoryName(exe);
            bool installed = string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(InstallInfo.InstallDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (installed)
            {
                try
                {
                    int pid = Process.GetCurrentProcess().Id;
                    string temp = Path.Combine(Path.GetTempPath(), InstallInfo.BaseName + "-uninstall-" + pid + ".exe");
                    File.Copy(exe, temp, true);
                    Process.Start(new ProcessStartInfo(temp, "--cleanup " + pid) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
                }
                catch { }
            }
            if (!quiet) MessageBox.Show(InstallInfo.AppName + " has been removed.", "Uninstall " + InstallInfo.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern bool MoveFileEx(string existing, string replacement, int flags);

        /// <summary>
        /// Second step of the uninstall, run from the temp copy: removes the files in the trainer's own program folder - that
        /// folder only, whatever the command line says - once the uninstalling process has ended.
        /// </summary>
        public static int Cleanup(string parentPid)
        {
            string dir = InstallInfo.InstallDir, self = Assembly.GetExecutingAssembly().Location;
            // This step only ever runs as the temp copy that a real uninstall has just started: the copy must lie in the temp
            // folder under the uninstaller's name, and the uninstall entry must already be gone. Typing "--cleanup" by hand
            // (or from the portable copy) therefore does nothing.
            if (!Path.GetFileName(self).StartsWith(InstallInfo.BaseName + "-uninstall-", StringComparison.OrdinalIgnoreCase)) return 3;
            if (!string.Equals(Path.GetFullPath(Path.GetDirectoryName(self)).TrimEnd('\\'), Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 3;
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(InstallInfo.UninstallKey)) if (k != null) return 3; } catch { return 3; }
            try { int pid; if (int.TryParse(parentPid, out pid)) { using (Process p = Process.GetProcessById(pid)) p.WaitForExit(15000); } } catch { }   // already gone
            for (int attempt = 0; attempt < 20 && Directory.Exists(dir); attempt++)
            {
                try
                {
                    foreach (string f in Directory.GetFiles(dir)) File.Delete(f);
                    Directory.Delete(dir, false);                                                          // only if nothing else is in it
                }
                catch { System.Threading.Thread.Sleep(300); }
            }
            try { MoveFileEx(self, null, 4); } catch { }   // ask Windows to remove this temp copy at the next restart (works when allowed; otherwise it stays in Temp)
            return Directory.Exists(dir) ? 4 : 0;
        }
    }
}
