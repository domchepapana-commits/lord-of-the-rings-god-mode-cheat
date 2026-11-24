using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WitchKingTrainer.Core
{
    /// <summary>How far a data patch has been checked with this trainer.</summary>
    public enum PatchStatus
    {
        Verified,      // every cheat was play-tested on it in real skirmishes
        Untested,      // runs on a verified game engine, but was never play-tested
        Unsupported    // different game engine: the trainer refuses to touch it
    }

    public sealed class PatchEntry
    {
        public string Key = "", Name = "", Note = ""; public PatchStatus Status;
        public string StatusText { get { return Status == PatchStatus.Verified ? "VERIFIED" : Status == PatchStatus.Untested ? "NOT VERIFIED" : "NOT SUPPORTED"; } }
    }

    /// <summary>What is known about one game on this PC.</summary>
    public sealed class InstalledGame
    {
        public GameId Game; public string Name = "", Short = ""; public bool Installed, EngineKnown;
        public string InstallPath = "", ExeVersion = "", EngineLabel = "", PatchSource = "";
        public PatchEntry Patch = new PatchEntry();     // the data patch that is active according to the launcher / the game folder
    }

    /// <summary>
    /// Finds the installed games and the data patch each one is set up to run. Read-only: registry, file versions and the
    /// "BFME Workshop" launcher's own record of the active patch. Nothing in the game folders is ever changed.
    /// </summary>
    public static class GameCatalog
    {
        /// <summary>
        /// The releases of Patch 2.02 that were play-tested, each with the size of its data file in the game folder
        /// (9.8.0: tested 16-24 Nov 2026; 9.8.7: tested 24 Nov 2026 after the All in One Launcher installed it).
        /// </summary>
        public static readonly string[] TestedRotwk202Versions = { "9.8.0", "9.8.7" };
        static readonly long[] TestedRotwk202PatchSizes = { 693767333, 707207673 };
        public const string Rotwk202PatchFile = "__patch202.big";
        const string TestedRotwk202 = "9.8.0 and v9.8.7";
        static bool IsTested202(string version) { return Array.IndexOf(TestedRotwk202Versions, version) >= 0; }
        /// <summary>The tested release whose patch file has this size (null if it is none of them).</summary>
        static string Tested202BySize(long size) { int i = Array.IndexOf(TestedRotwk202PatchSizes, size); return i >= 0 ? TestedRotwk202Versions[i] : null; }

        static readonly PatchEntry[] RotwkPatches =
        {
            new PatchEntry { Key = "rotwk-2.02", Name = "Patch 2.02 (community)", Status = PatchStatus.Verified, Note = "Play-tested in real skirmishes with 2.02 v" + TestedRotwk202 + "." },
            new PatchEntry { Key = "rotwk-2.01", Name = "Patch 2.01 (official)", Status = PatchStatus.Untested, Note = "Same game engine as 2.02, but never play-tested." },
            new PatchEntry { Key = "rotwk-2.00", Name = "Version 2.00 (retail)", Status = PatchStatus.Unsupported, Note = "Different game engine." },
        };
        static readonly PatchEntry[] Bfme2Patches =
        {
            new PatchEntry { Key = "bfme2-1.06", Name = "Patch 1.06 (official)", Status = PatchStatus.Verified, Note = "Play-tested in real skirmishes." },
            new PatchEntry { Key = "bfme2-1.09", Name = "Patch 1.09 / 1.09v3 (community)", Status = PatchStatus.Untested, Note = "Runs on the 1.06 game engine, but never play-tested." },
            new PatchEntry { Key = "bfme2-old", Name = "Versions 1.00 - 1.05", Status = PatchStatus.Unsupported, Note = "Different game engine." },
        };

        public static PatchEntry[] Known(GameId g) { return g == GameId.Rotwk ? RotwkPatches : Bfme2Patches; }
        public static PatchEntry Find(GameId g, string key) { foreach (PatchEntry p in Known(g)) if (p.Key == key) return p; return null; }
        public static string GameName(GameId g) { return g == GameId.Rotwk ? "The Rise of the Witch-king" : "The Battle for Middle-earth II"; }
        public static string ShortName(GameId g) { return g == GameId.Rotwk ? "Rise of the Witch-king" : "Battle for Middle-earth II"; }

        public static InstalledGame Detect(GameId g)
        {
            var r = new InstalledGame { Game = g, Name = GameName(g), Short = ShortName(g) };
            try
            {
                string key = @"SOFTWARE\Electronic Arts\Electronic Arts\" + (g == GameId.Rotwk ? "The Lord of the Rings, The Rise of the Witch-king" : "The Battle for Middle-earth II");
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (RegistryKey k = hklm.OpenSubKey(key))
                    if (k != null) r.InstallPath = (k.GetValue("InstallPath") as string ?? "").Trim();
            }
            catch { }
            string exe = r.InstallPath.Length > 0 ? Path.Combine(r.InstallPath, "game.dat") : "";
            try
            {
                if (exe.Length > 0 && File.Exists(exe))
                {
                    r.Installed = true;
                    FileVersionInfo v = FileVersionInfo.GetVersionInfo(exe);
                    r.ExeVersion = v.FileMajorPart + "." + v.FileMinorPart + "." + v.FileBuildPart + "." + v.FilePrivatePart;
                    r.EngineLabel = v.FileMajorPart + "." + v.FileMinorPart.ToString("00");
                    r.EngineKnown = g == GameId.Rotwk ? r.ExeVersion == "2.1.2614.37001" : r.ExeVersion == "1.6.2429.30210";
                }
            }
            catch { }
            r.Patch = ActivePatch(g, r);
            return r;
        }

        /// <summary>Classifies a patch by the name and version a launcher reports for it.</summary>
        public static PatchEntry Classify(GameId g, string name, string version)
        {
            name = name ?? ""; version = version ?? "";
            if (g == GameId.Rotwk)
            {
                if (name.Contains("2.02"))
                {
                    if (version.Length == 0 || IsTested202(version)) return new PatchEntry { Key = "rotwk-2.02", Name = "Patch 2.02" + (version.Length > 0 ? " v" + version : ""), Status = PatchStatus.Verified, Note = RotwkPatches[0].Note };
                    return new PatchEntry { Key = "rotwk-2.02-v" + version, Name = "Patch 2.02 v" + version, Status = PatchStatus.Untested, Note = "A different 2.02 release than the play-tested v" + TestedRotwk202 + "." };
                }
                if (name.Contains("2.01") || name.IndexOf("vanilla", StringComparison.OrdinalIgnoreCase) >= 0) return RotwkPatches[1];
            }
            else
            {
                if (name.Contains("1.06") || name.IndexOf("vanilla", StringComparison.OrdinalIgnoreCase) >= 0) return Bfme2Patches[0];
                if (name.Contains("1.09")) return new PatchEntry { Key = "bfme2-1.09", Name = name, Status = PatchStatus.Untested, Note = Bfme2Patches[1].Note };
            }
            return new PatchEntry { Key = (g == GameId.Rotwk ? "rotwk" : "bfme2") + "-other:" + name, Name = name.Length > 0 ? name : "Unknown patch", Status = PatchStatus.Untested, Note = "Not one of the play-tested patches." };
        }

        static PatchEntry ActivePatch(GameId g, InstalledGame r)
        {
            // 1) the BFME Workshop / All-in-One launcher keeps the active patch of each game in a small JSON file
            try
            {
                string f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"BFME Workshop\Config\active_patch_" + (g == GameId.Rotwk ? 2 : 1) + ".json");
                if (File.Exists(f))
                {
                    var buf = new char[4096]; int n;
                    using (var sr = new StreamReader(f)) n = sr.Read(buf, 0, buf.Length);
                    string head = new string(buf, 0, n);
                    Match name = Regex.Match(head, "\"Name\"\\s*:\\s*\"([^\"]*)\""), ver = Regex.Match(head, "\"Version\"\\s*:\\s*\"([^\"]*)\"");
                    if (name.Success)
                    {
                        string nm = name.Groups[1].Value, vs = ver.Success ? ver.Groups[1].Value : "";
                        // the launcher gives the untouched game the version "1.0.0"; that is not a patch version
                        if (nm.IndexOf("vanilla", StringComparison.OrdinalIgnoreCase) >= 0) vs = "";
                        r.PatchSource = "launcher";
                        // What the launcher has on record is not always what is installed: it can list a newer release whose
                        // files it has not downloaded yet (seen on 24 Nov 2026: record 9.8.7, files still 9.8.0). For Patch 2.02
                        // the patch file in the game folder decides - in both directions.
                        if (g == GameId.Rotwk && nm.Contains("2.02") && r.InstallPath.Length > 0)
                        {
                            long size = 0; try { var fi = new FileInfo(Path.Combine(r.InstallPath, Rotwk202PatchFile)); if (fi.Exists) size = fi.Length; } catch { }
                            string onDisk = Tested202BySize(size);
                            if (onDisk != null && vs != onDisk)
                            {
                                PatchEntry e = Classify(g, nm, onDisk); r.PatchSource = "game files";
                                e.Note += " The launcher lists v" + vs + ", but the patch file in the game folder is v" + onDisk + ".";
                                return e;
                            }
                            if (size > 0 && onDisk == null && IsTested202(vs))
                                return new PatchEntry { Key = "rotwk-2.02-changed", Name = "Patch 2.02 (other files)", Status = PatchStatus.Untested, Note = "The patch file in the game folder is not one of the play-tested files (v" + TestedRotwk202 + ")." };
                        }
                        return Classify(g, nm, vs);
                    }
                }
            }
            catch { }
            // 2) otherwise judge by the game folder and the executable
            try
            {
                if (r.Installed)
                {
                    r.PatchSource = "game folder";
                    if (g == GameId.Rotwk) return Directory.GetFiles(r.InstallPath, "*patch202*.big").Length > 0 ? Classify(g, "Patch 2.02", "") : RotwkPatches[1];
                    foreach (string f in Directory.GetFiles(r.InstallPath, "*.big"))
                    {
                        string n = Path.GetFileName(f).ToLowerInvariant();
                        if (n.Contains("109") || n.Contains("1.09")) return Classify(g, "Patch 1.09", "");
                    }
                    return r.EngineKnown ? Bfme2Patches[0] : Bfme2Patches[2];
                }
            }
            catch { }
            return new PatchEntry { Key = "none", Name = "Not installed", Status = PatchStatus.Unsupported, Note = "The game was not found on this PC." };
        }
    }
}
