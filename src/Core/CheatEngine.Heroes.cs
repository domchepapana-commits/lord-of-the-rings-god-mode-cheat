using System;
using System.Collections.Generic;

namespace WitchKingTrainer.Core
{
    /// <summary>One of the local player's heroes, as shown on the MY HEROES page.</summary>
    public sealed class HeroInfo
    {
        public uint Id; public string Template = "", Name = ""; public int Level; public float Health, MaxHealth, X, Y, Z; public bool Selected;
    }

    // MY HEROES (24 Nov 2026). The games' own hero bar has room for 16 portraits (their interface files define exactly
    // Hero1 .. Hero16), so with the heroes of every faction most of them cannot be reached from it. The trainer lists all
    // of the player's heroes in its menu; a click selects that hero in the game and moves the camera to him.
    //   * The list is only read while the page is open (once in about 0.8 s) and only from outside, read-only.
    //   * Selecting is done on the game's own thread by a small routine that repeats, step by step, what the hero bar's
    //     button code does (see Offsets, "MY HEROES"). The trainer hands it an object ID, never a pointer: the routine
    //     looks the hero up itself, so a hero that has just died is simply "not found".
    public sealed partial class CheatEngine
    {
        volatile bool _wantHeroList; volatile uint _heroFocus; volatile string _heroFocusName = "";
        HeroInfo[] _heroes = new HeroInfo[0]; string _heroListText = ""; DateTime _nextHeroScan = DateTime.MinValue;
        int _heroSelectReady; MainThreadHook _heroRoutineFor;
        const uint OffHeroRoutine = 0xE00, MaxHeroRoutine = 0x1E0;   // the page from +0xE00 to its end is free; the routine is about 0x130 bytes

        /// <summary>The menu sets this while the MY HEROES page is shown; the list is not read otherwise.</summary>
        public bool WantHeroList { get { return _wantHeroList; } set { if (_wantHeroList == value) return; _wantHeroList = value; if (value) { _nextHeroScan = DateTime.MinValue; _wake.Set(); } } }
        /// <summary>Select the hero with this object id in the game and move the camera to him.</summary>
        public void RequestHeroFocus(uint id, string displayName) { _heroFocusName = displayName ?? ""; _heroFocus = id; _wake.Set(); }

        static bool SameBytes(byte[] a, byte[] b) { if (a == null || b == null || a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
        bool CodeIs(uint addr, byte[] expected) { return expected != null && addr != 0 && SameBytes(_m.ReadBytes(addr, expected.Length), expected); }

        /// <summary>The game code this feature relies on is exactly the code that was analysed (checked once per connection).</summary>
        bool HeroSelectUsable()
        {
            if (_heroSelectReady != 0) return _heroSelectReady == 1;
            bool ok = Offsets.TheMessageStream != 0 && Offsets.TheInGameUI != 0
                      && CodeIs(Offsets.Fn_FindObjectById, Offsets.Sig_FindObjectById) && CodeIs(Offsets.Fn_IsSelectable, Offsets.Sig_IsSelectable)
                      && CodeIs(Offsets.Fn_MsgAppendBool, Offsets.Sig_MsgAppendBool) && CodeIs(Offsets.Fn_MsgAppendObjectId, Offsets.Sig_MsgAppendObjectId)
                      && CodeIs(Offsets.SelectSiteA, Offsets.Sig_SelectSiteA) && CodeIs(Offsets.SelectSiteB, Offsets.Sig_SelectSiteB);
            _heroSelectReady = ok ? 1 : 2; if (!ok) Log("MY HEROES: selecting is not available on this game version (the camera still goes to the hero)");
            return ok;
        }

        byte[] BuildHeroSelectRoutine(uint cave)
        {
            // uint selectHero(uint objectId, Player* localPlayer)  ->  1 selected, 0 not possible (gone, not yours, not selectable)
            var a = new MainThreadHook.Asm();
            a.Db(0x55, 0x8B, 0xEC, 0x53, 0x56, 0x57);                                   // push ebp ; mov ebp, esp ; push ebx ; push esi ; push edi
            a.Db(0x8B, 0x0D).D32(Offsets.TheGameLogic);                                 // mov ecx, [TheGameLogic]
            a.Db(0x85, 0xC9).Je("fail");
            a.Db(0xFF, 0x75, 0x08);                                                     // push [ebp+8]            (object id)
            a.CallAbs(Offsets.Fn_FindObjectById);
            a.Db(0x85, 0xC0).Je("fail");
            a.Db(0x8B, 0xF0);                                                           // mov esi, eax            (the hero)
            a.Db(0x8B, 0x86).D32(Offsets.Object_Team);                                  // mov eax, [esi+team]     - it must belong to the local player
            a.Db(0x85, 0xC0).Je("fail");
            a.Db(0x8B, 0x40, (byte)Offsets.Team_Owner1);                                // mov eax, [eax+0x30]
            a.Db(0x85, 0xC0).Je("fail");
            a.Db(0x8B, 0x40, (byte)Offsets.Team_Owner2);                                // mov eax, [eax+8]
            a.Db(0x3B, 0x45, 0x0C).Jne("fail");                                         // cmp eax, [ebp+0xC]
            a.Db(0x8B, 0x46, 0x04);                                                     // mov eax, [esi+4]        (template)
            a.Db(0xF6, 0x80, 0x13, 0x01, 0x00, 0x00, 0x04).Je("own");                   // test byte [eax+0x113], 4 : held by a container that is selected in his place?
            a.Db(0x8B, 0x86).D32(Offsets.Object_ContainedIn);                           // mov eax, [esi+containedIn]
            a.Db(0x85, 0xC0).Je("own");
            a.Db(0x8B, 0xF0);                                                           // mov esi, eax
            a.Label("own");
            a.Db(0x8B, 0xCE);                                                           // mov ecx, esi
            a.CallAbs(Offsets.Fn_IsSelectable);
            a.Db(0x84, 0xC0).Je("fail");                                                // test al, al
            a.Db(0x8B, 0xBE).D32(Offsets.Object_Drawable);                              // mov edi, [esi+0x84]     (its drawable)
            a.Db(0x85, 0xFF).Je("fail");
            a.Db(0x8B, 0x87).D32(Offsets.Drawable_Object);                              // mov eax, [edi+0xFC]
            a.Db(0x85, 0xC0).Je("pick");
            a.Db(0x8B, 0x88).D32(Offsets.Object_ContainedIn);                           // mov ecx, [eax+containedIn]
            a.Db(0x85, 0xC9).Je("pick");
            a.Db(0x8B, 0x41, 0x04);                                                     // mov eax, [ecx+4]
            a.Db(0xF6, 0x80, 0x15, 0x01, 0x00, 0x00, 0x20).Je("pick");                  // test byte [eax+0x115], 0x20
            a.Db(0x8B, 0xF1);                                                           // mov esi, ecx            (the group he leads is selected, as the game does)
            a.Db(0x8B, 0xBE).D32(Offsets.Object_Drawable);                              // mov edi, [esi+0x84]
            a.Db(0x85, 0xFF).Je("fail");
            a.Label("pick");
            a.Db(0x8B, 0x0D).D32(Offsets.TheInGameUI);                                  // mov ecx, [TheInGameUI]
            a.Db(0x85, 0xC9).Je("fail");
            a.Db(0x8B, 0x01, 0xFF, 0x90).D32(Offsets.UI_DeselectAllSlot);               // mov eax, [ecx] ; call [eax+0x110]     deselect everything
            a.Db(0x8B, 0x0D).D32(Offsets.TheMessageStream);                             // mov ecx, [TheMessageStream]
            a.Db(0x85, 0xC9).Je("fail");
            a.Db(0x8B, 0x01, 0x68).D32(Offsets.Msg_CreateSelectedGroup);                // mov eax, [ecx] ; push 0x3E9
            a.Db(0xFF, 0x50, (byte)Offsets.MsgStream_AppendSlot);                       // call [eax+0x48]                         new message "create selected group"
            a.Db(0x85, 0xC0).Je("fail");
            a.Db(0x8B, 0xD8);                                                           // mov ebx, eax
            a.Db(0x6A, 0x01, 0x8B, 0xCB);                                               // push 1 ; mov ecx, ebx
            a.CallAbs(Offsets.Fn_MsgAppendBool);
            a.Db(0xFF, 0x76, (byte)Offsets.Object_Id, 0x8B, 0xCB);                      // push [esi+id] ; mov ecx, ebx
            a.CallAbs(Offsets.Fn_MsgAppendObjectId);
            a.Db(0x8B, 0x0D).D32(Offsets.TheInGameUI);                                  // mov ecx, [TheInGameUI]
            a.Db(0x57, 0x8B, 0x01, 0xFF, 0x90).D32(Offsets.UI_SelectDrawableSlot);      // push edi ; mov eax, [ecx] ; call [eax+0x108]   show it as selected
            a.Db(0xB8).D32(1).Jmp("done");
            a.Label("fail");
            a.Db(0x33, 0xC0);                                                           // xor eax, eax
            a.Label("done");
            a.Db(0x5F, 0x5E, 0x5B, 0x5D, 0xC3);                                         // pop edi ; pop esi ; pop ebx ; pop ebp ; ret
            return a.Build(cave + OffHeroRoutine);
        }

        bool EnsureHeroRoutine()
        {
            if (_hook == null) return false; if (_heroRoutineFor == _hook) return true;
            if (!_hook.Installed && !_hook.Install()) return false;
            byte[] code = BuildHeroSelectRoutine(_hook.Cave); if (code.Length > MaxHeroRoutine) throw new InvalidOperationException("hero routine too large");
            if (!_m.WriteBytes(_hook.Cave + OffHeroRoutine, code)) return false;
            _heroRoutineFor = _hook; return true;
        }

        /// <summary>The local player's heroes as they stand right now (read only).</summary>
        HeroInfo[] ReadHeroes(GameSnapshot g)
        {
            var list = new List<HeroInfo>(); List<uint> selected = SelectedObjects();
            foreach (uint o in LocalHeroes(g))
            {
                uint id = _m.U32(o + Offsets.Object_Id); if (id == 0) continue;
                var h = new HeroInfo { Id = id, Template = TemplateNameOf(o), Selected = selected.Contains(o) };
                h.Name = PrettyName(h.Template);
                h.X = _m.F32(o + Offsets.Object_PosX); h.Y = _m.F32(o + Offsets.Object_PosY); h.Z = _m.F32(o + Offsets.Object_PosZ);
                uint tracker = _m.U32(o + Offsets.Object_ExperienceTracker); int level = tracker > 0x10000 ? _m.I32(tracker + Offsets.Tracker_Level) : 0;
                h.Level = level >= 0 && level <= 99 ? level : 0;
                uint body = _m.U32(o + Offsets.Object_Body);
                if (body > 0x10000) { float hp = _m.F32(body + 0x08), max = _m.F32(body + 0x10); if (Sane(hp, 0f, 1e7f) && Sane(max, 1f, 1e7f)) { h.Health = Math.Min(hp, max); h.MaxHealth = max; } }
                if (!Sane(h.X, -500f, 40000f) || !Sane(h.Y, -500f, 40000f)) continue;       // being created or removed this instant
                list.Add(h);
            }
            return list.ToArray();
        }

        bool MoveCameraTo(float x, float y, float z)
        {
            uint view = _m.U32(Offsets.TheTacticalView), res;
            if (view == 0 || _m.U32(view) != Offsets.View_Vtable || _m.U32(Offsets.View_Vtable + Offsets.View_MoveCameraToSlot) != Offsets.Fn_ViewMoveCameraTo) return false;
            if (!_hook.EnsureCave()) return false; uint pos = _hook.Cave + MainThreadHook.OffScratch;
            var b = new byte[12]; BitConverter.GetBytes(x).CopyTo(b, 0); BitConverter.GetBytes(y).CopyTo(b, 4); BitConverter.GetBytes(z).CopyTo(b, 8);
            if (!_m.WriteBytes(pos, b)) return false;
            return _hook.Call(Offsets.Fn_ViewMoveCameraTo, view, new[] { pos, 0u, 500u, 1u, Bits(0f), Bits(0f) }, 600, out res);
        }

        void FocusHero(GameSnapshot g, uint id)
        {
            HeroInfo h = Array.Find(_heroes, x => x.Id == id);
            if (h == null) { _heroListText = "That hero is no longer on the map."; Event("My heroes: that hero is no longer on the map.", true); return; }
            if (!EnsureHookFor("My heroes")) { _heroListText = "Could not reach the game."; return; }
            bool selected = false, reached = true;
            if (HeroSelectUsable() && EnsureHeroRoutine())
            {
                uint res; reached = _hook.CallCave(OffHeroRoutine, new[] { id, g.Player }, 900, out res);
                selected = reached && res == 1;
            }
            if (!reached) { _heroListText = "The game is paused - resume the match and click again."; Event("My heroes: the game is paused - resume the match and click the hero again.", true); return; }
            bool moved = MoveCameraTo(h.X, h.Y, h.Z); string shown = _heroFocusName.Length > 0 ? _heroFocusName : h.Name;
            _heroListText = shown + (selected ? " is selected" : " could not be selected") + (moved ? ", camera moved there." : ".");
            Event("My heroes: " + _heroListText, !selected && !moved);
            Log("MY HEROES " + h.Template + " (id " + id + ")" + (selected ? " selected" : " NOT selected") + (moved ? ", camera moved" : ""));
            _nextHeroScan = DateTime.MinValue;                                              // show the new selection at once
        }

        /// <summary>Called every tick while a match runs.</summary>
        void ApplyHeroList(GameSnapshot g)
        {
            uint focus = _heroFocus; if (focus != 0) _heroFocus = 0;
            if (!_wantHeroList && focus == 0) { if (_heroes.Length != 0) _heroes = new HeroInfo[0]; return; }
            if (focus != 0 && _heroes.Length == 0) _heroes = ReadHeroes(g);
            if (focus != 0) FocusHero(g, focus);
            if (DateTime.UtcNow >= _nextHeroScan) { _heroes = ReadHeroes(g); _nextHeroScan = DateTime.UtcNow.AddMilliseconds(800); }
        }

        void HeroListMatchEnded() { _heroes = new HeroInfo[0]; _heroListText = ""; _heroFocus = 0; _nextHeroScan = DateTime.MinValue; }

        /// <summary>
        /// Build check (used by dev\build-preview.ps1, never in a game): assembles every routine that goes into the trainer's
        /// page inside the game, for both games, and compares its size with the room reserved for it. Twice a routine had
        /// outgrown its room and was only stopped by the run-time check, in the middle of a test.
        /// </summary>
        public string RoutineSizeReport()
        {
            var sb = new System.Text.StringBuilder(); const uint cave = 0x10000000; GameProfile before = Offsets.Current;
            foreach (GameProfile p in new[] { Offsets.Rotwk201, Offsets.Bfme2_106 })
            {
                Offsets.Use(p);
                int a = BuildSpawnRoutine(cave).Length, b = BuildBookRoutine(cave).Length, c = BuildHeroSelectRoutine(cave).Length;
                bool ok = a <= MaxSpawnRoutine && b <= MaxBookRoutine && c <= MaxHeroRoutine && OffHeroRoutine + MaxHeroRoutine <= 0x1000;
                sb.AppendLine((ok ? "ok   " : "FAIL ") + "in-game routines " + p.Id + ": create " + a + "/" + MaxSpawnRoutine + ", spell book " + b + "/" + MaxBookRoutine + ", select hero " + c + "/" + MaxHeroRoutine + " bytes");
            }
            if (before != null) Offsets.Use(before);
            return sb.ToString();
        }

        /// <summary>Build check: the bytes of the "select hero" routine for both games as hex text (for an offline disassembly).</summary>
        public string HeroRoutineHex()
        {
            var sb = new System.Text.StringBuilder(); GameProfile before = Offsets.Current;
            foreach (GameProfile p in new[] { Offsets.Rotwk201, Offsets.Bfme2_106 })
            { Offsets.Use(p); sb.Append(p.Id).Append(' '); foreach (byte x in BuildHeroSelectRoutine(0x10000000)) sb.Append(x.ToString("X2")); sb.AppendLine(); }
            if (before != null) Offsets.Use(before);
            return sb.ToString();
        }
    }
}
