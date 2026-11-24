using System;
using System.Collections.Generic;

namespace WitchKingTrainer.Core
{
    /// <summary>Permanent Gollum marker on the in-game minimap, and Freeze Enemy AI.</summary>
    public sealed partial class CheatEngine
    {
        // ------------------------------------------------------------------ permanent Gollum marker
        // Version 1 flashed "under attack" radar events every few seconds, which looked exactly like the game's own attack
        // alerts. Version 2 owns ONE long-lived radar event of the "beacon" kind (never the attack kind), keeps it alive while
        // tracking is on, and moves it by updating its position fields, so there are no repeated alerts and no sounds.
        uint _mkSlot, _mkStamp, _gObj, _gId; DateTime _nextLocate = DateTime.MinValue, _mkCreated = DateTime.MinValue, _nextMarkerTry = DateTime.MinValue; string _gName = "";
        const double MarkerRefreshSeconds = 20;   // the game's ping animation slowly fades, so the marker is renewed to stay bright

        bool MarkerAlive()
        {
            if (_mkSlot == 0) return false; byte[] e = _m.ReadBytes(_mkSlot, 0x10);
            return e != null && BitConverter.ToInt32(e, 0) == Offsets.RadarEvent_Marker && e[4] == 1 && BitConverter.ToUInt32(e, 8) == _mkStamp;
        }

        uint ClientFrame() { uint gc = _m.U32(Offsets.TheGameClient); return gc > 0x10000 ? _m.U32(gc + Offsets.GameClient_Frame) : 0; }

        bool CreateMarker(GollumInfo i)
        {
            if (!EnsureHook(CheatId.TrackGollum)) return false;
            uint radar = _m.U32(Offsets.TheRadar), pos, res; if (radar == 0 || !WritePos(i, out pos)) return false;
            if (!_hook.Call(Offsets.Fn_RadarCreateEvent, radar, new[] { pos, (uint)Offsets.RadarEvent_Marker, Bits(3600f) }, 450, out res)) return false;
            byte[] all = _m.ReadBytes(radar + Offsets.Radar_Events, Offsets.Radar_EventCount * Offsets.Radar_EventSize); if (all == null) return false;
            int best = -1; uint bestFrame = 0;
            for (int k = 0; k < Offsets.Radar_EventCount; k++)
            {
                int o = k * Offsets.Radar_EventSize;
                if (BitConverter.ToInt32(all, o) != Offsets.RadarEvent_Marker || all[o + 4] != 1) continue;
                if (Math.Abs(BitConverter.ToSingle(all, o + 0x34) - i.X) > 0.5f || Math.Abs(BitConverter.ToSingle(all, o + 0x38) - i.Y) > 0.5f) continue;
                uint created = BitConverter.ToUInt32(all, o + 8); if (best < 0 || created >= bestFrame) { best = k; bestFrame = created; }
            }
            if (best < 0) return false;
            _mkSlot = radar + Offsets.Radar_Events + (uint)(best * Offsets.Radar_EventSize); _mkStamp = bestFrame; _mkCreated = DateTime.UtcNow;
            return true;
        }

        void UpdateMarker(GollumInfo i)
        {
            uint radar = _m.U32(Offsets.TheRadar); if (_mkSlot == 0 || radar == 0) return;
            float sx = _m.F32(radar + Offsets.Radar_CellScaleX), sy = _m.F32(radar + Offsets.Radar_CellScaleY); if (sx < 0.01f || sy < 0.01f) return;
            int cx = Math.Max(0, Math.Min(127, (int)(i.X / sx))), cy = Math.Max(0, Math.Min(127, (int)(i.Y / sy)));
            uint now = ClientFrame(); if (now == 0) return;
            var w = new byte[12]; BitConverter.GetBytes(i.X).CopyTo(w, 0); BitConverter.GetBytes(i.Y).CopyTo(w, 4); BitConverter.GetBytes(i.Z).CopyTo(w, 8);
            _m.WriteBytes(_mkSlot + 0x34, w);
            var c = new byte[8]; BitConverter.GetBytes(cx).CopyTo(c, 0); BitConverter.GetBytes(cy).CopyTo(c, 4); _m.WriteBytes(_mkSlot + 0x40, c);
            // "dead-man" lifetime: always 10 s ahead of the game clock, so the marker disappears by itself if the trainer goes away
            var t = new byte[8]; BitConverter.GetBytes(now + 300).CopyTo(t, 0); BitConverter.GetBytes(now + 285).CopyTo(t, 4); _m.WriteBytes(_mkSlot + 0x0C, t);
        }

        /// <summary>Lets one of our own events fade out (only if the slot still holds the event we created).</summary>
        void FadeEvent(uint slot, uint stamp)
        {
            if (slot == 0) return; byte[] e = _m.ReadBytes(slot, 0x10);
            if (e == null || BitConverter.ToInt32(e, 0) != Offsets.RadarEvent_Marker || e[4] != 1 || BitConverter.ToUInt32(e, 8) != stamp) return;
            uint now = ClientFrame(); var t = new byte[8]; BitConverter.GetBytes(now + 15).CopyTo(t, 0); BitConverter.GetBytes(1u).CopyTo(t, 4); _m.WriteBytes(slot + 0x0C, t);
        }

        void RemoveMarker() { FadeEvent(_mkSlot, _mkStamp); _mkSlot = 0; }

        /// <summary>Finds Gollum (or the dropped Ring) and remembers the object so its position can be followed cheaply.</summary>
        GollumInfo LocateGollum(GameSnapshot g)
        {
            int total; var info = new GollumInfo { MapWidth = g.MapWidth, MapHeight = g.MapHeight, When = DateTime.Now };
            List<WorldObject> l = GameReader.FindObjects(_m, n => IsGollum(n) || IsDroppedRing(n), out total);
            WorldObject gol = l.Find(o => IsGollum(o.TemplateName)), ring = l.Find(o => IsDroppedRing(o.TemplateName)), hit = gol ?? ring;
            if (hit != null)
            {
                _gObj = hit.Address; _gId = hit.Id; _gName = hit.TemplateName; info.X = hit.X; info.Y = hit.Y; info.Z = hit.Z;
                info.State = gol != null ? GollumState.OnMap : GollumState.RingDropped;
                info.Message = gol != null ? string.Format("Gollum is on the map at ({0:0}, {1:0}).", hit.X, hit.Y)
                                           : string.Format("Gollum is gone - the One Ring is lying on the ground at ({0:0}, {1:0}).", hit.X, hit.Y);
            }
            else
            {
                _gObj = 0; info.State = GollumState.NotOnMap;
                info.Message = total == 0 ? "Could not read the game's object list." : "Gollum is not on the map right now (not spawned yet, or killed and the Ring was picked up).";
            }
            return info;
        }

        /// <summary>Cheap position refresh from the remembered object; false if that object is gone (then a full search is needed).</summary>
        bool RefreshGollum()
        {
            if (_gObj == 0) return false; byte[] b = _m.ReadBytes(_gObj, 0x90);
            if (b == null || BitConverter.ToUInt32(b, 0) != Offsets.Object_Vtable || BitConverter.ToUInt32(b, (int)Offsets.Object_Id) != _gId) return false;
            _gollum.X = BitConverter.ToSingle(b, (int)Offsets.Object_PosX); _gollum.Y = BitConverter.ToSingle(b, (int)Offsets.Object_PosY); _gollum.Z = BitConverter.ToSingle(b, (int)Offsets.Object_PosZ);
            _gollum.Message = string.Format(_gollum.State == GollumState.OnMap ? "Gollum is on the map at ({0:0}, {1:0})." : "The One Ring is lying on the ground at ({0:0}, {1:0}).", _gollum.X, _gollum.Y);
            return true;
        }

        void ApplyGollumMarker(GameSnapshot g)
        {
            int req = _findRequest; _findRequest = 0; bool tracking = _track;
            if (!tracking && req == 0) { RemoveMarker(); Set(CheatId.TrackGollum, CheatState.Off, ""); return; }

            bool had = _gollum.State == GollumState.OnMap || _gollum.State == GollumState.RingDropped;
            if (req != 0 || DateTime.UtcNow >= _nextLocate || (had && !RefreshGollum()))
            {
                GollumState before = _gollum.State;
                _gollum = LocateGollum(g); _nextLocate = DateTime.UtcNow.AddSeconds(1.5);
                if (tracking && req == 0 && before != _gollum.State && before != GollumState.Unknown) Event(_gollum.Message, false);
            }
            bool have = _gollum.State == GollumState.OnMap || _gollum.State == GollumState.RingDropped;

            string extra = "";
            if (req == 2) extra = have ? (MoveCamera(_gollum, g) ? " Camera moved there." : " (Resume the match to move the camera.)") : "";
            string status;
            if (tracking && have)
            {
                DateTime utc = DateTime.UtcNow; bool alive = MarkerAlive();
                if (!alive && utc >= _nextMarkerTry && !CreateMarker(_gollum)) _nextMarkerTry = utc.AddSeconds(1);
                else if (alive && (utc - _mkCreated).TotalSeconds > MarkerRefreshSeconds && utc >= _nextMarkerTry)
                {
                    // renew: create the new marker first, and only then let the old one fade
                    uint oldSlot = _mkSlot, oldStamp = _mkStamp;
                    if (CreateMarker(_gollum)) FadeEvent(oldSlot, oldStamp); else { _mkSlot = oldSlot; _mkStamp = oldStamp; _nextMarkerTry = utc.AddSeconds(1); }
                }
                if (MarkerAlive()) { UpdateMarker(_gollum); status = _gollum.State == GollumState.OnMap ? "Gollum is marked on the minimap and followed" : "The dropped Ring is marked on the minimap"; }
                else status = "Marker appears when the match resumes";
            }
            else { RemoveMarker(); status = tracking ? "Watching - Gollum is not on the map" : ""; }
            if (req != 0) Event(_gollum.Message + extra, false);
            Set(CheatId.TrackGollum, tracking ? (have && _mkSlot == 0 ? CheatState.Pending : CheatState.Active) : CheatState.Off, status);
        }

        // ------------------------------------------------------------------ freeze enemy AI
        // Three things together, all undone when the switch goes off:
        //  1. The skirmish AI of every computer player is driven by a "brain" that the AI manager updates once per logic frame,
        //     alliance by alliance; each alliance carries an "enabled" byte that the manager tests first. That byte is held at 0
        //     for alliances that are not allied with the human: no new attacks, targets, spells, building or recruiting.
        //  2. Freezing the brain alone leaves the armies that are already out in the field - and those stand in the player's
        //     base and keep fighting (version 3.0 stopped here, which is why it looked as if nothing happened). So every frozen
        //     computer player is also made to regard the human as NEUTRAL (the engine's own PLAYER_RELATES_PLAYER setting, one
        //     value per player): its units no longer pick the human's units and buildings as targets.
        //  3. Its units are told once to stop where they stand, which ends the attacks already under way.
        // The human's side is untouched and still regards them as enemies.
        readonly List<uint> _frozenAlliances = new List<uint>(), _idledPlayers = new List<uint>();
        readonly Dictionary<uint, int> _relation = new Dictionary<uint, int>();
        readonly Dictionary<uint, uint> _neutralised = new Dictionary<uint, uint>();   // frozen Player* -> address of its "how I regard the human" value
        bool _freezeRepaired;

        /// <summary>Makes a computer player regard the human as neutral (or as an enemy again). Runs on the game's own thread.</summary>
        bool SetRegard(uint player, uint local, int relationship, out uint slot)
        {
            slot = 0; uint res;
            if (!_hook.Installed && !_hook.Install()) return false;
            if (!_hook.Call(Offsets.Fn_SetRelationship, player, new[] { local, (uint)relationship }, 450, out res)) return false;
            slot = res; return res > 0x10000 && _m.I32(res) == relationship;       // the routine returns where it stored the value
        }

        /// <summary>
        /// A computer opponent never regards the human as neutral by itself in a skirmish. If one does while the switch is off, a
        /// trainer was closed abnormally (or while the game was paused) with Freeze on: put that opponent back to normal.
        /// </summary>
        void RepairFreezeLeftovers(GameSnapshot g)
        {
            if (_freezeRepaired || !_hook.Installed) return;
            uint mgr; List<uint> alliances = AiAlliances(out mgr); if (mgr == 0) return;
            bool all = true, any = false;
            foreach (uint a in alliances)
                foreach (uint p in AllianceMembers(a))
                {
                    if (p == g.Player || _m.I32(p + Offsets.Player_Type) != 1) continue;
                    int rel = RelationToLocal(p, g.Player); if (rel < 0) { all = false; continue; }
                    if (rel != Offsets.Relationship_Neutral) continue;
                    uint slot; if (SetRegard(p, g.Player, Offsets.Relationship_Enemies, out slot)) { _relation[p] = Offsets.Relationship_Enemies; any = true; } else all = false;
                    byte[] flag = _m.ReadBytes(a + Offsets.Alliance_Enabled, 1); if (flag != null && flag[0] == 0 && !PokeByte(a + Offsets.Alliance_Enabled, 1)) all = false;
                }
            if (all) _freezeRepaired = true;
            if (any) Event("An enemy AI was still frozen from an earlier trainer session - it plays normally again.", false);
        }

        int RelationToLocal(uint player, uint local)
        {
            int r; if (_relation.TryGetValue(player, out r)) return r;
            if (!_hook.Installed && !_hook.Install()) return -1;
            uint res; if (!_hook.Call(Offsets.Fn_GetRelationship, player, new[] { local }, 450, out res)) return -1;
            r = unchecked((int)res); _relation[player] = r; return r;
        }

        List<uint> AiAlliances(out uint mgr)
        {
            var list = new List<uint>(); mgr = _m.U32(Offsets.TheSkirmishAIManager);
            if (mgr < 0x10000 || _m.U32(mgr) != Offsets.SkirmishAIManager_Vtable) { mgr = 0; return list; }
            uint beg = _m.U32(mgr + Offsets.AIManager_AlliancesBegin), end = _m.U32(mgr + Offsets.AIManager_AlliancesEnd);
            if (beg < 0x10000 || end < beg || end - beg > 4 * 32) return list;
            for (uint p = beg; p < end; p += 4) { uint a = _m.U32(p); if (a > 0x10000) list.Add(a); }
            return list;
        }

        List<uint> AllianceMembers(uint alliance)
        {
            var res = new List<uint>(); uint beg = _m.U32(alliance + 0x0C), end = _m.U32(alliance + 0x10);
            if (beg < 0x10000 || end < beg || end - beg > 4 * 16) return res;
            for (uint m = beg; m < end; m += 4) { uint p = _m.U32(m); if (p > 0x10000 && _m.U32(p) == Offsets.Player_Vtable) res.Add(p); }
            return res;
        }

        void ApplyFreezeAi(GameSnapshot g)
        {
            if (!_freezeAi)
            {
                if (_frozenAlliances.Count > 0 || _idledPlayers.Count > 0 || _neutralised.Count > 0) UnfreezeAi(); else RepairFreezeLeftovers(g);
                Set(CheatId.FreezeAI, _neutralised.Count > 0 ? CheatState.Pending : CheatState.Off, _neutralised.Count > 0 ? "Unfreezing when the match resumes" : ""); return;
            }
            uint mgr; List<uint> alliances = AiAlliances(out mgr);
            if (mgr == 0) { Set(CheatId.FreezeAI, CheatState.Error, "The skirmish AI was not found in this match"); return; }
            int frozenPlayers = 0; bool pending = false;
            foreach (uint a in alliances)
            {
                List<uint> members = AllianceMembers(a); bool enemy = false, unknown = false;
                foreach (uint p in members)
                {
                    if (p == g.Player) { enemy = false; unknown = false; break; }
                    if (_m.I32(p + Offsets.Player_Type) != 1) continue;
                    int rel = RelationToLocal(p, g.Player);
                    if (rel < 0) unknown = true; else if (rel != Offsets.Relationship_Allies) enemy = true;
                }
                if (unknown && !enemy) { pending = true; continue; }
                if (!enemy) continue;
                byte[] flag = _m.ReadBytes(a + Offsets.Alliance_Enabled, 1);
                if (flag == null) continue;
                bool ours = _frozenAlliances.Contains(a);
                if (flag[0] != 0) { if (!PokeByte(a + Offsets.Alliance_Enabled, 0)) { pending = true; continue; } if (!ours) _frozenAlliances.Add(a); }
                else if (!ours) continue;   // the game itself has this alliance switched off (nothing to freeze, nothing to restore later)
                foreach (uint p in members)
                {
                    if (_m.I32(p + Offsets.Player_Type) != 1) continue;
                    // the armies stop choosing the human's units and buildings as targets ...
                    if (!_neutralised.ContainsKey(p))
                    {
                        uint slot; if (!SetRegard(p, g.Player, Offsets.Relationship_Neutral, out slot)) { pending = true; continue; }
                        _neutralised[p] = slot;
                    }
                    else if (_m.I32(_neutralised[p]) != Offsets.Relationship_Neutral)
                    {
                        // something (a script) changed it back while frozen: set it again
                        uint slot; if (SetRegard(p, g.Player, Offsets.Relationship_Neutral, out slot)) { _neutralised[p] = slot; _idledPlayers.Remove(p); } else { pending = true; continue; }
                    }
                    frozenPlayers++;
                    if (_idledPlayers.Contains(p)) continue; uint res;
                    // ... and, once per player, the attacks already under way are stopped (the engine routine behind the IDLE_ALL_UNITS script action)
                    if (_hook.Installed && _hook.Call(Offsets.Fn_SetUnitsIdle, p, new[] { 1u }, 450, out res)) _idledPlayers.Add(p); else pending = true;
                }
            }
            if (frozenPlayers == 0) Set(CheatId.FreezeAI, pending ? CheatState.Pending : CheatState.Active, pending ? "Waiting for the game (resume the match)" : "No enemy computer players in this match");
            else Set(CheatId.FreezeAI, pending ? CheatState.Pending : CheatState.Active, frozenPlayers + " enemy AI frozen  ·  " + (pending ? "resume the match" : "armies stand down"));
        }

        void UnfreezeAi()
        {
            uint mgr; List<uint> alliances = AiAlliances(out mgr);
            // the opponents regard the human as an enemy again first, so the brains that wake up below find the world as they left it
            if (_neutralised.Count > 0)
            {
                GameSnapshot now = GameReader.Read(_m); var left = new Dictionary<uint, uint>();
                foreach (KeyValuePair<uint, uint> kv in _neutralised)
                {
                    uint p = kv.Key, slot = kv.Value, s2;
                    if (!now.InMatch || _m.U32(p) != Offsets.Player_Vtable) continue;                 // that match is over: nothing to undo
                    if (SetRegard(p, now.Player, Offsets.Relationship_Enemies, out s2)) continue;
                    // The game is not running its update step (paused / minimised). A paused match is not being torn down, and
                    // the value lives in the player's own table for the whole match, so it is put back directly.
                    if (slot > 0x10000 && _m.I32(slot) == Offsets.Relationship_Neutral && _m.WriteI32(slot, Offsets.Relationship_Enemies)) continue;
                    left[p] = slot;
                }
                _neutralised.Clear(); foreach (KeyValuePair<uint, uint> kv in left) _neutralised[kv.Key] = kv.Value;
                if (_neutralised.Count > 0) return;                                                   // retried on the next tick; the brains stay off until then
            }
            foreach (uint a in _frozenAlliances)
            {
                if (!alliances.Contains(a)) continue;
                byte[] f = _m.ReadBytes(a + Offsets.Alliance_Enabled, 1); if (f == null || f[0] != 0) continue;
                if (PokeByte(a + Offsets.Alliance_Enabled, 1)) continue;
                // The game is not running its update step (paused / minimised). A paused match is not being torn down, so the
                // AI is switched back on directly - but only if this alliance is still in the game's own list right now.
                uint mgr2; GameSnapshot now = GameReader.Read(_m);
                if (now.InMatch && AiAlliances(out mgr2).Contains(a)) _m.WriteBytes(a + Offsets.Alliance_Enabled, new byte[] { 1 });
            }
            _frozenAlliances.Clear();
            var still = new List<uint>();
            foreach (uint p in _idledPlayers)
            {
                uint res; if (_m.U32(p) != Offsets.Player_Vtable) continue;
                if (!(_hook != null && _hook.Installed && _hook.Call(Offsets.Fn_SetUnitsIdle, p, new[] { 0u }, 450, out res))) still.Add(p);
            }
            _idledPlayers.Clear(); _idledPlayers.AddRange(still);   // retried on the next tick if the game was paused
        }
    }
}
