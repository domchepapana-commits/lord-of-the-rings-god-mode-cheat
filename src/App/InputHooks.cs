using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace WitchKingTrainer
{
    public enum HotAction { Money = 0, Army = 1, Build = 2, CommandPoints = 3, Reveal = 4, Zoom = 5, Gollum = 6, Powers = 7, HeroLevel = 8, Cooldowns = 9, FreezeAI = 10, Menu = 11, GodMode = 12, Weather = 13 }

    /// <summary>
    /// Keyboard and mouse hooks on their own thread, so a busy UI can never make the mouse or keyboard lag.
    ///  * Plain function keys (no Ctrl/Alt/Shift/Win) are taken only while the GAME (or the trainer) is the active window, so they
    ///    are never hijacked in other programs. A handled key is swallowed completely (down, auto-repeats and up) and fires once.
    ///  * Hold-to-Recruit: while the left button is held still over the game, the click is repeated. See <see cref="HoldLoop"/>.
    /// </summary>
    internal sealed class InputHooks : IDisposable
    {
        #region Win32
        delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc cb, IntPtr mod, uint tid);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)] struct KBDLL { public int vk, scan, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct MSLL { public POINT pt; public int data, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        static readonly IntPtr OwnClickTag = new IntPtr(0x574B5452);   // marks the clicks this class generates itself
        #endregion

        // ---- configuration (written by the UI thread, read by the hook thread) ----
        public volatile bool HotkeysEnabled = true;
        public volatile bool HoldEnabled;
        public volatile int HoldClicksPerSecond = 6;
        public volatile int GamePid;
        public IntPtr TrainerWindow;
        readonly int[] _keys = new int[Settings.KeyCount];                     // virtual key per HotAction (0 = none)
        public void SetKeys(int[] keys) { for (int i = 0; i < _keys.Length; i++) _keys[i] = i < keys.Length ? keys[i] : 0; }

        /// <summary>Raised on the hook thread; the handler must only queue work (e.g. Dispatcher.BeginInvoke).</summary>
        public event Action<HotAction> Fired;
        /// <summary>
        /// The menu's search field. The in-game menu never takes the keyboard away from the game, so while the field is active
        /// (TypingActive) the letters, digits, Space, Backspace, Enter and Esc typed in the game go to this handler instead of
        /// the game. Raised on the hook thread with the virtual key; the handler must only queue work.
        /// </summary>
        public event Action<int> Typed;
        public volatile bool TypingActive;
        static bool IsTypingKey(int vk) { return (vk >= 0x41 && vk <= 0x5A) || (vk >= 0x30 && vk <= 0x39) || vk == 0x20 || vk == 0x08 || vk == 0x0D || vk == 0x1B || vk == 0xBD; }
        /// <summary>Supplied by the UI: a counter that changes whenever the local player spends resources, and whether a match is running.</summary>
        public Func<int> SpendCounter; public Func<bool> InMatch;
        public volatile string HoldStatus = "";

        Thread _thread, _holdThread; uint _tid; IntPtr _kb, _ms; HookProc _kbProc, _msProc; volatile bool _stop;
        readonly HashSet<int> _swallowed = new HashSet<int>();

        public void Start()
        {
            _thread = new Thread(HookThread) { IsBackground = true, Name = "WitchKingTrainer input" }; _thread.Start();
            _holdThread = new Thread(HoldLoop) { IsBackground = true, Name = "WitchKingTrainer hold-to-recruit" }; _holdThread.Start();
        }

        void HookThread()
        {
            _tid = GetCurrentThreadId(); _kbProc = Keyboard; _msProc = Mouse; IntPtr mod = GetModuleHandle(null);
            _kb = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0); _ms = SetWindowsHookEx(WH_MOUSE_LL, _msProc, mod, 0);
            MSG m; while (!_stop && GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
            if (_kb != IntPtr.Zero) UnhookWindowsHookEx(_kb); if (_ms != IntPtr.Zero) UnhookWindowsHookEx(_ms);
        }

        public void Dispose() { _stop = true; if (_tid != 0) PostThreadMessage(_tid, 0x0012, IntPtr.Zero, IntPtr.Zero); }

        public bool GameIsForeground()
        {
            int game = GamePid; if (game == 0) return false;
            int pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid == game;
        }
        bool TrainerIsForeground() { IntPtr fg = GetForegroundWindow(); return fg != IntPtr.Zero && fg == TrainerWindow; }
        static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        IntPtr Keyboard(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                var k = (KBDLL)Marshal.PtrToStructure(l, typeof(KBDLL)); int msg = w.ToInt32(); bool down = msg == 0x100 || msg == 0x104;
                if (down)
                {
                    if (TypingActive && IsTypingKey(k.vk) && !Down(0x11) && !Down(0x12) && !Down(0x5B) && !Down(0x5C) && (GameIsForeground() || TrainerIsForeground()))
                    {
                        // typing into the menu's search field (auto-repeat included, so Backspace can be held)
                        _swallowed.Add(k.vk);
                        Action<int> t = Typed; if (t != null) { try { t(k.vk); } catch { } }
                        return (IntPtr)1;
                    }
                    if (_swallowed.Contains(k.vk)) return (IntPtr)1;                     // auto-repeat of a key we already handled
                    if (HotkeysEnabled && !Down(0x11) && !Down(0x12) && !Down(0x10) && !Down(0x5B) && !Down(0x5C))
                    {
                        int action = -1; for (int i = 0; i < _keys.Length; i++) if (_keys[i] == k.vk) { action = i; break; }
                        if (action >= 0 && (GameIsForeground() || TrainerIsForeground()))
                        {
                            _swallowed.Add(k.vk);
                            Action<HotAction> h = Fired; if (h != null) { try { h((HotAction)action); } catch { } }
                            return (IntPtr)1;
                        }
                    }
                }
                else if (_swallowed.Remove(k.vk)) return (IntPtr)1;                       // the matching key-up
            }
            return CallNextHookEx(_kb, code, w, l);
        }

        // ---- hold to recruit ----
        volatile bool _held; volatile int _holdX, _holdY, _spendAtPress; long _holdSince;   // physical left button state (never our own clicks)

        IntPtr Mouse(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0 && HoldEnabled)
            {
                int msg = w.ToInt32();
                if (msg == 0x201 || msg == 0x202 || msg == 0x200)
                {
                    var m = (MSLL)Marshal.PtrToStructure(l, typeof(MSLL));
                    if (m.extra != OwnClickTag)
                    {
                        if (msg == 0x201)
                        {
                            // only presses that land on the game itself (not on the trainer menu or another window)
                            int pid; IntPtr under = GetAncestor(WindowFromPoint(m.pt), 2); GetWindowThreadProcessId(under, out pid);
                            if (pid == GamePid && GamePid != 0 && GameIsForeground()) { Func<int> sp = SpendCounter; _spendAtPress = sp != null ? sp() : 0; _holdX = m.pt.X; _holdY = m.pt.Y; Interlocked.Exchange(ref _holdSince, Stopwatch.GetTimestamp()); _held = true; }
                        }
                        else if (msg == 0x202) _held = false;
                        else if (_held && (Math.Abs(m.pt.X - _holdX) > 6 || Math.Abs(m.pt.Y - _holdY) > 6)) _held = false;   // moved: it is a drag, leave it alone
                    }
                }
            }
            return CallNextHookEx(_ms, code, w, l);
        }

        static void Click(bool down)
        {
            var i = new INPUT[1]; i[0].type = 0; i[0].mi.dwFlags = down ? 0x0002u : 0x0004u; i[0].mi.extra = OwnClickTag;
            SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>
        /// Hold-to-Recruit. When the left button has been held still on the game for a moment:
        ///  1. the trainer checks whether the user's own click bought something (the player's resources dropped since the press);
        ///  2. only if it did, the click is repeated at the chosen rate for as long as the button stays down and every few clicks
        ///     keep buying. Holding the button on the ground, a unit, a building or the minimap therefore never produces extra clicks.
        /// The game reads the mouse once per rendered frame, so each press and each release is held long enough (45 ms or more) to be
        /// seen. The sequence always ends with a button-up, so the game can never be left believing the button is still down.
        /// </summary>
        void HoldLoop()
        {
            const int StartDelayMs = 300; long statusAt = 0;
            while (!_stop)
            {
                Thread.Sleep(15);
                if (!HoldEnabled || !_held)
                {
                    if (HoldStatus.Length != 0 && (Stopwatch.GetTimestamp() - statusAt) / (double)Stopwatch.Frequency > 4) HoldStatus = "";
                    continue;
                }
                double heldMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _holdSince)) * 1000.0 / Stopwatch.Frequency;
                if (heldMs < StartDelayMs) continue;
                Func<bool> inMatch = InMatch; Func<int> spend = SpendCounter;
                if (inMatch != null && spend != null && inMatch() && GameIsForeground())
                {
                    // Did the user's own click buy something? (This game orders a unit on the button PRESS; the release is covered too.)
                    int before = _spendAtPress; Click(false);                 // completes the click the user started
                    bool bought = spend() != before;
                    for (int t = 0; t < 520 && !bought && _held && !_stop; t += 15) { Thread.Sleep(15); bought = spend() != before; }
                    if (bought)
                    {
                        int orders = 1, last = spend(); long lastAt = Stopwatch.GetTimestamp();
                        while (_held && HoldEnabled && !_stop && GameIsForeground())
                        {
                            int half = Math.Max(45, 500 / Math.Max(2, Math.Min(10, HoldClicksPerSecond)));
                            Click(true); Thread.Sleep(half);
                            Click(false); orders++; HoldStatus = "Recruiting...  " + orders + " orders"; statusAt = Stopwatch.GetTimestamp();
                            Thread.Sleep(half);
                            int now = spend(); long ts = Stopwatch.GetTimestamp();
                            if (now != last) { last = now; lastAt = ts; }
                            else if ((ts - lastAt) * 1000.0 / Stopwatch.Frequency > 900) { HoldStatus = "Stopped - nothing more could be ordered"; break; }
                        }
                        statusAt = Stopwatch.GetTimestamp();
                    }
                }
                while (_held && !_stop) Thread.Sleep(15);   // wait for the physical release before arming again
            }
        }
    }
}