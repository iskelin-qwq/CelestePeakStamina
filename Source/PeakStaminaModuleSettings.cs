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

    // ==================================================================
    //  各功能的子开关
    // ==================================================================
    //  以下开关只在总开关 Enabled 打开时才有意义。
    //  总开关一关，这些全部失效（本 mod 完全不介入）。
    //
    //  ★ 每个子开关都【不】影响其他功能。例如关掉"摔落伤害"后，
    //    摔落依然会重置参考高度，只是不结算伤害。
    //
    //  ★★ 它们在菜单里被收进「详细设置」子菜单，但【存储格式仍然是扁平的】★★
    //
    //    为什么要绕这一圈？——为了保护玩家已有的设置不丢失。
    //
    //    YamlDotNet 序列化嵌套对象时会写成：
    //        Detail:
    //          EnableFallDamage: true
    //    而本 mod 之前的存档是扁平的：
    //        EnableFallDamage: true
    //    直接改成嵌套的话，老存档里的值会全部读不回来（等于把玩家的设置清空）。
    //
    //    所以这里用两层结构：
    //      · 本类里这 5 个属性 —— 【负责存储】，名字和原来一模一样，
    //        于是 YAML 结构完全不变。它们被 [SettingIgnore] 从菜单里隐藏。
    //      · 下面 DetailSettings 类里的同名转发属性 —— 【负责菜单显示】，
    //        读写都转发到上面那几个属性上。
    //
    //    Everest 只会在【生成菜单】时检查 [SettingIgnore]，
    //    序列化时完全不看它 —— 这才让上面这个做法成立。
    // ==================================================================

    // ------------------------------------------------------------------
    //  1. 摔落伤害
    // ------------------------------------------------------------------
    // 关掉后从高处掉落不再扣体力上限（也不再有 ultra 豁免的押后结算）。
    [SettingIgnore]
    public bool EnableFallDamage { get; set; } = true;

    // ------------------------------------------------------------------
    //  2. 饥饿伤害
    // ------------------------------------------------------------------
    // 关掉后不再按时间扣上限。
    //
    // ★ 关掉的瞬间会把饥饿计时器【重置】—— 这是刻意的：
    //   玩家关掉饥饿通常是想"暂停这个机制"，那么"这一轮攒了多久"
    //   就不该被保留。重新打开时从零开始重新计满一分钟。
    //   （重置动作在 setter 里做，见下面 DetailSettings 的实现。）
    [SettingIgnore]
    public bool EnableHungerDamage { get; set; } = true;

    // ------------------------------------------------------------------
    //  3. 动作代价
    // ------------------------------------------------------------------
    // 关掉后跳跃 / 墙跳 / 冲刺都不再消耗当前体力。
    // 注意：爬墙跳消耗的 27.5 体力是【原版行为】，不受这个开关影响。
    //
    // 另外注意：归零惩罚现在是【每帧独立检查】的，
    // 所以关掉这个开关【不会】连带禁用"清空冲刺次数"。
    [SettingIgnore]
    public bool EnableActionCosts { get; set; } = true;

    // ------------------------------------------------------------------
    //  4. 禁用偷体力跳（wall boost）
    // ------------------------------------------------------------------
    // 打开（默认）：爬墙跳后朝墙推【不会】返还那 27.5 体力。
    // 关闭        ：恢复原版技巧，爬墙跳可以偷体力。
    //
    // 之所以写成"禁用XX"而不是"允许XX"，是为了让默认值 true
    // 对应"本 mod 的特色行为"，和文档里的描述一致。
    [SettingIgnore]
    public bool DisableWallBoost { get; set; } = true;

    // ------------------------------------------------------------------
    //  5. 体力归零惩罚
    // ------------------------------------------------------------------
    // 打开（默认）：体力归零时冲刺次数也清成 0（空中无法再冲刺）。
    // 关闭        ：体力归零不再影响冲刺次数。
    //
    // 注意"体力为 0 时禁止冲刺"和"禁止中性跳"这两条门槛也归这个开关管。
    [SettingIgnore]
    public bool EnableZeroStaminaPenalty { get; set; } = true;

    // ==================================================================
    //  「详细设置」子菜单
    // ==================================================================
    //  ★ [SettingSubMenu] 标在【嵌套类】上，不是标在属性上！
    //    Everest 的判断逻辑是"属性的【类型】有没有这个特性"，
    //    标在属性上会得到编译错误 CS0592。
    //
    //  标好之后 Everest 会：
    //    1. 自动 new 一个 DetailSettings 实例（用 Activator.CreateInstance）
    //    2. 在菜单里建一个可展开的子菜单
    //    3. 遍历这个类的属性，逐个生成菜单项
    //
    //  DetailSettings 里的属性【只负责显示】，get/set 都转发到上面那 5 个
    //  存储属性。所以 YAML 里保存的仍然是上面那些扁平名字，存档结构完全不变。
    [SettingName("详细设置")]
    public DetailSettings Detail { get; set; } = new DetailSettings();

    [SettingSubMenu]
    public class DetailSettings : EverestModuleSettings {
        // ------------------------------------------------------------------
        //  转发目标
        // ------------------------------------------------------------------
        // 为什么直接用静态的 PeakStaminaModule.Settings，而不是在构造时
        // 存一个"外层实例"引用？
        //
        //   因为 Everest 造这个类的实例时用的是 Activator.CreateInstance，
        //   只会调无参构造，我们没法在那里拿到外层实例；而且它加载存档、
        //   重建菜单时还会再造新实例，存下来的引用会失效。
        //
        //   PeakStaminaModule.Settings 始终指向"当前那份生效的设置"，
        //   而菜单构建、玩家点开关这些时机它一定有值 —— 所以直接用它最稳，
        //   既不用维护引用，也不用在 Load/Save 时重新接线。
        //
        // 兜底：万一静态引用还没建立（理论上不该发生），get 返回 true、
        //       set 直接忽略，至少不会抛异常。
        private static PeakStaminaModuleSettings S => PeakStaminaModule.Settings;

        [SettingName("Enable Fall Damage")]
        public bool EnableFallDamage {
            get => S?.EnableFallDamage ?? true;
            set { if (S != null) { S.EnableFallDamage = value; } }
        }

        [SettingName("Enable Hunger Damage")]
        public bool EnableHungerDamage {
            get => S?.EnableHungerDamage ?? true;
            set {
                if (S == null) {
                    return;
                }

                // 从"开"变成"关"的那一刻 → 让所有玩家身上的饥饿计时归零
                if (S.EnableHungerDamage && !value) {
                    PeakStaminaModule.ResetHungerTimers();
                }

                S.EnableHungerDamage = value;
            }
        }

        [SettingName("Enable Action Stamina Costs")]
        public bool EnableActionCosts {
            get => S?.EnableActionCosts ?? true;
            set { if (S != null) { S.EnableActionCosts = value; } }
        }

        [SettingName("Disable Wall Boost (Stamina Steal)")]
        public bool DisableWallBoost {
            get => S?.DisableWallBoost ?? true;
            set { if (S != null) { S.DisableWallBoost = value; } }
        }

        [SettingName("Enable Zero Stamina Penalty")]
        public bool EnableZeroStaminaPenalty {
            get => S?.EnableZeroStaminaPenalty ?? true;
            set { if (S != null) { S.EnableZeroStaminaPenalty = value; } }
        }
    }
}
