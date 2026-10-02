using System;
using Microsoft.Xna.Framework;
using Monocle;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.PeakStamina;

public class PeakStaminaModule : EverestModule {
    public static PeakStaminaModule Instance { get; private set; }

    // 挂钩 Player.Ducking 属性的 setter 用的对象。
    // Ducking 是属性而不是方法，没有 On.Celeste 事件可挂，只能用 Hook。
    private Hook duckingHook;

    public override Type SettingsType => typeof(PeakStaminaModuleSettings);
    public static PeakStaminaModuleSettings Settings => (PeakStaminaModuleSettings) Instance._Settings;

    public override Type SessionType => typeof(PeakStaminaModuleSession);
    public static PeakStaminaModuleSession Session => (PeakStaminaModuleSession) Instance._Session;

    public override Type SaveDataType => typeof(PeakStaminaModuleSaveData);
    public static PeakStaminaModuleSaveData SaveData => (PeakStaminaModuleSaveData) Instance._SaveData;

    public PeakStaminaModule() {
        Instance = this;

#if DEBUG
        // debug builds use verbose logging
        Logger.SetLogLevel(nameof(PeakStaminaModule), LogLevel.Verbose);
#else
        // release builds use info logging to reduce spam in log files
        Logger.SetLogLevel(nameof(PeakStaminaModule), LogLevel.Info);
#endif

    }

    public override void Load() {
        On.Celeste.Level.LoadLevel += OnLoadLevel;
        On.Celeste.Level.Reload += OnLevelReload;
        On.Celeste.Player.Die += OnPlayerDie;
        On.Celeste.Player.Jump += OnPlayerJump;
        On.Celeste.Player.WallJump += OnPlayerWallJump;
        On.Celeste.Player.ClimbJump += OnPlayerClimbJump;
        On.Celeste.Player.UseRefill += OnPlayerUseRefill;
        On.Celeste.Player.StartDash += OnPlayerStartDash;
        On.Celeste.Player.OnCollideV += OnPlayerCollideV;

        // Player.Ducking 是【属性】，没有 On.Celeste 事件可挂，只能用 Hook
        // 直接改它的 setter —— 这样就能捕捉到"贴地时按下蹲"这个 ultra 前置动作。
        duckingHook = new Hook(
            typeof(Player).GetProperty("Ducking").GetSetMethod(),
            new Action<Action<Player, bool>, Player, bool>(OnPlayerDuckingSet)
        );

        Logger.Log(LogLevel.Info, "PeakStamina", "Load() 执行了，钩子已挂上！");
    }

    public override void Unload() {
        On.Celeste.Level.LoadLevel -= OnLoadLevel;
        On.Celeste.Level.Reload -= OnLevelReload;
        On.Celeste.Player.Die -= OnPlayerDie;
        On.Celeste.Player.Jump -= OnPlayerJump;
        On.Celeste.Player.WallJump -= OnPlayerWallJump;
        On.Celeste.Player.ClimbJump -= OnPlayerClimbJump;
        On.Celeste.Player.UseRefill -= OnPlayerUseRefill;
        On.Celeste.Player.StartDash -= OnPlayerStartDash;
        On.Celeste.Player.OnCollideV -= OnPlayerCollideV;

        duckingHook?.Dispose();
        duckingHook = null;

        Logger.Log(LogLevel.Info, "PeakStamina", "Unload() 执行了，钩子已摘下。");
    }

    // 玩家把 Ducking 设成 true 时：如果他正贴着地面，就记下这次"贴地下蹲"。
    // 这是 ultra 的前置状态 —— 落地瞬间按着下蹲键。
    private static void OnPlayerDuckingSet(Action<Player, bool> orig, Player self, bool ducking) {
        if (ducking) {
            self.Components.Get<StaminaTracker>()?.NotifyDuckingOnGround();
        }

        orig(self, ducking);
    }

    // 每次关卡加载完成后：
    //   ① 把体力上限重置为满值（关键！见下面注释）
    //   ② 把我们的体力条实体塞进关卡里
    // 之后关卡会把它当成"界面元素"来渲染，我们什么都不用管。
    private static void OnLoadLevel(On.Celeste.Level.orig_LoadLevel orig, Level level, Player.IntroTypes playerIntro, bool isFromLoader) {
        orig(level, playerIntro, isFromLoader);

        // 没有玩家的关卡（比如过场图）就跳过，否则后面拿 player 会崩
        Player player = level.Tracker.GetEntity<Player>();
        if (player == null) {
            return;
        }

        // 玩家把 mod 关掉了 → 这一关完全不介入，连体力条都不生成，
        // 玩家身上的 StaminaTracker 也一个都不挂，手感就是原版。
        if (!Settings.Enabled) {
            return;
        }

        level.Add(new StaminaBarEntity());

        // ★ 进图时把上限补满。
        //
        // 为什么必须在这里重置、而不能只靠 PeakStaminaModuleSession 的字段初始值？
        //   因为 Everest 会把 Session 存档（Saves\<存档>-modsession-PeakStamina.celeste）
        //   加载并覆盖到 Session 对象上。字段初始值只在对象构造时执行一次，
        //   执行完之后立刻就被存档里的旧值盖掉了 ——
        //   结果就是"上一局摔掉的上限会被继承到下一局"，进新图绿条也是残缺的。
        //
        // ★ 请求把上限补满。
        //
        // 注意这里只是"立个标记"，真正的重置由 StaminaTracker.Update 在
        // 第一帧执行 —— 因为 Everest 是在关卡构建【之后】才把 Session 存档
        // 覆盖进来，此刻重置会被存档里的旧值盖掉。
        // 详见 StaminaTracker.RequestStartLevelReset() 的注释。
        player.Components.Get<StaminaTracker>()?.RequestStartLevelReset();
    }

    // 关卡重试（按 R 自杀、掉坑复活、死亡后 Retry）走的是 Level.Reload()，
    // 【不会】再走一次 Level.LoadLevel。
    // 所以只挂 LoadLevel 是不够的 —— 那样"重试之后上限没还原"就是这个原因。
    // 这里补上 Reload 的钩子，让两条路径都能重置。
    private static void OnLevelReload(On.Celeste.Level.orig_Reload orig, Level level) {
        orig(level);

        Player player = level.Tracker.GetEntity<Player>();
        if (player == null) {
            return;
        }

        // StaminaBarEntity 是关卡里的实体，重试时会跟着重建，
        // 但 StaminaTracker 挂在 Player 身上、会跨重试保留下来，
        // 所以我们能在这个已有实例上直接打标记。
        player.Components.Get<StaminaTracker>()?.RequestStartLevelReset();
    }

    // 玩家死亡时：把摔掉的那部分体力上限还回来。
    // 具体怎么还由 StaminaTracker.ResetMaxStamina() 负责。
    private static PlayerDeadBody OnPlayerDie(
        On.Celeste.Player.orig_Die orig,
        Player player,
        Vector2 direction,
        bool evenIfInvincible,
        bool registerDeathInStats
    ) {
        PlayerDeadBody body = orig(player, direction, evenIfInvincible, registerDeathInStats);

        // body 是 null 表示这次 Die 被忽略了（比如开了无敌，或者玩家已经死了）。
        // 那种情况不算真正死亡，就别重置。
        //
        // 不需要在这里检查总开关：ResetMaxStamina() 自己会检查。
        if (body != null) {
            player.Components.Get<StaminaTracker>()?.ResetMaxStamina();
        }

        return body;
    }

    // 玩家跳跃（普通地面跳）：ultra 判定 + 记录起跳高度 + 扣跳跃体力。
    // 【不禁止任何跳跃】，照常放行，只是要付体力。
    //
    // ★ 顺序很重要（和冲刺那边同理）：
    //   1. ultra 判定要最先 —— 它依赖只在"起跳前"才有效的前置状态
    //   2. 原版逻辑执行
    //   3. 最后才扣体力 —— 否则会被原版内部的 RefillStamina() 覆盖掉
    private static void OnPlayerJump(On.Celeste.Player.orig_Jump orig, Player player, bool particles, bool playSfx) {
        StaminaTracker tracker = player.Components.Get<StaminaTracker>();

        bool isUltra = tracker != null && tracker.IsUltraJump();

        orig(player, particles, playSfx);

        if (tracker != null) {
            tracker.OnPlayerJumpAction();   // 记录起跳高度
            tracker.NotifyJump(isUltra);
        }
    }

    // 玩家撞到垂直方向的实体：记录这一帧"贴着地"。
    // 供 ultra 判定使用（复刻 TechAnnouncer 的做法）。
    private static void OnPlayerCollideV(On.Celeste.Player.orig_OnCollideV orig, Player player, CollisionData data) {
        player.Components.Get<StaminaTracker>()?.NotifyCollideV(enter: true);

        orig(player, data);

        player.Components.Get<StaminaTracker>()?.NotifyCollideV(enter: false);
    }

    // 玩家墙跳：普通墙跳照常放行；中性跳在体力耗尽时被禁止。
    //
    // ★ 规则（中性跳 = 跳墙时【没按左右方向键】，抓取键不影响判定）
    //   按了方向键        ：普通墙跳，原版行为，体力 −10
    //   没按方向键 + 体力 > 0：原版行为，体力 −10
    //   没按方向键 + 体力 <= 0：【禁止】，且不扣体力（因为没跳成）
    //
    //   为什么单独卡中性跳？因为它本来就是"零体力爬墙"的技巧，
    //   体力见底还能用的话，体力系统等于白做。
    private static void OnPlayerWallJump(On.Celeste.Player.orig_WallJump orig, Player player, int dir) {
        StaminaTracker tracker = player.Components.Get<StaminaTracker>();

        // 门槛必须在 orig 之前判断：那时体力还是"跳之前"的值
        if (tracker != null && tracker.IsNeutralWallJump() && !tracker.HasStaminaForNeutralJump()) {
            return;   // 没体力了，吞掉这次中性跳（不调用 orig，所以也不扣体力）
        }

        orig(player, dir);

        // 跳成了才记账 + 扣体力，放在最后以免被原版的回满覆盖
        tracker?.OnPlayerJumpAction();
    }

    // 玩家爬墙跳：这里要做两件事。
    //
    // ★ 第一件：让紧接着的那次 Jump 不要重复计费 ——
    //   ClimbJump 内部会调用一次 Jump()，而原版自己已经扣过 27.5 体力了。
    //   如果不标记，我们会用"基准 − 10"覆盖掉原版的 27.5，净消耗反而变成 10。
    //
    //   原版 ClimbJump 的 IL 实测顺序：
    //       Stamina = Stamina - 27.5f;   ← 先扣
    //       Jump();                      ← 再调 Jump（触发我们的钩子）
    //
    // ★ 第二件：封掉"偷体力跳"（wall boost）——
    //   ClimbJump 还会把 wallBoostTimer 设成 0.2 秒、wallBoostDir 设成 dir。
    //   之后玩家只要在这 0.2 秒内朝墙方向推，原版 NormalUpdate 里这段就会触发：
    //
    //       if (wallBoostTimer > 0 && moveX == wallBoostDir) {
    //           Speed.X = 130f * moveX;      // 位移加速
    //           Stamina += 27.5f;            // ★ 把爬墙跳的体力还回来 = 偷体力
    //       }
    //
    //   于是爬墙跳变成零消耗。我们在 orig 之后把计时器清零，
    //   那个 if 就永远不成立，体力不会再被返还。
    //
    //   （顺带一提：这段里的位移加速也一起没了。两者写在同一个代码块里，
    //     没法只砍一个。但那个加速只在"朝墙推"时触发，而墙跳后朝墙推
    //     本身就是自毁操作，所以实战代价可以忽略。）
    private static void OnPlayerClimbJump(On.Celeste.Player.orig_ClimbJump orig, Player player) {
        StaminaTracker tracker = player.Components.Get<StaminaTracker>();

        tracker?.BeginClimbJump();

        try {
            orig(player);

            // 抹掉原版刚开启的回墙加速窗口。
            // 放在 orig 之后：那时它才被设成 0.2f。
            //
            // ★ 必须检查总开关：关掉 mod 时这个技巧要恢复正常，
            //   否则就成了"mod 关了还在改原版行为"。
            if (Settings.Enabled) {
                player.wallBoostTimer = 0f;
            }
        } finally {
            // 放在 finally 里，保证即使中途抛异常也不会把标记留下来
            tracker?.EndClimbJump();
        }
    }

    // 玩家吃到恢复水晶（Refill）。
    //
    // ★ 为什么需要：冲刺后的 0.25 秒"体力压制"是无差别的，它分不清
    //   「原版自己偷偷回满」（要压制）和「玩家主动吃水晶恢复」（要允许），
    //   结果就是"冲刺时吃水晶不回体力"。
    //   所以在这里把压制窗口取消掉。
    //
    // ★ 为什么挂 UseRefill 而不是 RefillStamina：
    //   RefillStamina() 原版有十几个调用点（Bounce / BoostBegin / DreamDashEnd …），
    //   那些正是【要压制的内部回满】。而 UseRefill 只有一个调用方
    //   Refill::OnPlayer，也就是真正碰到水晶。挂它最精确。
    //
    // 返回值必须透传：原版靠它判断"这次到底有没有恢复成功"。
    private static bool OnPlayerUseRefill(On.Celeste.Player.orig_UseRefill orig,
                                          Player player, bool twoDashes) {
        bool refilled = orig(player, twoDashes);

        // 只有真的恢复了才取消压制。
        // UseRefill 在"冲刺次数本来就满、且体力 >= 20"时会返回 false（什么都没做），
        // 那种情况下不该动压制窗口。
        if (refilled) {
            player.Components.Get<StaminaTracker>()?.NotifyRefill();
        }

        return refilled;
    }

    // 玩家冲刺：体力归零时禁止，否则扣 35 点体力。
    //
    // StartDash 是原版所有冲刺（普通 / 红冲 / 羽毛）的唯一入口，
    // 挂它就能保证一次冲刺只扣一次。
    //
    // ★ 直接 return 会吞掉这次冲刺（原版逻辑不执行），
    //   同时也消耗掉了玩家这次按键 —— 所以不会出现"按了没反应但输入被缓存、
    //   过一会儿突然自己冲出去"的诡异现象。
    //
    // ★ 体力必须在 orig 之后扣，不能之前！
    //   因为原版在很多地方会调用 RefillStamina()（把体力直接设回 110），
    //   例如站上地面、以及"地面上向上冲刺"这条特殊路径。
    //   如果先扣再执行原版，那 35 点会立刻被原版的回满覆盖掉 ——
    //   表现为"体力闪一下又变满、冲刺却没代价"。
    //   放到最后扣，就保证扣费是这一帧的最终结果。
    private static int OnPlayerStartDash(On.Celeste.Player.orig_StartDash orig, Player player) {
        StaminaTracker tracker = player.Components.Get<StaminaTracker>();

        // 体力归零 → 禁止冲刺。
        // 注意这个门槛必须在 orig 之前判断：那时体力还是"冲刺前"的值，
        // 否则会被原版的回满影响，判断就失效了。
        if (tracker != null && !tracker.CanDash()) {
            return 0;
        }

        int result = orig(player);

        // 冲刺已经成立 → 这时才扣体力，盖过原版可能发生的回满
        tracker?.OnPlayerDashAction();

        return result;
    }
}
