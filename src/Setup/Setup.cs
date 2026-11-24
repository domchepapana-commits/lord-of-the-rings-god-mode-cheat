// WitchKingTrainer-Setup.exe : per-user installer (no administrator rights needed).
// Copies the trainer to %LOCALAPPDATA%\Programs\WitchKingTrainer, creates the shortcuts and registers the uninstaller.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Lord of the Rings God Mode Cheat Setup")]
[assembly: AssemblyProduct("Lord of the Rings God Mode Cheat")]
[assembly: AssemblyDescription("Per-user installer for the Lord of the Rings God Mode Cheat, an offline skirmish trainer (no administrator rights needed, no network access).")]
[assembly: AssemblyCompany(WitchKingTrainer.InstallInfo.Publisher)]
[assembly: AssemblyCopyright("Free personal project. Not affiliated with or endorsed by the games' publishers.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace WitchKingTrainer
{
    static class Installer
    {
        static void Extract(string resource, string path)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (s == null) throw new InvalidOperationException("Setup package is damaged (missing " + resource + ").");
                using (FileStream f = File.Create(path)) s.CopyTo(f);
            }
        }

        public static bool IsInstalled { get { return File.Exists(Path.Combine(InstallInfo.InstallDir, InstallInfo.ExeName)); } }

        public static void Install(bool desktopShortcut)
        {
            if (!InstallInfo.CloseRunningTrainer(8000)) throw new InvalidOperationException("The trainer is running. Close it first (CLOSE button), then install again.");
            string dir = InstallInfo.InstallDir, exe = Path.Combine(dir, InstallInfo.ExeName);
            Directory.CreateDirectory(dir);
            Extract("Payload.App.exe", exe);
            foreach (string doc in new[] { "README.txt", "SECURITY.txt", "TESTING-GUIDE.txt" }) Extract("Payload." + doc, Path.Combine(dir, doc));
            bool hadDesktop = RemoveOldInstallation(exe);
            InstallInfo.CreateShortcut(InstallInfo.StartMenuLink, exe, "Offline skirmish trainer for The Battle for Middle-earth II and The Rise of the Witch-king");
            if (desktopShortcut || hadDesktop) InstallInfo.CreateShortcut(InstallInfo.DesktopLink, exe, "Offline skirmish trainer for The Battle for Middle-earth II and The Rise of the Witch-king");
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(InstallInfo.UninstallKey))
            {
                k.SetValue("DisplayName", InstallInfo.AppName); k.SetValue("DisplayVersion", InstallInfo.Version); k.SetValue("Publisher", InstallInfo.Publisher);
                k.SetValue("InstallLocation", dir); k.SetValue("DisplayIcon", exe);
                k.SetValue("UninstallString", "\"" + exe + "\" --uninstall"); k.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall --quiet");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord); k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024) + 8, RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// The program was called "Witch-King Trainer" before. An installation under that name is taken over: its shortcuts,
        /// its entry in Windows' list of apps, its start-with-Windows entry and its program files (only the four files that
        /// setup itself put there) are removed, now that the same program is installed under the new name. Settings are kept:
        /// the program copies them from the old settings folder the first time it starts. Returns true if there was a desktop
        /// shortcut under the old name.
        /// </summary>
        static bool RemoveOldInstallation(string newExe)
        {
            bool hadDesktop = false;
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), InstallInfo.OldAppName + ".lnk");
            string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), InstallInfo.OldAppName + ".lnk");
            try { if (File.Exists(desktop)) { hadDesktop = true; File.Delete(desktop); } } catch { }
            try { if (File.Exists(menu)) File.Delete(menu); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(InstallInfo.OldUninstallKey, false); } catch { }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(InstallInfo.RunKey, true))
                    if (k != null && k.GetValue(InstallInfo.OldBaseName) != null) { k.DeleteValue(InstallInfo.OldBaseName, false); k.SetValue(InstallInfo.BaseName, "\"" + newExe + "\" --tray"); }
            }
            catch { }
            try
            {
                string old = InstallInfo.OldInstallDir;
                if (Directory.Exists(old))
                {
                    foreach (string f in new[] { InstallInfo.OldBaseName + ".exe", "README.txt", "SECURITY.txt", "TESTING-GUIDE.txt" }) { try { string p = Path.Combine(old, f); if (File.Exists(p)) File.Delete(p); } catch { } }
                    if (Directory.GetFileSystemEntries(old).Length == 0) Directory.Delete(old, false);      // anything else in it is left alone
                }
            }
            catch { }
            return hadDesktop;
        }

        public static void Launch() { Process.Start(new ProcessStartInfo(Path.Combine(InstallInfo.InstallDir, InstallInfo.ExeName)) { UseShellExecute = true }); }
        public static void Uninstall() { Process.Start(new ProcessStartInfo(Path.Combine(InstallInfo.InstallDir, InstallInfo.ExeName), "--uninstall") { UseShellExecute = true }); }
    }

    sealed class SetupForm : Form
    {
        static readonly Color Bg = Color.FromArgb(17, 25, 35), Gold = Color.FromArgb(217, 182, 92), Text1 = Color.FromArgb(230, 237, 244), Dim = Color.FromArgb(147, 165, 184);
        readonly CheckBox _desktop = new CheckBox(), _launch = new CheckBox(); readonly Label _status = new Label(); readonly Button _install = new Button(), _uninstall = new Button(), _close = new Button();
        bool _done; float _k = 1f;
        int S(int v) { return (int)Math.Round(v * _k); }
        Point P(int x, int y) { return new Point(S(x), S(y)); }
        Size Z(int w, int h) { return new Size(S(w), S(h)); }

        Button Style(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Gold; b.FlatAppearance.BorderSize = 1; b.Cursor = Cursors.Hand; b.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            b.BackColor = primary ? Gold : Color.FromArgb(24, 36, 50); b.ForeColor = primary ? Color.FromArgb(11, 22, 34) : Color.FromArgb(207, 225, 240); 
            return b;
        }

        public SetupForm()
        {
            Text = InstallInfo.AppName + " Setup"; Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            // WinForms auto-scaling is unreliable on high-DPI displays for hand-built forms, so the layout is scaled explicitly
            AutoScaleMode = AutoScaleMode.None;
            using (Graphics g = CreateGraphics()) _k = g.DpiX / 96f;
            ClientSize = Z(560, 372); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg; ForeColor = Text1; Font = new Font("Segoe UI", 10f);
            Controls.Add(new PictureBox { Image = Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Location = P(26, 22), Size = Z(48, 48) });
            Controls.Add(new Label { Text = "LORD OF THE RINGS  ·  GOD MODE CHEAT", Font = new Font("Palatino Linotype", 15f, FontStyle.Bold), ForeColor = Gold, Location = P(86, 16), Size = Z(460, 40) });
            Controls.Add(new Label { Text = "Version " + InstallInfo.Version + "  ·  Rise of the Witch-king and Battle for Middle-earth II  ·  offline skirmish only", ForeColor = Dim, Font = new Font("Segoe UI", 8.75f), Location = P(88, 56), Size = Z(460, 20) });
            Controls.Add(new Label
            {
                Text = "Setup installs the trainer for your Windows account only. No administrator rights are needed to install.\r\n\r\n" +
                       "•  Nothing in your games, their patches, maps or saves is changed.\r\n" +
                       "•  The trainer itself asks for administrator rights when it starts, because the games run as administrator.\r\n" +
                       "•  Uninstall any time from Windows Settings > Apps, or from this setup program.",
                Location = P(28, 92), Size = Z(506, 128)
            });
            _desktop.Text = "Create a desktop shortcut"; _desktop.Checked = true; _desktop.Location = P(30, 224); _desktop.Size = Z(300, 26); _desktop.Cursor = Cursors.Hand; Controls.Add(_desktop);
            _launch.Text = "Start the trainer when setup finishes"; _launch.Checked = true; _launch.Location = P(30, 252); _launch.Size = Z(340, 26); _launch.Cursor = Cursors.Hand; Controls.Add(_launch);
            _status.Location = P(28, 286); _status.Size = Z(506, 24); _status.ForeColor = Dim; _status.Text = "Installs to " + InstallInfo.InstallDir; _status.AutoEllipsis = true; Controls.Add(_status);

            Style(_install, true).SetBounds(S(374), S(318), S(160), S(40)); _install.Text = Installer.IsInstalled ? "Reinstall" : "Install"; _install.Click += OnInstall; Controls.Add(_install);
            Style(_uninstall, false).SetBounds(S(28), S(318), S(130), S(40)); _uninstall.Text = "Uninstall"; _uninstall.Visible = Installer.IsInstalled; _uninstall.Click += (s, e) => { try { Installer.Uninstall(); Close(); } catch (Exception ex) { Fail(ex); } }; Controls.Add(_uninstall);
            Style(_close, false).SetBounds(S(254), S(318), S(110), S(40)); _close.Text = "Cancel"; _close.Click += (s, e) => Close(); Controls.Add(_close);
            AcceptButton = _install; CancelButton = _close;
        }

        void Fail(Exception ex) { _status.ForeColor = Color.FromArgb(229, 115, 104); _status.Text = ex.Message; }

        void OnInstall(object sender, EventArgs e)
        {
            if (_done) { Close(); return; }
            try
            {
                Cursor = Cursors.WaitCursor; _status.ForeColor = Dim; _status.Text = "Installing..."; _status.Refresh();
                Installer.Install(_desktop.Checked);
                _status.ForeColor = Color.FromArgb(127, 214, 154); _status.Text = "Installed. Open \"" + InstallInfo.AppName + "\" from the desktop or Start menu.";
                _done = true; _install.Text = "Finish"; _close.Visible = false; _uninstall.Visible = false; _desktop.Enabled = false;
                if (_launch.Checked) { _launch.Enabled = false; Installer.Launch(); }
            }
            catch (Exception ex) { Fail(ex); }
            finally { Cursor = Cursors.Default; }
        }
    }

    static class SetupProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            // "/silent" installs without showing a window (used for scripted installs); "/nodesktop" skips the desktop shortcut
            if (Array.IndexOf(args, "/silent") >= 0)
            {
                try { Installer.Install(Array.IndexOf(args, "/nodesktop") < 0); return 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
            return 0;
        }
    }
}
