using System;
using System.Collections.Generic;

namespace WitchKingTrainer.Core
{
    public enum WeatherMode { Default = 0, Clear = 1, Rain = 2, Snow = 3, Fog = 4, Storm = 5 }

    /// <summary>God Mode (no damage for the local player's own units / heroes / buildings) and visual weather control.</summary>
    public sealed partial class CheatEngine
    {
        // ------------------------------------------------------------------ god mode
        // All damage in the engine goes through one routine (ActiveBody::attemptDamage). It starts with the engine's own
        // "is this object indestructible?" test; the trainer extends that one test: while a God Mode switch is on, damage to
        // an object of the matching category that belongs to the local player is dropped exactly as if the engine had found
        // it indestructible. Healing, selling/demolishing your own buildings, timed summons expiring and other engine
        // housekeeping still work, because "kill" requests that come from the engine itself are let through.
        const int FlagGod = 1;   // one dword: byte 0 = units, byte 1 = heroes, byte 2 = buildings
        MainThreadHook.Detour _detGod, _detKillGuard;

        /// <summary>Emits "classify the object whose ThingTemplate is in [reg]" -> al = the matching God Mode switch (eax holds the flag dword on entry).</summary>
        static void EmitClassify(MainThreadHook.Asm a, byte modrmBase, string notProtected)
        {
            uint k = Offsets.Template_KindOf;
            Action<uint, byte> testByte = (off, mask) => a.Db(0xF6, modrmBase).D32(k + off).Db(mask);   // test byte [reg+kindOf+off], mask
            testByte(6, 0x80); a.Jne(notProtected);                         // MINE (bit 55): never protected
            testByte(0, 0x80); a.Je("nstruct"); a.Db(0xC1, 0xE8, 0x10).Jmp("have");      // STRUCTURE (bit 7)  -> shr eax,16
            a.Label("nstruct"); testByte(11, 0x04); a.Je("nhero"); a.Db(0xC1, 0xE8, 0x08).Jmp("have");   // HERO (bit 90) -> shr eax,8
            a.Label("nhero"); testByte(1, 0x4F); a.Jne("have");             // INFANTRY | CAVALRY | MONSTER | MACHINE | DOZER (bits 8-11, 14)
            testByte(23, 0x80); a.Je(notProtected);                         // SHIP (bit 191)
            a.Label("have").Db(0x84, 0xC0).Je(notProtected);                // test al,al : this category's switch is off
        }

        static MainThreadHook.Detour NewGodDetour()
        {
            return new MainThreadHook.Detour
            {
                Name = "god mode", Site = Offsets.GodSite, Original = Offsets.GodSiteOriginal, StubOffset = 0x380, RedirectCall = false,
                Build = (cave, at) =>
                {
                    // at the site: esi = body, edi = victim Object*, ebx = DamageInfo*; eax, ecx, edx and the flags are free
                    var a = new MainThreadHook.Asm();
                    a.Db(0xA1).D32(cave + MainThreadHook.OffFlags + 4 * FlagGod).Db(0x85, 0xC0).Je("orig");        // mov eax,[GOD] ; test eax,eax
                    a.Db(0x83, 0x7B, 0x10, 0x07).Je("orig");                                                      // cmp dword [ebx+0x10],7  (healing)
                    a.Db(0x8B, 0x8F).D32(Offsets.Object_Team).Db(0x85, 0xC9).Je("orig");                           // mov ecx,[edi+team]
                    a.Db(0x8B, 0x49, (byte)Offsets.Team_Owner1, 0x85, 0xC9).Je("orig");                            // mov ecx,[ecx+0x30]
                    a.Db(0x8B, 0x49, (byte)Offsets.Team_Owner2);                                                  // mov ecx,[ecx+8]      (owner Player*)
                    a.Db(0x8B, 0x15).D32(Offsets.ThePlayerList).Db(0x85, 0xD2).Je("orig");                         // mov edx,[ThePlayerList]
                    a.Db(0x8B, 0x52, (byte)Offsets.PlayerList_Local, 0x3B, 0xCA).Jne("orig");                      // mov edx,[edx+0x10] ; cmp ecx,edx
                    a.Db(0x8B, 0x4F, (byte)Offsets.Object_Template);                                              // mov ecx,[edi+4]      (ThingTemplate*)
                    EmitClassify(a, 0x81, "orig");
                    a.Db(0x80, 0x7B, 0x24, 0x00).Je("block");                                                     // ordinary damage -> drop
                    a.Db(0x8B, 0x43, 0x08, 0x85, 0xC0).Je("orig");                                                // kill request with no source = the engine itself
                    a.Db(0x3B, 0x47, (byte)Offsets.Object_Id).Je("orig");                                         // self-kill
                    a.Db(0x8B, 0x4A, (byte)Offsets.Player_Index, 0xB8).D32(1).Db(0xD3, 0xE0, 0x85, 0x43, 0x0C).Jne("orig");   // forced kill by an own weapon
                    a.Label("block").JmpAbs(Offsets.GodEpilogue);
                    a.Label("orig").Db(Offsets.GodSiteOriginal).JmpAbs(Offsets.GodSite + (uint)Offsets.GodSiteOriginal.Length);
                    return a.Build(at);
                }
            };
        }

        static MainThreadHook.Detour NewKillGuardDetour()
        {
            // "kill this object outright" used by knock-back weapons against builders and siege engines: Object::kill(type, death)
            var orig = new byte[5]; orig[0] = 0xE8; BitConverter.GetBytes(unchecked(Offsets.Fn_ObjectKill - (Offsets.KillGuardSite + 5))).CopyTo(orig, 1);
            return new MainThreadHook.Detour
            {
                Name = "god mode (impact kills)", Site = Offsets.KillGuardSite, Original = orig, StubOffset = 0x500, RedirectCall = true,
                Build = (cave, at) =>
                {
                    // ecx = victim Object*, two stack arguments, callee pops 8; eax/edx free, ecx must survive on the pass path
                    var a = new MainThreadHook.Asm();
                    a.Db(0xA1).D32(cave + MainThreadHook.OffFlags + 4 * FlagGod).Db(0x85, 0xC0).Je("pass");
                    a.Db(0x8B, 0x91).D32(Offsets.Object_Team).Db(0x85, 0xD2).Je("pass");                           // mov edx,[ecx+team]
                    a.Db(0x8B, 0x52, (byte)Offsets.Team_Owner1, 0x85, 0xD2).Je("pass");
                    a.Db(0x8B, 0x52, (byte)Offsets.Team_Owner2);
                    a.Db(0x56, 0x8B, 0x35).D32(Offsets.ThePlayerList).Db(0x85, 0xF6).Je("passp");                  // push esi ; mov esi,[ThePlayerList]
                    a.Db(0x3B, 0x56, (byte)Offsets.PlayerList_Local).Jne("passp");                                // cmp edx,[esi+0x10]
                    a.Db(0x5E, 0x8B, 0x51, (byte)Offsets.Object_Template);                                        // pop esi ; mov edx,[ecx+4]
                    EmitClassify(a, 0x82, "pass");
                    a.Db(0xC2, 0x08, 0x00);                                                                       // protected: swallow the kill
                    a.Label("passp").Db(0x5E);
                    a.Label("pass").JmpAbs(Offsets.Fn_ObjectKill);
                    return a.Build(at);
                }
            };
        }

        void ApplyGodMode(GameSnapshot g)
        {
            bool u = _on[(int)CheatId.GodUnits], h = _on[(int)CheatId.GodHeroes], b = _on[(int)CheatId.GodBuildings];
            uint want = (u ? 1u : 0u) | (h ? 0x100u : 0u) | (b ? 0x10000u : 0u);
            if (want == 0)
            {
                if (_detGod != null && _detGod.Installed) _m.WriteU32(_hook.FlagAddress(FlagGod), 0);
                Set(CheatId.GodUnits, CheatState.Off, ""); Set(CheatId.GodHeroes, CheatState.Off, ""); Set(CheatId.GodBuildings, CheatState.Off, "");
                return;
            }
            string err = null;
            if (!_hook.EnsureDetour(_detGod) || !_hook.EnsureDetour(_detKillGuard) || !_m.WriteU32(_hook.FlagAddress(FlagGod), want)) err = _hook.LastError ?? "Could not write to the game";
            Set(CheatId.GodUnits, !u ? CheatState.Off : err != null ? CheatState.Error : CheatState.Active, !u ? "" : err ?? "Your units take no damage");
            Set(CheatId.GodHeroes, !h ? CheatState.Off : err != null ? CheatState.Error : CheatState.Active, !h ? "" : err ?? "Your heroes take no damage");
            Set(CheatId.GodBuildings, !b ? CheatState.Off : err != null ? CheatState.Error : CheatState.Active, !b ? "" : err ?? "Your buildings take no damage");
        }

        // ------------------------------------------------------------------ weather (visual only)
        // Rain and dark clouds are the engine's own weather types (the same ones the Freezing Rain / Darkness powers switch
        // on), set through the engine's own setWeather routine. Snow and fog are not weather types in this engine: snow is
        // the per-map snow system, fog the per-map "hardware fog" setting; both are switched on by writing the same values a
        // map's own Weather block would set, and everything is put back by "Default".
        volatile int _weatherWanted;
        public WeatherMode Weather { get { return (WeatherMode)_weatherWanted; } set { _weatherWanted = (int)value; _wake.Set(); } }
        WeatherMode _weatherApplied = WeatherMode.Default; string _weatherText = ""; DateTime _nextWeatherTry = DateTime.MinValue;
        bool _fogSaved, _snowTouched, _lightningSet; uint _fogWs; readonly byte[] _fogOrig = new byte[24];   // colour(3f) enable(4 bytes) start(f) end(f)

        uint EffectiveWeatherSetting()
        {
            uint p = _m.U32(Offsets.TheWeatherSetting); int guard = 0;
            while (p > 0x10000 && guard++ < 16) { uint next = _m.U32(p + 4); if (next < 0x10000) break; p = next; }
            return p > 0x10000 ? p : 0;
        }
        bool InWeatherChain(uint ws)
        {
            uint p = _m.U32(Offsets.TheWeatherSetting); int guard = 0;
            while (p > 0x10000 && guard++ < 16) { if (p == ws) return true; p = _m.U32(p + 4); }
            return false;
        }

        bool SetEngineWeather(int type)
        {
            uint w = _m.U32(Offsets.TheGlobalWeatherSystem), fire = _m.U32(Offsets.TheFireLogicSystem), res; if (w < 0x10000) return false;
            if (_m.I32(w + 0x10) == type) return true;
            uint burn = fire > 0x10000 ? _m.U32(fire + 0x98) : 0;          // passed back unchanged, so the only non-visual value the routine writes stays as it is
            return _hook.Call(Offsets.Fn_SetWeather, w, new[] { (uint)type, burn, 0u }, 450, out res);
        }

        void SaveFog(uint ws)
        {
            if (_fogSaved || ws == 0) return; byte[] b = _m.ReadBytes(ws + 0x8C, 24); if (b == null) return;
            Array.Copy(b, _fogOrig, 24); _fogWs = ws; _fogSaved = true; PersistWeather();
        }
        void RestoreFog()
        {
            if (!_fogSaved) return;
            if (InWeatherChain(_fogWs)) _m.WriteBytes(_fogWs + 0x8C, _fogOrig);   // an override object of a finished map is freed memory: never written
            _fogSaved = false; PersistWeather();
        }
        void SetLightning(bool on)
        {
            uint w = _m.U32(Offsets.TheGlobalWeatherSystem); if (w < 0x10000) return;
            if (on == _lightningSet) return;
            _m.WriteBytes(w + 0x3C, new[] { on ? (byte)1 : (byte)0 }); _lightningSet = on; PersistWeather();
        }
        bool RestoreSnow()
        {
            if (!_snowTouched && !_boxRaised) return true; uint s = _m.U32(Offsets.TheSnowManager), res;
            if (s < 0x10000) { _snowTouched = _boxRaised = _snowRestorePending = false; return true; }
            ReleaseSnowTexture();
            if (!_hook.Call(Offsets.Fn_SnowUpdateIni, s, new uint[0], 450, out res)) return false;   // re-reads the map's own snow settings (box height and start heights included)
            _snowTouched = _boxRaised = _snowRestorePending = false; return true;
        }

        // The engine keeps rain drops and snow flakes between height 0 and "SnowBoxHeight", counted from the bottom of the world and
        // not from the ground (BFME II: 300, Patch 2.02: 800). Where the ground itself lies higher - Isengard in BFME II is at 370 -
        // all of it falls inside the terrain and nothing is seen. While rain, snow or a storm is selected the box is therefore kept
        // tall enough to reach the camera, the way a map's own "SnowBoxHeight" line would do it, and the drops are spaced closer by
        // the same measure, so the air between ground and camera holds as many of them as the map's own settings intend.
        bool _boxRaised, _snowRestorePending; float _boxOrig, _rampX0, _rampY0; readonly Random _rnd = new Random();
        const float SnowLook = 0.075f; float _snowCam;            // tuned in play: spacing 35 for a camera 560 up in a box 800 high, 12 for 300 up in a box 1000 high
        static bool Sane(float v, float lo, float hi) { return v >= lo && v <= hi; }   // false for NaN as well
        static bool WantsSnowBox(WeatherMode m) { return m == WeatherMode.Rain || m == WeatherMode.Snow || m == WeatherMode.Storm; }

        /// <summary>Returns true when the box was made taller just now (the snow preset then needs writing again).</summary>
        bool EnsureSnowBox(WeatherMode mode)
        {
            uint s = _m.U32(Offsets.TheSnowManager), v = _m.U32(Offsets.TheTacticalView); if (s < 0x10000 || v < 0x10000) return false;
            float box = _m.F32(s + 0x38), ground = _m.F32(v + Offsets.View_GroundLevel), cam = _m.F32(v + Offsets.View_CurrentHeight); bool grew = false;
            if (!Sane(box, 50f, 20000f) || !Sane(ground, -2000f, 8000f) || !Sane(cam, 30f, 8000f)) return false;
            float need = Math.Max(ground, 0f) + cam + 100f;                        // ground under the camera + camera height + a margin
            if (need > box)
            {
                uint table = _m.U32(s + 0x0C); if (table < 0x10000) return false;
                float nb = Math.Min(8000f, (float)Math.Ceiling((need + 150f) / 100f) * 100f);   // some headroom, so scrolling and zooming rarely change it again
                if (nb > box)
                {
                    if (!_boxRaised) { _boxOrig = box; _rampX0 = _m.F32(s + 0x64); _rampY0 = _m.F32(s + 0x68); }
                    // the engine's own form: 64 x 64 start heights, whole numbers below the box height
                    var b = new byte[4096 * 4]; int n = (int)nb;
                    for (int i = 0; i < 4096; i++) BitConverter.GetBytes((float)_rnd.Next(n)).CopyTo(b, i * 4);
                    if (!_m.WriteBytes(table, b) || !_m.WriteF32(s + 0x38, nb)) return false;
                    Log(string.Format("weather: snow/rain box {0:0} -> {1:0} (ground {2:0}, camera {3:0})", box, nb, ground, cam));
                    _boxRaised = true; box = nb; grew = true;
                }
            }
            if (!_boxRaised || mode == WeatherMode.Snow) return grew;                // snow: spacing and period come from WriteSnowPreset
            // rain: the engine blends its drop spacing between these two values while the rain builds up and holds
            float k = (float)Math.Sqrt(_boxOrig / box);
            if (Sane(_rampX0, 2f, 500f) && Sane(_rampY0, 2f, 500f))
            {
                float rx = Math.Max(6f, _rampX0 * k), ry = Math.Max(6f, _rampY0 * k);
                if (Math.Abs(_m.F32(s + 0x64) - rx) > 0.01f) _m.WriteF32(s + 0x64, rx);
                if (Math.Abs(_m.F32(s + 0x68) - ry) > 0.01f) _m.WriteF32(s + 0x68, ry);
            }
            float top = _m.F32(s + 0x70);                                            // fall speed of full rain
            if (Sane(top, 10f, 5000f) && Math.Abs(_m.F32(s + 0x18) - box / top) > 0.001f) _m.WriteF32(s + 0x18, box / top);
            return grew;
        }

        /// <summary>After rain: puts the engine's own snow settings back once the rain has faded out (doing it earlier would cut the rain off).</summary>
        void TryPendingSnowRestore()
        {
            uint s = _m.U32(Offsets.TheSnowManager); if (s < 0x10000 || !_boxRaised) { _snowRestorePending = false; return; }
            if (_m.I32(s + 0xAC) != 0 || DateTime.UtcNow < _nextWeatherTry) return;
            if (!RestoreSnow()) _nextWeatherTry = DateTime.UtcNow.AddMilliseconds(700);
        }

        void WriteSnowPreset()
        {
            uint s = _m.U32(Offsets.TheSnowManager); if (s < 0x10000) return;
            float box = _m.F32(s + 0x38); if (!Sane(box, 50f, 20000f)) box = 800f;
            // The number of flakes in view grows with the cube of the camera height (more ground in view, a taller column of air)
            // and falls with the box height, so the spacing is set from both: the same gentle snowfall on every map and at every zoom.
            float cam = CameraHeight(); _snowCam = cam;
            float spacing = Math.Min(60f, Math.Max(9f, SnowLook * (float)Math.Pow(cam, 1.5) / (float)Math.Sqrt(box)));
            Action<uint, float> f = (off, v) => _m.WriteF32(s + off, v);
            f(0x14, 100f); f(0x18, box / 100f); f(0x3C, spacing); f(0x24, 25f); f(0x1C, 0.012f); f(0x20, 0.006f);
            f(0x28, 3f); f(0x30, 3f); f(0x2C, 9f); f(0x34, 5f); f(0x48, 45f); f(0x4C, 25f);
            _m.WriteBytes(s + 0x41, new byte[] { 1 }); _snowTouched = true;
        }

        // the map's precipitation sprite is a rain drop on non-snow maps, so Snow borrows the game's own snowflake texture
        const uint OffTexName = 0x600, OffTexSlot = 0x620;
        bool ApplySnowTexture()
        {
            uint s = _m.U32(Offsets.TheSnowManager), cave = _hook.Cave, res; if (s < 0x10000 || cave == 0) return false;
            uint slot = _m.U32(cave + OffTexSlot);
            if (slot < 0x10000)
            {
                var name = new byte[0x20]; System.Text.Encoding.ASCII.GetBytes("EXSnowFlake2.tga").CopyTo(name, 0);
                _m.WriteBytes(cave + OffTexName, name); _m.WriteU32(cave + OffTexSlot, 0);
                if (!_hook.Call(Offsets.Fn_GetTexture, 0, new[] { cave + OffTexSlot, cave + OffTexName, 0u, 0u }, 450, out res)) return false;
                slot = _m.U32(cave + OffTexSlot); if (slot < 0x10000) return false;
            }
            if (_m.U32(s + 0x78) != slot && !_hook.Call(Offsets.Fn_TexRefAssign, s + 0x78, new[] { cave + OffTexSlot }, 450, out res)) return false;
            return true;
        }
        void ReleaseSnowTexture()
        {
            uint cave = _hook != null ? _hook.Cave : 0, res; if (cave == 0) return; uint slot = _m.U32(cave + OffTexSlot);
            if (slot > 0x10000 && _hook.Call(Offsets.Fn_TexRelease, slot, new uint[0], 450, out res)) _m.WriteU32(cave + OffTexSlot, 0);
        }

        // Storm = the engine's dark-cloud weather + its lightning + the rain effect started by hand (the engine has no storm type)
        bool _stormRain;
        void StartStormRain()
        {
            uint s = _m.U32(Offsets.TheSnowManager), res; if (s < 0x10000) return;
            _m.WriteI32(s + 0xA8, 0);
            if (_hook.Call(Offsets.Fn_SnowStartSpell, s, new uint[0], 450, out res)) _stormRain = true;
        }
        void StopStormRain()
        {
            if (!_stormRain) return; uint s = _m.U32(Offsets.TheSnowManager);
            if (s > 0x10000) _m.WriteI32(s + 0xA8, 2);      // the engine then ramps the rain out by itself
            _stormRain = false;
        }

        float _fogCam;
        float CameraHeight() { uint v = _m.U32(Offsets.TheTacticalView); float h = v > 0x10000 ? _m.F32(v + Offsets.View_CurrentHeight) : 0; return h < 150f || h > 6000f ? 300f : h; }
        void WriteFog(uint ws)
        {
            float cam = CameraHeight(); _fogCam = cam;
            var b = new byte[24];
            BitConverter.GetBytes(0.74f).CopyTo(b, 0); BitConverter.GetBytes(0.77f).CopyTo(b, 4); BitConverter.GetBytes(0.82f).CopyTo(b, 8);   // pale grey-blue
            b[12] = 1;                                                                                                                        // HardwareFogEnable
            BitConverter.GetBytes(Math.Max(40f, cam * 0.15f)).CopyTo(b, 16); BitConverter.GetBytes(Math.Max(700f, cam * 3.2f)).CopyTo(b, 20);  // start / end distance follow the zoom
            Array.Copy(_fogOrig, 13, b, 13, 3);
            _m.WriteBytes(ws + 0x8C, b);
        }

        public static string WeatherName(WeatherMode m)
        {
            switch (m) { case WeatherMode.Clear: return "Clear"; case WeatherMode.Rain: return "Rain"; case WeatherMode.Snow: return "Snow"; case WeatherMode.Fog: return "Fog"; case WeatherMode.Storm: return "Storm"; default: return "Map default"; }
        }

        void ApplyWeather(GameSnapshot g)
        {
            var want = (WeatherMode)_weatherWanted; uint w = _m.U32(Offsets.TheGlobalWeatherSystem);
            if (w < 0x10000) { _weatherText = want == WeatherMode.Default ? "" : "Weather system not found"; return; }
            if (want == _weatherApplied)
            {
                // the box follows the camera (higher ground, zooming out); after rain the engine's own settings come back once it has faded
                if (WantsSnowBox(want)) { _snowRestorePending = false; if (EnsureSnowBox(want) && want == WeatherMode.Snow) WriteSnowPreset(); }
                else if (_snowRestorePending) TryPendingSnowRestore();
                // keep it up: a real weather power (Freezing Rain, Darkness...) replaces the choice for its duration, then the engine goes back to "none"
                if (want == WeatherMode.Rain || want == WeatherMode.Storm)
                {
                    int type = _m.I32(w + 0x10), wantedType = want == WeatherMode.Rain ? 2 : 1;
                    if (type != wantedType && _m.I32(w + 0x28) <= 0 && DateTime.UtcNow >= _nextWeatherTry) { _nextWeatherTry = DateTime.UtcNow.AddSeconds(1); if (EnsureHook(CheatId.Zoom)) SetEngineWeather(wantedType); }
                    uint s = _m.U32(Offsets.TheSnowManager);
                    if (want == WeatherMode.Rain && s > 0x10000 && type == 2 && _m.I32(s + 0xAC) == 0 && _m.I32(s + 0xA8) == 2) _m.WriteI32(s + 0xA8, 0);   // engine quirk: lets the rain restart
                }
                else if (want == WeatherMode.Snow)
                {
                    uint s = _m.U32(Offsets.TheSnowManager); byte[] st = s > 0x10000 ? _m.ReadBytes(s + 0x41, 1) : null;
                    if (st != null && st[0] == 0 && _m.I32(s + 0xAC) == 0) WriteSnowPreset();
                    else if (_snowCam > 0 && Math.Abs(CameraHeight() - _snowCam) > _snowCam * 0.3f) WriteSnowPreset();   // zoomed a good way in or out
                }
                else if (want == WeatherMode.Fog && _fogSaved && InWeatherChain(_fogWs))
                {
                    byte[] en = _m.ReadBytes(_fogWs + 0x98, 1); float cam = CameraHeight();
                    if ((en != null && en[0] == 0) || Math.Abs(cam - _fogCam) > _fogCam * 0.08f) WriteFog(_fogWs);
                }
                if (want == WeatherMode.Snow && _hook.Installed && DateTime.UtcNow >= _nextWeatherTry)
                {
                    uint s2 = _m.U32(Offsets.TheSnowManager), slot = _hook.Cave != 0 ? _m.U32(_hook.Cave + OffTexSlot) : 0;
                    if (s2 > 0x10000 && slot > 0x10000 && _m.U32(s2 + 0x78) != slot) { _nextWeatherTry = DateTime.UtcNow.AddSeconds(1); ApplySnowTexture(); }
                }
                if (want == WeatherMode.Storm && _stormRain && _hook.Installed && DateTime.UtcNow >= _nextWeatherTry)
                {
                    uint s3 = _m.U32(Offsets.TheSnowManager);
                    if (s3 > 0x10000 && _m.I32(s3 + 0xAC) == 0 && _m.I32(w + 0x10) == 1) { _nextWeatherTry = DateTime.UtcNow.AddSeconds(2); StartStormRain(); }
                }
                return;
            }
            if (DateTime.UtcNow < _nextWeatherTry) return;
            if (!_hook.Installed && !_hook.Install()) { _weatherText = "Weather: " + (_hook.LastError ?? "could not reach the game"); _nextWeatherTry = DateTime.UtcNow.AddSeconds(3); return; }

            // 1) leave the previous mode: undo what the new mode does not use
            bool ok = true;
            if (_weatherApplied == WeatherMode.Storm || _stormRain || _lightningSet)
            {
                // leave the storm cleanly: lightning off and weather "none" first, give the renderer a moment, then go on
                StopStormRain(); SetLightning(false);
                if (!SetEngineWeather(0)) { _nextWeatherTry = DateTime.UtcNow.AddMilliseconds(700); _weatherText = "Weather changes when the match resumes"; return; }
                _weatherApplied = WeatherMode.Default; System.Threading.Thread.Sleep(250);
            }
            if (want != WeatherMode.Storm) SetLightning(false);
            if (want != WeatherMode.Fog && want != WeatherMode.Clear) RestoreFog();
            // a snow look (or "clear") is undone before anything else starts; the taller box alone is left to step 2
            if (_snowTouched && want != WeatherMode.Snow && want != WeatherMode.Clear) ok &= RestoreSnow();
            // 2) the engine weather type
            int engineType = want == WeatherMode.Rain ? 2 : want == WeatherMode.Storm ? 1 : 0;
            ok = ok && SetEngineWeather(engineType);
            if (!ok) { _nextWeatherTry = DateTime.UtcNow.AddMilliseconds(700); _weatherText = "Weather changes when the match resumes"; return; }
            _snowRestorePending = _boxRaised && !WantsSnowBox(want) && want != WeatherMode.Clear;   // after rain: restored once the rain has faded out
            // 3) the extras of the new mode
            uint ws = EffectiveWeatherSetting(), sm = _m.U32(Offsets.TheSnowManager);
            switch (want)
            {
                case WeatherMode.Rain: EnsureSnowBox(want); break;
                case WeatherMode.Storm: SetLightning(true); StartStormRain(); EnsureSnowBox(want); break;
                case WeatherMode.Snow: RestoreFog(); EnsureSnowBox(want); WriteSnowPreset(); ApplySnowTexture(); break;
                case WeatherMode.Fog: RestoreSnow(); if (ws != 0) { SaveFog(ws); if (_fogSaved) WriteFog(_fogWs); } break;
                case WeatherMode.Clear:
                    if (ws != 0) { SaveFog(ws); if (_fogSaved) _m.WriteBytes(_fogWs + 0x98, new byte[] { 0 }); }      // no haze
                    if (sm > 0x10000) { _m.WriteBytes(sm + 0x41, new byte[] { 0 }); _snowTouched = true; }            // no snowfall
                    break;
            }
            _weatherApplied = want;
            bool lowDetail = want == WeatherMode.Fog && Offsets.ShaderLod != 0 && _m.I32(Offsets.ShaderLod) < 1;
            _weatherText = want == WeatherMode.Default ? "" : "Weather: " + WeatherName(want) + (lowDetail ? " (fog needs a higher shader detail setting in the game options)" : "");
            Event(want == WeatherMode.Default ? "Weather restored to the map's default." : "Weather set to " + WeatherName(want) + ".", false);
        }

        /// <summary>Things the engine does not reset by itself when a match ends (plain memory writes, no engine call needed).</summary>
        void WeatherMatchEnded()
        {
            if (_fogSaved) RestoreFog();
            if (_lightningSet) SetLightning(false);
            _snowTouched = _boxRaised = _snowRestorePending = false; _stormRain = false; _weatherApplied = WeatherMode.Default; _weatherText = "";
        }

        /// <summary>On trainer exit: back to the map's default. Returns false if the engine calls could not run (game paused).</summary>
        bool WeatherShutdown()
        {
            bool ok = true;
            if (_weatherApplied == WeatherMode.Default && !_fogSaved && !_snowTouched && !_boxRaised && !_lightningSet) return true;
            StopStormRain(); SetLightning(false); RestoreFog();
            if (_hook != null && _hook.Installed) { ok &= SetEngineWeather(0); ok &= RestoreSnow(); } else ok = false;   // weather type first: re-reading the snow settings ends a running rain cleanly only then
            _weatherApplied = WeatherMode.Default; return ok;
        }

        // the fog originals and the lightning flag survive a trainer crash in the game's cave, so the next session can undo them
        const uint OffWeatherPersist = 0xE0;   // [ws ptr][24 bytes fog originals] ... lightning flag in the top byte of the pointer slot's neighbour
        void PersistWeather()
        {
            if (_hook == null || _hook.Cave == 0) return; var b = new byte[32];
            BitConverter.GetBytes(_fogSaved ? _fogWs : 0u).CopyTo(b, 0); Array.Copy(_fogOrig, 0, b, 4, 24); b[28] = _lightningSet ? (byte)1 : (byte)0;
            _m.WriteBytes(_hook.Cave + OffWeatherPersist, b);
        }
        void LoadWeatherPersist()
        {
            if (_hook == null || _hook.Cave == 0) return; byte[] b = _m.ReadBytes(_hook.Cave + OffWeatherPersist, 32); if (b == null) return;
            uint ws = BitConverter.ToUInt32(b, 0);
            if (ws > 0x10000) { _fogWs = ws; Array.Copy(b, 4, _fogOrig, 0, 24); _fogSaved = true; RestoreFog(); }
            if (b[28] == 1) { _lightningSet = true; SetLightning(false); }
        }
    }
}
