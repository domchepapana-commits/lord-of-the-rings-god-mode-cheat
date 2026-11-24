using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace WitchKingTrainer
{
    /// <summary>Names and locations shared by the trainer (for its --uninstall mode) and the setup program.</summary>
    public static class InstallInfo
    {
        public const string AppName = "Lord of the Rings God Mode Cheat";
        /// <summary>The name this program had up to November 2026 (shortcuts, folders and the Apps entry of an old installation are taken over under the new name).</summary>
        public const string OldAppName = "Witch-King Trainer", OldBaseName = "WitchKingTrainer";
        public const string BaseName = "LordOfTheRingsGodModeCheat";
        /// <summary>The version stays 1.0; builds are told apart by the build date in the file properties.</summary>
        public const string Version = "1.0";
        /// <summary>Shown as the publisher in Windows and in the file properties. Set this to the name on your code-signing certificate before a public release.</summary>
        public const string Publisher = "Personal project";
        public const string ExeName = BaseName + ".exe";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + BaseName;
        public const string OldUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + OldBaseName;
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string QuitMessageName = "WitchKingTrainer.Quit.v1";

        public static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", BaseName); } }
        public static string OldInstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", OldBaseName); } }
        public static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }
        public static string StartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        public static string SettingsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), BaseName); } }
        public static string OldSettingsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldBaseName); } }
        static System.Collections.Generic.List<Process> Running() { var l = new System.Collections.Generic.List<Process>(); int self = Process.GetCurrentProcess().Id; foreach (string n in new[] { BaseName, OldBaseName }) foreach (Process p in Process.GetProcessesByName(n)) if (p.Id != self) l.Add(p); return l; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);

        /// <summary>Posts a registered message to every top-level window of every running trainer (and broadcasts it as a fallback).</summary>
        public static void SignalTrainer(string messageName)
        {
            uint msg = RegisterWindowMessage(messageName); int self = Process.GetCurrentProcess().Id; var pids = new System.Collections.Generic.HashSet<int>();
            foreach (Process p in Running()) pids.Add(p.Id);
            EnumWindows((h, l) => { int pid; GetWindowThreadProcessId(h, out pid); if (pids.Contains(pid)) PostMessage(h, msg, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero);
            PostMessage(new IntPtr(0xFFFF), msg, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Asks a running trainer to close itself the normal way (which switches cheats off and restores the game's values)
        /// and waits for it to exit. Returns false if a copy is still running afterwards.
        /// </summary>
        public static bool CloseRunningTrainer(int waitMs)
        {
            int self = Process.GetCurrentProcess().Id;
            Func<bool> any = () => Running().Count > 0;
            if (!any()) return true;
            SignalTrainer(QuitMessageName);
            for (int t = 0; t < waitMs; t += 150) { Thread.Sleep(150); if (!any()) return true; }
            return !any();
        }

        // The Windows shell's own shortcut object (IShellLink). No script host is involved.
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath(IntPtr file, int cch, IntPtr findData, int flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription(IntPtr name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory(IntPtr dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments(IntPtr args, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation(IntPtr iconPath, int cch, out int icon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, int reserved);
            void Resolve(IntPtr hwnd, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        /// <summary>Creates a .lnk shortcut with the Windows shell's shortcut object.</summary>
        public static void CreateShortcut(string linkPath, string target, string description)
        {
            object o = new ShellLink();
            try
            {
                var link = (IShellLinkW)o;
                link.SetPath(target);
                link.SetWorkingDirectory(Path.GetDirectoryName(target));
                link.SetIconLocation(target, 0);
                link.SetDescription(description);
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)o).Save(linkPath, true);
            }
            finally { Marshal.FinalReleaseComObject(o); }
        }
    }
}
