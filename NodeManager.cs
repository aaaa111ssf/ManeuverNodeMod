using System;
using System.Collections.Generic;
using SFS.Input;
using SFS.UI.ModGUI;
using SFS.World;
using SFS.World.Maps;
using SFS.WorldBase;
using UnityEngine;
using UnityEngine.SceneManagement;
using ModButton = SFS.UI.ModGUI.Button;
using ModBuilder = SFS.UI.ModGUI.Builder;

namespace SFSManeuverNode
{
    /// <summary>
    /// 模组的运行时主体：持有节点列表、接管地图点击、绘制预测轨道、驱动参数窗口与自动执行流水线。
    /// 挂在 DontDestroyOnLoad 的常驻对象上，因此需要自己在场景切换时判断世界场景是否还存在。
    /// </summary>
    internal class NodeManager : MonoBehaviour
    {
        /// <summary>撤销栈里只存玩家真正能改的三个输入量，其余都是推导出来的。</summary>
        internal struct NodeState
        {
            public double burnTime;
            public double progradeDV;
            public double radialDV;
        }

        internal sealed class UndoEntry
        {
            public readonly List<NodeState> states = new List<NodeState>();
            public int selectedIndex = -1;
        }

        internal static NodeManager main;

        static readonly Color SelectedColor = new Color(1f, 0.63f, 0.15f, 1f);
        static readonly Color NormalColor = new Color(0.40f, 0.80f, 1f, 0.85f);

        /// <summary>TimewarpTo 里存选中点的私有字段，取一次缓存下来。</summary>
        static readonly System.Reflection.FieldInfo TimewarpSelectionField =
            HarmonyLib.AccessTools.Field(typeof(TimewarpTo), "selected");

        readonly List<ManeuverNodeData> nodes = new List<ManeuverNodeData>();
        readonly NodeGizmo gizmo = new NodeGizmo();

        ManeuverNodeData selected;
        NodeWindow window;
        Screen_Game hookedInput;

        double nextRebuildAt;
        double nextRefreshAt;
        double nextWindowRetryAt = double.NegativeInfinity;

        // 拖手柄期间为 true，PatchMapViewOnDrag 会据此跳过地图平移
        static bool handleDragActive;

        // 最近一次游戏报上来的光标像素位置。
        // DragData 里只有 deltaPixel、没有位置，而「拖中心点滑轨道」需要光标的世界位置，
        // 所以从 onInputStay 里把它记下来（InputManager 每帧都会先 InputStay 再 ApplyDrag）。
        Vector2 lastCursorPixel;

        // 这一次「按下」已经被手柄吃掉了（例如点了退出按钮）。松开时不要再当成一次地图点击。
        bool pressConsumedByGizmo;

        /// <summary>悬停命中半径（像素）：光标落进这个圈里，节点就从收回态升到聚焦态。</summary>
        const float HoverPixels = 22f;

        /// <summary>当前悬停的节点（聚焦态，设计文档 §2.2）。</summary>
        ManeuverNodeData hovered;

        // ---- 撤销 / 重做（设计文档 §3.2，容量 50 步，节点级） ----

        readonly List<UndoEntry> undoStack = new List<UndoEntry>();
        readonly List<UndoEntry> redoStack = new List<UndoEntry>();
        bool undoGroupOpen;

        // ---- 预测轨道上的「Create node」弹窗（与原生「加速到此处」一致的交互） ----
        Orbit pendingOrbit;
        double pendingAngle;
        GameObject placeHolder;
        ModButton placeButton;
        bool placePending;
        bool dismissNativePending;

        /// <summary>连续两次放置节点模式：开启后下一次地图左键点击就会落在轨道上。</summary>
        internal bool PlaceMode;

        /// <summary>给 Harmony 前缀查询：现在是否正抓着手柄拖 ΔV。</summary>
        internal static bool HandleDragActive => handleDragActive;

        internal BurnControl Control { get; private set; }

        internal IReadOnlyList<ManeuverNodeData> Nodes => nodes;

        internal ManeuverNodeData Selected => selected;

        internal NodeGizmo Gizmo => gizmo;

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------

        void Awake()
        {
            main = this;
            Control = new BurnControl(this);
            // 原生「加速到此处」菜单下方的 Create node 按钮
            gameObject.AddComponent<NativeMenuButton>();
        }

        void OnDestroy()
        {
            if (main == this)
            {
                main = null;
            }
            handleDragActive = false;
            pressConsumedByGizmo = false;
        }

        void Update()
        {
            try
            {
                HookInput();
                PollKeys();
                EnsureWindow();
                UpdateHover();
                TickRebuild();
                if (placePending && !InMapMode)
                {
                    HidePlacePopup();
                }

                Control.Tick();

                // 兜底：鼠标已经松开 / 场景换了，就当拖拽结束，别把地图永久锁住
                if (handleDragActive && !LeftMouseHeld())
                {
                    EndHandleDrag();
                }

                if (window != null && window.Created && Time.realtimeSinceStartup >= nextRefreshAt)
                {
                    nextRefreshAt = Time.realtimeSinceStartup + 0.1;
                    window.Refresh();
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[ManeuverNode] Update failed: " + e);
            }
        }

        void FixedUpdate()
        {
            try
            {
                Control.FixedTick();
            }
            catch (Exception e)
            {
                Debug.LogError("[ManeuverNode] FixedUpdate failed: " + e);
            }
        }

        /// <summary>
        /// 收原生菜单放在 LateUpdate：MapManager 比我们晚订阅 onInputEnd（它在地图场景 Start 才挂），
        /// 当场收掉会被它转头 TrySelect 设回去。LateUpdate 一定排在所有 Update 之后。
        /// </summary>
        void LateUpdate()
        {
            if (!dismissNativePending)
            {
                return;
            }
            dismissNativePending = false;
            try
            {
                TimewarpTo tw = Map.manager != null ? Map.manager.timewarpTo : null;
                if (tw != null)
                {
                    tw.Unselect();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ManeuverNode] dismiss native menu failed: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // 环境
        // ------------------------------------------------------------------

        internal Rocket Rocket
        {
            get
            {
                if (PlayerController.main == null)
                {
                    return null;
                }
                return PlayerController.main.player.Value as Rocket;
            }
        }

        internal Trajectory Baseline
        {
            get
            {
                Rocket rocket = Rocket;
                if (rocket == null || rocket.mapPlayer == null)
                {
                    return null;
                }
                return rocket.mapPlayer.Trajectory;
            }
        }

        static bool InWorld => Map.manager != null && Map.view != null && WorldTime.main != null;

        static bool InMapMode => InWorld && Map.manager.mapMode.Value;

        static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        // ------------------------------------------------------------------
        // 原生菜单（Timewarp Here）→ 建节点
        // ------------------------------------------------------------------

        static Planet capturedPlanet;
        static double capturedAngle;
        static bool capturedValid;

        /// <summary>
        /// 由 PatchTimewarpToTrySelect 调用。游戏在轨道上左键点一下就会走 TrySelect，
        /// 把玩家真正点到的行星与位置角记下来，「Create node」按钮再用它建节点。
        /// </summary>
        internal static void CaptureMapSelection(TimewarpTo instance, bool result)
        {
            capturedValid = false;
            if (!result || instance == null || TimewarpSelectionField == null)
            {
                return;
            }
            try
            {
                object value = TimewarpSelectionField.GetValue(instance);
                if (value is TimewarpTo.Select_Point point && point.planet != null &&
                    !double.IsNaN(point.angleRadians))
                {
                    capturedPlanet = point.planet;
                    capturedAngle = point.angleRadians;
                    capturedValid = true;
                }
            }
            catch
            {
                capturedValid = false;
            }
        }

        /// <summary>在玩家刚才点到的轨道位置建一个机动节点（0 ΔV 起步）。</summary>
        internal void CreateNodeFromMapSelection()
        {
            if (!capturedValid || capturedPlanet == null)
            {
                return;
            }
            Trajectory baseline = Baseline;
            if (baseline == null || baseline.paths == null)
            {
                return;
            }

            // 优先用同一颗行星的那条轨道，找不到就用离角度最近的轨道兜底
            for (int i = 0; i < baseline.paths.Count; i++)
            {
                if (baseline.paths[i] is Orbit orbit && orbit.Planet == capturedPlanet)
                {
                    AddOrSelectNode(NodeMath.TimeAtAngle(orbit, capturedAngle));
                    return;
                }
            }

            Orbit best = null;
            double bestError = double.MaxValue;
            for (int i = 0; i < baseline.paths.Count; i++)
            {
                if (!(baseline.paths[i] is Orbit orbit) || orbit.Planet == null)
                {
                    continue;
                }
                double radius = orbit.GetRadiusAtAngle(capturedAngle);
                if (!NodeMath.IsFinite(radius) || radius <= 0.0)
                {
                    continue;
                }
                double error = Math.Abs(radius - orbit.Planet.Radius);
                if (error < bestError)
                {
                    bestError = error;
                    best = orbit;
                }
            }
            if (best != null)
            {
                AddOrSelectNode(NodeMath.TimeAtAngle(best, capturedAngle));
            }
        }

        ManeuverNodeData AddOrSelectNode(double time)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (Math.Abs(nodes[i].burnTime - time) < 0.5)
                {
                    Select(nodes[i]);
                    DismissNativeMenu(); // 点已有节点也要把「加速到此处」菜单收掉
                    window?.Refresh();
                    return nodes[i];
                }
            }
            PushUndo();
            ManeuverNodeData node = new ManeuverNodeData
            {
                burnTime = time,
                progradeDV = 0.0,
                radialDV = 0.0
            };
            nodes.Add(node);
            SortNodes();
            Rebuild();
            Select(node);
            Debug.Log(string.Format(
                "[ManeuverNode] node created: index {0}, t+{1:0.#}s, nodes {2}",
                node.index, time - (WorldTime.main != null ? WorldTime.main.worldTime : 0.0), nodes.Count));
            DismissNativeMenu();
            window?.Refresh();
            return node;
        }

        // ------------------------------------------------------------------
        // 输入
        // ------------------------------------------------------------------

        void HookInput()
        {
            if (GameManager.main == null)
            {
                // 旧场景的 GameManager 已销毁。UnityEngine.Object 的 "== null" 对已销毁对象也返回 true，
                // 这里把它换成真正的 null，免得新场景万一复用了同一个实例 ID 被误判成"已挂过回调"。
                if (!ReferenceEquals(hookedInput, null) && hookedInput == null)
                {
                    hookedInput = null;
                }
                return;
            }
            Screen_Game input = GameManager.main.map_Input;
            if (input == null || hookedInput == input)
            {
                return;
            }
            hookedInput = input;
            input.onInputStart += OnMapInputStart;
            input.onInputStay += OnMapInputStay;
            input.onDrag += OnMapDrag;
            input.onInputEnd += OnMapInputEnd;
        }

        void OnMapInputStay(OnInputStayData data)
        {
            if (data != null && data.position != null)
            {
                lastCursorPixel = data.position.pixel;
            }
        }

        /// <summary>
        /// 悬停 → 聚焦态。光标直接读 mousePosition（悬停没有按键事件可接）。
        /// 拖手柄期间不换焦点，避免一边拖一边闪。
        /// </summary>
        void UpdateHover()
        {
            hovered = null;
            if (!InMapMode || nodes.Count == 0 || handleDragActive)
            {
                return;
            }
            Vector2 pixel;
            try
            {
                pixel = Input.mousePosition;
            }
            catch
            {
                return;
            }

            double best = HoverPixels;
            for (int i = 0; i < nodes.Count; i++)
            {
                ManeuverNodeData node = nodes[i];
                if (!node.valid || node.burnLocation == null)
                {
                    continue;
                }
                if (!NodeGizmo.ScreenCenter(node, out Vector2 center))
                {
                    continue;
                }
                double d = (center - pixel).magnitude;
                if (d < best)
                {
                    best = d;
                    hovered = node;
                }
            }
        }

        // 说明：地图平移（MapView.OnDrag）会把光标位移直接加到 view.position 上，和拖节点抢同一次
        // 拖拽。现在改成「屏幕上抓住手柄 → MapHooks 跳过地图平移 → 位移只用来改 ΔV」。
        void OnMapInputStart(OnInputStartData data)
        {
            try
            {
                EndHandleDrag();
                pressConsumedByGizmo = false;
                if (!InMapMode || data == null || data.position == null)
                {
                    return;
                }
                if (data.inputType != InputType.MouseLeft && data.inputType != InputType.Touch)
                {
                    return;
                }
                lastCursorPixel = data.position.pixel;

                ManeuverNodeData node = selected;
                if (node == null)
                {
                    return;
                }
                GizmoHandle handle = gizmo.TryBeginDrag(data.position.pixel, node);
                if (handle == GizmoHandle.Exit)
                {
                    // 手柄上的红叉 = 删除这个节点（不是退出编辑）。
                    // 按下已经被吃掉，松开时别再当成一次地图点击（否则会在原地又建一个节点）。
                    pressConsumedByGizmo = true;
                    DeleteSelectedFromGizmo();
                    return;
                }
                if (handle != GizmoHandle.None)
                {
                    handleDragActive = true;
                    OpenUndoGroup(); // 整个拖拽只记一步撤销
                    window?.Refresh();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ManeuverNode] input start failed: " + e.Message);
            }
        }

        void OnMapDrag(DragData data)
        {
            try
            {
                if (!handleDragActive || data == null)
                {
                    return;
                }
                ManeuverNodeData node = selected;
                if (node == null)
                {
                    EndHandleDrag();
                    return;
                }
                if (gizmo.ApplyDrag(data.deltaPixel, lastCursorPixel, node))
                {
                    Rebuild();
                    window?.Refresh();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ManeuverNode] handle drag failed: " + e.Message);
            }
        }

        void OnMapInputEnd(OnInputEndData data)
        {
            bool wasDragging = handleDragActive;
            bool consumed = pressConsumedByGizmo;
            pressConsumedByGizmo = false;
            EndHandleDrag();
            if (wasDragging || consumed || !InMapMode || data == null)
            {
                return;
            }

            bool leftClick = data.LeftClick;
            bool rightClick = data.click && data.inputType == InputType.MouseRight;
            if (!leftClick && !rightClick)
            {
                return;
            }

            // 1) 点在某个节点标记上 → 选中它，进入编辑模式（手柄 + 窗口一起出现）。
            //    这样「退出编辑」之后再点一下节点就能回来。
            HidePlacePopup();
            if (TrySelectNodeAt(data.position))
            {
                DismissNativeMenu(); // 点已有节点也会命中真实轨道、把原生菜单弹出来，得一起收掉
                window?.Refresh();
                return;
            }

            // 2) 落节点。
            //    显式放置模式 / 右键：点哪建哪（老行为）。
            //    普通左键点**预测轨道**：先弹一个自己的「Create node」按钮，点了才建 ——
            //    和原生「加速到此处」的交互保持一致，也避免误触就凭空多出一个节点。
            if (PlaceMode || rightClick)
            {
                if (TryFindPlacement(data.position, true, false, out Orbit orbit, out double angle))
                {
                    AddOrSelectNode(NodeMath.TimeAtAngle(orbit, angle));
                    window?.Refresh();
                    return;
                }
            }
            else
            {
                if (TryFindPlacement(data.position, false, true, out Orbit orbit, out double angle))
                {
                    ShowPlacePopup(orbit, angle, data.position.pixel);
                    return;
                }
            }

            // 3) 点在地图其它任何地方（手柄以外的位置）→ 退出节点编辑
            if (selected != null || PlaceMode)
            {
                ExitNodeEdit();
            }
        }

        // ------------------------------------------------------------------
        // 撤销 / 重做
        // ------------------------------------------------------------------

        /// <summary>
        /// 开一个撤销分组：拖手柄时整个拖拽过程只记一步，不会每帧压一条。
        /// 分组期间 <see cref="PushUndo"/> 不会再压栈。
        /// </summary>
        internal void OpenUndoGroup()
        {
            if (undoGroupOpen)
            {
                return;
            }
            undoGroupOpen = true;
            PushSnapshot();
        }

        internal void CloseUndoGroup()
        {
            undoGroupOpen = false;
        }

        /// <summary>离散操作（建/删节点、窗口里改数值、快捷键调时间）之前调一次。</summary>
        internal void PushUndo()
        {
            if (undoGroupOpen)
            {
                return;
            }
            PushSnapshot();
        }

        void PushSnapshot()
        {
            UndoEntry entry = new UndoEntry();
            for (int i = 0; i < nodes.Count; i++)
            {
                ManeuverNodeData node = nodes[i];
                entry.states.Add(new NodeState
                {
                    burnTime = node.burnTime,
                    progradeDV = node.progradeDV,
                    radialDV = node.radialDV
                });
            }
            entry.selectedIndex = nodes.IndexOf(selected);
            undoStack.Add(entry);
            if (undoStack.Count > 50)
            {
                undoStack.RemoveAt(0);
            }
            redoStack.Clear();
        }

        internal void Undo()
        {
            if (undoStack.Count == 0)
            {
                return;
            }
            redoStack.Add(Snapshot());
            if (redoStack.Count > 50)
            {
                redoStack.RemoveAt(0);
            }
            UndoEntry target = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            ApplySnapshot(target);
            Debug.Log("[ManeuverNode] undo, depth " + undoStack.Count);
        }

        internal void Redo()
        {
            if (redoStack.Count == 0)
            {
                return;
            }
            undoStack.Add(Snapshot());
            if (undoStack.Count > 50)
            {
                undoStack.RemoveAt(0);
            }
            UndoEntry target = redoStack[redoStack.Count - 1];
            redoStack.RemoveAt(redoStack.Count - 1);
            ApplySnapshot(target);
            Debug.Log("[ManeuverNode] redo, depth " + undoStack.Count);
        }

        UndoEntry Snapshot()
        {
            UndoEntry entry = new UndoEntry();
            for (int i = 0; i < nodes.Count; i++)
            {
                ManeuverNodeData node = nodes[i];
                entry.states.Add(new NodeState
                {
                    burnTime = node.burnTime,
                    progradeDV = node.progradeDV,
                    radialDV = node.radialDV
                });
            }
            entry.selectedIndex = nodes.IndexOf(selected);
            return entry;
        }

        void ApplySnapshot(UndoEntry entry)
        {
            EndHandleDrag();
            Control.StopEverything();
            nodes.Clear();
            for (int i = 0; i < entry.states.Count; i++)
            {
                NodeState s = entry.states[i];
                nodes.Add(new ManeuverNodeData
                {
                    burnTime = s.burnTime,
                    progradeDV = s.progradeDV,
                    radialDV = s.radialDV
                });
            }
            SortNodes();
            ManeuverNodeData next = entry.selectedIndex >= 0 && entry.selectedIndex < nodes.Count
                ? nodes[entry.selectedIndex]
                : null;
            if (!ReferenceEquals(selected, next))
            {
                selected = next;
                Control.OnSelectedChanged();
            }
            Rebuild();
            window?.Refresh();
        }

        /// <summary>退出节点编辑：收起手柄、退出放置模式、隐藏参数窗口（F7 可以再叫回来）。</summary>
        internal void ExitNodeEdit()
        {
            EndHandleDrag();
            PlaceMode = false;
            Select(null);
            DismissNativeMenu();
            if (window != null && window.Created)
            {
                window.Visible = false;
            }
        }

        /// <summary>手柄上的红叉：删除当前选中的节点，并退出编辑（不自动选中下一个）。</summary>
        internal void DeleteSelectedFromGizmo()
        {
            EndHandleDrag();
            PlaceMode = false;
            ManeuverNodeData victim = selected;
            if (victim == null)
            {
                ExitNodeEdit();
                return;
            }
            Debug.Log("[ManeuverNode] node deleted via gizmo X, index " + victim.index);
            PushUndo();
            // 先把 selected 清掉，免得 DeleteSelected 又自动选中下一个把窗口弹出来
            selected = null;
            Control.StopEverything();
            nodes.Remove(victim);
            SortNodes();
            Rebuild();
            DismissNativeMenu();
            if (window != null && window.Created)
            {
                window.Visible = false;
            }
            window?.Refresh();
        }

        /// <summary>
        /// 收掉游戏原生的「加速到此处」菜单和挂在它下面的 Create node 按钮。
        /// 实际收起动作推迟到 <see cref="LateUpdate"/>，避免被游戏自己的输入回调改回去。
        /// </summary>
        static void DismissNativeMenu()
        {
            NodeManager manager = main;
            if (manager != null)
            {
                manager.dismissNativePending = true;
            }
        }

        void EndHandleDrag()
        {
            handleDragActive = false;
            gizmo.EndDrag();
            CloseUndoGroup();
        }

        void PollKeys()
        {
            Keybindings keys = Keybindings.main;
            if (keys == null)
            {
                return;
            }
            if (WasPressed(keys.toggleWindow, KeyCode.F7))
            {
                if (window != null && window.Created)
                {
                    window.Visible = !window.Visible;
                }
            }
            if (WasPressed(keys.togglePlaceMode, KeyCode.F8))
            {
                PlaceMode = !PlaceMode;
                window?.Refresh();
            }
            if (WasPressed(keys.warpToNode, KeyCode.F6))
            {
                Control.ToggleWarp();
            }
            PollEditorKeys();
        }

        /// <summary>
        /// 设计文档 §3.2 的键盘操作。这些键游戏默认都没绑（Escape 例外，所以退出编辑不用 Esc），
        /// 但只在**地图模式**下生效，避免在建筑/飞行场景抢键。
        /// </summary>
        void PollEditorKeys()
        {
            if (!InMapMode)
            {
                return;
            }

            // Tab / Shift+Tab：沿时间轴遍历节点
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                SelectOffset(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? -1 : 1);
            }

            // Ctrl+Z / Ctrl+Y：撤销 / 重做（游戏只在建筑场景绑了这两个组合）
            if (Input.GetKeyDown(KeyCode.Z) &&
                (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            {
                Undo();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Y) &&
                (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            {
                Redo();
                return;
            }

            // Delete：删除选中节点（游戏只在建筑场景用 Delete）
            if (Input.GetKeyDown(KeyCode.Delete))
            {
                DeleteSelected();
                return;
            }

            ManeuverNodeData node = selected;
            if (node == null || !node.valid || node.sourceOrbit == null)
            {
                return;
            }
            double period = node.sourceOrbit.period;
            bool hasPeriod = NodeMath.IsFinite(period) && period > 0.0;

            // N：在选中节点之后派生下一个节点（串联规划）
            if (Input.GetKeyDown(KeyCode.N) && hasPeriod)
            {
                AddOrSelectNode(node.burnTime + period);
                return;
            }

            // ← / →：节点沿轨道移动；Shift 粗调。用的是周期的比例，缩放无关。
            if (hasPeriod && (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow)))
            {
                bool coarse = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                double fraction = (coarse ? 0.10 : 0.01) * (Input.GetKeyDown(KeyCode.RightArrow) ? 1.0 : -1.0);
                ShiftSelectedTime(period * fraction);
            }

            // ↑ / ↓：顺向 ΔV 增减；Shift 0.1 档，Ctrl 10 档
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow))
            {
                double step = Input.GetKeyDown(KeyCode.UpArrow) ? 1.0 : -1.0;
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                {
                    step *= 0.1;
                }
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                {
                    step *= 10.0;
                }
                SetSelectedDV(Clamp(node.progradeDV + step), null);
                window?.Refresh();
            }
        }

        static double Clamp(double v)
        {
            if (double.IsNaN(v))
            {
                return 0.0;
            }
            return Math.Max(-100000.0, Math.Min(100000.0, v));
        }

        static bool WasPressed(KeybindingsPC.Key key, KeyCode fallback)
        {
            if (key != null)
            {
                return ((I_Key)key).IsKeyDown();
            }
            return Input.GetKeyDown(fallback);
        }

        /// <summary>
        /// SFS 整个输入系统就是建在 UnityEngine.Input（旧版）上的，所以这里直接用它没问题；
        /// 外面再包一层 catch，万一哪天输入后端换了，也只是当没按住，不会每帧刷错误。
        /// </summary>
        static bool LeftMouseHeld()
        {
            try
            {
                return Input.GetMouseButton(0);
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 节点操作
        // ------------------------------------------------------------------

        internal void Select(ManeuverNodeData node)
        {
            if (ReferenceEquals(selected, node))
            {
                return;
            }
            selected = node;
            // 选中节点 = 进入编辑，把可能被「退出」收起来的窗口重新叫出来
            if (node != null && window != null && window.Created)
            {
                window.Visible = true;
            }
            Control.OnSelectedChanged();
            window?.Refresh();
        }

        internal void SelectOffset(int direction)
        {
            if (nodes.Count == 0)
            {
                return;
            }
            int index = selected == null ? 0 : nodes.IndexOf(selected);
            if (index < 0)
            {
                index = 0;
            }
            index = (index + direction) % nodes.Count;
            if (index < 0)
            {
                index += nodes.Count;
            }
            Select(nodes[index]);
        }

        internal void DeleteSelected()
        {
            if (selected == null)
            {
                return;
            }
            PushUndo();
            nodes.Remove(selected);
            SortNodes();
            selected = null;
            Select(nodes.Count > 0 ? nodes[0] : null);
            Control.StopEverything();
            Rebuild();
            window?.Refresh();
        }

        internal void ClearAllNodes()
        {
            nodes.Clear();
            selected = null;
            Control.StopEverything();
            window?.Refresh();
        }

        internal void SetSelectedDV(double? prograde, double? radial)
        {
            ManeuverNodeData node = selected;
            if (node == null)
            {
                return;
            }
            PushUndo();
            if (prograde.HasValue)
            {
                node.progradeDV = prograde.Value;
            }
            if (radial.HasValue)
            {
                node.radialDV = radial.Value;
            }
            node.executed = false;
            Rebuild();
        }

        internal void ShiftSelectedTime(double seconds)
        {
            ManeuverNodeData node = selected;
            if (node == null)
            {
                return;
            }
            PushUndo();
            double now = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;
            node.burnTime = Math.Max(now, node.burnTime + seconds);
            node.executed = false;
            SortNodes();
            Rebuild();
        }

        internal void ShiftSelectedPeriod(double fraction)
        {
            ManeuverNodeData node = selected;
            if (node == null || node.sourceOrbit == null)
            {
                return;
            }
            double period = node.sourceOrbit.period;
            if (!(period > 0.0))
            {
                return;
            }
            ShiftSelectedTime(period * fraction);
        }

        internal void NotifyBurnFinished(ManeuverNodeData node, bool complete)
        {
            Debug.Log("[ManeuverNode] burn finished, complete=" + complete);
            window?.Refresh();
        }

        // ------------------------------------------------------------------
        // 重建预测
        // ------------------------------------------------------------------

        internal void Rebuild()
        {
            // 点火过程中冻结预测：这时候火箭自己的轨迹已经变了，
            // 再拿新基准去算会让 Δv 方向漂移。
            if (Control != null && Control.Burning)
            {
                return;
            }
            Trajectory baseline = Baseline;
            if (baseline == null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    nodes[i].valid = false;
                    nodes[i].index = i + 1;
                }
                return;
            }
            NodeMath.RebuildAll(nodes, baseline);
        }

        void TickRebuild()
        {
            if (Time.realtimeSinceStartup < nextRebuildAt)
            {
                return;
            }
            nextRebuildAt = Time.realtimeSinceStartup + 0.3;
            Rebuild();
        }

        void SortNodes()
        {
            nodes.Sort((a, b) => a.burnTime.CompareTo(b.burnTime));
            for (int i = 0; i < nodes.Count; i++)
            {
                nodes[i].index = i + 1;
            }
        }

        // ------------------------------------------------------------------
        // 放置节点
        // ------------------------------------------------------------------

        /// <summary>
        /// 把一次地图点击换算成节点位置（不真正创建，交给调用方）。
        /// 候选轨道 = 真实轨迹（可选）+ 每个节点的 predicted，这样才能在规划轨道上续放节点。
        /// requireOffBaseline：普通左键要求明显偏离真实轨道，避开原生「加速到此处」的点击。
        /// </summary>
        bool TryFindPlacement(TouchPosition position, bool includeBaseline, bool requireOffBaseline,
            out Orbit foundOrbit, out double foundAngle)
        {
            foundOrbit = null;
            foundAngle = 0.0;
            Trajectory baseline = Baseline;
            if (baseline == null || Map.view == null || position == null)
            {
                return false;
            }

            Vector2 world = position.World((float)(Map.view.view.distance.Value / 1000.0));

            Orbit bestOrbit = null;
            double bestAngle = 0.0;
            double bestError = double.MaxValue;

            // 先量一下真实轨道离点击点有多远：用来判断这一下点的是不是游戏原生轨道
            Consider(baseline, world, out double baselineError, out _);

            if (includeBaseline)
            {
                Consider(baseline, world, ref bestOrbit, ref bestAngle, ref bestError);
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                ManeuverNodeData node = nodes[i];
                if (node.valid && node.predicted != null)
                {
                    Consider(node.predicted, world, ref bestOrbit, ref bestAngle, ref bestError);
                }
            }

            float tolerance = Map.view.ToConstantSize(0.04f);
            if (bestOrbit == null || bestError > tolerance)
            {
                return false;
            }
            if (requireOffBaseline && baselineError <= tolerance * 1.6f)
            {
                // 这一下离真实轨道也很近 —— 交给游戏原生的「加速到此处」处理，别抢
                return false;
            }

            foundOrbit = bestOrbit;
            foundAngle = bestAngle;
            return true;
        }

        /// <summary>在某条轨迹的所有锥段里找离点击点最近的那一条，并更新最优解。</summary>
        static void Consider(Trajectory trajectory, Vector2 world,
            ref Orbit bestOrbit, ref double bestAngle, ref double bestError)
        {
            if (trajectory == null || trajectory.paths == null)
            {
                return;
            }
            for (int i = 0; i < trajectory.paths.Count; i++)
            {
                if (!(trajectory.paths[i] is Orbit orbit) || orbit.Planet == null)
                {
                    continue;
                }
                double angle = NodeMath.ClosestOrbitAngle(orbit, world, out double error);
                if (error < bestError)
                {
                    bestError = error;
                    bestOrbit = orbit;
                    bestAngle = angle;
                }
            }
        }

        /// <summary>量某条轨迹离点击点最近的距离（不关心是哪一条锥段）。</summary>
        static void Consider(Trajectory trajectory, Vector2 world, out double error, out double angle)
        {
            Orbit orbit = null;
            double bestAngle = 0.0;
            double bestError = double.MaxValue;
            Consider(trajectory, world, ref orbit, ref bestAngle, ref bestError);
            error = bestError;
            angle = bestAngle;
        }

        // ------------------------------------------------------------------
        // 预测轨道上的「Create node」弹窗
        // ------------------------------------------------------------------

        /// <summary>
        /// 在点击处弹自己的「Create node」按钮。原生菜单只认真实轨迹，点预测轨道不会出现，
        /// 点按钮才真正建节点 —— 与原生交互一致，也避免误触就凭空多出一个节点。
        /// </summary>
        void ShowPlacePopup(Orbit orbit, double angle, Vector2 screenPixel)
        {
            pendingOrbit = orbit;
            pendingAngle = angle;
            placePending = true;

            if (!EnsurePlaceButton())
            {
                // UI 建不出来就退回老行为：直接落节点，别把功能弄没了
                Debug.LogWarning("[ManeuverNode] place popup unavailable, placing directly");
                ConfirmPendingPlace();
                return;
            }
            placeButton.rectTransform.position = new Vector3(screenPixel.x, screenPixel.y, 0f);
            if (!placeButton.gameObject.activeSelf)
            {
                placeButton.gameObject.SetActive(true);
            }
        }

        void HidePlacePopup()
        {
            placePending = false;
            pendingOrbit = null;
            if (placeButton != null && placeButton.gameObject != null && placeButton.gameObject.activeSelf)
            {
                placeButton.gameObject.SetActive(false);
            }
        }

        void ConfirmPendingPlace()
        {
            Orbit orbit = pendingOrbit;
            double angle = pendingAngle;
            HidePlacePopup();
            if (orbit == null || orbit.Planet == null)
            {
                return;
            }
            AddOrSelectNode(NodeMath.TimeAtAngle(orbit, angle));
        }

        bool EnsurePlaceButton()
        {
            if (placeButton != null && placeButton.gameObject != null)
            {
                return true;
            }
            try
            {
                placeHolder = ModBuilder.CreateHolder(Builder.SceneToAttach.CurrentScene, "ManeuverNode.PlacePopup");
                NodeWindow.EnsureCanvas(placeHolder);
                placeButton = ModBuilder.CreateButton(placeHolder.transform, 150, 34, 0, 0,
                    ConfirmPendingPlace, "Create node");
                if (placeButton == null || placeButton.gameObject == null)
                {
                    return false;
                }
                placeButton.gameObject.name = "ManeuverNode.PlacePopupButton";
                placeButton.gameObject.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ManeuverNode] place popup create failed: " + e.Message);
                return false;
            }
        }

        void DestroyPlacePopup()
        {
            HidePlacePopup();
            if (placeButton != null && placeButton.gameObject != null)
            {
                UnityEngine.Object.Destroy(placeButton.gameObject);
            }
            if (placeHolder != null)
            {
                UnityEngine.Object.Destroy(placeHolder);
            }
            placeButton = null;
            placeHolder = null;
        }


        bool TrySelectNodeAt(TouchPosition position)
        {
            if (nodes.Count == 0 || Map.view == null || position == null)
            {
                return false;
            }
            Vector2 world = position.World((float)(Map.view.view.distance.Value / 1000.0));
            float tolerance = Map.view.ToConstantSize(0.05f);
            ManeuverNodeData best = null;
            double bestError = double.MaxValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                ManeuverNodeData node = nodes[i];
                if (!node.valid || node.burnLocation == null || node.burnLocation.planet == null)
                {
                    continue;
                }
                Vector2 marker = MapDrawer.GetPosition(node.burnLocation);
                double error = (marker - world).magnitude;
                if (error < bestError)
                {
                    bestError = error;
                    best = node;
                }
            }
            if (best == null || bestError > tolerance)
            {
                return false;
            }
            Select(best);
            return true;
        }

        // ------------------------------------------------------------------
        // 窗口
        // ------------------------------------------------------------------

        void EnsureWindow()
        {
            if (!InWorld)
            {
                if (window != null)
                {
                    window.Destroy();
                    window = null;
                }
                DestroyPlacePopup();
                if (nodes.Count > 0)
                {
                    nodes.Clear();
                    selected = null;
                }
                EndHandleDrag();
                Control.StopEverything();
                return;
            }

            if (window != null && window.Created)
            {
                return;
            }
            if (Time.realtimeSinceStartup < nextWindowRetryAt)
            {
                return;
            }
            try
            {
                NodeWindow created = new NodeWindow(this);
                created.Create();
                window = created;
            }
            catch (Exception e)
            {
                Debug.LogError("[ManeuverNode] window create failed: " + e);
                window = null;
                nextWindowRetryAt = Time.realtimeSinceStartup + 5.0;
            }
        }

        // ------------------------------------------------------------------
        // 地图绘制（由 Harmony 在 MapManager.DrawMap 之后调用）
        // ------------------------------------------------------------------

        internal void DrawOverlay()
        {
            if (Map.manager == null || Map.view == null)
            {
                return;
            }
            if (!Map.manager.mapMode.Value || Map.drawer == null || Map.drawer.alphaPerDepth == null)
            {
                return;
            }
            if (Map.dashedLine == null || Map.solidLine == null || Map.elementDrawer == null)
            {
                return;
            }

            gizmo.BeginFrame();
            try
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    ManeuverNodeData node = nodes[i];
                    if (node == null || !node.valid || node.predicted == null || node.burnLocation == null)
                    {
                        continue;
                    }
                    bool isSelected = ReferenceEquals(node, selected);
                    bool isHovered = !isSelected && ReferenceEquals(node, hovered);
                    DrawPredicted(node.predicted, isSelected ? SelectedColor : NormalColor, isSelected);
                    DrawMarker(node, isSelected ? SelectedColor : NormalColor, isSelected, isHovered);
                }

                ManeuverNodeData sel = selected;
                if (sel != null && sel.valid)
                {
                    gizmo.Draw(sel);
                }
            }
            finally
            {
                gizmo.EndFrame();
            }
        }

        static void DrawPredicted(Trajectory trajectory, Color color, bool drawApses)
        {
            if (trajectory.paths == null)
            {
                return;
            }
            for (int i = 0; i < trajectory.paths.Count; i++)
            {
                if (!(trajectory.paths[i] is Orbit orbit) || orbit.Planet == null)
                {
                    continue;
                }
                // 坏轨道（NaN 半长轴）画出去会把整张地图带黑，直接跳过
                if (!NodeMath.IsSane(orbit))
                {
                    Debug.LogWarning("[ManeuverNode] skipped drawing insane predicted orbit");
                    continue;
                }
                int depth = orbit.Planet.orbitalDepth;
                float saved = 1f;
                bool restore = depth >= 0 && depth < Map.drawer.alphaPerDepth.Length;
                if (restore)
                {
                    // 游戏会按「当前视角目标」的深度把其它轨道的 alpha 压成 0，
                    // 预测轨道必须强制可见，画完再还原。
                    saved = Map.drawer.alphaPerDepth[depth];
                    Map.drawer.alphaPerDepth[depth] = 1f;
                }
                try
                {
                    orbit.DrawOrbit(color, false, false, false, Map.dashedLine);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[ManeuverNode] orbit draw failed: " + e.Message);
                }
                finally
                {
                    if (restore)
                    {
                        Map.drawer.alphaPerDepth[depth] = saved;
                    }
                }
            }

            if (!drawApses)
            {
                return;
            }
            Orbit closed = NodeMath.LastClosedOrbit(trajectory);
            if (closed == null || closed.Planet == null || closed.pathType != PathType.Eternal)
            {
                return;
            }
            DrawApsis(closed, closed.periapsis, 0.0, color);
            DrawApsis(closed, closed.apoapsis, Math.PI, color);
        }

        static void DrawApsis(Orbit orbit, double radius, double trueAnomaly, Color color)
        {
            // 半径不有限就直接跳过：NaN 传给 TextMesh 会让渲染器 bounds 变 NaN，整张地图跟着黑屏
            if (!NodeMath.IsFinite(radius) || !NodeMath.IsFinite(trueAnomaly) || radius < orbit.Planet.Radius)
            {
                return;
            }
            Double2 local = Double2.CosSin(trueAnomaly + orbit.arg) * radius;
            Vector2 position = MapDrawer.GetPosition(orbit.Planet, local);
            string prefix = trueAnomaly == 0.0 ? "Pe " : "Ap ";
            MapDrawer.DrawPointWithText(13, color, prefix + Lang.Height(radius - orbit.Planet.Radius),
                36, color, position, local.normalized, 3, 3);
        }

        static void DrawMarker(ManeuverNodeData node, Color color, bool isSelected, bool isHovered)
        {
            if (node.burnLocation == null || node.burnLocation.planet == null)
            {
                return;
            }
            Vector2 position = MapDrawer.GetPosition(node.burnLocation);
            // 渲染层最后一道闸：任何 NaN / Inf / 超大坐标都会把渲染器 bounds 搞坏，
            // 相机剔除随之失效 → 整张地图黑屏。宁可少画一个标记也不能让它进去。
            if (!IsFinite(position.x) || !IsFinite(position.y) ||
                Math.Abs(position.x) > 1e7f || Math.Abs(position.y) > 1e7f)
            {
                Debug.LogWarning(string.Format(
                    "[ManeuverNode] node {0} skipped: bad map position ({1}, {2}); burnLocation r={3}",
                    node.index, position.x.ToString("0.###e+00"), position.y.ToString("0.###e+00"),
                    node.burnLocation.Radius.ToString("0.###e+00")));
                return;
            }

            // Δv 指示线只在收回态画：编辑态下手柄已经把 ΔV 方向和大小都表达清楚了，
            // 再画一条 120px 的箭头会直接压在手柄上（这就是"手柄重叠"的来源之一）
            if (!isSelected)
            {
                DrawDeltaVLine(node, color);
            }

            // 收回态不画圆点 —— 只画文档 §2.1 那个菱形，画两个会叠在一起。
            // 选中态也不画圆点：手柄中心那个紫色实心点就是它。
            if (!isSelected)
            {
                // 收回态（§2.1/§5）：轨道线上一个屏幕恒定的小菱形，已过期的压暗。
                // 聚焦态（§2.2，悬停）：放大到 8.5px 并补一行轻量气泡。
                Color marker = node.Expired
                    ? new Color(0.91f, 0.96f, 1.00f, 0.40f)
                    : new Color(0.91f, 0.96f, 1.00f, 1f);
                DrawDiamondMarker(node, marker, isHovered ? 8.5 : 6.0);
                DrawLabel(position, string.Format("Node#{0}", node.index), color, isHovered ? 24f : 22f);
                if (isHovered)
                {
                    double now2 = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;
                    string tip = string.Format(
                        "dV {0}   T+{1}\nClick to edit",
                        Lang.Velocity(node.TotalDV), Lang.Duration(node.burnTime - now2));
                    // 两行气泡约 50px 高，中心放在 68px 才不会压到下面那行 Node#n
                    DrawLabel(position, tip, new Color(0.91f, 0.96f, 1.00f, 1f), 68f);
                }
                return;
            }

            double now = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;
            string text = string.Format(
                "Node#{0}  dV {1}\nT+{2}",
                node.index, Lang.Velocity(node.TotalDV), Lang.Duration(node.burnTime - now));
            // 放到手柄十字之外：顺向杆最长可伸到 68+24=92px，再留出手柄半径，所以取 116px
            DrawLabel(position, text, color, 116f);
        }

        /// <summary>
        /// 收回态标记：轨道线上一个屏幕空间恒定的小菱形（设计文档 §2.1 / §5）。
        /// 走 <c>Map.solidLine.DrawLine</c>，所以必须传**行星本地坐标**（世界 ÷ 1000）。
        /// </summary>
        static void DrawDiamondMarker(ManeuverNodeData node, Color color, double radiusPixels = 6.0)
        {
            if (Map.solidLine == null || node.burnLocation == null || node.burnLocation.planet == null)
            {
                return;
            }
            double perPixel = NodeGizmo.UnitsPerPixel();
            if (!(perPixel > 0.0))
            {
                return;
            }
            Double2 c = node.burnLocation.position / 1000.0;
            Double2 up = Double2.up;
            Double2 right = Double2.right;

            foreach (double radius in new[] { radiusPixels, radiusPixels * 0.52 })
            {
                Double2[] ring =
                {
                    c + up * radius, c + right * radius, c - up * radius, c - right * radius, c + up * radius
                };
                Vector3[] points = new Vector3[ring.Length];
                for (int i = 0; i < ring.Length; i++)
                {
                    points[i] = ring[i].ToVector3;
                }
                try
                {
                    Map.solidLine.DrawLine(points, node.burnLocation.planet, color, color);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[ManeuverNode] diamond marker failed: " + e.Message);
                    return;
                }
            }
        }

        /// <summary>
        /// 在节点旁写文字。不用 DrawPointWithText（只能沿 normal 四方向加换行微调），
        /// 自己按屏幕像素把标签推到手柄十字外面。
        /// </summary>
        static void DrawLabel(Vector2 position, string text, Color color, float pixelsAbove)
        {
            if (Map.elementDrawer == null)
            {
                return;
            }
            double perPixel = NodeGizmo.UnitsPerPixel();
            if (!(perPixel > 0.0))
            {
                return;
            }
            NodeGizmo.ScreenAxes(out Double2 mapUp, out _);
            Vector2 offset = (mapUp * (pixelsAbove * perPixel)).ToVector2;
            try
            {
                // anchorNormal 传 zero：让 TMP 居中显示，也不再自动加换行把文字推走
                Map.elementDrawer.DrawTextElement(text, Vector2.zero, 36, color, position + offset, 6, true, 60);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ManeuverNode] node label failed: " + e.Message);
            }
        }

        /// <summary>
        /// Δv 指示线。必须传行星本地坐标（世界 ÷ 1000）：LineDrawer.DrawLine 会把 LineRenderer
        /// 挂到 planet.mapHolder 并把 localPosition 归零，传绝对坐标会把 mapHolder.position 叠加两次。
        /// </summary>
        static void DrawDeltaVLine(ManeuverNodeData node, Color color)
        {
            Double2 direction = node.deltaV;
            if (!NodeMath.IsFinite(direction.x) || !NodeMath.IsFinite(direction.y))
            {
                return;
            }
            direction = direction.normalized;
            if (direction.magnitude <= 0.001)
            {
                return;
            }

            double length = Map.view.ToConstantSize(0.13f);
            if (!NodeMath.IsFinite(length) || length <= 0.0)
            {
                return;
            }

            Double2 start = node.burnLocation.position / 1000.0;
            Double2 end = start + direction * length;
            if (!NodeMath.IsFinite(start.x) || !NodeMath.IsFinite(start.y) ||
                !NodeMath.IsFinite(end.x) || !NodeMath.IsFinite(end.y))
            {
                return;
            }
            Vector3[] points = { start.ToVector3, end.ToVector3 };
            try
            {
                Map.solidLine.DrawLine(points, node.burnLocation.planet, color, color);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ManeuverNode] delta-v line failed: " + e.Message);
            }
        }
    }
}
