using Microsoft.Xna.Framework;
using YamlDotNet.Serialization;

namespace Celeste.Mod.PeakStamina;

// ============================================================================
//  伤害条目
//
//  代表"一种伤害来源累积在体力上限上的总量"。
//  体力上限 = 基础上限 110 − 所有伤害条目的数值之和。
//
//  同一种类型只会存在一个条目：再次受到同类型伤害时，
//  会直接累加到已有条目的数值上（见 DamageAccumulator.Apply）。
//
//  用法示例（摔落伤害）：
//      new Damage {
//          DamageType  = "Harm",
//          Number      = 5,
//          DamageColor = Color.Red,
//      }
// ============================================================================
public class Damage {
    // 伤害类型名。同类伤害会合并到同一条目上。
    public string DamageType { get; set; } = "";

    // 这种伤害累计扣掉了多少点体力上限。
    public int Number { get; set; }

    // 这种伤害在体力条上显示的颜色。
    //
    // ★ YamlIgnore：颜色不参与存档序列化。
    //   序列化 Color 会写出一串内部字段、既臃肿又容易在反序列化时报错，
    //   而且颜色本来就是由代码决定的（见 DamageColors），不需要存。
    //   反序列化后这个属性是默认值，会在渲染前由 DamageColors.Apply 补上。
    [YamlIgnore]
    public Color DamageColor { get; set; }
}
