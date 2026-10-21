using System.Collections.Generic;

namespace TransmutationTable
{
    /// <summary>
    /// 内置默认价格表（兜底写死值）。
    /// 由 calc_prices.py 生成——配方来源为游戏内 RecipeProto 实测导出（TransmutationTable.recipes.txt）。
    /// 运行时优先级：TransmutationTable.prices.txt（用户可编辑） > 此内置默认 > 配方推导。
    /// </summary>
    internal static class EmcDefaults
    {
        // 占位：待 recipes.txt 导出后由脚本填充 (itemId, emc) 对。
        // 数组为空时全表走"锚点 + 配方推导"兜底。
        internal static readonly (int id, int emc)[] Prices = new (int, int)[]
        {
        };
    }
}
