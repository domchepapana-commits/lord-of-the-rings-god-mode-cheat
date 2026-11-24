using System;
using System.IO;

namespace WitchKingTrainer.Core
{
    // Things that are not cheats: the game / patch selection and the session log.
    public sealed partial class CheatEngine
    {
        // ------------------------------------------------------------------ game and patch selection
        volatile int _onlyGame = -1;
        /// <summary>-1 = use whichever supported game is running; otherwise the <see cref="GameId"/> the trainer is limited to.</summary>
        public int OnlyGame { get { return _onlyGame; } set { if (_onlyGame != value) { _onlyGame = value; RequestReattach(); } } }
        /// <summary>Asked before the trainer connects to a game (engine profile, path of the running executable): returns null to allow it, or the reason cheats must stay off (patch not verified).</summary>
        public Func<GameProfile, string, string> PatchGate;
        volatile bool _reattach;
        /// <summary>Lets go of the game cleanly and connects again (used when the game or patch selection changes).</summary>
        public void RequestReattach() { _reattach = true; _wake.Set(); }
        volatile GameProfile _seen;
        /// <summary>Engine of a supported game that is running, even when the trainer is not connected to it (other game selected, patch not verified).</summary>
        public GameProfile SeenProfile { get { return _seen; } }
        volatile int _seenPid;
        /// <summary>Process id of that game (0 when there is none).</summary>
        public int SeenPid { get { return _attached ? 0 : _seenPid; } }

        void HandleReattach()
        {
            if (!_reattach) return; _reattach = false;
            if (_attached)
            {
                var keep = (bool[])_on.Clone(); int weather = _weatherWanted;
                try { Shutdown(); } catch { }
                Array.Copy(keep, _on, keep.Length); _weatherWanted = weather;
                if (_attached) { try { _m.Close(); } catch { } _attached = false; }
                _hook = null; _profile = null; _wasInMatch = false; ClearOverrides(true); _revealActive = false; _gollum = new GollumInfo();
            }
            _nextAttach = DateTime.MinValue;
        }

        // ------------------------------------------------------------------ session log
        /// <summary>When set, every engine event and switch change is appended to this file (helps to explain a problem afterwards).</summary>
        public static string LogPath;
        static readonly object LogLock = new object();
        readonly bool[] _loggedOn = new bool[CheatCount]; int _loggedWeather;

        internal static void Log(string text)
        {
            string path = LogPath; if (string.IsNullOrEmpty(path)) return;
            try
            {
                lock (LogLock)
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 512 * 1024) { string[] lines = File.ReadAllLines(path); File.WriteAllLines(path, System.Linq.Enumerable.Skip(lines, lines.Length / 2)); }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
                }
            }
            catch { }
        }

        void LogSwitches()
        {
            for (int i = 0; i < CheatCount; i++)
                if (_on[i] != _loggedOn[i]) { _loggedOn[i] = _on[i]; Log("switch " + (CheatId)i + (_on[i] ? " ON" : " off") + (_attached ? "" : " (no game connected)")); }
            if (_weatherWanted != _loggedWeather) { _loggedWeather = _weatherWanted; Log("weather -> " + (WeatherMode)_weatherWanted); }
        }
    }
}
