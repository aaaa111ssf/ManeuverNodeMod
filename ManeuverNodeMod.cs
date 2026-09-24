using System;
using System.Collections.Generic;
using HarmonyLib;
using ModLoader;
using UnityEngine;

namespace SFSManeuverNode
{
    /// <summary>模组入口。ModLoader 在 Mods/&lt;文件夹名&gt;/&lt;文件夹名&gt;.dll 里找 Mod 派生类。</summary>
    public class ManeuverNodeMod : Mod
    {
        internal static ManeuverNodeMod main;
        internal static Harmony harmony;
        internal static NodeManager manager;

        public override string ModNameID => "SFSManeuverNode";

        public override string DisplayName => "Maneuver Node";

        public override string Author => "A Future star";

        public override string MinimumGameVersionNecessary => "1.6.0.0";

        public override string ModVersion => "beta 0.0.0";

        public override string Description => Lang.T(
            "KSP / Juno-style maneuver nodes: place nodes on the map, drag the four handles to tune prograde & radial dV, drag the center ring to slide the node along its orbit, live predicted trajectory with encounters, burn countdown, warp-to-node, auto-align and auto-execute.");

#if UITOOLS
        public override Dictionary<string, string> Dependencies => new Dictionary<string, string>
        {
            { "UITools", "1.1.6" }
        };
#endif

        public override Action LoadKeybindings => Keybindings.Setup;

        public override void Early_Load()
        {
            main = this;
            try
            {
                harmony = new Harmony("SFSManeuverNode");
                harmony.PatchAll();
                Debug.Log("[ManeuverNode] Harmony patches applied");
            }
            catch (Exception e)
            {
                Debug.LogError("[ManeuverNode] Harmony patch failed: " + e);
            }
        }

        public override void Load()
        {
            try
            {
                Lang.Invalidate();
                GameObject go = new GameObject("ManeuverNode.Manager");
                UnityEngine.Object.DontDestroyOnLoad(go);
                manager = go.AddComponent<NodeManager>();
                Debug.Log("[ManeuverNode] Loaded");
            }
            catch (Exception e)
            {
                Debug.LogError("[ManeuverNode] Load failed: " + e);
            }
        }
    }
}
