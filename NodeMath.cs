using System;
using System.Collections.Generic;
using SFS;
using SFS.Parts;
using SFS.Parts.Modules;
using SFS.World;
using SFS.WorldBase;
using UnityEngine;

namespace SFSManeuverNode
{
    /// <summary>
    /// 单个机动节点的全部状态。<see cref="burnTime"/> 与两个 ΔV 是玩家可编辑的输入，
    /// 其余字段都是每次重建时推导出来的缓存。
    /// </summary>
    internal class ManeuverNodeData
    {
        /// <summary>点火时刻（绝对世界时间，单位秒）。</summary>
        public double burnTime;

        /// <summary>沿速度方向的 ΔV，正数 = 顺向加速，负数 = 逆向减速（m/s）。</summary>
        public double progradeDV;

        /// <summary>沿「行星中心 → 飞船」方向（径向）的 ΔV，正数 = 向外抬升（m/s）。</summary>
        public double radialDV;

        // ---- 以下为推导缓存 ----

        /// <summary>是否成功推导出预测轨迹。</summary>
        public bool valid;

        /// <summary>点火瞬间的实际状态。必须是自己 new 出来的副本，不能复用 Orbit.GetLocation 返回的内部对象。</summary>
        public Location burnLocation;

        /// <summary>世界坐标（行星参考系）下的脉冲矢量。</summary>
        public Double2 deltaV;

        /// <summary>点火后的预测轨迹，包含后续相遇/逃逸锥段。</summary>
        public Trajectory predicted;

        /// <summary>点火时刻所属的那条轨道（换算时间、拖动节点时要用）。</summary>
        public Orbit sourceOrbit;

        /// <summary>该节点在列表中的显示编号（1 起），仅用于界面。</summary>
        public int index;

        /// <summary>自动点火是否已经把这个节点执行完。</summary>
        public bool executed;

        /// <summary>总 ΔV。</summary>
        public double TotalDV
        {
            get
            {
                double p = progradeDV;
                double r = radialDV;
                return Math.Sqrt(p * p + r * r);
            }
        }

        /// <summary>点火时刻已经过去（相对当前世界时间）。</summary>
        public bool Expired
        {
            get
            {
                return WorldTime.main != null && burnTime <= WorldTime.main.worldTime;
            }
        }

        /// <summary>点火窗口的起点（中点对准点火，提前半个点火时长）。</summary>
        public double IgnitionTime(double burnDuration) => burnTime - burnDuration * 0.5;
    }

    internal static class NodeMath
    {
        /// <summary>SFS 内部的标准重力加速度，用于 ΔV = Isp·g0·ln(m0/m1)。</summary>
        internal const double Gravity0 = 9.8;

        /// <summary>创建 Location 的独立副本。</summary>
        internal static Location Copy(Location l)
        {
            if (l == null)
            {
                return null;
            }
            return new Location(l.time, l.planet, l.position, l.velocity);
        }

        internal static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>
        /// 该轨道能否安全地拿去画 / 当下游基准。极端偏心时 Kepler 解可能给出 NaN 半长轴，
        /// 传给渲染器会让 bounds 变 NaN → 相机剔除失效 → 整张地图黑屏，必须在 Build 阶段拒掉。
        /// </summary>
        internal static bool IsSane(Orbit orbit)
        {
            return orbit != null && orbit.Planet != null
                && IsFinite(orbit.periapsis) && orbit.periapsis > 0.0
                && IsFinite(orbit.sma)
                && IsFinite(orbit.ecc) && orbit.ecc >= 0.0;
        }

        /// <summary>找到覆盖指定时刻的锥段。</summary>
        internal static Orbit FindOrbitAt(Trajectory trajectory, double time)
        {
            if (trajectory == null || trajectory.paths == null || trajectory.paths.Count == 0)
            {
                return null;
            }
            Orbit last = null;
            Orbit first = null;
            for (int i = 0; i < trajectory.paths.Count; i++)
            {
                if (!(trajectory.paths[i] is Orbit orbit))
                {
                    continue;
                }
                if (first == null)
                {
                    first = orbit;
                }
                if (orbit.orbitStartTime <= time)
                {
                    last = orbit;
                }
                if (orbit.orbitStartTime <= time && time <= orbit.orbitEndTime)
                {
                    return orbit;
                }
            }
            return last ?? first;
        }

        /// <summary>预测轨迹里最后一条闭合轨道（用来读远/近点）；没有闭合轨道时退回最后一条锥段。</summary>
        internal static Orbit LastClosedOrbit(Trajectory trajectory)
        {
            if (trajectory == null || trajectory.paths == null)
            {
                return null;
            }
            Orbit fallback = null;
            for (int i = 0; i < trajectory.paths.Count; i++)
            {
                if (trajectory.paths[i] is Orbit orbit)
                {
                    fallback = orbit;
                    if (orbit.pathType == PathType.Eternal)
                    {
                        return orbit;
                    }
                }
            }
            return fallback;
        }

        /// <summary>
        /// 2D 游戏里只有两个有意义的机动轴：顺向/逆向（沿速度）与径向/反径向（沿地心连线）。
        /// KSP 的第三个法线轴在 2D 中恒等于轨道面法线，做不了任何事，因此这里不提供。
        /// </summary>
        internal static Double2 BurnVector(Location location, double progradeDV, double radialDV)
        {
            Double2 prograde = location.velocity.normalized;
            Double2 radial = location.position.normalized;
            return prograde * progradeDV + radial * radialDV;
        }

        /// <summary>在 baseline（点火前轨迹）上求解该节点，并把结果写回 node。</summary>
        internal static bool Build(Trajectory baseline, ManeuverNodeData node)
        {
            node.valid = false;
            node.predicted = null;
            node.sourceOrbit = null;
            node.burnLocation = null;

            try
            {
                Orbit orbit = FindOrbitAt(baseline, node.burnTime);
                if (orbit == null || orbit.Planet == null)
                {
                    return false;
                }
                Location raw = orbit.GetLocation(node.burnTime);
                if (raw == null || raw.planet == null)
                {
                    return false;
                }
                Location location = Copy(raw);

                Double2 dv = BurnVector(location, node.progradeDV, node.radialDV);
                Double2 postVelocity = location.velocity + dv;
                if (!IsFinite(location.position.x) || !IsFinite(location.position.y) ||
                    location.position.sqrMagnitude < 1e-6 ||
                    !IsFinite(location.velocity.x) || !IsFinite(location.velocity.y) ||
                    !IsFinite(location.position.x) || !IsFinite(postVelocity.x) || !IsFinite(postVelocity.y))
                {
                    Debug.LogWarning(string.Format(
                        "[ManeuverNode] node {0} rejected: non-finite state at t+{1:0.#}",
                        node.index, node.burnTime - (WorldTime.main != null ? WorldTime.main.worldTime : 0.0)));
                    return false;
                }
                // 脉冲把速度降到几乎为零时 Orbit.TryCreateOrbit 会直接失败，提前判掉以免抛异常
                if (postVelocity.magnitude < 0.1 && location.Radius < orbit.Planet.TimewarpRadius_Ascend)
                {
                    return false;
                }

                node.sourceOrbit = orbit;
                node.burnLocation = location;
                node.deltaV = dv;

                Location postBurn = new Location(node.burnTime, location.planet, location.position, postVelocity);
                node.predicted = Trajectory.CreateTrajectory(postBurn);
                node.valid = node.predicted != null && node.predicted.paths != null && node.predicted.paths.Count > 0;
                if (node.valid)
                {
                    // 预测锥段里只要有一条轨道是坏的（NaN 半长轴等），整个节点就作废，
                    // 否则画出去的 NaN 会把地图渲染整个带崩
                    for (int i = 0; i < node.predicted.paths.Count; i++)
                    {
                        if (node.predicted.paths[i] is Orbit o && !IsSane(o))
                        {
                            Debug.LogWarning(string.Format(
                                "[ManeuverNode] node {0} rejected: insane orbit #{1} (sma {2}, ecc {3}, pe {4})",
                                node.index, i,
                                o.sma.ToString("0.###e+00"), o.ecc.ToString("0.###e+00"),
                                o.periapsis.ToString("0.###e+00")));
                            node.valid = false;
                            break;
                        }
                    }
                }
                return node.valid;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ManeuverNode] node build failed: " + e.Message);
                node.valid = false;
                return false;
            }
        }

        /// <summary>
        /// 链式计算：第 n 个节点的基准轨迹是第 n-1 个节点的预测轨迹，这样就能像 KSP 一样在预测轨道上继续放后续节点。
        /// nodes 必须已按 burnTime 升序排好。
        /// </summary>
        internal static void RebuildAll(List<ManeuverNodeData> nodes, Trajectory baseline)
        {
            Trajectory current = baseline;
            for (int i = 0; i < nodes.Count; i++)
            {
                nodes[i].index = i + 1;
                if (current == null || !Build(current, nodes[i]))
                {
                    // 该节点失效，后面节点的基准也就不存在了
                    nodes[i].valid = false;
                    for (int j = i + 1; j < nodes.Count; j++)
                    {
                        nodes[j].valid = false;
                        nodes[j].index = j + 1;
                    }
                    return;
                }
                current = nodes[i].predicted;
            }
        }

        /// <summary>
        /// 把地图上一次点击换算成「离这条轨道最近的位置角」。点击坐标与 MapDrawer.GetPosition 同一坐标系。
        /// error 为地图坐标下的偏差，用来判断玩家是否真的点到了轨道附近。
        /// </summary>
        internal static double ClosestOrbitAngle(Orbit orbit, Vector2 clickWorld, out double error)
        {
            error = double.MaxValue;
            if (orbit == null || orbit.Planet == null)
            {
                return 0.0;
            }
            Vector2 local = clickWorld - (Vector2)orbit.Planet.mapHolder.position;
            return ClosestOrbitAngleLocal(orbit, local, out error);
        }

        internal static double ClosestOrbitAngleLocal(Orbit orbit, Vector2 local, out double error)
        {
            const int steps = 720;
            error = double.MaxValue;
            double best = 0.0;
            for (int i = 0; i < steps; i++)
            {
                double angle = i * (Math.PI * 2.0 / steps) - Math.PI;
                double radius = orbit.GetRadiusAtAngle(angle);
                if (!IsFinite(radius) || radius <= 0.0)
                {
                    continue;
                }
                double dx = Math.Cos(angle) * radius / 1000.0 - local.x;
                double dy = Math.Sin(angle) * radius / 1000.0 - local.y;
                double err = Math.Sqrt(dx * dx + dy * dy);
                if (err < error)
                {
                    error = err;
                    best = angle;
                }
            }
            return best;
        }

        /// <summary>
        /// 把一个位置角换算成「下一次经过该角度」的世界时间。逃逸轨道没有周期，
        /// GetNextAnglePassTime 可能给出过去的时间，此时退回单次通过时间再退回 now+30。
        /// </summary>
        internal static double TimeAtAngle(Orbit orbit, double angle)
        {
            double now = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;
            double from = Math.Max(now, orbit.orbitStartTime);
            double result;
            try
            {
                result = orbit.GetNextAnglePassTime(from, angle);
            }
            catch
            {
                return now + 30.0;
            }
            if (!IsFinite(result) || result <= now)
            {
                try
                {
                    result = orbit.GetLastAnglePassTime(from, angle);
                }
                catch
                {
                    result = double.NaN;
                }
                if (!IsFinite(result) || result <= now)
                {
                    result = now + 30.0;
                }
            }
            return result;
        }

        /// <summary>把角度规整到 (-π, π]。</summary>
        internal static double NormalizeAngle(double a)
        {
            while (a > Math.PI)
            {
                a -= Math.PI * 2.0;
            }
            while (a <= -Math.PI)
            {
                a += Math.PI * 2.0;
            }
            return a;
        }
    }

    /// <summary>当前级的推进能力估算，用于算点火时长与加速度。</summary>
    internal class Propulsion
    {
        /// <summary>推力合计，单位为游戏内的「吨力」（与 EngineModule.thrust 同口径）。</summary>
        internal double Thrust;

        /// <summary>推进剂质量流量（吨/秒）。</summary>
        internal double MassFlow;

        /// <summary>有效比冲（已乘上难度系数）。</summary>
        internal double Isp;

        internal bool Valid => Thrust > 0.0 && MassFlow > 0.0 && Isp > 0.0;

        /// <summary>ΔV = Isp·g0·ln(m0/m1) → 反解出点火时长。</summary>
        internal double BurnDuration(double deltaV, double mass)
        {
            if (!Valid || mass <= 0.0)
            {
                return -1.0;
            }
            double exhausted = mass * Math.Exp(-Math.Abs(deltaV) / (Isp * NodeMath.Gravity0));
            if (exhausted < 0.0 || double.IsNaN(exhausted))
            {
                exhausted = 0.0;
            }
            double burned = mass - exhausted;
            return burned / MassFlow;
        }

        /// <summary>当前加速度（m/s²）。游戏里 engine.thrust·9.8 就是推力（吨力→kN），除以吨位即得加速度。</summary>
        internal double Acceleration(double mass)
        {
            return mass > 0.0 ? Thrust * NodeMath.Gravity0 / mass : 0.0;
        }
    }

    internal static class PropulsionUtil
    {
        /// <summary>把整船正在工作的发动机汇总成一个推进能力模型。</summary>
        internal static Propulsion Get(Rocket rocket)
        {
            Propulsion result = new Propulsion();
            if (rocket == null || rocket.partHolder == null)
            {
                return result;
            }

            double multiplier = 1.0;
            try
            {
                multiplier = Base.worldBase.settings.difficulty.IspMultiplier;
            }
            catch
            {
                multiplier = 1.0;
            }
            if (multiplier <= 0.0)
            {
                multiplier = 1.0;
            }

            double sumThrust = 0.0;
            double sumFlow = 0.0;
            try
            {
                EngineModule[] engines = rocket.partHolder.GetModules<EngineModule>();
                for (int i = 0; i < engines.Length; i++)
                {
                    EngineModule engine = engines[i];
                    if (engine == null || engine.engineOn == null || !engine.engineOn.Value)
                    {
                        continue;
                    }
                    double thrust = engine.thrust.Value;
                    double isp = engine.ISP.Value * multiplier;
                    if (thrust <= 0.0 || isp <= 0.0)
                    {
                        continue;
                    }
                    sumThrust += thrust;
                    // 与 EngineModule.RecalculateMassFlow 完全相同的流量口径
                    sumFlow += thrust / isp;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ManeuverNode] propulsion probe failed: " + e.Message);
            }

            result.Thrust = sumThrust;
            result.MassFlow = sumFlow;
            result.Isp = sumFlow > 0.0 ? sumThrust / sumFlow : 0.0;
            return result;
        }

        /// <summary>
        /// 统计已点火的发动机（active / total）。只观察、不点火：
        /// partHolder 含所有级的发动机，代玩家置 engineOn 会把所有级一起点燃（火箭会被拉爆）。
        /// </summary>
        internal static int CountActiveEngines(Rocket rocket, out int total)
        {
            total = 0;
            int active = 0;
            if (rocket == null || rocket.partHolder == null)
            {
                return 0;
            }
            try
            {
                EngineModule[] engines = rocket.partHolder.GetModules<EngineModule>();
                for (int i = 0; i < engines.Length; i++)
                {
                    EngineModule engine = engines[i];
                    if (engine == null || engine.thrust == null || engine.thrust.Value <= 0f)
                    {
                        continue;
                    }
                    total++;
                    if (engine.engineOn != null && engine.engineOn.Value)
                    {
                        active++;
                    }
                }
            }
            catch
            {
                // 拿不到就当成没有
            }
            return active;
        }

        /// <summary>当前是否至少有一台发动机处于点火状态。</summary>
        internal static bool HasActiveEngine(Rocket rocket)
        {
            return CountActiveEngines(rocket, out _) > 0;
        }

        /// <summary>整船剩余燃料百分比（各资源组平均），拿不到时返回 -1。</summary>
        internal static double FuelPercent(Rocket rocket)
        {
            try
            {
                if (rocket == null || rocket.resources == null)
                {
                    return -1.0;
                }
                ResourceModule[] groups = rocket.resources.globalGroups;
                if (groups == null || groups.Length == 0)
                {
                    return -1.0;
                }
                double sum = 0.0;
                for (int i = 0; i < groups.Length; i++)
                {
                    sum += groups[i].resourcePercent.Value;
                }
                return sum / groups.Length;
            }
            catch
            {
                return -1.0;
            }
        }
    }
}
