using System;
using System.Collections.Generic;
using System.Reflection;

namespace WitchKingTrainer.Core
{
    public enum GameId { Rotwk = 0, Bfme2 = 1 }

    /// <summary>One supported game ENGINE BUILD (executable). Data patches that run on the same executable share a profile.</summary>
    public sealed class GameProfile
    {
        public string Id, GameName, EngineBuild, Notes; public GameId Game;
        internal Dictionary<string, object> Overrides = new Dictionary<string, object>();
        internal Offsets.Sig[] Signatures;
        public override string ToString() { return GameName + " (engine " + EngineBuild + ")"; }
    }

    /// <summary>
    /// Addresses and structure offsets of the game engine. The values written in this file are those of the Rise of the
    /// Witch-king engine 2.01.2614.37001 (the executable Patch 2.01 and 2.02 run on). Other engine builds are described as a
    /// list of differences (see the static constructor) that was produced by matching the code of both executables
    /// (dev\retools: "ret portall") and then verified against the live game. Neither executable uses ASLR (base 0x00400000).
    /// Every profile carries code signatures; a running game is only touched when all of them match.
    /// </summary>
    public static class Offsets
    {
        public const uint ImageBase = 0x00400000;
        public static uint CodeEnd = 0x00BD0000;         // end of the .text section (calls outside it are refused)

        // ---- global singletons (pointers stored in .data) ----
        public static uint ThePlayerList = 0x00DE4928;
        public static uint TheGameLogic = 0x00DE412C;
        public static uint ThePartitionManager = 0x00DE4358;
        public static uint TheRadar = 0x00DE4AB8;
        public static uint TheTacticalView = 0x00DE447C;
        public static uint TheScriptEngine = 0x00DE3BAC;

        // ---- PlayerList ----
        public static uint PlayerList_Local = 0x10, PlayerList_Count = 0x14, PlayerList_Array = 0x18;

        // ---- Player ----
        public static uint Player_Vtable = 0x00C141BC;
        public static uint Player_Template = 0x34;
        public static uint Player_HandicapBuildTimeUnits = 0x44;      // float multiplier, 1.0 = normal
        public static uint Player_HandicapBuildTimeBuildings = 0x48;  // float multiplier, 1.0 = normal
        public static uint Player_Index = 0x54;
        public static uint Player_Type = 0x5C;                        // 0 = human
        public static uint Player_CpBase = 0x64, Player_CpUsed = 0x68, Player_CpBonus = 0x6C, Player_CpCap = 0x70;
        public static uint Player_Money = 0x94;

        // ---- GameLogic ----
        public static uint GameLogic_MapWidth = 0x30, GameLogic_MapHeight = 0x34, GameLogic_Frame = 0x40,
                           GameLogic_FirstObject = 0xAC, GameLogic_GameMode = 0x110;
        public static int GameMode_Skirmish = 2;          // verified live; the main menu reports 9
        public const int LogicFramesPerSecond = 5;

        // ---- Object / ThingTemplate ----
        public static uint Object_Vtable = 0x00C123D8;
        public static uint Object_Template = 0x04, Object_PosX = 0x38, Object_PosY = 0x3C, Object_PosZ = 0x40, Object_Id = 0x74, Object_Next = 0x8C;
        public static uint Template_Name = 0x64;          // AsciiString: pointer to { int refs; ushort len; ushort cap; char text[] }
        public static uint AsciiString_Text = 8;

        // ---- View (camera) ----
        public static uint View_CameraLimits = 0x24C8;    // embedded per-map camera settings object
        public static uint CameraLimits_Vtable = 0x00BE5648;
        public static uint CameraLimits_MinHeight = 0x04, CameraLimits_MaxHeight = 0x08, CameraLimits_Pitch = 0x0C;
        public static uint View_Vtable = 0x00BDD490;
        public static uint View_TargetHeight = 0x40;      // camera height the view is zooming toward
        public static uint View_CurrentHeight = 0x50;     // camera height right now (eases toward the target)
        public static uint View_GroundLevel = 0x54;       // height of the terrain the camera is looking at (the camera sits this + CurrentHeight above the bottom of the world)
        public static uint View_MoveCameraToSlot = 0x60;  // virtual moveCameraTo(const Coord3D*, namedCam, int ms, int flag, float easeInMs, float easeOutMs)
        public static uint Fn_ViewMoveCameraTo = 0x00489C32;

        // ---- GlobalData ----
        public static uint TheGlobalData = 0x00DE4364;
        public static uint GlobalData_FarClipScale = 0x950;   // camera far plane = 1800 * this (default 1.2)

        // ---- engine functions called through the main-thread hook ----
        public static uint Fn_RevealMapPermanently = 0x00B4D940;   // thiscall(ThePartitionManager, int playerIndex)
        public static uint Fn_UndoRevealMapPermanently = 0x00B4D960;
        public static uint Fn_RadarCreateEvent = 0x006D98FA;       // thiscall(TheRadar, const Coord3D*, int type, float seconds)

        // ---- power points / power tree (dev\notes\re-powers.md) ----
        public static uint Player_PowerPoints = 0x24, Player_SciencesBegin = 0x310, Player_SciencesEnd = 0x314, Player_SharedTimers = 0x724;
        public static uint SharedTimer_ReadyFrame = 0x0C;
        public static uint TheControlBar = 0x00DE7744;
        public static uint Fn_GetPurchaseScienceCommandSet = 0x0071F933;   // thiscall(TheControlBar, Player*) -> CommandSet*, ret 4
        public static uint Fn_GrantScience = 0x006AE3A7;                   // thiscall(Player*, scienceId) -> bool, ret 4
        public static uint CommandSet_Slots = 0x14; public const int CommandSet_SlotCount = 33;
        public static uint Button_Command = 0x14, Button_SciencesBegin = 0xA4, Button_SciencesEnd = 0xA8; public static int Command_PurchaseScience = 25;

        // ---- ability cooldowns (dev\notes\re-cooldowns.md) ----
        public static uint Object_Modules = 0x24C, Object_Team = 0x31C, Team_Owner1 = 0x30, Team_Owner2 = 0x08, Object_FireWeaponLockout = 0x478;
        public static uint Module_SecondaryVtable = 0x0C;
        public static uint SpecialPowerVtblA = 0x00C06050, SpecialPowerA_ReadyFrame = 0x18;               // SpecialPowerModule and its subclasses
        public static uint SpecialPowerVtblB1 = 0x00C87478, SpecialPowerVtblB2 = 0x00C74650, SpecialPowerB_ReadyFrame = 0x28;   // update-style powers
        public static uint ProductionUpdate_Vtable = 0x00C67EF4;

        // ---- heroes (dev\notes\re-heroes.md) ----
        public static uint Object_ExperienceTracker = 0x26C, Tracker_Level = 0x24;
        public static uint Fn_GainLevels = 0x0079DA0A;                     // thiscall(tracker, int levels, bool fx, bool) ret 0xC
        public static uint Template_KindOf = 0x108;                        // KindOf bit mask (dwords)
        public static uint Template_KindOfHeroDword = 0x110, KindOf_HeroBit = 0x04000000;
        public static uint TheInGameUI = 0x00DE4830, UI_SelectedList = 0x20, UI_SelectedCount = 0x568, Drawable_Object = 0xFC;
        public static uint TheDelayedLevelGrant = 0x00DE8AC0;

        // ---- radar events and enemy AI (dev\notes\re-ai-radar.md) ----
        public static uint TheGameClient = 0x00DE4388, GameClient_Frame = 0x10;      // client clock, 30 frames per second
        public static uint Radar_Events = 0x2C, Radar_CellScaleX = 0x24, Radar_CellScaleY = 0x28; public const int Radar_EventCount = 64, Radar_EventSize = 0x50;
        public const int RadarEvent_Marker = 6;                                      // "beacon" ping: silent, never used for attack alerts
        public static uint TheSkirmishAIManager = 0x00DE4938, SkirmishAIManager_Vtable = 0x00C13DFC, AIManager_AlliancesBegin = 0xA48, AIManager_AlliancesEnd = 0xA4C;
        public static uint Alliance_Enabled = 0x24;                                  // byte tested first by the alliance update
        public static uint Fn_GetRelationship = 0x006ACEAF;                          // thiscall(Player*, Player* other) -> 2 = allies, ret 4
        public const int Relationship_Allies = 2;
        public static uint Fn_SetUnitsIdle = 0x006AFBBC;                             // thiscall(Player*, bool idle), ret 4 (IDLE_ALL_UNITS)
        public static uint Fn_SetRelationship = 0x006AEA95;                          // thiscall(Player* self; Player* other, int relationship), ret 8: how "self" regards "other" (PLAYER_RELATES_PLAYER)
        public const int Relationship_Enemies = 0, Relationship_Neutral = 1;

        // ---- god mode (dev\notes\re-godmode.md) ----
        public static uint GodSite = 0x008C3FDA, GodEpilogue = 0x008C47CD;           // the engine's own "indestructible" test inside ActiveBody::attemptDamage
        public static readonly byte[] GodSiteOriginal = { 0x80, 0xBE, 0xB7, 0x00, 0x00, 0x00, 0x00 };
        public static uint Fn_ObjectKill = 0x00698EC3, KillGuardSite = 0x00910695;   // Object::kill and its call inside the knock-back weapon code
        public static uint Object_Body = 0x25C;

        // ---- weather (dev\notes\re-weather.md) ----
        public static uint TheGlobalWeatherSystem = 0x00DE772C, Fn_SetWeather = 0x0071A024;   // thiscall(type, burnDecay, durationFrames), ret 0xC
        public static uint TheFireLogicSystem = 0x00DE46A8, TheWeatherSetting = 0x00DE3B58, TheSnowManager = 0x00DE3B54;
        public static uint Fn_SnowUpdateIni = 0x004934D3, ShaderLod = 0x00D9B058;
        public static uint Fn_SnowStartSpell = 0x00492D51;                           // thiscall(TheSnowManager): starts the rain effect
        public static uint Fn_GetTexture = 0x00532875, Fn_TexRefAssign = 0x00441DE0, Fn_TexRelease = 0x00A329C0;   // cdecl(&slot, name, 0, 0) / thiscall(&dst; &src) / thiscall(tex)

        // ---- upgrades: the one call that computes how many frames a queued upgrade takes (UpgradeTemplate::calcTimeToBuild(Player*, Object*)) ----
        public static uint UpgradeTimeCallSite = 0x008A051B, Fn_UpgradeCalcTime = 0x0066F1A8;
        public static byte[] UpgradeTimeCallOriginal = { 0xE8, 0x88, 0xEC, 0xDC, 0xFF };

        // ---- main-thread hook site: "mov ecx,[TheScriptEngine]" at the top of the per-frame logic update ----
        public static uint HookSite = 0x0062E515;
        public static byte[] HookSiteOriginal = { 0x8B, 0x0D, 0xAC, 0x3B, 0xDE, 0x00 };
        public static byte[] HookSiteFollowing = { 0xE8, 0x32, 0x5C, 0xFD, 0xFF };
        public static uint HookReturn = 0x0062E51B;

        // ---- markers above units (health bars, level squares): sized by 1 / View::getZoom(); the two places that ask for it ----
        public static uint MarkerZoomSiteA = 0x0067920A, MarkerZoomSiteB = 0x0067814E;
        public static readonly byte[] MarkerZoomOriginal = { 0xFF, 0x90, 0x24, 0x01, 0x00, 0x00 };              // call dword [eax+0x124]  (ecx = the view)

        // ---- ALL FACTIONS (version 4.0.0, NOT YET GAMEPLAY TESTED; dev\notes\re-spawn.md and re-factions.md) ----
        // These are deliberately NOT part of the profile signatures above: the trainer must keep connecting to the game even if
        // one of them were wrong. Each "Sig_" is the start of the routine as it was analysed; it is compared with the running
        // game right before the first use, and the feature is refused if it differs (a null Sig = not supported in that game).
        // Creating an object: the same six engine calls, in the same order, that the games' own map-script action "create object"
        // makes (RotWK 0x007C595A, BFME2 0x007C4EC3), plus the ground height and - for buildings - the pathfinding entry that the
        // games' builder code adds (RotWK 0x00797934, BFME2 0x00795482).
        public static uint TheThingFactory = 0x00DE4A40, TheAI = 0x00DE4B40;     // [TheAI] + 0x10 = the pathfinder
        public static uint Fn_FindTemplate = 0x006D1305;        // thiscall(TheThingFactory; AsciiString* name) -> ThingTemplate* / 0, ret 4 (only reads the name)
        public static uint Fn_NewObject = 0x006D165E;           // thiscall(TheThingFactory; template, Team*, 16-byte status mask*, id 0) -> Object* / 0, ret 0x10
        public static uint Fn_SetOrientation = 0x0070C31E;      // thiscall(object; float angle), ret 4
        public static uint Fn_SetPosition = 0x0070C201;         // thiscall(object; Coord3D*), ret 4
        public static uint Fn_OnBuildComplete = 0x0068D252;     // thiscall(object), plain ret - fills a battalion with its soldiers
        public static uint Fn_AddToPathfindMap = 0x006E85E9;    // thiscall(pathfinder; object), ret 4 - makes a building solid for path finding
        public static uint Fn_GetGroundHeight = 0x00462355;     // (float x, float y, 0), ret 0xC, result on the FPU stack
        public static byte[] Sig_FindTemplate = { 0x55, 0x8B, 0xEC, 0x51, 0x51, 0xFF, 0x75, 0x08, 0x8D, 0x45, 0xF8, 0x50, 0x83, 0xC1, 0x14, 0xE8 };
        public static byte[] Sig_NewObject = { 0x55, 0x8B, 0xEC, 0x53, 0x56, 0x57, 0x8B, 0x7D, 0x08, 0x33, 0xDB, 0x3B, 0xFB, 0x8B, 0xF1, 0x75 };
        public static byte[] Sig_SetOrientation = { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x58, 0x53, 0xD9, 0x45, 0x08, 0x8B, 0xD9, 0xF3, 0x0F, 0x10, 0x43 };
        public static byte[] Sig_SetPosition = { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x40, 0x53, 0x8B, 0xD9, 0x8B, 0x43, 0x04, 0xF6, 0x80, 0x08, 0x01 };
        public static byte[] Sig_OnBuildComplete = { 0x56, 0x57, 0x8B, 0xF9, 0x8B, 0xB7, 0x4C, 0x02, 0x00, 0x00, 0xEB, 0x16, 0x8D, 0x48, 0x0C, 0x8B };
        public static byte[] Sig_AddToPathfindMap = { 0x6A, 0x00, 0x6A, 0x00, 0x6A, 0x01, 0xFF, 0x74, 0x24, 0x10, 0xE8, 0x85, 0xE5, 0x24, 0x00, 0xC2 };
        public static byte[] Sig_GetGroundHeight = { 0x8B, 0x44, 0x24, 0x0C, 0x85, 0xC0, 0x74, 0x19, 0x0F, 0x57, 0xC0, 0xF3, 0x0F, 0x11, 0x00, 0xF3 };
        public static uint Player_DefaultTeam = 0x30C, View_LookAtX = 0x0C, View_LookAtY = 0x10;
        public static uint Player_NoObjectsFlagA = 0x35A, Player_NoObjectsFlagB = 0x754;   // if either byte is set the engine gives new objects to the neutral player instead
        public static uint Template_KindOf0 = 0x108;            // first byte of the template's "kind of" bits: 0x80 = STRUCTURE
        // Faction of a player during a match = the PlayerTemplate its Player object points to (Player_Template, declared above:
        // which power tree the purchase screen shows, which heroes a fortress offers) + the id of the player's spell book object.
        public static uint Player_SpellBookId = 0x710;          // ObjectID of the spell book the player uses (0 = the engine looks for the first one it owns)
        public static uint Fn_GetSpellBookObject = 0x006AD0F8;  // never called by the trainer: its code contains Player_SpellBookId, so it proves that offset
        public static byte[] Sig_GetSpellBook = { 0x55, 0x8B, 0xEC, 0x51, 0x51, 0x56, 0x8B, 0xF1, 0x83, 0xBE, 0x10, 0x07, 0x00, 0x00, 0x00, 0x75 };
        // the list of all factions (PlayerTemplates): [store + Begin] .. [store + End], one element every PlayerTemplate_Size bytes
        public static uint ThePlayerTemplateStore = 0x00DE3B10, PlayerTemplateStore_Begin = 0x0C, PlayerTemplateStore_End = 0x10, PlayerTemplate_Size = 0x1DC;
        public static uint PlayerTemplate_Name = 0x18;          // "Side" (AsciiString): Men, Elves, Dwarves, Isengard, Mordor, Wild, Angmar ...
        public static uint PlayerTemplate_Playable = 0x151, PlayerTemplate_ScienceSetMP = 0x13C, PlayerTemplate_SpellBookMp = 0x1B8;
        public static uint PlayerTemplateStore_Site = 0x0064A27A;   // a piece of game code that walks the list: proves the pointer, both offsets and the size
        public static byte[] Sig_PlayerTemplateStore = { 0x8B, 0x0D, 0x10, 0x3B, 0xDE, 0x00, 0x8B, 0x41, 0x10, 0x2B, 0x41, 0x0C, 0xB9, 0xDC, 0x01, 0x00, 0x00, 0x99, 0xF7, 0xF9 };
        public static uint PlayerTemplate_Override = 0x04;      // a map can replace a faction's data: follow this pointer until it is 0
        // Another faction's power tree is found under its own name: ControlBar::findCommandSet(PlayerTemplate + ScienceSetMP).
        public static uint Fn_FindCommandSet = 0x0071EFA2;      // thiscall(TheControlBar; AsciiString* name) -> CommandSet* / 0, ret 4 (only reads the name)
        public static byte[] Sig_FindCommandSet = { 0x55, 0x8B, 0xEC, 0x51, 0x51, 0xFF, 0x75, 0x08, 0x8D, 0x45, 0xF8, 0x50, 0x83, 0xC1, 0x30, 0xE8 };
        // Making the game's spell bar read the spell book again: the bar remembers for which player it was filled
        // ([[[TheHud] + Hud_Impl] + Hud_SpellPanel] + SpellPanel_Player) and refills itself when that differs from the local
        // player; the control bar redraws when its "dirty" byte is set. Four pieces of game code prove these numbers.
        public static uint TheHud = 0x00DE4A70, Hud_Impl = 0x10, Hud_SpellPanel = 0x94, SpellPanel_Player = 0x28, ControlBar_Dirty = 0x28;
        public static uint SpellPanelSiteA = 0x00930FDE, SpellPanelSiteB = 0x006D5302, SpellPanelSiteC = 0x00611DE9, ControlBarDirtySite = 0x006AE2F0;
        public static byte[] Sig_SpellPanelA = { 0x3B, 0x7E, 0x28, 0x74, 0x55, 0x83, 0x66, 0x2C, 0x00, 0x85, 0xFF, 0x89, 0x7E, 0x28 };   // cmp player,[panel+0x28] ; je ; ... ; mov [panel+0x28],player
        public static byte[] Sig_SpellPanelB = { 0x8B, 0x4E, 0x10, 0x81, 0xC1, 0x94, 0x00, 0x00, 0x00, 0xE8 };                           // mov ecx,[hud+0x10] ; add ecx,0x94 ; call
        public static byte[] Sig_SpellPanelC = { 0x8B, 0x0D, 0x70, 0x4A, 0xDE, 0x00, 0x6A, 0x01, 0xE8 };                                 // mov ecx,[TheHud] ; push 1 ; call
        public static byte[] Sig_ControlBarDirty = { 0xA1, 0x44, 0x77, 0xDE, 0x00, 0xC6, 0x40, 0x28, 0x01 };                             // mov eax,[TheControlBar] ; mov byte [eax+0x28],1

        // ---- MY HEROES (hero list, 24 Nov 2026): selecting a hero exactly as the games' own hero bar does ----
        // The button code of that bar (RotWK 0x0092DB91, BFME II 0x00927122) looks the hero up by its id, lets a container
        // that holds him stand in for him, asks "is it selectable", clears the selection, sends the game the message
        // "create selected group" (0x3E9: TRUE, object id) and marks the drawable selected. The trainer's routine does the
        // same steps for any hero, not only the 16 the bar has room for. Like the ALL FACTIONS numbers these are checked
        // against the running game before first use (Sig_*) and are not part of the profile signatures.
        public static uint TheMessageStream = 0x00DE6398;
        public static uint Fn_FindObjectById = 0x00449681;      // Object* GameLogic::findObjectByID(id)         thiscall, ret 4
        public static uint Fn_IsSelectable = 0x0068DE58;        // bool Object::isSelectable()                   thiscall
        public static uint Fn_MsgAppendBool = 0x00711104;       // GameMessage::appendBooleanArgument(bool)       thiscall, ret 4
        public static uint Fn_MsgAppendObjectId = 0x0071111A;   // GameMessage::appendObjectIDArgument(id)        thiscall, ret 4
        public static uint Object_Drawable = 0x84, Object_ContainedIn = 0x27C;
        public static uint UI_DeselectAllSlot = 0x110, UI_SelectDrawableSlot = 0x108, MsgStream_AppendSlot = 0x48, Msg_CreateSelectedGroup = 0x3E9;
        public static uint SelectSiteA = 0x0092DDA2, SelectSiteB = 0x0092DDCF;
        public static byte[] Sig_FindObjectById = { 0x83, 0x7C, 0x24, 0x04, 0x00, 0x74, 0x14, 0x8D, 0x44, 0x24, 0x04, 0x50, 0x81, 0xC1, 0xB4, 0x00 };
        public static byte[] Sig_IsSelectable = { 0x56, 0x8B, 0xF1, 0x57, 0x8B, 0x7E, 0x04, 0xF6, 0x87, 0x0F, 0x01, 0x00, 0x00, 0x04, 0x74, 0x04 };
        public static byte[] Sig_MsgAppendBool = { 0xE8, 0xAA, 0xFF, 0xFF, 0xFF, 0x8A, 0x4C, 0x24, 0x04, 0x88, 0x48, 0x08, 0xC7, 0x40, 0x18, 0x02 };
        public static byte[] Sig_MsgAppendObjectId = { 0xE8, 0x94, 0xFF, 0xFF, 0xFF, 0x8B, 0x4C, 0x24, 0x04, 0x89, 0x48, 0x08, 0xC7, 0x40, 0x18, 0x03 };
        // mov eax,[ecx] ; call [eax+0x110] ; mov ecx,[TheMessageStream] ; mov eax,[ecx] ; push 0x3E9 ; call [eax+0x48]
        public static byte[] Sig_SelectSiteA = { 0x8B, 0x01, 0xFF, 0x90, 0x10, 0x01, 0x00, 0x00, 0x8B, 0x0D, 0x98, 0x63, 0xDE, 0x00, 0x8B, 0x01, 0x68, 0xE9, 0x03, 0x00, 0x00, 0xFF, 0x50, 0x48 };
        // mov ecx,[TheInGameUI] ; push drawable ; mov eax,[ecx] ; call [eax+0x108]
        public static byte[] Sig_SelectSiteB = { 0x8B, 0x0D, 0x30, 0x48, 0xDE, 0x00, 0xFF, 0x75, 0x08, 0x8B, 0x01, 0xFF, 0x90, 0x08, 0x01, 0x00, 0x00 };

        // ---- the call that draws one unit's health bar (thiscall, two stack arguments, ret 8) ----
        public static uint HealthBarCallSite = 0x00676514, Fn_DrawHealthBar = 0x00674445;
        public static byte[] HealthBarCallOriginal = { 0xE8, 0x2C, 0xDF, 0xFF, 0xFF };

        // ---- construction time: int ThingTemplate::calcTimeToBuild(Player*, Object* factory, int overrideFrames), thiscall, ret 12 ----
        public static uint Fn_CalcTimeToBuild = 0x0073C39E;
        public static readonly byte[] CalcTimeToBuildOriginal = { 0x55, 0x8B, 0xEC, 0x83, 0x7D, 0x10, 0xFF };   // push ebp ; mov ebp,esp ; cmp dword [ebp+0x10],-1

        // ------------------------------------------------------------------ profiles
        internal struct Sig { public uint Addr; public byte[] Bytes; public string What; public Sig(uint a, string w, params byte[] b) { Addr = a; Bytes = b; What = w; } }

        public static readonly GameProfile Rotwk201, Bfme2_106;
        public static readonly GameProfile[] Profiles;
        public static GameProfile Current { get; private set; }
        static readonly Dictionary<string, object> Defaults = new Dictionary<string, object>();

        static Offsets()
        {
            // remember the values written above (the RotWK 2.01 engine) so a profile switch can always start from them
            foreach (FieldInfo f in typeof(Offsets).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (!f.IsLiteral && !f.IsInitOnly) Defaults[f.Name] = f.GetValue(null);

            Rotwk201 = new GameProfile
            {
                Id = "rotwk-2.01", Game = GameId.Rotwk, GameName = "The Rise of the Witch-king", EngineBuild = "2.01.2614.37001",
                Notes = "Executable used by Patch 2.01 and the community Patch 2.02 (game.dat / delay-fix lotrbfme2ep1.exe).",
                Signatures = new[]
                {
                    new Sig(0x007B19BF, "handicap lookup", 0x8B, 0x44, 0x24, 0x08, 0x0F, 0xB6, 0x80, 0x08, 0x01, 0x00, 0x00),
                    new Sig(0x006A7B9F, "command point limit", 0x51, 0x53, 0x56, 0x8B, 0xF1, 0x8B, 0x5E, 0x0C),
                    new Sig(0x006D57F3, "resource display", 0x8D, 0x83, 0x90, 0x00, 0x00, 0x00),
                    new Sig(0x00B4D940, "map reveal", 0x8B, 0x49, 0x10, 0xE9, 0x88, 0x1C, 0x00, 0x00),
                    new Sig(0x00B4D960, "map reveal undo", 0x8B, 0x49, 0x10, 0xE9, 0x48, 0x40, 0x00, 0x00),
                    new Sig(0x006D98FA, "radar event", 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x20, 0x33, 0xD2),
                    new Sig(0x0062E515, "logic update", 0x8B, 0x0D, 0xAC, 0x3B, 0xDE, 0x00, 0xE8, 0x32, 0x5C, 0xFD, 0xFF) { },
                    new Sig(0x0048B816, "camera far plane", 0xD9, 0x80, 0x50, 0x09, 0x00, 0x00, 0xD8, 0x0D, 0x30, 0xD7, 0xBD, 0x00),
                    new Sig(0x005013C6, "map camera limits", 0xD9, 0x5E, 0x08),
                    new Sig(0x006AE3A7, "power tree", 0x56, 0xFF, 0x74, 0x24, 0x08, 0x8B, 0xF1, 0x8B, 0x0D, 0x20, 0x3B, 0xDE, 0x00),
                    new Sig(0x0071F933, "power tree list", 0x8B, 0x44, 0x24, 0x04, 0x56, 0x8B, 0x70, 0x34, 0x85, 0xF6),
                    new Sig(0x0079DA0A, "hero levels", 0x55, 0x8B, 0xEC, 0x51, 0x53),
                    new Sig(0x008A051B, "upgrade timer", 0xE8),
                    new Sig(0x008EDDF6, "AI alliance update", 0x53, 0x56, 0x8B, 0xF1, 0x33, 0xDB, 0x38, 0x5E, 0x24),
                    new Sig(0x006ACEAF, "player relationship", 0x55, 0x8B, 0xEC, 0x51, 0x51),
                    new Sig(0x006AFBBC, "idle units", 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x2C),
                    new Sig(0x009F9F1A, "client clock", 0x8B, 0x41, 0x10, 0xC3),
                    new Sig(0x0066F1A8, "upgrade time", 0x56, 0x8B, 0xF1, 0x8B, 0x4C, 0x24, 0x08),
                    new Sig(0x008C3FDA, "damage check", 0x80, 0xBE, 0xB7, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x85),
                    new Sig(0x00698EC3, "kill routine", 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x7C, 0x56, 0x8B, 0xF1),
                    new Sig(0x00910695, "impact kill", 0xE8, 0x29, 0x88, 0xD8, 0xFF),
                    new Sig(0x0071A024, "weather", 0x8B, 0x44, 0x24, 0x04, 0x39, 0x41, 0x10, 0x74, 0x1F, 0x89, 0x41, 0x10),
                    new Sig(0x004934D3, "snow settings", 0xB8, 0xAE, 0x42, 0xB7, 0x00),
                    new Sig(0x00492D51, "rain effect", 0x56, 0x8B, 0xF1, 0xC7, 0x86, 0xAC, 0x00, 0x00),
                    new Sig(0x00532875, "texture lookup", 0xB8, 0x0F, 0x9B, 0xB7, 0x00),
                    new Sig(0x00441DE0, "texture assign", 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x0C, 0x8B, 0x07),
                    new Sig(0x00A329C0, "texture release", 0xF7, 0x41, 0x04, 0xFF, 0xFF, 0x00, 0x00),
                    new Sig(0x0073C39E, "build time", 0x55, 0x8B, 0xEC, 0x83, 0x7D, 0x10, 0xFF),
                    new Sig(0x006AEA95, "player relationship setter", 0x55, 0x8B, 0xEC, 0x8B, 0x45, 0x08, 0x85, 0xC0, 0x74, 0x1D, 0x8B, 0x40, 0x54),
                    new Sig(0x0067920A, "marker size", 0xFF, 0x90, 0x24, 0x01, 0x00, 0x00),
                    new Sig(0x0067814E, "marker size", 0xFF, 0x90, 0x24, 0x01, 0x00, 0x00),
                    new Sig(0x00676514, "health bar draw", 0xE8, 0x2C, 0xDF, 0xFF, 0xFF),
                }
            };

            Bfme2_106 = new GameProfile
            {
                Id = "bfme2-1.06", Game = GameId.Bfme2, GameName = "The Battle for Middle-earth II", EngineBuild = "1.06.2429.30210",
                Notes = "Executable of the official Patch 1.06 (game.dat).",
                Signatures = new[]
                {
                    new Sig(0x007B0EA2, "handicap lookup", 0x8B, 0x44, 0x24, 0x08, 0x0F, 0xB6, 0x80, 0x08, 0x01, 0x00, 0x00),
                    new Sig(0x006A7461, "command point limit", 0x51, 0x53, 0x56, 0x8B, 0xF1, 0x8B, 0x5E, 0x0C),
                    new Sig(0x006D47BF, "resource display", 0x8D, 0x83, 0x90, 0x00, 0x00, 0x00),
                    new Sig(0x00B39790, "map reveal", 0x8B, 0x49, 0x10, 0xE9, 0x78, 0x1C, 0x00, 0x00),
                    new Sig(0x00B397B0, "map reveal undo", 0x8B, 0x49, 0x10, 0xE9, 0x58, 0x40, 0x00, 0x00),
                    new Sig(0x006D88A4, "radar event", 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x20, 0x33, 0xD2),
                    new Sig(0x00645587, "logic update", 0x8B, 0x0D, 0x6C, 0xE1, 0xDF, 0x00, 0xE8, 0x3D, 0xF2, 0xFB, 0xFF),
                    new Sig(0x0048BED0, "camera far plane", 0xD9, 0x80, 0x50, 0x09, 0x00, 0x00, 0xD8, 0x0D, 0x08, 0x78, 0xBC, 0x00),
                    new Sig(0x006AD85E, "power tree", 0x56, 0xFF, 0x74, 0x24, 0x08, 0x8B, 0xF1, 0x8B, 0x0D, 0xE0, 0xE0, 0xDF, 0x00),
                    new Sig(0x0071DF89, "power tree list", 0x8B, 0x44, 0x24, 0x04, 0x56, 0x8B, 0x70, 0x34, 0x85, 0xF6),
                    new Sig(0x0079B4EC, "hero levels", 0x55, 0x8B, 0xEC, 0x51, 0x53),
                    new Sig(0x0089CD9C, "upgrade timer", 0xE8),
                    new Sig(0x008E9710, "AI alliance update", 0x53, 0x56, 0x8B, 0xF1, 0x33, 0xDB, 0x38, 0x5E, 0x24),
                    new Sig(0x006AC3E0, "player relationship", 0x55, 0x8B, 0xEC, 0x51, 0x51),
                    new Sig(0x006AF0C5, "idle units", 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x2C),
                    new Sig(0x0066EE30, "upgrade time", 0x56, 0x8B, 0xF1, 0x8B, 0x4C, 0x24, 0x08),
                    new Sig(0x0048A2EF, "camera move", 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x18, 0x53, 0x56),
                    new Sig(0x008BFE3E, "damage check", 0x80, 0xBE, 0xB7, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x85),
                    new Sig(0x006984D4, "kill routine", 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x7C, 0x56, 0x8B, 0xF1),
                    new Sig(0x0090A24F, "impact kill", 0xE8, 0x80, 0xE2, 0xD8, 0xFF),
                    new Sig(0x007186EE, "weather", 0x8B, 0x44, 0x24, 0x04, 0x39, 0x41, 0x10, 0x74, 0x1F, 0x89, 0x41, 0x10),
                    new Sig(0x00493ACB, "snow settings", 0xB8, 0x9E, 0x00, 0xB6, 0x00),
                    new Sig(0x00493349, "rain effect", 0x56, 0x8B, 0xF1, 0xC7, 0x86, 0xAC, 0x00, 0x00),
                    new Sig(0x00532D89, "texture lookup", 0xB8, 0xEF, 0x58, 0xB6, 0x00),
                    new Sig(0x004424D0, "texture assign", 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x0C, 0x8B, 0x07),
                    new Sig(0x00A1ED10, "texture release", 0xF7, 0x41, 0x04, 0xFF, 0xFF, 0x00, 0x00),
                    new Sig(0x0073AA1F, "build time", 0x55, 0x8B, 0xEC, 0x83, 0x7D, 0x10, 0xFF),
                    new Sig(0x006ADF9C, "player relationship setter", 0x55, 0x8B, 0xEC, 0x8B, 0x45, 0x08, 0x85, 0xC0, 0x74, 0x1D, 0x8B, 0x40, 0x54),
                    new Sig(0x00678EDF, "marker size", 0xFF, 0x90, 0x24, 0x01, 0x00, 0x00),
                    new Sig(0x00677E23, "marker size", 0xFF, 0x90, 0x24, 0x01, 0x00, 0x00),
                    new Sig(0x006761E9, "health bar draw", 0xE8, 0x31, 0xDF, 0xFF, 0xFF),
                }
            };
            var b = Bfme2_106.Overrides;
            b["CodeEnd"] = 0x00BBA000u;
            // globals
            b["ThePlayerList"] = 0x00DFEEE8u; b["TheGameLogic"] = 0x00DFE78Cu; b["ThePartitionManager"] = 0x00DFE74Cu; b["TheRadar"] = 0x00DFF070u;
            b["TheTacticalView"] = 0x00DFEA3Cu; b["TheScriptEngine"] = 0x00DFE16Cu; b["TheGlobalData"] = 0x00DFE758u; b["TheControlBar"] = 0x00E01CFCu;
            b["TheInGameUI"] = 0x00DFEDF0u; b["TheDelayedLevelGrant"] = 0x00E03144u; b["TheGameClient"] = 0x00DFE77Cu; b["TheSkirmishAIManager"] = 0x00DFEEF8u;
            // class tables
            b["Player_Vtable"] = 0x00BFDF3Cu; b["Object_Vtable"] = 0x00BFC300u; b["CameraLimits_Vtable"] = 0x00BCF670u; b["View_Vtable"] = 0x00BC7568u;
            b["SkirmishAIManager_Vtable"] = 0x00BFDB94u; b["SpecialPowerVtblA"] = 0x00BEFCF8u; b["SpecialPowerVtblB1"] = 0x00C4E998u; b["SpecialPowerVtblB2"] = 0x00C5D9C0u;
            b["ProductionUpdate_Vtable"] = 0x00C515BCu;
            // functions
            b["Fn_RevealMapPermanently"] = 0x00B39790u; b["Fn_UndoRevealMapPermanently"] = 0x00B397B0u; b["Fn_RadarCreateEvent"] = 0x006D88A4u; b["Fn_ViewMoveCameraTo"] = 0x0048A2EFu;
            b["Fn_GetPurchaseScienceCommandSet"] = 0x0071DF89u; b["Fn_GrantScience"] = 0x006AD85Eu; b["Fn_GainLevels"] = 0x0079B4ECu; b["Fn_UpgradeCalcTime"] = 0x0066EE30u;
            b["Fn_GetRelationship"] = 0x006AC3E0u; b["Fn_SetUnitsIdle"] = 0x006AF0C5u; b["Fn_SetRelationship"] = 0x006ADF9Cu;
            // code sites
            b["HookSite"] = 0x00645587u; b["HookReturn"] = 0x0064558Du;
            b["HookSiteOriginal"] = new byte[] { 0x8B, 0x0D, 0x6C, 0xE1, 0xDF, 0x00 }; b["HookSiteFollowing"] = new byte[] { 0xE8, 0x3D, 0xF2, 0xFB, 0xFF };
            b["UpgradeTimeCallSite"] = 0x0089CD9Cu; b["UpgradeTimeCallOriginal"] = new byte[] { 0xE8, 0x8F, 0x20, 0xDD, 0xFF };
            // structure members that sit at a different place in the older engine
            b["Object_Modules"] = 0x244u; b["Object_Team"] = 0x304u; b["Object_ExperienceTracker"] = 0x264u; b["Object_FireWeaponLockout"] = 0x458u;
            b["Player_SciencesBegin"] = 0x2F0u; b["Player_SciencesEnd"] = 0x2F4u; b["Player_SharedTimers"] = 0x704u;
            b["AIManager_AlliancesBegin"] = 0x914u; b["AIManager_AlliancesEnd"] = 0x918u;
            // god mode and weather
            b["GodSite"] = 0x008BFE3Eu; b["GodEpilogue"] = 0x008C0631u; b["Fn_ObjectKill"] = 0x006984D4u; b["KillGuardSite"] = 0x0090A24Fu; b["Object_Body"] = 0x254u;
            b["TheGlobalWeatherSystem"] = 0x00E01CE4u; b["Fn_SetWeather"] = 0x007186EEu; b["TheFireLogicSystem"] = 0x00DFEC68u; b["TheWeatherSetting"] = 0x00DFE118u;
            b["Fn_CalcTimeToBuild"] = 0x0073AA1Fu; b["MarkerZoomSiteA"] = 0x00678EDFu; b["MarkerZoomSiteB"] = 0x00677E23u;
            b["HealthBarCallSite"] = 0x006761E9u; b["Fn_DrawHealthBar"] = 0x0067411Fu; b["HealthBarCallOriginal"] = new byte[] { 0xE8, 0x31, 0xDF, 0xFF, 0xFF };
            // ALL FACTIONS (4.0.0, not yet gameplay tested): creating objects
            b["TheThingFactory"] = 0x00DFF000u; b["TheAI"] = 0x00DFF0F8u;
            b["Fn_FindTemplate"] = 0x006D06CAu; b["Fn_NewObject"] = 0x006D0A23u; b["Fn_SetOrientation"] = 0x0070AB9Du; b["Fn_SetPosition"] = 0x0070AA80u;
            b["Fn_OnBuildComplete"] = 0x0068CBFDu; b["Fn_AddToPathfindMap"] = 0x006E7178u; b["Fn_GetGroundHeight"] = 0x00462C45u;
            b["Sig_OnBuildComplete"] = new byte[] { 0x56, 0x57, 0x8B, 0xF9, 0x8B, 0xB7, 0x44, 0x02, 0x00, 0x00, 0xEB, 0x16, 0x8D, 0x48, 0x0C, 0x8B };
            b["Sig_AddToPathfindMap"] = new byte[] { 0x6A, 0x00, 0x6A, 0x00, 0x6A, 0x01, 0xFF, 0x74, 0x24, 0x10, 0xE8, 0x8B, 0x90, 0x24, 0x00, 0xC2 };
            b["Player_DefaultTeam"] = 0x2ECu; b["Player_NoObjectsFlagA"] = 0x33Au; b["Player_NoObjectsFlagB"] = 0x734u;
            // ALL FACTIONS: faction and spell book (the PlayerTemplate layout itself is the same in both games)
            b["Player_SpellBookId"] = 0x6F0u; b["Fn_GetSpellBookObject"] = 0x006AC629u;
            b["Sig_GetSpellBook"] = new byte[] { 0x55, 0x8B, 0xEC, 0x51, 0x51, 0x56, 0x8B, 0xF1, 0x83, 0xBE, 0xF0, 0x06, 0x00, 0x00, 0x00, 0x75 };
            b["ThePlayerTemplateStore"] = 0x00DFE0D0u; b["PlayerTemplateStore_Site"] = 0x0064A596u;
            b["Sig_PlayerTemplateStore"] = new byte[] { 0x8B, 0x0D, 0xD0, 0xE0, 0xDF, 0x00, 0x8B, 0x41, 0x10, 0x2B, 0x41, 0x0C, 0xB9, 0xDC, 0x01, 0x00, 0x00, 0x99, 0xF7, 0xF9 };
            b["Fn_FindCommandSet"] = 0x0071D5F8u; b["TheHud"] = 0x00DFF028u;
            b["SpellPanelSiteA"] = 0x0092A583u; b["SpellPanelSiteB"] = 0x006D42ACu; b["SpellPanelSiteC"] = 0x00611FFCu; b["ControlBarDirtySite"] = 0x006AD7AAu;   // 4.0.1: was 0x006AD7A5 = the 6-byte jne in front of it, so the check refused every powers request in BFME II
            b["Sig_SpellPanelC"] = new byte[] { 0x8B, 0x0D, 0x28, 0xF0, 0xDF, 0x00, 0x6A, 0x01, 0xE8 };
            b["Sig_ControlBarDirty"] = new byte[] { 0xA1, 0xFC, 0x1C, 0xE0, 0x00, 0xC6, 0x40, 0x28, 0x01 };
            // MY HEROES: the same code in BFME II (hero bar button code at 0x00927122)
            b["TheMessageStream"] = 0x00E00950u; b["Fn_FindObjectById"] = 0x00449DC5u; b["Fn_IsSelectable"] = 0x0068D7FDu;
            b["Fn_MsgAppendBool"] = 0x0070F963u; b["Fn_MsgAppendObjectId"] = 0x0070F979u; b["Object_ContainedIn"] = 0x274u;
            b["SelectSiteA"] = 0x00927333u; b["SelectSiteB"] = 0x00927360u;
            b["Sig_MsgAppendBool"] = new byte[] { 0xE8, 0x97, 0xFF, 0xFF, 0xFF, 0x8A, 0x4C, 0x24, 0x04, 0x88, 0x48, 0x08, 0xC7, 0x40, 0x18, 0x02 };
            b["Sig_MsgAppendObjectId"] = new byte[] { 0xE8, 0x81, 0xFF, 0xFF, 0xFF, 0x8B, 0x4C, 0x24, 0x04, 0x89, 0x48, 0x08, 0xC7, 0x40, 0x18, 0x03 };
            b["Sig_SelectSiteA"] = new byte[] { 0x8B, 0x01, 0xFF, 0x90, 0x10, 0x01, 0x00, 0x00, 0x8B, 0x0D, 0x50, 0x09, 0xE0, 0x00, 0x8B, 0x01, 0x68, 0xE9, 0x03, 0x00, 0x00, 0xFF, 0x50, 0x48 };
            b["Sig_SelectSiteB"] = new byte[] { 0x8B, 0x0D, 0xF0, 0xED, 0xDF, 0x00, 0xFF, 0x75, 0x08, 0x8B, 0x01, 0xFF, 0x90, 0x08, 0x01, 0x00, 0x00 };
            b["TheSnowManager"] = 0x00DFE114u; b["Fn_SnowUpdateIni"] = 0x00493ACBu; b["ShaderLod"] = 0x00DB5FA0u;
            b["Fn_SnowStartSpell"] = 0x00493349u; b["Fn_GetTexture"] = 0x00532D89u; b["Fn_TexRefAssign"] = 0x004424D0u; b["Fn_TexRelease"] = 0x00A1ED10u;

            Profiles = new[] { Rotwk201, Bfme2_106 };
            Current = Rotwk201;
        }

        /// <summary>Makes a profile's addresses the active ones.</summary>
        public static void Use(GameProfile p)
        {
            Type t = typeof(Offsets);
            foreach (KeyValuePair<string, object> kv in Defaults) t.GetField(kv.Key).SetValue(null, kv.Value);
            foreach (KeyValuePair<string, object> kv in p.Overrides)
            {
                FieldInfo f = t.GetField(kv.Key); if (f == null) throw new InvalidOperationException("Unknown offset " + kv.Key);
                f.SetValue(null, kv.Value);
            }
            Current = p;
        }

        /// <summary>Checks one profile's code signatures against the attached process. Returns null when all match, otherwise the reason.</summary>
        public static string Verify(GameMemory m, GameProfile p)
        {
            byte[] hdr = m.ReadBytes(ImageBase, 0x400);
            if (hdr == null || hdr[0] != 'M' || hdr[1] != 'Z') return "The game image could not be read at its expected base address.";
            int pe = BitConverter.ToInt32(hdr, 0x3C);
            if (pe <= 0 || pe > 0x300 || BitConverter.ToUInt32(hdr, pe) != 0x00004550) return "Unexpected executable header.";
            foreach (Sig s in p.Signatures)
            {
                byte[] b = m.ReadBytes(s.Addr, s.Bytes.Length);
                if (b == null) return "Could not read the " + s.What + " code.";
                for (int i = 0; i < b.Length; i++)
                    if (b[i] != s.Bytes[i])
                    {
                        // our own patches are allowed at the two detour sites (a trainer session may already be attached)
                        if ((b[0] == 0xE9 || b[0] == 0xE8) && (s.What == "logic update" || s.What == "upgrade timer" || s.What == "damage check" || s.What == "impact kill" || s.What == "build time" || s.What == "marker size" || s.What == "health bar draw")) break;
                        return "the " + s.What + " code differs from engine " + p.EngineBuild;
                    }
            }
            return null;
        }

        /// <summary>Finds the profile whose signatures all match the attached process (null if none does).</summary>
        public static GameProfile Detect(GameMemory m, out string reason)
        {
            reason = null;
            foreach (GameProfile p in Profiles) { string r = Verify(m, p); if (r == null) return p; if (reason == null || p == Current) reason = r; }
            return null;
        }
    }
}
