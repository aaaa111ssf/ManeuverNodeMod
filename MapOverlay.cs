using HarmonyLib;
using SFS.World.Maps;

namespace SFSManeuverNode
{
    /// <summary>
    /// 在游戏自己把地图画完之后，叠加本模组的预测轨道、节点标记与远/近点标签。
    /// MapManager.DrawMap 是 private，Harmony 用名字匹配可以正常打上。
    /// </summary>
    [HarmonyPatch(typeof(MapManager), "DrawMap")]
    internal static class Patch_MapManager_DrawMap
    {
        private static void Postfix()
        {
            NodeManager manager = NodeManager.main;
            if (manager != null)
            {
                manager.DrawOverlay();
            }
        }
    }
}
