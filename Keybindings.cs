using System;
using ModLoader;
using SFS.Input;
using UnityEngine;

namespace SFSManeuverNode
{
    /// <summary>
    /// 可在游戏「按键设置」里改的快捷键。若按键系统尚未就绪导致注册失败，
    /// NodeManager 会退回到写死的 F6 / F7 / F8。
    /// </summary>
    internal class Keybindings : ModKeybindings
    {
        internal static Keybindings main;

        public KeybindingsPC.Key toggleWindow = KeyCode.F7;

        public KeybindingsPC.Key togglePlaceMode = KeyCode.F8;

        public KeybindingsPC.Key warpToNode = KeyCode.F6;

        internal static void Setup()
        {
            try
            {
                main = SetupKeybindings<Keybindings>(ManeuverNodeMod.main);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ManeuverNode] keybinding setup failed, falling back to F6/F7/F8: " + e.Message);
                main = null;
            }
        }

        public override void CreateUI()
        {
            CreateUI_Text("Maneuver Node");
            CreateUI_Keybinding(toggleWindow, (KeybindingsPC.Key)KeyCode.F7, "Show / hide maneuver node window");
            CreateUI_Keybinding(togglePlaceMode, (KeybindingsPC.Key)KeyCode.F8, "Place node mode");
            CreateUI_Keybinding(warpToNode, (KeybindingsPC.Key)KeyCode.F6, "Time warp to selected node");
        }
    }
}
