using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace WitchKingTrainer.Core
{
    /// <summary>One thing to create for the local player (a hero, a unit / battalion, or a finished building).</summary>
    public sealed class SpawnOrder { public string Object = "", Name = ""; public bool Hero, Structure; }

    // =====================================================================================================================
    //  ALL FACTIONS (version 4.0.0)  -  NOT YET GAMEPLAY TESTED
    //  Everything in this file was written from static analysis of the two game programs (dev\notes\re-spawn.md and
    //  re-factions.md). None of it has run inside a game yet. It is built so that it cannot disturb the existing cheats:
    //   * nothing here runs unless a button on the All Factions page is pressed;
    //   * before the first use in a game, the exact code bytes of every engine routine it calls are compared with the bytes
    //     that were analysed; if one differs, the feature reports "not available on this game version" and does nothing;
    //   * every engine routine is called on the game's own thread, in its update step (the same way the game's map scripts
    //     create units), never from outside;
    //   * offline skirmish only, local player only.
    // =====================================================================================================================
    public sealed partial class CheatEngine
    {
        // ------------------------------------------------------------------ requests from the menu
        sealed class FactionJob { public string Side, Name, SpellBook; public bool Change; public List<SpawnOrder> Extras; public bool Restore; }
        readonly Queue<SpawnOrder> _spawnQueue = new Queue<SpawnOrder>(); readonly object _facLock = new object();
        FactionJob _factionJob; string _spawnTitle = ""; int _spawnTotal, _spawnDone, _spawnSkipped, _spawnFailed;
        string _spawnText = "", _factionText = "", _templateName = "", _ownTemplateName = "";

        /// <summary>Creates the given heroes / units / buildings for the local player where the camera is looking.</summary>
        public void RequestSpawn(IList<SpawnOrder> orders, string title)
        {
            if (orders == null || orders.Count == 0) return;
            lock (_facLock)
            {
                if (_spawnQueue.Count == 0) { _spawnTotal = 0; _spawnDone = 0; _spawnSkipped = 0; _spawnFailed = 0; _spawnTitle = title ?? ""; }
                else _spawnTitle = "several requests";
                foreach (SpawnOrder o in orders) { if (_spawnQueue.Count >= 400) break; _spawnQueue.Enqueue(o); _spawnTotal++; }
            }
            _wake.Set();
        }

        /// <summary>
        /// Gives the local player another faction's powers: that faction's spell book becomes the one in use and its whole power
        /// tree is unlocked (change = false) - or that plus the extra objects given (its builders / fortress). The player's own
        /// faction, units and buildings are never touched. "side" is the faction's name in the game's data (Men, Elves, Wild ...).
        /// </summary>
        public void RequestFaction(string side, string displayName, string spellBook, bool change, IList<SpawnOrder> extras)
        {
            lock (_facLock) _factionJob = new FactionJob { Side = side ?? "", Name = displayName ?? "", SpellBook = spellBook ?? "", Change = change, Extras = extras == null ? new List<SpawnOrder>() : new List<SpawnOrder>(extras) };
            _wake.Set();
        }
        public void RequestRestoreFaction() { lock (_facLock) _factionJob = new FactionJob { Restore = true }; _wake.Set(); }

        // ------------------------------------------------------------------ one-time check of the engine routines
        // 0 = not checked yet, 1 = usable, 2 = this game version does not match what was analysed
        int _spawnReady, _factionReady; string _facWhyNot = "";

        bool BytesMatch(uint addr, byte[] expected, string what)
        {
            byte[] b = _m.ReadBytes(addr, expected.Length);
            if (b == null) { _facWhyNot = "could not read the game's " + what + " code"; return false; }
            for (int i = 0; i < expected.Length; i++) if (b[i] != expected[i]) { _facWhyNot = "the game's " + what + " code is not the version this was written for"; return false; }
            return true;
        }

        bool SpawnUsable()
        {
            if (_spawnReady != 0) return _spawnReady == 1;
            bool ok = Offsets.Sig_FindTemplate != null && Offsets.Sig_NewObject != null && Offsets.Sig_SetPosition != null && Offsets.Sig_SetOrientation != null
                      && Offsets.Sig_OnBuildComplete != null && Offsets.Sig_AddToPathfindMap != null && Offsets.Sig_GetGroundHeight != null && Offsets.Player_DefaultTeam != 0
                      && BytesMatch(Offsets.Fn_FindTemplate, Offsets.Sig_FindTemplate, "object list")
                      && BytesMatch(Offsets.Fn_NewObject, Offsets.Sig_NewObject, "object creation")
                      && BytesMatch(Offsets.Fn_SetOrientation, Offsets.Sig_SetOrientation, "object placement")
                      && BytesMatch(Offsets.Fn_SetPosition, Offsets.Sig_SetPosition, "object placement")
                      && BytesMatch(Offsets.Fn_OnBuildComplete, Offsets.Sig_OnBuildComplete, "object completion")
                      && BytesMatch(Offsets.Fn_AddToPathfindMap, Offsets.Sig_AddToPathfindMap, "path finding")
                      && BytesMatch(Offsets.Fn_GetGroundHeight, Offsets.Sig_GetGroundHeight, "terrain height");
            if (!ok && _facWhyNot.Length == 0) _facWhyNot = "creating objects is not supported for this game version";
            if (ok) { uint f = _m.U32(Offsets.TheThingFactory); if (f < 0x10000) { ok = false; _facWhyNot = "the game's object list was not found"; } }
            _spawnReady = ok ? 1 : 2;
            if (!ok) Log("ALL FACTIONS spawn unavailable: " + _facWhyNot);
            return ok;
        }

        // ------------------------------------------------------------------ the routine that runs inside the game
        // cave layout of this feature: +0xB00 "create object" code, +0xC00 AsciiString slot, +0xC10 string data {refs, len, cap, text},
        // +0xCA0 position (3 floats), +0xCB0 16 zero bytes, +0xCC0 "template found", +0xCC4 busy, +0xCC8 result, +0xD00 "spell book" code
        const uint OffSpawnRoutine = 0xB00, MaxSpawnRoutine = 0x100, OffStrSlot = 0xC00, OffStrData = 0xC10, OffSpawnPos = 0xCA0, OffZero16 = 0xCB0, OffSpawnFound = 0xCC0,
                   OffSpawnBusy = 0xCC4, OffSpawnResult = 0xCC8, OffBookRoutine = 0xD00, MaxBookRoutine = 0x100;
        MainThreadHook _spawnRoutineFor;

        byte[] BuildSpawnRoutine(uint cave)
        {
            // Object* spawn(AsciiString* name, Team* team, Coord3D* pos, float angle)
            // Step for step what the games' own map-script action "create object" does (find the template, new object for a team,
            // orientation, position, "build complete" - the last one is what fills a battalion with its soldiers). Two things are
            // added that the games' builder code does and the script action does not: the height of the ground is looked up for
            // the position, and a building is entered into the path-finding map so that units walk around it.
            var a = new MainThreadHook.Asm();
            a.Db(0x55, 0x8B, 0xEC, 0x56, 0x57);                                       // push ebp ; mov ebp, esp ; push esi ; push edi
            a.Db(0xC7, 0x05).D32(cave + OffSpawnBusy).D32(1);                         // busy = 1   (the game has started on this request)
            a.Db(0xC7, 0x05).D32(cave + OffSpawnFound).D32(0);                        // found = 0
            a.Db(0xFF, 0x75, 0x08);                                                   // push [ebp+8]            (name)
            a.Db(0x8B, 0x0D).D32(Offsets.TheThingFactory);                            // mov ecx, [TheThingFactory]
            a.CallAbs(Offsets.Fn_FindTemplate);                                       // -> eax = ThingTemplate* or 0
            a.Db(0x85, 0xC0).Je("done");
            a.Db(0x8B, 0xF8);                                                         // mov edi, eax            (template)
            a.Db(0xC7, 0x05).D32(cave + OffSpawnFound).D32(1);                        // found = 1
            a.Db(0x8B, 0x75, 0x10);                                                   // mov esi, [ebp+0x10]     (position)
            a.Db(0x6A, 0x00);                                                         // push 0                  (no surface normal wanted)
            a.Db(0xFF, 0x76, 0x04);                                                   // push [esi+4]            (y)
            a.Db(0xFF, 0x36);                                                         // push [esi]              (x)
            a.CallAbs(Offsets.Fn_GetGroundHeight);                                    // -> height on the FPU stack
            a.Db(0xD9, 0x5E, 0x08);                                                   // fstp dword [esi+8]      (z = ground height)
            a.Db(0x6A, 0x00);                                                         // push 0                  (a new object id)
            a.Db(0x68).D32(cave + OffZero16);                                         // push &zero16            (no status bits = a normal, finished object)
            a.Db(0xFF, 0x75, 0x0C);                                                   // push [ebp+0xC]          (team)
            a.Db(0x57);                                                               // push edi                (template)
            a.Db(0x8B, 0x0D).D32(Offsets.TheThingFactory);                            // mov ecx, [TheThingFactory]
            a.CallAbs(Offsets.Fn_NewObject);                                          // -> eax = Object* or 0
            a.Db(0x85, 0xC0).Je("done");
            a.Db(0x8B, 0xF0);                                                         // mov esi, eax            (object)
            a.Db(0xFF, 0x75, 0x14);                                                   // push [ebp+0x14]         (angle, float)
            a.Db(0x8B, 0xCE);                                                         // mov ecx, esi
            a.CallAbs(Offsets.Fn_SetOrientation);
            a.Db(0xFF, 0x75, 0x10);                                                   // push [ebp+0x10]         (position)
            a.Db(0x8B, 0xCE);                                                         // mov ecx, esi
            a.CallAbs(Offsets.Fn_SetPosition);
            a.Db(0x8B, 0xCE);                                                         // mov ecx, esi
            a.CallAbs(Offsets.Fn_OnBuildComplete);
            a.Db(0x8B, 0x46, 0x04);                                                   // mov eax, [esi+4]        (the object's template)
            a.Db(0xF6, 0x80).D32(Offsets.Template_KindOf0).Db(0x80);                  // test byte [eax+kindOf], STRUCTURE
            a.Je("notbuilding");
            a.Db(0xA1).D32(Offsets.TheAI);                                            // mov eax, [TheAI]
            a.Db(0x85, 0xC0).Je("notbuilding");
            a.Db(0x8B, 0x48, 0x10);                                                   // mov ecx, [eax+0x10]     (the pathfinder)
            a.Db(0x85, 0xC9).Je("notbuilding");
            a.Db(0x56);                                                               // push esi
            a.CallAbs(Offsets.Fn_AddToPathfindMap);
            a.Label("notbuilding");
            a.Db(0x8B, 0xC6);                                                         // mov eax, esi
            a.Label("done");
            a.Db(0xA3).D32(cave + OffSpawnResult);                                    // result = eax
            a.Db(0xC7, 0x05).D32(cave + OffSpawnBusy).D32(2);                         // busy = 2   (finished)
            a.Db(0x5F, 0x5E, 0x5D, 0xC3);                                             // pop edi ; pop esi ; pop ebp ; ret   (the caller restores the stack)
            return a.Build(cave + OffSpawnRoutine);
        }

        byte[] BuildBookRoutine(uint cave)
        {
            // uint useSpellBook(AsciiString* bookName, Team* team, Player* player, uint existingBookId)  -> the id now in use, or 0
            // With existingBookId = 0 the book is created first, exactly as the games do it for every player when a match
            // starts (RotWK 0x006B183D, BFME2 0x006B0D00): find the template, new object for the player's team, "build
            // complete". Then - in the same step of the game, so the bar never shows one book while commands go to another -
            // the id is written into the Player and the spell bar is told to read the book again.
            var a = new MainThreadHook.Asm();
            a.Db(0x55, 0x8B, 0xEC, 0x56);                                             // push ebp ; mov ebp, esp ; push esi
            a.Db(0xC7, 0x05).D32(cave + OffSpawnBusy).D32(1);                         // busy = 1
            a.Db(0x8B, 0x45, 0x14);                                                   // mov eax, [ebp+0x14]     (existing id)
            a.Db(0x85, 0xC0).Jne("haveid");
            a.Db(0xFF, 0x75, 0x08);                                                   // push [ebp+8]            (name)
            a.Db(0x8B, 0x0D).D32(Offsets.TheThingFactory);                            // mov ecx, [TheThingFactory]
            a.CallAbs(Offsets.Fn_FindTemplate);
            a.Db(0x85, 0xC0).Je("done");
            a.Db(0x6A, 0x00);                                                         // push 0
            a.Db(0x68).D32(cave + OffZero16);                                         // push &zero16
            a.Db(0xFF, 0x75, 0x0C);                                                   // push [ebp+0xC]          (team)
            a.Db(0x50);                                                               // push eax                (template)
            a.Db(0x8B, 0x0D).D32(Offsets.TheThingFactory);                            // mov ecx, [TheThingFactory]
            a.CallAbs(Offsets.Fn_NewObject);
            a.Db(0x85, 0xC0).Je("done");
            a.Db(0x8B, 0xF0);                                                         // mov esi, eax
            a.Db(0x8B, 0xCE);                                                         // mov ecx, esi
            a.CallAbs(Offsets.Fn_OnBuildComplete);
            a.Db(0x8B, 0x46, (byte)Offsets.Object_Id);                                // mov eax, [esi+id]
            a.Db(0x85, 0xC0).Je("done");
            a.Label("haveid");
            a.Db(0x8B, 0x4D, 0x10);                                                   // mov ecx, [ebp+0x10]     (player)
            a.Db(0x89, 0x81).D32(Offsets.Player_SpellBookId);                         // mov [ecx+spellBookId], eax
            a.Db(0x8B, 0xF0);                                                         // mov esi, eax            (keep the id)
            a.Db(0xA1).D32(Offsets.TheHud);                                           // mov eax, [TheHud]
            a.Db(0x85, 0xC0).Je("nohud");
            a.Db(0x8B, 0x40, (byte)Offsets.Hud_Impl);                                 // mov eax, [eax+0x10]
            a.Db(0x85, 0xC0).Je("nohud");
            a.Db(0x8B, 0x80).D32(Offsets.Hud_SpellPanel);                             // mov eax, [eax+0x94]     (the spell bar)
            a.Db(0x85, 0xC0).Je("nohud");
            a.Db(0x39, 0x48, (byte)Offsets.SpellPanel_Player);                        // cmp [eax+0x28], ecx     (only if it really holds this player)
            a.Jne("nohud");
            a.Db(0xC7, 0x40, (byte)Offsets.SpellPanel_Player).D32(0);                 // mov dword [eax+0x28], 0 (it fills itself again on its next update)
            a.Label("nohud");
            a.Db(0xA1).D32(Offsets.TheControlBar);                                    // mov eax, [TheControlBar]
            a.Db(0x85, 0xC0).Je("nobar");
            a.Db(0xC6, 0x40, (byte)Offsets.ControlBar_Dirty, 0x01);                   // mov byte [eax+0x28], 1  (redraw)
            a.Label("nobar");
            a.Db(0x8B, 0xC6);                                                         // mov eax, esi
            a.Label("done");
            a.Db(0xA3).D32(cave + OffSpawnResult);                                    // result = eax
            a.Db(0xC7, 0x05).D32(cave + OffSpawnBusy).D32(2);                         // busy = 2
            a.Db(0x5E, 0x5D, 0xC3);                                                   // pop esi ; pop ebp ; ret
            return a.Build(cave + OffBookRoutine);
        }

        bool EnsureSpawnRoutine()
        {
            if (_hook == null) return false; if (_spawnRoutineFor == _hook) return true;
            if (!_hook.Installed && !_hook.Install()) return false;
            byte[] code = BuildSpawnRoutine(_hook.Cave); if (code.Length > MaxSpawnRoutine) throw new InvalidOperationException("spawn routine too large");
            byte[] book = BuildBookRoutine(_hook.Cave); if (book.Length > MaxBookRoutine) throw new InvalidOperationException("spell book routine too large");
            if (!_m.WriteBytes(_hook.Cave + OffZero16, new byte[16]) || !_m.WriteBytes(_hook.Cave + OffSpawnRoutine, code) || !_m.WriteBytes(_hook.Cave + OffBookRoutine, book)) return false;
            _spawnRoutineFor = _hook; return true;
        }

        /// <summary>The local player's own team, if the game accepts new objects for that player right now.</summary>
        bool LocalTeam(GameSnapshot g, out uint team)
        {
            team = 0;
            // while one of these two bytes is set on a player the engine quietly gives new objects to the neutral player: do nothing then
            byte[] fa = _m.ReadBytes(g.Player + Offsets.Player_NoObjectsFlagA, 1), fb = _m.ReadBytes(g.Player + Offsets.Player_NoObjectsFlagB, 1);
            if (fa == null || fb == null || fa[0] != 0 || fb[0] != 0) { _facWhyNot = "the game does not accept new objects for your player at the moment"; return false; }
            team = _m.U32(g.Player + Offsets.Player_DefaultTeam);
            // the default team must really belong to the local player (guards against a wrong offset on an unknown build)
            uint o1 = team > 0x10000 ? _m.U32(team + Offsets.Team_Owner1) : 0;
            if (o1 < 0x10000 || _m.U32(o1 + Offsets.Team_Owner2) != g.Player) { _facWhyNot = "your player's team was not found"; team = 0; return false; }
            return true;
        }

        /// <summary>
        /// Runs one of the two routines above on the game's thread. Returns SpawnOk (res = its result), SpawnNotReached (the
        /// game never started on it - paused or minimised - and the request was withdrawn, so it may be asked again), or
        /// SpawnRefused (the game took it but did not finish in time: never ask again, that could do it twice).
        /// </summary>
        int CallTracked(uint routine, uint[] args, out uint res)
        {
            res = 0; if (!_m.WriteU32(_hook.Cave + OffSpawnBusy, 0)) return SpawnCannot;
            if (_hook.CallCave(routine, args, 1500, out res)) return SpawnOk;
            // Not answered in time. Either the game never started on it (busy is still 0) or it is in the middle of it
            // (loading a model it has not shown yet can take a while): then wait for it to finish.
            uint busy; if (!_m.TryU32(_hook.Cave + OffSpawnBusy, out busy) || busy == 0) return SpawnNotReached;
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            while (busy != 2 && DateTime.UtcNow < until && !_stop) { Thread.Sleep(25); if (!_m.TryU32(_hook.Cave + OffSpawnBusy, out busy)) return SpawnRefused; }
            if (busy != 2) return SpawnRefused;
            res = _m.U32(_hook.Cave + OffSpawnResult); return SpawnOk;
        }

        /// <summary>Puts an object name into the trainer's page inside the game, in the form the engine expects (an AsciiString).</summary>
        bool WriteName(string name)
        {
            byte[] text = Encoding.ASCII.GetBytes(name); if (text.Length == 0 || text.Length > 120) return false;
            var data = new byte[8 + text.Length + 1];
            BitConverter.GetBytes(0x00100000).CopyTo(data, 0);                         // reference count: large, so the engine never tries to free this block
            BitConverter.GetBytes((ushort)text.Length).CopyTo(data, 4); BitConverter.GetBytes((ushort)(text.Length + 1)).CopyTo(data, 6);
            text.CopyTo(data, 8);
            return _m.WriteBytes(_hook.Cave + OffStrData, data) && _m.WriteU32(_hook.Cave + OffStrSlot, _hook.Cave + OffStrData);
        }

        // results of SpawnOne
        const int SpawnOk = 0, SpawnNotReached = 1, SpawnUnknownName = 2, SpawnRefused = 3, SpawnCannot = 4, SpawnNotMine = 5;

        bool OwnedByLocalPlayer(GameSnapshot g, uint obj)
        {
            uint team = _m.U32(obj + Offsets.Object_Team); if (team < 0x10000) return false;
            uint o1 = _m.U32(team + Offsets.Team_Owner1); return o1 > 0x10000 && _m.U32(o1 + Offsets.Team_Owner2) == g.Player;
        }

        /// <summary>
        /// Creates one object for the local player at (x, y) on the ground. Returns one of the Spawn* results:
        /// NotReached = the game did not take the request (paused / minimised) and nothing happened, so it may be asked again.
        /// </summary>
        int SpawnOne(GameSnapshot g, string name, float x, float y, float angle, out uint obj)
        {
            obj = 0; uint team; if (!LocalTeam(g, out team)) return SpawnCannot;
            if (!EnsureSpawnRoutine() || !WriteName(name)) { _facWhyNot = "could not write the request into the game"; return SpawnCannot; }
            var pos = new byte[12]; BitConverter.GetBytes(x).CopyTo(pos, 0); BitConverter.GetBytes(y).CopyTo(pos, 4);     // z is filled in by the game (ground height)
            if (!_m.WriteBytes(_hook.Cave + OffSpawnPos, pos)) { _facWhyNot = "could not write the request into the game"; return SpawnCannot; }
            uint res; int r = CallTracked(OffSpawnRoutine, new[] { _hook.Cave + OffStrSlot, team, _hook.Cave + OffSpawnPos, Bits(angle) }, out res);
            if (r != SpawnOk) { if (r == SpawnCannot) _facWhyNot = "could not write the request into the game"; return r; }
            if (_m.U32(_hook.Cave + OffSpawnFound) != 1) return SpawnUnknownName;
            if (res < 0x10000) return SpawnRefused;
            obj = res;
            return OwnedByLocalPlayer(g, obj) ? SpawnOk : SpawnNotMine;
        }

    }
}
