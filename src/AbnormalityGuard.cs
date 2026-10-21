using System;
using System.Collections.Generic;
using ABN;
using HarmonyLib;

namespace TransmutationTable
{
    /// <summary>
    /// 屏蔽原版「数据异常」提示。
    ///
    /// 背景（反编译核对 0.10.35）：
    ///   DSP 内置一套异常检测系统（AbnormalityLogic + ABN_ItemProduction / ABN_ConsumptionForUpgrade /
    ///   ABN_ItemForUnlockTech / ABN_DysonSphereConstruct 等检测器），会比对物品产量、消耗与科技/建造进度
    ///   的比例是否合理。EMC 的本质就是"凭空生成 / 凭空消失物品"，因此必然被判为产量异常，
    ///   触发 GameAbnormalityData_0925.TriggerAbnormality → onAbnormalityChecked → UIAbnormalityTip
    ///   在屏幕上方弹出「数据异常」提示条（点开还有"数据异常详情"）。
    ///
    /// 处理方式（签名恒等校验）：
    ///   ABN_ProtoData 的判定是 `protoTable.Signature != CalculateSignature(protoTable)`，
    ///   而 LDBTool 注入任何物品/配方/科技都会改变这张表的 MD5 → 必然判定不符（每张表各弹一次）。
    ///   这里让 CalculateSignature 直接返回表里存的 Signature（恒等），校验自然成立；
    ///   同时清掉旧存档里已经累积的异常记录 —— UIAbnormalityTip.Determine() 会读这些记录反复弹提示条。
    ///   其余检测器（产量/消耗类）保持原版行为，仅在日志里留痕便于排查。
    /// </summary>
    [HarmonyPatch]
    public static class AbnormalityGuard
    {
        private static readonly HashSet<int> _reported = new HashSet<int>();

        /// <summary>签名恒等校验：直接返回表里存的原版签名，跳过 MD5 重算。
        /// ABN_ProtoData 触发点 = onGameBegin + 每次存档前 beforeGameSave，
        /// 恒等后即视为"原型表未被修改"，异常不再产生。</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProtoSignature_0_10_30_3100), nameof(ProtoSignature_0_10_30_3100.CalculateSignature))]
        public static bool CalculateSignature_Prefix(ProtoTable protoSet, ref string __result)
        {
            try
            {
                __result = protoSet?.Signature ?? "";
            }
            catch
            {
                __result = "";
            }
            return false; // 跳过原 MD5 计算（同时省掉存档前的三次哈希开销）
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameAbnormalityData_0925), nameof(GameAbnormalityData_0925.TriggerAbnormality))]
        public static bool TriggerAbnormality_Prefix(int protoId, int minRecordVersion, long[] vals)
        {
            try
            {
                // 只留痕不干预：除 ABN_ProtoData 之外的检测器（产量/消耗/建造进度等）保持原版行为，
                // 万一被 Mod 行为误判，日志里能直接看到是哪个检测器。
                if (_reported.Add(protoId))
                {
                    string det = "", tip = "";
                    try
                    {
                        var proto = LDB.abnormalities?.Select(protoId);
                        det = proto?.DeterminatorName ?? "";
                        tip = proto?.TipDesc ?? "";
                    }
                    catch { }
                    TransmutationTablePlugin.Log.LogInfo(
                        $"[数据异常] 原版判定触发 protoId={protoId} 检测器={det} 说明={tip}");
                }
            }
            catch { }
            return true; // 放行原版逻辑
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameAbnormalityData_0925), nameof(GameAbnormalityData_0925.Import))]
        public static void Import_Postfix(GameAbnormalityData_0925 __instance)
        {
            try
            {
                // 清掉旧存档里累积的异常记录：UIAbnormalityTip.Determine() 只要发现
                // NothingAbnormal()==false 就会反复弹提示条，即使签名校验已恒等通过
                var arr = __instance?.runtimeDatas;
                if (arr == null) return;
                int cleared = 0;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i].protoId > 0)
                    {
                        arr[i] = default;
                        cleared++;
                    }
                }
                if (cleared > 0)
                    TransmutationTablePlugin.Log.LogInfo($"[数据异常] 已清除存档中的 {cleared} 条历史异常记录（签名恒等校验下不再产生）");
            }
            catch (Exception e)
            {
                Diag.Error("异常记录清理", e);
            }
        }
    }
}
