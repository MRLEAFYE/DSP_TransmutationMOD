using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace TransmutationTable
{
    /// <summary>
    /// EMC 系统：物品价值表 + 玩家 EMC 余额 + 输出通道配置。
    /// 价值表 = 手工锚点（原材料）+ 沿配方图向下游传播（取最小成本）。
    /// 余额/输出配置/已学习的价值 全部随存档保存（DSPModSave）。
    /// </summary>
    public static class EmcSystem
    {
        // 锚点：不可再生产的原材料直接定值（用户定价规则：初始矿一律 10 EMC）。
        // 使用精确物品 ID（0.10.35 实测布局：1003=硅石 1005=石矿 1007=原油），
        // ID 匹配不到时退回按名字查找（防止未来版本改 ID）。
        private static readonly (int id, string[] fallbackNames, double emc)[] Anchors =
        {
            // ---- 基础矿（用户规则：一律 ×10） ----
            (1001, new[] { "iron ore", "铁矿" }, 10.0),
            (1002, new[] { "copper ore", "铜矿" }, 10.0),
            (1003, new[] { "silicon ore", "硅石", "硅矿" }, 10.0),
            (1004, new[] { "titanium ore", "钛石", "钛矿" }, 10.0),
            (1005, new[] { "stone", "石矿", "石头" }, 10.0),
            (1006, new[] { "coal", "煤" }, 10.0),
            (1007, new[] { "crude oil", "原油" }, 20.0),
            (1011, new[] { "combustible iceberg", "可燃冰" }, 20.0),
            (1012, new[] { "kimberlite", "金伯利" }, 20.0),
            (1013, new[] { "fractal silicon", "分形硅" }, 20.0),
            (1014, new[] { "grating crystal", "光栅石" }, 20.0),
            (1015, new[] { "bamboo crystal", "刺笋结晶" }, 20.0),
            (1016, new[] { "unipolar magnet", "单极磁石" }, 80.0),
            (1000, new[] { "water", "水" }, 5.0),            // 用户指定：水 5 EMC
            (1099, new[] { "sand", "沙土" }, 1.0),           // 用户指定：沙土 1 EMC
            (0,     new[] { "wood", "木材", "木头" }, 10.0), // 木材按名字找
            // ---- 无配方产物（射线接收器/战斗/特殊产出），配方图无法覆盖，直接定值（随矿价 ×10）----
            (1031, new[] { "植物燃料" }, 30.0),
            (1122, new[] { "antimatter", "反物质" }, 300.0),  // 射线接收器：临界光子 → 反物质
            (1208, new[] { "critical photon", "临界光子" }, 50.0),
            (1211, new[] { "引力透镜" }, 200.0),              // 黑雾引力透镜
            (1608, new[] { "反物质胶囊" }, 400.0),
            (1803, new[] { "反物质燃料棒" }, 800.0),
            (1804, new[] { "湮灭燃料棒", "金色燃料棒" }, 1000.0),
            (2207, new[] { "蓄电器（满）" }, 150.0),          // 满电蓄电器（空蓄电器走配方传播）
            (5201, new[] { "黑雾矩阵", "存储单元" }, 400.0),  // 黑雾战斗产出
            (5202, new[] { "硅基神经元" }, 400.0),
            (5203, new[] { "物质重组器" }, 400.0),
            (5204, new[] { "负熵奇点" }, 400.0),
            (5205, new[] { "核心素", "虚粒子" }, 400.0),
            (5206, new[] { "能量碎片" }, 80.0),
        };

        /// <summary>物品ID -> EMC 价值</summary>
        private static Dictionary<int, double> _emc = new Dictionary<int, double>();

        /// <summary>被价格文件/内置默认"写死"的物品 ID——配方推导不得改动</summary>
        private static HashSet<int> _locked = new HashSet<int>();

        /// <summary>解析价格文件，返回成功条数。行格式: "ID EMC [中文名]"，# 开头为注释。</summary>
        private static int LoadPriceFile(string path)
        {
            int n = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                var parts = line.Split(new[] { '\t', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (!int.TryParse(parts[0], out int id) || id <= 0) continue;
                if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v)) continue;
                _emc[id] = Math.Max(0, v);
                _locked.Add(id);
                n++;
            }
            return n;
        }

        /// <summary>可转化为物品的 ID 列表（GUI 用，按价值排序）</summary>
        private static List<ItemProto> _sortedItems;

        /// <summary>玩家 EMC 余额（存在存档里 = 存在玩家身上）</summary>
        public static double Balance;

        /// <summary>每座建筑独立的输出配置：key = (planetId << 32) | entityId</summary>
        private static Dictionary<long, int[]> _outputs = new Dictionary<long, int[]>();

        /// <summary>构造建筑配置键</summary>
        public static long MakeKey(int planetId, int entityId)
            => ((long)planetId << 32) | (uint)entityId;

        private static int[] GetOutputArray(long key)
        {
            if (!_outputs.TryGetValue(key, out var arr))
            {
                arr = new int[4]; // 最多 4 端口（四向版）；普通版只用 0-2
                _outputs[key] = arr;
            }
            return arr;
        }

        public static int GetOutput(long key, int slot)
            => GetOutputArray(key)[slot];

        public static void SetOutput(long key, int slot, int itemId)
            => GetOutputArray(key)[slot] = itemId;

        /// <summary>转化箱的单一输出物品（复用每建筑 4 槽数组的第 4 格，随存档持久）</summary>
        public static int GetBoxOutput(long key) => GetOutputArray(key)[3];

        public static void SetBoxOutput(long key, int itemId) => GetOutputArray(key)[3] = itemId;

        /// <summary>转化站配置（"端口"模型）：
        /// 输入端口 = 自动收购本星球物流塔供应的物品（AutoBuy 开关）；
        /// 输出端口 = 手动配置的供应物品（Outputs）；
        /// 自动满足需求 = AutoSupply 开启后自动供应星球内其他塔的需求物品。</summary>
        public class StationCfg
        {
            public bool AutoBuy = true;
            public bool AutoSupply;
            public bool InfiniteDrones = true;   // 无限小飞机：每帧自动补满内置无人机
            public List<int> Outputs = new List<int>();
        }

        private static Dictionary<long, StationCfg> _stationCfgs = new Dictionary<long, StationCfg>();

        /// <summary>转化站内部仓储槽上限。必须 ≤ 6：原版 needs/remotePairProcesses 是硬编码
        /// int[6]，UI 图标数组也按原版 4 格预算布局 —— 12 格会让选中信息面板越界崩溃。</summary>
        public const int StationSlotCount = 6;

        public static StationCfg GetStationCfg(long key)
        {
            if (!_stationCfgs.TryGetValue(key, out var cfg))
            {
                cfg = new StationCfg();
                _stationCfgs[key] = cfg;
            }
            return cfg;
        }

        // ------------------------------------------------------------------
        // 建筑附加参数：输出速度 + 输出喷涂等级（桌/箱/站通用）
        // ------------------------------------------------------------------
        public class BuildCfg
        {
            public int OutputSpeed = 60;  // 个/秒（60 = 每 tick 1 件 = 满速）
            public int SprayLevel;        // 0=无 1=MK.I 2=MK.II 3=MK.III
        }

        private static Dictionary<long, BuildCfg> _buildCfgs = new Dictionary<long, BuildCfg>();

        public static BuildCfg GetBuildCfg(long key)
        {
            if (!_buildCfgs.TryGetValue(key, out var cfg))
            {
                cfg = new BuildCfg();
                _buildCfgs[key] = cfg;
            }
            return cfg;
        }

        private static readonly int[] ProlifIds = { 0, 1141, 1142, 1143 };

        /// <summary>喷涂等级对应的 inc 点数（每件物品获得的点数，读自涂料物品 Ability）</summary>
        public static int SprayInc(int level)
            => level > 0 && level < ProlifIds.Length ? (LDB.items.Select(ProlifIds[level])?.Ability ?? 0) : 0;

        /// <summary>喷涂后每件物品的附加 EMC 成本 = 涂料价 / 可喷涂次数</summary>
        public static double SprayCostPerItem(int level)
        {
            if (level <= 0 || level >= ProlifIds.Length) return 0;
            var p = LDB.items.Select(ProlifIds[level]);
            if (p == null) return 0;
            return EmcOf(ProlifIds[level]) / Math.Max(1, p.HpMax);
        }

        /// <summary>inc 点数单价 = 涂料价 / (喷涂次数 × 每件点数)，用于收购被喷涂物品的加价</summary>
        public static double SprayCostPerIncPoint(int incPoints)
        {
            if (incPoints <= 0) return 0;
            for (int lv = 1; lv < ProlifIds.Length; lv++)
            {
                var p = LDB.items.Select(ProlifIds[lv]);
                if (p == null) continue;
                if (p.Ability == incPoints)
                    return EmcOf(ProlifIds[lv]) / Math.Max(1, (double)p.HpMax * p.Ability);
            }
            // 未知点数：按 MK.I 单价兜底
            var p0 = LDB.items.Select(ProlifIds[1]);
            return p0 != null ? EmcOf(ProlifIds[1]) / Math.Max(1, (double)p0.HpMax * p0.Ability) : 0;
        }

        private static bool _built;

        /// <summary>存档中已学习的价值（Import 阶段暂存，构建完价值表后合并，保证跨版本不丢）</summary>
        private static Dictionary<int, double> _importedEmc;

        public static double EmcOf(int itemId)
        {
            EnsureBuilt();
            return _emc.TryGetValue(itemId, out var v) ? v : 1.0; // 未知物品按 1 EMC 收取，绝不卡传送带
        }

        public static bool HasEmc(int itemId)
        {
            EnsureBuilt();
            return _emc.ContainsKey(itemId);
        }

        public static IReadOnlyList<ItemProto> SortedItems
        {
            get { EnsureBuilt(); return _sortedItems; }
        }

        // ------------------------------------------------------------------
        // 学习机制：投入过至少一次的物品才会解锁兑换（ProjectE 式）
        // ------------------------------------------------------------------
        private static readonly HashSet<int> _learned = new HashSet<int>();

        /// <summary>默认已学习的物品：玩家无法主动投入转化设施的特殊物品。
        /// 沙土(1099) 是地基施工的副产物，只存在于"沙土堆"里，无法用分拣器/传送带送入，
        /// 否则会永远显示"未学习"、永远换不出来。</summary>
        private static readonly (int id, string[] names)[] AlwaysLearned =
        {
            (1099, new[] { "sand", "沙土" }),
        };

        /// <summary>把默认已学习项加入集合（每次重建/读档后都要调，因为 Import 会 Clear）</summary>
        private static void ApplyAlwaysLearned()
        {
            foreach (var (id, names) in AlwaysLearned)
            {
                var p = id > 0 ? LDB.items?.Select(id) : null;
                if (p == null) p = FindItemAny(names);
                if (p != null) _learned.Add(p.ID);
            }
        }

        public static bool IsLearned(int itemId) => itemId > 0 && _learned.Contains(itemId);

        public static int LearnedCount => _learned.Count;

        /// <summary>标记物品已学习（投入/收购/存入时调用）。返回 true 表示本次是新学会</summary>
        public static bool Learn(int itemId)
        {
            if (itemId <= 0) return false;
            if (!_learned.Add(itemId)) return false;
            if (_learned.Count == 1 || _learned.Count % 20 == 0)
                TransmutationTablePlugin.Log.LogInfo($"[学习] 已解锁 {LDB.items.Select(itemId)?.name ?? itemId.ToString()}（累计 {_learned.Count} 种）");
            return true;
        }

        /// <summary>沙盒一键解锁：把所有有 EMC 价值的物品标记为已学习</summary>
        public static int LearnAll()
        {
            EnsureBuilt();
            int before = _learned.Count;
            foreach (var kv in _emc)
                if (kv.Key > 0) _learned.Add(kv.Key);
            foreach (var p in LDB.items?.dataArray ?? Array.Empty<ItemProto>())
                if (p != null && p.ID > 0) _learned.Add(p.ID);
            int added = _learned.Count - before;
            TransmutationTablePlugin.Log.LogInfo($"[学习] 一键解锁完成：新增 {added} 种（累计 {_learned.Count} 种）");
            return added;
        }

        /// <summary>旧存档（无学习数据）迁移：玩家背包里现有的物品算作已学习</summary>
        private static void MigrateLearnedFromPackage()
        {
            try
            {
                var package = GameMain.mainPlayer?.package;
                if (package?.grids == null) return;
                for (int i = 0; i < package.size && i < package.grids.Length; i++)
                    if (package.grids[i].itemId > 0 && package.grids[i].count > 0)
                        _learned.Add(package.grids[i].itemId);   // 静默迁移，不逐条打日志
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // 价值表构建
        // ------------------------------------------------------------------
        public static void EnsureBuilt()
        {
            if (_built) return;
            try
            {
                Build();
                _built = true;
            }
            catch (Exception e)
            {
                TransmutationTablePlugin.Log.LogError("EMC 价值表构建失败: " + e);
            }
        }

        private static void Build()
        {
            var arr = LDB.items.dataArray;
            if (arr == null) throw new InvalidOperationException("LDB 未就绪");

            _emc.Clear();
            _locked.Clear();

            // ---- 写死价格表（用户可编辑）：文件 > 内置默认 > 配方推导 ----
            // 文件: BepInEx/config/TransmutationTable.prices.txt，每行 "ID EMC [中文名]"，# 开头为注释。
            // 文件里的价格是最终价，配方推导不再改动（locked）；文件没有的物品依次回退。
            string pricePath = Path.Combine(Paths.ConfigPath, "TransmutationTable.prices.txt");
            try
            {
                if (File.Exists(pricePath))
                {
                    int n = LoadPriceFile(pricePath);
                    TransmutationTablePlugin.Log.LogInfo($"已加载价格文件 {pricePath}（{n} 项，文件值锁定）");
                }
            }
            catch (Exception e)
            {
                TransmutationTablePlugin.Log.LogWarning($"价格文件加载失败（改用内置默认）: {e.Message}");
            }
            foreach (var (id, emc) in EmcDefaults.Prices)
                if (!_locked.Contains(id)) { _emc[id] = emc; _locked.Add(id); }   // 内置默认 = 兜底写死值

            int missedAnchors = 0;
            foreach (var (id, names, emc) in Anchors)
            {
                if (_emc.ContainsKey(id) && _locked.Contains(id)) continue;
                var p = id > 0 ? LDB.items.Select(id) : null;
                if (p == null) p = FindItemAny(names);
                if (p != null && !_emc.ContainsKey(p.ID)) { _emc[p.ID] = emc; _locked.Add(p.ID); }
                else if (p == null) missedAnchors++;
            }
            if (missedAnchors > 0)
                TransmutationTablePlugin.Log.LogWarning($"EMC 锚点有 {missedAnchors}/{Anchors.Length} 个未匹配到物品（价值表仍可由配方推导）");

            // 沿配方传播（用户规则：多条配方时按最贵的"低效配方"定价，
            // 这样走高效配方生产才有差价可赚）。产物价值 = max(各配方的 Σ原料价×数量 ÷ 产物总件数)。
            // 锚点/写死值已 _locked，不会被传播顶掉（如"石矿×10→硅石"不会覆盖硅石锚点 10）。
            var recipes = LDB.recipes.dataArray;
            for (int iter = 0; iter < 300; iter++)
            {
                bool changed = false;
                foreach (var r in recipes)
                {
                    if (r?.Items == null || r.Results == null || r.Items.Length == 0) continue;

                    double cost = 0;
                    bool known = true;
                    // 必须乘 ItemCounts：如反物质配方 = 10 临界光子 → 2 反物质 + 2 氢，
                    // 漏乘会把反物质算成 50/4×2=25（比 1 个光子还便宜），钢材=铁块价等系统性失真
                    for (int i = 0; i < r.Items.Length; i++)
                    {
                        int cnt = (r.ItemCounts != null && i < r.ItemCounts.Length && r.ItemCounts[i] > 0)
                            ? r.ItemCounts[i] : 1;
                        if (_emc.TryGetValue(r.Items[i], out var v)) cost += v * cnt;
                        else { known = false; break; }
                    }
                    if (!known) continue;

                    int totalOut = 0;
                    foreach (var c in r.ResultCounts) totalOut += c;
                    if (totalOut <= 0) continue;

                    double perUnit = cost / totalOut;
                    for (int i = 0; i < r.Results.Length; i++)
                    {
                        int outId = r.Results[i];
                        if (_locked.Contains(outId)) continue;   // 写死价不参与推导
                        double candidate = perUnit;              // 每件同价：批次成本均摊到产物总件数
                        if (!_emc.TryGetValue(outId, out var cur) || candidate > cur + 1e-9)
                        {
                            _emc[outId] = Math.Round(candidate, 4);
                            changed = true;
                        }
                    }
                }
                if (!changed) break;
            }

            // 宽松兜底 pass：对"有配方但仍无价"的物品（原料链上存在无锚的稀有物），
            // 用"未知原料按 1 EMC"计入成本推导价格，迭代至不动点——保证任何可制造物品都有价，
            // 不再出现"临时价 1"。（无配方产物如黑雾掉落已由锚点覆盖）
            for (int iter = 0; iter < 300; iter++)
            {
                bool changed = false;
                foreach (var r in recipes)
                {
                    if (r?.Items == null || r.Results == null || r.Items.Length == 0) continue;

                    bool allKnown = true;
                    double cost = 0;
                    for (int i = 0; i < r.Items.Length; i++)
                    {
                        int cnt = (r.ItemCounts != null && i < r.ItemCounts.Length && r.ItemCounts[i] > 0)
                            ? r.ItemCounts[i] : 1;
                        if (_emc.TryGetValue(r.Items[i], out var v)) { cost += v * cnt; }
                        else { cost += cnt; allKnown = false; }   // 未知原料按 1 EMC/个
                    }
                    if (allKnown) continue; // 严格 pass 已覆盖

                    int totalOut = 0;
                    foreach (var c in r.ResultCounts) totalOut += c;
                    if (totalOut <= 0) continue;

                    double perUnit = cost / totalOut;
                    for (int i = 0; i < r.Results.Length; i++)
                    {
                        int outId = r.Results[i];
                        if (_locked.Contains(outId) || _emc.ContainsKey(outId)) continue; // 只填空，不覆盖严格价
                        _emc[outId] = Math.Round(perUnit, 4);
                        changed = true;
                    }
                }
                if (!changed) break;
            }

            // Mod 自有建筑（桌/箱/站）兜底：LDBTool 可能重映射建筑 ID（如转化桌 9701→11500），
            // 配方 Results 仍指旧 ID，传播价落在幽灵 ID 上，表现为转化桌"-"无价。
            // 直接按配方原料成本给三座建筑本体定价。
            EnsureOwnBuildingPrice(TransmutationTablePlugin.TableProto, TransmutationTablePlugin.TableRecipe);
            EnsureOwnBuildingPrice(TransmutationTablePlugin.BoxProto, TransmutationTablePlugin.BoxRecipe);
            EnsureOwnBuildingPrice(TransmutationTablePlugin.StationProto, TransmutationTablePlugin.StationRecipe);

            // 整数化：用户要求价格尽量往整数靠（锚点价本身已是整数，这里对传播价四舍五入）
            var intKeys = new List<int>(_emc.Keys);
            foreach (int k in intKeys)
                _emc[k] = Math.Max(1, Math.Round(_emc[k]));

            // 蓄电器（满）防漏洞：满蓄电器必须贵于空蓄电器（差价 ≥100），
            // 否则"买满 → 放电 → 卖空"可无限刷 EMC（若被价格文件锁定则尊重文件值）
            if (!_locked.Contains(2207) && _emc.TryGetValue(2206, out var emptyAcc) &&
                (!_emc.TryGetValue(2207, out var fullAcc) || fullAcc < emptyAcc + 100))
                _emc[2207] = Math.Max(1, Math.Round(emptyAcc + 100));

            // 存档中已学习的价值只用于"补缺"（锚点/配方算不出来的物品），
            // 保证锚点调整或配方变化后，新计算结果优先生效
            if (_importedEmc != null)
            {
                foreach (var kv in _importedEmc)
                    if (!_emc.ContainsKey(kv.Key)) _emc[kv.Key] = kv.Value;
                _importedEmc = null;
            }

            // GUI 列表：按官方"自动整理"顺序（原版 StorageComponent.Sort 用 ItemProto.index，
            // 即物品在图鉴/dataArray 中的顺序），而不是按价格
            _sortedItems = new List<ItemProto>();
            foreach (var p in arr)
                if (p != null && p.ID > 0)
                    _sortedItems.Add(p);
            _sortedItems.Sort((a, b) =>
            {
                int ia = a.index > 0 ? a.index : int.MaxValue;
                int ib = b.index > 0 ? b.index : int.MaxValue;
                int c = ia.CompareTo(ib);
                return c != 0 ? c : a.ID.CompareTo(b.ID);
            });

            ApplyAlwaysLearned();

            TransmutationTablePlugin.Log.LogInfo($"EMC 价值表构建完成，共 {_emc.Count} 种物品有价");
            DumpItemList();
            DumpRecipeList();
        }

        /// <summary>导出全部配方（原料/产物 ID×数量），用于离线审计定价。</summary>
        private static void DumpRecipeList()
        {
            try
            {
                string path = Path.Combine(Paths.ConfigPath, "TransmutationTable.recipes.txt");
                using var w = new StreamWriter(path, false);
                w.WriteLine("ID\t原料\t产物");
                foreach (var r in LDB.recipes.dataArray)
                {
                    if (r == null || r.Items == null || r.Results == null) continue;
                    var ins = new List<string>();
                    for (int i = 0; i < r.Items.Length; i++)
                        ins.Add($"{r.Items[i]}x{((r.ItemCounts != null && i < r.ItemCounts.Length) ? r.ItemCounts[i] : 1)}");
                    var outs = new List<string>();
                    for (int i = 0; i < r.Results.Length; i++)
                        outs.Add($"{r.Results[i]}x{((r.ResultCounts != null && i < r.ResultCounts.Length) ? r.ResultCounts[i] : 1)}");
                    w.WriteLine($"{r.ID}\t{string.Join("+", ins)}\t{string.Join("+", outs)}");
                }
            }
            catch (Exception e)
            {
                TransmutationTablePlugin.Log.LogWarning("配方清单导出失败: " + e.Message);
            }
        }

        /// <summary>为 Mod 自有建筑补价：按其配方 Σ(原料价×数量) 计算（未知原料按 1/个）；仅填空不覆盖。</summary>
        private static void EnsureOwnBuildingPrice(ItemProto proto, RecipeProto recipe)
        {
            if (proto == null || proto.ID <= 0 || _locked.Contains(proto.ID) || _emc.ContainsKey(proto.ID)) return;
            double cost = 0;
            if (recipe?.Items != null)
            {
                for (int i = 0; i < recipe.Items.Length; i++)
                {
                    int cnt = (recipe.ItemCounts != null && i < recipe.ItemCounts.Length && recipe.ItemCounts[i] > 0)
                        ? recipe.ItemCounts[i] : 1;
                    cost += _emc.TryGetValue(recipe.Items[i], out var v) ? v * cnt : cnt;
                }
            }
            _emc[proto.ID] = Math.Max(1, Math.Round(cost > 0 ? cost : 40.0));
        }

        private static ItemProto FindItemAny(string[] names)
        {
            var arr = LDB.items?.dataArray;
            if (arr == null) return null;
            foreach (var p in arr)
            {
                if (p == null) continue;
                foreach (var n in names)
                {
                    if ((p.Name != null && p.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (p.name != null && p.name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                        return p;
                }
            }
            return null;
        }

        /// <summary>导出全部物品清单与 EMC 价值，便于校准锚点（写到 BepInEx/config）</summary>
        private static void DumpItemList()
        {
            try
            {
                string path = Path.Combine(Paths.ConfigPath, "TransmutationTable.items.txt");
                using var w = new StreamWriter(path, false);
                w.WriteLine("ID\t英文名Name\t本地化名name\tEMC");
                foreach (var p in LDB.items.dataArray)
                {
                    if (p == null) continue;
                    w.WriteLine($"{p.ID}\t{p.Name}\t{p.name}\t{(p.ID > 0 && _emc.ContainsKey(p.ID) ? _emc[p.ID].ToString("0.####") : "-")}");
                }
            }
            catch (Exception e)
            {
                TransmutationTablePlugin.Log.LogWarning("物品清单导出失败: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // 存档
        // ------------------------------------------------------------------
        public static void Export(BinaryWriter w)
        {
            EnsureBuilt();
            w.Write(9); // 版本（9 = + 已学习物品集合）
            w.Write(Balance);
            w.Write(_emc.Count);
            foreach (var kv in _emc)
            {
                w.Write(kv.Key);
                w.Write(kv.Value);
            }
            var validOutputs = new List<KeyValuePair<long, int[]>>();
            foreach (var kv in _outputs)
                if (kv.Value != null && (kv.Value[0] != 0 || kv.Value[1] != 0 || kv.Value[2] != 0 || kv.Value[3] != 0))
                    validOutputs.Add(kv);
            w.Write(validOutputs.Count);
            foreach (var kv in validOutputs)
            {
                w.Write(kv.Key);
                for (int i = 0; i < 4; i++) w.Write(kv.Value[i]);
            }
            var validStations = new List<KeyValuePair<long, StationCfg>>();
            foreach (var kv in _stationCfgs)
                if (kv.Value != null && (kv.Value.AutoBuy || kv.Value.AutoSupply || kv.Value.Outputs.Count > 0))
                    validStations.Add(kv);
            w.Write(validStations.Count);
            foreach (var kv in validStations)
            {
                var cfg = kv.Value;
                w.Write(kv.Key);
                w.Write(cfg.AutoBuy);
                w.Write(cfg.AutoSupply);
                w.Write(cfg.InfiniteDrones);
                w.Write(cfg.Outputs.Count);
                foreach (int item in cfg.Outputs) w.Write(item);
            }
            // v8：建筑附加参数（只保存有自定义内容的）
            var validBuilds = new List<KeyValuePair<long, BuildCfg>>();
            foreach (var kv in _buildCfgs)
                if (kv.Value != null && (kv.Value.OutputSpeed != 60 || kv.Value.SprayLevel != 0))
                    validBuilds.Add(kv);
            w.Write(validBuilds.Count);
            foreach (var kv in validBuilds)
            {
                w.Write(kv.Key);
                w.Write(kv.Value.OutputSpeed);
                w.Write(kv.Value.SprayLevel);
            }
            // v9：已学习物品
            w.Write(_learned.Count);
            foreach (int id in _learned) w.Write(id);
        }

        public static void Import(BinaryReader r)
        {
            int version = r.ReadInt32();
            Balance = r.ReadDouble();
            int n = r.ReadInt32();
            var learned = new Dictionary<int, double>();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                double v = r.ReadDouble();
                if (id > 0 && v > 0) learned[id] = v;
            }

            _outputs.Clear();
            if (version >= 2)
            {
                int m = r.ReadInt32();
                int ports = version >= 3 ? 4 : 3;
                for (int i = 0; i < m; i++)
                {
                    long key = r.ReadInt64();
                    var arr = new int[4];
                    for (int s = 0; s < ports; s++) arr[s] = r.ReadInt32();
                    _outputs[key] = arr;
                }
            }
            else
            {
                // v1 旧格式：3 个全局输出通道，直接丢弃
                for (int i = 0; i < 3; i++) r.ReadInt32();
            }

            _stationCfgs.Clear();
            if (version >= 6)
            {
                // v6+：端口模型
                int k = r.ReadInt32();
                for (int i = 0; i < k; i++)
                {
                    long key = r.ReadInt64();
                    var cfg = GetStationCfg(key);
                    cfg.AutoBuy = r.ReadBoolean();
                    cfg.AutoSupply = r.ReadBoolean();
                    if (version >= 7) cfg.InfiniteDrones = r.ReadBoolean();
                    int nOut = r.ReadInt32();
                    cfg.Outputs.Clear();
                    for (int j = 0; j < nOut; j++)
                    {
                        int item = r.ReadInt32();
                        if (item > 0) cfg.Outputs.Add(item);
                    }
                }
            }
            _buildCfgs.Clear();
            if (version >= 8)
            {
                int nBuild = r.ReadInt32();
                for (int i = 0; i < nBuild; i++)
                {
                    long key = r.ReadInt64();
                    var cfg = GetBuildCfg(key);
                    cfg.OutputSpeed = r.ReadInt32();
                    cfg.SprayLevel = r.ReadInt32();
                }
            }
            else if (version >= 4)
            {
                // v4/v5 旧槽位模型 → 迁移为端口模型
                int k = r.ReadInt32();
                int slots = version >= 5 ? StationSlotCount : 5; // v4 存档每站 5 槽
                for (int i = 0; i < k; i++)
                {
                    long key = r.ReadInt64();
                    var cfg = GetStationCfg(key);
                    for (int s = 0; s < slots; s++)
                    {
                        int mode = r.ReadInt32();
                        int item = r.ReadInt32();
                        if (mode == 1) cfg.AutoBuy = true;
                        else if (mode == 2 && item > 0 && !cfg.Outputs.Contains(item)) cfg.Outputs.Add(item);
                    }
                }
            }

            _learned.Clear();
            if (version >= 9)
            {
                int nLearned = r.ReadInt32();
                for (int i = 0; i < nLearned; i++)
                {
                    int id = r.ReadInt32();
                    if (id > 0) _learned.Add(id);
                }
            }
            // 旧存档没有学习数据：背包现成物品算作已学习，其余靠玩家自己投入解锁
            if (version < 9) MigrateLearnedFromPackage();

            _importedEmc = learned;
            EnsureBuilt();
            ApplyAlwaysLearned(); // 读档后补回默认已学习项（Import 开头清空过集合）

            TransmutationTablePlugin.Log.LogInfo($"EMC 数据读取完成，余额 {Balance:F1}");
        }

        public static void ResetPlayerData()
        {
            Balance = 0;
            _outputs.Clear();
            _stationCfgs.Clear();
            _buildCfgs.Clear();
            _learned.Clear();
            ApplyAlwaysLearned();
            // 价值表是公共数据，重置玩家数据不重置价值表
        }
    }
}
