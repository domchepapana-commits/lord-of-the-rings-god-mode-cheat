// CatalogGen: builds src\App\catalog-<game>.txt (heroes / units / buildings per skirmish faction) straight from the
// game's own INI text inside its .big archives. Development tool, not shipped. Read-only on the game folders.
//
//   CatalogGen rotwk|bfme2 <outFile>          write the catalog (also prints the --stats report)
//   CatalogGen rotwk|bfme2 --stats            counts per faction and category, dropped / odd items, checks (writes nothing)
//   CatalogGen rotwk|bfme2 --list             the catalog lines on stdout
//   CatalogGen rotwk|bfme2 --kinds            every catalog object with the KindOf flags its category came from
//   CatalogGen rotwk|bfme2 --diag             parser self-checks (block balance, duplicates, shadowed archives)
//   CatalogGen rotwk|bfme2 --obj <Name>       one object with inheritance resolved
//   CatalogGen rotwk|bfme2 --cs <CommandSet>  one command set with its buttons resolved
//   CatalogGen rotwk|bfme2 --find <regex>     objects by name
//   CatalogGen rotwk|bfme2 --str <Label>      one lotr.str label
//
// Build (C# 5, compiler that ships with Windows):
//   C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /out:CatalogGen.exe CatalogGen.cs
//
// Output format (UTF-8 without BOM, '|' separated, '#' = comment):
//   G|<game>|<patch label>|<generation date>
//   F|<FactionKey>|<display name>|<PlayerTemplate>|<Side>|<Fortress object>|<Builder object>|<SpellBookMp object>|<PurchaseScienceCommandSetMP>
//   O|<FactionKey>|<Category>|<ObjectName>|<display name>|<CommandPoints or empty>|<BuildCost or empty>|<produced at or empty>
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

static class CatalogGen
{
    // ------------------------------------------------------------------------------------------------ game setup
    class Game
    {
        public string Key, Title, Patch, Dir;
        public string[] IniArchives;   // highest priority first: the first archive that has a file name wins
        public string[] StrArchives;   // same, for data\lotr.str
    }

    static Game GetGame(string key)
    {
        if (key == "rotwk") return new Game
        {
            Key = "rotwk", Title = "The Rise of the Witch-king", Patch = "2.02 v9.8.7", Dir = @"C:\RotWK",
            // Names that sort first win. Only __patch202 / _patch201ini / ini.big hold data\ini text; the others are listed so
            // the order is complete (textures, music, models, maps - they have no data\ini entry and cannot change the result).
            IniArchives = new[] { @"C:\RotWK\___rotwkhdpack.big", @"C:\RotWK\__patch202.big", @"C:\RotWK\_202music.big", @"C:\RotWK\_patch201ini.big", @"C:\RotWK\_patch201.big", @"C:\RotWK\_patch201maps.big", @"C:\RotWK\ini.big" },
            StrArchives = new[] { @"C:\RotWK\lang\englishpatch202.big", @"C:\RotWK\lang\englishpatch201.big", @"C:\RotWK\lang\english.big" }
        };
        if (key == "bfme2") return new Game
        {
            Key = "bfme2", Title = "The Battle for Middle-earth II", Patch = "1.06", Dir = @"C:\BFME2",
            IniArchives = new[] { @"C:\BFME2\_patch103.big", @"C:\BFME2\_patch101.big", @"C:\BFME2\ini.big" },
            StrArchives = new[] { @"C:\BFME2\lang\englishpatch105.big", @"C:\BFME2\lang\english.big" }
        };
        return null;
    }

    // side (PlayerTemplate.Side) in output order; FactionKey = side, except Wild -> Goblins
    static readonly string[] SideOrder = { "Men", "Elves", "Dwarves", "Isengard", "Mordor", "Wild", "Angmar" };
    static string FactionKey(string side) { return side.Equals("Wild", StringComparison.OrdinalIgnoreCase) ? "Goblins" : side; }

    static readonly string[] CategoryOrder = { "Fortress", "Builder", "Hero", "RingHero", "Infantry", "Cavalry", "Monster", "Siege", "Ship", "Structure", "Defence", "Other" };
    static readonly string[] UnitCategories = { "Infantry", "Cavalry", "Monster", "Siege", "Ship", "Other" };

    // ------------------------------------------------------------------------------------------------ .big reading (same format code as dev\retools\BigX.cs)
    class Entry { public string Archive, Name; public long Offset; public int Size; }
    static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
    static readonly Encoding Win1252 = Encoding.GetEncoding(1252);
    static readonly StringComparer IC = StringComparer.OrdinalIgnoreCase;
    static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    static uint BE(byte[] b, int p) { return (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]); }
    static List<Entry> Index(string path)
    {
        var list = new List<Entry>(); if (!File.Exists(path)) return list;
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var h = new byte[16]; f.Read(h, 0, 16); string magic = Encoding.ASCII.GetString(h, 0, 4); if (magic != "BIGF" && magic != "BIG4") return list;
            int count = (int)BE(h, 8), hdr = (int)BE(h, 12); var idx = new byte[hdr]; f.Position = 0; f.Read(idx, 0, hdr); int p = 16;
            for (int i = 0; i < count && p + 8 < hdr; i++)
            {
                uint off = BE(idx, p), size = BE(idx, p + 4); p += 8; int e = Array.IndexOf(idx, (byte)0, p); if (e < 0) break;
                list.Add(new Entry { Archive = path, Name = Latin1.GetString(idx, p, e - p), Offset = off, Size = (int)size }); p = e + 1;
            }
        }
        return list;
    }
    static byte[] ReadEntry(Entry e) { using (var f = new FileStream(e.Archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { var b = new byte[e.Size]; f.Position = e.Offset; f.Read(b, 0, b.Length); return b; } }

    // lower-cased entry name -> entry of the highest-priority archive that has it
    static Dictionary<string, Entry> Vfs(string[] archives, List<string> notes)
    {
        var d = new Dictionary<string, Entry>(IC);
        foreach (string arc in archives)
        {
            if (!File.Exists(arc)) { notes.Add(arc + ": MISSING"); continue; }
            int used = 0, shadowed = 0, text = 0, textUsed = 0;
            foreach (Entry e in Index(arc))
            {
                string n = e.Name.Replace('/', '\\').ToLowerInvariant();
                bool isText = (n.EndsWith(".ini") || n.EndsWith(".inc") || n.EndsWith(".str")) && (n.StartsWith("data\\ini\\") || n == "data\\lotr.str");
                if (isText) text++;
                if (d.ContainsKey(n)) { shadowed++; continue; }
                d[n] = e; used++; if (isText) textUsed++;
            }
            notes.Add(string.Format("{0}: {1} entries, {2} shadowed by a higher archive; data\\ini + lotr.str text files: {3}, of which used: {4}", arc, used + shadowed, shadowed, text, textUsed));
        }
        return d;
    }

    // ------------------------------------------------------------------------------------------------ INI text layer
    class Ln { public string Text, File; public int No; public string[] Tok; public bool HasEq; }

    static Dictionary<string, Entry> ini;                                   // data\ini\... entries
    static readonly Dictionary<string, string> Macros = new Dictionary<string, string>(IC);
    static readonly Dictionary<string, string> MacroFile = new Dictionary<string, string>(IC);
    static readonly List<string> MacroConflicts = new List<string>();
    static readonly List<string> Warnings = new List<string>();
    static readonly Dictionary<string, List<Ln>> LineCache = new Dictionary<string, List<Ln>>(IC);
    static readonly char[] Ws = { ' ', '\t', '=', '\r' };          // the engine's tokenizer treats '=' as white space
    static readonly char[] Sp = { ' ', '\t' };

    static string StripComment(string s)
    {
        int a = s.IndexOf(';'), b = s.IndexOf("//", StringComparison.Ordinal), c = -1;
        if (a >= 0) c = a; if (b >= 0 && (c < 0 || b < c)) c = b;
        return (c >= 0 ? s.Substring(0, c) : s).Trim();
    }

    static string Combine(string baseFile, string rel)
    {
        var parts = new List<string>(baseFile.Split('\\')); parts.RemoveAt(parts.Count - 1);
        foreach (string p in rel.Replace('/', '\\').Split('\\'))
        {
            if (p.Length == 0 || p == ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(p);
        }
        return string.Join("\\", parts.ToArray()).ToLowerInvariant();
    }

    // one file: comments removed, #include expanded in place, #define collected
    static List<Ln> Lines(string name, int guard)
    {
        List<Ln> res; name = name.ToLowerInvariant();
        if (LineCache.TryGetValue(name, out res)) return res;
        res = new List<Ln>(); LineCache[name] = res;
        Entry e; if (!ini.TryGetValue(name, out e)) { Warnings.Add("missing file: " + name); return res; }
        string[] raw = Latin1.GetString(ReadEntry(e)).Split('\n');
        for (int i = 0; i < raw.Length; i++)
        {
            string s = StripComment(raw[i]); if (s.Length == 0) continue;
            if (s[0] == '#')
            {
                if (s.StartsWith("#include", StringComparison.OrdinalIgnoreCase))
                {
                    int q1 = s.IndexOf('"'), q2 = s.LastIndexOf('"');
                    if (q1 < 0 || q2 <= q1) { Warnings.Add("bad include in " + name + ":" + (i + 1)); continue; }
                    string target = Combine(name, s.Substring(q1 + 1, q2 - q1 - 1));
                    if (guard > 12) { Warnings.Add("include too deep: " + target); continue; }
                    if (!ini.ContainsKey(target)) { Warnings.Add("include not found: " + target + " (from " + name + ":" + (i + 1) + ")"); continue; }
                    res.AddRange(Lines(target, guard + 1));
                    continue;
                }
                if (s.StartsWith("#define", StringComparison.OrdinalIgnoreCase))
                {
                    string[] t = s.Split(Sp, 3, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length >= 2)
                    {
                        string val = t.Length > 2 ? t[2].Trim() : "", old;
                        if (!Macros.TryGetValue(t[1], out old)) { Macros[t[1]] = val; MacroFile[t[1]] = name; }
                        else if (old != val) MacroConflicts.Add(t[1] + ": '" + old + "' (" + MacroFile[t[1]] + ", used) vs '" + val + "' (" + name + ")");
                    }
                    continue;
                }
            }
            res.Add(new Ln { Text = s, File = name, No = i + 1, Tok = s.Split(Ws, StringSplitOptions.RemoveEmptyEntries), HasEq = s.IndexOf('=') >= 0 });
        }
        return res;
    }

    static string Val(Ln l) { return string.Join(" ", l.Tok, 1, l.Tok.Length - 1); }
    static string First(string v) { if (string.IsNullOrEmpty(v)) return ""; string[] t = v.Split(Sp, StringSplitOptions.RemoveEmptyEntries); return t.Length > 0 ? t[0] : ""; }
    static string[] Words(string v) { return (v ?? "").Split(Sp, StringSplitOptions.RemoveEmptyEntries); }

    // ---- macro arithmetic: NAME, 123, #ADD( a b ), #SUBTRACT( a b ), #MULTIPLY( a b ), #DIVIDE( a b )
    static readonly HashSet<string> MacrosUsed = new HashSet<string>(IC);
    static bool TryNumber(string expr, out double v)
    {
        v = 0; if (string.IsNullOrEmpty(expr)) return false;
        var t = new List<string>(expr.Replace("(", " ( ").Replace(")", " ) ").Split(Sp, StringSplitOptions.RemoveEmptyEntries));
        int i = 0; if (!Eval(t, ref i, 0, out v)) return false;
        return i == t.Count;
    }
    static bool Eval(List<string> t, ref int i, int guard, out double v)
    {
        v = 0; if (i >= t.Count || guard > 40) return false;
        string tok = t[i++];
        if (tok[0] == '#')
        {
            string op = tok.ToUpperInvariant(); if (i >= t.Count || t[i] != "(") return false; i++;
            var args = new List<double>();
            while (i < t.Count && t[i] != ")") { double a; if (!Eval(t, ref i, guard + 1, out a)) return false; args.Add(a); }
            if (i >= t.Count) return false; i++;
            if (args.Count < 2) return false;
            v = args[0];
            for (int k = 1; k < args.Count; k++)
            {
                if (op == "#ADD") v += args[k]; else if (op == "#SUBTRACT") v -= args[k]; else if (op == "#MULTIPLY") v *= args[k];
                else if (op == "#DIVIDE") { if (args[k] == 0) return false; v /= args[k]; } else return false;
            }
            return true;
        }
        if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return true;
        string def; if (!Macros.TryGetValue(tok, out def)) return false;
        MacrosUsed.Add(tok);
        return TryNumber(def, out v);
    }
    static string Num(string expr)
    {
        double v; if (expr == null || !TryNumber(expr.Trim(), out v)) return "";
        return Math.Abs(v - Math.Round(v)) < 1e-6 ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
    }
    // macro that expands to text (e.g. a KindOf list)
    static string Expand(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        string[] t = Words(value);
        for (int i = 0; i < t.Length; i++) { string def; int g = 0; while (g++ < 8 && Macros.TryGetValue(t[i], out def)) t[i] = def; }
        return string.Join(" ", t);
    }

    // ------------------------------------------------------------------------------------------------ objects
    class Module { public string Kind, Class, Tag; public List<KeyValuePair<string, string>> Props = new List<KeyValuePair<string, string>>(); }
    class Obj
    {
        public string Name, Parent, File, Form; public int Line;
        public Dictionary<string, string> Props = new Dictionary<string, string>(IC);   // depth-1 keys, last one wins
        public List<Module> Modules = new List<Module>();
        public HashSet<string> RemovedTags = new HashSet<string>(IC);
    }
    static readonly Dictionary<string, Obj> Objects = new Dictionary<string, Obj>(IC);
    static readonly List<string> DuplicateObjects = new List<string>();
    static readonly List<string> ParseAnomalies = new List<string>();

    static readonly HashSet<string> ModuleKeys = new HashSet<string>(IC) { "Behavior", "Draw", "Body", "ClientUpdate", "ClientBehavior" };
    // keys that open a nested block (closed by End) inside an Object; checked by --diag (every Object must close exactly at its own End)
    static readonly HashSet<string> Openers = new HashSet<string>(IC) {
        "Behavior", "Draw", "Body", "ClientUpdate", "ClientBehavior",
        "DefaultModelConditionState", "IdleAnimationState", "ModelConditionState", "AnimationState", "TransitionState", "Animation",
        "ArmorSet", "WeaponSet", "LocomotorSet", "UnitSpecificSounds", "UnitSpecificFX", "ThreatBreakdown",
        "AutoResolveArmor", "AutoResolveWeapon", "ProductionModifier", "Flammability", "FloodMember",
        "DecalTemplate", "FormationPreviewDecal", "FormationPreviewItemDecal", "DynamicDecal", "GridDecalTemplate",
        "ReplaceModule", "AddModule", "InheritableModule", "LodOptions", "MeleeBehavior", "SoundState", "SoundUpgrade" };
    static bool IsOpener(Ln l)
    {
        string k = l.Tok[0];
        if (Openers.Contains(k)) return true;
        if (k.EndsWith("Nugget", StringComparison.OrdinalIgnoreCase)) return true;
        if (Eq(k, "Turret") || Eq(k, "AltTurret") || Eq(k, "ReplaceObject")) return l.Tok.Length == 1;   // "Turret = BONE" is a plain key
        if (Eq(k, "AddEmotion")) return l.Tok.Length > 1 && Eq(l.Tok[1], "OVERRIDE");
        if (l.Tok.Length == 2 && Eq(l.Tok[1], "FCurve")) return true;
        return false;
    }
    static bool IsObjectStart(Ln l)
    {
        if (l.HasEq || l.Tok.Length < 2) return false;
        string k = l.Tok[0];
        return Eq(k, "Object") || Eq(k, "ChildObject") || Eq(k, "ObjectReskin");
    }

    static void ParseObjects(string file)
    {
        List<Ln> lines = Lines(file, 0);
        Obj cur = null; int depth = 0; bool script = false; Module mod = null; int modDepth = 0; int trailing = 0; Obj last = null; string firstTrailing = null;
        for (int i = 0; i < lines.Count; i++)
        {
            Ln l = lines[i]; string k = l.Tok[0];
            if (script) { if (Eq(k, "EndScript")) script = false; continue; }
            if (Eq(k, "BeginScript")) { script = true; continue; }
            if (IsObjectStart(l))
            {
                if (cur != null) ParseAnomalies.Add(string.Format("unterminated {0} (depth {1}) before {2}:{3}", cur.Name, depth, l.File, l.No));
                if (last != null && trailing > 0) ParseAnomalies.Add(string.Format("{0} line(s) after the End of {1} ({2}) - first: {3}", trailing, last.Name, last.File, firstTrailing));
                cur = new Obj { Name = l.Tok[1], Parent = Eq(l.Tok[0], "Object") ? null : (l.Tok.Length > 2 ? l.Tok[2] : null), File = l.File, Line = l.No, Form = l.Tok[0] };
                depth = 1; mod = null; trailing = 0; last = null; firstTrailing = null;
                if (Objects.ContainsKey(cur.Name)) DuplicateObjects.Add(cur.Name + " (" + Objects[cur.Name].File + ":" + Objects[cur.Name].Line + " kept, " + l.File + ":" + l.No + " ignored)");
                else Objects[cur.Name] = cur;
                continue;
            }
            if (cur == null) { if (last != null) { trailing++; if (firstTrailing == null) firstTrailing = l.File + ":" + l.No + " " + l.Text; } continue; }
            if (Eq(k, "End"))
            {
                depth--;
                if (mod != null && depth < modDepth) mod = null;
                if (depth == 0) { last = cur; cur = null; }
                continue;
            }
            string v = Val(l);
            if (depth == 1)
            {
                if (Eq(k, "RemoveModule")) { if (l.Tok.Length > 1) cur.RemovedTags.Add(l.Tok[1]); continue; }
                if (Eq(k, "ReplaceModule")) { if (l.Tok.Length > 1) cur.RemovedTags.Add(l.Tok[1]); }
                else if (!IsOpener(l)) cur.Props[k] = v;
            }
            if (mod != null) mod.Props.Add(new KeyValuePair<string, string>(k, v));
            if (IsOpener(l))
            {
                if (mod == null && ModuleKeys.Contains(k) && depth <= 2)
                {
                    mod = new Module { Kind = k, Class = l.Tok.Length > 1 ? l.Tok[1] : "", Tag = l.Tok.Length > 2 ? l.Tok[2] : "" };
                    modDepth = depth + 1; cur.Modules.Add(mod);
                }
                depth++;
            }
        }
        if (cur != null) ParseAnomalies.Add(string.Format("unterminated {0} (depth {1}) at end of {2}", cur.Name, depth, file));
    }

    static Obj Find(string name) { Obj o; return !string.IsNullOrEmpty(name) && Objects.TryGetValue(name, out o) ? o : null; }

    // a child inherits every key it does not set itself
    static string Prop(Obj o, string key)
    {
        for (int g = 0; o != null && g < 20; g++) { string v; if (o.Props.TryGetValue(key, out v)) return v; o = Find(o.Parent); }
        return null;
    }
    static List<Module> Mods(Obj o) { return Mods(o, 0); }
    static List<Module> Mods(Obj o, int guard)
    {
        var res = new List<Module>(); if (o == null || guard > 20) return res;
        Obj p = Find(o.Parent);
        if (p != null)
        {
            var own = new HashSet<string>(o.Modules.Where(m => m.Tag.Length > 0).Select(m => m.Tag), IC);
            foreach (Module m in Mods(p, guard + 1)) if (!o.RemovedTags.Contains(m.Tag) && !own.Contains(m.Tag)) res.Add(m);
        }
        res.AddRange(o.Modules);
        return res;
    }
    // KindOf with inheritance: a child either replaces the list or patches the parent's with +FLAG / -FLAG
    static HashSet<string> KindOf(Obj o) { return KindOf(o, 0); }
    static HashSet<string> KindOf(Obj o, int guard)
    {
        var res = new HashSet<string>(IC); if (o == null || guard > 20) return res;
        string v; if (!o.Props.TryGetValue("KindOf", out v)) return KindOf(Find(o.Parent), guard + 1);
        string[] t = Words(Expand(v));
        bool patch = t.Length > 0 && t.All(x => x[0] == '+' || x[0] == '-');
        if (!patch) { foreach (string x in t) res.Add(x); return res; }
        res = KindOf(Find(o.Parent), guard + 1);
        foreach (string x in t) { if (x[0] == '+') res.Add(x.Substring(1)); else res.Remove(x.Substring(1)); }
        return res;
    }
    // every command set an object can show: its own CommandSet plus the ones its behaviours switch to
    // (CommandSetUpgrade: CommandSet / CommandSetAlt, MonitorConditionUpdate: ModelConditionCommandSet)
    static bool IsCommandSetKey(string k) { return Eq(k, "CommandSet") || Eq(k, "CommandSetAlt") || Eq(k, "ModelConditionCommandSet"); }
    static List<string> CommandSetsOf(Obj o)
    {
        var res = new List<string>(); string cs = First(Prop(o, "CommandSet"));
        if (cs.Length > 0) res.Add(cs);
        foreach (Module m in Mods(o))
            foreach (var kv in m.Props)
                if (IsCommandSetKey(kv.Key)) { string n = First(kv.Value); if (n.Length > 0 && !res.Contains(n, IC)) res.Add(n); }
        return res;
    }
    // members of a horde: (object, count)
    static List<KeyValuePair<string, int>> Payload(Obj o)
    {
        var res = new List<KeyValuePair<string, int>>();
        foreach (Module m in Mods(o))
        {
            if (!Eq(m.Class, "HordeContain")) continue;
            foreach (var kv in m.Props)
            {
                if (!Eq(kv.Key, "InitialPayload")) continue;
                string[] t = Words(kv.Value); if (t.Length == 0) continue;
                int n = 1; if (t.Length > 1) int.TryParse(Num(t[1]), out n);
                res.Add(new KeyValuePair<string, int>(t[0], n));
            }
        }
        return res;
    }

    // ------------------------------------------------------------------------------------------------ command sets / buttons / templates / strings
    class CmdSet { public string Name; public SortedDictionary<int, string> Slots = new SortedDictionary<int, string>(); }
    static readonly Dictionary<string, CmdSet> CommandSets = new Dictionary<string, CmdSet>(IC);
    static readonly Dictionary<string, Dictionary<string, string>> Buttons = new Dictionary<string, Dictionary<string, string>>(IC);
    static readonly List<Dictionary<string, List<string>>> Templates = new List<Dictionary<string, List<string>>>();
    static readonly Dictionary<string, string> Strings = new Dictionary<string, string>(IC);
    static readonly List<string> DuplicateDefs = new List<string>();

    static void ParseCommandSets(string file)
    {
        CmdSet cur = null;
        foreach (Ln l in Lines(file, 0))
        {
            string k = l.Tok[0];
            if (Eq(k, "CommandSet") && !l.HasEq && l.Tok.Length > 1)
            {
                cur = new CmdSet { Name = l.Tok[1] };
                if (CommandSets.ContainsKey(cur.Name)) DuplicateDefs.Add("CommandSet " + cur.Name + " defined again at " + l.File + ":" + l.No + " (last definition used)");
                CommandSets[cur.Name] = cur; continue;
            }
            if (cur == null) continue;
            if (Eq(k, "End")) { cur = null; continue; }
            int slot; if (int.TryParse(k, out slot) && l.Tok.Length > 1) cur.Slots[slot] = l.Tok[1];
        }
    }
    static void ParseButtons(string file)
    {
        Dictionary<string, string> cur = null;
        foreach (Ln l in Lines(file, 0))
        {
            string k = l.Tok[0];
            if (Eq(k, "CommandButton") && !l.HasEq && l.Tok.Length > 1)
            {
                cur = new Dictionary<string, string>(IC);
                if (Buttons.ContainsKey(l.Tok[1])) DuplicateDefs.Add("CommandButton " + l.Tok[1] + " defined again at " + l.File + ":" + l.No + " (last definition used)");
                Buttons[l.Tok[1]] = cur; continue;
            }
            if (cur == null) continue;
            if (Eq(k, "End")) { cur = null; continue; }
            cur[k] = Val(l);
        }
    }
    static void ParseTemplates(string file)
    {
        Dictionary<string, List<string>> cur = null;
        foreach (Ln l in Lines(file, 0))
        {
            string k = l.Tok[0];
            if (Eq(k, "PlayerTemplate") && !l.HasEq && l.Tok.Length > 1)
            { cur = new Dictionary<string, List<string>>(IC); cur["__name"] = new List<string> { l.Tok[1] }; Templates.Add(cur); continue; }
            if (cur == null) continue;
            if (Eq(k, "End")) { cur = null; continue; }
            List<string> list; if (!cur.TryGetValue(k, out list)) { list = new List<string>(); cur[k] = list; }
            list.Add(Val(l));
        }
    }
    static string T1(Dictionary<string, List<string>> t, string key) { List<string> l; return t.TryGetValue(key, out l) && l.Count > 0 ? l[l.Count - 1] : ""; }

    static string strSource = "";
    static void LoadStrings(Game g, List<string> notes)
    {
        Dictionary<string, Entry> v = Vfs(g.StrArchives, notes); Entry e;
        if (!v.TryGetValue(@"data\lotr.str", out e)) { Warnings.Add("data\\lotr.str not found"); return; }
        strSource = e.Archive;
        string label = null; StringBuilder sb = null;
        foreach (string raw in Win1252.GetString(ReadEntry(e)).Split('\n'))
        {
            string t = raw.Trim(); if (t.Length == 0 || t.StartsWith("//")) continue;
            if (label == null) { label = t; sb = new StringBuilder(); continue; }
            if (Eq(t, "END")) { if (!Strings.ContainsKey(label)) Strings[label] = sb.ToString(); label = null; continue; }
            int q1 = t.IndexOf('"'), q2 = t.LastIndexOf('"');
            if (q1 >= 0 && q2 > q1) sb.Append(t.Substring(q1 + 1, q2 - q1 - 1));
        }
    }
    static string Clean(string s)
    {
        if (s == null) return null;
        s = s.Replace("\\n", " ").Replace("|", "/").Replace("&", "").Replace('\t', ' ');
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
    static string SplitWords(string name)
    {
        string s = name.Replace('_', ' ');
        s = Regex.Replace(s, @"(?<=[a-z0-9])(?=[A-Z])", " ");
        s = Regex.Replace(s, @"(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
    static readonly List<string> NameFallbacks = new List<string>();
    static string Display(Obj o)
    {
        string label = First(Prop(o, "DisplayName")), text;
        if (label.Length > 0 && Strings.TryGetValue(label, out text) && Clean(text).Length > 0) return Clean(text);
        string note = o.Name + " (DisplayName = " + (label.Length == 0 ? "none" : label + ", not in lotr.str") + ") -> \"" + SplitWords(o.Name) + "\"";
        if (!NameFallbacks.Contains(note)) NameFallbacks.Add(note);
        return SplitWords(o.Name);
    }

    // ------------------------------------------------------------------------------------------------ loading
    static readonly List<string> ArchiveNotes = new List<string>();
    static void Load(Game g)
    {
        ini = Vfs(g.IniArchives, ArchiveNotes);
        // macros first (gamedata.ini), then everything else under data\ini
        Lines(@"data\ini\gamedata.ini", 0);
        var objFiles = new List<string>();
        foreach (string n in ini.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!n.StartsWith(@"data\ini\") || !n.EndsWith(".ini")) continue;
            Lines(n, 0);
            if (n.StartsWith(@"data\ini\object\")) objFiles.Add(n);
        }
        foreach (string f in objFiles) ParseObjects(f);
        ParseCommandSets(@"data\ini\commandset.ini");
        ParseButtons(@"data\ini\commandbutton.ini");
        ParseTemplates(@"data\ini\playertemplate.ini");
        LoadStrings(g, ArchiveNotes);
        // patch-like archives in the game folder that this tool does not know about
        foreach (string dir in new[] { g.Dir, Path.Combine(g.Dir, "lang") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string f in Directory.GetFiles(dir, "*.big"))
            {
                string n = Path.GetFileName(f);
                bool known = g.IniArchives.Concat(g.StrArchives).Any(a => Eq(a, f));
                if (!known && (n[0] == '_' || n[0] == '!' || n.ToLowerInvariant().Contains("patch"))) Warnings.Add("UNKNOWN PATCH ARCHIVE (not in the priority list of this tool): " + f);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ catalog
    class Rec { public string Category, Name, Display, CP, Cost, At; }
    class Faction
    {
        public string Key, Display, Template, Side, Fortress, Builder, SpellBook, ScienceSet, Keep = "";
        public string[] InitialUpgrades = new string[0];
        public Obj BuilderObj;
        public List<string> Pads = new List<string>();
        public List<Rec> Recs = new List<Rec>();
        public HashSet<string> Seen = new HashSet<string>(IC);
    }
    static readonly List<string> Dropped = new List<string>();
    static readonly List<string> Odd = new List<string>();
    static void Note(string s) { if (!Odd.Contains(s)) Odd.Add(s); }

    static Dictionary<string, string> Button(string name) { Dictionary<string, string> b; return name != null && Buttons.TryGetValue(name, out b) ? b : null; }

    // { button, command type, object, command set } for every slot of one command set, in slot order
    static List<string[]> ButtonsOfSet(string csName, string owner)
    {
        var res = new List<string[]>();
        CmdSet cs; if (!CommandSets.TryGetValue(csName, out cs)) { if (owner != null) Note(owner + ": command set " + csName + " is not defined in commandset.ini"); return res; }
        foreach (var slot in cs.Slots)
        {
            Dictionary<string, string> b = Button(slot.Value); if (b == null) { if (owner != null) Note(owner + ": command set " + csName + " slot " + slot.Key + " names the missing button " + slot.Value); continue; }
            string cmd, obj; b.TryGetValue("Command", out cmd); b.TryGetValue("Object", out obj);
            res.Add(new[] { slot.Value, First(cmd), First(obj), csName });
        }
        return res;
    }
    static List<string[]> ButtonsOf(Obj o) { return ButtonsOf(o, false); }
    static List<string[]> ButtonsOf(Obj o, bool quiet)
    {
        var res = new List<string[]>();
        foreach (string csName in CommandSetsOf(o)) res.AddRange(ButtonsOfSet(csName, quiet ? null : o.Name));
        return res;
    }
    // a real builder: DOZER flag and at least one DOZER_CONSTRUCT button (peasants and lumber-mill labourers carry DOZER too)
    static bool CanConstruct(Obj o)
    {
        if (!KindOf(o).Contains("DOZER")) return false;
        return ButtonsOf(o, true).Any(b => b[1] == "DOZER_CONSTRUCT");
    }

    // own KindOf flags plus the flags of the horde members
    static HashSet<string> KindsWithMembers(Obj o)
    {
        var all = new HashSet<string>(KindOf(o), IC);
        foreach (var p in Payload(o)) { Obj m = Find(p.Key); if (m != null) all.UnionWith(KindOf(m)); }
        return all;
    }
    // Category of a trainable object from its KindOf flags (and those of its horde members), first match wins:
    //   DOZER with DOZER_CONSTRUCT buttons -> Builder; SHIP -> Ship; HARVESTER (lumber mill labourer) -> Other;
    //   MACHINE without CAVALRY, or MINE -> Siege (trebuchet, catapult, ballista, ram, demolisher, troll stone thrower, explosive mine);
    //   MONSTER / HORDE_MONSTER -> Monster (trolls, ents, mumakil, giants, drakes; also the troll hordes that carry INFANTRY or CAVALRY);
    //   CAVALRY -> Cavalry (includes the Dwarven battle wagon, which is CAVALRY + MACHINE); remaining MACHINE -> Siege;
    //   INFANTRY / ARCHER -> Infantry; anything else -> Other.
    static string UnitCategory(Obj o)
    {
        HashSet<string> all = KindsWithMembers(o);
        if (CanConstruct(o)) return "Builder";
        if (all.Contains("SHIP")) return "Ship";
        if (all.Contains("HARVESTER")) return "Other";
        if ((all.Contains("MACHINE") && !all.Contains("CAVALRY")) || all.Contains("MINE")) return "Siege";
        if (all.Contains("MONSTER") || all.Contains("HORDE_MONSTER")) return "Monster";
        if (all.Contains("CAVALRY")) return "Cavalry";
        if (all.Contains("MACHINE")) return "Siege";
        if (all.Contains("INFANTRY") || all.Contains("ARCHER")) return "Infantry";
        return "Other";
    }

    // KindOf flags that make a structure a defence rather than a plain building
    static readonly string[] DefenceFlags = { "FS_BASE_DEFENSE", "WALL_HUB", "WALL_SEGMENT", "WALL_UPGRADE", "WALL_GATE", "DEFENSIVE_WALL" };
    static string StructCategory(Obj o)
    {
        HashSet<string> k = KindOf(o);
        foreach (string d in DefenceFlags) if (k.Contains(d)) return "Defence";
        return "Structure";
    }

    // Templates that never go into the catalog, whatever refers to them:
    //   create-a-hero templates (KindOf CREATE_A_HERO or "CreateAHero" in the name) - the engine builds them from a hero profile;
    //   wall pieces (WALL_SEGMENT / DEFENSIVE_WALL / WALL_GATE: segments, cliff caps, gates) - laid out by a wall hub, not owned on their own;
    //   things a player does not own as an object (projectiles, inert props, spell books).
    static readonly List<string> Excluded = new List<string>();
    static string ExcludeReason(Obj o)
    {
        HashSet<string> k = KindOf(o);
        if (k.Contains("CREATE_A_HERO") || o.Name.IndexOf("CreateAHero", StringComparison.OrdinalIgnoreCase) >= 0) return "create-a-hero template";
        if (k.Contains("WALL_SEGMENT") || k.Contains("DEFENSIVE_WALL") || k.Contains("WALL_GATE")) return "wall piece (segment / cap / gate)";
        if (k.Contains("PROJECTILE") || k.Contains("INERT") || k.Contains("SPELL_BOOK") || k.Contains("CLEARED_BY_BUILD") || k.Contains("OPTIMIZED_PROP")) return "not a player-owned object (projectile / prop / spell book)";
        return null;
    }
    static Rec Add(Faction f, string cat, Obj o, string at)
    {
        if (f.Seen.Contains(o.Name)) return null;
        string why = ExcludeReason(o);
        if (why != null) { string s = f.Key + ": " + o.Name + " - " + why; if (!Excluded.Contains(s)) Excluded.Add(s); return null; }
        f.Seen.Add(o.Name);
        var r = new Rec { Category = cat, Name = o.Name, Display = Display(o), CP = Num(Prop(o, "CommandPoints")), Cost = Num(Prop(o, "BuildCost")), At = at ?? "" };
        f.Recs.Add(r); return r;
    }
    static void Drop(Faction f, string name, string why)
    {
        string s = f.Key + ": " + name + " - " + why; if (!Dropped.Contains(s)) Dropped.Add(s);
    }

    // buttons that put a new structure on the map: porter builds, foundation / plot builds, expansion "upgrades" that name an object
    static readonly string[] StructureCommands = { "DOZER_CONSTRUCT", "FOUNDATION_CONSTRUCT", "OBJECT_UPGRADE", "CASTLE_UNPACK_EXPLICIT_OBJECT" };
    static readonly List<string> SkippedTemplates = new List<string>();
    static bool CreateAHeroListed = false;

    static List<Faction> Build(Game g)
    {
        var res = new List<Faction>();
        foreach (string side in SideOrder)
            foreach (var t in Templates)
                if (Eq(T1(t, "Side"), side) && Eq(T1(t, "PlayableSide"), "Yes"))
                    res.Add(BuildFaction(t));
        foreach (var t in Templates)
        {
            string side = T1(t, "Side"), name = T1(t, "__name");
            if (Eq(T1(t, "PlayableSide"), "Yes")) { if (!SideOrder.Contains(side, IC)) Note("playable PlayerTemplate with an unknown side was skipped: " + name + " (Side " + side + ")"); }
            else SkippedTemplates.Add(name + " (Side " + side + ")");
        }

        // "Other": real objects that belong to no single playable faction
        var other = new Faction { Key = "Other", Display = "Other", Template = "", Side = "", Fortress = "", Builder = "", SpellBook = "", ScienceSet = "" };

        // Capturable neutral buildings (Side = Neutral; the Civilian look-alikes are campaign / map-specific): a CommandSetUpgrade
        // triggered by a faction's InitialUpgrades gives that faction a production command set there (inn, shipwright).
        // A module triggered by every playable faction (creep lairs) goes to Other instead.
        // A neutral building is listed (under Other) only when it adds at least one unit that no earlier building gave, so
        // look-alike variants (snow lairs, ShipWright_SP, a campaign bridge that reuses the inn command sets) stay out;
        // the standard flag-linked skirmish buildings are looked at first.
        foreach (Obj n in Objects.Values.OrderBy(o => KindOf(o).Contains("LINKED_TO_FLAG") ? 0 : 1).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!Eq(Prop(n, "Side") ?? "", "Neutral") || !KindOf(n).Contains("STRUCTURE")) continue;
            foreach (Module m in Mods(n))
            {
                if (!Eq(m.Class, "CommandSetUpgrade")) continue;
                var trig = new List<string>(); var sets = new List<string>();
                foreach (var kv in m.Props)
                {
                    if (Eq(kv.Key, "TriggeredBy")) trig.AddRange(Words(kv.Value));
                    if (IsCommandSetKey(kv.Key) && First(kv.Value).Length > 0) sets.Add(First(kv.Value));
                }
                List<Faction> users = res.Where(f => f.InitialUpgrades.Intersect(trig, IC).Any()).ToList();
                if (users.Count == 0) continue;
                bool universal = users.Count == res.Count && res.Count > 1;
                foreach (string csName in sets)
                {
                    List<string[]> btns = ButtonsOfSet(csName, n.Name);
                    if (!btns.Any(b => b[1] == "UNIT_BUILD")) continue;
                    int before = res.Sum(f => f.Recs.Count) + other.Recs.Count;
                    if (universal) AddUnits(other, n, btns, null);
                    else foreach (Faction f in users) AddUnits(f, n, btns, f.BuilderObj);
                    if (res.Sum(f => f.Recs.Count) + other.Recs.Count == before) continue;
                    Add(other, StructCategory(n), n, "");
                    if (universal) Note("Other: " + n.Name + " switches to " + csName + " for every playable faction (whoever owns the lair can train there); its units are listed once under Other, not under each faction");
                    else Note("Other: " + n.Name + " is a capturable neutral building whose command set depends on the owner's faction upgrade; its units are listed under the factions that get them (produced at " + n.Name + ")");
                }
            }
        }
        if (other.Recs.Count > 0) res.Add(other);
        foreach (Faction f in res) f.Recs = f.Recs.OrderBy(r => Array.IndexOf(CategoryOrder, r.Category)).ToList();   // stable: keeps button order inside a category
        return res;
    }

    static List<string> StartingUnits(Dictionary<string, List<string>> t)
    {
        var res = new SortedDictionary<int, string>();
        foreach (var kv in t)
        {
            Match m = Regex.Match(kv.Key, @"^StartingUnit(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success && kv.Value.Count > 0) res[int.Parse(m.Groups[1].Value)] = First(kv.Value[kv.Value.Count - 1]);
        }
        return res.Values.ToList();
    }

    // The keep: the CASTLE_KEEP object of the same side that trains the faction's builder. The StartingBuilding itself is only
    // the base foundation - it unpacks a base template from bases.big (keep + expansion pads) and has no command set of its own.
    static Obj FindKeep(Faction f, Obj fort, Obj builder)
    {
        var cands = new List<Obj>();
        foreach (Obj o in Objects.Values)
        {
            if (!KindOf(o).Contains("CASTLE_KEEP")) continue;
            if (!Eq(f.Side, Prop(o, "Side") ?? "")) continue;
            if (builder != null && !ButtonsOf(o, true).Any(b => b[1] == "UNIT_BUILD" && Eq(b[2], builder.Name))) continue;
            cands.Add(o);
        }
        if (cands.Count == 0) return null;
        string fortFile = fort != null ? fort.File : "";
        Obj best = cands.OrderBy(o => o.Name.StartsWith(f.Fortress, StringComparison.OrdinalIgnoreCase) ? 0 : 1)   // MenFortressCitadel for MenFortress
                        .ThenBy(o => Eq(o.File, fortFile) ? 0 : 1)                                                    // defined next to the fortress
                        .ThenBy(o => o.Parent == null ? 0 : 1)                                                         // the base object, not a variant
                        .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).First();
        if (cands.Count > 1) Note(f.Key + ": several CASTLE_KEEP objects train " + (builder != null ? builder.Name : "?") + " (" + string.Join(", ", cands.Select(c => c.Name).OrderBy(x => x).ToArray()) + "); used " + best.Name + ", the others are campaign / camp / summoned variants");
        return best;
    }

    static void AddHeroes(Faction f, string list, string cat, string home)
    {
        string key = cat == "Hero" ? "BuildableHeroesMP" : "BuildableRingHeroesMP";
        var seen = new HashSet<string>(IC);
        foreach (string n in Words(list))
        {
            if (!seen.Add(n)) { Note(f.Key + ": " + n + " is listed more than once in " + key + " (kept once)"); continue; }
            Obj o = Find(n); if (o == null) { Drop(f, n, "named in " + key + " but no Object/ChildObject block has that name"); continue; }
            if (ExcludeReason(o) != null) { CreateAHeroListed = true; Drop(f, n, "in " + key + " but excluded: " + ExcludeReason(o)); continue; }
            if (Add(f, cat, o, home) == null) continue;
            if (o.Name != n) Note(f.Key + ": " + key + " spells " + n + ", the template is declared as " + o.Name + " (catalog uses the declared spelling)");
            if (o.Parent != null) Note(f.Key + ": skirmish " + (cat == "RingHero" ? "ring hero " : "hero ") + o.Name + " is a " + o.Form + " of " + o.Parent);
            if (!KindOf(o).Contains("HERO")) Note(f.Key + ": " + o.Name + " is in " + key + " but has no HERO KindOf");
        }
    }

    // one trainable object, plus what it turns into: BuildVariations (random variant picked when built) and the hordes a
    // unit replaces itself with (SummonReplacementSpecialAbilityUpdate.MountedTemplate - Angmar thrall master)
    static void AddUnit(Faction f, Obj o, string at, Obj builder, string how)
    {
        if (f.Seen.Contains(o.Name)) return;
        HashSet<string> k = KindOf(o);
        string cat;
        if (builder != null && o == builder) cat = "Builder";
        else if (k.Contains("STRUCTURE")) cat = StructCategory(o);
        else if (k.Contains("HERO")) { cat = "Hero"; Note(f.Key + ": " + o.Name + " is a HERO that is " + how + " (not in BuildableHeroesMP); listed as Hero"); }
        else cat = UnitCategory(o);
        Add(f, cat, o, at);
        foreach (string v in Words(Prop(o, "BuildVariations")))
        {
            Obj vo = Find(v); if (vo == null) { Drop(f, v, "BuildVariations of " + o.Name + " names an object that does not exist"); continue; }
            if (!f.Seen.Contains(vo.Name)) Note(f.Key + ": " + o.Name + " is a generic object, the game replaces it with one of its BuildVariations when built; " + vo.Name + " listed as well");
            AddUnit(f, vo, at, builder, "a BuildVariations entry of " + o.Name);
        }
        foreach (Module m in Mods(o))
        {
            if (!Eq(m.Class, "SummonReplacementSpecialAbilityUpdate")) continue;
            foreach (var kv in m.Props)
            {
                if (!Eq(kv.Key, "MountedTemplate")) continue;
                Obj mo = Find(First(kv.Value)); if (mo == null) { Drop(f, First(kv.Value), "MountedTemplate of " + o.Name + " names an object that does not exist"); continue; }
                if (!f.Seen.Contains(mo.Name)) Note(f.Key + ": " + mo.Name + " is not trained at a building - " + o.Name + " replaces itself with it (" + m.Class + "); listed with produced-at " + o.Name);
                AddUnit(f, mo, o.Name, builder, "summoned by " + o.Name);
            }
        }
    }

    static void AddUnits(Faction f, Obj producer, IEnumerable<string[]> buttons, Obj builder)
    {
        foreach (string[] b in buttons)
        {
            if (b[1] != "UNIT_BUILD" || b[2].Length == 0) continue;
            Obj o = Find(b[2]);
            if (o == null) { Drop(f, b[2], "UNIT_BUILD button " + b[0] + " in " + b[3] + " (" + producer.Name + ") names an object that does not exist"); continue; }
            AddUnit(f, o, producer.Name, builder, "trained by the UNIT_BUILD button " + b[0] + " at " + producer.Name);
        }
    }

    static Faction BuildFaction(Dictionary<string, List<string>> t)
    {
        var f = new Faction { Template = T1(t, "__name"), Side = T1(t, "Side") };
        f.Key = FactionKey(f.Side);
        string label = First(T1(t, "DisplayName")), text;
        f.Display = Strings.TryGetValue(label, out text) ? Clean(text) : f.Key;
        f.Fortress = First(T1(t, "StartingBuilding")); f.SpellBook = First(T1(t, "SpellBookMp")); f.ScienceSet = First(T1(t, "PurchaseScienceCommandSetMP"));
        f.InitialUpgrades = Words(T1(t, "InitialUpgrades"));
        if (f.SpellBook.Length > 0 && Find(f.SpellBook) == null) Note(f.Key + ": SpellBookMp object " + f.SpellBook + " not found");
        if (f.ScienceSet.Length > 0 && !CommandSets.ContainsKey(f.ScienceSet)) Note(f.Key + ": PurchaseScienceCommandSetMP " + f.ScienceSet + " not found");

        Obj fort = Find(f.Fortress);
        if (fort == null) Drop(f, f.Fortress, "StartingBuilding has no Object block");
        // the engine looks templates up case-sensitively: always write the spelling of the Object declaration
        if (fort != null) f.Fortress = fort.Name;
        if (Find(f.SpellBook) != null) f.SpellBook = Find(f.SpellBook).Name;
        if (CommandSets.ContainsKey(f.ScienceSet)) f.ScienceSet = CommandSets[f.ScienceSet].Name;
        Obj builder = null;
        foreach (string su in StartingUnits(t)) { Obj o = Find(su); if (o != null && KindOf(o).Contains("DOZER")) { builder = o; break; } }
        f.Builder = builder != null ? builder.Name : ""; f.BuilderObj = builder;
        Obj keep = fort != null && ButtonsOf(fort).Any(b => b[1] == "UNIT_BUILD") ? fort : FindKeep(f, fort, builder);
        f.Keep = keep != null ? keep.Name : "";
        if (keep == null) Note(f.Key + ": no keep (CASTLE_KEEP that trains the builder) found for " + f.Fortress);

        if (fort != null) Add(f, "Fortress", fort, "");
        if (keep != null && keep != fort) Add(f, "Fortress", keep, f.Fortress);
        string home = keep != null ? keep.Name : f.Fortress;
        AddHeroes(f, T1(t, "BuildableHeroesMP"), "Hero", home);
        AddHeroes(f, T1(t, "BuildableRingHeroesMP"), "RingHero", home);

        // producers[i] = a structure whose command sets are searched; depth[i]: 0 = placed by the builder or on a fortress pad
        var producers = new List<Obj>(); var depth = new List<int>(); var viaWall = new HashSet<string>(IC);
        if (keep != null) { producers.Add(keep); depth.Add(0); }
        // buildings: what the builder can place
        if (builder != null)
            foreach (string[] b in ButtonsOf(builder))
            {
                if (b[1] != "DOZER_CONSTRUCT" || b[2].Length == 0) continue;
                Obj o = Find(b[2]);
                if (o == null) { Drop(f, b[2], "DOZER_CONSTRUCT button " + b[0] + " in " + b[3] + " names an object that does not exist"); continue; }
                if (o == fort || f.Seen.Contains(o.Name)) continue;
                if (Add(f, StructCategory(o), o, "") != null) { producers.Add(o); depth.Add(0); }
            }
        // fortress expansions: built on the pads that the base template places around the keep
        if (fort != null)
            foreach (Obj pad in Objects.Values.Where(o => o.Name.StartsWith(f.Fortress + "ExpansionPad", StringComparison.OrdinalIgnoreCase) && o.Name.IndexOf("Tutorial", StringComparison.OrdinalIgnoreCase) < 0).OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
            {
                f.Pads.Add(pad.Name);
                foreach (string[] b in ButtonsOf(pad))
                {
                    if (!StructureCommands.Contains(b[1]) || b[2].Length == 0) continue;
                    Obj o = Find(b[2]);
                    if (o == null) { Drop(f, b[2], b[1] + " button " + b[0] + " in " + b[3] + " (" + pad.Name + ") names an object that does not exist"); continue; }
                    if (f.Seen.Contains(o.Name) || !KindOf(o).Contains("STRUCTURE")) continue;
                    if (Add(f, StructCategory(o), o, home) != null) { producers.Add(o); depth.Add(0); }
                }
            }
        // structures that other structures create: wall hub -> wall segments (WallHubBehavior templates), wall segment -> gate /
        // tower / catapult upgrades, plot upgrades. Two levels only (templates only from what the builder / a pad places), so that
        // left-over references in the data (the old Dwarven castle hub still names Gondor wall pieces) do not leak in.
        for (int i = 0; i < producers.Count; i++)
        {
            Obj s = producers[i]; int d = depth[i]; if (d > 1) continue;
            HashSet<string> sk = KindOf(s); bool wall = sk.Contains("WALL_HUB") || sk.Contains("WALL_SEGMENT") || viaWall.Contains(s.Name);
            var names = new List<string>();
            foreach (string[] b in ButtonsOf(s)) if (StructureCommands.Contains(b[1]) && b[2].Length > 0) names.Add(b[2]);
            // (the segments a hub lays out - WallHubBehavior ...TemplateName - are wall pieces and are deliberately not followed)
            foreach (string n in names)
            {
                Obj o = Find(n);
                if (o == null) { Drop(f, n, "named by a build button of " + s.Name + " but no such object exists"); continue; }
                if (o == fort || f.Seen.Contains(o.Name) || !KindOf(o).Contains("STRUCTURE")) continue;
                string oside = Prop(o, "Side") ?? "";
                if (oside.Length > 0 && !Eq(oside, f.Side)) Note(f.Key + ": " + o.Name + " (created by " + s.Name + ") carries Side = " + oside + " in the INI; listed under " + f.Key + " anyway");
                if (Add(f, wall ? "Defence" : StructCategory(o), o, s.Name) == null) continue;
                producers.Add(o); depth.Add(d + 1);
                if (wall) viaWall.Add(o.Name);
            }
        }
        // units
        foreach (Obj s in producers) AddUnits(f, s, ButtonsOf(s), builder);
        if (builder != null && !f.Seen.Contains(builder.Name)) { Add(f, "Builder", builder, ""); Note(f.Key + ": builder " + builder.Name + " is not trained by any listed building"); }
        return f;
    }

    // ---- report only (not part of the catalog): what the spell-book powers a faction can buy put on the map.
    // science command set -> PURCHASE_SCIENCE buttons -> Science -> SpecialPower with that RequiredScience -> module of the
    // SpellBookMp object with that SpecialPowerTemplate -> its OCLs -> objects (eggs) -> their OCLs ... (a few levels)
    static Dictionary<string, List<string>> Ocls; static Dictionary<string, string> PowerScience;
    static void LoadSummonData()
    {
        Ocls = new Dictionary<string, List<string>>(IC); List<string> cur = null;
        foreach (Ln l in Lines(@"data\ini\objectcreationlist.ini", 0))
        {
            if (Eq(l.Tok[0], "ObjectCreationList") && !l.HasEq && l.Tok.Length > 1) { cur = new List<string>(); Ocls[l.Tok[1]] = cur; continue; }
            if (cur != null && Eq(l.Tok[0], "ObjectNames")) for (int i = 1; i < l.Tok.Length; i++) cur.Add(l.Tok[i]);
        }
        PowerScience = new Dictionary<string, string>(IC); string sp = null;
        foreach (Ln l in Lines(@"data\ini\specialpower.ini", 0))
        {
            if (Eq(l.Tok[0], "SpecialPower") && !l.HasEq && l.Tok.Length > 1) { sp = l.Tok[1]; continue; }
            if (sp != null && (Eq(l.Tok[0], "RequiredSciences") || Eq(l.Tok[0], "RequiredScience")) && l.Tok.Length > 1) PowerScience[sp] = l.Tok[1];
        }
    }
    static void Reach(string ocl, HashSet<string> seenOcl, HashSet<string> objs, int depth)
    {
        List<string> names; if (depth > 6 || !seenOcl.Add(ocl) || !Ocls.TryGetValue(ocl, out names)) return;
        foreach (string n in names)
        {
            Obj o = Find(n); if (o == null || !objs.Add(o.Name)) continue;
            foreach (Module m in Mods(o)) foreach (var kv in m.Props) foreach (string tok in Words(kv.Value)) if (Ocls.ContainsKey(tok)) Reach(tok, seenOcl, objs, depth + 1);
        }
    }
    static List<string> Summoned(Faction f)
    {
        var res = new List<string>(); Obj book = Find(f.SpellBook); CmdSet cs;
        if (book == null || !CommandSets.TryGetValue(f.ScienceSet, out cs)) return res;
        if (Ocls == null) LoadSummonData();
        var sciences = new HashSet<string>(IC);
        foreach (var slot in cs.Slots) { Dictionary<string, string> b = Button(slot.Value); string s; if (b != null && b.TryGetValue("Science", out s)) foreach (string x in Words(s)) sciences.Add(x); }
        var objs = new HashSet<string>(IC); var seen = new HashSet<string>(IC);
        foreach (Module m in Mods(book))
        {
            string power = m.Props.Where(p => Eq(p.Key, "SpecialPowerTemplate")).Select(p => First(p.Value)).FirstOrDefault(), sci;
            if (power == null || !PowerScience.TryGetValue(power, out sci) || !sciences.Contains(sci)) continue;
            foreach (var kv in m.Props) foreach (string tok in Words(kv.Value)) if (Ocls.ContainsKey(tok)) Reach(tok, seen, objs, 0);
        }
        foreach (string n in objs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            Obj o = Find(n); HashSet<string> k = KindOf(o);
            if (f.Seen.Contains(n) || !k.Contains("SELECTABLE") || k.Contains("INERT")) continue;
            string label = First(Prop(o, "DisplayName")), text;
            res.Add(n + " [" + (k.Contains("STRUCTURE") ? StructCategory(o) : k.Contains("HERO") ? "Hero" : UnitCategory(o)) + (label.Length > 0 && Strings.TryGetValue(label, out text) ? ", \"" + Clean(text) + "\"" : "") + "]");
        }
        return res;
    }

    static string Field(string s) { return (s ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim(); }

    static List<string> Render(Game g, List<Faction> factions)
    {
        var o = new List<string>();
        o.Add("# " + g.Title + " - skirmish catalog (heroes, units, buildings per faction). Generated by dev\\tools\\CatalogGen.exe " + g.Key + " <file> - do not edit by hand.");
        o.Add("# INI archives (highest priority first): " + string.Join("; ", Array.ConvertAll(g.IniArchives, System.IO.Path.GetFileName)));   // names only, not the folder of the PC they were read on
        o.Add("# lotr.str from: " + System.IO.Path.GetFileName(strSource));
        o.Add("# G|game|patch|date");
        o.Add("# F|FactionKey|display name|PlayerTemplate|Side|Fortress object|Builder object|SpellBookMp object|PurchaseScienceCommandSetMP");
        o.Add("# O|FactionKey|Category|ObjectName|display name|CommandPoints|BuildCost|produced at");
        o.Add("# Categories: " + string.Join(", ", CategoryOrder));
        o.Add("G|" + g.Key + "|" + Field(g.Patch) + "|" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        foreach (Faction f in factions)
        {
            o.Add(string.Join("|", new[] { "F", Field(f.Key), Field(f.Display), Field(f.Template), Field(f.Side), Field(f.Fortress), Field(f.Builder), Field(f.SpellBook), Field(f.ScienceSet) }));
            foreach (Rec r in f.Recs)
                o.Add(string.Join("|", new[] { "O", Field(f.Key), Field(r.Category), Field(r.Name), Field(r.Display), Field(r.CP), Field(r.Cost), Field(r.At) }));
        }
        return o;
    }

    // format + sanity checks on the rendered lines; returns the problems
    static List<string> Check(List<string> lines, List<Faction> factions)
    {
        var bad = new List<string>();
        foreach (string l in lines)
        {
            if (l.StartsWith("#")) continue;
            int n = l.Split('|').Length; char c = l[0];
            int want = c == 'G' ? 4 : c == 'F' ? 9 : c == 'O' ? 8 : -1;
            if (n != want) bad.Add("wrong field count (" + n + "): " + l);
        }
        foreach (Faction f in factions)
        {
            foreach (var dup in f.Recs.GroupBy(r => r.Name, IC).Where(x => x.Count() > 1)) bad.Add(f.Key + ": duplicate " + dup.Key);
            foreach (Rec r in f.Recs)
            {
                if (Find(r.Name) == null) bad.Add(f.Key + ": " + r.Name + " has no object");
                if (!CategoryOrder.Contains(r.Category)) bad.Add(f.Key + ": " + r.Name + " has the unknown category " + r.Category);
                if (r.Display.Length == 0) bad.Add(f.Key + ": " + r.Name + " has no display name");
            }
            if (f.Key == "Other") continue;
            int heroes = f.Recs.Count(r => r.Category == "Hero" || r.Category == "RingHero");
            int units = f.Recs.Count(r => UnitCategories.Contains(r.Category));
            int buildings = f.Recs.Count(r => r.Category == "Structure" || r.Category == "Defence" || r.Category == "Fortress");
            if (!f.Recs.Any(r => r.Category == "Fortress")) bad.Add(f.Key + ": no fortress");
            if (!f.Recs.Any(r => r.Category == "Builder")) bad.Add(f.Key + ": no builder");
            if (heroes < 4) bad.Add(f.Key + ": only " + heroes + " heroes");
            if (units < 8) bad.Add(f.Key + ": only " + units + " units");
            if (buildings < 6) bad.Add(f.Key + ": only " + buildings + " buildings");
        }
        return bad;
    }

    static void Stats(Game g, List<Faction> factions, List<string> problems)
    {
        Console.WriteLine(g.Title + " " + g.Patch);
        foreach (string n in ArchiveNotes) Console.WriteLine("archive  " + n);
        Console.WriteLine("lotr.str from " + strSource + " (" + Strings.Count + " labels); objects " + Objects.Count + ", command sets " + CommandSets.Count + ", buttons " + Buttons.Count + ", macros " + Macros.Count);
        Console.WriteLine();
        Console.WriteLine("{0,-9} {1}     total   heroes units buildings", "faction", string.Join(" ", CategoryOrder.Select(c => c.PadLeft(9)).ToArray()));
        foreach (Faction f in factions)
            Console.WriteLine("{0,-9} {1} {2,9} {3,8} {4,5} {5,9}", f.Key, string.Join(" ", CategoryOrder.Select(c => f.Recs.Count(r => r.Category == c).ToString().PadLeft(9)).ToArray()), f.Recs.Count,
                f.Recs.Count(r => r.Category == "Hero" || r.Category == "RingHero"), f.Recs.Count(r => UnitCategories.Contains(r.Category)), f.Recs.Count(r => r.Category == "Structure" || r.Category == "Defence" || r.Category == "Fortress"));
        Console.WriteLine();
        foreach (Faction f in factions) if (f.Key != "Other") Console.WriteLine("{0,-9} fortress {1} (keep {2}; pads {3}), builder {4}, spell book {5}, science set {6}", f.Key, f.Fortress, f.Keep, string.Join(",", f.Pads.ToArray()), f.Builder, f.SpellBook, f.ScienceSet);
        Console.WriteLine("\n--- dropped names: " + Dropped.Count); foreach (string s in Dropped) Console.WriteLine("  " + s);
        Console.WriteLine("--- excluded on purpose (create-a-hero templates, wall pieces, non-owned objects): " + Excluded.Count); foreach (string s in Excluded) Console.WriteLine("  " + s);
        Console.WriteLine("--- odd: " + Odd.Count); foreach (string s in Odd) Console.WriteLine("  " + s);
        Console.WriteLine("--- display-name fallbacks (no usable label in lotr.str; object name split into words): " + NameFallbacks.Count); foreach (string s in NameFallbacks) Console.WriteLine("  " + s);
        var noCp = factions.SelectMany(f => f.Recs.Where(r => UnitCategories.Contains(r.Category) && r.CP.Length == 0).Select(r => f.Key + ":" + r.Name)).ToList();
        var noCost = factions.SelectMany(f => f.Recs.Where(r => r.Cost.Length == 0).Select(r => f.Key + ":" + r.Name)).ToList();
        Console.WriteLine("--- units without a resolvable CommandPoints: " + noCp.Count + "  " + string.Join(", ", noCp.ToArray()));
        Console.WriteLine("--- objects without a resolvable BuildCost: " + noCost.Count + "  " + string.Join(", ", noCost.ToArray()));
        Console.WriteLine("--- summoned by the spell-book powers a faction can buy (NOT in the catalog; best-effort list from the OCL chains):");
        foreach (Faction f in factions) if (f.Key != "Other") Console.WriteLine("  " + f.Key + ": " + string.Join(", ", Summoned(f).ToArray()));
        Console.WriteLine("--- non-playable PlayerTemplates skipped: " + string.Join(", ", SkippedTemplates.ToArray()));
        Console.WriteLine("--- warnings: " + Warnings.Count); foreach (string s in Warnings) Console.WriteLine("  " + s);
        var usedConflicts = MacroConflicts.Where(c => MacrosUsed.Contains(c.Substring(0, c.IndexOf(':')))).ToList();
        Console.WriteLine("--- macros defined twice with different values: " + MacroConflicts.Count + ", of which used for a number in the catalog: " + usedConflicts.Count); foreach (string s in usedConflicts) Console.WriteLine("  " + s);
        Console.WriteLine("--- parser: " + ParseAnomalies.Count + " block anomalies, " + DuplicateObjects.Count + " duplicate object names, " + DuplicateDefs.Count + " duplicate command sets / buttons (details: --diag)");
        Console.WriteLine("--- checks: " + (problems.Count == 0 ? "all passed (field counts, fortress, builder, >=4 heroes, >=8 units, >=6 buildings, no duplicates, every object exists)" : problems.Count + " PROBLEM(S)")); foreach (string s in problems) Console.WriteLine("  " + s);
    }

    // ------------------------------------------------------------------------------------------------ main
    static int Main(string[] a)
    {
        if (a.Length < 2) { Console.WriteLine("CatalogGen rotwk|bfme2 <outFile> | --stats | --list | --kinds | --diag | --obj <Name> | --cs <CommandSet> | --find <regex> | --str <Label>"); return 1; }
        Game g = GetGame(a[0].ToLowerInvariant()); if (g == null) { Console.WriteLine("unknown game: " + a[0]); return 1; }
        Load(g);
        string cmd = a[1];
        if (cmd == "--diag") return Diag();
        if (cmd == "--obj" && a.Length > 2) { ShowObj(a[2]); return 0; }
        if (cmd == "--cs" && a.Length > 2) { ShowCs(a[2]); return 0; }
        if (cmd == "--find" && a.Length > 2)
        {
            var re = new Regex(a[2], RegexOptions.IgnoreCase);
            foreach (Obj o in Objects.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                if (re.IsMatch(o.Name)) Console.WriteLine("{0,-44} {1,-10} side={2,-9} cs={3} | {4}", o.Name, o.Parent == null ? "" : "<" + o.Parent, Prop(o, "Side"), string.Join(",", CommandSetsOf(o).ToArray()), string.Join(" ", KindOf(o).ToArray()));
            return 0;
        }
        if (cmd == "--str" && a.Length > 2) { string s; Console.WriteLine(Strings.TryGetValue(a[2], out s) ? s : "(not found)"); return 0; }

        List<Faction> factions = Build(g);
        List<string> lines = Render(g, factions);
        List<string> problems = Check(lines, factions);
        if (cmd == "--stats") { Stats(g, factions, problems); return problems.Count == 0 ? 0 : 3; }
        if (cmd == "--list") { foreach (string l in lines) Console.WriteLine(l); return 0; }
        if (cmd == "--kinds")
        {
            foreach (Faction f in factions) foreach (Rec r in f.Recs)
                Console.WriteLine("{0,-9} {1,-9} {2,-44} {3}", f.Key, r.Category, r.Name, string.Join(" ", KindsWithMembers(Find(r.Name)).OrderBy(x => x).ToArray()));
            return 0;
        }
        if (cmd.StartsWith("--")) { Console.WriteLine("unknown option " + cmd); return 1; }
        File.WriteAllText(cmd, string.Join("\r\n", lines.ToArray()) + "\r\n", new UTF8Encoding(false));
        Console.WriteLine("wrote " + cmd + ": " + factions.Count + " factions, " + factions.Sum(f => f.Recs.Count) + " objects");
        Stats(g, factions, problems);
        return problems.Count == 0 ? 0 : 3;
    }

    static int Diag()
    {
        foreach (string n in ArchiveNotes) Console.WriteLine("archive  " + n);
        Console.WriteLine("lotr.str from " + strSource + ": " + Strings.Count + " labels");
        Console.WriteLine("objects {0}, command sets {1}, buttons {2}, player templates {3}, macros {4}", Objects.Count, CommandSets.Count, Buttons.Count, Templates.Count, Macros.Count);
        Console.WriteLine("--- warnings: " + Warnings.Count); foreach (string s in Warnings.Take(60)) Console.WriteLine("  " + s);
        Console.WriteLine("--- parse anomalies: " + ParseAnomalies.Count + " (lines after an object's End are expected only where a file also holds non-object blocks such as FXList / ObjectCreationList)"); foreach (string s in ParseAnomalies.Take(80)) Console.WriteLine("  " + s);
        Console.WriteLine("--- duplicate objects: " + DuplicateObjects.Count); foreach (string s in DuplicateObjects.Take(60)) Console.WriteLine("  " + s);
        Console.WriteLine("--- duplicate command sets / buttons: " + DuplicateDefs.Count); foreach (string s in DuplicateDefs.Take(60)) Console.WriteLine("  " + s);
        Console.WriteLine("--- macros defined twice with different values: " + MacroConflicts.Count); foreach (string s in MacroConflicts.Take(60)) Console.WriteLine("  " + s);
        return 0;
    }

    static void ShowObj(string name)
    {
        Obj o = Find(name); if (o == null) { Console.WriteLine("no such object"); return; }
        var chain = new List<string>(); for (Obj p = o; p != null && chain.Count < 20; p = Find(p.Parent)) chain.Add(p.Form + " " + p.Name + " (" + p.File + ":" + p.Line + ")");
        Console.WriteLine(string.Join("  <-  ", chain.ToArray()));
        foreach (string k in new[] { "Side", "DisplayName", "CommandSet", "BuildCost", "BuildTime", "CommandPoints", "EditorSorting", "BuildVariations", "MaxSimultaneousOfType" })
            Console.WriteLine("  {0,-22} = {1}{2}", k, Prop(o, k), (k == "BuildCost" || k == "CommandPoints") ? "   -> " + Num(Prop(o, k)) : "");
        Console.WriteLine("  KindOf                 = " + string.Join(" ", KindOf(o).ToArray()));
        Console.WriteLine("  display                = " + Display(o));
        Console.WriteLine("  command sets           = " + string.Join(", ", CommandSetsOf(o).ToArray()));
        Console.WriteLine("  payload                = " + string.Join(", ", Payload(o).Select(p => p.Key + " x" + p.Value).ToArray()));
        Console.WriteLine("  unit category          = " + UnitCategory(o) + " / structure category = " + StructCategory(o));
        foreach (Module m in Mods(o))
        {
            if (Eq(m.Kind, "Draw")) continue;
            var interesting = m.Props.Where(p => Regex.IsMatch(p.Key, "^(\\w*CommandSet\\w*|TriggeredBy|InitialPayload|CastleToUnpackForFaction|Upgrade|ConflictsWith|RequiredUpgrade|SpecialPowerTemplate|\\w*OCL\\w*|\\w*Template\\w*|\\w*Object\\w*)$", RegexOptions.IgnoreCase)).Select(p => p.Key + "=" + p.Value).ToArray();
            Console.WriteLine("  [{0} {1} {2}] {3}", m.Kind, m.Class, m.Tag, string.Join(" ; ", interesting));
        }
    }

    static void ShowCs(string name)
    {
        CmdSet cs; if (!CommandSets.TryGetValue(name, out cs)) { Console.WriteLine("no such command set"); return; }
        foreach (var s in cs.Slots)
        {
            Dictionary<string, string> b = Button(s.Value); string cmd = "?", obj = "", up = "";
            if (b != null) { b.TryGetValue("Command", out cmd); b.TryGetValue("Object", out obj); b.TryGetValue("Upgrade", out up); }
            Console.WriteLine("  {0,2} {1,-62} {2,-28} {3} {4}", s.Key, s.Value, cmd, obj, string.IsNullOrEmpty(up) ? "" : "[" + up + "]");
        }
    }
}
