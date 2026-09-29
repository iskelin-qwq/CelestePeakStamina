namespace Celeste.Mod.PeakStamina;

// ============================================================================
//  Mod 设置：会显示在游戏里的「模组选项 → PeakStamina」菜单中
//
//  ★ 两个必须知道的规矩：
//
//    1. 必须写成【属性】，不能写成字段！
//       Everest 生成选项菜单时用的是 type.GetProperties()，
//       它会完全忽略 public 字段，写了也不会出现在菜单里。
//       而且属性必须同时有 get 和 set（缺一个就会被跳过）。
//
//    2. 属性名叫 Enabled 是刻意的。
//       Everest 内置了这个名字的翻译（"启用"/"Enabled"…），
//       所以菜单里会自动显示成本地化文字。换成别的名字就会显示成
//       拆开的英文单词（SpacedPascalCase），除非你自己准备翻译文件。
//
//  设置会被 Everest 自动读写到 Saves\modsettings-PeakStamina.celeste，
//  不需要自己写任何存档代码。
// ============================================================================
public class PeakStaminaModuleSettings : EverestModuleSettings {
    // 总开关。关掉之后本 mod 完全不介入：
    //   · 不生成体力条
    //   · 不检测摔落、不扣上限
    //   · 不限制玩家体力
    // 相当于原版 Celeste 的手感。
    public bool Enabled { get; set; } = true;

    // ------------------------------------------------------------------
    //  切换房间时恢复体力上限
    // ------------------------------------------------------------------
    // 打开（默认）：每次加载关卡 / 切换房间，体力上限都恢复成满值 110。
    // 关闭        ：上限会一直保留下去，跨房间也不会恢复，
    //               只有玩家死亡（或重进关卡）才会清零重来。
    //
    // 关闭之后就变成"一命制"的耐力挑战：摔伤的代价会累积到整局结束。
    //
    // 名字用 SettingName 特性显式指定，否则 Everest 会把它按
    // SpacedPascalCase 拆成很长的英文标题。
    [SettingName("Restore Stamina Cap on Room Transition")]
    public bool RestoreStaminaCapOnRoomTransition { get; set; } = true;
}
