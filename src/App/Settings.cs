using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace WitchKingTrainer
{
    /// <summary>User preferences, stored as a small key=value file in %APPDATA%\WitchKingTrainer.</summary>
    public sealed class Settings
    {
        public int CommandPointLimit = 10000;
        public double ZoomFactor = 1.6;
        public int MoneyAmount = 999999;
        public bool Hotkeys = true;
        public bool Sounds = true;
        public bool Topmost = false;
        public bool TrayTipShown = false;
        public bool HoldToRecruit = false;
        public int HoldSpeed = 6;                 // clicks per second
        public int OverlayPercent = 90;           // menu width as a share of the game window
        public double Left = double.NaN, Top = double.NaN;
        public string Game = "auto";              // auto | rotwk | bfme2 : which game the trainer works with
        public string LastGame = "rotwk";         // the game last seen (decides the look while nothing is running)
        public string PatchRotwk = "auto", PatchBfme2 = "auto";   // auto, or the key of a known patch
        public bool AllowUnverified = false;      // let cheats run on patches that were never play-tested (same engine only)
        public const int KeyCount = 14;
        /// <summary>Virtual-key code per <see cref="HotAction"/>; 0 = not assigned. Defaults: F1..F12 in the order of the enum.</summary>
        public int[] Keys = DefaultKeys();

        public static int[] DefaultKeys() { var k = new int[KeyCount]; for (int i = 0; i < 12; i++) k[i] = 0x70 + i; return k; }
        /// <summary>Assignable keys: F1-F12 and the number-pad digits (0 = none).</summary>
        public static bool KeyAllowed(int vk) { return vk == 0 || (vk >= 0x70 && vk <= 0x7B) || (vk >= 0x60 && vk <= 0x69); }
        public static string KeyName(int vk) { return vk >= 0x70 && vk <= 0x7B ? "F" + (vk - 0x6F) : vk >= 0x60 && vk <= 0x69 ? "Num " + (vk - 0x60) : "None"; }

        // Portable copy: a file called "portable.txt" next to the program makes it keep its settings and its session log in a
        // "TrainerData" folder beside itself instead of the Windows profile, so the whole folder can be moved or deleted as one.
        static string _folder; static bool _portable;
        public static bool Portable { get { string f = Folder; return _portable; } }
        public static string Folder
        {
            get
            {
                if (_folder != null) return _folder;
                string normal = InstallInfo.SettingsDir;
                try
                {
                    string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    if (File.Exists(Path.Combine(dir, "portable.txt")))
                    {
                        string data = Path.Combine(dir, "TrainerData");
                        Directory.CreateDirectory(data);
                        string probe = Path.Combine(data, "write.test"); File.WriteAllText(probe, ""); File.Delete(probe);   // read-only place (CD, protected folder)? then use the profile
                        _portable = true; _folder = data; return _folder;
                    }
                }
                catch { }
                // The program was called "Witch-King Trainer" before: the first start under the new name takes the settings
                // over from the old settings folder (a copy; the old folder with its diary is left as it is).
                try
                {
                    string oldFile = Path.Combine(InstallInfo.OldSettingsDir, "settings.ini"), newFile = Path.Combine(normal, "settings.ini");
                    if (!File.Exists(newFile) && File.Exists(oldFile)) { Directory.CreateDirectory(normal); File.Copy(oldFile, newFile, false); }
                }
                catch { }
                _folder = normal; return _folder;
            }
        }
        static string FilePath { get { return Path.Combine(Folder, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(FilePath)) { int i = line.IndexOf('='); if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim(); }
                string v; CultureInfo c = CultureInfo.InvariantCulture; int n; double f;
                if (d.TryGetValue("CommandPointLimit", out v) && int.TryParse(v, out n)) s.CommandPointLimit = n;
                if (d.TryGetValue("ZoomFactor", out v) && double.TryParse(v, NumberStyles.Float, c, out f)) s.ZoomFactor = Math.Max(1, Math.Min(3, f));
                if (d.TryGetValue("MoneyAmount", out v) && int.TryParse(v, out n)) s.MoneyAmount = n;
                if (d.TryGetValue("Sounds", out v)) s.Sounds = v == "1";
                if (d.TryGetValue("Topmost", out v)) s.Topmost = v == "1";
                if (d.TryGetValue("TrayTipShown", out v)) s.TrayTipShown = v == "1";
                if (d.TryGetValue("HoldToRecruit", out v)) s.HoldToRecruit = v == "1";
                if (d.TryGetValue("HoldSpeed", out v) && int.TryParse(v, out n)) s.HoldSpeed = Math.Max(2, Math.Min(10, n));
                if (d.TryGetValue("OverlayPercent", out v) && int.TryParse(v, out n)) s.OverlayPercent = Math.Max(55, Math.Min(100, n));
                if (d.TryGetValue("Left", out v) && double.TryParse(v, NumberStyles.Float, c, out f)) s.Left = f;
                if (d.TryGetValue("Top", out v) && double.TryParse(v, NumberStyles.Float, c, out f)) s.Top = f;
                // version 2 hotkeys (plain function keys). Settings written by version 1 used Ctrl+Alt combinations and are ignored.
                if (d.TryGetValue("HotkeysV2", out v)) s.Hotkeys = v == "1";
                if (d.TryGetValue("KeysV2", out v))
                {
                    string[] parts = v.Split(',');
                    if (parts.Length == 12) for (int i = 0; i < 12; i++) if (int.TryParse(parts[i], out n) && KeyAllowed(n)) s.Keys[i] = n;
                }
                if (d.TryGetValue("KeysExtra", out v))
                {
                    string[] parts = v.Split(',');
                    for (int i = 0; i < parts.Length && 12 + i < KeyCount; i++) if (int.TryParse(parts[i], out n) && KeyAllowed(n)) s.Keys[12 + i] = n;
                }
                if (d.TryGetValue("Game", out v) && (v == "auto" || v == "rotwk" || v == "bfme2")) s.Game = v;
                if (d.TryGetValue("LastGame", out v) && (v == "rotwk" || v == "bfme2")) s.LastGame = v;
                if (d.TryGetValue("PatchRotwk", out v) && v.Length > 0) s.PatchRotwk = v;
                if (d.TryGetValue("PatchBfme2", out v) && v.Length > 0) s.PatchBfme2 = v;
                if (d.TryGetValue("AllowUnverified", out v)) s.AllowUnverified = v == "1";
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder); CultureInfo c = CultureInfo.InvariantCulture;
                File.WriteAllLines(FilePath, new[]
                {
                    "CommandPointLimit=" + CommandPointLimit, "ZoomFactor=" + ZoomFactor.ToString("0.0", c), "MoneyAmount=" + MoneyAmount,
                    "HotkeysV2=" + (Hotkeys ? "1" : "0"), "KeysV2=" + string.Join(",", Array.ConvertAll(First(Keys, 0, 12), k => k.ToString())),
                    "KeysExtra=" + string.Join(",", Array.ConvertAll(First(Keys, 12, KeyCount - 12), k => k.ToString())),
                    "Game=" + Game, "LastGame=" + LastGame, "PatchRotwk=" + PatchRotwk, "PatchBfme2=" + PatchBfme2,
                    "AllowUnverified=" + (AllowUnverified ? "1" : "0"),
                    "Sounds=" + (Sounds ? "1" : "0"), "Topmost=" + (Topmost ? "1" : "0"), "TrayTipShown=" + (TrayTipShown ? "1" : "0"),
                    "HoldToRecruit=" + (HoldToRecruit ? "1" : "0"), "HoldSpeed=" + HoldSpeed, "OverlayPercent=" + OverlayPercent,
                    "Left=" + (double.IsNaN(Left) ? "" : Left.ToString("0", c)), "Top=" + (double.IsNaN(Top) ? "" : Top.ToString("0", c))
                });
            }
            catch { }
        }

        static int[] First(int[] a, int start, int count) { var r = new int[count]; Array.Copy(a, start, r, 0, count); return r; }

        // ---- "Start with Windows" lives in the per-user Run key (no administrator rights needed, off by default) ----
        const string RunKey = InstallInfo.RunKey, RunName = InstallInfo.BaseName;

        public static bool StartWithWindows
        {
            get { try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; } catch { return false; } }
            set
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) k.SetValue(RunName, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --tray");
                        else k.DeleteValue(RunName, false);
                    }
                }
                catch { }
            }
        }
    }
}
