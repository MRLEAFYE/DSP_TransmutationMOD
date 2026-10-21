using System;
using System.Text;
using HarmonyLib;

namespace TransmutationTable
{
    /// <summary>
    /// 建造菜单安装器（每局游戏运行一次）。
    ///
    /// 背景（来自反编译核对）：
    ///  - UIBuildMenu.StaticLoad 只在 UI 创建时跑一次，把 BuildIndex=分类*100+槽位 的物品铺进
    ///    protos[16,13]；渲染循环 for(l=0; l<12 ...) 只画槽位 0..11，槽位 12 永远不可见。
    ///  - 0.10.30 的分类 3 是传送带/分拣器（物流），分类 4 才是仓储（小型储物仓/大型储物仓/储液罐），
    ///    储液罐在第 3 格，后面 4..10 格是空的。
    ///  - 因此安装策略：全分类扫描储液罐（isTank）定位仓储分类，把三个建筑放进储液罐后面的三格；
    ///    若目标格被原版建筑占用，就把原版建筑平移到其他分类的空格里（并在日志里写明去向）。
    ///  - 槽位 11 是否可用取决于 childBgs[11] 等控件是否为 null（旧版地雷），运行时实测决定。
    /// </summary>
    internal static class BuildMenuInstaller
    {
        private static bool _installed;

        /// <summary>Plugin.Update 每帧调用，条件满足后执行一次。</summary>
        public static void TryInstall()
        {
            if (_installed) return;
            try
            {
                if (LDB.items == null || LDB.items.dataArray == null) return;
                if (TransmutationTablePlugin.TableProto == null) return;

                var protosField = AccessTools.Field(typeof(UIBuildMenu), "protos");
                if (!(protosField?.GetValue(null) is ItemProto[,] protos)) return;

                var staticLoadedField = AccessTools.Field(typeof(UIBuildMenu), "staticLoaded");
                if (staticLoadedField == null || !(bool)staticLoadedField.GetValue(null)) return;

                Install(protos);
                _installed = true;
            }
            catch (Exception e)
            {
                TransmutationTablePlugin.Log.LogError("建造菜单安装异常: " + e);
                _installed = true; // 失败也不再重试，避免刷屏
            }
        }

        private static void Install(ItemProto[,] protos)
        {
            int maxCat = protos.GetLength(0); // 16
            int maxSlot = protos.GetLength(1); // 13

            // 可见上限：渲染循环 l<12 → 槽位 0..11；槽位 11 还要求控件齐全
            int visibleMax = Math.Min(11, maxSlot - 1);
            if (!SlotControlsUsable(visibleMax)) visibleMax = Math.Min(10, maxSlot - 1);

            var mine = new ItemProto[]
            {
                TransmutationTablePlugin.TableProto,
                TransmutationTablePlugin.BoxProto,
                TransmutationTablePlugin.StationProto
            };

            // 0) 先把我们的物品从所有格子里摘出来（清掉历史错误落位）
            for (int c = 0; c < maxCat; c++)
                for (int s = 0; s < maxSlot; s++)
                    if (protos[c, s] != null && Array.IndexOf(mine, protos[c, s]) >= 0)
                        protos[c, s] = null;

            // 1) 全分类扫描储液罐（isTank），确定「仓储」分类与锚点槽位
            //    （实测 0.10.30：分类 3 是传送带/分拣器，分类 4 才是仓储，不能写死）
            int cat = -1, tankSlot = -1;
            for (int c = 1; c <= Math.Min(9, maxCat - 1) && cat < 0; c++)
            {
                for (int s = 1; s <= visibleMax; s++)
                {
                    var p = protos[c, s];
                    if (p == null) continue;
                    try
                    {
                        if (p.prefabDesc != null && p.prefabDesc.isTank)
                        {
                            cat = c;
                            tankSlot = s;
                            break;
                        }
                    }
                    catch { }
                }
            }
            if (cat < 0)
            {
                cat = 4; // 兜底：当前版本仓储分类
                TransmutationTablePlugin.Log.LogWarning("[建造菜单] 未找到储液罐锚点，使用兜底分类 4");
            }

            // 2) 目标槽位：紧贴储液罐后面的三格；储液罐太靠后/没找到就占该分类可见区最后三格
            int[] targets;
            if (tankSlot > 0 && tankSlot + 3 <= visibleMax)
            {
                targets = new[] { tankSlot + 1, tankSlot + 2, tankSlot + 3 };
            }
            else
            {
                targets = new[] { visibleMax - 2, visibleMax - 1, visibleMax };
            }

            // 3) 目标格上的原版建筑平移到物流分类（cat 2）的空格；找不到空格再扫其他分类
            foreach (int s in targets)
            {
                var occupant = protos[cat, s];
                if (occupant == null || Array.IndexOf(mine, occupant) >= 0) continue;
                int newCat = -1, newSlot = -1;
                for (int c = 2; c <= 9 && newCat < 0; c = (c == 2) ? 1 : c + 1)
                {
                    if (c >= maxCat) break;
                    for (int ss = 1; ss <= visibleMax; ss++)
                    {
                        if (protos[c, ss] != null) continue;
                        newCat = c; newSlot = ss;
                        break;
                    }
                }
                if (newCat < 0)
                {
                    TransmutationTablePlugin.Log.LogWarning(
                        $"[建造菜单] 无空格安置原版建筑 {occupant.Name}（原 仓储栏 {s} 号位），其暂不显示");
                    protos[cat, s] = null;
                    continue;
                }
                protos[newCat, newSlot] = occupant;
                occupant.BuildIndex = newCat * 100 + newSlot;
                TransmutationTablePlugin.Log.LogInfo(
                    $"[建造菜单] 原版「{occupant.Name}」从 仓储栏 {s} 号位 → {(newCat == 2 ? "物流栏" : "分类" + newCat)} {newSlot} 号位");
            }

            // 4) 我们的三个建筑按 桌→箱→站 顺序放入目标格
            int n = 0;
            foreach (var item in mine)
            {
                if (item == null) continue;
                if (n >= targets.Length) break;
                int s = targets[n++];
                protos[cat, s] = item;
                item.BuildIndex = cat * 100 + s;
                TransmutationTablePlugin.Log.LogInfo($"[建造菜单] 「{item.Name}」→ 仓储栏 {s} 号位 (BuildIndex={item.BuildIndex})");
            }

            DumpLayout(protos, "安装后", Math.Min(9, maxCat - 1), visibleMax);
            TransmutationTablePlugin.Log.LogInfo(
                $"[建造菜单] 安装完成：仓储栏 {targets[0]}/{targets[1]}/{targets[2]} 三格为 转化桌/转化箱/转化站（悬停可看名字）");
        }

        /// <summary>检查某槽位的 UI 控件是否齐全（防 childBgs[11] 之类的 null 地雷）</summary>
        private static bool SlotControlsUsable(int slot)
        {
            try
            {
                var menu = UIRoot.instance?.uiGame?.buildMenu;
                if (menu == null) return false; // UI 尚未创建 → 保守只用 1..10
                if (menu.childBgs == null || menu.childBgs.Length <= slot || menu.childBgs[slot] == null) return false;
                if (menu.childIcons == null || menu.childIcons.Length <= slot || menu.childIcons[slot] == null) return false;
                if (menu.childNumTexts == null || menu.childNumTexts.Length <= slot || menu.childNumTexts[slot] == null) return false;
                if (menu.childButtons == null || menu.childButtons.Length <= slot || menu.childButtons[slot] == null) return false;
                if (menu.childTips == null || menu.childTips.Length <= slot || menu.childTips[slot] == null) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把建造菜单 1..9 分类 × 0..visibleMax 槽的完整布局转储到日志文件，便于诊断</summary>
        private static void DumpLayout(ItemProto[,] protos, string phase, int maxCat, int visibleMax)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"===== 建造菜单布局（{phase}）=====");
                for (int c = 1; c <= maxCat; c++)
                {
                    var row = new StringBuilder($"分类{c}: ");
                    for (int s = 0; s <= visibleMax; s++)
                    {
                        var p = protos[c, s];
                        row.Append('[').Append(s).Append(']').Append(p == null ? "—" : p.Name).Append(' ');
                    }
                    sb.AppendLine(row.ToString());
                }
                string text = sb.ToString();
                string path = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "TransmutationTable.buildmenu.log");
                System.IO.File.WriteAllText(path, text); // 完整布局只落盘，不打进日志
            }
            catch (Exception e)
            {
                TransmutationTablePlugin.Log.LogWarning("布局转储失败: " + e.Message);
            }
        }
    }
}
