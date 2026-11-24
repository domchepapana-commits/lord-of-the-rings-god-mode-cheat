using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;

[assembly: AssemblyTitle("Lord of the Rings God Mode Cheat")]
[assembly: AssemblyProduct("Lord of the Rings God Mode Cheat")]
[assembly: AssemblyDescription("Offline single-player skirmish trainer for The Battle for Middle-earth II and The Rise of the Witch-king. No network access, no telemetry.")]
[assembly: AssemblyCompany(WitchKingTrainer.InstallInfo.Publisher)]
[assembly: AssemblyCopyright("Free personal project. Not affiliated with or endorsed by the games' publishers.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0 (build 2026-11-24)")]

namespace WitchKingTrainer
{
    internal static class Win32
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr info);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("kernel32.dll")] public static extern bool Beep(uint freq, uint ms);
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
    }

    public static class Program
    {
        public const string ShowMessageName = "WitchKingTrainer.ShowWindow.v1";
        const string MutexName = "WitchKingTrainer.SingleInstance.v1";

        public static bool IsElevated
        {
            get { try { using (WindowsIdentity id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; } }
        }

        /// <summary>Restarts this program through the normal Windows elevation prompt. Returns false if the user declined.</summary>
        public static bool RelaunchElevated(string args)
        {
            try
            {
                var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, args) { UseShellExecute = true, Verb = "runas" };
                Process.Start(psi);
                return true;
            }
            catch (Win32Exception) { return false; }   // 1223 = the user cancelled the prompt
            catch { return false; }
        }

        static bool OtherInstanceRunning()
        {
            try { Mutex m; if (Mutex.TryOpenExisting(MutexName, out m)) { m.Dispose(); return true; } return false; }
            catch (UnauthorizedAccessException) { return true; }   // it exists, but belongs to an elevated copy
            catch { return false; }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            bool tray = Array.IndexOf(args, "--tray") >= 0, noElevate = Array.IndexOf(args, "--no-elevate") >= 0, waited = Array.IndexOf(args, "--relaunched") >= 0;
            if (Array.IndexOf(args, "--uninstall") >= 0) return Uninstaller.Run(Array.IndexOf(args, "--quiet") >= 0);
            int cleanup = Array.IndexOf(args, "--cleanup");
            if (cleanup >= 0) return Uninstaller.Cleanup(cleanup + 1 < args.Length ? args[cleanup + 1] : "");

            // Already running (e.g. sitting in the tray)? Just bring that window forward - no second copy, no elevation prompt.
            if (!waited && OtherInstanceRunning())
            {
                InstallInfo.SignalTrainer(ShowMessageName);
                return 0;
            }

            // The game runs elevated, so a trainer started by hand elevates straight away (standard UAC prompt).
            // When started with Windows (--tray) it stays unelevated until the game actually needs it.
            if (!IsElevated && !tray && !noElevate)
            {
                if (RelaunchElevated("--relaunched")) return 0;
                // declined: carry on without rights; the window explains what is missing and offers to retry
            }

            Mutex mutex = null; bool created = false;
            for (int attempt = 0; attempt < (waited ? 20 : 1) && !created; attempt++)
            {
                try { mutex = new Mutex(true, MutexName, out created); }
                catch (UnauthorizedAccessException) { created = false; }   // an elevated instance owns it
                if (!created) { if (mutex != null) { mutex.Dispose(); mutex = null; } if (waited) Thread.Sleep(150); }
            }
            if (!created)
            {
                // another copy is already running: ask it to show its window, then leave
                InstallInfo.SignalTrainer(ShowMessageName);
                return 0;
            }

            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var main = new MainWindow(app, tray);
                app.DispatcherUnhandledException += (s, e) => { main.ReportCrash(e.Exception); e.Handled = true; };
                main.Show();
                app.Run();
                return 0;
            }
            finally { try { mutex.ReleaseMutex(); } catch { } mutex.Dispose(); }
        }
    }
}
