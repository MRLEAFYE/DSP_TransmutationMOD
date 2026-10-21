using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TransmutationTable
{
    /// <summary>
    /// 原型索引守卫：LDBTool 注入的自定义物品/配方在部分流程下会从 ProtoSet.dataIndices 丢失，
    /// 表现为 Select 返回 null → CommonAPI 替换成假货 → "Unknown item" / 图标白块 / 材料判定异常。
    /// 本类集中处理三件事：
    ///   1. dataIndices 自检与重建（含 dataArray 条目回填），绕开一切 Select 补丁；
    ///   2. Select 假货拦截——查到我们的物品/配方为空或假货时立即重建并把真 proto 回填给调用方；
    ///   3. IconSet 图集槽位兜底，防止背包/建造栏图标白块。
    /// </summary>
    [HarmonyPatch]
    public static class ProtoGuard
    {
        private static bool _indicesFixed;

        /// <summary>
        /// 索引自检：确认注入的物品/配方仍在 LDB 的 dataIndices 里；缺失则重建。
        /// 注意：不能用 Select(id)==null 判断——CommonAPI 给 ProtoSet&lt;ItemProto&gt;.Select
        /// 打了 Postfix，null 会被替换成假的 "Unknown Item" proto，导致自检永远"通过"。
        /// 这里直接反射读 dataIndices 字典，绕开一切 Select 补丁。
        /// </summary>
        public static void EnsureProtoIndices()
        {
            try
            {
                int[] itemIds = { TransmutationTablePlugin.TableItemId, TransmutationTablePlugin.BoxItemId, TransmutationTablePlugin.StationItemId };
                int[] recipeIds = { 9702, 9704, 9706 };
                bool need = false;
                foreach (int id in itemIds) if (id > 0 && IndexMissing(LDB.items, id)) { need = true; break; }
                if (!need)
                    foreach (int id in recipeIds)
                        if (id > 0 && IndexMissing(LDB.recipes, id)) { need = true; break; }
                // 产物绑定自检：LDBTool 重映射物品 ID 时不同步 Results（桌 9701→11500 曾导致产物 Unknown item）
                if (!need && TransmutationTablePlugin.TableRecipe != null && TransmutationTablePlugin.TableProto != null)
                {
                    var rr = TransmutationTablePlugin.TableRecipe.Results;
                    if (rr == null || rr.Length != 1 || rr[0] != TransmutationTablePlugin.TableProto.ID) need = true;
                }

                if (!need)
                {
                    EnsurePrefabDescArrays(); // 幂等：补齐模型→PrefabDesc 索引（OnBeltBuilt 空引用修复）
                    if (!_indicesFixed)
                    {
                        _indicesFixed = true;
                        TransmutationTablePlugin.Log.LogInfo("[索引自检] 全部通过");
                    }
                    return;
                }

                RebuildAllIndices(out bool itemOk, out bool recipeOk);
                TransmutationTablePlugin.Log.LogWarning(
                    $"[索引自检] 检测到注入条目丢失，已重建 dataIndices（物品恢复={itemOk}, 配方恢复={recipeOk}）");
            }
            catch (Exception e)
            {
                Diag.Error("EnsureProtoIndices", e);
            }
        }

        /// <summary>读取 ProtoSet 私有的 dataIndices 字典（不经 Select，绕开 CommonAPI 假货补丁）</summary>
        private static Dictionary<int, int> GetIndices(object protoSet)
        {
            if (protoSet == null) return null;
            return AccessTools.Field(protoSet.GetType(), "dataIndices")?.GetValue(protoSet) as Dictionary<int, int>;
        }

        private static Array GetDataArray(object protoSet)
        {
            if (protoSet == null) return null;
            return AccessTools.Field(protoSet.GetType(), "dataArray")?.GetValue(protoSet) as Array;
        }

        /// <summary>id 是否不在 dataIndices、或下标越界（dataArray 被替换/缩短）</summary>
        private static bool IndexMissing(object protoSet, int id)
        {
            var idx = GetIndices(protoSet);
            if (idx == null || !idx.ContainsKey(id)) return true;
            var arr = GetDataArray(protoSet);
            return arr == null || idx[id] >= arr.Length;
        }

        /// <summary>dataArray 中是否已含指定 ID 的条目</summary>
        private static bool ArrayHas(object protoSet, int id)
        {
            var arr = GetDataArray(protoSet);
            if (arr == null) return false;
            for (int i = 0; i < arr.Length; i++)
                if (arr.GetValue(i) is Proto p && p.ID == id) return true;
            return false;
        }

        /// <summary>从dataArray 中按 ID 原生查找（反射，不经过 Select，不受补丁影响）</summary>
        private static Proto FindInArray(object protoSet, int id)
        {
            var arr = GetDataArray(protoSet);
            if (arr == null) return null;
            for (int i = 0; i < arr.Length; i++)
                if (arr.GetValue(i) is Proto p && p.ID == id) return p;
            return null;
        }

        /// <summary>确保条目在 dataArray 中：丢失则扩容数组并把 held 引用追加到末尾</summary>
        private static void EnsureProtoInArray(object protoSet, Proto held)
        {
            try
            {
                if (protoSet == null || held == null || ArrayHas(protoSet, held.ID)) return;
                var t = protoSet.GetType();
                var arr = GetDataArray(protoSet);
                var bigger = Array.CreateInstance(arr.GetType().GetElementType(), arr.Length + 1);
                Array.Copy(arr, bigger, arr.Length);
                bigger.SetValue(held, arr.Length);
                AccessTools.Field(t, "dataArray").SetValue(protoSet, bigger);
                TransmutationTablePlugin.Log.LogWarning($"[索引重建] dataArray 缺 {held.ID}({held.Name})，已扩容回填");
            }
            catch (Exception e)
            {
                Diag.Error("EnsureProtoInArray", e);
            }
        }

        /// <summary>重建三个 ProtoSet 的 dataIndices；dataArray 缺条目时先回填（引用由 Plugin 静态持有）</summary>
        public static void RebuildAllIndices(out bool itemOk, out bool recipeOk)
        {
            try
            {
                // dataArray 丢失时回填（Plugin 静态持有 proto 引用，即使 LDB 被整体替换也能恢复）
                EnsureProtoInArray(LDB.items, TransmutationTablePlugin.TableProto);
                EnsureProtoInArray(LDB.items, TransmutationTablePlugin.BoxProto);
                EnsureProtoInArray(LDB.items, TransmutationTablePlugin.StationProto);
                EnsureProtoInArray(LDB.recipes, TransmutationTablePlugin.TableRecipe);
                EnsureProtoInArray(LDB.recipes, TransmutationTablePlugin.BoxRecipe);
                EnsureProtoInArray(LDB.recipes, TransmutationTablePlugin.StationRecipe);

                RebuildIndices(LDB.items);
                RebuildIndices(LDB.recipes);
                RebuildIndices(LDB.techs);
                TransmutationTablePlugin.FixRecipeBindings(); // 顺带校验产物绑定

                itemOk = !IndexMissing(LDB.items, TransmutationTablePlugin.TableItemId);
                recipeOk = !IndexMissing(LDB.recipes, TablePatches.TableRecipeId);
            }
            catch (Exception e)
            {
                Diag.Error("RebuildAllIndices", e);
                itemOk = recipeOk = false;
            }
        }

        private static void RebuildIndices(object protoSet)
        {
            // 不走 OnAfterDeserialize：它会把所有 proto 的 name 重置成未翻译的 key。
            // 这里只重建 dataIndices（ID → 数组下标），与 LDBTool 注入时的做法一致。
            if (protoSet == null) return;
            var t = protoSet.GetType();
            var dataArr = AccessTools.Field(t, "dataArray")?.GetValue(protoSet) as Array;
            var idxField = AccessTools.Field(t, "dataIndices");
            if (dataArr == null || idxField == null) return;
            var dict = new Dictionary<int, int>();
            for (int i = 0; i < dataArr.Length; i++)
            {
                if (dataArr.GetValue(i) is Proto p && p != null)
                    dict[p.ID] = i;
            }
            idxField.SetValue(protoSet, dict);
        }

        // ------------------------------------------------------------------
        // Unknown item 自愈：CommonAPI 给 ProtoSet<ItemProto>.Select 打了 Postfix，
        // Select 返回 null 时会被替换成假的 "Unknown Item" proto（ID 相同、name="Unknown Item"）。
        // UI/产出流程一旦拿到这个假货就显示 Unknown item / 白块。
        // 这里拦截：当我们的物品/配方被查到 null 或假货时，立即重建索引，
        // 并把真 proto 直接回填给本次调用（两种补丁顺序下都正确工作）。
        // ------------------------------------------------------------------
        private static float _lastSelfHeal;
        /// <summary>立即重建索引（每次都修），日志按 1 秒节流防刷屏</summary>
        private static void SelfHeal(string what, int id)
        {
            RebuildAllIndices(out bool itemOk, out bool recipeOk);
            if (Time.unscaledTime - _lastSelfHeal < 1f) return;
            _lastSelfHeal = Time.unscaledTime;
            TransmutationTablePlugin.Log.LogWarning(
                $"[假货拦截] {what} Select({id}) 返回无效结果，已重建索引（物品恢复={itemOk}, 配方恢复={recipeOk}）");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ProtoSet<ItemProto>), "Select", new[] { typeof(int) })]
        public static void ProtoSet_Item_Select_Postfix(int id, ref ItemProto __result)
        {
            try
            {
                if (id <= 0) return;
                bool ours = id == TransmutationTablePlugin.TableItemId
                         || id == TransmutationTablePlugin.BoxItemId
                         || id == TransmutationTablePlugin.StationItemId;
                if (!ours) return;
                bool fake = __result == null || __result.name == "Unknown Item";
                if (!fake) return;
                var real = FindInArray(LDB.items, id) as ItemProto;
                if (real != null)
                {
                    __result = real; // 本次调用直接给真货
                    if (__result != null && (GetIndices(LDB.items) == null || !GetIndices(LDB.items).ContainsKey(id)))
                        SelfHeal("物品", id);
                }
                else
                {
                    SelfHeal("物品", id); // dataArray 也没了 → 重建（回填）后再取一次
                    __result = FindInArray(LDB.items, id) as ItemProto;
                }
            }
            catch (Exception e)
            {
                Diag.Error("假货拦截-item", e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ProtoSet<RecipeProto>), "Select", new[] { typeof(int) })]
        public static void ProtoSet_Recipe_Select_Postfix(int id, ref RecipeProto __result)
        {
            try
            {
                if (id != 9702 && id != 9704 && id != 9706) return;
                if (__result != null) return; // 配方侧 CommonAPI 不造假货，null 即丢失
                if (TransmutationTablePlugin.TableRecipe == null) return; // 尚未注册
                SelfHeal("配方", id);
                __result = FindInArray(LDB.recipes, id) as RecipeProto;
            }
            catch (Exception e)
            {
                Diag.Error("假货拦截-recipe", e);
            }
        }

        // ------------------------------------------------------------------
        // 图集兜底：IconSet.Create（进游戏时）把 proto.iconSprite 拷进 2000x2000 GPU 图集，
        // 并填 itemIconIndex / recipeIconIndex。若那一刻注入条目的 iconSprite 不在
        // （proto 被替换或图标未应用），对应槽位为 0 → 背包/建造栏显示白块。
        // 这里在 Create 之后检查三个物品与三个配方的槽位，缺失则补拷贝并更新 GPU 缓冲。
        // ------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(IconSet), nameof(IconSet.Create))]
        public static void IconSet_Create_Postfix(IconSet __instance)
        {
            try
            {
                PatchIconSlots(__instance);
            }
            catch (Exception e)
            {
                Diag.Error("IconSet.Create兜底", e);
            }
        }

        private static void PatchIconSlots(IconSet set)
        {
            if (set == null || set.itemIconIndex == null || set.recipeIconIndex == null || set.texture == null) return;
            int[] itemIds = { TransmutationTablePlugin.TableItemId, TransmutationTablePlugin.BoxItemId, TransmutationTablePlugin.StationItemId };
            int[] recipeIds = { 9702, 9704, 9706 };
            bool dirtyItem = false, dirtyRecipe = false;

            foreach (int id in itemIds)
            {
                if (id <= 0 || set.itemIconIndex[id] != 0u) continue;
                var proto = FindInArray(LDB.items, id) as ItemProto;
                var sprite = proto?.iconSprite;
                if (sprite == null || sprite.texture == null) continue;
                uint slot = AllocFreeSlot(set);
                if (slot == 0u) break;
                CopyToAtlas(set, sprite, slot);
                set.itemIconIndex[id] = slot;
                if (set.signalIconIndex != null && id + 12000 < set.signalIconIndex.Length)
                    set.signalIconIndex[id + 12000] = slot;
                dirtyItem = true;
                TransmutationTablePlugin.Log.LogWarning($"[图集兜底] 物品 {id} 图标槽位={slot} 已补入图集");
            }
            foreach (int id in recipeIds)
            {
                if (id <= 0 || set.recipeIconIndex[id] != 0u) continue;
                var proto = FindInArray(LDB.recipes, id) as RecipeProto;
                var sprite = proto?.iconSprite;
                if (sprite == null || sprite.texture == null) continue;
                uint slot = AllocFreeSlot(set);
                if (slot == 0u) break;
                CopyToAtlas(set, sprite, slot);
                set.recipeIconIndex[id] = slot;
                if (set.signalIconIndex != null && id + 20000 < set.signalIconIndex.Length)
                    set.signalIconIndex[id + 20000] = slot;
                dirtyRecipe = true;
                TransmutationTablePlugin.Log.LogWarning($"[图集兜底] 配方 {id} 图标槽位={slot} 已补入图集");
            }

            if (dirtyItem) set.itemIconIndexBuffer?.SetData(set.itemIconIndex);
            if (dirtyRecipe)
            {
                set.recipeIconIndexBuffer?.SetData(set.recipeIconIndex);
                set.signalIconIndexBuffer?.SetData(set.signalIconIndex);
            }
        }

        private static void CopyToAtlas(IconSet set, Sprite sprite, uint slot)
        {
            int col = (int)slot % 25;
            int row = (int)slot / 25;
            Graphics.CopyTexture(sprite.texture, 0, 0, 0, 0, 80, 80, set.texture, 0, 0, col * 80, row * 80);
        }

        /// <summary>在 1..624 中找一个未被任何图标索引数组占用的图集槽位（0 是空白保留槽）</summary>
        private static uint AllocFreeSlot(IconSet set)
        {
            for (uint s = 1u; s < 625u; s++)
            {
                if (UsedIn(set.itemIconIndex, s)) continue;
                if (UsedIn(set.recipeIconIndex, s)) continue;
                if (UsedIn(set.techIconIndex, s)) continue;
                if (UsedIn(set.veinIconIndex, s)) continue;
                if (UsedIn(set.signalIconIndex, s)) continue;
                return s;
            }
            return 0u;
        }

        private static bool UsedIn(uint[] arr, uint slot)
        {
            if (arr == null) return false;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] == slot) return true;
            return false;
        }

        // ------------------------------------------------------------------
        // PrefabDescByModelIndex 守卫：
        // 原版 GameMain.Awake 在【主菜单】就调用 PlanetFactory/SpaceSector.InitPrefabDescArray()，
        // 而这两处都是 static 且只建一次（if == null）。LDBTool 注入我们的紫色模型（666/667/668）
        // 发生在这之后 → 数组里对应槽位永远是 null。
        // 后果：建造传送带时 OnBeltBuilt 会读 PrefabDescByModelIndex[entity.modelIndex].addonType,
        //       遇到转化桌等实体直接 NRE → 屏幕上每帧红字 "The game errored..."。
        // 这里在注入完成后把数组补齐/扩容，并在自检周期内兜底。
        // ------------------------------------------------------------------
        private static bool _prefabDescFixed;

        public static void EnsurePrefabDescArrays()
        {
            try
            {
                FillPrefabDescArray(typeof(PlanetFactory), "PrefabDescByModelIndex");
                FillPrefabDescArray(typeof(SpaceSector), "PrefabDescByModelIndex");
            }
            catch (Exception e)
            {
                Diag.Error("PrefabDesc 数组补齐", e);
            }
        }

        private static void FillPrefabDescArray(Type holder, string fieldName)
        {
            var field = AccessTools.Field(holder, fieldName);
            if (field == null) return;
            var arr = field.GetValue(null) as PrefabDesc[];
            if (arr == null) return; // 尚未创建：原版创建时会遍历到我们的模型，天然包含

            var models = LDB.models?.dataArray;
            if (models == null) return;

            int maxId = 0;
            foreach (var m in models)
                if (m != null && m.ID > maxId) maxId = m.ID;
            int need = maxId + 64;
            bool dirty = false;
            if (arr.Length < need)
            {
                var bigger = new PrefabDesc[need];
                Array.Copy(arr, bigger, arr.Length);
                arr = bigger;
                dirty = true;
            }
            for (int i = 0; i < models.Length; i++)
            {
                var m = models[i];
                if (m == null || m.ID < 0 || m.ID >= arr.Length) continue;
                if (arr[m.ID] == null || !ReferenceEquals(arr[m.ID], m.prefabDesc))
                {
                    arr[m.ID] = m.prefabDesc;
                    dirty = true;
                }
            }
            if (!dirty) return;
            field.SetValue(null, arr);
            if (!_prefabDescFixed)
            {
                _prefabDescFixed = true;
                TransmutationTablePlugin.Log.LogInfo(
                    $"[PrefabDesc] 已补齐 {holder.Name}.PrefabDescByModelIndex（长度 {arr.Length}）：注入模型此前缺失槽位会导致 OnBeltBuilt 空引用");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.InitPrefabDescArray))]
        public static void PlanetFactory_InitPrefabDescArray_Postfix() => EnsurePrefabDescArrays();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SpaceSector), nameof(SpaceSector.InitPrefabDescArray))]
        public static void SpaceSector_InitPrefabDescArray_Postfix() => EnsurePrefabDescArrays();
    }
}
