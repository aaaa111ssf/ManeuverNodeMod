using System;
using System.Globalization;
using SFS.UI.ModGUI;
using UnityEngine;
using GUIElement = SFS.UI.ModGUI.GUIElement;
using Type = SFS.UI.ModGUI.Type;
#if UITOOLS
using UITools;
#endif

namespace SFSManeuverNode
{
    /// <summary>
    /// 数值输入控件。编译时若检测到 UITools 就使用它的 NumberInput（自带 &lt; &gt; 微调按钮），
    /// 否则退化成自建的「按钮 + 输入框 + 按钮」组合，功能一致。
    /// </summary>
    internal class NumberField
    {
        /// <summary>放在布局里的根元素。</summary>
        internal GUIElement Root;

        /// <summary>微调按钮的步长。</summary>
        internal float Step = 1f;

        readonly Action<float> onValueChanged;
        readonly float minValue;
        readonly float maxValue;

        float current;
        bool suppress;

#if UITOOLS
        NumberInput ui;
#else
        TextInput ui;
#endif

        internal float Value
        {
            get => current;
            set => SetValue(value, false);
        }

        internal NumberField(Transform parent, int width, int height, float initial, float step,
                             Action<float> onValueChanged, float minValue = -1e9f, float maxValue = 1e9f)
        {
            this.onValueChanged = onValueChanged;
            this.minValue = minValue;
            this.maxValue = maxValue;
            Step = step;
            current = Clamp(initial);

#if UITOOLS
            ui = UIToolsBuilder.CreateNumberInput(parent, width, height, current, step);
            ui.OnValueChangedEvent += HandleUiChange;
            Root = ui;
#else
            Container row = Builder.CreateContainer(parent);
            row.Size = new Vector2(width, height);
            row.CreateLayoutGroup(Type.Horizontal, TextAnchor.MiddleCenter, 4f);

            Builder.CreateButton(row, 30, height, 0, 0, () => Nudge(-1f), "-");
            ui = Builder.CreateTextInput(row, width - 68, height, 0, 0, Format(current), HandleTextChange);
            Builder.CreateButton(row, 30, height, 0, 0, () => Nudge(1f), "+");
            Root = row;
#endif

            Root.Size = new Vector2(width, height);
        }

        void Nudge(float direction) => SetValue(current + Step * direction, true);

        void HandleUiChange(float v) => SetValue(v, true);

        void HandleTextChange(string text)
        {
            if (suppress || string.IsNullOrEmpty(text))
            {
                return;
            }
            if (text.EndsWith(".", StringComparison.Ordinal) || text == "-" ||
                text.EndsWith("+", StringComparison.Ordinal) || text.EndsWith("e", StringComparison.OrdinalIgnoreCase))
            {
                return; // 正在输入中间态，别急着解析
            }
            if (float.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out float parsed))
            {
                SetValue(parsed, true);
            }
        }

        void SetValue(float v, bool fromUi)
        {
            v = Clamp(v);
            bool changed = Math.Abs(v - current) > 1e-6f;
            current = v;

            // 来自界面的改动不用回写控件（会打断正在输入的光标）
            if (!fromUi)
            {
                suppress = true;
                try
                {
#if UITOOLS
                    ui.Value = v;
#else
                    ui.Text = Format(v);
#endif
                }
                finally
                {
                    suppress = false;
                }
            }

            if (changed)
            {
                onValueChanged?.Invoke(v);
            }
        }

        float Clamp(float v)
        {
            if (float.IsNaN(v))
            {
                return current;
            }
            if (v < minValue)
            {
                return minValue;
            }
            return v > maxValue ? maxValue : v;
        }

        internal static string Format(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
