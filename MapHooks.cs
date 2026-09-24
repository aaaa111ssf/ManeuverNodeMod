using HarmonyLib;
using SFS.World.Maps;

namespace SFSManeuverNode
{
    /// <summary>
    /// MapView.OnDrag 把「光标位移 = 地图位移」，抓手柄期间跳过它，位移留给 NodeGizmo 改 ΔV。
    /// </summary>
    [HarmonyPatch(typeof(MapView), "OnDrag")]
    internal static class PatchMapViewOnDrag
    {
        private static bool Prefix()
        {
            return !NodeManager.HandleDragActive;
        }
    }

    /// <summary>
    /// 把 TimewarpTo.TrySelect 选中点（私有字段 selected）交给 NodeManager，
    /// 「Create node」按钮据此在玩家点到的位置建节点。
    /// </summary>
    [HarmonyPatch(typeof(TimewarpTo), "TrySelect")]
    internal static class PatchTimewarpToTrySelect
    {
        private static void Postfix(TimewarpTo __instance, bool __result)
        {
            NodeManager.CaptureMapSelection(__instance, __result);
        }
    }
}
