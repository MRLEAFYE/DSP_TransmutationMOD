using System;
using System.Collections.Generic;
using UnityEngine;

namespace TransmutationTable
{
    /// <summary>
    /// 把克隆出来的建筑模型整体染成紫色。
    /// 染色目标（不改原版共享资源，全部克隆）：
    ///   1. 所有 Renderer 的 sharedMaterials → new Material 染色
    ///   2. LODModelDesc.lodMeshDatas（MeshDataAsset 是共享 ScriptableObject）→ 克隆资源后染其 materials/blueprintMaterials
    /// 染色方式：按优先级 _TintColor / _Color / _BaseColor 读取原色并乘紫色系数。
    /// DSP 建筑着色器普遍支持 _TintColor（同网格不同色阶的做法），大多数建筑只需染这一个属性。
    /// </summary>
    internal static class PurpleModelTint
    {
        /// <summary>紫色系数：乘到原色上得到紫色调</summary>
        private static readonly Color Tint = new Color(0.62f, 0.30f, 1.00f);

        /// <summary>strength=1 全紫；strength&lt;1 与原色插值（转化桌用 0.35 淡紫）</summary>
        public static void Apply(GameObject go, float strength = 1f)
        {
            if (go == null) return;
            var matCache = new Dictionary<Material, Material>();
            var assetCache = new Dictionary<MeshDataAsset, MeshDataAsset>();

            // 1) 普通渲染器
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var sms = r.sharedMaterials;
                if (sms == null) continue;
                bool changed = false;
                for (int i = 0; i < sms.Length; i++)
                {
                    var nm = TintMaterial(sms[i], matCache, strength);
                    if (!ReferenceEquals(nm, sms[i])) { sms[i] = nm; changed = true; }
                }
                if (changed) r.sharedMaterials = sms;
            }

            // 2) LOD 材质（实体 GPU 实例化渲染用的就是这套）
            foreach (var lod in go.GetComponentsInChildren<LODModelDesc>(true))
            {
                if (lod.lodMeshDatas == null) continue;
                for (int i = 0; i < lod.lodMeshDatas.Length; i++)
                {
                    var src = lod.lodMeshDatas[i];
                    if (src == null) continue;
                    if (!assetCache.TryGetValue(src, out var na))
                    {
                        na = UnityEngine.Object.Instantiate(src);
                        na.name = src.name + "_purple";
                        na.materials = TintArray(src.materials, matCache, strength);
                        na.blueprintMaterials = TintArray(src.blueprintMaterials, matCache, strength);
                        assetCache[src] = na;
                    }
                    lod.lodMeshDatas[i] = na;
                }
            }
        }

        private static Material[] TintArray(Material[] arr, Dictionary<Material, Material> cache, float strength)
        {
            if (arr == null) return arr;
            var dst = new Material[arr.Length];
            for (int i = 0; i < arr.Length; i++) dst[i] = TintMaterial(arr[i], cache, strength);
            return dst;
        }

        private static Material TintMaterial(Material m, Dictionary<Material, Material> cache, float strength)
        {
            if (m == null) return null;
            if (cache.TryGetValue(m, out var cached)) return cached;
            Material nm;
            try
            {
                nm = new Material(m);
                nm.name = m.name + "_purple";
                bool done = false;
                if (nm.HasProperty("_TintColor")) { nm.SetColor("_TintColor", Mul(nm.GetColor("_TintColor"), strength)); done = true; }
                if (!done && nm.HasProperty("_Color")) { nm.SetColor("_Color", Mul(nm.GetColor("_Color"), strength)); done = true; }
                if (!done && nm.HasProperty("_BaseColor")) { nm.SetColor("_BaseColor", Mul(nm.GetColor("_BaseColor"), strength)); done = true; }
                // 发光贴图不染：保留原建筑灯光效果
            }
            catch
            {
                nm = m; // 染色失败就退回原材质（外观不变，功能不受影响）
            }
            cache[m] = nm;
            return nm;
        }

        /// <summary>按 strength 把原色向紫色调插值：1=全紫，0.35=保留大部分原色只带紫味</summary>
        private static Color Mul(Color c, float strength)
        {
            var tinted = new Color(c.r * Tint.r, c.g * Tint.g, c.b * Tint.b, c.a);
            return Color.Lerp(c, tinted, Mathf.Clamp01(strength));
        }

        /// <summary>
        /// 生成源图标的紫色版本 Sprite（不修改原共享纹理）。
        /// 输出统一为 80×80 sRGB 纹理——IconSet 图集用 CopyTexture(0,0,80,80) 取块，
        /// 只有"独立 80×80、非 linear、格式兼容"的纹理才能被正确复制进图集，
        /// 共享图集/其他尺寸/linear 格式的源（部分原版建筑图标如此）会导致背包/手搓图标透明或白块。
        /// </summary>
        public static Sprite CreatePurpleIcon(Sprite src, float strength = 0.5f)
        {
            try
            {
                if (src == null || src.texture == null)
                {
                    TransmutationTablePlugin.Log.LogWarning("紫色图标: 源 sprite/texture 为空");
                    return null;
                }
                var tex = src.texture;
                var rect = src.textureRect;

                // 1) 源纹理 → RT → 可读纹理（绕过不可读限制）
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var full = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                full.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                full.Apply(false, false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);

                // 2) 裁剪出图标内容区域（防越界）
                int x = Mathf.Clamp(Mathf.RoundToInt(rect.x), 0, tex.width - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt(rect.y), 0, tex.height - 1);
                int w = Mathf.Clamp(Mathf.RoundToInt(rect.width), 1, tex.width - x);
                int h = Mathf.Clamp(Mathf.RoundToInt(rect.height), 1, tex.height - y);
                var px = full.GetPixels(x, y, w, h);
                // 内容检测：全透明视为无效源（如分馏塔的物品图标是空白残片），返回 null 触发回退链
                bool anyVisible = false;
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].a > 0.05f) { anyVisible = true; break; }
                }
                if (px.Length == 0 || !anyVisible)
                {
                    TransmutationTablePlugin.Log.LogWarning($"[紫色图标] 源 {src.name} 裁剪区域全透明 ({x},{y},{w},{h})，视为无效源");
                    return null;
                }
                for (int i = 0; i < px.Length; i++) px[i] = Mul(px[i], strength);

                // 3) 统一缩放到 80×80 sRGB（IconSet CopyTexture 完全兼容）
                var cropped = new Texture2D(w, h, TextureFormat.RGBA32, false);
                cropped.SetPixels(px);
                cropped.Apply(false, false);
                var rt2 = RenderTexture.GetTemporary(80, 80, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(cropped, rt2);
                RenderTexture.active = rt2;
                var outTex = new Texture2D(80, 80, TextureFormat.RGBA32, false);
                outTex.ReadPixels(new Rect(0, 0, 80, 80), 0, 0);
                outTex.Apply(false, false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt2);

                var sp = Sprite.Create(outTex, new Rect(0, 0, 80, 80), new Vector2(0.5f, 0.5f), 50f);
                sp.name = src.name + "_purple";
                return sp;
            }
            catch (Exception e)
            {
                TransmutationTablePlugin.Log.LogWarning($"紫色图标生成失败({src?.name}): {e}");
                return null;
            }
        }
    }
}
