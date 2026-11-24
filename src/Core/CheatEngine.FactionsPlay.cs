using System;
using System.Collections.Generic;
using System.Threading;

namespace WitchKingTrainer.Core
{
    // ALL FACTIONS (version 4.0.0) - NOT YET GAMEPLAY TESTED. Second part: where new things are put, and other factions' powers.
    public sealed partial class CheatEngine
    {
        // ------------------------------------------------------------------ where things appear
        int _spawnWaits; bool _spawnAnchorSet; float _anchorX, _anchorY; DateTime _nextSpawn = DateTime.MinValue;
        HashSet<string> _ownedForBatch;
        const float FaceCamera = -1.5707964f;       // new things look "down the screen", towards the default camera

        // Nothing is put on top of anything else (before 24 Nov 2026 every click started again at the camera point, so
        // buildings, battalions and heroes piled up on one spot). When a request starts, every object on the map is read once:
        // where it stands, whether it is a building, whether it is the player's. Each new thing then takes the free place
        // nearest to the point the camera looks at, and is itself remembered as taken for the things that follow.
        struct Taken { public float X, Y, R; public bool Structure, Mine; }
        List<Taken> _taken;
        const float FootUnit = 35f, FootBuilding = 130f, FootFortress = 260f, BuildingGap = 60f;   // half-widths in map units; buildings keep a lane between them

        /// <summary>Grid steps around the camera point, nearest first (places "below" the point - towards the player - slightly preferred).</summary>
        static readonly int[][] SpotOrder = BuildSpotOrder(32);
        static int[][] BuildSpotOrder(int rings)
        {
            var l = new List<int[]>();
            for (int i = -rings; i <= rings; i++) for (int j = -rings; j <= rings; j++) l.Add(new[] { i, j });
            l.Sort((a, b) => (Math.Sqrt(a[0] * a[0] + a[1] * a[1]) + 0.3 * a[1]).CompareTo(Math.Sqrt(b[0] * b[0] + b[1] * b[1]) + 0.3 * b[1]));
            return l.ToArray();
        }

        /// <summary>Everything that stands on the map right now (read only).</summary>
        List<Taken> ReadTaken(GameSnapshot g)
        {
            var list = new List<Taken>(); uint gl = _m.U32(Offsets.TheGameLogic); if (gl == 0) return list;
            float w = g.MapWidth > 200 ? g.MapWidth : 40000f, h = g.MapHeight > 200 ? g.MapHeight : 40000f;
            var isStructure = new Dictionary<uint, bool>(); int guard = 0, size = (int)Offsets.Object_Team + 4;
            for (uint o = _m.U32(gl + Offsets.GameLogic_FirstObject); o > 0x10000 && guard++ < 60000; )
            {
                byte[] buf = _m.ReadBytes(o, size); if (buf == null) break;
                float x = BitConverter.ToSingle(buf, (int)Offsets.Object_PosX), y = BitConverter.ToSingle(buf, (int)Offsets.Object_PosY);
                if (x > 1f && y > 1f && x < w && y < h)                              // helpers without a place (spell books, command point shadows) lie outside the map
                {
                    uint tmpl = BitConverter.ToUInt32(buf, (int)Offsets.Object_Template); bool st;
                    if (!isStructure.TryGetValue(tmpl, out st))
                    {
                        byte[] k = tmpl > 0x10000 ? _m.ReadBytes(tmpl + Offsets.Template_KindOf0, 1) : null;
                        st = k != null && (k[0] & 0x80) != 0; isStructure[tmpl] = st;
                    }
                    bool mine = OwnerFromTeam(BitConverter.ToUInt32(buf, (int)Offsets.Object_Team)) == g.Player;
                    list.Add(new Taken { X = x, Y = y, R = st ? FootBuilding : FootUnit, Structure = st, Mine = mine });
                }
                o = BitConverter.ToUInt32(buf, (int)Offsets.Object_Next);
            }
            return list;
        }

        bool SpotFree(float x, float y, float foot, bool structure)
        {
            foreach (Taken t in _taken)
            {
                if (structure && !t.Structure && !t.Mine) continue;                  // a building only minds other buildings and the player's own troops
                float need = foot + t.R + (structure && t.Structure ? BuildingGap : 0f) - 1f, dx = t.X - x, dy = t.Y - y;
                if (dx * dx + dy * dy < need * need) return false;
            }
            return true;
        }

        /// <summary>The free place nearest to (ax, ay) for something with the given half-width. False if there is none within reach.</summary>
        bool FindFreeSpot(GameSnapshot g, float ax, float ay, float foot, bool structure, out float x, out float y)
        {
            // Nothing is created closer than 150 map units to the edge of the map. (On 24 Nov 2026 a game crashed in
            // its own movement code over a unit that had ended up just outside the map edge beside a corner base. Nothing was
            // created there - the cause was not found - but the edge is no place to put new things.)
            float step = structure ? 80f : 70f, edge = Math.Max(150f, foot + 40f);
            float w = g.MapWidth > 200 ? g.MapWidth : 40000f, h = g.MapHeight > 200 ? g.MapHeight : 40000f;
            foreach (int[] c in SpotOrder)
            {
                x = ax + c[0] * step; y = ay + c[1] * step;
                if (x < edge || y < edge || x > w - edge || y > h - edge) continue;
                if (SpotFree(x, y, foot, structure)) return true;
            }
            x = ax; y = ay; return false;
        }

        static float FootOf(SpawnOrder o)
        {
            if (!o.Structure) return FootUnit;
            return o.Object.IndexOf("Fortress", StringComparison.OrdinalIgnoreCase) >= 0 ? FootFortress : FootBuilding;
        }

        bool CameraTarget(GameSnapshot g, out float x, out float y)
        {
            x = y = 0; if (g.View < 0x10000) return false;
            x = _m.F32(g.View + Offsets.View_LookAtX); y = _m.F32(g.View + Offsets.View_LookAtY);
            return Sane(x, -200f, 40000f) && Sane(y, -200f, 40000f);
        }

        void ClampToMap(GameSnapshot g, ref float x, ref float y)
        {
            float w = g.MapWidth > 200 ? g.MapWidth : 40000f, h = g.MapHeight > 200 ? g.MapHeight : 40000f;
            x = Math.Max(60f, Math.Min(w - 60f, x)); y = Math.Max(60f, Math.Min(h - 60f, y));
        }

        string TemplateNameOfObject(uint obj, Dictionary<uint, string> cache)
        {
            uint tmpl = _m.U32(obj + Offsets.Object_Template); if (tmpl < 0x10000) return ""; string n;
            if (cache != null && cache.TryGetValue(tmpl, out n)) return n;
            uint s = _m.U32(tmpl + Offsets.Template_Name); n = s > 0x10000 ? _m.AsciiZ(s + 8, 64) : "";
            if (cache != null) cache[tmpl] = n; return n;
        }

        /// <summary>Names of everything the local player owns right now (to keep heroes unique).</summary>
        HashSet<string> OwnedTemplates(GameSnapshot g)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); uint gl = _m.U32(Offsets.TheGameLogic); if (gl == 0) return set;
            var names = new Dictionary<uint, string>(); int guard = 0;
            for (uint o = _m.U32(gl + Offsets.GameLogic_FirstObject); o > 0x10000 && guard++ < 60000; o = _m.U32(o + Offsets.Object_Next))
            {
                if (!OwnedByLocalPlayer(g, o)) continue;
                string n = TemplateNameOfObject(o, names); if (n.Length > 0) set.Add(n);
            }
            return set;
        }

        void EndBatch(string text, bool error)
        {
            lock (_facLock) _spawnQueue.Clear();
            _spawnAnchorSet = false; _ownedForBatch = null; _taken = null; _spawnWaits = 0; _spawnText = text;
            if (error) Event("All Factions: " + text + ". " + (_spawnDone > 0 ? _spawnDone + " created before that." : "Nothing was created."), true);
        }

        void HandleSpawns(GameSnapshot g)
        {
            SpawnOrder order;
            lock (_facLock) { if (_spawnQueue.Count == 0) return; order = _spawnQueue.Peek(); }
            if (!SpawnUsable()) { EndBatch("Not available on this game version (" + _facWhyNot + ")", true); return; }
            if (DateTime.UtcNow < _nextSpawn) return;                                   // waiting for a paused game: try again on a later tick
            if (!EnsureHookFor("All Factions")) { EndBatch("Could not reach the game", false); return; }
            if (!_spawnAnchorSet)
            {
                if (!CameraTarget(g, out _anchorX, out _anchorY)) { EndBatch("The camera position could not be read", true); return; }
                ClampToMap(g, ref _anchorX, ref _anchorY);
                _spawnWaits = 0; _spawnAnchorSet = true; _ownedForBatch = OwnedTemplates(g); _taken = ReadTaken(g);
            }
            if (_taken == null) _taken = ReadTaken(g);

            bool dequeue = true;
            if (order.Hero && _ownedForBatch != null && _ownedForBatch.Contains(order.Object)) _spawnSkipped++;          // a hero exists only once
            else
            {
                // Everything goes to the free place nearest to the point the camera looks at: exactly there if the ground is
                // free, otherwise beside what already stands there (see ReadTaken / FindFreeSpot above).
                float x, y, foot = FootOf(order);
                if (!FindFreeSpot(g, _anchorX, _anchorY, foot, order.Structure, out x, out y))
                {
                    _spawnFailed++; Log("ALL FACTIONS no free ground near the camera for " + order.Object);
                    goto next;
                }
                uint obj; int r = SpawnOne(g, order.Object, x, y, FaceCamera, out obj);
                switch (r)
                {
                    case SpawnOk:
                        _spawnDone++; _spawnWaits = 0; _taken.Add(new Taken { X = x, Y = y, R = foot, Structure = order.Structure, Mine = true });
                        if (_ownedForBatch != null) _ownedForBatch.Add(order.Object);
                        Log("ALL FACTIONS created " + order.Object + " at (" + (int)x + ", " + (int)y + ")"); break;
                    case SpawnNotReached:
                        dequeue = false; _spawnText = "Waiting for the game (resume the match)"; _nextSpawn = DateTime.UtcNow.AddMilliseconds(400);
                        if (++_spawnWaits > 150) { EndBatch("The game did not respond - resume the match and try again", true); return; }
                        break;
                    case SpawnCannot:
                        EndBatch("Not possible right now (" + _facWhyNot + ")", true); return;
                    case SpawnNotMine:
                        _spawnFailed++; Log("ALL FACTIONS " + order.Object + " was created, but the game did not give it to your player"); break;
                    default:
                        _spawnFailed++; Log("ALL FACTIONS could not create " + order.Object + (r == SpawnUnknownName ? " (name not in this game version)" : " (the game refused)")); break;
                }
            }
            next:
            if (dequeue)
            {
                int left; lock (_facLock) { if (_spawnQueue.Count > 0) _spawnQueue.Dequeue(); left = _spawnQueue.Count; }
                if (left > 0) _spawnText = "Creating " + _spawnTitle + ":  " + (_spawnDone + _spawnSkipped + _spawnFailed) + " of " + _spawnTotal;
                else
                {
                    string r = _spawnDone + " created" + (_spawnSkipped > 0 ? ", " + _spawnSkipped + " already in your army" : "") + (_spawnFailed > 0 ? ", " + _spawnFailed + " not available" : "");
                    string what = _spawnTotal == 1 ? order.Name : _spawnTitle;
                    _spawnText = what + ":  " + r;
                    Event("All Factions - " + what + ": " + r + ".", _spawnDone == 0 && _spawnFailed > 0);
                    _spawnAnchorSet = false; _ownedForBatch = null; _taken = null; _spawnWaits = 0;
                }
            }
            _wake.Set();   // the next one follows on the next tick
        }

        // ------------------------------------------------------------------ another faction's powers
        // Which powers a player can cast is decided by ONE object the player owns - the faction's "spell book" - whose id
        // is kept in the Player. The trainer creates the other faction's spell book for the player (the same three calls the
        // game makes for every player when a match starts), makes it the one in use, has the spell bar read it, and unlocks
        // that faction's whole power tree (found under its own name) with the same call the Unlock All Powers cheat uses.
        // The player's own faction is NOT rewritten: the game derives too much from it (Good / Evil of the whole army, the
        // rank table, the hero roster, the side string) for that to be safe without testing. See dev\notes\re-factions.md.
        string _activeBook = "", _ownBook = ""; int _bookScanTries; bool _bookScanDone;
        // 4.0.1: _wantForeign = another faction's powers were asked for in THIS match by THIS trainer. The games do not save
        // which spell book is in use; a loaded game takes the first one it finds (seen in the first real test: the Isengard bar
        // came back although the own bar was in use when the game was saved). A foreign bar nobody asked for is put right.
        bool _wantForeign; uint _lastBookId, _facLastFrame; int _autoBackTries; DateTime _autoBackNext;

        bool FactionUsable()
        {
            if (_factionReady != 0) return _factionReady == 1;
            bool ok = Offsets.Player_SpellBookId != 0 && Offsets.ThePlayerTemplateStore != 0 && Offsets.PlayerTemplate_Size != 0 && Offsets.TheHud != 0
                      && Offsets.Sig_GetSpellBook != null && Offsets.Sig_PlayerTemplateStore != null && Offsets.Sig_FindCommandSet != null
                      && Offsets.Sig_SpellPanelA != null && Offsets.Sig_SpellPanelB != null && Offsets.Sig_SpellPanelC != null && Offsets.Sig_ControlBarDirty != null
                      && BytesMatch(Offsets.Fn_GetSpellBookObject, Offsets.Sig_GetSpellBook, "spell book")
                      && BytesMatch(Offsets.PlayerTemplateStore_Site, Offsets.Sig_PlayerTemplateStore, "faction list")
                      && BytesMatch(Offsets.Fn_FindCommandSet, Offsets.Sig_FindCommandSet, "power tree")
                      && BytesMatch(Offsets.SpellPanelSiteA, Offsets.Sig_SpellPanelA, "spell bar")
                      && BytesMatch(Offsets.SpellPanelSiteB, Offsets.Sig_SpellPanelB, "spell bar")
                      && BytesMatch(Offsets.SpellPanelSiteC, Offsets.Sig_SpellPanelC, "spell bar")
                      && BytesMatch(Offsets.ControlBarDirtySite, Offsets.Sig_ControlBarDirty, "control bar");
            if (!ok && _facWhyNot.Length == 0) _facWhyNot = "other factions' powers are not supported for this game version";
            _factionReady = ok ? 1 : 2; if (!ok) Log("ALL FACTIONS powers unavailable: " + _facWhyNot);
            return ok;
        }

        /// <summary>Text of an AsciiString that lives in the game (empty if it is not set).</summary>
        string AsciiAt(uint addr) { uint s = _m.U32(addr); return s > 0x10000 ? _m.AsciiZ(s + 8, 64) : ""; }

        bool FactionList(out uint beg, out uint end)
        {
            beg = end = 0; uint store = _m.U32(Offsets.ThePlayerTemplateStore), size = Offsets.PlayerTemplate_Size; if (store < 0x10000 || size == 0) return false;
            beg = _m.U32(store + Offsets.PlayerTemplateStore_Begin); end = _m.U32(store + Offsets.PlayerTemplateStore_End);
            return beg > 0x10000 && end > beg && (end - beg) % size == 0 && (end - beg) / size <= 64;
        }

        /// <summary>A map may replace a faction's data; the entry that counts is the last one of that chain.</summary>
        uint LiveTemplate(uint pt)
        {
            for (int i = 0; i < 8 && pt > 0x10000; i++) { uint next = _m.U32(pt + Offsets.PlayerTemplate_Override); if (next < 0x10000) break; pt = next; }
            return pt;
        }

        /// <summary>Finds a faction in the game's own list by its side name (read only). spellBook, if given, must agree with the game's data.</summary>
        uint FindPlayerTemplate(string side, string spellBook)
        {
            uint beg, end; if (side.Length == 0 || !FactionList(out beg, out end)) return 0;
            for (uint e = beg; e < end; e += Offsets.PlayerTemplate_Size)
            {
                uint pt = LiveTemplate(e);
                if (!string.Equals(AsciiAt(pt + Offsets.PlayerTemplate_Name), side, StringComparison.OrdinalIgnoreCase)) continue;
                byte[] playable = _m.ReadBytes(pt + Offsets.PlayerTemplate_Playable, 1); if (playable == null || playable[0] == 0) continue;
                if (spellBook.Length > 0 && !string.Equals(AsciiAt(pt + Offsets.PlayerTemplate_SpellBookMp), spellBook, StringComparison.OrdinalIgnoreCase)) continue;
                return pt;
            }
            return 0;
        }

        /// <summary>The side whose spell book has this name (for the "powers in use" line of the menu).</summary>
        string SideOfBook(string book)
        {
            uint beg, end; if (book.Length == 0 || !FactionList(out beg, out end)) return "";
            for (uint e = beg; e < end; e += Offsets.PlayerTemplate_Size)
            {
                uint pt = LiveTemplate(e);
                if (string.Equals(AsciiAt(pt + Offsets.PlayerTemplate_SpellBookMp), book, StringComparison.OrdinalIgnoreCase)) { string s = AsciiAt(pt + Offsets.PlayerTemplate_Name); if (s.Length > 0) return s; }
            }
            return "";
        }

        uint OwnTemplate(GameSnapshot g) { uint pt = _m.U32(g.Player + Offsets.Player_Template); return pt > 0x10000 ? LiveTemplate(pt) : 0; }

        uint FindOwnedObject(GameSnapshot g, string templateName)
        {
            uint gl = _m.U32(Offsets.TheGameLogic); if (gl == 0 || templateName.Length == 0) return 0; int guard = 0; var names = new Dictionary<uint, string>();
            for (uint o = _m.U32(gl + Offsets.GameLogic_FirstObject); o > 0x10000 && guard++ < 60000; o = _m.U32(o + Offsets.Object_Next))
                if (string.Equals(TemplateNameOfObject(o, names), templateName, StringComparison.OrdinalIgnoreCase) && OwnedByLocalPlayer(g, o)) return o;
            return 0;
        }

        string NameOfOwnedObjectWithId(GameSnapshot g, uint id)
        {
            uint gl = _m.U32(Offsets.TheGameLogic); if (gl == 0 || id == 0) return ""; int guard = 0;
            for (uint o = _m.U32(gl + Offsets.GameLogic_FirstObject); o > 0x10000 && guard++ < 60000; o = _m.U32(o + Offsets.Object_Next))
                if (_m.U32(o + Offsets.Object_Id) == id) return OwnedByLocalPlayer(g, o) ? TemplateNameOfObject(o, null) : "";
            return "";
        }

        /// <summary>Reads which spell book is in use (once per match, and again after every change).</summary>
        void ScanBooks(GameSnapshot g)
        {
            uint own = OwnTemplate(g); _ownBook = own != 0 ? AsciiAt(own + Offsets.PlayerTemplate_SpellBookMp) : "";
            _ownTemplateName = own != 0 ? AsciiAt(own + Offsets.PlayerTemplate_Name) : "";
            uint id = _m.U32(g.Player + Offsets.Player_SpellBookId);
            if (id == 0) { _activeBook = _ownBook; _templateName = _ownTemplateName; _bookScanDone = ++_bookScanTries > 25; return; }      // the game has not looked its book up yet
            string name = NameOfOwnedObjectWithId(g, id); _activeBook = name.Length > 0 ? name : _ownBook;
            string side = SideOfBook(_activeBook); _templateName = side.Length > 0 ? side : _ownTemplateName; _bookScanDone = true;
        }

        bool PowersSwitched { get { return _activeBook.Length > 0 && _ownBook.Length > 0 && !string.Equals(_activeBook, _ownBook, StringComparison.OrdinalIgnoreCase); } }

        /// <summary>
        /// Makes the spell book with the given template name the one in use - creating it for the player if they do not own it
        /// yet (nameCell = the game's own AsciiString holding that name). Returns SpawnOk / SpawnNotReached / other = failed.
        /// </summary>
        int UseBook(GameSnapshot g, string bookName, uint nameCell)
        {
            uint team; if (!LocalTeam(g, out team)) return SpawnCannot;
            if (!EnsureSpawnRoutine()) { _facWhyNot = "could not write the request into the game"; return SpawnCannot; }
            uint existing = FindOwnedObject(g, bookName), existingId = existing != 0 ? _m.U32(existing + Offsets.Object_Id) : 0;
            uint id; int r = CallTracked(OffBookRoutine, new[] { nameCell, team, g.Player, existingId }, out id);
            if (r != SpawnOk) { if (r == SpawnCannot) _facWhyNot = "could not write the request into the game"; return r; }
            if (id == 0) { _facWhyNot = "the game could not create the spell book " + bookName; return SpawnRefused; }
            if (existingId == 0) Log("ALL FACTIONS created spell book " + bookName + " (id " + id + ")");
            return SpawnOk;
        }

        void HandleFaction(GameSnapshot g)
        {
            FactionJob job; lock (_facLock) { job = _factionJob; _factionJob = null; }
            if (job == null) return;
            if (!FactionUsable() || !SpawnUsable()) { _factionText = "Not available on this game version."; Event("Other factions' powers: " + _facWhyNot + ". Nothing was changed.", true); return; }
            if (!EnsureHookFor("Other factions' powers")) { _factionText = "Could not reach the game."; return; }
            ScanBooks(g);

            if (job.Restore)
            {
                if (!PowersSwitched) { _factionText = "You are using your own faction's powers."; return; }
                string err; if (RestoreFaction(g, out err)) { _wantForeign = false; _factionText = "Your own powers are back. Everything you built or recruited stays."; Event("Your own faction's power bar is back.", false); }
                else _factionText = err;
                return;
            }

            uint tmpl = FindPlayerTemplate(job.Side, job.SpellBook);
            if (tmpl == 0) { _factionText = job.Name + " was not found in this game's faction list."; Event("Other factions' powers: the game has no playable faction \"" + job.Side + "\" with the spell book " + job.SpellBook + ". Nothing was changed.", true); return; }
            string book = AsciiAt(tmpl + Offsets.PlayerTemplate_SpellBookMp);
            if (book.Length == 0) { _factionText = job.Name + " has no spell book in this game."; return; }

            int r = UseBook(g, book, tmpl + Offsets.PlayerTemplate_SpellBookMp);
            if (r == SpawnNotReached) { _factionText = "The game is paused - resume the match and press again."; return; }
            if (r != SpawnOk) { _factionText = "Could not switch the powers (" + _facWhyNot + ")."; Event("Other factions' powers: " + _facWhyNot + ". Nothing else was changed.", true); ScanBooks(g); return; }
            _wantForeign = true;
            Log("ALL FACTIONS powers -> " + job.Side + " (" + book + ")" + (job.Change ? " + start-up" : ""));

            // the whole power tree of that faction, under its own name (the game's purchase screen keeps showing your own tree)
            string unlock = "";
            uint bar = _m.U32(Offsets.TheControlBar), cs;
            if (bar > 0x10000 && AsciiAt(tmpl + Offsets.PlayerTemplate_ScienceSetMP).Length > 0
                && _hook.Call(Offsets.Fn_FindCommandSet, bar, new[] { tmpl + Offsets.PlayerTemplate_ScienceSetMP }, 700, out cs) && cs > 0x10000)
                unlock = UnlockCommandSet(g, cs, job.Name, job.Name + " powers");
            else { unlock = "its power tree was not found"; SweepCooldowns(g, true); }
            ScanBooks(g);

            if (job.Change && job.Extras.Count > 0) RequestSpawn(job.Extras, job.Name + " start-up");
            _factionText = (job.Change ? "You now command " + job.Name + " as well: its powers are on your power bar (" + unlock + ")" + (job.Extras.Count > 0 ? "; its builders appear where the camera is looking." : ".")
                                       : job.Name + " powers are on your power bar now (" + unlock + ").")
                           + " Your army and buildings are unchanged.";
            Event(job.Change ? "Changed to " + job.Name + ": powers switched" + (job.Extras.Count > 0 ? ", start-up units on the way." : ".") : "Now using the powers of " + job.Name + ".", false);
        }

        /// <summary>Puts the player's own spell book back in use. The id is never cleared: the game would then pick any book.</summary>
        bool RestoreFaction(GameSnapshot g, out string error)
        {
            error = ""; uint own = OwnTemplate(g); string book = own != 0 ? AsciiAt(own + Offsets.PlayerTemplate_SpellBookMp) : "";
            if (book.Length == 0) { error = "Your own faction's spell book could not be identified."; return false; }
            if (FindOwnedObject(g, book) == 0) { error = "Your own spell book was not found in this match - nothing was changed."; return false; }
            int r = UseBook(g, book, own + Offsets.PlayerTemplate_SpellBookMp);
            if (r == SpawnNotReached) { error = "The game is paused - resume the match and press again."; return false; }
            if (r != SpawnOk) { error = "Could not switch back (" + _facWhyNot + ")."; return false; }
            Log("ALL FACTIONS powers -> own faction (" + book + ")"); ScanBooks(g); return true;
        }

        /// <summary>Called every tick while a match runs.</summary>
        void ApplyFactions(GameSnapshot g)
        {
            bool work; lock (_facLock) work = _factionJob != null || _spawnQueue.Count > 0;
            bool running = g.Frame != _facLastFrame; _facLastFrame = g.Frame;
            // which powers are in use is only read when it is cheap and the layout has been proven for this game version;
            // after the first look it is read again whenever the game itself changes the book it uses
            if (_factionReady == 1 || (_factionReady == 0 && FactionUsable()))
            {
                uint id = _m.U32(g.Player + Offsets.Player_SpellBookId);
                if (!_bookScanDone || id != _lastBookId) { _lastBookId = id; ScanBooks(g); }
                if (!work && running && PowersSwitched && !_wantForeign) AutoBack(g);
            }
            if (!work) return;
            HandleFaction(g);
            HandleSpawns(g);
        }

        /// <summary>
        /// Another faction's power bar is in use that nobody asked for in this match - a loaded saved game (the games do not
        /// store which spell book was in use). The player's own book is put back, with the same call the Back button makes.
        /// Only while the game is running, at most a few times per match, and only if the own book really exists.
        /// </summary>
        void AutoBack(GameSnapshot g)
        {
            if (_autoBackTries >= 6 || DateTime.UtcNow < _autoBackNext || !SpawnUsable()) return;
            _autoBackNext = DateTime.UtcNow.AddSeconds(3);
            if (!_hook.Installed && !_hook.Install()) { _autoBackTries++; return; }
            string was = _templateName, err; _autoBackTries++;
            if (RestoreFaction(g, out err))
            {
                _factionText = "This saved game came back with the " + was + " power bar. Your own powers are in use again - press USE THIS FACTION'S POWERS if you want the others.";
                Event("Loaded game: your own faction's power bar was put back (it came back as " + was + ").", false);
            }
            else Log("ALL FACTIONS own power bar not put back yet: " + err);
        }

        /// <summary>A match ended or a new one started: forget everything that belonged to the old one.</summary>
        void FactionsMatchEnded()
        {
            lock (_facLock) { _spawnQueue.Clear(); _factionJob = null; }
            _spawnAnchorSet = false; _ownedForBatch = null; _taken = null; _spawnWaits = 0; _spawnText = ""; _factionText = ""; _templateName = ""; _ownTemplateName = "";
            _activeBook = ""; _ownBook = ""; _bookScanDone = false; _bookScanTries = 0;
            _wantForeign = false; _lastBookId = 0; _autoBackTries = 0; _autoBackNext = DateTime.MinValue;
        }
    }
}
