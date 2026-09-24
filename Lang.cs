using System;

namespace SFSManeuverNode
{
    /// <summary>
    /// 界面文案只提供英文（纯 ASCII）：游戏字体没有完整 CJK 字形，中文会被替换成方块，
    /// 所以不做语言判定，直接返回英文。
    /// </summary>
    internal static class Lang
    {
        /// <summary>保留给调用方（ManeuverNodeMod.Load），语言判定已不需要。</summary>
        internal static void Invalidate()
        {
        }

        /// <summary>所有用户可见文案统一走这里，方便以后整体换语言。</summary>
        internal static string T(string en) => en ?? string.Empty;

        // ------------------------------------------------------------------
        // 通用格式化（尽量只用 ASCII，避免字体缺字）
        // ------------------------------------------------------------------

        /// <summary>倒计时 / 时长，形如 00:12:34 或 3d 04:05:06。</summary>
        internal static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            {
                return "--:--";
            }
            bool negative = seconds < 0;
            seconds = Math.Abs(seconds);
            int days = (int)(seconds / 86400.0);
            seconds -= days * 86400.0;
            int hours = (int)(seconds / 3600.0);
            seconds -= hours * 3600.0;
            int minutes = (int)(seconds / 60.0);
            int secs = (int)(seconds - minutes * 60.0);
            string body = days > 0
                ? string.Format("{0}d {1:00}:{2:00}:{3:00}", days, hours, minutes, secs)
                : string.Format("{0:00}:{1:00}:{2:00}", hours, minutes, secs);
            return (negative ? "-" : "") + body;
        }

        /// <summary>把「相对行星表面高度」格式化成 km / m。</summary>
        internal static string Height(double metres)
        {
            if (double.IsNaN(metres) || double.IsInfinity(metres))
            {
                return "--";
            }
            double abs = Math.Abs(metres);
            if (abs >= 1000.0)
            {
                return (metres / 1000.0).ToString("0.##") + " km";
            }
            return metres.ToString("0.#") + " m";
        }

        internal static string Velocity(double metresPerSecond)
        {
            if (double.IsNaN(metresPerSecond) || double.IsInfinity(metresPerSecond))
            {
                return "--";
            }
            return metresPerSecond.ToString("0.#") + " m/s";
        }

        /// <summary>加速度。用 "m/s2" 而不是 "m/s²"，上标字符同样可能缺字。</summary>
        internal static string Acceleration(double metresPerSecondSquared)
        {
            if (double.IsNaN(metresPerSecondSquared) || double.IsInfinity(metresPerSecondSquared))
            {
                return "--";
            }
            return metresPerSecondSquared.ToString("0.##") + " m/s2";
        }
    }
}
