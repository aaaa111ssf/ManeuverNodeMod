using System;
using System.Collections.Generic;
using SFS.World;
using UnityEngine;

namespace SFSManeuverNode
{
    /// <summary>自动执行所处的阶段，仅用于界面提示。</summary>
    internal enum BurnPhase
    {
        Idle,
        Warping,
        Holding,
        Burning,
        Complete
    }

    /// <summary>
    /// 「一键加速到节点 / 自动对准 / 自动点火 / 自动执行」的流水线。
    /// 整个过程是：轨道加速 → 物理模式对准 → 等待点火窗口 → 闭环点火 → 关机。
    /// </summary>
    internal class BurnControl
    {
        internal BurnPhase Phase = BurnPhase.Idle;

        /// <summary>正在做轨道时间加速。</summary>
        internal bool Warping;

        /// <summary>保持姿态对准点火方向（自动对准）。</summary>
        internal bool HoldAttitude;

        /// <summary>到点自动点火（自动点火）。</summary>
        internal bool AutoBurn;

        /// <summary>一键跑完整流程。</summary>
        internal bool Executing;

        /// <summary>正在点火。</summary>
        internal bool Burning;

        /// <summary>本次点火已经累计的 ΔV（m/s）。</summary>
        internal double AccumulatedDV;

        /// <summary>轨道加速的目标世界时间。</summary>
        internal double WarpEndTime;

        /// <summary>最近一次姿态误差（度），用于界面显示。</summary>
        internal double AlignError;

        internal bool Aligned;

        /// <summary>点火窗口提前量：在「点火时刻」前这么久结束加速，留出对准时间。</summary>
        internal double AlignLead = 5.0;

        /// <summary>最近一次失败/提示信息，显示在窗口里（英文）。</summary>
        internal string Message = string.Empty;

        Propulsion prop = new Propulsion();
        double propRefreshTime = double.NegativeInfinity;
        double burnStartedTime = double.NegativeInfinity;

        /// <summary>本次加速允许的最大倍速（按剩余 Δt 估算），收尾时逐帧往下压。</summary>
        double warpMaxSpeed = 1.0;

        // 点火前的油门设定，关机时还原，避免把玩家的油门顺手清零
        float savedThrottlePercent = 1f;
        bool savedThrottleOn = true;
        bool throttleSaved;

        readonly NodeManager owner;

        internal BurnControl(NodeManager owner)
        {
            this.owner = owner;
        }

        internal Propulsion Propulsion => prop;

        internal bool Busy => Warping || HoldAttitude || AutoBurn || Executing || Burning;

        // ------------------------------------------------------------------
        // 对外操作
        // ------------------------------------------------------------------

        internal void ToggleWarp()
        {
            if (Warping)
            {
                StopWarp();
                Phase = BurnPhase.Idle;
                return;
            }
            Rocket rocket = owner.Rocket;
            ManeuverNodeData node = owner.Selected;
            if (rocket == null || node == null || !node.valid)
            {
                return;
            }
            RefreshPropulsion(rocket, true);
            BeginWarp(node, HalfBurnTime(node, rocket));
        }

        internal void ToggleAlign()
        {
            HoldAttitude = !HoldAttitude;
            UpdatePhase();
        }

        internal void ToggleAutoBurn()
        {
            AutoBurn = !AutoBurn;
            UpdatePhase();
        }

        internal void ToggleExecute()
        {
            if (Executing)
            {
                Abort();
                return;
            }
            Rocket rocket = owner.Rocket;
            ManeuverNodeData node = owner.Selected;
            if (rocket == null || node == null || !node.valid)
            {
                return;
            }
            Executing = true;
            AutoBurn = true;
            HoldAttitude = true;
            AccumulatedDV = 0.0;
            RefreshPropulsion(rocket, true);
            BeginWarp(node, HalfBurnTime(node, rocket));
            UpdatePhase();
        }

        internal void Abort()
        {
            Rocket rocket = owner.Rocket;
            if (Burning)
            {
                RestoreThrottle(rocket);
            }
            Warping = false;
            Executing = false;
            AutoBurn = false;
            HoldAttitude = false;
            Burning = false;
            Phase = BurnPhase.Idle;
            Message = string.Empty;
            if (WorldTime.main != null && !WorldTime.main.realtimePhysics.Value)
            {
                WorldTime.main.StopTimewarp(false);
            }
        }

        /// <summary>节点被删除 / 火箭没了 / 场景切换时的兜底清理。</summary>
        internal void StopEverything()
        {
            Rocket rocket = owner.Rocket;
            if (Burning)
            {
                RestoreThrottle(rocket);
            }
            if (Warping && WorldTime.main != null && !WorldTime.main.realtimePhysics.Value)
            {
                WorldTime.main.StopTimewarp(false);
            }
            Warping = false;
            Executing = false;
            AutoBurn = false;
            HoldAttitude = false;
            Burning = false;
            Phase = BurnPhase.Idle;
        }

        internal void OnSelectedChanged()
        {
            Abort();
            Phase = BurnPhase.Idle;
        }

        // ------------------------------------------------------------------
        // 每帧逻辑
        // ------------------------------------------------------------------

        internal void Tick()
        {
            Rocket rocket = owner.Rocket;
            ManeuverNodeData node = owner.Selected;
            if (WorldTime.main == null || rocket == null || node == null || !node.valid)
            {
                if (Busy)
                {
                    StopEverything();
                }
                return;
            }

            double now = WorldTime.main.worldTime;
            RefreshPropulsion(rocket, false);

            double mass = GetMass(rocket);
            double burnDuration = prop.BurnDuration(node.TotalDV, mass);
            double halfBurn = burnDuration > 0.0 ? burnDuration * 0.5 : 0.0;
            double ignition = node.burnTime - halfBurn;

            // ---- 轨道加速：边走边调速度，保证精准停在点火窗口前 ----
            if (Warping)
            {
                if (WorldTime.main.realtimePhysics.Value)
                {
                    StopWarp();
                    UpdatePhase();
                }
                else
                {
                    AdaptWarpSpeed(now);
                    if (now >= WarpEndTime - 0.02)
                    {
                        StopWarp();
                        UpdatePhase();
                    }
                }
            }

            // ---- 一键执行编排：没到窗口就加速，到了窗口就交给自动点火 ----
            if (Executing && !Warping && !Burning)
            {
                if (now < ignition - AlignLead)
                {
                    BeginWarp(node, halfBurn);
                    UpdatePhase();
                }
                else
                {
                    AutoBurn = true;
                    HoldAttitude = true;
                    UpdatePhase();
                }
            }

            // ---- 点火 ----
            if (Burning)
            {
                TickBurn(rocket, node, now);
            }
            else if (AutoBurn)
            {
                // 允许在窗口内点火；窗口外太早就不动，超过 60 秒判定为错过
                if (now >= ignition && now <= node.burnTime + 60.0)
                {
                    BeginBurn(rocket, node);
                }
                else if (now > node.burnTime + 60.0 && !Executing)
                {
                    AutoBurn = false;
                    UpdatePhase();
                }
            }
        }

        void TickBurn(Rocket rocket, ManeuverNodeData node, double now)
        {
            // 累计 Δv（逐物理步，见 FixedTick 里的调用）
            double fuel = PropulsionUtil.FuelPercent(rocket);
            bool outOfFuel = fuel >= 0.0 && fuel <= 0.0005;
            bool noThrust = !prop.Valid && now > burnStartedTime + 1.5;
            bool railsMode = WorldTime.main != null && !WorldTime.main.realtimePhysics.Value;
            bool complete = AccumulatedDV >= node.TotalDV - Math.Max(0.05, node.TotalDV * 0.004);
            bool timeout = now > node.burnTime + 900.0;

            if (complete || outOfFuel || noThrust || railsMode || timeout)
            {
                EndBurn(rocket, node, complete);
            }
        }

        internal void FixedTick()
        {
            Rocket rocket = owner.Rocket;
            if (rocket == null || rocket.rb2d == null || WorldTime.main == null)
            {
                return;
            }

            // 累计 Δv
            if (Burning && prop.Valid)
            {
                double mass = GetMass(rocket);
                AccumulatedDV += prop.Acceleration(mass) * WorldTime.FixedDeltaTime;
            }

            // 姿态保持
            if (!HoldAttitude || !WorldTime.main.realtimePhysics.Value)
            {
                Aligned = false;
                return;
            }

            ManeuverNodeData node = owner.Selected;
            if (node == null || !node.valid)
            {
                Aligned = false;
                return;
            }

            Location location = rocket.location.Value;
            if (location == null || location.planet == null)
            {
                return;
            }

            // 目标方向：把「顺向 Δv 系数 + 径向 Δv 系数」套用到当前的位置/速度上，
            // 这样点火过程中方向会跟着轨道一起转，误差比固定世界矢量小得多。
            Double2 dir = NodeMath.BurnVector(location, node.progradeDV, node.radialDV);
            if (dir.magnitude < 1e-6)
            {
                Aligned = false;
                return;
            }

            float target = (float)(Math.Atan2(dir.y, dir.x) * 180.0 / Math.PI);
            float current = rocket.GetRotation();
            float error = Mathf.DeltaAngle(current, target);

            // 直接给刚体设定姿态（等效于无限力矩 SAS），保证对准一定收敛。
            rocket.rb2d.MoveRotation(rocket.rb2d.rotation + error);
            rocket.rb2d.angularVelocity = 0f;

            AlignError = error;
            Aligned = Math.Abs(error) < 0.5f;
        }

        // ------------------------------------------------------------------
        // 内部实现
        // ------------------------------------------------------------------

        void BeginWarp(ManeuverNodeData node, double halfBurn)
        {
            if (WorldTime.main == null || Warping)
            {
                return;
            }
            double now = WorldTime.main.worldTime;
            double target = node.burnTime - halfBurn - AlignLead;
            double delta = target - now;
            if (delta < 2.0)
            {
                return; // 已经进入窗口，不需要加速
            }
            if (!WorldTime.CanTimewarp(false, false, out bool isInWater) || isInWater)
            {
                // 之前这里静默返回，玩家只会看到"点了 Execute 却原地不动"
                Message = "Cannot timewarp now - throttle / atmosphere / water.";
                return;
            }

            // 总时长沿用旧的经验公式（real ≈ 2 + log10(delta) 秒），
            // 但收尾精度交给 AdaptWarpSpeed 每帧重算。
            warpMaxSpeed = Clamp(delta / (2.0 + Math.Log10(delta)), 2.0, WorldTime.MaxTimewarpSpeed);
            WorldTime.main.SetState(warpMaxSpeed, false, false);
            WarpEndTime = target;
            Warping = true;
            Message = string.Empty;
            Phase = BurnPhase.Warping;
        }

        /// <summary>
        /// 边加速边调速度：一帧最多走剩余时间的 1/4，越接近终点越慢，稳稳停在点火窗口前。
        /// 倍速一次性定死的话，1000× 时一帧就能跨 16.7 秒游戏时间，很容易跨过整个窗口。
        /// </summary>
        void AdaptWarpSpeed(double now)
        {
            if (WorldTime.main == null)
            {
                return;
            }
            double remaining = WarpEndTime - now;
            if (remaining <= 0.0)
            {
                return;
            }
            double frame = Math.Max(0.01, Time.unscaledDeltaTime);
            double allowed = Math.Max(0.05, remaining * 0.25);
            double speed = Clamp(allowed / frame, 1.0, Math.Max(1.0, warpMaxSpeed));
            try
            {
                WorldTime.main.SetState(speed, false, false);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ManeuverNode] adapt warp speed failed: " + e.Message);
            }
        }

        void StopWarp()
        {
            Warping = false;
            if (WorldTime.main != null)
            {
                WorldTime.main.StopTimewarp(false);
            }
        }

        void BeginBurn(Rocket rocket, ManeuverNodeData node)
        {
            // 轨道加速（rails）模式下点火是无效的：先把时间加速停掉，进入物理模式
            if (WorldTime.main != null && !WorldTime.main.realtimePhysics.Value)
            {
                WorldTime.main.StopTimewarp(false);
                Warping = false;
            }

            // 只推油门，绝不代玩家点火。
            // partHolder 里包含所有级的发动机，以前那把「把所有 engineOn 置 true」会把整支
            // 火箭连同上面几级一起点燃 —— 玩家反馈的火箭被烧毁就是这里造成的。
            // 现在如果没有任何一台发动机已经点火，就明确报错并退出自动流程。
            if (!PropulsionUtil.HasActiveEngine(rocket))
            {
                Message = "No engine lit - activate a stage first (Space).";
                AutoBurn = false;
                Executing = false;
                HoldAttitude = false;
                Phase = BurnPhase.Idle;
                return;
            }

            Burning = true;
            AccumulatedDV = 0.0;
            burnStartedTime = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;
            HoldAttitude = true;
            Message = string.Empty;
            PushThrottle(rocket);
            Phase = BurnPhase.Burning;
        }

        void EndBurn(Rocket rocket, ManeuverNodeData node, bool complete)
        {
            Burning = false;
            AutoBurn = false;
            HoldAttitude = false;
            RestoreThrottle(rocket);
            if (complete)
            {
                node.executed = true;
            }
            else
            {
                Message = Lang.T("Burn stopped early - check fuel / engine");
            }

            // 一键执行：这个节点烧完了，还有后续节点就接着跑 —— 中间的时间加速也由流水线负责
            if (Executing && complete)
            {
                ManeuverNodeData next = FindNextExecutableNode(node);
                if (next != null)
                {
                    // Select 会触发 OnSelectedChanged → Abort（清空所有标志），所以要重新置位
                    owner.Select(next);
                    Executing = true;
                    AutoBurn = true;
                    HoldAttitude = true;
                    AccumulatedDV = 0.0;
                    RefreshPropulsion(rocket, true);
                    Message = string.Empty;
                    UpdatePhase(); // Tick 会接着编排：加速 → 对准 → 点火
                    owner.NotifyBurnFinished(node, complete);
                    return;
                }
            }

            Executing = false;
            Phase = BurnPhase.Complete;
            owner.NotifyBurnFinished(node, complete);
        }

        /// <summary>找「比 <paramref name="done"/> 晚、还没执行」的最近一个节点。</summary>
        ManeuverNodeData FindNextExecutableNode(ManeuverNodeData done)
        {
            ManeuverNodeData best = null;
            IReadOnlyList<ManeuverNodeData> all = owner.Nodes;
            for (int i = 0; i < all.Count; i++)
            {
                ManeuverNodeData node = all[i];
                if (node == done || !node.valid || node.executed)
                {
                    continue;
                }
                if (node.burnTime <= done.burnTime)
                {
                    continue;
                }
                if (best == null || node.burnTime < best.burnTime)
                {
                    best = node;
                }
            }
            return best;
        }

        /// <summary>把油门推到 100%。第一次推之前先记住玩家原本的设定。</summary>
        void PushThrottle(Rocket rocket)
        {
            if (rocket == null || rocket.throttle == null)
            {
                return;
            }
            if (!throttleSaved)
            {
                savedThrottlePercent = rocket.throttle.throttlePercent.Value;
                savedThrottleOn = rocket.throttle.throttleOn.Value;
                throttleSaved = true;
            }
            rocket.throttle.throttlePercent.Value = 1f;
            rocket.throttle.throttleOn.Value = true;
        }

        /// <summary>还原玩家原本的油门，而不是粗暴地写成 0。</summary>
        void RestoreThrottle(Rocket rocket)
        {
            if (!throttleSaved)
            {
                return;
            }
            throttleSaved = false;
            if (rocket == null || rocket.throttle == null)
            {
                return;
            }
            rocket.throttle.throttlePercent.Value = savedThrottlePercent;
            rocket.throttle.throttleOn.Value = savedThrottleOn;
        }

        double HalfBurnTime(ManeuverNodeData node, Rocket rocket)
        {
            double duration = prop.BurnDuration(node.TotalDV, GetMass(rocket));
            return duration > 0.0 ? duration * 0.5 : 0.0;
        }

        void RefreshPropulsion(Rocket rocket, bool force)
        {
            double now = WorldTime.main != null ? WorldTime.main.worldTime : 0.0;
            // 空闲时没必要频繁统计发动机，但界面一直在显示点火时长，也不能完全不刷新
            double interval = Busy ? 0.15 : 0.5;
            if (force || now - propRefreshTime > interval || now < propRefreshTime)
            {
                prop = PropulsionUtil.Get(rocket);
                propRefreshTime = now;
            }
        }

        void UpdatePhase()
        {
            if (Burning)
            {
                Phase = BurnPhase.Burning;
            }
            else if (Warping)
            {
                Phase = BurnPhase.Warping;
            }
            else if (HoldAttitude || AutoBurn || Executing)
            {
                Phase = BurnPhase.Holding;
            }
            else
            {
                Phase = BurnPhase.Idle;
            }
        }

        internal static double GetMass(Rocket rocket)
        {
            try
            {
                return rocket != null && rocket.mass != null ? rocket.mass.GetMass() : 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        internal static double Clamp(double v, double min, double max)
        {
            if (v < min)
            {
                return min;
            }
            return v > max ? max : v;
        }
    }
}
