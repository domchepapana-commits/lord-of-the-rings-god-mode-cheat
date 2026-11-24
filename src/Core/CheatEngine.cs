using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace WitchKingTrainer.Core
{
    public enum Connection { NotRunning, NeedsElevation, Unsupported, Starting, InMenus, Multiplayer, InMatch }
    public enum CheatId { Money = 0, InstantArmy = 1, InstantBuild = 2, InstantUpgrades = 3, CommandPoints = 4, FreezeAI = 5, PowerPoints = 6, NoCooldowns = 7, RevealMap = 8, Zoom = 9, TrackGollum = 10, GodUnits = 11, GodHeroes = 12, GodBuildings = 13 }
    public enum CheatState { Off, Armed, Active, Pending, Error }
    public enum GollumState { Unknown, OnMap, RingDropped, NotOnMap, NoMatch }

    public sealed class CheatStatus
    {
        public CheatState State; public string Text = "";
        public CheatStatus Clone() { return new CheatStatus { State = State, Text = Text }; }
    }

    public sealed class GollumInfo
    {
        public GollumState State = GollumState.Unknown;
        public string Message = "Press FIND to locate Gollum.";
        public float X, Y, Z, MapWidth, MapHeight;
        public DateTime When = DateTime.MinValue;
    }

    /// <summary>Immutable picture of the engine state handed to the UI after every tick.</summary>
    public sealed class EngineSnapshot
    {
        public Connection Connection = Connection.NotRunning;
        public string ConnectionText = "Game not running";
        public string Detail = "Start the game and begin a skirmish.";
        public GameSnapshot Game = new GameSnapshot();
        public CheatStatus[] Cheats = new CheatStatus[CheatEngine.CheatCount];
        public GollumInfo Gollum = new GollumInfo();
        public string LastEvent = "";
        public bool LastEventIsError;
        public int EventSerial;
        public EngineEvent[] Events = new EngineEvent[0];   // most recent last
        public string PowerPointsText = "", HeroText = "", UnlockText = "";
        public GameProfile Profile;   // engine of the attached game, null when none
        public string WeatherText = ""; public WeatherMode Weather;
        public GameProfile SeenProfile;   // a supported game that is running but not connected (other game selected / patch not verified)
        // All Factions page (CheatEngine.Factions.cs)
        public string FactionText = "", SpawnText = "", PlayerTemplateName = "", OwnTemplateName = ""; public bool FactionSwitched;
        /// <summary>MY HEROES page: the local player's heroes (only filled while that page is open) and the result of the last click.</summary>
        public HeroInfo[] Heroes = new HeroInfo[0]; public string HeroListText = "";
    }

    public sealed class EngineEvent { public int Serial; public string Text; public bool IsError; public DateTime When; }

    /// <summary>
    /// Owns the connection to the game and applies the cheats. Everything that touches the game runs on one worker
    /// thread; the UI only flips the public switches and reads <see cref="Current"/>.
    /// Design rules:
    ///  * Only the local human player's data is changed (enemy AI is never touched).
    ///  * Nothing is applied outside an offline match (skirmish / single player).
    ///  * Every value that is overwritten is remembered and written back when its switch goes off.
    /// </summary>
    public sealed partial class CheatEngine : IDisposable
    {
        // ---- switches (set from the UI thread, read by the worker) ----
        public const int CheatCount = 14;
        readonly bool[] _on = new bool[CheatCount];
        bool _money { get { return _on[(int)CheatId.Money]; } set { _on[(int)CheatId.Money] = value; } }
        bool _army { get { return _on[(int)CheatId.InstantArmy]; } set { _on[(int)CheatId.InstantArmy] = value; } }
        bool _build { get { return _on[(int)CheatId.InstantBuild]; } set { _on[(int)CheatId.InstantBuild] = value; } }
        bool _upgrades { get { return _on[(int)CheatId.InstantUpgrades]; } set { _on[(int)CheatId.InstantUpgrades] = value; } }
        bool _cp { get { return _on[(int)CheatId.CommandPoints]; } set { _on[(int)CheatId.CommandPoints] = value; } }
        bool _freezeAi { get { return _on[(int)CheatId.FreezeAI]; } set { _on[(int)CheatId.FreezeAI] = value; } }
        bool _powerPoints { get { return _on[(int)CheatId.PowerPoints]; } set { _on[(int)CheatId.PowerPoints] = value; } }
        bool _noCooldowns { get { return _on[(int)CheatId.NoCooldowns]; } set { _on[(int)CheatId.NoCooldowns] = value; } }
        bool _reveal { get { return _on[(int)CheatId.RevealMap]; } set { _on[(int)CheatId.RevealMap] = value; } }
        bool _zoom { get { return _on[(int)CheatId.Zoom]; } set { _on[(int)CheatId.Zoom] = value; } }
        bool _track { get { return _on[(int)CheatId.TrackGollum]; } set { _on[(int)CheatId.TrackGollum] = value; } }
        volatile int _moneyAmount = 999999, _cpLimit = 10000; volatile float _zoomFactor = 1.6f;
        public int MoneyAmount { get { return _moneyAmount; } set { _moneyAmount = Math.Max(1000, Math.Min(9999999, value)); } }
        public int CommandPointLimit { get { return _cpLimit; } set { _cpLimit = Math.Max(100, Math.Min(99999, value)); } }
        /// <summary>How much farther than the map's normal limit the camera may zoom out (1.0 = normal).</summary>
        public float ZoomFactor { get { return _zoomFactor; } set { _zoomFactor = Math.Max(1f, Math.Min(MaxZoomFactor, value)); } }
        public const float MaxZoomFactor = 3.0f;

        public bool GetSwitch(CheatId id) { return _on[(int)id]; }
        public void SetSwitch(CheatId id, bool on) { _on[(int)id] = on; _wake.Set(); }

        /// <summary>Process id of the attached game (0 when not attached); used by the UI for overlay placement and hotkeys.</summary>
        public int GamePid { get { return _attached ? _m.ProcessId : 0; } }
        /// <summary>Counts every time the local player's money was seen to drop (a purchase). Used by Hold-to-Recruit.</summary>
        public int SpendEvents { get { return _spendEvents; } }
        volatile int _spendEvents; int _lastMoneySeen = int.MinValue;
        // ---- one-shot requests ----
        volatile int _findRequest;   // 0 none, 1 find + ping, 2 find + ping + move camera
        public void RequestFindGollum(bool moveCamera) { _findRequest = moveCamera ? 2 : 1; _wake.Set(); }
        volatile int _unlockRequest, _levelRequest;   // level: 1 = selected hero, 2 = all heroes
        public void RequestUnlockPowers() { _unlockRequest = 1; _wake.Set(); }
        public void RequestLevelHeroes(bool selectedOnly) { _levelRequest = selectedOnly ? 1 : 2; _wake.Set(); }

        public event Action<EngineSnapshot> Updated;
        public EngineSnapshot Current { get { return _current; } }
        volatile EngineSnapshot _current = NewSnapshot();
        static EngineSnapshot NewSnapshot() { var s = new EngineSnapshot(); for (int i = 0; i < s.Cheats.Length; i++) s.Cheats[i] = new CheatStatus(); return s; }

        // ---- worker state ----
        readonly GameMemory _m = new GameMemory();
        MainThreadHook _hook;
        Thread _thread; readonly AutoResetEvent _wake = new AutoResetEvent(false); volatile bool _stop;
        bool _attached; string _unsupported; DateTime _nextAttach = DateTime.MinValue;
        bool _wasInMatch; uint _lastFrame, _lastPlayer;
        DateTime _nextHookTry = DateTime.MinValue, _nextTrack = DateTime.MinValue;
        readonly CheatStatus[] _st = new CheatStatus[CheatCount];
        GollumInfo _gollum = new GollumInfo();
        string _event = ""; bool _eventErr; int _eventSerial;
        readonly bool[] _announced = new bool[CheatCount];

        /// <summary>A memory cell we overwrite while a cheat is on and put back afterwards.</summary>
        sealed class Override { public bool Saved; public uint Orig, Written; }
        readonly Override _ovUnits = new Override(), _ovBuild = new Override(), _ovCpBase = new Override(), _ovCpCap = new Override(), _ovZoom = new Override(), _ovFar = new Override();
        bool _revealActive; int _revealIndex; bool _persistDirty;

        public CheatEngine() { for (int i = 0; i < _st.Length; i++) _st[i] = new CheatStatus(); }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "WitchKingTrainer engine" };
            _thread.Start();
        }

        /// <summary>Switches everything off, restores the game's own values, removes the hook and stops the worker.</summary>
        public void Stop()
        {
            if (_thread == null) return;
            _stop = true; _wake.Set();
            if (!_thread.Join(4000)) { try { _thread.Abort(); } catch { } }
            _thread = null;
        }
        public void Dispose() { Stop(); _m.Dispose(); }

        void Run()
        {
            while (!_stop)
            {
                try { Tick(); }
                catch (Exception ex) { Event("Internal error: " + ex.Message, true); }
                Publish();
                _wake.WaitOne(_attached ? 100 : 700);
            }
            try { Shutdown(); } catch { }
            Publish();
        }

        // ------------------------------------------------------------------ attach / detach
        Connection _conn = Connection.NotRunning; string _connText = "Game not running", _detail = "Start the game and begin a skirmish.";
        GameSnapshot _game = new GameSnapshot();

        void SetConn(Connection c, string text, string detail) { _conn = c; _connText = text; _detail = detail; }

        /// <summary>Engine profile of the attached game (null while not attached).</summary>
        public GameProfile Profile { get { return _profile; } }
        volatile GameProfile _profile;
        const string StartHint = "Start the game and begin a skirmish.";

        void TryAttach()
        {
            if (DateTime.UtcNow < _nextAttach) return;
            _nextAttach = DateTime.UtcNow.AddSeconds(1);
            List<Process> candidates = GameMemory.FindGameProcesses();
            if (candidates.Count == 0) { _unsupported = null; _seen = null; _seenPid = 0; SetConn(Connection.NotRunning, "Game not running", StartHint); return; }
            bool denied = false, starting = false; string bad = null, blocked = null; GameProfile other = null; _seen = null; _seenPid = 0;
            foreach (Process p in candidates)
            {
                string err; AttachResult r = _m.Open(p, out err);
                if (r == AttachResult.AccessDenied) { denied = true; continue; }
                if (r != AttachResult.Ok) continue;
                string why; GameProfile prof = Offsets.Detect(_m, out why);
                if (prof == null)
                {
                    // a launcher stub is simply skipped; a full-size game executable that matches no profile is an unsupported build
                    if (_m.ImageSize() > 0x00800000) bad = why;
                    _m.Close(); continue;
                }
                if (_onlyGame >= 0 && (int)prof.Game != _onlyGame) { other = prof; if (_seen == null) { _seen = prof; _seenPid = p.Id; } _m.Close(); continue; }
                string gate = null, exePath = null; Func<GameProfile, string, string> pg = PatchGate;
                if (pg != null) { try { exePath = p.MainModule.FileName; } catch { } try { gate = pg(prof, exePath); } catch { } }
                if (gate != null) { blocked = gate; _seen = prof; _seenPid = p.Id; _m.Close(); continue; }
                Offsets.Use(prof);
                // the process may still be initialising: wait until its singletons exist
                if (_m.U32(Offsets.ThePlayerList) == 0 || _m.U32(Offsets.TheGameLogic) == 0) { _m.Close(); starting = true; continue; }
                _attached = true; _unsupported = null; _profile = prof; _seen = null; _hook = new MainThreadHook(_m); _spawnReady = 0; _factionReady = 0; _heroSelectReady = 0; _facWhyNot = ""; _detUpgrades = NewUpgradeDetour(); _detGod = NewGodDetour(); _detKillGuard = NewKillGuardDetour(); _detBuildTime = NewBuildTimeDetour(); _detMarkerA = NewMarkerDetour(Offsets.MarkerZoomSiteA, 0x980); _detMarkerB = NewMarkerDetour(Offsets.MarkerZoomSiteB, 0x9C0); _detBars = NewHealthBarDetour();
                _wasInMatch = false; _lastFrame = 0; _lastPlayer = 0;
                ClearOverrides(true); _revealActive = false;
                LoadPersist();
                Event("Connected to " + prof.GameName + " (engine " + prof.EngineBuild + ").", false);
                return;
            }
            if (starting) SetConn(Connection.Starting, "Game is starting", "Waiting for the game to finish loading.");
            else if (blocked != null) { SetConn(Connection.Unsupported, "Patch not verified - cheats are off", blocked); _nextAttach = DateTime.UtcNow.AddSeconds(3); }
            else if (other != null && !denied) SetConn(Connection.NotRunning, "Selected game is not running", other.GameName + " is running. Choose it (or Auto-detect) in the game list at the top to use the trainer with it.");
            else if (denied) SetConn(Connection.NeedsElevation, "Administrator rights needed", "The game runs as administrator, so the trainer must too. Click here to restart the trainer as administrator.");
            else if (bad != null) { _unsupported = bad; SetConn(Connection.Unsupported, "Unsupported game build", "This executable is not a verified build (" + bad + "). Supported: RotWK engine 2.01 (Patch 2.01 / 2.02) and BFME2 engine 1.06. Nothing was changed."); _nextAttach = DateTime.UtcNow.AddSeconds(5); }
            else SetConn(Connection.NotRunning, "Game not running", StartHint);
        }
        void Detach(string why)
        {
            _attached = false; _hook = null; _profile = null; _m.Close(); _wasInMatch = false;
            ClearOverrides(true); _revealActive = false; _gollum = new GollumInfo();
            SetConn(Connection.NotRunning, "Game not running", StartHint);
            if (why != null) Event(why, false);
        }

        void ClearOverrides(bool includeGlobal)
        {
            _ovUnits.Saved = _ovBuild.Saved = _ovCpBase.Saved = _ovCpCap.Saved = _ovZoom.Saved = false;
            _weatherApplied = WeatherMode.Default; _snowTouched = _boxRaised = _snowRestorePending = false; _weatherText = "";
            _mkSlot = 0; _gObj = 0; _frozenAlliances.Clear(); _idledPlayers.Clear(); _relation.Clear(); _neutralised.Clear(); _freezeRepaired = false;
            FactionsMatchEnded(); HeroListMatchEnded();
            _ppSaved = false; _teamOwner.Clear(); _heroText = ""; _unlockText = ""; _powerPointsText = "";
            if (includeGlobal) _ovFar.Saved = false;
        }

        // ------------------------------------------------------------------ main tick
        void Tick()
        {
            LogSwitches(); HandleReattach();
            if (!_attached) { TryAttach(); if (!_attached) { ResetStatuses(); return; } }
            if (_m.HasExited) { Detach("The game was closed."); ResetStatuses(); return; }

            GameSnapshot g = GameReader.Read(_m);
            _game = g;
            if (!g.InMatch)
            {
                if (_wasInMatch) { _wasInMatch = false; ClearOverrides(false); _revealActive = false; _gollum = new GollumInfo(); }
                bool mp = g.OtherModeRunning;
                SetConn(mp ? Connection.Multiplayer : Connection.InMenus, mp ? "Connected - cheats disabled in this mode" : "Connected - waiting for a match", g.NotInMatchReason);
                ApplyFarClip(1f);   // the far-clip value is global, so make sure it is normal outside matches
                WeatherMatchEnded();
                if (_hook != null && _hook.Cave != 0 && _detGod != null && _detGod.Installed) _m.WriteU32(_hook.FlagAddress(FlagGod), 0);
                if (_hook != null && _hook.Cave != 0 && _detBuildTime != null && _detBuildTime.Installed) _hook.SetFlag(FlagBuildTime, false);
                ApplyMarkerScale(1f);   // the camera limit is the map's own again outside a match
                ResetStatuses();
                _findRequest = 0;
                FlushPersist();
                return;
            }

            if (!_wasInMatch || g.Frame + 10 < _lastFrame || g.Player != _lastPlayer)
            {
                // a new match (or a restart): the game has re-initialised its own values, so forget ours
                bool restart = _wasInMatch;
                ClearOverrides(false); _revealActive = false; _gollum = new GollumInfo(); Array.Clear(_announced, 0, _announced.Length);
                _persistDirty = true;
                if (restart) Event("New match detected - cheats re-applied.", false);
            }
            _wasInMatch = true; _lastFrame = g.Frame; _lastPlayer = g.Player;
            SetConn(Connection.InMatch, "Connected - in match", string.Format("Match time {0:mm\\:ss}", g.MatchTime));

            if (_lastMoneySeen != int.MinValue && g.Money < _lastMoneySeen) _spendEvents++;
            ApplyMoney(g);
            _lastMoneySeen = _money ? Math.Max(g.Money, _moneyAmount) : g.Money;
            ApplyUpgrades(g);
            ApplyPowerPoints(g); HandleUnlock(g); ApplyCooldowns(g); HandleHeroLevel(g);
            ApplyFreezeAi(g);
            ApplyGodMode(g);
            ApplyWeather(g);
            ApplyHandicap(CheatId.InstantArmy, _army, g.Player + Offsets.Player_HandicapBuildTimeUnits, _ovUnits, "Units recruit instantly");
            ApplyHandicap(CheatId.InstantBuild, _build, g.Player + Offsets.Player_HandicapBuildTimeBuildings, _ovBuild, "Buildings complete instantly");
            ApplyBuildTimeGuard();
            if ((_cp || _army || _zoom) && !_detBars.Installed) _hook.EnsureDetour(_detBars);   // see NewHealthBarDetour; stays until the trainer closes
            ApplyCommandPoints(g);
            ApplyZoom(g);
            ApplyReveal(g);
            ApplyGollumMarker(g);
            // the All Factions features are new and not gameplay tested: whatever goes wrong in them must never stop the cheats above
            try { ApplyFactions(g); }
            catch (Exception ex) { _spawnText = _factionText = "Stopped after an internal error"; lock (_facLock) { _spawnQueue.Clear(); _factionJob = null; } Log("ALL FACTIONS error: " + ex.GetType().Name + ": " + ex.Message); }
            // the hero list is new as well (24 Nov 2026): the same rule
            try { ApplyHeroList(g); }
            catch (Exception ex) { _heroes = new HeroInfo[0]; _heroFocus = 0; _heroListText = "Stopped after an internal error"; Log("MY HEROES error: " + ex.GetType().Name + ": " + ex.Message); }
            FlushPersist();
        }

        void ResetStatuses()
        {
            for (int i = 0; i < _st.Length; i++)
            {
                bool on = GetSwitch((CheatId)i);
                _st[i].State = on ? CheatState.Armed : CheatState.Off;
                _st[i].Text = on ? "On - will apply when a match starts" : "";
                _announced[i] = false;
            }
        }

        void Set(CheatId id, CheatState s, string text)
        {
            CheatStatus c = _st[(int)id];
            if (s == CheatState.Active && !_announced[(int)id]) { _announced[(int)id] = true; Event(Name(id) + " is ON.", false); }
            if (s == CheatState.Off && _announced[(int)id]) { _announced[(int)id] = false; Event(Name(id) + " is OFF.", false); }
            if (s == CheatState.Error && (c.State != CheatState.Error || c.Text != text)) Event(Name(id) + ": " + text, true);
            c.State = s; c.Text = text;
        }

        public static string Name(CheatId id)
        {
            switch (id)
            {
                case CheatId.Money: return "Unlimited Money"; case CheatId.InstantArmy: return "Instant Army"; case CheatId.InstantBuild: return "Instant Construction";
                case CheatId.CommandPoints: return "Unlimited Command Points"; case CheatId.RevealMap: return "Reveal Map"; case CheatId.Zoom: return "Extended Zoom";
                case CheatId.InstantUpgrades: return "Instant Upgrades"; case CheatId.FreezeAI: return "Freeze Enemy AI"; case CheatId.PowerPoints: return "Unlimited Power Points"; case CheatId.NoCooldowns: return "No Ability Cooldowns";
                case CheatId.GodUnits: return "God Mode (units)"; case CheatId.GodHeroes: return "God Mode (heroes)"; case CheatId.GodBuildings: return "God Mode (buildings)";
                default: return "Gollum Tracking";
            }
        }

        // ------------------------------------------------------------------ individual cheats
        static uint Bits(float f) { return BitConverter.ToUInt32(BitConverter.GetBytes(f), 0); }
        static float Float(uint b) { return BitConverter.ToSingle(BitConverter.GetBytes(b), 0); }

        /// <summary>Core overwrite/restore primitive. Returns false only when memory could not be accessed.</summary>
        bool Apply(bool on, uint addr, uint desired, Override ov, uint defaultOrig)
        {
            uint cur; if (!_m.TryU32(addr, out cur)) return false;
            if (on)
            {
                if (cur != desired)
                {
                    if (!(ov.Saved && cur == ov.Written)) { ov.Orig = cur; ov.Saved = true; }   // a value we did not write = the game's own value
                    if (!_m.WriteU32(addr, desired)) return false;
                    ov.Written = desired; _persistDirty = true;
                }
                else if (!ov.Saved) { ov.Orig = defaultOrig; ov.Saved = true; ov.Written = desired; _persistDirty = true; }
                else ov.Written = desired;
            }
            else if (ov.Saved)
            {
                if (cur == ov.Written && !_m.WriteU32(addr, ov.Orig)) return false;   // only undo our own value
                ov.Saved = false; _persistDirty = true;
            }
            return true;
        }

        void ApplyMoney(GameSnapshot g)
        {
            if (!_money) { Set(CheatId.Money, CheatState.Off, ""); return; }
            int target = _moneyAmount;
            if (g.Money < target && !_m.WriteI32(g.Player + Offsets.Player_Money, target)) { Set(CheatId.Money, CheatState.Error, "Could not write to the game"); return; }
            Set(CheatId.Money, CheatState.Active, "Resources kept at " + target.ToString("N0"));
        }

        void ApplyHandicap(CheatId id, bool on, uint addr, Override ov, string activeText)
        {
            // multiplier on the local player's build time: 0 = instant, the game's own value (normally 1.0) = normal
            if (!Apply(on, addr, Bits(0f), ov, Bits(1f))) { Set(id, on ? CheatState.Error : CheatState.Off, on ? "Could not write to the game" : ""); return; }
            Set(id, on ? CheatState.Active : CheatState.Off, on ? activeText : "");
        }

        // The game sizes the markers above units (health bars, level squares) by 1 / zoom, and its "zoom" is the camera height
        // measured against the map's maximum camera height. Extended Zoom raises that maximum, so at the same camera height the
        // markers came out as many times too large as the zoom slider says (3x at 3.0). At the two places where the marker code
        // asks for the zoom, the answer is multiplied by (raised maximum / original maximum): the markers are then exactly as
        // large as the game itself draws them at that camera height. Nothing else that uses the zoom value is touched.
        MainThreadHook.Detour _detMarkerA, _detMarkerB; const uint OffMarkerFactor = 0xA00;
        static MainThreadHook.Detour NewMarkerDetour(uint site, uint stubOffset) { return new MainThreadHook.Detour
        {
            Name = "unit marker size", Site = site, Original = Offsets.MarkerZoomOriginal, StubOffset = stubOffset, RedirectCall = false,
            Build = (cave, at) =>
            {
                var a = new MainThreadHook.Asm();
                a.Db(Offsets.MarkerZoomOriginal);                                   // call dword [eax+0x124]   -> st0 = the view's zoom
                a.Db(0xD8, 0x0D).D32(cave + OffMarkerFactor);                       // fmul dword [factor]
                a.JmpAbs(site + (uint)Offsets.MarkerZoomOriginal.Length);
                return a.Build(at);
            }
        }; }

        void ApplyMarkerScale(float ratio)
        {
            if (_hook == null || _detMarkerA == null) return;
            bool need = ratio > 1.001f && ratio < 8f;
            if (!need)
            {
                if (_hook.Cave == 0) return; float now = _m.F32(_hook.Cave + OffMarkerFactor);
                if (now != 0f && Math.Abs(now - 1f) > 0.0005f) _m.WriteF32(_hook.Cave + OffMarkerFactor, 1f);
                return;
            }
            if (!_hook.EnsureCave()) return; uint addr = _hook.Cave + OffMarkerFactor;
            if (!(_detMarkerA.Installed && _detMarkerB.Installed))
            {
                if (!_m.WriteF32(addr, 1f)) return;                                 // neutral until both places are in
                if (!_hook.EnsureDetour(_detMarkerA) || !_hook.EnsureDetour(_detMarkerB)) return;   // the zoom itself still works without it
            }
            if (Math.Abs(_m.F32(addr) - ratio) > 0.0005f) _m.WriteF32(addr, ratio);
        }

        // The game collects every health bar of one picture in a buffer whose size it keeps in 16 bits. Roughly 900 bars - a whole
        // army selected and in view, which only the command point, army and zoom cheats make possible - overflow it and the game
        // crashes (reproduced on 18 Nov 2026: same address as the crash dump of 17:41 that day). Once one of those cheats has been
        // on, the bars of each picture are counted and those beyond a safe number are simply not drawn.
        const int MaxHealthBarsPerFrame = 600; const uint OffBarFrame = 0xA04, OffBarCount = 0xA08;
        MainThreadHook.Detour _detBars;
        static MainThreadHook.Detour NewHealthBarDetour() { return new MainThreadHook.Detour
        {
            Name = "health bar limit", Site = Offsets.HealthBarCallSite, Original = Offsets.HealthBarCallOriginal, StubOffset = 0xA40, RedirectCall = true,
            Build = (cave, at) =>
            {
                // entered instead of the bar routine: ecx = the unit's drawable, two stack arguments (the routine pops 8); eax is free
                var a = new MainThreadHook.Asm();
                a.Db(0xA1).D32(Offsets.TheGameClient).Db(0x85, 0xC0).Je("draw");            // mov eax, [TheGameClient]
                a.Db(0x8B, 0x40, (byte)Offsets.GameClient_Frame);                           // mov eax, [eax+0x10]      (number of the picture)
                a.Db(0x3B, 0x05).D32(cave + OffBarFrame).Je("same");                        // cmp eax, [lastFrame]
                a.Db(0xA3).D32(cave + OffBarFrame);                                         // a new picture: remember it, start counting again
                a.Db(0xC7, 0x05).D32(cave + OffBarCount).D32(0);
                a.Label("same").Db(0xFF, 0x05).D32(cave + OffBarCount);                     // inc dword [count]
                a.Db(0x81, 0x3D).D32(cave + OffBarCount).D32(MaxHealthBarsPerFrame).Ja("skip");
                a.Label("draw").JmpAbs(Offsets.Fn_DrawHealthBar);
                a.Label("skip").Db(0xC2, 0x08, 0x00);                                       // ret 8 : this bar is left out
                return a.Build(at);
            }
        }; }

        /// <summary>Keeps self-building structures (walls) working while Instant Construction is on; see NewBuildTimeDetour.</summary>
        void ApplyBuildTimeGuard()
        {
            if (!_build) { if (_detBuildTime.Installed) _hook.SetFlag(FlagBuildTime, false); return; }
            if (_st[(int)CheatId.InstantBuild].State != CheatState.Active) return;
            if (!_hook.EnsureDetour(_detBuildTime) || !_hook.SetFlag(FlagBuildTime, true))
                Set(CheatId.InstantBuild, CheatState.Error, _hook.LastError ?? "Could not write to the game");
        }

        // ---- code detours (see MainThreadHook.Detour): each one is guarded by a flag and by "is this the local player" ----
        const int FlagUpgrades = 0, FlagCooldowns = 1, FlagFreezeAi = 2;
        MainThreadHook.Detour _detUpgrades;
        static MainThreadHook.Detour NewUpgradeDetour() { return new MainThreadHook.Detour
        {
            Name = "instant upgrades", Site = Offsets.UpgradeTimeCallSite, Original = Offsets.UpgradeTimeCallOriginal, StubOffset = 0x300, RedirectCall = true,
            Build = (cave, at) =>
            {
                // int __thiscall UpgradeTemplate::calcTimeToBuild(Player* player, Object* obj)  ->  1 frame for the local player while the flag is set
                var a = new MainThreadHook.Asm();
                a.Db(0x83, 0x3D).D32(cave + MainThreadHook.OffFlags + 4 * FlagUpgrades).Db(0x00).Je("orig");   // cmp dword [flag], 0 ; je orig
                a.Db(0xA1).D32(Offsets.ThePlayerList).Db(0x85, 0xC0).Je("orig");                               // mov eax, [ThePlayerList] ; test eax, eax ; jz orig
                a.Db(0x8B, 0x40, (byte)Offsets.PlayerList_Local);                                              // mov eax, [eax+0x10]      (local player)
                a.Db(0x3B, 0x44, 0x24, 0x04).Jne("orig");                                                      // cmp eax, [esp+4]         (the player buying the upgrade)
                a.Db(0xB8).D32(1).Db(0xC2, 0x08, 0x00);                                                        // mov eax, 1 ; ret 8
                a.Label("orig").JmpAbs(Offsets.Fn_UpgradeCalcTime);
                return a.Build(at);
            }
        }; }
        MainThreadHook.Detour[] AllDetours { get { return _detUpgrades == null ? new MainThreadHook.Detour[0] : new[] { _detUpgrades, _detGod, _detKillGuard, _detBuildTime, _detMarkerA, _detMarkerB, _detBars }; } }

        // Instant Construction sets the local player's build-time multiplier to 0, and the engine turns that into "0 frames".
        // Buildings put up by a builder cope with it. Things that build themselves - the wall segments between two wall hubs -
        // never finish with 0 frames: they stay "under construction at 0 %", which the game shows as sunk in the ground
        // (invisible) and without collision, so enemies walk through. While the switch is on, a result of 0 frames for one of the
        // local player's structures is therefore reported as 1 frame (a fifth of a second), which every code path completes.
        const int FlagBuildTime = 2;
        MainThreadHook.Detour _detBuildTime;
        static MainThreadHook.Detour NewBuildTimeDetour() { return new MainThreadHook.Detour
        {
            Name = "instant construction", Site = Offsets.Fn_CalcTimeToBuild, Original = Offsets.CalcTimeToBuildOriginal, StubOffset = 0x900, RedirectCall = false,
            Build = (cave, at) =>
            {
                // entered instead of the routine: ecx = ThingTemplate*, [esp+4] = Player*, [esp+8] = factory Object*, [esp+12] = override; the routine pops 12
                var a = new MainThreadHook.Asm();
                a.Db(0x83, 0x3D).D32(cave + MainThreadHook.OffFlags + 4 * FlagBuildTime).Db(0x00).Je("plain");   // cmp dword [flag], 0
                a.Db(0xA1).D32(Offsets.ThePlayerList).Db(0x85, 0xC0).Je("plain");                                // mov eax, [ThePlayerList]
                a.Db(0x8B, 0x40, (byte)Offsets.PlayerList_Local);                                                // mov eax, [eax+0x10]   (local player)
                a.Db(0x3B, 0x44, 0x24, 0x04).Jne("plain");                                                       // cmp eax, [esp+4]      (the player who builds)
                a.Db(0xF6, 0x81).D32(Offsets.Template_KindOf).Db(0x80).Je("plain");                              // test byte [ecx+kindOf], STRUCTURE
                a.Db(0xFF, 0x74, 0x24, 0x0C, 0xFF, 0x74, 0x24, 0x0C, 0xFF, 0x74, 0x24, 0x0C);                    // push the three arguments again
                a.Call("plain");                                                                                 // the routine itself (it pops the copies)
                a.Db(0x83, 0xF8, 0x02, 0x7D, 0x05, 0xB8).D32(2);                                                 // cmp eax, 2 ; jge +5 ; mov eax, 2
                a.Db(0xC2, 0x0C, 0x00);                                                                          // ret 12
                a.Label("plain").Db(Offsets.CalcTimeToBuildOriginal).JmpAbs(Offsets.Fn_CalcTimeToBuild + (uint)Offsets.CalcTimeToBuildOriginal.Length);
                return a.Build(at);
            }
        }; }

        void ApplyUpgrades(GameSnapshot g)
        {
            if (!_upgrades) { if (_detUpgrades.Installed) _hook.SetFlag(FlagUpgrades, false); Set(CheatId.InstantUpgrades, CheatState.Off, ""); return; }
            if (!_hook.EnsureDetour(_detUpgrades) || !_hook.SetFlag(FlagUpgrades, true)) { Set(CheatId.InstantUpgrades, CheatState.Error, _hook.LastError ?? "Could not write to the game"); return; }
            Set(CheatId.InstantUpgrades, CheatState.Active, "Upgrades and research finish instantly");
        }

        void ApplyCommandPoints(GameSnapshot g)
        {
            uint limit = (uint)_cpLimit;
            // limit = min(base + structure bonus, cap). Setting base and cap to the same number gives exactly that limit.
            bool ok = Apply(_cp, g.Player + Offsets.Player_CpCap, limit, _ovCpCap, limit) & Apply(_cp, g.Player + Offsets.Player_CpBase, limit, _ovCpBase, limit);
            if (!_cp) { Set(CheatId.CommandPoints, CheatState.Off, ""); return; }
            if (!ok) { Set(CheatId.CommandPoints, CheatState.Error, "Could not write to the game"); return; }
            Set(CheatId.CommandPoints, CheatState.Active, "Command point limit " + limit.ToString("N0"));
        }

        /// <summary>
        /// The camera's far plane is a fixed distance (1800 x a global scale). When the camera is allowed higher than the
        /// map's normal limit the far plane must move out by the same ratio, otherwise distant terrain is cut off.
        /// ratio = how many times higher than normal the camera may currently be (1 = normal: the game's own value is restored).
        /// </summary>
        void ApplyFarClip(float ratio)
        {
            uint gd = _m.U32(Offsets.TheGlobalData); if (gd == 0) return;
            uint addr = gd + Offsets.GlobalData_FarClipScale;
            float orig = _ovFar.Saved ? Float(_ovFar.Orig) : _m.F32(addr);
            if (orig < 0.5f || orig > 10f) orig = 1.2f;
            bool on = ratio > 1.02f;
            // quantise so the value is not rewritten on every tiny camera movement
            float want = orig * (float)Math.Ceiling(Math.Min(ratio, MaxZoomFactor + 0.5f) * 1.15f * 10f) / 10f;
            Apply(on, addr, Bits(want), _ovFar, Bits(1.2f));
        }

        void ApplyZoom(GameSnapshot g)
        {
            if (g.View == 0) { Set(CheatId.Zoom, _zoom ? CheatState.Error : CheatState.Off, _zoom ? "Camera not found" : ""); return; }
            uint addr = g.View + Offsets.View_CameraLimits + Offsets.CameraLimits_MaxHeight;
            float orig = _ovZoom.Saved ? Float(_ovZoom.Orig) : g.CameraMaxHeight;
            if (orig < 100f || orig > 5000f) { Set(CheatId.Zoom, _zoom ? CheatState.Error : CheatState.Off, _zoom ? "Unexpected camera limit" : ""); return; }
            float want = (float)Math.Round(orig * _zoomFactor);
            bool ok = Apply(_zoom, addr, Bits(want), _ovZoom, Bits(560f));

            // The game only enforces its limit on the next mouse-wheel input. If the camera is currently above the limit
            // that now applies (zoom switched off, or the slider lowered), bring its target height down so it glides back,
            // and keep the far plane extended until it has actually arrived.
            float limit = _zoom ? want : orig, target, current;
            if (_m.TryF32(g.View + Offsets.View_TargetHeight, out target) && _m.TryF32(g.View + Offsets.View_CurrentHeight, out current) && target > 0 && target < 20000 && current > 0 && current < 20000)
            {
                if (target > limit + 0.5f) { _m.WriteF32(g.View + Offsets.View_TargetHeight, limit); target = limit; }
                ApplyFarClip(Math.Max(limit, Math.Max(target, current)) / orig);
            }
            else ApplyFarClip(limit / orig);

            ApplyMarkerScale(_zoom && ok ? want / orig : 1f);
            if (!_zoom) { Set(CheatId.Zoom, CheatState.Off, ""); return; }
            if (!ok) { Set(CheatId.Zoom, CheatState.Error, "Could not write to the game"); return; }
            Set(CheatId.Zoom, CheatState.Active, string.Format("Zoom-out limit {0:0.0}x (camera height {1:0}, normal {2:0})", _zoomFactor, want, orig));
        }

        bool EnsureHook(CheatId forCheat)
        {
            if (_hook.Installed) return true;
            if (DateTime.UtcNow < _nextHookTry) return false;
            if (_hook.Install()) return true;
            _nextHookTry = DateTime.UtcNow.AddSeconds(3);
            Set(forCheat, CheatState.Error, _hook.LastError ?? "Could not connect to the game's update loop");
            return false;
        }

        DateTime _nextRevealTry = DateTime.MinValue;
        void ApplyReveal(GameSnapshot g)
        {
            if (_reveal == _revealActive)
            {
                Set(CheatId.RevealMap, _reveal ? CheatState.Active : CheatState.Off, _reveal ? "Whole map visible for you only" : "");
                return;
            }
            if (DateTime.UtcNow < _nextRevealTry) return;
            if (!EnsureHook(CheatId.RevealMap)) return;
            uint pm = _m.U32(Offsets.ThePartitionManager); if (pm == 0) { Set(CheatId.RevealMap, CheatState.Error, "Fog-of-war system not found"); return; }
            uint res;
            if (_reveal)
            {
                if (_hook.Call(Offsets.Fn_RevealMapPermanently, pm, new[] { (uint)g.PlayerIndex }, 450, out res))
                { _revealActive = true; _revealIndex = g.PlayerIndex; _persistDirty = true; Set(CheatId.RevealMap, CheatState.Active, "Whole map visible for you only"); }
                else { _nextRevealTry = DateTime.UtcNow.AddMilliseconds(600); Set(CheatId.RevealMap, CheatState.Pending, "Waiting for the game (resume the match)"); }
            }
            else
            {
                if (_hook.Call(Offsets.Fn_UndoRevealMapPermanently, pm, new[] { (uint)_revealIndex }, 450, out res))
                { _revealActive = false; _persistDirty = true; Set(CheatId.RevealMap, CheatState.Off, ""); }
                else { _nextRevealTry = DateTime.UtcNow.AddMilliseconds(600); Set(CheatId.RevealMap, CheatState.Pending, "Fog returns when the match resumes"); }
            }
        }

        // ---- Gollum ----
        static bool IsGollum(string n) { return n.StartsWith("NeutralGollum", StringComparison.OrdinalIgnoreCase); }
        static bool IsDroppedRing(string n) { return n.Equals("TheDroppedRing", StringComparison.OrdinalIgnoreCase); }

        bool WritePos(GollumInfo i, out uint pos)
        {
            pos = _hook.Cave + MainThreadHook.OffScratch;
            var b = new byte[12]; BitConverter.GetBytes(i.X).CopyTo(b, 0); BitConverter.GetBytes(i.Y).CopyTo(b, 4); BitConverter.GetBytes(i.Z).CopyTo(b, 8);
            return _m.WriteBytes(pos, b);
        }

        bool MoveCamera(GollumInfo i, GameSnapshot g)
        {
            uint view = _m.U32(Offsets.TheTacticalView), pos, res;
            if (view == 0 || _m.U32(view) != Offsets.View_Vtable || _m.U32(Offsets.View_Vtable + Offsets.View_MoveCameraToSlot) != Offsets.Fn_ViewMoveCameraTo) return false;
            if (!WritePos(i, out pos)) return false;
            return _hook.Call(Offsets.Fn_ViewMoveCameraTo, view, new[] { pos, 0u, 600u, 1u, Bits(0f), Bits(0f) }, 450, out res);
        }

        // ------------------------------------------------------------------ persistence inside the game process
        // Lets a restarted trainer undo what an earlier session left active (e.g. after a crash).
        const uint PersistMagic = 0x31504B57; // "WKP1"
        Override[] PersistList { get { return new[] { _ovUnits, _ovBuild, _ovCpBase, _ovCpCap, _ovZoom, _ovFar }; } }

        void LoadPersist()
        {
            if (!_hook.EnsureCave()) return;
            _hook.ClearFlags();
            LoadWeatherPersist();
            _hook.CancelDeferred();   // an undo left behind by an earlier session that has not run yet: this session takes over
            uint p = _hook.Cave + MainThreadHook.OffPersist; byte[] b = _m.ReadBytes(p, 0x60);
            if (b == null || BitConverter.ToUInt32(b, 0) != PersistMagic) return;
            uint player = BitConverter.ToUInt32(b, 4), frame = BitConverter.ToUInt32(b, 8);
            GameSnapshot g = GameReader.Read(_m);
            Override[] list = PersistList;
            bool sameMatch = g.InMatch && g.Player == player && g.Frame >= frame;
            for (int i = 0; i < list.Length; i++)
            {
                bool global = list[i] == _ovFar;
                if (!sameMatch && !global) continue;
                int o = 0x14 + i * 12;
                list[i].Saved = BitConverter.ToUInt32(b, o) != 0; list[i].Orig = BitConverter.ToUInt32(b, o + 4); list[i].Written = BitConverter.ToUInt32(b, o + 8);
            }
            if (sameMatch) { _revealActive = BitConverter.ToUInt32(b, 12) != 0; _revealIndex = BitConverter.ToInt32(b, 16); _wasInMatch = true; _lastFrame = g.Frame; _lastPlayer = g.Player; }
        }

        void FlushPersist()
        {
            if (!_persistDirty || _hook == null || _hook.Cave == 0) return;
            _persistDirty = false;
            var b = new byte[0x60]; Override[] list = PersistList;
            BitConverter.GetBytes(PersistMagic).CopyTo(b, 0); BitConverter.GetBytes(_lastPlayer).CopyTo(b, 4); BitConverter.GetBytes(_lastFrame).CopyTo(b, 8);
            BitConverter.GetBytes(_revealActive ? 1u : 0u).CopyTo(b, 12); BitConverter.GetBytes(_revealIndex).CopyTo(b, 16);
            for (int i = 0; i < list.Length; i++)
            {
                int o = 0x14 + i * 12;
                BitConverter.GetBytes(list[i].Saved ? 1u : 0u).CopyTo(b, o); BitConverter.GetBytes(list[i].Orig).CopyTo(b, o + 4); BitConverter.GetBytes(list[i].Written).CopyTo(b, o + 8);
            }
            _m.WriteBytes(_hook.Cave + MainThreadHook.OffPersist, b);
        }

        // ------------------------------------------------------------------ shutdown
        void Shutdown()
        {
            if (!_attached || _m.HasExited) return;
            for (int i = 0; i < _on.Length; i++) _on[i] = false; _findRequest = 0;
            GameSnapshot g = GameReader.Read(_m); _game = g; bool deferred = false;
            // Everything is taken out of the game again - except the health bar limit (see NewHealthBarDetour). It protects the
            // game from its own crash with a very large army on screen, and such an army is still there after the trainer has
            // closed: taking the limit out with about 1,400 units selected and in view crashed the game at once (test of
            // 24 Nov 2026, same address as the earlier dumps). It is a few bytes that only cap the bars at 600 per picture, it
            // needs nothing from the trainer, and it is gone when the game itself closes. The next start takes it over.
            if (_hook != null)
            {
                _hook.ClearFlags();
                foreach (MainThreadHook.Detour d in AllDetours) if (d != _detBars) _hook.RemoveDetour(d);
                if (_detBars != null && _detBars.Installed) Log("health bar limit (600 per picture) stays in the game until the game closes");
            }
            if (g.InMatch && _wasInMatch)
            {
                ApplyHandicap(CheatId.InstantArmy, false, g.Player + Offsets.Player_HandicapBuildTimeUnits, _ovUnits, "");
                ApplyHandicap(CheatId.InstantBuild, false, g.Player + Offsets.Player_HandicapBuildTimeBuildings, _ovBuild, "");
                ApplyCommandPoints(g);
                ApplyZoom(g);
                RemoveMarker(); if (_frozenAlliances.Count > 0 || _idledPlayers.Count > 0 || _neutralised.Count > 0) UnfreezeAi();
                _weatherWanted = 0; WeatherShutdown();
                try { string err; if (PowersSwitched && _hook != null && _hook.Installed) RestoreFaction(g, out err); } catch { }   // your own faction's power bar comes back when the trainer closes
                ApplyPowerPoints(g);
                // give the camera a moment to glide back inside the normal limit before the draw distance is put back
                for (int i = 0; i < 25 && _ovFar.Saved; i++) { Thread.Sleep(100); GameSnapshot g2 = GameReader.Read(_m); if (!g2.InMatch) break; ApplyZoom(g2); }
                if (_ovFar.Saved) ApplyFarClip(1f);
                if (_revealActive)
                {
                    uint pm = _m.U32(Offsets.ThePartitionManager), res;
                    if (pm != 0 && _hook.Installed && _hook.Call(Offsets.Fn_UndoRevealMapPermanently, pm, new[] { (uint)_revealIndex }, 1200, out res)) _revealActive = false;
                    _persistDirty = true; FlushPersist();
                    if (_revealActive && pm != 0 && _hook.Installed)
                    {
                        // The game is not running logic frames right now (paused, or minimised in the background), so it cannot
                        // perform the undo yet. Leave the request with the in-game helper: it runs when this match resumes.
                        GameSnapshot now = GameReader.Read(_m);
                        deferred = _hook.Defer(Offsets.Fn_UndoRevealMapPermanently, pm, new[] { (uint)_revealIndex }, now.Frame, g.Player);
                    }
                }
            }
            else ApplyFarClip(1f);
            FlushPersist();
            if (_hook != null && !_revealActive) _hook.Uninstall();   // stays in place only while a deferred undo is waiting
            for (int i = 0; i < _st.Length; i++) { _st[i].State = CheatState.Off; _st[i].Text = ""; }
            _m.Close(); _attached = false;
            SetConn(Connection.NotRunning, "Trainer stopped", deferred ? "Cheats are off. The fog of war returns as soon as the match resumes." : "All cheats were switched off and the game's own values restored.");
        }

        // ------------------------------------------------------------------ publishing
        readonly List<EngineEvent> _events = new List<EngineEvent>();
        string _powerPointsText = "", _heroText = "", _unlockText = "";
        void Event(string text, bool error)
        {
            _event = text; _eventErr = error; _eventSerial++; Log((error ? "ERROR  " : "") + text);
            _events.Add(new EngineEvent { Serial = _eventSerial, Text = text, IsError = error, When = DateTime.Now });
            if (_events.Count > 40) _events.RemoveAt(0);
        }

        void Publish()
        {
            var s = new EngineSnapshot { Connection = _conn, ConnectionText = _connText, Detail = _detail, Game = _game, Gollum = _gollum, LastEvent = _event, LastEventIsError = _eventErr, EventSerial = _eventSerial, Events = _events.ToArray(), PowerPointsText = _powerPointsText, HeroText = _heroText, UnlockText = _unlockText, Profile = _profile, WeatherText = _weatherText, Weather = _weatherApplied, SeenProfile = _seen, FactionText = _factionText, SpawnText = _spawnText, PlayerTemplateName = _templateName, OwnTemplateName = _ownTemplateName, FactionSwitched = PowersSwitched, Heroes = _heroes, HeroListText = _heroListText };
            for (int i = 0; i < _st.Length; i++) s.Cheats[i] = _st[i].Clone();
            _current = s;
            Action<EngineSnapshot> h = Updated; if (h != null) { try { h(s); } catch { } }
        }
    }
}
