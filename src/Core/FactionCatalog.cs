using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace WitchKingTrainer.Core
{
    /// <summary>One hero, unit or building that can be placed for the local player.</summary>
    public sealed class CatalogItem
    {
        public string Faction = "", Category = "", Object = "", Name = "", ProducedAt = "";
        public int CommandPoints, Cost;
        public bool IsHero { get { return Category == "Hero" || Category == "RingHero"; } }
        public bool IsStructure { get { return Category == "Structure" || Category == "Defence" || Category == "Fortress"; } }
    }

    /// <summary>One playable faction of a game, as its own data files describe it.</summary>
    public sealed class CatalogFaction
    {
        public string Key = "", Name = "", PlayerTemplate = "", Side = "", Fortress = "", Builder = "", SpellBook = "", ScienceSet = "";
        public readonly List<CatalogItem> Items = new List<CatalogItem>();
        public List<CatalogItem> Heroes { get { return Items.FindAll(i => i.IsHero); } }
        /// <summary>A real, playable faction with a power tree (the last block of a list, "Other", is not: inns, lairs, creeps).</summary>
        public bool HasPowers { get { return Side.Length > 0 && SpellBook.Length > 0; } }
    }

    /// <summary>
    /// The lists behind the "All Factions" page: every skirmish faction of a game with its heroes, units and buildings.
    /// They are generated from the game's own INI data by dev\tools\CatalogGen (one text file per verified patch) and are
    /// carried inside the trainer; nothing is read from the game folder at run time. Every name is checked against the running
    /// game before it is used, so an entry that a patch does not have is simply reported as unavailable.
    /// </summary>
    public sealed class FactionCatalog
    {
        public string Game = "", Patch = "", Generated = "";
        public readonly List<CatalogFaction> Factions = new List<CatalogFaction>();

        public static readonly string[] CategoryOrder = { "Hero", "RingHero", "Infantry", "Cavalry", "Monster", "Siege", "Ship", "Builder", "Fortress", "Structure", "Defence", "Other" };
        public static string CategoryTitle(string c)
        {
            switch (c)
            {
                case "Hero": return "Heroes"; case "RingHero": return "Ring heroes"; case "Infantry": return "Infantry"; case "Cavalry": return "Cavalry";
                case "Monster": return "Monsters"; case "Siege": return "Siege"; case "Ship": return "Ships"; case "Builder": return "Builders";
                case "Fortress": return "Fortress"; case "Structure": return "Buildings"; case "Defence": return "Defences"; default: return "Other";
            }
        }

        public CatalogFaction Find(string key) { return Factions.Find(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)); }
        /// <summary>The faction with this side name in the game's data (Men, Elves, Dwarves, Isengard, Mordor, Wild, Angmar).</summary>
        public CatalogFaction BySide(string side) { return side == null || side.Length == 0 ? null : Factions.Find(f => string.Equals(f.Side, side, StringComparison.OrdinalIgnoreCase)); }
        public int ItemCount { get { int n = 0; foreach (CatalogFaction f in Factions) n += f.Items.Count; return n; } }

        static readonly Dictionary<GameId, FactionCatalog> Cache = new Dictionary<GameId, FactionCatalog>();
        public static FactionCatalog For(GameId game)
        {
            lock (Cache)
            {
                FactionCatalog c; if (Cache.TryGetValue(game, out c)) return c;
                c = new FactionCatalog();
                try
                {
                    string res = "WitchKingTrainer.catalog-" + (game == GameId.Bfme2 ? "bfme2" : "rotwk") + ".txt";
                    using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res))
                        if (s != null) using (var r = new StreamReader(s, Encoding.UTF8)) c = Parse(r.ReadToEnd());
                }
                catch { c = new FactionCatalog(); }
                Cache[game] = c; return c;
            }
        }

        static int Num(string s) { int n; return int.TryParse(s.Trim(), out n) ? n : 0; }
        static bool ValidName(string s)
        {
            // object / template names are plain identifiers; anything else is dropped (they are sent to the game as they are)
            if (s.Length == 0 || s.Length > 60) return false;
            foreach (char ch in s) if (!(ch >= 'A' && ch <= 'Z') && !(ch >= 'a' && ch <= 'z') && !(ch >= '0' && ch <= '9') && ch != '_') return false;
            return true;
        }

        /// <summary>
        /// Things that are in the generated lists but are deliberately NOT offered, because the game never creates them on their
        /// own and they cannot be tested here:
        ///  * buildings that a building makes, not a builder - the fortress keep and the fortress expansions. They belong to
        ///    a fortress base ("castle") and are created by it; the fortress itself is offered and unpacks its own keep;
        ///  * create-a-hero templates (they need per-player data) and creatures that are tied to a lair ("_Slaved");
        ///  * "_NoCP" twins of lair units (same unit, listed twice).
        /// </summary>
        static bool Offered(CatalogItem i)
        {
            if (i.Object.IndexOf("CreateAHero", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (i.Object.EndsWith("_Slaved", StringComparison.OrdinalIgnoreCase) || i.Object.EndsWith("_NoCP", StringComparison.OrdinalIgnoreCase)) return false;
            if (i.IsStructure && i.ProducedAt.Length > 0) return false;
            return true;
        }

        public static FactionCatalog Parse(string text)
        {
            var c = new FactionCatalog();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim(); if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split('|');
                if (p[0] == "G" && p.Length >= 4) { c.Game = p[1]; c.Patch = p[2]; c.Generated = p[3]; }
                else if (p[0] == "F" && p.Length >= 9)
                {
                    if (!ValidName(p[1]) || c.Find(p[1]) != null) continue;
                    c.Factions.Add(new CatalogFaction { Key = p[1], Name = p[2], PlayerTemplate = ValidName(p[3]) ? p[3] : "", Side = ValidName(p[4]) ? p[4] : "", Fortress = ValidName(p[5]) ? p[5] : "", Builder = ValidName(p[6]) ? p[6] : "", SpellBook = ValidName(p[7]) ? p[7] : "", ScienceSet = p[8] });
                }
                else if (p[0] == "O" && p.Length >= 5)
                {
                    CatalogFaction f = c.Find(p[1]); if (f == null || !ValidName(p[3])) continue;
                    if (f.Items.Exists(i => i.Object == p[3])) continue;
                    var item = new CatalogItem { Faction = f.Key, Category = p[2], Object = p[3], Name = p[4].Length > 0 ? p[4] : p[3], CommandPoints = p.Length > 5 ? Num(p[5]) : 0, Cost = p.Length > 6 ? Num(p[6]) : 0, ProducedAt = p.Length > 7 ? p[7].Trim() : "" };
                    if (Offered(item)) f.Items.Add(item);
                }
            }
            return c;
        }
    }
}
