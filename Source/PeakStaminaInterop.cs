namespace Celeste.Mod.PeakStamina;

// ============================================================================
//  对外接口（供其他 mod 调用）
//
//  这是本 mod 公开的稳定入口，其他 mod 可以直接引用 PeakStamina.dll 后调用，
//  不需要了解内部结构（StaminaTracker 是挂在 Player 上的组件）。
//
//  ★ 为什么不做成 Everest 的 interop 注册？
//    Everest 的跨 mod 互操作是【反射驱动的动态机制】，并没有一个强类型的
//    "注册 API" 可调用（我扫过 Celeste.dll / Celeste.Mod.mm.dll，不存在
//    RegisterAPI 这类成员）。依赖它反而更脆。
//    用普通 public static 类，任何 Everest 版本都能用，而且编译期就能检查参数。
//
//  ★ 命名：刻意用 PeakStaminaInterop 这种带前缀的全名。
//    Celeste 的命名空间是扁平合并的，太通用的名字（比如 StaminaAPI）
//    有和其他 mod 撞名的风险。
//
//  用法（在其他 mod 里）：
//      using Celeste.Mod.PeakStamina;
//
//      // 治好 20 点摔落伤害（上限随之回升）
//      int healed = PeakStaminaInterop.Heal(player, DamageColors.Harm, 20);
// ============================================================================
public static class PeakStaminaInterop {
    // ------------------------------------------------------------------
    //  ★ 治疗
    // ------------------------------------------------------------------
    // 参数 player      ：要治疗哪个玩家（通常是 level.Tracker.GetEntity<Player>()）
    // 参数 damageType  ：要治掉哪种伤害。用 DamageColors 里的常量，例如
    //                    DamageColors.Harm（摔落）/ DamageColors.Hunger（饥饿）
    // 参数 amount      ：要治疗多少点
    //
    // 返回：实际治好了多少点。下面几种情况会返回 0 或少于 amount：
    //   · 玩家为空 / mod 被玩家在设置里关掉了
    //   · 伤害表里【没有】这个类型        → 返回 0（什么都不做）
    //   · 该类型的伤害不够扣           → 只治掉剩下的，并把该条目移除
    //
    // 安全性：内部做了全部空引用检查，其他 mod 不需要自己判空。
    public static int Heal(Player player, string damageType, int amount) {
        StaminaTracker tracker = GetTracker(player);

        if (tracker == null || string.IsNullOrEmpty(damageType)) {
            return 0;
        }

        return tracker.Heal(DamageColors.Create(damageType, amount));
    }

    // ------------------------------------------------------------------
    //  ★ 查询类接口
    // ------------------------------------------------------------------

    /// <summary>当前体力上限（= 110 − 所有伤害之和）。玩家无效时返回 0。</summary>
    public static float GetStaminaCap(Player player) {
        StaminaTracker tracker = GetTracker(player);

        return tracker != null ? tracker.MaxStamina : 0f;
    }

    /// <summary>某一种伤害当前累计扣掉了多少点上限。没有该类型时返回 0。</summary>
    public static int GetDamageAmount(Player player, string damageType) {
        if (player == null || string.IsNullOrEmpty(damageType)) {
            return 0;
        }

        // 这一项不依赖 StaminaTracker，只要 Session 在就能查
        PeakStaminaModuleSession session = PeakStaminaModule.Session;
        if (session?.Damages == null) {
            return 0;
        }

        foreach (Damage damage in session.Damages) {
            if (damage != null && damage.DamageType == damageType) {
                return damage.Number;
            }
        }

        return 0;
    }

    /// <summary>本 mod 当前是否处于启用状态（玩家可能在设置里关掉了它）。</summary>
    public static bool IsEnabled() {
        return PeakStaminaModule.Settings != null && PeakStaminaModule.Settings.Enabled;
    }

    // ------------------------------------------------------------------
    //  内部
    // ------------------------------------------------------------------
    // 取玩家身上的体力组件。返回 null 表示"本 mod 当前不介入这个玩家"
    //（被关掉了，或者还没进关卡），调用方应当把它当成"什么都不做"。
    private static StaminaTracker GetTracker(Player player) {
        if (player == null || !IsEnabled()) {
            return null;
        }

        return player.Components.Get<StaminaTracker>();
    }
}
