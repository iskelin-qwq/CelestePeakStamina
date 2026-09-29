using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Celeste.Mod.PeakStamina;

// ============================================================================
//  伤害类型的颜色表
//
//  每种伤害类型在体力条上显示的颜色在这里统一登记。
//  这样"类型 → 颜色"只有一个定义处，不会散落在各个调用点。
//
//  新增一种伤害类型时，只要在这里加一行即可。
// ============================================================================
public static class DamageColors {
    // 摔落伤害 —— 红色（网页色号 #E03A3A）
    public const string Harm = "Harm";

    // 饥饿伤害 —— 黄色（网页色号 #F2C744）
    public const string Hunger = "Hunger";

    // 类型名 → 颜色
    private static readonly Dictionary<string, Color> Table = new() {
        { Harm,   new Color(0xE0, 0x3A, 0x3A) },
        { Hunger, new Color(0xF2, 0xC7, 0x44) },
    };

    // 未登记类型时用的兜底色（洋红，方便一眼看出"忘了登记颜色"）
    private static readonly Color Fallback = new Color(0xFF, 0x00, 0xFF);

    // 查颜色
    public static Color Get(string damageType) {
        if (damageType != null && Table.TryGetValue(damageType, out Color color)) {
            return color;
        }

        return Fallback;
    }

    // 创建一条伤害记录（自动带上该类型的颜色）
    public static Damage Create(string damageType, int number) {
        return new Damage {
            DamageType  = damageType,
            Number      = number,
            DamageColor = Get(damageType),
        };
    }

    // 给列表里所有条目补上颜色。
    // 从存档反序列化出来的条目没有颜色（颜色不被序列化），
    // 所以渲染前要调一次这个方法。
    public static void Apply(IEnumerable<Damage> damages) {
        if (damages == null) {
            return;
        }

        foreach (Damage damage in damages) {
            if (damage != null) {
                damage.DamageColor = Get(damage.DamageType);
            }
        }
    }
}


// ============================================================================
//  伤害累积
//
//  管理那个"存放所有伤害类型的向量"，并实现合并规则：
//
//    列表里没有同名类型 → 直接插入新条目
//    列表里已有同名类型 → 把数值加到那一条上（不新增条目）
// ============================================================================
public static class DamageAccumulator {
    // 把一条伤害累加进列表，并返回它代表的**总伤害量**。
    public static int Apply(List<Damage> damages, Damage incoming) {
        if (damages == null || incoming == null || incoming.Number <= 0) {
            return 0;
        }

        // 查有没有同类型
        foreach (Damage existing in damages) {
            if (existing != null && existing.DamageType == incoming.DamageType) {
                existing.Number += incoming.Number;

                // 顺便把颜色刷新一下，保证渲染用的是当前登记的颜色
                existing.DamageColor = DamageColors.Get(existing.DamageType);

                return existing.Number;
            }
        }

        // 没有同类型 → 插入新条目
        incoming.DamageColor = DamageColors.Get(incoming.DamageType);
        damages.Add(incoming);

        return incoming.Number;
    }

    // 列表里所有伤害的总和（也就是被扣掉的体力上限）
    public static int Total(List<Damage> damages) {
        if (damages == null) {
            return 0;
        }

        int total = 0;

        foreach (Damage damage in damages) {
            if (damage != null) {
                total += damage.Number;
            }
        }

        return total;
    }

    // ------------------------------------------------------------------
    //  治疗
    // ------------------------------------------------------------------
    // 和 Apply 正好相反：把数值从已有条目上减掉。
    //
    //   列表里没有这个类型 → 什么都不做
    //   列表里已有这个类型 → 数值 -=，减到 <= 0 就把整个条目移除
    //
    // 参数 incoming.Number 表示"要治疗多少点"，必须是正数。
    // 返回：这次实际治好了多少点（可能少于请求值，因为该类型的伤害本来就不够）。
    public static int Heal(List<Damage> damages, Damage incoming) {
        if (damages == null || incoming == null || incoming.Number <= 0) {
            return 0;
        }

        // 反着遍历：因为可能会移除元素，正着遍历会打乱索引
        for (int i = damages.Count - 1; i >= 0; i--) {
            Damage existing = damages[i];

            if (existing == null || existing.DamageType != incoming.DamageType) {
                continue;
            }

            // 该类型的伤害本来就不够治 → 只能治掉剩下的这些
            int healed = Math.Min(incoming.Number, existing.Number);
            existing.Number -= incoming.Number;

            // 减到 0 或以下 → 整条移除（没有伤害了，也就不该占体力条上的位置）
            if (existing.Number <= 0) {
                damages.RemoveAt(i);
            }

            return healed;
        }

        // 没找到同类型 → 什么都不做
        return 0;
    }
}
