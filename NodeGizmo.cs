using System;
using SFS.Cameras;
using SFS.Input;
using SFS.World;
using SFS.World.Maps;
using UnityEngine;

namespace SFSManeuverNode
{
    internal enum GizmoHandle
    {
        None,
        Prograde,   // 上：顺向加速（绿）
        Retrograde, // 下：逆向减速（黄）
        RadialOut,  // 右：径向抬升（蓝）
        RadialIn,   // 左：径向压低（橙）
        Center,     // 中心：沿轨道滑动节点
        Exit        // 右上：删除节点（红叉）
    }

    /// <summary>
    /// 选中节点的拖拽手柄。
    /// 三个坑：① MapView.OnDrag 会吞掉光标位移 → 抓手柄时由 MapHooks 跳过地图平移；
    /// ② InputManager.ApplyDrag 传的 deltaPixel 已取反（new DragData(-vector)）→ 要再取反；
    /// ③ 地图↔屏幕方向不要用相机滚转角推，直接实测（见 ScreenAxes）。
    /// sortingOrder 须高于行星地形的 10。
    /// </summary>
    internal class NodeGizmo
    {
        // 尺寸为屏幕像素，与缩放无关。改动时核对：
        //   相邻手柄（90°）中心距 ArmPixels×√2 > 2×(HandlePixels + 描边/2)
        //   CenterGrabPixels + GrabPixels < ArmPixels
        //   ExitPixels 对角点距手柄中心 > ExitGrabPixels + GrabPixels
        const float ArmPixels = 68f;
        const float HandlePixels = 12f;
        const float BarPixels = 8f;
        const float GrabPixels = 20f;
        const float CenterPixels = 8f;
        const float CenterGrabPixels = 26f;
        const float ExitPixels = 58f;
        const float ExitGrabPixels = 17f;

        /// <summary>高于行星地形的 sortingOrder(10)，避免被星球盖住。</summary>
        const int RenderOrder = 60;

        static readonly Color ProgradeColor = new Color(0.27f, 0.88f, 0.42f, 1f);
        static readonly Color RetrogradeColor = new Color(1.00f, 0.84f, 0.29f, 1f);
        static readonly Color RadialOutColor = new Color(0.30f, 0.62f, 1.00f, 1f);
        static readonly Color RadialInColor = new Color(1.00f, 0.54f, 0.24f, 1f);
        static readonly Color CenterColor = new Color(0.70f, 0.40f, 1.00f, 1f);
        static readonly Color ExitCrossColor = new Color(1.00f, 0.23f, 0.19f, 1f);

        /// <summary>每像素对应的 ΔV（m/s）。Shift 0.1×，Ctrl 10×。</summary>
        const double DVPerPixel = 1.0;

        const double MaxDV = 100000.0;

        readonly LineRenderer[] pool = new LineRenderer[32];
        int used;
        Transform planetHolder;
        Vector2 dragPixels;

        internal GizmoHandle ActiveHandle { get; private set; }

        internal bool Dragging => ActiveHandle != GizmoHandle.None;

        /// <summary>1 像素对应多少地图单位。</summary>
        internal static double UnitsPerPixel()
        {
            if (Map.view == null)
            {
                return 0.0;
            }
            double z = Map.view.view.distance.Value / 1000.0;
            if (!(z > 0.0))
            {
                return 0.0;
            }
            try
            {
                Vector2 a = new TouchPosition(Vector2.zero).World((float)z);
                Vector2 b = new TouchPosition(new Vector2(1f, 0f)).World((float)z);
                return (b - a).magnitude;
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// 屏幕两条轴在地图坐标系里的方向（单位向量）。
        /// 直接实测 TouchPosition.World(z) 对 (1,0)/(0,1) 的位移 —— 符号/滚转/透视全自动正确，
        /// 不用相机滚转角去推（推反了十字会被转 2 倍角度）。每帧缓存一次。
        /// </summary>
        internal static void ScreenAxes(out Double2 up, out Double2 right)
        {
            if (cachedFrame == Time.frameCount)
            {
                up = cachedUp;
                right = cachedRight;
                return;
            }

            up = Double2.up;
            right = Double2.right;
            if (Map.view != null)
            {
                float planeZ = (float)(Map.view.view.distance.Value / 1000.0);
                if (planeZ > 0f)
                {
                    try
                    {
                        Vector2 origin = new TouchPosition(Vector2.zero).World(planeZ);
                        Vector2 rightPx = new TouchPosition(new Vector2(1f, 0f)).World(planeZ);
                        Vector2 upPx = new TouchPosition(new Vector2(0f, 1f)).World(planeZ);
                        right = SafeDirection(Double2.ToDouble2(rightPx - origin), Double2.right);
                        up = SafeDirection(Double2.ToDouble2(upPx - origin), Double2.up);
                    }
                    catch
                    {
                    }
                }
            }

            cachedFrame = Time.frameCount;
            cachedUp = up;
            cachedRight = right;
        }

        static Double2 cachedUp = Double2.up;
        static Double2 cachedRight = Double2.right;
        static int cachedFrame = -1;

        /// <summary>地图方向 → 屏幕方向（像素系，y 向上）。对 ScreenAxes 的 2×2 基矩阵求逆。</summary>
        internal static Vector2 ScreenDirection(Double2 mapDirection)
        {
            ScreenAxes(out Double2 up, out Double2 right);
            double det = right.x * up.y - up.x * right.y;
            if (Math.Abs(det) < 1e-12)
            {
                return new Vector2(0f, 1f);
            }
            double sx = (mapDirection.x * up.y - up.x * mapDirection.y) / det;
            double sy = (right.x * mapDirection.y - mapDirection.x * right.y) / det;
            Vector2 v = new Vector2((float)sx, (float)sy);
            float mag = v.magnitude;
            return mag > 1e-6f ? v / mag : new Vector2(0f, 1f);
        }

        static Double2 SafeDirection(Double2 value, Double2 fallback)
        {
            if (!NodeMath.IsFinite(value.x) || !NodeMath.IsFinite(value.y))
            {
                return fallback;
            }
            Double2 normalized = value.normalized;
            return normalized.sqrMagnitude > 0.5 ? normalized : fallback;
        }

        /// <summary>
        /// 节点的绝对地图坐标与所在 z 深度。地图平面在 z = distance/1000（非 0），
        /// 世界↔屏幕换算必须带上。坐标须有限且 |x|,|y| ≤ 1e7，否则渲染器 bounds 会坏。
        /// </summary>
        internal static bool MapCenter(ManeuverNodeData node, out Double2 center, out float mapZ)
        {
            center = Double2.zero;
            mapZ = 0f;
            if (node == null || !node.valid || node.burnLocation == null || node.burnLocation.planet == null)
            {
                return false;
            }
            Vector3 holder = node.burnLocation.planet.mapHolder.position;
            center = new Double2(holder.x, holder.y) + node.burnLocation.position / 1000.0;
            mapZ = holder.z;
            if (!NodeMath.IsFinite(center.x) || !NodeMath.IsFinite(center.y) ||
                Math.Abs(center.x) > 1e7 || Math.Abs(center.y) > 1e7)
            {
                return false;
            }
            return true;
        }

        internal static bool MapCenter(ManeuverNodeData node, out Double2 center)
        {
            return MapCenter(node, out center, out _);
        }

        /// <summary>节点在屏幕上的像素位置。</summary>
        internal static bool ScreenCenter(ManeuverNodeData node, out Vector2 pixel)
        {
            pixel = Vector2.zero;
            if (!MapCenter(node, out Double2 center, out float mapZ))
            {
                return false;
            }
            try
            {
                Vector3 point = new Vector3((float)center.x, (float)center.y, mapZ);
                Vector3 sp = ActiveCamera.Camera.camera.WorldToScreenPoint(point);
                if (sp.z <= 0f)
                {
                    return false;
                }
                pixel = new Vector2(sp.x, sp.y);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal void BeginFrame()
        {
            used = 0;
        }

        internal void EndFrame()
        {
            for (int i = used; i < pool.Length; i++)
            {
                if (pool[i] != null && pool[i].gameObject.activeSelf)
                {
                    pool[i].gameObject.SetActive(false);
                }
            }
        }

        internal void Draw(ManeuverNodeData node)
        {
            if (node == null || !node.valid || node.burnLocation == null || node.burnLocation.planet == null ||
                Map.solidLine == null || Map.solidLine.linePrefab == null || Map.manager == null)
            {
                return;
            }
            planetHolder = node.burnLocation.planet.mapHolder;
            if (planetHolder == null)
            {
                return;
            }

            double perPixel = UnitsPerPixel();
            if (!(perPixel > 0.0))
            {
                return;
            }

            GetAxes(node, out Double2 prograde, out Double2 radial);
            Double2 exitDir = prograde + radial;
            exitDir = exitDir.magnitude < 1e-9 ? prograde : exitDir.normalized;

            double radius = HandlePixels * perPixel;
            double centerRadius = CenterPixels * perPixel;
            double exitRadius = ExitPixels * perPixel;
            float outline = (float)(BarPixels * perPixel) * 0.55f;

            // 几何必须用行星本地坐标（LineRenderer 挂在 planet.mapHolder 上），
            // 传绝对坐标会把 mapHolder.position 叠加两次。
            Double2 origin = node.burnLocation.position / 1000.0;

            ScreenAxes(out Double2 basisUp, out Double2 basisRight);
            // innerFactor：形状朝圆心的内切比，圆/菱/三角=1.0（有顶点朝圆心），方块=cos45°
            DrawArm(origin, prograde, basisUp, basisRight, 24, 0.0, 1.0, radius,
                StretchOf(GizmoHandle.Prograde, ScreenDirection(prograde)), perPixel, ProgradeColor,
                ActiveHandle == GizmoHandle.Prograde, outline);
            DrawArm(origin, -prograde, basisUp, basisRight, 4, 0.0, 1.0, radius,
                StretchOf(GizmoHandle.Retrograde, ScreenDirection(-prograde)), perPixel, RetrogradeColor,
                ActiveHandle == GizmoHandle.Retrograde, outline);
            DrawArm(origin, radial, basisUp, basisRight, 4, Math.PI * 0.25, 0.72, radius,
                StretchOf(GizmoHandle.RadialOut, ScreenDirection(radial)), perPixel, RadialOutColor,
                ActiveHandle == GizmoHandle.RadialOut, outline);
            DrawArm(origin, -radial, basisUp, basisRight, 3, Math.PI, 1.0, radius,
                StretchOf(GizmoHandle.RadialIn, ScreenDirection(-radial)), perPixel, RadialInColor,
                ActiveHandle == GizmoHandle.RadialIn, outline);

            float centerScale = ActiveHandle == GizmoHandle.Center ? 1.35f : 1f;
            Polyline(Shape(origin, basisUp, basisRight, centerRadius, 16, 0.0),
                CenterColor, outline * centerScale, true);
            Polyline(Shape(origin, basisUp, basisRight, centerRadius * 0.42 * centerScale, 16, 0.0),
                CenterColor, outline * 1.25f * centerScale, true);

            Double2 exitCenter = origin + exitDir * exitRadius;
            double tick = radius * 0.78;
            Segment(exitCenter + basisUp * tick + basisRight * tick,
                    exitCenter - basisUp * tick - basisRight * tick, ExitCrossColor, outline * 1.1f);
            Segment(exitCenter + basisUp * tick - basisRight * tick,
                    exitCenter - basisUp * tick + basisRight * tick, ExitCrossColor, outline * 1.1f);
        }

        /// <summary>
        /// 两条机动轴：顺向 = 速度方向（轨道切线），径向 = 顺向旋转 90° 再指向行星外侧。
        /// 径向不能拿位置矢量 —— 椭圆轨道上速度与位置不垂直，十字会歪。
        /// </summary>
        static void GetAxes(ManeuverNodeData node, out Double2 prograde, out Double2 radial)
        {
            prograde = node.burnLocation.velocity;
            if (!NodeMath.IsFinite(prograde.x) || !NodeMath.IsFinite(prograde.y) || prograde.magnitude < 1e-6)
            {
                ScreenAxes(out prograde, out radial);
                return;
            }
            prograde = prograde.normalized;
            radial = new Double2(-prograde.y, prograde.x);
            if (Double2.Dot(radial, node.burnLocation.position) < 0.0)
            {
                radial = -radial;
            }
        }

        /// <summary>被抓手柄沿它的轴伸长多少像素（其它手柄为 0）。</summary>
        float StretchOf(GizmoHandle handle, Vector2 screenAxis)
        {
            if (ActiveHandle != handle)
            {
                return 0f;
            }
            return Mathf.Clamp(Vector2.Dot(dragPixels, screenAxis), -18f, 24f);
        }

        /// <summary>
        /// 一根杆 + 末端图案。杆顶到图案内缘（innerFactor 为该形状的内切比），留 8% 重叠保证连接。
        /// armDir 跟轨道切线走，basisUp/basisRight 是屏幕基 —— 图案用它生成，所以杆转图案不转。
        /// </summary>
        void DrawArm(Double2 origin, Double2 armDir, Double2 basisUp, Double2 basisRight,
            int sides, double rotation, double innerFactor,
            double baseRadius, float stretchPixels, double perPixel, Color color, bool active, float outline)
        {
            float armPixels = ArmPixels + stretchPixels;
            float scale = 1f + 0.30f * Mathf.Clamp(stretchPixels, -18f, 24f) / 24f;
            double arm = armPixels * perPixel;
            double handleRadius = baseRadius * scale;
            float width = outline * (active ? 1.35f : 1f);

            double barEnd = arm - handleRadius * innerFactor * 0.92;
            if (barEnd > 0.0)
            {
                Segment(origin, origin + armDir * barEnd, color, width);
            }

            DrawHandle(origin + armDir * arm, basisUp, basisRight, handleRadius, sides, rotation, color, width);
        }

        void DrawHandle(Double2 center, Double2 up, Double2 right, double radius,
            int sides, double rotation, Color color, float outline)
        {
            Polyline(Shape(center, up, right, radius, sides, rotation), color, outline, true);
        }

        /// <summary>按下时判断抓住了哪个手柄。</summary>
        internal GizmoHandle TryBeginDrag(Vector2 pixel, ManeuverNodeData node)
        {
            ActiveHandle = GizmoHandle.None;
            dragPixels = Vector2.zero;
            if (node == null || node.burnLocation == null)
            {
                return GizmoHandle.None;
            }
            if (!ScreenCenter(node, out Vector2 center))
            {
                return GizmoHandle.None;
            }

            GetAxes(node, out Double2 prograde, out Double2 radial);
            Vector2 up = ScreenDirection(prograde);
            Vector2 right = ScreenDirection(radial);
            Double2 exitMap = prograde + radial;
            Vector2 exitDir = exitMap.magnitude < 1e-9 ? up : ScreenDirection(exitMap);

            if ((pixel - (center + exitDir * ExitPixels)).magnitude <= ExitGrabPixels)
            {
                ActiveHandle = GizmoHandle.Exit;
            }
            else if ((pixel - center).magnitude <= CenterGrabPixels)
            {
                ActiveHandle = GizmoHandle.Center;
            }
            else if ((pixel - (center + up * ArmPixels)).magnitude <= GrabPixels)
            {
                ActiveHandle = GizmoHandle.Prograde;
            }
            else if ((pixel - (center - up * ArmPixels)).magnitude <= GrabPixels)
            {
                ActiveHandle = GizmoHandle.Retrograde;
            }
            else if ((pixel - (center + right * ArmPixels)).magnitude <= GrabPixels)
            {
                ActiveHandle = GizmoHandle.RadialOut;
            }
            else if ((pixel - (center - right * ArmPixels)).magnitude <= GrabPixels)
            {
                ActiveHandle = GizmoHandle.RadialIn;
            }
            return ActiveHandle;
        }

        internal void EndDrag()
        {
            ActiveHandle = GizmoHandle.None;
        }

        /// <summary>把这一帧拖拽换算成 ΔV / 滑时间。返回 true 表示需要重建预测。</summary>
        internal bool ApplyDrag(Vector2 dragDelta, Vector2 cursorPixel, ManeuverNodeData node)
        {
            if (!Dragging || node == null)
            {
                return false;
            }

            // InputManager.ApplyDrag 传的是 new DragData(-vector)，先还原成真实光标位移
            Vector2 cursorDelta = -dragDelta;
            dragPixels += cursorDelta;

            if (ActiveHandle == GizmoHandle.Center)
            {
                return SlideAlongOrbit(cursorPixel, node);
            }

            double scale = DVPerPixel;
            if (Held(KeyCode.LeftShift) || Held(KeyCode.RightShift))
            {
                scale *= 0.1;
            }
            if (Held(KeyCode.LeftControl) || Held(KeyCode.RightControl))
            {
                scale *= 10.0;
            }

            GetAxes(node, out Double2 prograde, out Double2 radial);

            if (ActiveHandle == GizmoHandle.Prograde || ActiveHandle == GizmoHandle.Retrograde)
            {
                double amount = Vector2.Dot(cursorDelta, ScreenDirection(prograde)) * scale;
                if (amount == 0.0)
                {
                    return false;
                }
                node.progradeDV = ClampDV(node.progradeDV + amount);
            }
            else if (ActiveHandle == GizmoHandle.RadialOut || ActiveHandle == GizmoHandle.RadialIn)
            {
                double amount = Vector2.Dot(cursorDelta, ScreenDirection(radial)) * scale;
                if (amount == 0.0)
                {
                    return false;
                }
                node.radialDV = ClampDV(node.radialDV + amount);
            }
            else
            {
                return false;
            }

            node.executed = false;
            return true;
        }

        /// <summary>
        /// 拖中心点沿轨道滑动节点。必须用瞬时角速度 dθ/dt = h/r²（h = r×v），
        /// 用平均角速度 2π/period 会在偏心轨道远拱点发散 → 节点飞速旋转。
        /// </summary>
        bool SlideAlongOrbit(Vector2 cursorPixel, ManeuverNodeData node)
        {
            Orbit orbit = node.sourceOrbit;
            if (orbit == null || orbit.Planet == null || Map.view == null || node.burnLocation == null)
            {
                return false;
            }
            float planeZ = (float)(Map.view.view.distance.Value / 1000.0);
            if (!(planeZ > 0f))
            {
                return false;
            }

            Vector2 world;
            try
            {
                world = new TouchPosition(cursorPixel).World(planeZ);
            }
            catch
            {
                return false;
            }
            Vector2 offset = world - (Vector2)orbit.Planet.mapHolder.position;
            if (offset.sqrMagnitude < 1e-12f)
            {
                return false;
            }
            double cursorAngle = Math.Atan2(offset.y, offset.x);

            Double2 r = node.burnLocation.position;
            Double2 v = node.burnLocation.velocity;
            double r2 = r.sqrMagnitude;
            if (!NodeMath.IsFinite(r2) || !(r2 > 1e-6) ||
                !NodeMath.IsFinite(v.x) || !NodeMath.IsFinite(v.y))
            {
                return false;
            }
            double h = r.x * v.y - r.y * v.x; // 有符号比角动量，km²/s
            if (!NodeMath.IsFinite(h) || Math.Abs(h) < 1e-9)
            {
                return false;
            }

            double now = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;
            double lowerBound = Math.Max(now + 0.5, orbit.orbitStartTime);

            double delta = NodeMath.NormalizeAngle(cursorAngle - r.AngleRadians);
            double dt = delta * r2 / h;

            double maxStep = NodeMath.IsFinite(orbit.period) && orbit.period > 0.0
                ? orbit.period * 0.25
                : 100000.0;
            if (!NodeMath.IsFinite(dt) || Math.Abs(dt) > maxStep)
            {
                dt = Math.Sign(dt) * maxStep;
                Debug.Log(string.Format(
                    "[ManeuverNode] slide clamped: dt -> {0} (period {1}, r {2}, h {3})",
                    dt.ToString("0.###e+00"), orbit.period.ToString("0.###e+00"),
                    Math.Sqrt(r2).ToString("0.###"), h.ToString("0.###")));
            }

            double newTime = node.burnTime + dt;
            if (!NodeMath.IsFinite(newTime))
            {
                Debug.LogWarning("[ManeuverNode] slide produced non-finite time, ignored");
                return false;
            }
            newTime = Math.Max(newTime, lowerBound);
            if (Math.Abs(newTime - node.burnTime) < 1e-4)
            {
                return false;
            }
            node.burnTime = newTime;
            node.executed = false;
            return true;
        }

        LineRenderer Take()
        {
            if (used >= pool.Length)
            {
                return null;
            }
            if (pool[used] == null)
            {
                Transform prefab = Map.solidLine != null ? Map.solidLine.linePrefab : null;
                if (prefab == null)
                {
                    return null;
                }
                LineRenderer created = UnityEngine.Object.Instantiate(prefab).GetComponent<LineRenderer>();
                if (created == null)
                {
                    return null;
                }
                created.gameObject.name = "ManeuverNode.Gizmo";
                created.sortingLayerName = "Map";
                created.sortingOrder = RenderOrder;
                created.textureMode = LineTextureMode.Stretch; // 否则共用材质会把短杆画成虚线
                created.useWorldSpace = false;
                created.numCapVertices = 0;
                created.gameObject.SetActive(false);
                pool[used] = created;
            }

            LineRenderer line = pool[used];
            used++;
            // 挂到 planet.mapHolder 并把 localPosition 归零，SetPositions 收到的
            // 行星本地坐标正好落在行星所在的地图层面（mapHolder.z = distance/1000）。
            Transform parent = planetHolder != null
                ? planetHolder
                : (Map.manager != null ? Map.manager.transform : null);
            line.transform.SetParent(parent, false);
            line.transform.localPosition = Vector3.zero;
            line.transform.localRotation = Quaternion.identity;
            line.transform.localScale = Vector3.one;
            if (!line.gameObject.activeSelf)
            {
                line.gameObject.SetActive(true);
            }
            return line;
        }

        void Segment(Double2 a, Double2 b, Color color, float width)
        {
            LineRenderer line = Take();
            if (line == null)
            {
                return;
            }
            line.loop = false;
            line.widthMultiplier = width;
            line.startColor = color;
            line.endColor = color;
            line.positionCount = 2;
            line.SetPosition(0, a.ToVector3);
            line.SetPosition(1, b.ToVector3);
        }

        void Polyline(Double2[] points, Color color, float width, bool loop)
        {
            if (points == null || points.Length < 2)
            {
                return;
            }
            LineRenderer line = Take();
            if (line == null)
            {
                return;
            }
            line.loop = loop;
            line.widthMultiplier = width;
            line.startColor = color;
            line.endColor = color;
            line.positionCount = points.Length;
            for (int i = 0; i < points.Length; i++)
            {
                line.SetPosition(i, points[i].ToVector3);
            }
        }

        /// <summary>正多边形顶点，角度按屏幕角解释（basis 为屏幕基，图案才不会跟着杆转）。</summary>
        static Double2[] Shape(Double2 center, Double2 up, Double2 right, double radius, int sides, double rotation)
        {
            Double2[] points = new Double2[sides];
            for (int i = 0; i < sides; i++)
            {
                double angle = rotation + i * (Math.PI * 2.0 / sides);
                points[i] = center + right * (Math.Cos(angle) * radius) + up * (Math.Sin(angle) * radius);
            }
            return points;
        }

        static double ClampDV(double v)
        {
            if (double.IsNaN(v))
            {
                return 0.0;
            }
            if (v > MaxDV)
            {
                return MaxDV;
            }
            return v < -MaxDV ? -MaxDV : v;
        }

        static bool Held(KeyCode code)
        {
            try
            {
                return Input.GetKey(code);
            }
            catch
            {
                return false;
            }
        }
    }
}
