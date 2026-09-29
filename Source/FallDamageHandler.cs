using System;

namespace Celeste.Mod.PeakStamina;

// ============================================================================
//  摔落承伤
//
//  这个文件只负责一件事：判断"玩家这次落地算不算摔伤"，算出数值就好。
//  真正的扣血由 StaminaTracker 包装成一条 Damage 记录（类型 "Harm"）
//  再交给 StaminaTracker.TakeDamage(Damage) 执行。渲染完全不在这里。
//
//  规则（按"瓦砖格数"计算，不按像素）：
//    · 5 格以内 → 免伤
//    · 超过 5 格后，每满 / 不满 2 格 → 扣 5 点体力上限
//
//  为什么用格而不用像素？
//    Celeste 里 1 个瓦砖 = 8px，是地图的天然单位。
//    用格数来描述高度，你调数值时可以直接对着地图数砖块。
// ============================================================================
internal static class FallDamageHandler {
    // Celeste 里 1 个瓦砖的边长（像素）。
    // 地图的网格就是这个尺寸，所有地形对齐都基于它。
    internal const float TileSize = 8f;

    // 免伤高度：掉这么多格以内不扣血
    internal const float FreeFallTiles = 5f;

    // 超过免伤高度后，每多少格扣一次血
    internal const float TilesPerTick = 2f;

    // 每一跳扣多少点体力上限
    internal const int DamagePerTick = 5;

    // 计算这次摔落应该扣多少点体力上限。
    // 返回 0 表示不扣血（高度不够，或者根本没在摔）。
    //
    // 参数 pixelDistance：本次摔落的像素高度差（起跳点.Y 到落地.Y）
    //
    // 换算过程：
    //    格数      = 像素高度 / 8
    //    超出的格数 = 格数 - 5          （负数/0 表示没超过，直接免伤）
    //    倍率      = ceil(超出的格数 / 2)   ← 向上取整，"不满 2 格也算 2 格"
    //    伤害      = 倍率 × 5
    internal static int Calculate(float pixelDistance) {
        float fallTiles = pixelDistance / TileSize;

        // 没超过免伤格数 → 不掉血
        if (fallTiles <= FreeFallTiles) {
            return 0;
        }

        // 只有"超出的部分"才计入伤害
        float excessTiles = fallTiles - FreeFallTiles;

        // 向上取整：塞牙缝的一点点也算一整跳。
        // 比如超出 0.1 格 → 1 跳；超出 2.0 格 → 1 跳；超出 2.1 格 → 2 跳。
        int ticks = (int) Math.Ceiling(excessTiles / TilesPerTick);

        return ticks * DamagePerTick;
    }
}
