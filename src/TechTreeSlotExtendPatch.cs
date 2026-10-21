using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TransmutationTable
{
    /// <summary>
    /// 原版 UITechNode / UITechTip 的「解锁配方」图标槽位是预制体里固定的 3 个
    /// （原版科技最多 3 条配方，图标框宽度公式也只算到 Min(3, len)）。
    /// 我们把第 4 条配方追加进原版科技后，索引 ≥3 的配方永远显示不出来——
    /// 这就是「原版科技完全没变化」的根因。
    /// 本补丁在刷新前动态克隆图标/按钮槽位扩容到实际配方数，并把图标框撑宽。
    /// </summary>
    [HarmonyPatch]
    internal static class TechTreeSlotExtendPatch
    {
        private static readonly FieldInfo OnClickField = AccessTools.Field(typeof(UIButton), "onClick");

        /// <summary>克隆扩展槽位；返回新数组（未扩容时原样返回）</summary>
        private static void EnsureSlots(TechProto tech, Image[] icons, UIButton[] buttons,
            out Image[] newIcons, out UIButton[] newButtons)
        {
            newIcons = icons;
            newButtons = buttons;
            int need = tech?.unlockRecipeArray?.Length ?? 0;
            if (need <= 0 || icons == null || buttons == null) return;
            int have = Math.Min(icons.Length, buttons.Length);
            if (need <= have) return;

            var ni = new Image[need];
            var nb = new UIButton[need];
            for (int i = 0; i < have; i++) { ni[i] = icons[i]; nb[i] = buttons[i]; }

            Image srcIcon = icons[have - 1];
            UIButton srcBtn = buttons[have - 1];
            var srcIconPos = srcIcon.rectTransform.anchoredPosition;
            var srcBtnRect = srcBtn.GetComponent<RectTransform>();
            var srcBtnPos = srcBtnRect != null ? srcBtnRect.anchoredPosition : Vector2.zero;

            for (int i = have; i < need; i++)
            {
                float dx = 46f * (i - have + 1);

                var img = UnityEngine.Object.Instantiate(srcIcon, srcIcon.transform.parent);
                img.name = srcIcon.name + "_x" + i;
                img.rectTransform.anchoredPosition = srcIconPos + new Vector2(dx, 0f);
                img.gameObject.SetActive(true);
                ni[i] = img;

                var go = UnityEngine.Object.Instantiate(srcBtn.gameObject, srcBtn.transform.parent);
                go.name = srcBtn.name + "_x" + i;
                var rt = go.GetComponent<RectTransform>();
                if (rt != null) rt.anchoredPosition = srcBtnPos + new Vector2(dx, 0f);
                go.SetActive(true);
                var btn = go.GetComponent<UIButton>();
                // 克隆不携带 C# 事件订阅（委托不序列化），从源按钮复制点击处理器
                try
                {
                    var srcHandler = OnClickField?.GetValue(srcBtn) as Delegate;
                    if (srcHandler != null) OnClickField.SetValue(btn, srcHandler);
                }
                catch { /* 点击无效可接受，悬浮提示不受影响 */ }
                nb[i] = btn;
            }
            newIcons = ni;
            newButtons = nb;
        }

        /// <summary>刷新前扩容槽位数组（UITechNode 与 UITechTip 共用）</summary>
        private static void ExtendBeforeRefresh(TechProto tech, ref Image[] icons, ref UIButton[] buttons)
        {
            try
            {
                EnsureSlots(tech, icons, buttons, out var ni, out var nb);
                if (!ReferenceEquals(ni, icons))
                {
                    icons = ni;
                    buttons = nb;
                }
            }
            catch (Exception e)
            {
                Diag.Error("科技槽位扩容", e);
            }
        }

        /// <summary>刷新后按配方数撑宽图标框（各面板基础宽度不同，由调用方给出）</summary>
        private static void WidenAfterRefresh(TechProto tech, RectTransform box, float baseWidth)
        {
            try
            {
                int len = tech?.unlockRecipeArray?.Length ?? 0;
                if (len > 3 && box != null)
                    box.sizeDelta = new Vector2(baseWidth + 46f * len, box.sizeDelta.y);
            }
            catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UITechNode), nameof(UITechNode.UpdateInfoComplete))]
        public static void UITechNode_Pre(UITechNode __instance)
            => ExtendBeforeRefresh(__instance.techProto, ref __instance.unlockIcons, ref __instance.unlockButtons);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UITechNode), nameof(UITechNode.UpdateInfoComplete))]
        public static void UITechNode_Post(UITechNode __instance)
            => WidenAfterRefresh(__instance.techProto, __instance.unlockIconBox, 4f);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UITechTip), nameof(UITechTip.UpdateInfoComplete))]
        public static void UITechTip_Pre(UITechTip __instance)
            => ExtendBeforeRefresh(__instance.techProto, ref __instance.unlockIcons, ref __instance.unlockButtons);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UITechTip), nameof(UITechTip.UpdateInfoComplete))]
        public static void UITechTip_Post(UITechTip __instance)
            => WidenAfterRefresh(__instance.techProto, __instance.unlockIconBox, 2f);
    }
}
