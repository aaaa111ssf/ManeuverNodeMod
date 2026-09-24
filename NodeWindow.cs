using System;
using SFS.UI;
using SFS.UI.ModGUI;
using SFS.World;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using Button = SFS.UI.ModGUI.Button;
using Type = SFS.UI.ModGUI.Type;
#if UITOOLS
using UITools;
#endif

namespace SFSManeuverNode
{
    /// <summary>
    /// 机动节点参数窗口。文案全部为英文：游戏字体缺 CJK 字形，中文会变成方块（见 Lang.cs）。
    /// </summary>
    internal class NodeWindow
    {
        const int Width = 400;
        const int Height = 738;
        const int ContentWidth = 376;
        const int WindowId = 190411;

        readonly NodeManager owner;

        GameObject holderObject;
        Window window;
        Transform holder;

        bool refreshing;

        Button btnPlace, btnDelete, btnClear;
        Button btnPrev, btnNext;
        Label lblNode;
        NumberField numPrograde, numRadial;
        Label lblOrbit, lblBurn, lblStatus, lblHint;
        Button btnAlign, btnAutoBurn, btnExecute;

        internal NodeWindow(NodeManager owner)
        {
            this.owner = owner;
        }

        internal bool Created => window != null && window.gameObject != null;

        internal bool Visible
        {
            get => Created && window.gameObject.activeSelf;
            set
            {
                if (Created)
                {
                    window.gameObject.SetActive(value);
                }
            }
        }

        // ------------------------------------------------------------------
        // 构建
        // ------------------------------------------------------------------

        internal void Create()
        {
            holderObject = Builder.CreateHolder(Builder.SceneToAttach.CurrentScene, "ManeuverNode.UI");
            EnsureCanvas(holderObject);

            window = Builder.CreateWindow(
                holderObject.transform, WindowId, Width, Height, -620, 0,
                draggable: true, savePosition: true, opacity: 1f,
                titleText: "Maneuver Nodes");

            window.CreateLayoutGroup(Type.Vertical, TextAnchor.UpperCenter, 6f, new RectOffset(10, 10, 6, 10));
            holder = window.ChildrenHolder;

            BuildRows();
            Refresh();
        }

        internal void Destroy()
        {
            if (window != null && window.gameObject != null)
            {
                UnityEngine.Object.Destroy(window.gameObject);
            }
            if (holderObject != null)
            {
                UnityEngine.Object.Destroy(holderObject);
            }
            window = null;
            holderObject = null;
            holder = null;
        }

        void BuildRows()
        {
            // ---- 节点管理 ----
            Container row = Row(346, 32);
            btnPlace = AddButton(row, 110, 32, "Place node", TogglePlaceMode);
            btnDelete = AddButton(row, 110, 32, "Delete", owner.DeleteSelected);
            btnClear = AddButton(row, 110, 32, "Clear all", owner.ClearAllNodes);

            Builder.CreateSeparator(holder, ContentWidth);

            // ---- 节点切换 ----
            row = Row(300, 32);
            btnPrev = AddButton(row, 70, 32, "Prev", () => owner.SelectOffset(-1));
            lblNode = AddLabel(row, 140, 32, "--", TextAlignmentOptions.Center);
            btnNext = AddButton(row, 70, 32, "Next", () => owner.SelectOffset(1));

            // ---- ΔV 输入 ----
            row = Row(348, 34);
            AddLabel(row, 148, 34, "Prograde dV (m/s)", TextAlignmentOptions.Left);
            numPrograde = new NumberField(row, 190, 30, 0f, 1f, OnProgradeChanged);

            row = Row(348, 34);
            AddLabel(row, 148, 34, "Radial dV (m/s)", TextAlignmentOptions.Left);
            numRadial = new NumberField(row, 190, 30, 0f, 1f, OnRadialChanged);

            Builder.CreateSeparator(holder, ContentWidth);

            // ---- 预测信息 ----
            lblOrbit = AddLabelBlock(66);
            lblBurn = AddLabelBlock(66);

            Builder.CreateSeparator(holder, ContentWidth);

            // ---- 点火时刻微调 ----
            row = Row(360, 30);
            AddButton(row, 84, 30, "-10 s", () => owner.ShiftSelectedTime(-10.0));
            AddButton(row, 84, 30, "-1 s", () => owner.ShiftSelectedTime(-1.0));
            AddButton(row, 84, 30, "+1 s", () => owner.ShiftSelectedTime(1.0));
            AddButton(row, 84, 30, "+10 s", () => owner.ShiftSelectedTime(10.0));

            row = Row(360, 30);
            AddButton(row, 176, 30, "< 1/4 orbit", () => owner.ShiftSelectedPeriod(-0.25));
            AddButton(row, 176, 30, "1/4 orbit >", () => owner.ShiftSelectedPeriod(0.25));

            Builder.CreateSeparator(holder, ContentWidth);

            // ---- 自动执行 ----
            row = Row(370, 36);
            AddButton(row, 370, 36, "Warp to node", () => owner.Control.ToggleWarp());

            row = Row(360, 36);
            btnAlign = AddButton(row, 176, 36, "Auto-align", () => owner.Control.ToggleAlign());
            btnAutoBurn = AddButton(row, 176, 36, "Auto-burn", () => owner.Control.ToggleAutoBurn());

            row = Row(360, 36);
            btnExecute = AddButton(row, 176, 36, "Execute all", () => owner.Control.ToggleExecute());
            AddButton(row, 176, 36, "Abort", () => owner.Control.Abort());

            Builder.CreateSeparator(holder, ContentWidth);

            // ---- 状态 / 提示 ----
            lblStatus = AddLabelBlock(58);
            lblHint = AddLabelBlock(76);
            lblHint.Text =
                "Click a node on the map to edit it; click anywhere\n" +
                "else to leave. Drag the 4 handles to tune dV, drag\n" +
                "the center dot to slide the node along its orbit,\n" +
                "or click the red X on the gizmo to DELETE the node.\n" +
                "Shift = fine, Ctrl = coarse, Ctrl+Z/Y undo, F7 hide.";
        }

        // ------------------------------------------------------------------
        // 刷新
        // ------------------------------------------------------------------

        internal void Refresh()
        {
            if (!Created)
            {
                return;
            }
            refreshing = true;
            try
            {
                BurnControl control = owner.Control;
                ManeuverNodeData node = owner.Selected;
                int count = owner.Nodes.Count;

                SetSelected(btnPlace, owner.PlaceMode);
                SetSelected(btnAlign, control.HoldAttitude);
                SetSelected(btnAutoBurn, control.AutoBurn || control.Burning);
                SetSelected(btnExecute, control.Executing);

                btnDelete.Active = node != null;
                btnClear.Active = count > 0;
                btnPrev.Active = count > 1;
                btnNext.Active = count > 1;

                RefreshStatus(control);

                if (node == null)
                {
                    lblNode.Text = count == 0 ? "(no node)" : "(none selected)";
                    numPrograde.Value = 0f;
                    numRadial.Value = 0f;
                    lblOrbit.Text = "Total dV: --\nPredicted Ap: --\nPredicted Pe: --";
                    lblBurn.Text = "Burn at: --\nBurn time: --\nRemaining dV: --";
                    return;
                }

                lblNode.Text = string.Format("Node {0} / {1}", node.index, count);
                numPrograde.Value = (float)node.progradeDV;
                numRadial.Value = (float)node.radialDV;

                double now = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;
                double countdown = node.burnTime - now;
                double mass = BurnControl.GetMass(owner.Rocket);
                double duration = control.Propulsion.BurnDuration(node.TotalDV, mass);

                string totalDv = Lang.Velocity(node.TotalDV);

                // 远点 / 近点：取预测轨迹里最后一条闭合轨道
                Orbit closed = NodeMath.LastClosedOrbit(node.predicted);
                string ap = "--";
                string pe = "--";
                if (closed != null && closed.Planet != null && closed.ecc < 1.0)
                {
                    ap = Lang.Height(closed.apoapsis - closed.Planet.Radius);
                    pe = Lang.Height(closed.periapsis - closed.Planet.Radius);
                }
                else if (closed != null && closed.Planet != null)
                {
                    ap = pe = "escape";
                }
                else if (!node.valid)
                {
                    ap = pe = "invalid";
                }

                lblOrbit.Text = string.Format("Total dV: {0}\nPredicted Ap: {1}\nPredicted Pe: {2}", totalDv, ap, pe);

                string burnAt = node.Expired ? "expired" : string.Format("T+{0}", Lang.Duration(countdown));
                string burnTime = duration > 0.0 ? Lang.Duration(duration) : "unknown";
                double remainingDv = control.Burning
                    ? Math.Max(0.0, node.TotalDV - control.AccumulatedDV)
                    : node.TotalDV;
                string status = node.executed && control.Phase == BurnPhase.Idle
                    ? "done"
                    : PhaseText(control);

                lblBurn.Text = string.Format(
                    "Burn at: {0}  ({1})\nBurn time: {2}\nRemaining dV: {3}",
                    burnAt, status, burnTime, Lang.Velocity(remainingDv));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ManeuverNode] window refresh failed: " + e.Message);
            }
            finally
            {
                refreshing = false;
            }
        }

        void RefreshStatus(BurnControl control)
        {
            int active = PropulsionUtil.CountActiveEngines(owner.Rocket, out int total);
            string engines = total == 0
                ? "Engines: none"
                : string.Format("Engines: {0}/{1} lit", active, total);

            string message = control.Message;
            if (string.IsNullOrEmpty(message) && active == 0 && total > 0)
            {
                message = "No engine lit - activate a stage (Space).";
            }

            lblStatus.Text = string.IsNullOrEmpty(message)
                ? engines
                : engines + "\n" + message;
        }

        static string PhaseText(BurnControl control)
        {
            switch (control.Phase)
            {
                case BurnPhase.Warping:
                    return "warping";
                case BurnPhase.Holding:
                    return control.Aligned ? "aligned" : "aligning";
                case BurnPhase.Burning:
                    return "burning";
                case BurnPhase.Complete:
                    return "complete";
                default:
                    return "idle";
            }
        }

        // ------------------------------------------------------------------
        // 回调
        // ------------------------------------------------------------------

        void TogglePlaceMode()
        {
            owner.PlaceMode = !owner.PlaceMode;
            Refresh();
        }

        void OnProgradeChanged(float v)
        {
            if (!refreshing)
            {
                owner.SetSelectedDV(v, null);
            }
        }

        void OnRadialChanged(float v)
        {
            if (!refreshing)
            {
                owner.SetSelectedDV(null, v);
            }
        }

        // ------------------------------------------------------------------
        // 小工具
        // ------------------------------------------------------------------

        Container Row(int width, int height, float spacing = 8f)
        {
            Container row = Builder.CreateContainer(holder, 0, 0);
            row.Size = new Vector2(width, height);
            row.CreateLayoutGroup(Type.Horizontal, TextAnchor.MiddleCenter, spacing);
            return row;
        }

        static Button AddButton(Transform parent, int width, int height, string text, Action onClick)
        {
            return Builder.CreateButton(parent, width, height, 0, 0, onClick, text);
        }

        static Label AddLabel(Transform parent, int width, int height, string text, TextAlignmentOptions alignment)
        {
            Label label = Builder.CreateLabel(parent, width, height, 0, 0, text);
            label.TextAlignment = alignment;
            return label;
        }

        Label AddLabelBlock(int height)
        {
            // 不关自动缩放：多行文案万一超框，让 TMP 自己压字号
            Label label = Builder.CreateLabel(holder, ContentWidth, height, 0, 0, "--");
            label.TextAlignment = TextAlignmentOptions.TopLeft;
            return label;
        }

        static void SetSelected(Button button, bool selected)
        {
            if (button == null || button.gameObject == null)
            {
                return;
            }
            ButtonPC pc = button.gameObject.GetComponent<ButtonPC>();
            if (pc != null)
            {
                pc.SetSelected(selected);
            }
        }

        /// <summary>
        /// ModGUI 的 CurrentScene 模式会去找活动场景里名为 "--- UI ---" 的根节点。
        /// 找不到时（例如 UI 在附加场景里）退化为搜索任意已加载场景中的 Canvas。
        /// </summary>
        internal static void EnsureCanvas(GameObject holder)
        {
            if (holder == null || holder.transform.parent != null)
            {
                return;
            }
            try
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded)
                    {
                        continue;
                    }
                    GameObject[] roots = scene.GetRootGameObjects();
                    for (int j = 0; j < roots.Length; j++)
                    {
                        Canvas canvas = roots[j].GetComponentInChildren<Canvas>(false);
                        if (canvas != null && canvas.renderMode != RenderMode.WorldSpace)
                        {
                            holder.transform.SetParent(canvas.transform, false);
                            return;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ManeuverNode] UI parent lookup failed: " + e.Message);
            }
        }
    }
}
