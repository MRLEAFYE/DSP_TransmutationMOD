using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TransmutationTable
{
    /// <summary>
    /// 核心逻辑补丁。
    /// 转化桌复用分馏器的模型（带传送带端口），引擎会自动为它建 FractionatorComponent 并绑定传送带；
    /// 我们拦截分馏器的原始行为，替换为 EMC 收购 / 产出逻辑：
    ///   - 输入带（isOutput=false）：从带尾取任意物品 → 按 EMC 折算入账
    ///   - 输出带（isOutput=true） ：按 GUI 配置把 EMC 换成物品，从带头插入（每 tick 尝试，传送带满速）
    ///   - 电力需求清零：转化桌是魔法建筑，不耗电
    /// </summary>
    [HarmonyPatch]
    public static class TablePatches
    {
        /// <summary>注册后回读的实际 Proto ID（LDBTool 允许用户改配置，实际值可能不同）</summary>
        public static int TableRecipeId = 9702;

        // ------------------------------------------------------------------
        // 配方解锁 ↔ 原版物流科技状态同步
        //   转化桌→高效物流(1603)、转化箱→基础物流(1601)、转化站→星际物流(1605)
        // 读档后校正 recipeUnlocked 集合（旧存档里配方曾被强制解锁/科技状态变化都能纠正）
        // ------------------------------------------------------------------

        /// <summary>(配方ID, 科技ID) 对；配方 ID 在注册后由 Plugin 回填</summary>
        public static (int recipe, int tech)[] RecipeTechPairs = new (int, int)[]
        {
            (9702, 1603), (9704, 1601), (9706, 1605)
        };

        /// <summary>物品ID → 配方ID 显式映射（注册后由 Plugin 回填，避免顺序假设错位）</summary>
        public static readonly Dictionary<int, int> ItemRecipeMap = new Dictionary<int, int>();

        /// <summary>把三个转化配方的解锁状态同步成对应科技的解锁状态</summary>
        public static void SyncRecipeUnlocks(GameHistoryData h)
        {
            try
            {
                if (h?.recipeUnlocked == null) return;
                foreach (var (recipeId, techId) in RecipeTechPairs)
                {
                    if (recipeId <= 0) continue;
                    var tech = LDB.techs?.Select(techId);
                    bool techOk = tech == null || h.TechUnlocked(tech.ID); // 找不到科技时放行，避免误锁
                    bool recOk = h.recipeUnlocked.Contains(recipeId);
                    if (techOk && !recOk)
                    {
                        h.UnlockRecipe(recipeId);
                        TransmutationTablePlugin.Log.LogInfo($"[解锁同步] 科技{techId} 已解锁 → 补解锁配方 {recipeId}");
                    }
                    else if (!techOk && recOk)
                    {
                        h.recipeUnlocked.Remove(recipeId);
                        TransmutationTablePlugin.Log.LogInfo($"[解锁同步] 科技{techId} 未解锁 → 锁定配方 {recipeId}");
                    }
                }
            }
            catch (Exception e)
            {
                Diag.Error("SyncRecipeUnlocks", e);
            }
        }

        /// <summary>研究/解锁科技完成后的兜底：强制解锁挂在该科技上的转化配方（防 UnlockRecipes 链路断点）</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameHistoryData), nameof(GameHistoryData.UnlockTechUnlimited))]
        public static void GameHistoryData_UnlockTechUnlimited_Postfix(GameHistoryData __instance, int techId)
        {
            try
            {
                if (__instance?.recipeUnlocked == null) return;
                foreach (var (recipeId, t) in RecipeTechPairs)
                {
                    if (t != techId || recipeId <= 0) continue;
                    if (!__instance.recipeUnlocked.Contains(recipeId))
                    {
                        __instance.UnlockRecipe(recipeId);
                        TransmutationTablePlugin.Log.LogInfo($"[解锁兜底] 科技 {techId} 解锁完成，强制解锁配方 {recipeId}");
                    }
                }
            }
            catch (Exception e)
            {
                Diag.Error("UnlockTechUnlimited 兜底", e);
            }
        }

        private static bool _itemUnlockDiagDone;

        /// <summary>
        /// 建造菜单显示判定接管：三个转化建筑的 ItemUnlocked = 对应配方是否已解锁。
        /// 原版逻辑依赖 item.maincraft（FindRecipes 构建），自注册物品的判定链可能断点，
        /// 这里用我们自己的映射直接给结果，并输出一次性诊断。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameHistoryData), nameof(GameHistoryData.ItemUnlocked))]
        public static void GameHistoryData_ItemUnlocked_Postfix(int itemId, GameHistoryData __instance, ref bool __result)
        {
            try
            {
                // 物品→配方映射（显式字典，回填于 AfterProtosAdded）
                if (ItemRecipeMap.Count == 0 || !ItemRecipeMap.TryGetValue(itemId, out int recipeId)) return;
                if (__instance?.recipeUnlocked == null) return;

                if (!_itemUnlockDiagDone)
                {
                    _itemUnlockDiagDone = true;
                    var proto = LDB.items?.Select(itemId);
                    TransmutationTablePlugin.Log.LogInfo(
                        $"[ItemUnlocked诊断] 物品{itemId}: maincraft={(proto?.maincraft != null ? proto.maincraft.ID.ToString() : "null")}, " +
                        $"recipes={(proto?.recipes != null ? proto.recipes.Count.ToString() : "null")}, " +
                        $"原版判定={__result}, 配方{recipeId}已解锁={__instance.recipeUnlocked.Contains(recipeId)}");
                }
                __result = __instance.recipeUnlocked.Contains(recipeId);
            }
            catch (Exception e)
            {
                Diag.Error("ItemUnlocked 接管", e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameHistoryData), nameof(GameHistoryData.Import))]
        public static void GameHistoryData_Import_Postfix(GameHistoryData __instance)
        {
            SyncRecipeUnlocks(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameHistoryData), nameof(GameHistoryData.SetForNewGame))]
        public static void GameHistoryData_SetForNewGame_Postfix(GameHistoryData __instance)
        {
            SyncRecipeUnlocks(__instance);
        }

        // 转化站端口分配的临时集合（每 tick 复用，避免 GC）
        private static readonly List<int> _suppliedItems = new List<int>();   // 其他塔供应的物品
        private static readonly List<int> _demandedItems = new List<int>();   // 其他塔需求的物品
        private static readonly List<int> _outWants = new List<int>();        // 本站期望的输出端口
        private static readonly List<int> _inWants = new List<int>();         // 本站期望的输入端口
        private static readonly List<int> _beltItems = new List<int>();       // 输入端口带尾学习到的物品
        private static readonly int[] _wantItem = new int[16];
        private static readonly bool[] _wantSupply = new bool[16];
        private static readonly bool[] _wantDrain = new bool[16];

        /// <summary>把物流站槽位里的残留物品折算成 EMC（切换配置/关闭前调用，防止物品凭空丢失；含喷涂点数加价）</summary>
        private static void ConvertSlotToEmc(ref StationStore store)
        {
            if (store.count > 0 && store.itemId > 0)
            {
                EmcSystem.Learn(store.itemId);
                double gained = EmcSystem.EmcOf(store.itemId) * store.count
                    + EmcSystem.SprayCostPerIncPoint(store.inc) * store.inc;
                EmcSystem.Balance += gained;
                StationBuyEarned += gained;
                PickedTotal += store.count;
            }
        }

        // 输出速率累计器：key = 建筑key * 4 + 通道（桌 0-2 / 箱 3）
        private static readonly Dictionary<long, double> _rateAcc = new Dictionary<long, double>();

        private static double TakeRateBudget(long key, int speedPerSec)
        {
            double acc;
            lock (_rateAcc)
            {
                _rateAcc.TryGetValue(key, out acc);
                acc = Math.Min(acc + speedPerSec / 60.0, speedPerSec); // 上限 1 秒的量防积压
                _rateAcc[key] = acc;
            }
            return acc;
        }

        private static void RefundRateBudget(long key, double used)
        {
            lock (_rateAcc)
            {
                _rateAcc.TryGetValue(key, out var acc);
                _rateAcc[key] = acc - used;
            }
        }

        /// <summary>
        /// 魔法建筑不耗电：把功率需求清零，并清掉实体上的"缺电"告示图标（iconType 2）。
        /// </summary>
        private static void ClearPowerDemand(PlanetFactory factory, int entityId, PowerConsumerComponent[] consumerPool)
        {
            try
            {
                if (entityId <= 0 || entityId >= factory.entityPool.Length) return;
                int pcId = factory.entityPool[entityId].powerConId;
                if (pcId > 0 && consumerPool != null && pcId < consumerPool.Length)
                    consumerPool[pcId].requiredEnergy = 0;
                var signs = factory.entitySignPool;
                if (signs != null && entityId < signs.Length)
                {
                    var sign = signs[entityId];
                    if (sign.iconType == 2)
                    {
                        sign.iconType = 0;
                        sign.iconId0 = 0;
                        sign.iconId1 = 0;
                        signs[entityId] = sign;
                    }
                }
            }
            catch (Exception e)
            {
                Diag.Error("缺电告示清理", e);
            }
        }

        // ---- 诊断计数器（GUI 里可见） ----
        public static long TickCount;
        public static long PickedTotal;
        public static long InsertOkTotal;
        public static long InsertFailTotal;
        public static long NoEmcSkipTotal;

        // ---- 转化站资金流水（会话累计，GUI 可见，方便核对有没有"刷钱"） ----
        public static double StationSupplySpent;   // 供应端口购货支出
        public static double StationBuyEarned;     // 输入端口收购收入

        /// <summary>读取某实体当前绑定状态（GUI 用）</summary>
        public static bool TryGetTableBinding(PlanetFactory factory, int entityId, out FractionatorComponent f)
        {
            f = default;
            if (factory?.factorySystem?.fractionatorPool == null) return false;
            int fid = factory.entityPool[entityId].fractionatorId;
            if (fid <= 0 || fid >= factory.factorySystem.fractionatorPool.Length) return false;
            f = factory.factorySystem.fractionatorPool[fid];
            return f.id == fid;
        }

        // ------------------------------------------------------------------
        // 转化站放置时：把 storage 数组从原版的 4 格扩到 12 格。
        // 反编译核对：NewStationComponent 里 storage/priorityLocks 均按
        // prefabDesc.stationMaxItemKinds 分配；全游戏逻辑一律按 storage.Length 遍历，
        // StationComponent.Export/Import 也是动态长度读写 —— 运行时扩容安全且随存档持久。
        // ------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.NewStationComponent))]
        public static void PlanetTransport_NewStationComponent_Postfix(PlanetTransport __instance, int _entityId, StationComponent __result)
        {
            try
            {
                if (__result == null || __result.storage == null || __result.storage.Length >= EmcSystem.StationSlotCount)
                    return;
                if (__instance.factory == null || __instance.factory.entityPool == null ||
                    _entityId <= 0 || _entityId >= __instance.factory.entityPool.Length) return;
                if (__instance.factory.entityPool[_entityId].protoId != TransmutationTablePlugin.StationItemId) return;

                int old = __result.storage.Length;
                var newStorage = new StationStore[EmcSystem.StationSlotCount];
                for (int i = 0; i < old && i < EmcSystem.StationSlotCount; i++) newStorage[i] = __result.storage[i];
                __result.storage = newStorage;
                __result.priorityLocks = new StationPriorityLock[EmcSystem.StationSlotCount];
                // 预装满内置无人机：关闭"无限小飞机"时也有满编机队可用（飞出去会减少，回来恢复）
                if (__result.workDroneDatas != null && __result.workDroneDatas.Length > 0)
                    __result.idleDroneCount = __result.workDroneDatas.Length - __result.workDroneCount;
                TransmutationTablePlugin.Log.LogInfo($"[转化站] 实体 {_entityId} 槽位扩容 {old} → {EmcSystem.StationSlotCount}，预装无人机 {__result.idleDroneCount} 架");
            }
            catch (Exception e)
            {
                Diag.Error("转化站扩容", e);
            }
        }

        // ------------------------------------------------------------------
        // 分拣器直插转化站 → 直接折算 EMC（"喂什么收什么"，等价转化桌的输入带）。
        // 反编译实锤：InsertInto 的 Station 分支要求 entityNeeds 含该物品且槽位 itemId
        // 匹配，空槽一律拒收 —— 所以直接输入必须在 InsertInto 层拦截。
        // 分拣器实际调用的是 typedId 重载；实体 ID 重载一并拦截以防其他入口。
        // ------------------------------------------------------------------
        // 注意：InsertInto 是重载方法且带 out 参数，attribute 无法表达 MakeByRefType，
        // 改为在 RegisterManualPatches() 中手动注册（Plugin.Awake 调用）。
        private static bool PlanetFactory_InsertInto_Typed_Prefix(
            PlanetFactory __instance, uint ioTargetTypedId, int itemId, byte itemCount, byte itemInc, ref byte remainInc, ref int __result)
        {
            // typedId 高 8 位 = EFactoryIOTargetType（Station = 0x6000000 → 6）
            if ((ioTargetTypedId >> 24) != 6u || itemId <= 0 || itemCount <= 0) return true;
            int stationId = (int)(ioTargetTypedId & 0xFFFFFFu);
            return TryBuyStationInsertion(__instance, stationId, itemId, itemCount, itemInc, ref remainInc, ref __result);
        }

        private static bool PlanetFactory_InsertInto_Entity_Prefix(
            PlanetFactory __instance, int entityId, int itemId, byte itemCount, byte itemInc, ref byte remainInc, ref int __result)
        {
            if (entityId <= 0 || entityId >= __instance.entityPool.Length || itemId <= 0 || itemCount <= 0) return true;
            int stationId = __instance.entityPool[entityId].stationId;
            return TryBuyStationInsertion(__instance, stationId, itemId, itemCount, itemInc, ref remainInc, ref __result);
        }

        /// <summary>手动注册无法用 attribute 表达的补丁（InsertInto 重载带 out 参数）</summary>
        public static void RegisterManualPatches(Harmony harmony)
        {
            try
            {
                var byrefByte = typeof(byte).MakeByRefType();

                var typed = AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.InsertInto),
                    new[] { typeof(uint), typeof(int), typeof(int), typeof(byte), typeof(byte), byrefByte });
                if (typed != null)
                    harmony.Patch(typed, prefix: new HarmonyMethod(typeof(TablePatches),
                        nameof(PlanetFactory_InsertInto_Typed_Prefix)));

                var byEntity = AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.InsertInto),
                    new[] { typeof(int), typeof(int), typeof(int), typeof(byte), typeof(byte), byrefByte });
                if (byEntity != null)
                    harmony.Patch(byEntity, prefix: new HarmonyMethod(typeof(TablePatches),
                        nameof(PlanetFactory_InsertInto_Entity_Prefix)));

                TransmutationTablePlugin.Log.LogInfo($"InsertInto 直插拦截注册: typed={typed != null} entity={byEntity != null}");
            }
            catch (Exception e)
            {
                Diag.Error("InsertInto 补丁注册", e);
            }
        }

        /// <summary>目标实体是转化站时把物品折算成 EMC 并"吃掉"；否则放行原版逻辑</summary>
        private static bool TryBuyStationInsertion(PlanetFactory factory, int stationId, int itemId, int itemCount, int itemInc, ref byte remainInc, ref int __result)
        {
            try
            {
                if (stationId <= 0 || factory.transport?.stationPool == null ||
                    stationId >= factory.transport.stationPool.Length) return true;
                var st = factory.transport.stationPool[stationId];
                if (st == null || st.id != stationId || st.entityId <= 0 || st.entityId >= factory.entityPool.Length)
                    return true;
                if (factory.entityPool[st.entityId].protoId != TransmutationTablePlugin.StationItemId)
                    return true; // 普通物流站走原版

                EmcSystem.Learn(itemId);
                double gained = EmcSystem.EmcOf(itemId) * itemCount + EmcSystem.SprayCostPerIncPoint(itemInc) * itemInc;
                EmcSystem.Balance += gained;
                StationBuyEarned += gained;
                PickedTotal += itemCount;
                remainInc = 0;
                __result = itemCount;
                return false; // 跳过原版插入
            }
            catch (Exception e)
            {
                Diag.Error("转化站直插收购", e);
                return true;
            }
        }

        // ------------------------------------------------------------------
        // 拆除转化站时清零内置无人机：拆除会按站内无人机数量退款，
        // 无限小飞机补满的机队不应当变成白送的资源。
        // ------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.RemoveStationComponent))]
        public static void PlanetTransport_RemoveStationComponent_Prefix(PlanetTransport __instance, int id)
        {
            try
            {
                if (__instance?.stationPool == null || id <= 0 || id >= __instance.stationPool.Length) return;
                var st = __instance.stationPool[id];
                if (st == null || st.id != id || st.entityId <= 0 || st.entityId >= __instance.factory.entityPool.Length)
                    return;
                if (__instance.factory.entityPool[st.entityId].protoId != TransmutationTablePlugin.StationItemId) return;
                st.idleDroneCount = 0; // workDrone 在飞的由原版订单清理逻辑处理，闲置机直接清零
                TransmutationTablePlugin.Log.LogInfo($"[转化站] 实体 {st.entityId} 拆除：内置无人机已清零（不退款）");
            }
            catch (Exception e)
            {
                Diag.Error("转化站拆除清理", e);
            }
        }

        // ------------------------------------------------------------------
        // 拦截分馏器原始逻辑
        // ------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FractionatorComponent), nameof(FractionatorComponent.InternalUpdate))]
        public static bool Fractionator_InternalUpdate_Prefix(
            FractionatorComponent __instance, PlanetFactory factory, ref uint __result)
        {
            if (factory.entityPool != null &&
                __instance.entityId > 0 && __instance.entityId < factory.entityPool.Length &&
                factory.entityPool[__instance.entityId].protoId == TransmutationTablePlugin.TableItemId)
            {
                __result = 0;
                return false; // 跳过原版分馏逻辑（否则会吞氢气/自行反应）
            }
            return true;
        }

        // ------------------------------------------------------------------
        // 每 tick：扫描所有转化桌，执行 EMC 收购与输出。
        // 挂点 = GameMain.FixedUpdate（主线程、每帧、在并行工厂逻辑完成后执行）。
        // 注意：0.10.x 真实游戏中分馏器等设施走 _fractionator_parallel 工作线程内联更新，
        //       FactorySystem.GameTick 根本不会被调用（仅菜单演示用），不能挂那里！
        // ------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameMain), "FixedUpdate")]
        public static void GameMain_FixedUpdate_Postfix()
        {
            try
            {
                GameData data = GameMain.data;
                if (data == null || data.factories == null || GameMain.instance.isMenuDemo) return;

                // 全局帧计数与索引维护放在这里：与工厂数量无关，多星球也不会漂移
                TickCount++;
                if (TickCount == 3L || TickCount % 1200L == 3L)
                    ProtoGuard.EnsureProtoIndices();

                for (int i = 0; i < data.factoryCount && i < data.factories.Length; i++)
                {
                    PlanetFactory pf = data.factories[i];
                    if (pf == null || pf.factorySystem == null) continue;
                    TickFactory(pf.factorySystem);
                }
            }
            catch (Exception e)
            {
                Diag.Error("转化桌 tick", e);
            }
        }

        private static void TickFactory(FactorySystem sys)
        {
            PlanetFactory factory = sys.factory;
            if (factory == null || factory.entityPool == null) return;

            FractionatorComponent[] fracPool = sys.fractionatorPool;
            if (fracPool == null) return;

            PowerConsumerComponent[] consumerPool = factory.powerSystem != null
                ? factory.powerSystem.consumerPool
                : null;
            CargoTraffic traffic = factory.cargoTraffic;
            bool anyTable = false;

            for (int i = 1; i < sys.fractionatorCursor; i++)
            {
                if (i >= fracPool.Length) break;
                ref FractionatorComponent f = ref fracPool[i];
                if (f.id != i) continue;

                EntityData ent = factory.entityPool[f.entityId];
                if (ent.id != f.entityId || ent.protoId != TransmutationTablePlugin.TableItemId) continue;
                anyTable = true;

                // 1. 电力需求清零（SetPCState 每 tick 会重设，这里再压回去）+ 清缺电告示
                if (consumerPool != null && f.pcId > 0 && f.pcId < consumerPool.Length)
                    consumerPool[f.pcId].requiredEnergy = 0;
                ClearPowerDemand(factory, f.entityId, consumerPool);

                // 2. 三条传送带通道（输出配置按建筑独立：key = 星球ID + 实体ID）
                if (traffic != null)
                {
                    // 自愈绑定：某端口没绑上带但实际有传送带连着时，直接读连接表补绑
                    if (f.belt0 == 0) HealBindSlot(sys, factory, fracPool, i, f.entityId, 0);
                    if (f.belt1 == 0) HealBindSlot(sys, factory, fracPool, i, f.entityId, 1);
                    if (f.belt2 == 0) HealBindSlot(sys, factory, fracPool, i, f.entityId, 2);

                    long key = EmcSystem.MakeKey(factory.planet.id, f.entityId);
                    HandleLane(traffic, f.belt0, f.isOutput0, 0, key);
                    HandleLane(traffic, f.belt1, f.isOutput1, 1, key);
                    HandleLane(traffic, f.belt2, f.isOutput2, 2, key);
                }

            }

            // ---------- 转化箱（储物仓模型）：箱内物品自动收购为 EMC；可配置单一输出物品自动备货 ----------
            if (factory.factoryStorage != null && factory.factoryStorage.storagePool != null)
            {
                var storagePool = factory.factoryStorage.storagePool;
                for (int i = 1; i < storagePool.Length; i++)
                {
                    var storage = storagePool[i];
                    if (storage == null || storage.id != i) continue;
                    if (storage.entityId <= 0 || storage.entityId >= factory.entityPool.Length) continue;
                    if (factory.entityPool[storage.entityId].protoId != TransmutationTablePlugin.BoxItemId) continue;

                    long key = EmcSystem.MakeKey(factory.planet.id, storage.entityId);
                    int outItem = EmcSystem.GetBoxOutput(key);
                    ClearPowerDemand(factory, storage.entityId, consumerPool);

                    // 1) 收购：除输出物品外的所有箱内物品 → EMC
                    if (storage.grids != null)
                    {
                        for (int g = 0; g < storage.size && g < storage.grids.Length; g++)
                        {
                            var row = storage.grids[g];
                            if (row.itemId > 0 && row.count > 0)
                            {
                                if (outItem > 0 && row.itemId == outItem) continue; // 输出缓冲保留，供分拣器取走
                                EmcSystem.Learn(row.itemId);
                                EmcSystem.Balance += EmcSystem.EmcOf(row.itemId) * row.count
                                    + EmcSystem.SprayCostPerIncPoint(row.inc) * row.inc;
                                PickedTotal += row.count;
                                row.itemId = row.filter;   // 保留筛选（与原版 TakeItemFromGrid 行为一致）
                                row.count = 0;
                                row.inc = 0;
                                if (row.filter == 0) row.stackSize = 0;
                                storage.grids[g] = row;
                            }
                        }
                    }

                    // 2) 输出补货：箱内自动备好输出物品（花 EMC，被取走即补；速率/喷涂按建筑参数）
                    if (outItem > 0 && storage.grids != null)
                    {
                        int total = 0;
                        for (int g = 0; g < storage.size && g < storage.grids.Length; g++)
                            if (storage.grids[g].itemId == outItem) total += storage.grids[g].count;

                        int stackMax = LDB.items.Select(outItem)?.StackSize ?? 100;
                        if (total < stackMax)
                        {
                            var bcfg = EmcSystem.GetBuildCfg(key);
                            double budget = TakeRateBudget(key * 4 + 3, Math.Max(1, bcfg.OutputSpeed));
                            int give = Math.Min(stackMax - total, (int)budget);
                            int inc = EmcSystem.SprayInc(bcfg.SprayLevel);
                            double unitCost = EmcSystem.EmcOf(outItem) + EmcSystem.SprayCostPerItem(bcfg.SprayLevel);
                            give = Math.Min(give, (int)Math.Floor((EmcSystem.Balance + 1e-9) / unitCost));
                            if (give > 0)
                            {
                                int added = storage.AddItem(outItem, give, give * inc, out int _, false);
                                if (added > 0)
                                {
                                    EmcSystem.Balance -= unitCost * added;
                                    InsertOkTotal += added;
                                }
                            }
                            else NoEmcSkipTotal++;
                            RefundRateBudget(key * 4 + 3, budget - give);
                        }
                    }
                }
            }

            // ---------- 转化站（物流站模型）："端口"模型 ----------
            // 输入端口（自动）：收购本星球其他物流塔供应的物品 + 传送带输入端口带尾物品 → EMC
            // 输出端口（手动 + 可选自动满足需求）：EMC 换物供应，本站自带无人机派送
            if (factory.transport != null && factory.transport.stationPool != null)
            {
                var stationPool = factory.transport.stationPool;

                // 扫描本星球其他物流塔的供应/需求物品（跳过转化站，避免站间内循环）。
                // 只认显式配置为 Supply 的格子，绝不改动玩家自己的塔（收购兜底走 InsertInto 直插拦截）。
                _suppliedItems.Clear();
                _demandedItems.Clear();
                for (int i = 1; i < stationPool.Length; i++)
                {
                    var other = stationPool[i];
                    if (other == null || other.id != i) continue;
                    if (other.entityId <= 0 || other.entityId >= factory.entityPool.Length) continue;
                    if (factory.entityPool[other.entityId].protoId == TransmutationTablePlugin.StationItemId) continue;
                    if (other.storage == null) continue;
                    for (int s = 0; s < other.storage.Length; s++)
                    {
                        var ss = other.storage[s];
                        if (ss.itemId <= 0) continue;
                        if (ss.localLogic == ELogisticStorage.Supply)
                        {
                            if (!_suppliedItems.Contains(ss.itemId)) _suppliedItems.Add(ss.itemId);
                        }
                        else if (ss.localLogic == ELogisticStorage.Demand)
                        {
                            if (!_demandedItems.Contains(ss.itemId)) _demandedItems.Add(ss.itemId);
                        }
                    }
                }

                for (int i = 1; i < stationPool.Length; i++)
                {
                    var st = stationPool[i];
                    if (st == null || st.id != i) continue;
                    if (st.entityId <= 0 || st.entityId >= factory.entityPool.Length) continue;
                    if (factory.entityPool[st.entityId].protoId != TransmutationTablePlugin.StationItemId) continue;

                    long key = EmcSystem.MakeKey(factory.planet.id, st.entityId);
                    var cfg = EmcSystem.GetStationCfg(key);
                    int slotCount = Math.Min(EmcSystem.StationSlotCount, st.storage != null ? st.storage.Length : 0);
                    if (slotCount <= 0) continue;
                    ClearPowerDemand(factory, st.entityId, consumerPool);

                    // 旧存档（曾扩容到 12 格）的残留槽位清理：物品折算 EMC、配置清空
                    for (int s = slotCount; s < st.storage.Length; s++)
                    {
                        ref StationStore legacy = ref st.storage[s];
                        if (legacy.itemId > 0 || legacy.localLogic != ELogisticStorage.None || legacy.count > 0)
                        {
                            ConvertSlotToEmc(ref legacy);
                            factory.transport.SetStationStorage(i, s, 0, 0, ELogisticStorage.None, ELogisticStorage.None, null);
                        }
                    }

                    // 内置无人机：开=每帧补满（被打掉/吞掉秒回，无限）；
                    // 关=不干预（原版行为：飞机送完货自动回站，只有被摧毁才会减少）
                    if (cfg.InfiniteDrones && st.workDroneDatas != null && st.workDroneDatas.Length > 0)
                    {
                        int droneTotal = st.workDroneCount + st.idleDroneCount;
                        if (droneTotal < st.workDroneDatas.Length)
                            st.idleDroneCount += st.workDroneDatas.Length - droneTotal;
                    }
                    // 固定电量不受外界影响：保证任何本星球内的派单路费都付得起
                    if (st.energyMax < 30000000L) st.energyMax = 30000000L;
                    if (st.energy < st.energyMax) st.energy = st.energyMax;
                    // 大飞机禁止：行星站本就不派星际飞船，保险清零
                    st.workShipCount = 0;
                    st.idleShipCount = 0;

                    // 0) 传送带输入学习：偷看输入端口带尾的物品（GetItemIdAtRear 只读不取），
                    //    自动加入收购目标。原版输入端口只捡 needs 表里的物品（needs = 槽位 itemId），
                    //    所以必须把带上的物品配进槽位才能被捡 —— 学习机制实现"喂什么收什么"。
                    _beltItems.Clear();
                    if (st.slots != null && factory.cargoTraffic != null)
                    {
                        for (int sl = 0; sl < st.slots.Length; sl++)
                        {
                            var slot = st.slots[sl];
                            if (slot.dir != IODir.Input || slot.beltId <= 0 ||
                                slot.beltId >= factory.cargoTraffic.beltPool.Length) continue;
                            var belt = factory.cargoTraffic.beltPool[slot.beltId];
                            if (belt.id != slot.beltId) continue;
                            var path = factory.cargoTraffic.GetCargoPath(belt.segPathId);
                            if (path == null) continue;
                            int rear = 0;
                            try { rear = path.GetItemIdAtRear(); } catch { }
                            if (rear > 0 && !_beltItems.Contains(rear))
                                _beltItems.Add(rear);
                        }
                    }

                    // 1) 期望端口：输出优先（手动配置 + 自动满足需求），剩余槽位给输入端口
                    //    无 EMC 定价的物品按临时价 1 EMC 供应（只支出不生钱，可在流水里核对）
                    _outWants.Clear();
                    _inWants.Clear();
                    foreach (int item in cfg.Outputs)
                        if (item > 0 && !_outWants.Contains(item)) _outWants.Add(item);
                    if (cfg.AutoSupply)
                        foreach (int item in _demandedItems)
                            if (!_outWants.Contains(item)) _outWants.Add(item);
                    if (cfg.AutoBuy)
                        foreach (int item in _suppliedItems)
                            if (!_outWants.Contains(item) && !_inWants.Contains(item)) _inWants.Add(item);
                    // 传送带学习的物品无条件接受（不收的话输入带就白接了），优先级最低
                    foreach (int item in _beltItems)
                        if (!_outWants.Contains(item) && !_inWants.Contains(item)) _inWants.Add(item);

                    for (int s = 0; s < slotCount; s++) { _wantItem[s] = 0; _wantSupply[s] = false; _wantDrain[s] = false; }

                    // 2) 保持：已符合期望的槽位原位保留（稳定不折腾）
                    for (int s = 0; s < slotCount; s++)
                    {
                        ref StationStore store = ref st.storage[s];
                        if (store.itemId > 0 && store.localLogic == ELogisticStorage.Supply && _outWants.Remove(store.itemId))
                        {
                            _wantItem[s] = store.itemId; _wantSupply[s] = true;
                        }
                        else if (store.itemId > 0 && store.localLogic == ELogisticStorage.Demand && _inWants.Remove(store.itemId))
                        {
                            _wantItem[s] = store.itemId; _wantSupply[s] = false;
                        }
                        else if (store.itemId > 0 && store.localOrder > 0)
                        {
                            // 有无人机在途订单：保持当前配置直到订单消化完，
                            // 防止到货落进已换成别的物品的槽位（否则会产生凭空物品）
                            _wantItem[s] = store.itemId;
                            _wantSupply[s] = store.localLogic == ELogisticStorage.Supply;
                            _wantDrain[s] = true;
                        }
                    }
                    // 3) 填充：剩余期望端口依次占用空余槽位（输出优先于输入）
                    for (int s = 0; s < slotCount && (_outWants.Count > 0 || _inWants.Count > 0); s++)
                    {
                        if (_wantItem[s] != 0) continue;
                        if (_outWants.Count > 0)
                        {
                            _wantItem[s] = _outWants[0]; _outWants.RemoveAt(0); _wantSupply[s] = true;
                        }
                        else
                        {
                            _wantItem[s] = _inWants[0]; _inWants.RemoveAt(0); _wantSupply[s] = false;
                        }
                    }

                    // 4) 执行：多余槽位清空；输出槽用 EMC 补货；输入槽到货转 EMC
                    for (int s = 0; s < slotCount; s++)
                    {
                        ref StationStore store = ref st.storage[s];
                        if (_wantItem[s] == 0)
                        {
                            if (store.itemId > 0 || store.localLogic != ELogisticStorage.None || store.count > 0)
                            {
                                ConvertSlotToEmc(ref store);
                                factory.transport.SetStationStorage(i, s, 0, 0, ELogisticStorage.None, ELogisticStorage.None, null);
                            }
                            continue;
                        }
                        var wantLogic = _wantSupply[s] ? ELogisticStorage.Supply : ELogisticStorage.Demand;
                        if (store.itemId != _wantItem[s] || store.localLogic != wantLogic)
                        {
                            ConvertSlotToEmc(ref store);
                            factory.transport.SetStationStorage(i, s, _wantItem[s], 9999, wantLogic, ELogisticStorage.None, null);
                            store = ref st.storage[s];
                        }

                        if (_wantSupply[s])
                        {
                            // 输出端口：EMC 只补到 75% 基准线（原版输入端口捡货要求槽未满，
                            // 填满会堵住传送带并让 needs 归零）；超出基准的货（传送带/分拣器
                            // 喂进来的）自动卖出 EMC —— 输入即收购，输出永不堵。
                            // 供应货物带喷涂：store.inc 累计点数，无人机派送/带输出按 inc/count 均摊。
                            if (!_wantDrain[s] && store.itemId > 0)
                            {
                                var bcfg = EmcSystem.GetBuildCfg(key);
                                int sprayInc = EmcSystem.SprayInc(bcfg.SprayLevel);
                                double cost = EmcSystem.EmcOf(store.itemId) + EmcSystem.SprayCostPerItem(bcfg.SprayLevel);
                                int baseLine = store.max * 3 / 4;
                                if (store.count > baseLine)
                                {
                                    int sell = store.count - baseLine;
                                    EmcSystem.Learn(store.itemId);
                                    double gained = cost * sell;
                                    EmcSystem.Balance += gained;
                                    StationBuyEarned += gained;
                                    PickedTotal += sell;
                                    store.count = baseLine;
                                    store.inc = baseLine * sprayInc;
                                }
                                else if (store.count < baseLine)
                                {
                                    int give = baseLine - store.count;
                                    int affordable = (int)Math.Floor((EmcSystem.Balance + 1e-9) / cost);
                                    give = Math.Min(give, affordable);
                                    if (give > 0)
                                    {
                                        EmcSystem.Balance -= cost * give;
                                        StationSupplySpent += cost * give;
                                        store.count += give;
                                        store.inc += give * sprayInc;
                                        InsertOkTotal += give;
                                    }
                                    else NoEmcSkipTotal++;
                                }
                            }
                        }
                        else
                        {
                            // 输入端口：对方塔的无人机送货进来 → 转 EMC（含喷涂点数加价）
                            if (store.count > 0 && store.itemId > 0)
                            {
                                EmcSystem.Learn(store.itemId);
                                double gained = EmcSystem.EmcOf(store.itemId) * store.count
                                    + EmcSystem.SprayCostPerIncPoint(store.inc) * store.inc;
                                EmcSystem.Balance += gained;
                                StationBuyEarned += gained;
                                PickedTotal += store.count;
                                store.count = 0;
                                store.inc = 0;
                            }
                        }
                    }
                }
            }

            // 专项告警：有分馏器组件但没匹配到转化桌 → protoId 对不上（每 10 秒最多一条）
            if (!anyTable && sys.fractionatorCursor > 1 && TickCount % 600 == 1)
            {
                TransmutationTablePlugin.Log.LogWarning(
                    $"[转化桌] 星球上有 {sys.fractionatorCursor - 1} 个分馏器组件，但没有一个是转化桌 (TableItemId={TransmutationTablePlugin.TableItemId})");
            }

            // 心跳：证明 tick 补丁存活（每 100 秒一条，仅在有转化建筑时）
            if (anyTable && TickCount % 6000 == 5)
            {
                TransmutationTablePlugin.Log.LogInfo($"[心跳] tick={TickCount} 转化桌识别正常");
            }
        }

        /// <summary>读取建筑端口的连接表，把连着但未绑定的传送带补绑到组件上</summary>
        private static void HealBindSlot(FactorySystem sys, PlanetFactory factory, FractionatorComponent[] fracPool, int fid, int entityId, int slot)
        {
            try
            {
                factory.ReadObjectConn(entityId, slot, out bool isOutput, out int otherObjId, out int otherSlot);
                if (otherObjId <= 0 || otherObjId >= factory.entityPool.Length) return;
                int beltId = factory.entityPool[otherObjId].beltId;
                if (beltId <= 0 || beltId >= factory.cargoTraffic.beltPool.Length ||
                    factory.cargoTraffic.beltPool[beltId].id != beltId) return;

                ref FractionatorComponent f = ref fracPool[fid];
                switch (slot)
                {
                    case 0: f.belt0 = beltId; f.isOutput0 = isOutput; break;
                    case 1: f.belt1 = beltId; f.isOutput1 = isOutput; break;
                    case 2: f.belt2 = beltId; f.isOutput2 = isOutput; break;
                }
            }
            catch (Exception e)
            {
                TransmutationTablePlugin.Log.LogWarning($"自愈绑定异常(slot={slot}): {e.Message}");
            }
        }

        private static void HandleLane(CargoTraffic traffic, int beltId, bool isOutput, int lane, long configKey)
        {
            if (beltId <= 0 || beltId >= traffic.beltPool.Length) return;
            if (traffic.beltPool[beltId].id != beltId) return; // 传送带已失效

            long rateKey = configKey * 4 + lane;
            var bcfg = EmcSystem.GetBuildCfg(configKey);

            if (isOutput)
            {
                // 输出：按本建筑配置把 EMC 换成物品放到带上（速率 = OutputSpeed 个/秒）
                int outItem = EmcSystem.GetOutput(configKey, lane);
                if (outItem <= 0) return;
                double budget = TakeRateBudget(rateKey, Math.Max(1, bcfg.OutputSpeed));
                int want = (int)budget;
                if (want <= 0) return;

                int inc = EmcSystem.SprayInc(bcfg.SprayLevel);
                double unitCost = EmcSystem.EmcOf(outItem) + EmcSystem.SprayCostPerItem(bcfg.SprayLevel);
                int inserted = 0;
                for (int k = 0; k < want; k++)
                {
                    if (EmcSystem.Balance + 1e-9 < unitCost)
                    {
                        NoEmcSkipTotal++;
                        break;
                    }
                    if (traffic.TryInsertItemAtHead(beltId, outItem, 1, (byte)inc))
                    {
                        EmcSystem.Balance -= unitCost;
                        InsertOkTotal++;
                        inserted++;
                    }
                    else
                    {
                        InsertFailTotal++;
                        break;
                    }
                }
                RefundRateBudget(rateKey, want - inserted);
            }
            else
            {
                // 输入：从带尾取任意物品折算 EMC（带喷涂的点数按涂料价加价）
                int itemId = traffic.TryPickItemAtRear(beltId, 0, null, out byte stack, out byte inc);
                if (itemId > 0)
                {
                    EmcSystem.Learn(itemId);
                    double gained = EmcSystem.EmcOf(itemId) * stack + EmcSystem.SprayCostPerIncPoint(inc) * inc;
                    EmcSystem.Balance += gained;
                    PickedTotal++;
                }
            }
        }

        // 建造菜单落位已移交 BuildMenuInstaller（存储分类已满，需要腾挪原版建筑）。

        // ------------------------------------------------------------------
        // 红字捕捉：游戏内飘红提示（UIRealtimeTip）不写日志文件，这里把内容
        // 记录到 TransmutationTable.errors.log，每个去重文本只记一次。
        // ------------------------------------------------------------------
        private static readonly HashSet<string> _seenTips = new HashSet<string>();

        /// <summary>原版常规提示（材料不足/背包已满等）不是 Mod 异常，不写入错误日志</summary>
        private static readonly string[] _benignTips =
        {
            "材料不足", "背包已满", "配方未解锁", "该配方", "无法", "不足", "需要"
        };

        private static void LogTip(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text) || !_seenTips.Add(text)) return;
                foreach (var benign in _benignTips)
                    if (text.Contains(benign)) return;
                // 去掉 harmony 注入帧，保留真实调用者前 6 帧
                var lines = Environment.StackTrace.Split('\n');
                var sb = new System.Text.StringBuilder();
                int taken = 0;
                for (int i = 0; i < lines.Length && taken < 6; i++)
                {
                    var l = lines[i].Trim();
                    if (l.Length == 0 || l.Contains("Harmony") || l.Contains("LogTip") || l.Contains("Traverse")) continue;
                    sb.Append("  ← ").Append(l).Append('\n');
                    taken++;
                }
                Diag.Warn("游戏内红字提示", $"{text}\n{sb}");
            }
            catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIRealtimeTip), "Popup", new[] { typeof(string), typeof(bool), typeof(int) })]
        public static void UIRealtimeTip_Popup_Str_Postfix(string text) => LogTip(text);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIRealtimeTip), "Popup", new[] { typeof(string), typeof(Vector2), typeof(int) })]
        public static void UIRealtimeTip_Popup_V2_Postfix(string text) => LogTip(text);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIRealtimeTip), "Popup", new[] { typeof(string), typeof(Vector2), typeof(bool) })]
        public static void UIRealtimeTip_Popup_V2B_Postfix(string text) => LogTip(text);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIRealtimeTip), "Popup", new[] { typeof(string), typeof(Vector3), typeof(bool), typeof(int) })]
        public static void UIRealtimeTip_Popup_V3_Postfix(string text) => LogTip(text);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIRealtimeTip), "Popup", new[] { typeof(string), typeof(string), typeof(int) })]
        public static void UIRealtimeTip_Popup_Snd_Postfix(string text) => LogTip(text);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIRealtimeTip), "PopupAhead", new[] { typeof(string), typeof(bool), typeof(int) })]
        public static void UIRealtimeTip_PopupAhead_Postfix(string text) => LogTip(text);

        // ------------------------------------------------------------------
        // 转化桌/转化箱/转化站配方改由科技解锁（等价交换 → 转化箱 → 转化站，
        // 挂在物流系列主线上），不再强制免科技。旧存档研究对应科技即可。
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // 玩家查看建筑时分发窗口：转化桌/转化箱/转化站 → 打开 EMC 窗口，
        // 屏蔽原版分馏器/储物仓/物流站窗口
        // ------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIGame), "OnPlayerInspecteeChange")]
        public static bool UIGame_OnPlayerInspecteeChange_Prefix(UIGame __instance, EObjectType objType, int objId)
        {
            try
            {
                bool mine = false;
                if (objType == EObjectType.None && objId > 0 && __instance.gameData?.localPlanet?.factory != null)
                {
                    var pool = __instance.gameData.localPlanet.factory.entityPool;
                    if (objId < pool.Length && pool[objId].id == objId)
                    {
                        int protoId = pool[objId].protoId;
                        mine = protoId == TransmutationTablePlugin.TableItemId ||
                               protoId == TransmutationTablePlugin.BoxItemId ||
                               protoId == TransmutationTablePlugin.StationItemId;
                    }
                }

                if (mine)
                {
                    EmcWindow.Open(objId);
                    return false; // 不打开原版窗口
                }

                if (EmcWindow.IsOpen) EmcWindow.Close();
            }
            catch (Exception e)
            {
                Diag.Error("窗口分发", e);
            }
            return true;
        }
    }
}
