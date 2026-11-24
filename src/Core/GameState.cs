using System;
using System.Collections.Generic;

namespace WitchKingTrainer.Core
{
    /// <summary>Read-only view of the running match, refreshed every engine tick.</summary>
    public sealed class GameSnapshot
    {
        public bool InMatch;
        public bool OtherModeRunning;   // a game is running, but not an offline skirmish
        public string NotInMatchReason = "";
        public int GameMode = -1;
        public uint Frame;
        public uint Player;            // address of the local Player object
        public int PlayerIndex = -1;
        public int Money, CpUsed, CpBase, CpBonus, CpCap;
        public float BuildTimeUnits = 1f, BuildTimeBuildings = 1f;
        public float MapWidth, MapHeight;
        public uint View;              // tactical view (camera)
        public float CameraMaxHeight, CameraMinHeight;

        public int CpLimit { get { long v = (long)CpBase + CpBonus; return (int)Math.Min(v, CpCap); } }
        public TimeSpan MatchTime { get { return TimeSpan.FromSeconds(Frame / (double)Offsets.LogicFramesPerSecond); } }
    }

    public sealed class WorldObject
    {
        public uint Address; public uint Id; public string TemplateName; public float X, Y, Z;
    }

    /// <summary>Stateless readers for engine structures.</summary>
    public static class GameReader
    {
        public static GameSnapshot Read(GameMemory m)
        {
            var s = new GameSnapshot();
            uint gl = m.U32(Offsets.TheGameLogic), pl = m.U32(Offsets.ThePlayerList);
            if (gl == 0 || pl == 0) { s.NotInMatchReason = "The game is still starting."; return s; }
            s.GameMode = m.I32(gl + Offsets.GameLogic_GameMode);
            s.Frame = m.U32(gl + Offsets.GameLogic_Frame);
            s.MapWidth = m.F32(gl + Offsets.GameLogic_MapWidth); s.MapHeight = m.F32(gl + Offsets.GameLogic_MapHeight);
            s.Player = m.U32(pl + Offsets.PlayerList_Local);
            s.View = m.U32(Offsets.TheTacticalView);

            // Only the offline skirmish mode (verified value 2) is ever treated as a match. LAN / online games use other
            // mode values, so the trainer stays completely inactive in them by construction.
            if (s.GameMode != Offsets.GameMode_Skirmish)
            {
                // engine game modes: 0 campaign, 1 LAN, 2 skirmish, 3 replay, 4 shell (animated main menu), 5 internet, 6+ none
                bool menu = s.GameMode == 4 || s.GameMode >= 6 || s.GameMode < 0;
                bool running = !menu && s.Frame > 30 && m.U32(gl + Offsets.GameLogic_FirstObject) != 0 && s.MapWidth > 100;
                s.OtherModeRunning = running;
                s.NotInMatchReason = running ? "This is not an offline skirmish (multiplayer, campaign or replay) - cheats stay disabled." : "In the menus - start a skirmish.";
                return s;
            }
            if (s.Player == 0 || m.U32(s.Player) != Offsets.Player_Vtable) { s.NotInMatchReason = "In the menus - start a skirmish."; return s; }
            if (m.I32(s.Player + Offsets.Player_Type) != 0) { s.NotInMatchReason = "Observing - no human player to apply cheats to."; return s; }
            if (m.U32(gl + Offsets.GameLogic_FirstObject) == 0) { s.NotInMatchReason = "Loading the map."; return s; }
            if (s.Frame < 5) { s.NotInMatchReason = "The match is starting."; return s; }

            byte[] p = m.ReadBytes(s.Player, 0xA0);
            if (p == null) { s.NotInMatchReason = "Could not read the player data."; return s; }
            s.PlayerIndex = BitConverter.ToInt32(p, (int)Offsets.Player_Index);
            s.BuildTimeUnits = BitConverter.ToSingle(p, (int)Offsets.Player_HandicapBuildTimeUnits);
            s.BuildTimeBuildings = BitConverter.ToSingle(p, (int)Offsets.Player_HandicapBuildTimeBuildings);
            s.CpBase = BitConverter.ToInt32(p, (int)Offsets.Player_CpBase); s.CpUsed = BitConverter.ToInt32(p, (int)Offsets.Player_CpUsed);
            s.CpBonus = BitConverter.ToInt32(p, (int)Offsets.Player_CpBonus); s.CpCap = BitConverter.ToInt32(p, (int)Offsets.Player_CpCap);
            s.Money = BitConverter.ToInt32(p, (int)Offsets.Player_Money);
            if (s.PlayerIndex < 0 || s.PlayerIndex >= 20) { s.NotInMatchReason = "Unexpected player data."; return s; }

            if (s.View != 0 && m.U32(s.View + Offsets.View_CameraLimits) == Offsets.CameraLimits_Vtable)
            {
                s.CameraMinHeight = m.F32(s.View + Offsets.View_CameraLimits + Offsets.CameraLimits_MinHeight);
                s.CameraMaxHeight = m.F32(s.View + Offsets.View_CameraLimits + Offsets.CameraLimits_MaxHeight);
            }
            else s.View = 0;
            s.InMatch = true;
            return s;
        }

        static string TemplateName(GameMemory m, uint obj, Dictionary<uint, string> cache)
        {
            uint tmpl = m.U32(obj + Offsets.Object_Template); if (tmpl == 0) return null;
            string name;
            if (cache.TryGetValue(tmpl, out name)) return name;
            uint str = m.U32(tmpl + Offsets.Template_Name);
            name = str == 0 ? null : m.AsciiZ(str + Offsets.AsciiString_Text, 96);
            cache[tmpl] = name;
            return name;
        }

        /// <summary>Walks the engine's object list and returns every object whose template name satisfies the filter.</summary>
        public static List<WorldObject> FindObjects(GameMemory m, Func<string, bool> filter, out int total)
        {
            var res = new List<WorldObject>(); total = 0;
            uint gl = m.U32(Offsets.TheGameLogic); if (gl == 0) return res;
            uint o = m.U32(gl + Offsets.GameLogic_FirstObject);
            var names = new Dictionary<uint, string>(); var seen = new HashSet<uint>();
            byte[] buf = new byte[0x90];
            while (o != 0 && total < 30000 && seen.Add(o))
            {
                if (!m.ReadBytes(o, buf, buf.Length) || BitConverter.ToUInt32(buf, 0) == 0) break;
                total++;
                string name = TemplateName(m, o, names);
                if (name != null && filter(name))
                    res.Add(new WorldObject
                    {
                        Address = o, Id = BitConverter.ToUInt32(buf, (int)Offsets.Object_Id), TemplateName = name,
                        X = BitConverter.ToSingle(buf, (int)Offsets.Object_PosX), Y = BitConverter.ToSingle(buf, (int)Offsets.Object_PosY), Z = BitConverter.ToSingle(buf, (int)Offsets.Object_PosZ)
                    });
                o = BitConverter.ToUInt32(buf, (int)Offsets.Object_Next);
            }
            return res;
        }
    }
}
