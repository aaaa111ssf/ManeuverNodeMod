using System;
using SFS.UI;
using SFS.World.Maps;
using UnityEngine;
using ModButton = SFS.UI.ModGUI.Button;
using ModBuilder = SFS.UI.ModGUI.Builder;

namespace SFSManeuverNode
{
    /// <summary>
    /// 在原生「加速到此处」按钮正下方挂一个「Create node」按钮（挂在同一个 menuHolder 下），
    /// 每帧按原生按钮的世界包围盒重算位置，menuHolder 隐藏时跟着隐藏。
    /// </summary>
    internal class NativeMenuButton : MonoBehaviour
    {
        const float GapPixels = 8f;
        const string Label = "Create node";

        readonly Vector3[] corners = new Vector3[4];

        TimewarpTo owner;
        ButtonPC nativeButton;
        ModButton button;
        bool failed;

        void Update()
        {
            if (failed)
            {
                return;
            }
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                // 原生菜单结构万一和预期不同，就安静地放弃这个附加按钮，别每帧刷错误
                failed = true;
                Hide();
                Debug.LogWarning("[ManeuverNode] create-node button unavailable: " + e.Message);
            }
        }

        void Tick()
        {
            TimewarpTo current = Map.manager != null ? Map.manager.timewarpTo : null;
            if (current == null || current.menuHolder == null)
            {
                owner = null;
                Hide();
                return;
            }

            if (owner != current)
            {
                owner = current;
                nativeButton = current.menuHolder.GetComponentInChildren<ButtonPC>(true);
                if (button != null && button.gameObject != null)
                {
                    UnityEngine.Object.Destroy(button.gameObject);
                    button = null;
                }
            }
            if (nativeButton == null || nativeButton.transform == null)
            {
                Hide();
                return;
            }

            if (button == null || button.gameObject == null)
            {
                CreateButton();
            }
            if (button == null || button.gameObject == null)
            {
                return;
            }

            bool visible = owner.menuHolder.gameObject.activeInHierarchy;
            if (button.gameObject.activeSelf != visible)
            {
                button.gameObject.SetActive(visible);
            }
            if (visible)
            {
                PlaceBelowNative();
            }
        }

        void CreateButton()
        {
            RectTransform nativeRect = nativeButton.GetComponent<RectTransform>();
            if (nativeRect == null)
            {
                return;
            }
            nativeRect.GetWorldCorners(corners);
            float worldWidth = Mathf.Abs(corners[2].x - corners[0].x);
            float worldHeight = Mathf.Abs(corners[1].y - corners[0].y);
            if (worldWidth < 1f || worldHeight < 1f)
            {
                return;
            }

            Vector3 scale = owner.menuHolder.lossyScale;
            int width = Mathf.Max(90, Mathf.RoundToInt(worldWidth / Mathf.Max(0.0001f, Mathf.Abs(scale.x))));
            int height = Mathf.Max(26, Mathf.RoundToInt(worldHeight / Mathf.Max(0.0001f, Mathf.Abs(scale.y))));

            button = ModBuilder.CreateButton(owner.menuHolder, width, height, 0, 0, OnClicked, Label);
            if (button == null || button.gameObject == null)
            {
                return;
            }
            button.gameObject.name = "ManeuverNode.CreateNodeButton";
            button.gameObject.SetActive(false);
        }

        void PlaceBelowNative()
        {
            RectTransform nativeRect = nativeButton.GetComponent<RectTransform>();
            RectTransform mine = button.rectTransform;
            if (nativeRect == null || mine == null)
            {
                return;
            }

            nativeRect.GetWorldCorners(corners);
            float bottom = Mathf.Min(corners[0].y, corners[3].y);
            float centerX = (corners[0].x + corners[2].x) * 0.5f;

            // 自己的世界高度 = 本地高度 × 父级缩放
            float myWorldHeight = Mathf.Abs(mine.rect.height * mine.lossyScale.y);
            if (myWorldHeight < 1f)
            {
                myWorldHeight = Mathf.Abs(mine.sizeDelta.y * mine.lossyScale.y);
            }

            float y = bottom - GapPixels - myWorldHeight * 0.5f;
            mine.position = new Vector3(centerX, y, nativeRect.position.z);
        }

        void OnClicked()
        {
            NodeManager manager = NodeManager.main;
            if (manager != null)
            {
                manager.CreateNodeFromMapSelection();
            }
        }

        void Hide()
        {
            if (button != null && button.gameObject != null && button.gameObject.activeSelf)
            {
                button.gameObject.SetActive(false);
            }
        }
    }
}
