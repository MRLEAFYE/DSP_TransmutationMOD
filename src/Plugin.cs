using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using crecheng.DSPModSave;
using xiaoye97;
using UnityEngine;

namespace TransmutationTable
{
    /// <summary>
    /// 转化桌 (Transmutation Table) — ProjectE 式 EMC 等价交换系统。
    /// 建筑本体只是访问接口：EMC 余额、物品价值表、输出配置全部保存在存档（玩家）身上，
    /// 任意转化桌共享同一状态，销毁建筑不影响数据。
    /// 快捷键 F9：直接把一台转化桌放进背包（兜底获取手段）。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("me.xiaoye97.plugin.Dyson.LDBTool")]
    [BepInDependency("crecheng.DSPModSave")]
    public class TransmutationTablePlugin : BaseUnityPlugin, IModCanSave
    {
        public const string PluginGuid = "workbuddy.TransmutationTable";
        public const string PluginName = "TransmutationTable";
        public const string PluginVersion = "1.0.0";

        // 注册后的实际 Proto ID（LDBTool 允许用户在配置里改，注册后回读）
        public static int TableItemId = 9701;

        // 注册阶段持有的 Proto 对象引用（ID 可能被 LDBTool 配置改写，用引用而非 ID 定位）
        internal static ItemProto TableProto;
        internal static RecipeProto TableRecipe;
        internal static ItemProto BoxProto;      // 转化箱（储物仓模型，爪子进出）
        internal static RecipeProto BoxRecipe;
        internal static ItemProto StationProto;  // 转化站（行星内物流站模型，物流无人机进出）
        internal static RecipeProto StationRecipe;

        /// <summary>模板建筑（用于运行时生成紫色图标）</summary>
        internal static ItemProto TableTemplate, BoxTemplate, StationTemplate;

        /// <summary>转化箱/转化站物品 ID（注册后回读）</summary>
        public static int BoxItemId = 9703;
        public static int StationItemId = 9705;

        internal static ManualLogSource Log;

        internal Harmony HarmonyInstance;

        private void Awake()
        {
            Log = Logger;
            LDBTool.PreAddDataAction += RegisterProtos;
            LDBTool.PostAddDataAction += AfterProtosAdded;
            HarmonyInstance = new Harmony(PluginGuid);
            HarmonyInstance.PatchAll(typeof(AbnormalityGuard));         // 屏蔽原版"数据异常"误判
            HarmonyInstance.PatchAll(typeof(ProtoGuard));              // 索引自愈 + 图标图集兜底
            HarmonyInstance.PatchAll(typeof(TablePatches));
            HarmonyInstance.PatchAll(typeof(TechTreeSlotExtendPatch)); // 科技树解锁配方图标槽位扩容
            TablePatches.RegisterManualPatches(HarmonyInstance);
            Logger.LogInfo("转化桌已加载 (EMC system ready)");
        }

        private void OnDestroy()
        {
            LDBTool.PreAddDataAction -= RegisterProtos;
            LDBTool.PostAddDataAction -= AfterProtosAdded;
            HarmonyInstance?.UnpatchSelf();
        }

        private void Update()
        {
            // 建造菜单安装：一次性，等 UI 静态表建好且 Proto 注册完成后执行
            try
            {
                BuildMenuInstaller.TryInstall();
            }
            catch (Exception e)
            {
                Diag.Error("建造菜单安装", e);
            }

            // F9 = 领取转化桌/转化箱/转化站（进游戏后任意时刻可用）
            try
            {
                if (Input.GetKeyDown(KeyCode.F9) && GameMain.data != null && GameMain.mainPlayer != null)
                {
                    GiveItem(TableItemId, "转化桌");
                    GiveItem(BoxItemId, "转化箱");
                    GiveItem(StationItemId, "转化站");
                }
            }
            catch (Exception e)
            {
                Diag.Error("F9 发放", e);
            }
        }

        private static void GiveItem(int itemId, string label)
        {
            var proto = LDB.items.Select(itemId);
            if (proto == null)
            {
                UIRealtimeTip.Popup(label + " 物品尚未注册，请重启游戏", sound: false);
                return;
            }
            GameMain.mainPlayer.package.AddItem(itemId, 1, 0, out int _, false);
            UIRealtimeTip.Popup("已获得 " + label + " ×1（F9 随时领取）");
            Log.LogInfo("F9 发放" + label + " ×1");
        }

        // ------------------------------------------------------------------
        // Proto 注册：在 LDBTool 预加载阶段加入物品与配方
        // ------------------------------------------------------------------
        private static void RegisterProtos()
        {
            try
            {
                // 用分馏器作为外观/模型模板：它的模型带传送带端口 (SlotConfig)，
                // 复用其 modelIndex 后引擎会自动为建筑建立 FractionatorComponent 并绑定传送带，
                // Mod 只需拦截其原逻辑、替换为自己的 EMC 逻辑。
                ItemProto frac = FindFractionator();
                if (frac == null)
                {
                    // 诊断：转储样例物品名，便于定位命名规则
                    var arr = LDB.items?.dataArray;
                    var sb = new System.Text.StringBuilder("dataArray=");
                    sb.Append(arr?.Length ?? -1).Append("; 样例: ");
                    if (arr != null)
                        for (int i = 0; i < Math.Min(30, arr.Length); i++)
                            sb.Append('[').Append(arr[i]?.Name).Append('|').Append(arr[i]?.name).Append("] ");
                    Log.LogError("找不到分馏器原型，转化桌注册失败。" + sb);
                    return;
                }
                Log.LogInfo($"模板分馏器: ID={frac.ID} Name={frac.Name} ModelIndex={frac.ModelIndex}");

                // 动态挑选未占用的 ID（避开原版/其他 Mod），LDBTool 会以此为默认值写入配置
                // （四向物品从 itemId+2 起找，避开与转化桌配方同号的混淆）
                int itemId = FindFreeId(GetUsedItemIds(), 9701, 11899);
                int recipeId = FindFreeId(GetUsedRecipeIds(), itemId + 1, 11999);
                int boxItemId = FindFreeId(GetUsedItemIds(), itemId + 2, 11899);
                int boxRecipeId = FindFreeId(GetUsedRecipeIds(), boxItemId + 1, 11999);
                int stItemId = FindFreeId(GetUsedItemIds(), itemId + 4, 11899);
                int stRecipeId = FindFreeId(GetUsedRecipeIds(), stItemId + 1, 11999);

                // 建造菜单槽位：最终落位由 BuildMenuInstaller 在运行时完成（自动锚定储液罐）。
                // 注册期只写一个临时值占位。
                int tableBI = 4 * 100 + 4;
                int boxBI = 4 * 100 + 5;
                int stationBI = 4 * 100 + 6;

                // ---------- 科技：不新增科技节点，直接挂进原版物流科技（用户要求） ----------
                // 转化桌→基础物流系统(1601)、转化箱→高效物流系统(1603)、转化站→星际物流系统(1605)。
                // LDBTool 会在注入配方时自动把配方 ID 追加到对应科技的 UnlockRecipes。
                // 科技挂接（用户指定映射）：转化箱→基础物流(1601)、转化桌→高效物流(1603)、转化站→星际物流(1605)
                TechProto tableTech = LDB.techs?.Select(1603);   // 高效物流系统 → 转化桌
                TechProto boxTech = LDB.techs?.Select(1601);     // 基础物流系统 → 转化箱
                TechProto stationTech = LDB.techs?.Select(1605); // 星际物流系统 → 转化站

                // 合成器位置（用户指定）：三个配方的手搓格放在高斯机枪塔(3001)手搓格的正下方
                int gaussGrid = FindGaussTurretGrid();
                int[] recipeGrids = AllocateGridsBelow(gaussGrid, 3);

                // ---------- 紫色模型：克隆模板建筑模型并整体染紫，不与原版共用外观 ----------
                int tableModel = RegisterPurpleModel(frac, "转化桌-紫", 0.35f); // 淡紫：保留大部分原色（用户反馈全紫太丑）

                var table = new ItemProto
                {
                    ID = itemId,
                    Name = "转化桌",
                    SID = "转化桌",
                    Description = "向内投入任意物品即可转化为 EMC 存储，再从 EMC 中换取需要的物品。" +
                                  "所有转化桌共享同一个 EMC 空间，销毁建筑不会损失数据。",
                    IconPath = frac.IconPath,
                    ModelIndex = tableModel,
                    ModelCount = frac.ModelCount,
                    IsEntity = true,
                    CanBuild = true,
                    BuildInGas = frac.BuildInGas,
                    HpMax = frac.HpMax,
                    StackSize = 20,
                    BuildMode = frac.BuildMode,
                    GridIndex = frac.GridIndex,
                    UnlockKey = itemId, // 自引用：ItemUnlocked 会检查本物品自己的配方是否解锁（开局锁定，研究后解锁）
                    DescFields = frac.DescFields,
                    Upgrades = new int[0], // 必须非空：IsSimilar 等原版逻辑会直接遍历
                    BuildIndex = tableBI,
                    handcraftProductCount = 1
                };

                // 配方 = 分馏器配方 ×3（动态读取原版配方数量；读不到时用写死值兜底）
                var tableR = ScaleTemplateRecipe(frac,
                    new[] { ResolveIdOr("steel", "钢材", 1103), ResolveIdOr("stone brick", "石材", 1108), ResolveIdOr("glass", "玻璃", 1110), ResolveIdOr("processor", "处理器", 1303) },
                    new[] { 24, 12, 12, 3 });
                var recipe = new RecipeProto
                {
                    ID = recipeId,
                    Name = "制作转化桌",
                    SID = "制作转化桌",
                    Description = "制作一台转化桌。",
                    Type = ERecipeType.Assemble,
                    Handcraft = true,
                    Explicit = true, // 手搓界面按 Explicit 直接显示，tooltip 也走配方分支
                    GridIndex = recipeGrids[0], // 手搓界面：高斯机枪塔下方（用户指定）
                    TimeSpend = tableR.timeSpend > 0 ? tableR.timeSpend : 240,
                    Items = tableR.items,
                    ItemCounts = tableR.counts,
                    Results = new[] { itemId },
                    ResultCounts = new[] { 1 },
                    IconPath = frac.IconPath,
                    preTech = tableTech // 高效物流系统解锁
                };

                CommitBuilding(table, recipe);
                TableProto = table;   // 持有引用，ID 被 LDBTool 配置改写也不影响定位
                TableRecipe = recipe;
                TableTemplate = frac;

                // ---------- 转化箱：储物仓模型，爪子进出物品，箱内物品自动收购为 EMC ----------
                ItemProto boxSrc = FindSmallStorageBox();
                if (boxSrc == null)
                {
                    Log.LogWarning("找不到储物仓原型，转化箱未注册");
                }
                else
                {
                    int boxModel = RegisterPurpleModel(boxSrc, "转化箱-紫");
                    var box = new ItemProto
                    {
                        ID = boxItemId,
                        Name = "转化箱",
                        SID = "转化箱",
                        Description = "储物仓造型的转化设施：用分拣器把任意物品送进箱内即自动转化为 EMC，" +
                                      "在界面中取出的物品会出现在箱内，再用分拣器取走即可。EMC 余额全宇宙共享。",
                        IconPath = boxSrc.IconPath,
                        ModelIndex = boxModel,
                        ModelCount = boxSrc.ModelCount,
                        IsEntity = true,
                        CanBuild = true,
                        BuildInGas = boxSrc.BuildInGas,
                        HpMax = boxSrc.HpMax,
                        StackSize = 20,
                        BuildMode = boxSrc.BuildMode,
                        GridIndex = boxSrc.GridIndex,
                        UnlockKey = boxItemId, // 自引用：跟随本物品配方的解锁状态
                        DescFields = boxSrc.DescFields,
                        Upgrades = new int[0],
                        BuildIndex = boxBI,
                        handcraftProductCount = 1
                    };
                    // 配方 = 小型储物仓配方 ×3
                    var boxR = ScaleTemplateRecipe(boxSrc,
                        new[] { ResolveIdOr("iron ingot", "铁块", 1101), ResolveIdOr("stone brick", "石材", 1108) },
                        new[] { 12, 12 });
                    var boxRecipe = new RecipeProto
                    {
                        ID = boxRecipeId,
                        Name = "制作转化箱",
                        SID = "制作转化箱",
                        Description = "制作一台转化箱。",
                        Type = ERecipeType.Assemble,
                        Handcraft = true,
                        Explicit = true,
                        GridIndex = recipeGrids[1], // 手搓界面：高斯机枪塔下方（用户指定）
                        TimeSpend = boxR.timeSpend > 0 ? boxR.timeSpend : 120,
                        Items = boxR.items,
                        ItemCounts = boxR.counts,
                        Results = new[] { boxItemId },
                        ResultCounts = new[] { 1 },
                        IconPath = boxSrc.IconPath,
                        preTech = boxTech // 基础物流系统解锁
                    };
                    CommitBuilding(box, boxRecipe);
                    BoxProto = box;
                    BoxRecipe = boxRecipe;
                    BoxTemplate = boxSrc;
                }

                // ---------- 转化站：行星内物流站模型，物流无人机通过站内槽位进出物品 ----------
                ItemProto stationSrc = FindPlanetStation();
                if (stationSrc == null)
                {
                    Log.LogWarning("找不到行星内物流站原型，转化站未注册");
                }
                else
                {
                    int stModel = RegisterPurpleModel(stationSrc, "转化站-紫");
                    var station = new ItemProto
                    {
                        ID = stItemId,
                        Name = "转化站",
                        SID = "转化站",
                        Description = "行星内物流站造型的转化设施：在界面中把槽位设为「收购」，物流无人机会把全星球" +
                                      "该物品送来并转化为 EMC；设为「供应」则用 EMC 生成物品供无人机运往全星球。" +
                                      "EMC 余额全宇宙共享。需要电力驱动物流无人机。",
                        IconPath = stationSrc.IconPath,
                        ModelIndex = stModel,
                        ModelCount = stationSrc.ModelCount,
                        IsEntity = true,
                        CanBuild = true,
                        BuildInGas = stationSrc.BuildInGas,
                        HpMax = stationSrc.HpMax,
                        StackSize = 20,
                        BuildMode = stationSrc.BuildMode,
                        GridIndex = stationSrc.GridIndex,
                        UnlockKey = stItemId, // 自引用：跟随本物品配方的解锁状态
                        DescFields = stationSrc.DescFields,
                        Upgrades = new int[0],
                        BuildIndex = stationBI,
                        handcraftProductCount = 1
                    };
                    // 配方 = 行星内物流运输站配方 ×3
                    var stR = ScaleTemplateRecipe(stationSrc,
                        new[] { ResolveIdOr("steel", "钢材", 1103), ResolveIdOr("titanium ingot", "钛块", 1106), ResolveIdOr("processor", "处理器", 1303), ResolveIdOr("particle container", "粒子容器", 1206) },
                        new[] { 120, 120, 120, 60 });
                    var stRecipe = new RecipeProto
                    {
                        ID = stRecipeId,
                        Name = "制作转化站",
                        SID = "制作转化站",
                        Description = "制作一座转化站。",
                        Type = ERecipeType.Assemble,
                        Handcraft = true,
                        Explicit = true,
                        GridIndex = recipeGrids[2], // 手搓界面：高斯机枪塔下方（用户指定）
                        TimeSpend = stR.timeSpend > 0 ? stR.timeSpend : 600,
                        Items = stR.items,
                        ItemCounts = stR.counts,
                        Results = new[] { stItemId },
                        ResultCounts = new[] { 1 },
                        IconPath = stationSrc.IconPath,
                        preTech = stationTech // 星际物流系统解锁
                    };
                    CommitBuilding(station, stRecipe);
                    StationProto = station;
                    StationRecipe = stRecipe;
                    StationTemplate = stationSrc;
                }
            }
            catch (Exception e)
            {
                Diag.Error("Proto 注册", e);
            }
        }

        // ----------------------------------------------------------------------
        // 紫色模型 / 科技 / 模板配方辅助
        // ----------------------------------------------------------------------

        /// <summary>
        /// 克隆模板建筑的模型并整体染紫，注册为新 ModelProto，返回新 modelIndex（失败返回原 modelIndex 保底）。
        /// 先按原 PrefabPath 走一遍 Preload（拿到完整残骸/废墟接线），再用染色克隆体覆盖 prefabDesc——
        /// 即使后续被重置也只是外观回退原色，功能不受影响。
        /// </summary>
        private static int RegisterPurpleModel(ItemProto src, string modelName, float tintStrength = 1f)
        {
            try
            {
                var srcModel = LDB.models.modelArray[src.ModelIndex] ?? LDB.models.Select(src.ModelIndex);
                if (srcModel == null || string.IsNullOrEmpty(srcModel.PrefabPath))
                {
                    Log.LogWarning($"紫色模型: 找不到模板模型 {src.Name} (ModelIndex={src.ModelIndex})，保持原外观");
                    return src.ModelIndex;
                }
                var srcGo = Resources.Load<GameObject>(srcModel.PrefabPath);
                if (srcGo == null)
                {
                    Log.LogWarning($"紫色模型: Prefab 加载失败 {srcModel.PrefabPath}，保持原外观");
                    return src.ModelIndex;
                }

                int newId = ModelProto.maxModelIndex + 1;
                while (newId < 2048 && LDB.models.Select(newId) != null) newId++;
                if (newId >= 2048) return src.ModelIndex;

                var go = UnityEngine.Object.Instantiate(srcGo);
                go.name = modelName;
                go.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(go);
                PurpleModelTint.Apply(go, tintStrength);

                var mp = new ModelProto
                {
                    ID = newId,
                    Name = modelName,
                    SID = modelName,
                    OverrideName = modelName,
                    ObjectType = srcModel.ObjectType,
                    RuinType = srcModel.RuinType,
                    RendererType = srcModel.RendererType,
                    RotSymmetry = srcModel.RotSymmetry,
                    HpMax = srcModel.HpMax,
                    HpUpgrade = srcModel.HpUpgrade,
                    HpRecover = srcModel.HpRecover,
                    RuinId = srcModel.RuinId,
                    RuinCount = srcModel.RuinCount,
                    RuinLifeTime = srcModel.RuinLifeTime,
                    PrefabPath = srcModel.PrefabPath
                };
                mp.Preload(); // 原版路径：完整 prefabDesc + 残骸/废墟接线（保底外观）
                mp.prefabDesc = new PrefabDesc(newId, go); // 覆盖为紫色克隆版
                if (srcModel.prefabDesc != null)
                {
                    mp.prefabDesc.wreckagePrefab = srcModel.prefabDesc.wreckagePrefab;
                    mp.prefabDesc.wreckagePrefabs = srcModel.prefabDesc.wreckagePrefabs;
                    mp.prefabDesc.wreckageFragments = srcModel.prefabDesc.wreckageFragments;
                }
                try
                {
                    if (mp.prefabDesc.lodMeshes != null && mp.prefabDesc.lodMeshes.Length > 0)
                        mp.meshBounds = mp.prefabDesc.lodMeshes[0].bounds;
                    else if (mp.prefabDesc.mesh != null)
                        mp.meshBounds = mp.prefabDesc.mesh.bounds;
                }
                catch { }

                // 注册进 LDB.models：dataArray 追加 + modelArray(ID 索引) + dataIndices + 静态索引
                var set = LDB.models;
                int oldLen = set.dataArray.Length;
                Array.Resize(ref set.dataArray, oldLen + 1);
                set.dataArray[oldLen] = mp;
                var ma = new ModelProto[Math.Max(2048, newId + 1)];
                for (int i = 0; i < set.dataArray.Length; i++)
                {
                    var m = set.dataArray[i];
                    if (m != null && m.ID < ma.Length) ma[m.ID] = m;
                }
                set.modelArray = ma;
                var diField = AccessTools.Field(typeof(ProtoSet<ModelProto>), "dataIndices");
                if (diField != null)
                {
                    if (diField.GetValue(set) is Dictionary<int, int> di) di[newId] = oldLen;
                    else diField.SetValue(set, new Dictionary<int, int> { { newId, oldLen } });
                }
                ModelProto.maxModelIndex = newId;
                if (ModelProto.modelIndices != null && newId < ModelProto.modelIndices.Length)
                    ModelProto.modelIndices[newId] = newId;
                if (ModelProto.modelOrders != null && newId < ModelProto.modelOrders.Length)
                    ModelProto.modelOrders[newId] = 99999;

                Log.LogInfo($"紫色模型注册成功: {modelName} modelIndex={newId} (源 {srcModel.ID}/{srcModel.PrefabPath})");
                return newId;
            }
            catch (Exception e)
            {
                Diag.Error("紫色模型注册", e);
                return src.ModelIndex;
            }
        }

        /// <summary>
        /// 提交一个转化建筑：注入物品与配方，并把产物绑定/自引用同步到最终 ID。
        /// LDBTool 会改写与其它 Mod/原版冲突的物品 ID（如转化桌 9701 → 11500），
        /// 但不会同步配方里的 Results —— 不同步就会产出不存在的旧 ID（Unknown item）。
        /// </summary>
        private static void CommitBuilding(ItemProto item, RecipeProto recipe)
        {
            LDBTool.PreAddProto(item);
            LDBTool.PreAddProto(recipe);
            recipe.Results = new[] { item.ID };
            recipe.ResultCounts = new[] { 1 };
            item.UnlockKey = item.ID;
            Log.LogInfo($"{item.Name} Proto 注册完成 Item={item.ID} Recipe={recipe.ID} ModelIndex={item.ModelIndex} 配方={string.Join("+", recipe.ItemCounts)}");
        }

        /// <summary>
        /// 读取模板建筑的制作配方，把原料数量 ×3（用户规则）。找不到时返回 null 由调用方走写死兜底。
        /// </summary>
        private static (int[] items, int[] counts, int timeSpend, int gridIndex) ScaleTemplateRecipe(ItemProto src, int[] fallbackItems, int[] fallbackCounts)
        {
            try
            {
                var rarr = LDB.recipes?.dataArray;
                if (rarr != null && src != null)
                {
                    foreach (var r in rarr)
                    {
                        if (r?.Results == null || r.Items == null || r.ItemCounts == null) continue;
                        if (r.Items.Length == 0 || Array.IndexOf(r.Results, src.ID) < 0) continue;
                        var counts = (int[])r.ItemCounts.Clone();
                        for (int i = 0; i < counts.Length; i++) counts[i] = Math.Max(1, counts[i] * 3);
                        return ((int[])r.Items.Clone(), counts, r.TimeSpend, r.GridIndex);
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"模板配方读取失败（{src?.Name}），用写死值兜底: {e.Message}");
            }
            return (fallbackItems, fallbackCounts, 0, 0);
        }

        /// <summary>找高斯机枪塔(物品3001)的手搓配方格（用户指定：三个转化配方放在它下方）</summary>
        private static int FindGaussTurretGrid()
        {
            foreach (var r in LDB.recipes?.dataArray ?? Array.Empty<RecipeProto>())
            {
                if (r?.Results == null || r.GridIndex < 1101) continue;
                if (Array.IndexOf(r.Results, 3001) >= 0) return r.GridIndex;
            }
            return 0;
        }

        /// <summary>
        /// 在 baseGrid 正下方一行起，按行内顺序分配 count 个空格（跳过占用，行满换行，页满换页）。
        /// </summary>
        private static int[] AllocateGridsBelow(int baseGrid, int count)
        {
            var used = new HashSet<int>();
            foreach (var r in LDB.recipes?.dataArray ?? Array.Empty<RecipeProto>())
                if (r != null && r.GridIndex > 0) used.Add(r.GridIndex);

            int type = Math.Max(1, baseGrid / 1000);
            int row = Math.Max(1, (baseGrid % 1000) / 100) + 1; // 下一行 = 下方
            int col = Math.Max(1, baseGrid % 100);
            var result = new int[count];
            for (int n = 0; n < count; n++)
            {
                bool found = false;
                for (int guard = 0; guard < 400 && !found; guard++)
                {
                    int g = type * 1000 + row * 100 + col;
                    if (col <= 14 && row <= 8 && !used.Contains(g))
                    {
                        used.Add(g);
                        result[n] = g;
                        found = true;
                    }
                    col++;
                    if (col > 14) { col = 1; row++; }
                    if (row > 8) { row = 1; type++; }
                }
                if (!found) result[n] = baseGrid >= 1101 ? baseGrid : 1101;
            }
            return result;
        }

        /// <summary>
        /// 按模型特征找行星内物流站。
        /// 注意：矿脉采集器/轨道采集器也带 StationComponent（isStation=true），
        /// 必须排除 isCollectStation / isVeinCollector / isStellarStation，否则会拿到"大型矿机"外观。
        /// </summary>
        private static ItemProto FindPlanetStation()
        {
            var arr = LDB.items?.dataArray;
            ItemProto best = null;
            if (arr != null)
            {
                foreach (var p in arr)
                {
                    try
                    {
                        if (p?.prefabDesc == null) continue;
                        if (!p.prefabDesc.isStation) continue;
                        if (p.prefabDesc.isStellarStation) continue;   // 星际物流站
                        if (p.prefabDesc.isCollectStation) continue;   // 轨道采集器
                        if (p.prefabDesc.isVeinCollector) continue;    // 矿脉采集器（大型矿机外观）
                        best = p; // 行星内物流站
                        break;
                    }
                    catch { }
                }
            }
            var byName = FindItemByName("行星内物流站") ??
                         FindItemByName("行星内物流运输站") ??
                         FindItemByName("Planetary Logistics Station");
            var chosen = best ?? byName;
            if (chosen != null)
                Log.LogInfo($"转化站模板: ID={chosen.ID} Name={chosen.Name} ModelIndex={chosen.ModelIndex} " +
                            $"maxItemKinds={(chosen.prefabDesc != null ? chosen.prefabDesc.stationMaxItemKinds : -1)}");
            else
                Log.LogWarning("未找到行星内物流站原型，转化站可能未注册");
            return chosen;
        }

        /// <summary>按模型特征找小型储物仓（isStorage 中容量最小的）</summary>
        private static ItemProto FindSmallStorageBox()
        {
            var arr = LDB.items?.dataArray;
            ItemProto best = null;
            int bestCap = int.MaxValue;
            if (arr != null)
            {
                foreach (var p in arr)
                {
                    try
                    {
                        if (p?.prefabDesc != null && p.prefabDesc.isStorage)
                        {
                            int cap = p.prefabDesc.storageCol * p.prefabDesc.storageRow;
                            if (cap > 0 && cap < bestCap)
                            {
                                best = p;
                                bestCap = cap;
                            }
                        }
                    }
                    catch { }
                }
            }
            return best ?? FindItemByName("储物仓");
        }

        /// <summary>PostAdd 阶段：对新增 Proto 执行完整 Preload 注册（byId 表/prefabDesc/图标/配方），并回读实际 ID</summary>
        private static void AfterProtosAdded()
        {
            try
            {
                PreloadProto(TableProto, ref TableItemId);
                PreloadRecipe(TableRecipe);
                TablePatches.TableRecipeId = TableRecipe?.ID ?? 0;
                // 配方解锁走原版物流科技，回填实际配方 ID 供读档同步用（桌→高效1603、箱→基础1601、站→星际1605）
                TablePatches.RecipeTechPairs = new (int, int)[]
                {
                    (TableRecipe?.ID ?? 0, 1603),
                    (BoxRecipe?.ID ?? 0, 1601),
                    (StationRecipe?.ID ?? 0, 1605)
                };
                // 物品→配方显式映射（ItemUnlocked 接管用，杜绝顺序假设错位）
                TablePatches.ItemRecipeMap.Clear();
                if (TableRecipe != null) TablePatches.ItemRecipeMap[TableItemId] = TableRecipe.ID;
                if (BoxRecipe != null) TablePatches.ItemRecipeMap[BoxItemId] = BoxRecipe.ID;
                if (StationRecipe != null) TablePatches.ItemRecipeMap[StationItemId] = StationRecipe.ID;

                // 转化箱 + 转化站
                PreloadProto(BoxProto, ref BoxItemId);
                PreloadRecipe(BoxRecipe);
                PreloadProto(StationProto, ref StationItemId);
                PreloadRecipe(StationRecipe);

                // 统一校验产物绑定：配方 Results 必须等于对应物品的最终 ID
                // （LDBTool 改写物品 ID 时不会同步 Results，产物会指向不存在的旧 ID → Unknown item）
                FixRecipeBindings();

                // 配方挂接确认 + 兜底补挂（用户指定映射）：
                // 转化箱→基础物流(1601)、转化桌→高效物流(1603)、转化站→星际物流(1605)
                EnsureRecipeAttached(BoxRecipe, 1601);
                EnsureRecipeAttached(TableRecipe, 1603);
                EnsureRecipeAttached(StationRecipe, 1605);

                // 紫色图标：物品 + 配方都用染色版，替代原版图标（同时解决部分模板图标加载失败的白块问题）
                ApplyPurpleIcon(TableProto, TableRecipe, TableTemplate);
                ApplyPurpleIcon(BoxProto, BoxRecipe, BoxTemplate);
                ApplyPurpleIcon(StationProto, StationRecipe, StationTemplate);

                // 配方链重建：自注册物品的 FindRecipes 在 Preload 时机没有正确关联手搓配方
                // （maincraft/handcraft 为空会导致建造菜单点击/双击定位合成器等原版功能异常）
                RebuildItemRecipeLinks(TableProto, TableRecipe);
                RebuildItemRecipeLinks(BoxProto, BoxRecipe);
                RebuildItemRecipeLinks(StationProto, StationRecipe);

                // 紫色模型是运行时注入的，原版 PrefabDescByModelIndex 早已建好（主菜单阶段）→ 补齐
                ProtoGuard.EnsurePrefabDescArrays();

                // 建造菜单落位：不在注册期调用 SetBuildBar（此时 staticLoaded 可能已为 true，
                // 直接写 protos 会产生错误坐标）。统一交给 BuildMenuInstaller 在主循环里安装。
                Log.LogInfo($"转化设施就绪: 桌={TableItemId} 箱={BoxItemId} 站={StationItemId} 配方={TablePatches.TableRecipeId}");
            }
            catch (Exception e)
            {
                Diag.Error("Preload", e);
            }
        }

        /// <summary>清空并重建物品的配方关联（recipes/maincraft/handcraft/rawMats/preTech），此时配方已全部注入。
        /// 若 FindRecipes 仍未关联（自注册物品的时序断点），直接手动补链。</summary>
        private static void RebuildItemRecipeLinks(ItemProto item, RecipeProto ownRecipe)
        {
            try
            {
                if (item == null || ownRecipe == null) return;
                var tv = Traverse.Create(item);
                tv.Field("recipes").SetValue(null);
                tv.Field("handcrafts").SetValue(null);
                tv.Field("makes").SetValue(null);
                tv.Field("handcraft").SetValue(null);
                tv.Field("maincraft").SetValue(null);
                tv.Field("rawMats").SetValue(null);
                tv.Method("FindRecipes").GetValue();

                // 兜底：FindRecipes 没关联上（如转化桌 ID 被重映射的场景）→ 手动补链
                if (item.recipes == null || item.recipes.Count == 0)
                {
                    tv.Field("recipes").SetValue(new List<RecipeProto> { ownRecipe });
                    tv.Field("maincraft").SetValue(ownRecipe);
                    tv.Field("maincraftProductCount").SetValue(ownRecipe.ResultCounts != null && ownRecipe.ResultCounts.Length > 0 ? ownRecipe.ResultCounts[0] : 1);
                    tv.Field("handcrafts").SetValue(new List<RecipeProto> { ownRecipe });
                    tv.Field("handcraft").SetValue(ownRecipe);
                    tv.Field("handcraftProductCount").SetValue(1);
                    tv.Field("isRaw").SetValue(false);
                    tv.Field("rawMats").SetValue(null);
                    tv.Method("ComputeRawMats").GetValue();
                    Log.LogWarning($"配方链手动补齐: {item.Name}({item.ID}) ← 配方 {ownRecipe.ID}");
                }

                tv.Method("FindPreTech").GetValue();
            }
            catch (Exception e)
            {
                Diag.Error("配方链重建", e);
            }
        }

        /// <summary>数组追加元素（返回新数组）</summary>
        private static T[] ArrayAddItem<T>(T[] array, T item)
        {
            var dst = new T[array.Length + 1];
            Array.Copy(array, dst, array.Length);
            dst[array.Length] = item;
            return dst;
        }

        /// <summary>
        /// 用模板建筑的图标生成紫色版 Sprite，覆盖物品、配方与模型的 _iconSprite。
        /// 物品图标用于科技树/tooltip，模型图标用于建造菜单等以 modelIndex 取图的 UI，
        /// 配方图标用于手搓界面与科技解锁图标。
        /// </summary>
        private static void ApplyPurpleIcon(ItemProto item, RecipeProto recipe, ItemProto template)
        {
            try
            {
                // 源回退链：模板物品图标 → 模板模型图标（Icons/Model/model-{ID}，建造菜单图标来源）
                // 例：分馏塔的物品 iconSprite 是空白残片，必须回退到模型 119 的图标
                Sprite sp = null;
                var src = template?.iconSprite;
                if (src != null) sp = PurpleModelTint.CreatePurpleIcon(src, 0.5f);
                if (sp == null && template != null)
                {
                    var model = LDB.models?.Select(template.ModelIndex);
                    var mSrc = model?.iconSprite;
                    if (mSrc != null)
                    {
                        Log.LogInfo($"紫色图标: 物品图标源无效，回退到模型 {template.ModelIndex} 图标 ({mSrc.name})");
                        sp = PurpleModelTint.CreatePurpleIcon(mSrc, 0.5f);
                    }
                }
                if (sp == null)
                {
                    Log.LogWarning($"紫色图标: {template?.Name} 所有图标源均无效，保持原图标");
                    return;
                }
                if (item != null) Traverse.Create(item).Field("_iconSprite").SetValue(sp);
                if (recipe != null) Traverse.Create(recipe).Field("_iconSprite").SetValue(sp);
                // 同步模型图标（建造菜单部分 UI 以模型图标为准）
                try
                {
                    var model = LDB.models?.Select(item.ModelIndex);
                    model?.OverrideIconSprite(sp);
                }
                catch { }
            }
            catch (Exception e)
            {
                Diag.Error("紫色图标", e);
            }
        }

        /// <summary>
        /// 确认配方已挂进目标科技的解锁列表（UnlockRecipes + unlockRecipeArray），
        /// 缺失时补挂（幂等）。原版 UI 槽位扩容见 TechTreeSlotExtendPatch。
        /// </summary>
        private static void EnsureRecipeAttached(RecipeProto recipe, int techId)
        {
            if (recipe == null) return;
            var tech = LDB.techs?.Select(techId);
            if (tech == null)
            {
                Log.LogWarning($"配方挂接失败: 科技 {techId} 不存在，{recipe.Name}({recipe.ID}) 无法挂接");
                return;
            }
            bool inIds = tech.UnlockRecipes != null && Array.IndexOf(tech.UnlockRecipes, recipe.ID) >= 0;
            bool inArr = tech.unlockRecipeArray != null && Array.IndexOf(tech.unlockRecipeArray, recipe) >= 0;
            if (!inIds)
                tech.UnlockRecipes = tech.UnlockRecipes == null
                    ? new[] { recipe.ID }
                    : ArrayAddItem(tech.UnlockRecipes, recipe.ID);
            if (!inArr)
                tech.unlockRecipeArray = tech.unlockRecipeArray == null
                    ? new[] { recipe }
                    : ArrayAddItem(tech.unlockRecipeArray, recipe);
            Log.LogInfo($"配方挂接确认: {recipe.Name}({recipe.ID}) → {tech.Name}({tech.ID}) " +
                        $"{(inIds && inArr ? "LDBTool已挂接" : "已补挂")}，该科技现有 {tech.unlockRecipeArray.Length} 条配方");
        }

        private static void PreloadProto(ItemProto proto, ref int idHolder)
        {            if (proto == null) return;
            var arr = LDB.items.dataArray;
            int sameIdCount = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] != null && arr[i].ID == proto.ID && !ReferenceEquals(arr[i], proto)) sameIdCount++;
                if (ReferenceEquals(arr[i], proto))
                    proto.Preload(i); // 注册 itemProtoById、绑定 prefabDesc、加载图标
            }
            if (sameIdCount > 0)
                Log.LogError($"物品 ID {proto.ID}（{proto.Name}）与其他物品冲突！请删除 BepInEx/config/LDBTool/LDBTool.CustomID.cfg 后重启");
            idHolder = proto.ID;
        }

        private static void PreloadRecipe(RecipeProto recipe)
        {
            if (recipe == null) return;
            var rarr = LDB.recipes.dataArray;
            for (int i = 0; i < rarr.Length; i++)
            {
                if (ReferenceEquals(rarr[i], recipe))
                {
                    recipe.Preload(i);
                    break;
                }
            }
        }

        /// <summary>按模型特征找分馏器（不依赖本地化名字），失败再按名字/ID 兜底</summary>
        private static ItemProto FindFractionator()
        {
            var arr = LDB.items?.dataArray;
            if (arr != null)
            {
                foreach (var p in arr)
                {
                    try
                    {
                        if (p?.prefabDesc != null && p.prefabDesc.isFractionator)
                            return p;
                    }
                    catch { /* prefabDesc 尚未构建时忽略 */ }
                }
            }
            return FindItemByName("Fractionator") ?? FindItemByName("分馏器") ?? LDB.items.Select(2311);
        }

        private static ItemProto FindItemByName(string name)
        {
            var arr = LDB.items?.dataArray;
            if (arr == null) return null;
            foreach (var p in arr)
            {
                if (p == null) continue;
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
                if (string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)) return p; // 已本地化名
            }
            return null;
        }

        private static int ResolveIdOr(string englishName, string chineseName, int fallback)
        {
            var p = FindItemByName(englishName) ?? FindItemByName(chineseName);
            return p != null ? p.ID : fallback;
        }

        private static HashSet<int> GetUsedItemIds()
        {
            var set = new HashSet<int>();
            foreach (var p in LDB.items?.dataArray ?? Array.Empty<ItemProto>())
                if (p != null) set.Add(p.ID);
            return set;
        }

        private static HashSet<int> GetUsedRecipeIds()
        {
            var set = new HashSet<int>();
            foreach (var r in LDB.recipes?.dataArray ?? Array.Empty<RecipeProto>())
                if (r != null) set.Add(r.ID);
            return set;
        }

        /// <summary>校验并修复三对 配方→物品 的 Results 绑定（LDBTool ID 重映射后必须同步）</summary>
        internal static void FixRecipeBindings()
        {
            FixOne(TableRecipe, TableProto, "转化桌");
            FixOne(BoxRecipe, BoxProto, "转化箱");
            FixOne(StationRecipe, StationProto, "转化站");
        }

        private static void FixOne(RecipeProto recipe, ItemProto item, string label)
        {
            try
            {
                if (recipe == null || item == null) return;
                if (recipe.Results != null && recipe.Results.Length == 1 && recipe.Results[0] == item.ID) return;
                Log.LogWarning($"[{label}] 配方 {recipe.ID} 产物绑定错误: Results={string.Join(",", recipe.Results ?? new int[0])} → 修正为物品 ID {item.ID}");
                recipe.Results = new[] { item.ID };
                recipe.ResultCounts = new[] { 1 };
            }
            catch (Exception e)
            {
                Log.LogError($"FixOne({label}) 异常: {e}");
            }
        }

        private static int FindFreeId(HashSet<int> used, int start, int max)
        {
            for (int id = start; id <= max; id++)
                if (!used.Contains(id)) return id;
            return start;
        }

        // ------------------------------------------------------------------
        // 存档（DSPModSave）：EMC 全部数据跟着存档走 = 跟着玩家走
        // ------------------------------------------------------------------
        public void Export(BinaryWriter w)
        {
            EmcSystem.Export(w);
        }

        public void Import(BinaryReader r)
        {
            EmcSystem.Import(r);
        }

        public void IntoOtherSave()
        {
            EmcSystem.ResetPlayerData();
        }

        // ------------------------------------------------------------------
        // IMGUI 界面（仅当玩家查看转化桌时显示）
        // ------------------------------------------------------------------
        private void OnGUI()
        {
            try
            {
                EmcWindow.OnGUIWindow();
            }
            catch (Exception e)
            {
                Diag.Error("F9 GUI", e);
                EmcWindow.Close();
            }
        }
    }
}
