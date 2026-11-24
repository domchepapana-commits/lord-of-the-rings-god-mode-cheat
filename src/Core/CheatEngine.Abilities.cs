using System;
using System.Collections.Generic;
using System.Threading;

namespace WitchKingTrainer.Core
{
    /// <summary>Power points, power tree, ability cooldowns and hero levels. All of it touches the local human player only.</summary>
    public sealed partial class CheatEngine
    {
        // ------------------------------------------------------------------ shared helpers
        readonly Dictionary<uint, uint> _teamOwner = new Dictionary<uint, uint>();

        /// <summary>Player that controls an object: [[[obj+0x31C]+0x30]+8] (what Object::getControllingPlayer does).</summary>
        uint OwnerFromTeam(uint team)
        {
            if (team == 0) return 0; uint p;
            if (_teamOwner.TryGetValue(team, out p)) return p;
            uint x = _m.U32(team + Offsets.Team_Owner1); p = x != 0 ? _m.U32(x + Offsets.Team_Owner2) : 0;
            _teamOwner[team] = p; return p;
        }

        string TemplateNameOf(uint obj)
        {
            uint tmpl = _m.U32(obj + Offsets.Object_Template), s = tmpl != 0 ? _m.U32(tmpl + Offsets.Template_Name) : 0;
            return s != 0 ? (_m.AsciiZ(s + Offsets.AsciiString_Text, 96) ?? "?") : "?";
        }

        static string PrettyName(string template)
        {
            foreach (string p in new[] { "EvilMen", "Gondor", "Rohan", "Arnor", "Men", "Elven", "Dwarven", "Mordor", "Isengard", "Wild", "Angmar", "Neutral", "CreateAHero" })
                if (template.StartsWith(p, StringComparison.OrdinalIgnoreCase) && template.Length > p.Length) { template = template.Substring(p.Length); break; }
            int us = template.IndexOf('_'); if (us > 2) template = template.Substring(0, us);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < template.Length; i++) { if (i > 0 && char.IsUpper(template[i]) && char.IsLower(template[i - 1])) sb.Append(' '); sb.Append(template[i]); }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ unlimited power points
        const int PowerPointsTarget = 250;
        bool _ppSaved; int _ppOrig;

        void ApplyPowerPoints(GameSnapshot g)
        {
            uint addr = g.Player + Offsets.Player_PowerPoints; uint raw;
            if (!_m.TryU32(addr, out raw)) { Set(CheatId.PowerPoints, _powerPoints ? CheatState.Error : CheatState.Off, _powerPoints ? "Could not read the game" : ""); return; }
            int cur = unchecked((int)raw);
            if (_powerPoints)
            {
                if (!_ppSaved) { _ppSaved = true; _ppOrig = cur; }
                if (cur < PowerPointsTarget) { _m.WriteI32(addr, PowerPointsTarget); cur = PowerPointsTarget; }
                Set(CheatId.PowerPoints, CheatState.Active, "Power points kept at " + PowerPointsTarget);
            }
            else
            {
                if (_ppSaved) { _ppSaved = false; if (cur >= 0 && cur <= PowerPointsTarget) { _m.WriteI32(addr, Math.Max(0, _ppOrig)); cur = Math.Max(0, _ppOrig); } }
                Set(CheatId.PowerPoints, CheatState.Off, "");
            }
            _powerPointsText = "Power points " + cur;
        }

        // ------------------------------------------------------------------ unlock all powers
        HashSet<uint> OwnedSciences(uint player)
        {
            var set = new HashSet<uint>(); uint beg = _m.U32(player + Offsets.Player_SciencesBegin), end = _m.U32(player + Offsets.Player_SciencesEnd);
            if (beg == 0 || end < beg || end - beg > 4096) return set;
            byte[] b = _m.ReadBytes(beg, (int)(end - beg)); if (b == null) return set;
            for (int i = 0; i + 4 <= b.Length; i += 4) set.Add(BitConverter.ToUInt32(b, i));
            return set;
        }

        /// <summary>
        /// Grants every power on the local player's own power tree. The list comes from the game itself: the command set its
        /// power-tree screen uses for this player (so it is always the right faction and the skirmish variant), and each power is
        /// granted through the engine's own Player::grantScience, which notifies the spellbook and refreshes the interface.
        /// </summary>
        void HandleUnlock(GameSnapshot g)
        {
            if (_unlockRequest == 0) return; _unlockRequest = 0;
            if (!EnsureHookFor("Unlock All Powers")) { _unlockText = "Not available: " + (_hook.LastError ?? "could not reach the game"); return; }
            uint bar = _m.U32(Offsets.TheControlBar), cs;
            if (bar == 0 || !_hook.Call(Offsets.Fn_GetPurchaseScienceCommandSet, bar, new[] { g.Player }, 700, out cs))
            { _unlockText = "Resume the match and press again"; Event("Unlock All Powers: the game is paused - resume the match and press the button again.", true); return; }
            if (cs < 0x10000) { _unlockText = "No power tree found"; Event("Unlock All Powers: this player has no power tree.", true); return; }
            _unlockText = UnlockCommandSet(g, cs, "your faction", "Unlock All Powers");
        }

        /// <summary>
        /// Grants every power that the given power-tree command set offers (the local player's own tree, or - All Factions -
        /// the tree of another faction, which the game keeps under that faction's name). Returns the status line.
        /// </summary>
        string UnlockCommandSet(GameSnapshot g, uint cs, string whose, string what)
        {
            var ids = new List<uint>(); byte[] slots = _m.ReadBytes(cs + Offsets.CommandSet_Slots, 4 * Offsets.CommandSet_SlotCount);
            if (slots == null) return "Could not read the power tree";
            for (int i = 0; i < Offsets.CommandSet_SlotCount; i++)
            {
                uint b = BitConverter.ToUInt32(slots, i * 4); if (b < 0x10000) continue;
                if (_m.I32(b + Offsets.Button_Command) != Offsets.Command_PurchaseScience) continue;
                uint beg = _m.U32(b + Offsets.Button_SciencesBegin), end = _m.U32(b + Offsets.Button_SciencesEnd);
                if (beg < 0x10000 || beg == end) continue;
                uint id = _m.U32(beg); if (id == 0 || id == 0xFFFFFFFF || ids.Contains(id)) continue;
                ids.Add(id);
            }
            if (ids.Count == 0) { Event(what + ": no powers were found for this faction.", true); return "No powers found"; }

            HashSet<uint> owned = OwnedSciences(g.Player); int granted = 0;
            foreach (uint id in ids)
            {
                if (_stop) return ""; if (owned.Contains(id)) continue; uint r;
                if (!_hook.Call(Offsets.Fn_GrantScience, g.Player, new[] { id }, 700, out r)) break;
                granted++;
            }
            Thread.Sleep(250);
            owned = OwnedSciences(g.Player); int have = 0; foreach (uint id in ids) if (owned.Contains(id)) have++;
            SweepCooldowns(g, true);      // newly granted powers start ready to use
            if (have == ids.Count) { Event("All " + ids.Count + " powers of " + whose + " are unlocked" + (granted == 0 ? " (they already were)." : "."), false); return "All " + ids.Count + " powers unlocked"; }
            Event(what + ": " + have + " of " + ids.Count + " unlocked - resume the match and press again.", true); return have + " of " + ids.Count + " unlocked";
        }

        bool EnsureHookFor(string what)
        {
            if (_hook.Installed) return true;
            if (_hook.Install()) return true;
            Event(what + ": " + (_hook.LastError ?? "could not connect to the game's update loop"), true); return false;
        }

        // ------------------------------------------------------------------ no ability cooldowns
        // Every cooldown-limited ability in this game is a "special power" whose module stores the logic frame at which it is ready
        // again. The game never re-checks that timer when the ability executes; it only uses it to decide whether the button is
        // ready. So the cheat simply keeps those ready-frames at "now" for the local player's own objects (fortress, spellbook,
        // heroes, units). Enemy objects are never touched, and switching off just lets the normal timers run again.
        DateTime _nextSweep = DateTime.MinValue; int _cooldownResets;

        // IMPORTANT: this is done by a small routine that runs on the game's OWN thread, inside its update step. An earlier
        // version wrote to the ability timers from the trainer's process; if a unit died (or the match ended) at that very
        // moment the write landed in memory the game had just freed, which could corrupt its heap and crash it later.
        const uint OffPokeRoutine = 0x700, OffCooldownRoutine = 0x720, MaxCooldownRoutine = 0x1E0;
        MainThreadHook _routinesFor;

        byte[] BuildCooldownRoutine()
        {
            var a = new MainThreadHook.Asm();
            a.Db(0x33, 0xED);                                                        // xor ebp, ebp            (timers reset this call)
            a.Db(0xA1).D32(Offsets.TheGameLogic);                                    // mov eax, [TheGameLogic]
            a.Db(0x85, 0xC0).Je("done");
            a.Db(0x83, 0xB8).D32(Offsets.GameLogic_GameMode).Db((byte)Offsets.GameMode_Skirmish).Jne("done");   // offline skirmish only
            a.Db(0x8B, 0x58, (byte)Offsets.GameLogic_Frame);                         // mov ebx, [eax+frame]    (now)
            a.Db(0x8B, 0xB0).D32(Offsets.GameLogic_FirstObject);                     // mov esi, [eax+firstObject]
            a.Db(0x8B, 0x3D).D32(Offsets.ThePlayerList);                             // mov edi, [ThePlayerList]
            a.Db(0x85, 0xFF).Je("done");
            a.Db(0x8B, 0x7F, (byte)Offsets.PlayerList_Local);                        // mov edi, [edi+local]    (the human player)
            a.Db(0x85, 0xFF).Je("done");
            a.Label("obj");
            a.Db(0x85, 0xF6).Je("shared");                                           // test esi, esi
            a.Db(0x8B, 0x86).D32(Offsets.Object_Team);                               // mov eax, [esi+team]
            a.Db(0x85, 0xC0).Je("next");
            a.Db(0x8B, 0x40, (byte)Offsets.Team_Owner1);                             // mov eax, [eax+0x30]
            a.Db(0x85, 0xC0).Je("next");
            a.Db(0x39, 0x78, (byte)Offsets.Team_Owner2).Jne("next");                 // cmp [eax+8], edi        (only my own objects)
            a.Db(0x8B, 0x86).D32(Offsets.Object_FireWeaponLockout);                  // mov eax, [esi+lockout]
            a.Db(0x2B, 0xC3, 0x48, 0x3D).D32(99999).Ja("mods");                      // sub eax, ebx ; dec eax ; cmp eax, 99999 ; ja mods
            a.Db(0x89, 0x9E).D32(Offsets.Object_FireWeaponLockout).Db(0x45);         // mov [esi+lockout], ebx ; inc ebp
            a.Label("mods");
            a.Db(0x8B, 0x96).D32(Offsets.Object_Modules);                            // mov edx, [esi+modules]  (null-terminated array)
            a.Db(0x85, 0xD2).Je("next");
            a.Label("mod");
            a.Db(0x8B, 0x0A, 0x85, 0xC9).Je("next");                                 // mov ecx, [edx] ; test ecx, ecx
            a.Db(0x8B, 0x41, (byte)Offsets.Module_SecondaryVtable);                  // mov eax, [ecx+0xC]
            a.Db(0x3D).D32(Offsets.SpecialPowerVtblA).Jne("tryB");
            a.Db(0x8D, 0x49, (byte)Offsets.SpecialPowerA_ReadyFrame).Jmp("clamp");   // lea ecx, [ecx+0x18]
            a.Label("tryB");
            a.Db(0x3D).D32(Offsets.SpecialPowerVtblB1).Je("isB");
            a.Db(0x3D).D32(Offsets.SpecialPowerVtblB2).Jne("nextmod");
            a.Label("isB");
            a.Db(0x8D, 0x49, (byte)Offsets.SpecialPowerB_ReadyFrame);                // lea ecx, [ecx+0x28]
            a.Label("clamp");
            a.Db(0x8B, 0x01, 0x2B, 0xC3, 0x48, 0x3D).D32(999999).Ja("nextmod");      // mov eax,[ecx] ; sub eax,ebx ; dec eax ; cmp eax, 999999
            a.Db(0x89, 0x19, 0x45);                                                  // mov [ecx], ebx ; inc ebp
            a.Label("nextmod");
            a.Db(0x83, 0xC2, 0x04).Jmp("mod");                                       // add edx, 4
            a.Label("next");
            a.Db(0x8B, 0xB6).D32(Offsets.Object_Next).Jmp("obj");                    // mov esi, [esi+next]
            a.Label("shared");
            a.Db(0x8B, 0x97).D32(Offsets.Player_SharedTimers);                       // mov edx, [edi+sharedTimers]  (list head)
            a.Db(0x85, 0xD2).Je("done");
            a.Db(0x8B, 0x0A);                                                        // mov ecx, [edx]
            a.Db(0xBE).D32(64);                                                      // mov esi, 64
            a.Label("sh");
            a.Db(0x3B, 0xCA).Je("done");                                             // cmp ecx, edx
            a.Db(0x85, 0xC9).Je("done");
            a.Db(0x8B, 0x41, (byte)Offsets.SharedTimer_ReadyFrame, 0x2B, 0xC3, 0x48, 0x3D).D32(999999).Ja("shnext");
            a.Db(0x89, 0x59, (byte)Offsets.SharedTimer_ReadyFrame, 0x45);            // mov [ecx+0xC], ebx ; inc ebp
            a.Label("shnext");
            a.Db(0x8B, 0x09, 0x4E).Jne("sh");                                        // mov ecx, [ecx] ; dec esi ; jnz sh
            a.Label("done");
            a.Db(0x8B, 0xC5, 0xC3);                                                  // mov eax, ebp ; ret
            return a.Build();
        }

        /// <summary>Writes the trainer's helper routines into its page inside the game (once per connection).</summary>
        bool EnsureCaveRoutines()
        {
            if (_hook == null) return false; if (_routinesFor == _hook) return true;
            if (!_hook.Installed && !_hook.Install()) return false;
            byte[] sweep = BuildCooldownRoutine(); if (sweep.Length > MaxCooldownRoutine) throw new InvalidOperationException("cooldown routine too large");
            byte[] poke = { 0x8B, 0x44, 0x24, 0x04, 0x8A, 0x4C, 0x24, 0x08, 0x88, 0x08, 0xC3 };   // mov eax,[esp+4] ; mov cl,[esp+8] ; mov [eax],cl ; ret
            if (!_m.WriteBytes(_hook.Cave + OffCooldownRoutine, sweep) || !_m.WriteBytes(_hook.Cave + OffPokeRoutine, poke)) return false;
            _routinesFor = _hook; return true;
        }

        /// <summary>Writes one byte on the game's own thread (inside its update step, where nothing is being freed).</summary>
        bool PokeByte(uint addr, byte value)
        {
            uint res; return EnsureCaveRoutines() && _hook.CallCave(OffPokeRoutine, new[] { addr, (uint)value }, 350, out res);
        }

        /// <summary>Makes every ability of the local player's objects ready now. force = do it immediately.</summary>
        void SweepCooldowns(GameSnapshot g, bool force)
        {
            DateTime t = DateTime.UtcNow; if (!force && t < _nextSweep) return;
            _nextSweep = t.AddMilliseconds(150);
            uint n = 0;
            _sweepOk = EnsureCaveRoutines() && _hook.CallCave(OffCooldownRoutine, null, 400, out n);
            if (_sweepOk) _cooldownResets += (int)Math.Min(n, 100000u);
        }
        bool _sweepOk = true;

        void ApplyCooldowns(GameSnapshot g)
        {
            if (!_noCooldowns) { Set(CheatId.NoCooldowns, CheatState.Off, ""); return; }
            SweepCooldowns(g, false);
            if (_hook != null && _hook.Cave != 0) _m.WriteI32(_hook.Cave + 0x7C, _cooldownResets);   // readable by the test harness
            if (_sweepOk) Set(CheatId.NoCooldowns, CheatState.Active, "Instant recharge  ·  " + _cooldownResets + " so far");
            else Set(CheatId.NoCooldowns, CheatState.Pending, "Waiting for the game (resume the match)");
        }

        // ------------------------------------------------------------------ hero level up
        List<uint> SelectedObjects()
        {
            var res = new List<uint>(); uint ui = _m.U32(Offsets.TheInGameUI); if (ui < 0x10000) return res;
            int count = _m.I32(ui + Offsets.UI_SelectedCount); uint head = _m.U32(ui + Offsets.UI_SelectedList);
            if (count <= 0 || count > 2000 || head < 0x10000) return res;
            int k = 0;
            for (uint node = _m.U32(head); node != head && node > 0x10000 && k < count; node = _m.U32(node), k++)
            { uint draw = _m.U32(node + 8), obj = draw > 0x10000 ? _m.U32(draw + Offsets.Drawable_Object) : 0; if (obj > 0x10000) res.Add(obj); }
            return res;
        }

        List<uint> LocalHeroes(GameSnapshot g)
        {
            var res = new List<uint>(); _teamOwner.Clear();
            uint gl = _m.U32(Offsets.TheGameLogic), o = gl != 0 ? _m.U32(gl + Offsets.GameLogic_FirstObject) : 0; int guard = 0; byte[] buf = new byte[0x320];
            var heroTemplate = new Dictionary<uint, bool>();
            while (o != 0 && guard++ < 40000)
            {
                if (!_m.ReadBytes(o, buf, buf.Length)) break;
                uint tmpl = BitConverter.ToUInt32(buf, (int)Offsets.Object_Template); bool hero;
                if (!heroTemplate.TryGetValue(tmpl, out hero)) { hero = tmpl > 0x10000 && (_m.U32(tmpl + Offsets.Template_KindOfHeroDword) & Offsets.KindOf_HeroBit) != 0; heroTemplate[tmpl] = hero; }
                if (hero && OwnerFromTeam(BitConverter.ToUInt32(buf, (int)Offsets.Object_Team)) == g.Player) res.Add(o);
                o = BitConverter.ToUInt32(buf, (int)Offsets.Object_Next);
            }
            return res;
        }

        /// <summary>Raises heroes to their maximum level through the engine's own "gain levels" routine, so every level-up effect applies.</summary>
        void HandleHeroLevel(GameSnapshot g)
        {
            int req = _levelRequest; if (req == 0) return; _levelRequest = 0;
            List<uint> heroes = LocalHeroes(g), targets = heroes;
            if (req == 1)
            {
                List<uint> sel = SelectedObjects(); targets = heroes.FindAll(h => sel.Contains(h));
                if (targets.Count == 0) { _heroText = heroes.Count == 0 ? "You have no heroes on the map yet." : "No hero is selected - select one of your heroes first."; Event("Hero level up: " + _heroText, true); return; }
            }
            else if (heroes.Count == 0) { _heroText = "You have no heroes on the map yet."; Event("Hero level up: you have no heroes on the map yet.", true); return; }
            if (!EnsureHookFor("Hero level up")) { _heroText = "Not available: " + (_hook.LastError ?? "could not reach the game"); return; }
            uint grant = _m.U32(Offsets.TheDelayedLevelGrant);
            if (grant > 0x10000 && (_m.U32(grant + 0x14) & 0xFF) != 0) { _heroText = "The game is busy - try again in a moment."; return; }

            var before = new Dictionary<uint, int>(); int called = 0;
            foreach (uint h in targets)
            {
                if (_stop) return; uint tracker = _m.U32(h + Offsets.Object_ExperienceTracker), r; if (tracker < 0x10000) continue;
                before[h] = _m.I32(tracker + Offsets.Tracker_Level);
                if (!_hook.Call(Offsets.Fn_GainLevels, tracker, new uint[] { 32, 1, 0 }, 700, out r)) { _heroText = "Resume the match and press again."; Event("Hero level up: the game is paused - resume the match and press again.", true); return; }
                called++;
            }
            Thread.Sleep(450);   // the engine applies queued level grants later in the same logic frame
            var parts = new List<string>(); int raised = 0;
            foreach (uint h in targets)
            {
                uint tracker = _m.U32(h + Offsets.Object_ExperienceTracker); if (tracker < 0x10000 || !before.ContainsKey(h)) continue;
                int lvl = _m.I32(tracker + Offsets.Tracker_Level); if (lvl > before[h]) raised++;
                if (parts.Count < 3) parts.Add(PrettyName(TemplateNameOf(h)) + " level " + lvl);
            }
            _heroText = string.Join(",  ", parts.ToArray()) + (targets.Count > 3 ? "  (+" + (targets.Count - 3) + " more)" : "");
            Event(raised > 0 ? "Hero level up: " + _heroText + "." : "Hero level up: already at maximum level (" + _heroText + ").", false);
            if (_noCooldowns) SweepCooldowns(g, true);
        }
    }
}
