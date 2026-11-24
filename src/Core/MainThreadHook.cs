using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace WitchKingTrainer.Core
{
    /// <summary>
    /// A small page of memory inside the game ("the cave") that holds
    ///   * a persistent state block, so originals survive a trainer restart, and
    ///   * a stub that runs once per logic frame on the game's own main thread and executes one queued engine call.
    /// Engine functions such as "reveal map" are not thread safe, so they are never called from a foreign thread:
    /// the trainer writes a request into the cave and the game itself performs the call at the same point where
    /// it runs its own map scripts.
    /// The only code change made to the game is one 6-byte instruction replaced by a jump to the stub; it is
    /// restored when the trainer exits.
    /// </summary>
    public sealed class MainThreadHook
    {
        // ---- cave layout ----
        const uint Magic = 0x31544B57;          // "WKT1"
        const uint OffMagic = 0x00, OffState = 0x04, OffFn = 0x08, OffThis = 0x0C, OffArgc = 0x10, OffArgs = 0x14,
                   OffResult = 0x34, OffCounter = 0x38, OffEspSave = 0x3C;
        public const uint OffScratch = 0x40;    // 0x40 bytes the trainer may use for call arguments (e.g. a Coord3D)
        public const uint OffPersist = 0x80;    // 0x80 bytes of persistent trainer state (see CheatEngine)
        const uint OffCode = 0x100;
        const int CaveSize = 0x1000;

        readonly GameMemory _m;
        public uint Cave { get; private set; }
        public bool Installed { get; private set; }
        public string LastError { get; private set; }

        public MainThreadHook(GameMemory m) { _m = m; }

        /// <summary>Finds a cave left by an earlier trainer session, or allocates a new one. Does not modify game code.</summary>
        public bool EnsureCave()
        {
            if (Cave != 0) return true;
            // 1) if the hook jump is already in place it points straight at the cave
            byte[] site = _m.ReadBytes(Offsets.HookSite, 6);
            if (site != null && site[0] == 0xE9 && site[5] == 0x90)
            {
                uint target = unchecked(Offsets.HookSite + 5 + BitConverter.ToUInt32(site, 1));
                uint cave = target - OffCode;
                if (_m.U32(cave + OffMagic) == Magic) { Cave = cave; Installed = _m.U32(cave + OffStubVer) == StubVersion; return true; }
            }
            // 2) otherwise look for our page among private regions
            foreach (Native.MEMORY_BASIC_INFORMATION r in _m.PrivateRegions())
            {
                if ((uint)r.RegionSize.ToInt64() != CaveSize || r.Protect != Native.PAGE_EXECUTE_READWRITE) continue;
                uint b = unchecked((uint)r.BaseAddress.ToInt64());
                if (_m.U32(b + OffMagic) == Magic) { Cave = b; return true; }
            }
            // 3) allocate
            uint p = _m.Alloc(CaveSize, true);
            if (p == 0) { LastError = "Could not allocate memory in the game."; return false; }
            _m.WriteBytes(p, new byte[CaveSize]);
            _m.WriteU32(p + OffMagic, Magic);
            Cave = p; return true;
        }

        // deferred-call parameters and the stub's version live in the upper part of the scratch area
        const uint OffDefFrame = 0x70, OffDefPlayer = 0x74, OffStubVer = 0x78;
        const uint StubVersion = 2;
        const uint StateIdle = 0, StateCall = 1, StateDone = 2, StateDeferred = 3;

        /// <summary>Minimal x86 emitter with labels (all jumps are 32-bit relative).</summary>
        internal sealed class Asm
        {
            readonly List<byte> _b = new List<byte>(); readonly Dictionary<string, int> _labels = new Dictionary<string, int>(); readonly List<KeyValuePair<int, string>> _fix = new List<KeyValuePair<int, string>>();
            public int Count { get { return _b.Count; } }
            public Asm Db(params byte[] x) { _b.AddRange(x); return this; }
            public Asm D32(uint v) { _b.AddRange(BitConverter.GetBytes(v)); return this; }
            public Asm Label(string n) { _labels[n] = _b.Count; return this; }
            Asm Rel(string label) { _fix.Add(new KeyValuePair<int, string>(_b.Count, label)); return D32(0); }
            public Asm Je(string l) { return Db(0x0F, 0x84).Rel(l); }
            public Asm Jne(string l) { return Db(0x0F, 0x85).Rel(l); }
            public Asm Ja(string l) { return Db(0x0F, 0x87).Rel(l); }
            public Asm Jmp(string l) { return Db(0xE9).Rel(l); }
            public Asm Call(string l) { return Db(0xE8).Rel(l); }
            readonly List<KeyValuePair<int, uint>> _abs = new List<KeyValuePair<int, uint>>();
            /// <summary>jmp / call to an absolute address; needs the stub's own address at Build(baseVa) time.</summary>
            public Asm JmpAbs(uint target) { Db(0xE9); _abs.Add(new KeyValuePair<int, uint>(_b.Count, target)); return D32(0); }
            public Asm CallAbs(uint target) { Db(0xE8); _abs.Add(new KeyValuePair<int, uint>(_b.Count, target)); return D32(0); }
            public byte[] Build(uint baseVa)
            {
                foreach (KeyValuePair<int, uint> f in _abs) { byte[] r = BitConverter.GetBytes(unchecked(f.Value - (baseVa + (uint)f.Key + 4))); for (int i = 0; i < 4; i++) _b[f.Key + i] = r[i]; }
                return Build();
            }
            public byte[] Build()
            {
                foreach (KeyValuePair<int, string> f in _fix) { byte[] r = BitConverter.GetBytes(_labels[f.Value] - (f.Key + 4)); for (int i = 0; i < 4; i++) _b[f.Key + i] = r[i]; }
                return _b.ToArray();
            }
        }

        /// <summary>
        /// The stub that runs once per logic frame on the game's main thread:
        ///   state 1 = perform the queued call now;
        ///   state 3 = "deferred" call left behind by a trainer that has exited (used to give the fog of war back when the
        ///             trainer was closed while the game was paused). It only fires if the very same match simply resumes
        ///             (same player object, frame counter continuous); in any other situation it is silently dropped.
        /// </summary>
        byte[] BuildStub()
        {
            uint b = Cave; var a = new Asm();
            a.Db(0x60, 0x9C);                                        // pushad ; pushfd
            a.Db(0xFF, 0x05).D32(b + OffCounter);                    // inc dword [counter]
            a.Db(0xA1).D32(b + OffState);                            // mov eax, [state]
            a.Db(0x83, 0xF8, 0x01).Je("call");                       // cmp eax, 1 ; je call
            a.Db(0x83, 0xF8, 0x03).Jne("done");                      // cmp eax, 3 ; jne done
            a.Db(0xA1).D32(Offsets.TheGameLogic);                    // mov eax, [TheGameLogic]
            a.Db(0x85, 0xC0).Je("drop");                             // test eax, eax ; jz drop
            a.Db(0x83, 0xB8).D32(Offsets.GameLogic_GameMode).Db((byte)Offsets.GameMode_Skirmish).Jne("drop");   // cmp dword [eax+mode], skirmish
            a.Db(0x8B, 0x40, (byte)Offsets.GameLogic_Frame);         // mov eax, [eax+frame]
            a.Db(0x2B, 0x05).D32(b + OffDefFrame);                   // sub eax, [defFrame]
            a.Db(0x83, 0xF8, 0x0A).Ja("drop");                       // cmp eax, 10 ; ja drop   (unsigned: also rejects "earlier")
            a.Db(0xA1).D32(Offsets.ThePlayerList);                   // mov eax, [ThePlayerList]
            a.Db(0x85, 0xC0).Je("drop");
            a.Db(0x8B, 0x40, (byte)Offsets.PlayerList_Local);        // mov eax, [eax+local]
            a.Db(0x3B, 0x05).D32(b + OffDefPlayer).Je("call");       // cmp eax, [defPlayer] ; je call
            a.Label("drop");
            a.Db(0xC7, 0x05).D32(b + OffState).D32(StateIdle);       // mov dword [state], 0
            a.Jmp("done");
            a.Label("call");
            a.Db(0x89, 0x25).D32(b + OffEspSave);                    // mov [espsave], esp
            a.Db(0x8B, 0x0D).D32(b + OffArgc);                       // mov ecx, [argc]
            a.Db(0x85, 0xC9).Je("nopush");                           // test ecx, ecx ; jz nopush
            a.Db(0x8D, 0x34, 0x8D).D32(b + OffArgs - 4);             // lea esi, [ecx*4 + args-4]   (= &args[ecx-1])
            a.Label("loop");
            a.Db(0xFF, 0x36, 0x83, 0xEE, 0x04, 0x49).Jne("loop");    // push dword [esi] ; sub esi, 4 ; dec ecx ; jnz loop
            a.Label("nopush");
            a.Db(0x8B, 0x0D).D32(b + OffThis);                       // mov ecx, [this]
            a.Db(0xFF, 0x15).D32(b + OffFn);                         // call dword [fn]
            a.Db(0x8B, 0x25).D32(b + OffEspSave);                    // mov esp, [espsave]   (works for callee- and caller-cleanup)
            a.Db(0xA3).D32(b + OffResult);                           // mov [result], eax
            a.Db(0x83, 0x3D).D32(b + OffState).Db(0x03).Jne("notdeferred");   // cmp dword [state], 3
            a.Db(0xC7, 0x05).D32(b + OffPersist + PersistRevealFlag).D32(0);  // the deferred call was "undo reveal": record that it happened
            a.Label("notdeferred");
            a.Db(0xC7, 0x05).D32(b + OffState).D32(StateDone);       // mov dword [state], 2
            a.Label("done");
            a.Db(0x9D, 0x61);                                        // popfd ; popad
            a.Db(Offsets.HookSiteOriginal);                          // the instruction we displaced
            var c = new List<byte>(a.Build());
            c.Add(0xE9); c.AddRange(BitConverter.GetBytes(unchecked(Offsets.HookReturn - (b + OffCode + (uint)c.Count + 4))));   // jmp back
            return c.ToArray();
        }

        // ---- switchable code detours ----------------------------------------------------------------------------------
        // Each detour is a small stub in the cave that the game is redirected through. The stub tests a flag dword that the
        // trainer flips, and only then (and only for the local player) changes behaviour; otherwise it runs the original code.
        public const uint OffFlags = 0x60;      // four dword flags inside the scratch area
        public const int FlagCount = 4;
        public uint FlagAddress(int index) { return Cave + OffFlags + 4 * (uint)index; }
        public bool SetFlag(int index, bool on) { return Cave != 0 && _m.WriteU32(FlagAddress(index), on ? 1u : 0u); }
        public void ClearFlags() { if (Cave != 0) _m.WriteBytes(Cave + OffFlags, new byte[4 * FlagCount]); }

        public sealed class Detour
        {
            public string Name; public uint Site; public byte[] Original; public uint StubOffset;
            /// <summary>true: Site is an existing "call rel32" whose target is replaced. false: Original (>= 5 bytes) is replaced by a jump.</summary>
            public bool RedirectCall;
            /// <summary>Builds the stub for a given cave base and stub address.</summary>
            public Func<uint, uint, byte[]> Build;
            internal bool Installed;
        }

        byte[] PatchBytes(Detour d)
        {
            var p = new byte[d.Original.Length]; for (int i = 0; i < p.Length; i++) p[i] = 0x90;
            p[0] = d.RedirectCall ? (byte)0xE8 : (byte)0xE9;
            BitConverter.GetBytes(unchecked(Cave + d.StubOffset - (d.Site + 5))).CopyTo(p, 1);
            return p;
        }
        static bool Same(byte[] a, byte[] b) { if (a == null || a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }

        /// <summary>Installs the detour if needed. Safe to call every tick.</summary>
        public bool EnsureDetour(Detour d)
        {
            if (d.Installed) return true;
            if (!EnsureCave()) return false;
            byte[] cur = _m.ReadBytes(d.Site, d.Original.Length);
            if (cur == null) { LastError = "Could not read the game code for " + d.Name + "."; return false; }
            if (!Same(cur, d.Original))
            {
                // Exactly this detour with exactly this helper code is already in the game (the health bar limit stays in when
                // the trainer closes): take it over as it is. Nothing is written, so the game never runs without it.
                byte[] want = d.Build(Cave, Cave + d.StubOffset);
                if (Same(cur, PatchBytes(d)) && Same(_m.ReadBytes(Cave + d.StubOffset, want.Length), want)) { d.Installed = true; return true; }
                // left behind by an earlier trainer session? (a jump/call into our own page) -> take it out first, then reinstall cleanly
                bool ours = (cur[0] == 0xE8 || cur[0] == 0xE9) && unchecked(d.Site + 5 + BitConverter.ToUInt32(cur, 1)) - Cave < CaveSize;
                if (!ours) { LastError = "Another program has changed the game code used for " + d.Name + "."; return false; }
                _m.Suspend(); try { _m.WriteCode(d.Site, d.Original); } finally { _m.Resume(); }
                Thread.Sleep(120);
            }
            if (!_m.WriteBytes(Cave + d.StubOffset, d.Build(Cave, Cave + d.StubOffset))) { LastError = "Could not write helper code for " + d.Name + "."; return false; }
            bool ok; _m.Suspend(); try { ok = _m.WriteCode(d.Site, PatchBytes(d)); } finally { _m.Resume(); }
            if (!ok) { LastError = "Could not patch the game code for " + d.Name + "."; return false; }
            d.Installed = true; return true;
        }

        public void RemoveDetour(Detour d)
        {
            if (Cave == 0) { d.Installed = false; return; }
            byte[] cur = _m.ReadBytes(d.Site, d.Original.Length);
            if (cur != null && Same(cur, PatchBytes(d))) { _m.Suspend(); try { _m.WriteCode(d.Site, d.Original); } finally { _m.Resume(); } }
            d.Installed = false;
        }
        /// <summary>Offset of the "map is revealed" flag inside the persistent block (shared with CheatEngine).</summary>
        public const uint PersistRevealFlag = 12;

        /// <summary>Leaves a call behind for the game to run when the current match resumes. The trainer may exit afterwards.</summary>
        public bool Defer(uint fn, uint thisPtr, uint[] args, uint frame, uint player)
        {
            if (!Installed || Cave == 0 || args.Length > 8) return false;
            uint b = Cave; if (_m.U32(b + OffState) == StateCall) _m.WriteU32(b + OffState, StateIdle);
            var blk = new byte[OffResult - OffFn];
            BitConverter.GetBytes(fn).CopyTo(blk, 0); BitConverter.GetBytes(thisPtr).CopyTo(blk, 4); BitConverter.GetBytes(args.Length).CopyTo(blk, 8);
            for (int i = 0; i < args.Length; i++) BitConverter.GetBytes(args[i]).CopyTo(blk, 12 + 4 * i);
            return _m.WriteBytes(b + OffFn, blk) && _m.WriteU32(b + OffDefFrame, frame) && _m.WriteU32(b + OffDefPlayer, player) && _m.WriteU32(b + OffState, StateDeferred);
        }

        /// <summary>A new trainer session takes over from a deferred call that has not fired yet. Returns true if one was pending.</summary>
        public bool CancelDeferred()
        {
            if (Cave == 0 || _m.U32(Cave + OffState) != StateDeferred) return false;
            bool was;
            _m.Suspend();
            try { was = _m.U32(Cave + OffState) == StateDeferred; if (was) _m.WriteU32(Cave + OffState, StateIdle); }
            finally { _m.Resume(); }
            Thread.Sleep(30);   // if the game was inside the stub at that instant, let it finish before anyone reads the result
            return was;
        }
        /// <summary>Installs the per-frame stub (idempotent). Returns false with LastError set if the game code is not what we expect.</summary>
        public bool Install()
        {
            if (!EnsureCave()) return false;
            byte[] site = _m.ReadBytes(Offsets.HookSite, 11);
            if (site == null) { LastError = "Could not read the game's logic update code."; return false; }
            for (int i = 0; i < 5; i++) if (site[6 + i] != Offsets.HookSiteFollowing[i]) { LastError = "Unexpected game code at the hook site (unsupported build)."; return false; }
            uint rel = unchecked(Cave + OffCode - (Offsets.HookSite + 5));
            var jmp = new byte[6]; jmp[0] = 0xE9; BitConverter.GetBytes(rel).CopyTo(jmp, 1); jmp[5] = 0x90;
            bool ours = true, orig = true;
            for (int i = 0; i < 6; i++) { if (site[i] != jmp[i]) ours = false; if (site[i] != Offsets.HookSiteOriginal[i]) orig = false; }
            if (ours)
            {
                if (_m.U32(Cave + OffStubVer) == StubVersion) { Installed = true; return true; }
                // helper code from an older trainer version: take it out, let the game thread leave it, then install the current one
                _m.Suspend();
                try { if (!_m.WriteCode(Offsets.HookSite, Offsets.HookSiteOriginal)) { LastError = "Could not update the helper code in the game."; return false; } }
                finally { _m.Resume(); }
                Installed = false; Thread.Sleep(150); orig = true;
            }
            if (!orig) { LastError = "Another program has already modified the game's logic update code; map reveal and Gollum markers are unavailable while it is active."; return false; }

            _m.WriteU32(Cave + OffState, StateIdle);
            if (!_m.WriteBytes(Cave + OffCode, BuildStub()) || !_m.WriteU32(Cave + OffStubVer, StubVersion)) { LastError = "Could not write the helper code into the game."; return false; }
            _m.Suspend();
            bool ok;
            try { ok = _m.WriteCode(Offsets.HookSite, jmp); }
            finally { _m.Resume(); }
            if (!ok) { LastError = "Could not patch the game's logic update code."; return false; }
            Installed = true; return true;
        }

        /// <summary>Restores the original instruction. The cave itself is intentionally left allocated (4 KB) so no thread can ever run freed memory.</summary>
        public bool Uninstall()
        {
            byte[] site = _m.ReadBytes(Offsets.HookSite, 6);
            if (site == null) return false;
            if (site[0] != 0xE9) { Installed = false; return true; }
            if (Cave == 0 || unchecked(Offsets.HookSite + 5 + BitConverter.ToUInt32(site, 1)) != Cave + OffCode) return false; // not ours
            _m.Suspend();
            bool ok;
            try { ok = _m.WriteCode(Offsets.HookSite, Offsets.HookSiteOriginal); }
            finally { _m.Resume(); }
            if (ok) Installed = false;
            return ok;
        }

        /// <summary>Number of logic frames the stub has seen; advances only while a match is actually running (not paused).</summary>
        public uint Heartbeat { get { return Cave == 0 ? 0 : _m.U32(Cave + OffCounter); } }

        public bool IsCallPending { get { return Cave != 0 && _m.U32(Cave + OffState) == 1; } }

        /// <summary>
        /// Queues one engine call and waits for the game to execute it on its main thread.
        /// (CallCave does the same for a small routine the trainer itself wrote into its own page.)
        /// Returns false if the game did not pick it up within the timeout (e.g. it is paused or minimised);
        /// in that case the request is withdrawn so it cannot fire later unexpectedly.
        /// </summary>
        public bool CallCave(uint offset, uint[] args, int timeoutMs, out uint result)
        {
            result = 0; if (!Installed && !Install()) return false;
            _caveCall = true; try { return Call(Cave + offset, 0, args, timeoutMs, out result); } finally { _caveCall = false; }
        }
        bool _caveCall;

        public bool Call(uint fn, uint thisPtr, uint[] args, int timeoutMs, out uint result)
        {
            result = 0;
            if (!Installed && !Install()) return false;
            bool own = _caveCall && fn >= Cave + OffCode && fn < Cave + CaveSize;
            if (!own && (fn < Offsets.ImageBase + 0x1000 || fn >= Offsets.CodeEnd)) { LastError = "Refusing to call an address outside the game's code."; return false; }
            if (args == null) args = new uint[0];
            if (args.Length > 8) { LastError = "Too many arguments."; return false; }
            uint b = Cave;
            if (_m.U32(b + OffState) == 1) { LastError = "A previous request is still waiting for the game."; return false; }
            var blk = new byte[OffResult - OffFn];
            BitConverter.GetBytes(fn).CopyTo(blk, 0); BitConverter.GetBytes(thisPtr).CopyTo(blk, 4); BitConverter.GetBytes(args.Length).CopyTo(blk, 8);
            for (int i = 0; i < args.Length; i++) BitConverter.GetBytes(args[i]).CopyTo(blk, 12 + 4 * i);
            if (!_m.WriteBytes(b + OffFn, blk) || !_m.WriteU32(b + OffState, 1)) { LastError = "Could not write the request into the game."; return false; }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                uint st;
                if (!_m.TryU32(b + OffState, out st)) { LastError = "The game closed."; return false; }
                if (st == 2) { result = _m.U32(b + OffResult); _m.WriteU32(b + OffState, 0); return true; }
                Thread.Sleep(15);
            }
            // withdraw; if the stub grabbed it in this very instant it will simply complete and set state 2
            if (_m.U32(b + OffState) == 1) _m.WriteU32(b + OffState, 0);
            Thread.Sleep(30);
            if (_m.U32(b + OffState) == 2) { result = _m.U32(b + OffResult); _m.WriteU32(b + OffState, 0); return true; }
            LastError = "The game did not respond (it is paused, minimised or not in a match).";
            return false;
        }
    }
}
