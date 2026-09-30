using Monocle;

namespace Celeste.Mod.PeakStamina;

// ============================================================================
//  体力上限管理器（挂在 Player 自己身上的组件）
//
//  它干三件事：
//    ① 保存"当前体力上限"（持久化在 PeakStaminaModuleSession，切关卡重置）
//    ② 每帧盯住玩家是不是在空中往下掉，落地时算一次摔落伤害
//    ③ 保证玩家的真实体力永远不超过上限
//
//  为什么挂在 Player 上而不是那个绿条实体上？
//    因为绿条实体的 Update 每帧跑在很后面（它是在 LoadLevel 钩子里才被加进关卡的），
//    而"把体力压到上限以下"这件事必须紧跟玩家的 Update，否则玩家每帧都会先
//    以超过上限的体力去爬墙、再去补算体力，出现"偷取体力"的漏洞。
//    挂到 Player 上就能保证：玩家 Update 干了什么，我们同一帧内立刻收口。
// ============================================================================
public class StaminaTracker : Component {
    // 原始体力上限。原版 Celeste 里 Player.ClimbMaxStamina 就是这个值，
    // 普通状态下 RefillStamina() 也是把体力回满到 110。
    public const float BaseMaxStamina = 110f;

    // 体力上限的下限。
    //
    // ★ 这个值现在是【致死阈值】的一部分，不只是个防除零的兜底：
    //   致死判定是"伤害 >= 当前上限 − 下限"，下限取 0 时就退化成
    //       伤害 >= 当前上限  →  死亡
    //   也就是"伤害把上限吃光"就死。
    //
    //   推论：存活时上限最小是 1，永远不会是 0
    //   （因为只有 伤害 < 上限 才会活下来）。
    public const float StaminaCapFloor = 0f;

    // 刚进地图的"免伤宽限期"（秒）。
    //
    // 为什么需要它：玩家出生点往往在地面上方一点点，落地时会有一小段下落。
    // 如果不豁免，刚进图就会莫名其妙掉血。
    // 一秒足够覆盖出生下落和各种出场动画（IntroTypes），之后开始正常判定。
    private const float SpawnGraceTime = 1.0f;

    // ------------------------------------------------------------------
    //  ★ 动作代价
    // ------------------------------------------------------------------
    // 做这些动作时要额外付体力。扣的是【当前体力】(player.Stamina)。
    //
    // 注意：这是额外加上的规则，不是原版行为 ——
    // 原版 Celeste 里跳跃和冲刺都【不消耗】体力，只有抓墙爬墙才消耗。
    public const int JumpStaminaCost = 10;    // 每次跳跃
    public const int DashStaminaCost = 35;    // 每次冲刺

    // ------------------------------------------------------------------
    //  ★ ultra 判定用的常量
    // ------------------------------------------------------------------
    // ultra 豁免给玩家 0.5 秒时间，落地后要在这个窗口内完成 ultra 动作。
    private const float UltraJumpWindow = 0.5f;

    // ultra 判定的速度门槛：40000 = 200²，也就是水平速度超过 200px/s。
    //
    // ★ 这个值【故意比 TechAnnouncer 宽松】。TechAnnouncer 用的是 57600 (240²)，
    //   但它只是拿它决定"要不要播报 ultradash 音效"——门槛偏严无所谓，漏播一次没人受伤。
    //
    //   我们把它当作【伤害豁免的闸门】，门槛过严会很难受：
    //   实测数据里，一次真正做出前置的 ultra 是 Speed²=52777（约 230px/s），
    //   用 57600 会以"差 4%"的差距判定失败，从完全免伤变成全额受伤，没有中间状态。
    //   而普通跳跃（没做 ultra 前置）的速度只有 1247 / 8100 这个量级，
    //   所以降到 40000 仍然能准确区分，同时给真 ultra 留出余量。
    private const float UltraSpeedSquared = 40000f;

    // 动作扣费后，持续压制体力多久（秒）。
    // 用来抵消原版在动作前后调用 RefillStamina() 造成的"体力被回满"。
    // 0.25 秒（约 15 帧）足够覆盖原版的各种回满时机，
    // 又短到不会干扰之后正常的落地/水晶回满。
    private const float StaminaHoldTime = 0.25f;

    // ------------------------------------------------------------------
    //  ★ 饥饿
    // ------------------------------------------------------------------
    // 从"重生 / 重开章节"那一刻开始计时，每隔一段时间扣一次上限。
    //
    // 计时器会在每次 ApplyStartLevelReset() 时归零 —— 那个方法正是由
    // LoadLevel / Reload 触发的，也就是死亡重生、按 R 重试、切换章节
    // 都会让饥饿重新开始计时。
    private const float HungerInterval = 60f;   // 每隔多少秒饿一次
    private const int HungerDamage = 5;         // 每次扣多少点上限

    // 玩家实体。构造函数里传进来，存成字段。
    private readonly Player player;

    // 上一帧玩家在不在空中。用来找"刚落地"的那一帧。
    private bool wasOnGround;

    // 上一帧在不在冲刺状态。用来找"刚进入冲刺"的那一帧。
    // （攀爬不再需要这个 —— 现在攀爬期间每帧都重置参考高度，
    //   不需要判断"刚进入"，所以没有 wasClimbing 了。）
    private bool wasDashing;

    // 宽限期倒计时。归零之后才开始算摔落伤害。
    private float graceTimer = SpawnGraceTime;

    // ------------------------------------------------------------------
    //  ★ 摔落参考高度
    // ------------------------------------------------------------------
    // 摔落高度 = 这个值 - 落地时的 Y。它按下面三条规则被"重置"：
    //
    //    1. 起跳时（OnPlayerJump 被 Jump 钩子调用）
    //    2. 抓住墙壁后（刚进入攀爬状态的那一帧）
    //    3. 冲刺时（刚进入冲刺状态的那一帧）
    //
    // 除此之外它保持不变 —— 所以玩家跳起来再落回原高度，摔落高度是 0，
    // 不会因为"最高点"而白白扣血。
    //
    // initialized 的作用：组件是在关卡加载时创建的，那时玩家还没落地。
    // 如果不加这个标记，参考高度会被设成"空中的出生点"，
    // 于是出生落地那一下会被算成摔落伤害。所以第一次落地之前不记录任何值。
    private float fallReferenceY;
    private bool fallReferenceInitialized;

    // ------------------------------------------------------------------
    //  关卡重置相关（见 RequestStartLevelReset 的说明）
    // ------------------------------------------------------------------
    // pendingStartReset：LoadLevel / Reload 钩子立下的 flag，Update 里消费掉
    // lastLevel：上一次 Update 时看到的关卡对象，用来发现"换关卡了"
    private bool pendingStartReset;
    private Level lastLevel;

    // ------------------------------------------------------------------
    //  ★ ultra dash 免伤（仅限"斜下 / 向下冲刺"）
    // ------------------------------------------------------------------
    // 含义：本次"离地期间"做过一次【向下分量的冲刺】，这次落地就免摔落伤害。
    //
    // 判定依据是冲刺方向 player.DashDir：
    //     DashDir.Y > 0  → 冲刺朝下（纯下冲是 (0,1)，斜下冲是两个分量都非 0）
    //     DashDir.Y == 0 → 水平冲刺，【不】豁免
    //   所以单纯的空中水平冲刺不会白拿免伤，只有下冲类才免。
    //
    //   地面上的冲刺（hyper、super）不会置位，因为那一刻 OnGround() 还是 true；
    //   只有真正在空中发动的下冲才算。落地时清零，所以豁免不跨跳跃。
    private bool downwardDashSinceTakeoff;

    // ------------------------------------------------------------------
    //  ultra 判定状态（复刻 TechAnnouncer 的做法）
    // ------------------------------------------------------------------
    // collidingV         ：这一帧玩家撞到了垂直方向的实体（通常就是地面）
    // ultraDucked        ：在"贴着地面"的状态下把 Ducking 设成了 true
    //                      （也就是玩家按着下蹲键贴在地上，这是 ultra 的前置）
    // pendingUltraDamage ：落地时被"缓期执行"的摔落伤害
    // ultraDamageTimer   ：缓期执行的剩余时间，归零还没做出 ultra 就照罚
    private bool collidingV;
    private bool ultraDucked;
    private Damage pendingUltraDamage;
    private float ultraDamageTimer;

    // ------------------------------------------------------------------
    //  动作代价的"基准体力"
    // ------------------------------------------------------------------
    // staminaAtFrameStart：上一帧收口之后记录的干净体力值。
    //
    // ★ 所有"按当前体力扣费"的动作（跳跃、墙跳、冲刺）都必须用它当基准，
    //   绝对不能直接读 player.Stamina。
    //
    //   原因：这些动作的按键是在【玩家自己的 Update】里处理的，
    //   那一刻原版可能刚刚把体力回满到 110（最典型的是"落地那一帧"
    //   和"地面上发起冲刺"）。直接读就会拿到被污染的满值，
    //   表现为"从体力上限开始扣"。
    //
    //   而这个变量是在我们自己的 Update 里、ClampPlayerStamina 之后记录的，
    //   所以永远满足 体力 ≤ 上限，是可靠的读数。
    private float staminaAtFrameStart;

    // 动作扣费后的"体力保持"状态。
    // staminaHoldTarget ：扣费后"本该是"的体力值，作为压制上限
    // staminaHoldTimer  ：还要保持多久
    private float staminaHoldTarget;
    private float staminaHoldTimer;

    // "接下来这次 Jump 来自爬墙跳"的标记。见 OnPlayerJumpAction 的说明。
    private bool suppressNextJumpCost;

    // 饥饿倒计时（秒）。归零时扣一次上限，然后重置回 HungerInterval。
    // 每帧用 Engine.DeltaTime 递减，所以游戏暂停 / 过场冻结期间不会饿。
    private float hungerTimer = HungerInterval;

    // 因为这是个挂在 Player 身上的组件，构造函数需要 Player 引用。
    // active: true  → 每帧要跑 Update（检测落地 + 收口体力）
    // visible: false → 它自己不画任何东西，别浪费一次 Render 调用
    public StaminaTracker(Player player) : base(active: true, visible: false) {
        this.player = player;

        // 注意：这里【不要】去读写 Session.Damages 或 player.Stamina。
        // 因为组件是在 LoadLevel 早期创建的，那时 Everest 还没把 Session 存档
        // 加载完，读到的会是上一局的旧值 —— 这正是之前"进图绿条不满"的根源。
        // 所有和上限有关的初始化都统一交给 ApplyStartLevelReset() 里的
        // ResetMaxStamina()，那时序靠后，数据一定是准的。
    }

    // ------------------------------------------------------------------
    //  当前体力上限
    // ------------------------------------------------------------------
    // 不单独存一个数，而是由 Session 里的"伤害向量"算出来：
    //     上限 = 基础上限 110 − 所有伤害之和
    // 这样就不可能出现"伤害表和上限对不上"的状态。
    public float MaxStamina => PeakStaminaModule.Session.StaminaCap;

    // ------------------------------------------------------------------
    //  ★ 起跳事件（由 PeakStaminaModule 的 On.Celeste.Player.Jump 钩子调用）
    // ------------------------------------------------------------------
    // 起跳的瞬间把参考高度设成"起跳那一刻的 Y"。
    // 之后玩家先往上飞再往下落，参考高度都不会变，于是：
    //    落在起跳点以上 → 摔落高度为负 → 不扣血
    //    落回起跳点     → 摔落高度为 0  → 不扣血
    //    落在起跳点以下 → 才开始按格数扣血
    public void OnPlayerJump() {
        fallReferenceY = player.Y;
        fallReferenceInitialized = true;
    }

    // ------------------------------------------------------------------
    //  ★ 动作代价：跳跃
    // ------------------------------------------------------------------
    // 由 On.Celeste.Player.Jump（普通地面跳）和
    // On.Celeste.Player.WallJump（墙跳，含中性跳）两个钩子调用。
    // 记录起跳高度 + 扣 10 点体力。
    public void OnPlayerJumpAction() {
        // 起跳高度永远要记录（和总开关无关，这是摔落判定的基础）
        OnPlayerJump();

        // ------------------------------------------------------------------
        // ★ 爬墙跳不在这里扣费
        // ------------------------------------------------------------------
        // 原版 ClimbJump() 的实现（IL 实测）是：
        //     Stamina = Stamina - 27.5f;   ← 先自己扣 27.5
        //     ...
        //     Jump();                      ← 再调用 Jump()，于是触发我们这个钩子
        //
        // 如果我们在这里再按"基准 − 10"扣一次，就会把原版那次 27.5 覆盖掉，
        // 净消耗反而变成 10（比原版还少）。
        //
        // 所以爬墙跳期间跳过我们的计费，让原版那 27.5 保持生效。
        if (suppressNextJumpCost) {
            return;
        }

        SpendJumpStamina();
    }

    // ------------------------------------------------------------------
    //  标记"接下来这次 Jump 来自爬墙跳"
    // ------------------------------------------------------------------
    // 由 On.Celeste.Player.ClimbJump 的钩子调用。
    public void BeginClimbJump() {
        suppressNextJumpCost = true;
    }

    // 爬墙跳流程结束后清掉标记（避免残留到之后的普通跳跃）
    public void EndClimbJump() {
        suppressNextJumpCost = false;
    }

    // ==================================================================
    //  ★ 动作代价的统一扣费口径
    // ==================================================================
    // ★★ 为什么不直接用 player.Stamina？★★
    //   跳跃 / 冲刺的按键是在【玩家自己的 Update】里处理的，
    //   那一刻原版可能刚刚把体力回满到 110（最典型的是"落地那一帧"
    //   和"地面上发起冲刺"）。直接减就会变成"从体力上限开始扣"。
    //
    //   正确口径：
    //     ① 先把体力压回上一帧收口后的干净值（staminaAtFrameStart）
    //     ② 再从这个基准上减去代价
    //
    //   第 ① 步同时起两个作用：
    //     · 把原版刚回满的那部分抹掉，保证基准正确
    //     · 顺手完成了"体力不得超过上限"的收口
    private void SpendStaminaFromBaseline(int amount) {
        if (amount <= 0) {
            return;
        }

        float baseline = Calc.Min(staminaAtFrameStart, MaxStamina);
        float target = Calc.Max(baseline - amount, 0f);

        player.Stamina = target;

        // ------------------------------------------------------------------
        // 还要"按住"这个结果一小段时间，理由见 BeginStaminaHold 的说明。
        // ------------------------------------------------------------------
        BeginStaminaHold(target);

        // 扣完之后立刻检查有没有见底（体力归零的惩罚）
        ApplyZeroStaminaPenalty();
    }

    // ------------------------------------------------------------------
    //  动作扣费后的"体力保持"
    // ------------------------------------------------------------------
    // ★★ 为什么扣完还要保持一段时间？★★
    //   原版在很多地方会调用 RefillStamina()（把体力直接设回满值），
    //   而这一步既可能发生在我们扣费【之前】（落地那一帧），
    //   也可能发生【之后】（冲刺启动的协程里）。
    //
    //   只在扣费那一刻压一次，挡不住"之后才发生"的那种回满 ——
    //   表现就是"体力闪一下又变满、动作却没有代价"。
    //
    //   所以在接下来的一小段时间里，只要体力超过这个目标值就把它压回去。
    //   只做上限压制（不主动扣），所以玩家自己继续消耗体力不受影响。
    private void BeginStaminaHold(float target) {
        staminaHoldTarget = target;
        staminaHoldTimer = StaminaHoldTime;
    }

    private void ApplyStaminaHold() {
        if (staminaHoldTimer <= 0f) {
            return;
        }

        staminaHoldTimer -= Engine.DeltaTime;

        if (player.Stamina > staminaHoldTarget) {
            player.Stamina = staminaHoldTarget;
        }
    }

    // ==================================================================
    //  ★ ultra 判定
    // ==================================================================
    // 判定逻辑完全复刻 TechAnnouncer 里 Player_Jump 的做法：
    //
    //   ① NotifyCollideV        —— 撞到垂直实体（通常是地面）时记一帧
    //   ② NotifyDuckingOnGround —— 贴着地面时按下蹲键（ultra 的前置动作）
    //   ③ IsUltraJump           —— 起跳那一刻检查：
    //                                处于普通状态(State == 0)
    //                                且速度平方 > 240²
    //
    // 三者全部满足才算做出 ultra。

    // 由 On.Celeste.Player.OnCollideV 钩子调用
    public void NotifyCollideV(bool enter) {
        collidingV = enter;
    }

    // 由 Player.Ducking 属性 setter 的 Hook 调用
    public void NotifyDuckingOnGround() {
        if (collidingV) {
            ultraDucked = true;
        }
    }

    // 由 On.Celeste.Player.Jump 钩子调用（判定必须发生在扣体力之前）
    public bool IsUltraJump() {
        bool isUltra = ultraDucked
                    && player.StateMachine.State == Player.StNormal
                    && player.Speed.LengthSquared() > UltraSpeedSquared;

        // 和 TechAnnouncer 一样：不管成没成，判定完就清掉前置状态
        ultraDucked = false;

        return isUltra;
    }

    // 由 On.Celeste.Player.Jump 钩子调用，参数表示"这次是不是 ultra"
    public void NotifyJump(bool isUltra) {
        // 做出 ultra → 免掉这次落地的摔落伤害。
        // 不是 ultra 就什么都不做：押后的伤害会由倒计时照常结算。
        if (isUltra) {
            ClearPendingFallDamage();
        }
    }

    // ==================================================================
    //  ★ 缓期执行的摔落伤害
    // ==================================================================
    // 斜下冲刺落地时不再立刻结算，而是把伤害"押后" 0.5 秒：
    //
    //   在这 0.5 秒内做出 ultra  → 免除这次伤害
    //   没做出 ultra / 超时      → 照常扣血
    //
    // 这样 ultra 豁免就不再是"冲刺过就无条件免伤"，
    // 而是必须真正把 ultra 做出来才行。

    // 落地时调用：如果本次滞空做过下降冲刺，就把伤害押后
    private void FreezeFallDamageIfUltra(Damage damage) {
        if (damage == null || damage.Number <= 0) {
            return;
        }

        if (!downwardDashSinceTakeoff) {
            // 没有下降冲刺 → 普通摔落，立刻结算
            TakeDamage(damage);
            return;
        }

        // 有下降冲刺 → 押后，等玩家在窗口内证明自己能做出 ultra。
        // 注意要把【整条 Damage】存下来，不能只存数值 ——
        // 否则延迟结算时会丢掉伤害类型和颜色。
        pendingUltraDamage = damage;
        ultraDamageTimer = UltraJumpWindow;
    }

    // 做出 ultra 时调用：免掉押后的伤害
    private void ClearPendingFallDamage() {
        pendingUltraDamage = null;
        ultraDamageTimer = 0f;
    }

    // 每帧调用：处理押后伤害的倒计时
    private void UpdateUltraDeferral() {
        if (ultraDamageTimer <= 0f) {
            return;
        }

        ultraDamageTimer -= Engine.DeltaTime;

        if (ultraDamageTimer > 0f) {
            return;
        }

        // 窗口结束还没做出 ultra → 照常扣血
        ultraDamageTimer = 0f;

        Damage pending = pendingUltraDamage;
        pendingUltraDamage = null;

        if (pending != null) {
            TakeDamage(pending);
        }
    }

    // ------------------------------------------------------------------
    //  ★ 识别中性跳（Neutral Jump）
    // ------------------------------------------------------------------
    // 中性跳指的是：跳离墙的时候【没有按左右方向键】。
    //
    // 为什么只看方向键、不看抓取键？
    //   从"跳完能飞多远"的机制看，决定因素只有方向键：
    //   不按方向键时，原版不会给那 0.16 秒的离墙控制权（forceMoveX），
    //   人飞得很近、能立刻贴回墙上，反复操作就能不消耗体力地爬墙。
    //   而抓取键（Grab）对跳出距离没有任何影响。
    //
    //   所以"按住抓取 + 不按方向"跳墙，结果和中性跳是一样的，
    //   必须一并卡住 —— 否则玩家只要一直按着抓取键，
    //   就能绕过体力门槛无限爬墙。
    public bool IsNeutralWallJump() {
        if (!PeakStaminaModule.Settings.Enabled) {
            return false;
        }

        return Input.MoveX.Value == 0;
    }

    // ------------------------------------------------------------------
    //  ★ 中性跳的体力门槛
    // ------------------------------------------------------------------
    // 规则：体力耗尽（<= 0）时，中性跳不再可用。
    //
    // 为什么单独限制中性跳？
    //   中性跳的看家本领就是"零体力爬墙"。如果体力见底了还能靠它爬，
    //   体力系统就被架空了。所以体力归零时把这个漏洞堵上，
    //   而按了方向键的普通墙跳不受影响。
    public bool HasStaminaForNeutralJump() {
        // mod 被关掉时不介入，永远放行
        if (!PeakStaminaModule.Settings.Enabled) {
            return true;
        }

        return player.Stamina > 0f;
    }

    // ------------------------------------------------------------------
    //  ★ 冲刺的体力门槛
    // ------------------------------------------------------------------
    // 规则：体力归零（<= 0）时禁止冲刺。
    //
    // 为什么在"发起冲刺那一刻"检查，而不是等体力被扣到 0 再锁死？
    //   因为爬墙扣体力是原版逻辑，我们不去改它。
    //   如果只在"动作扣体力导致归零"时处理，玩家爬到体力见底后
    //   仍然能冲刺，等于留了个免费的逃生手段。
    //   把检查放到冲刺入口，就覆盖了所有让体力归零的途径
    //（爬墙、跳跃、上一次冲刺、摔落……）。
    public bool CanDash() {
        // mod 被关掉时不介入，永远放行
        if (!PeakStaminaModule.Settings.Enabled) {
            return true;
        }

        return player.Stamina > 0f;
    }

    // ------------------------------------------------------------------
    //  内部：收跳跃的体力
    // ------------------------------------------------------------------
    private void SpendJumpStamina() {
        if (!PeakStaminaModule.Settings.Enabled) {
            return;
        }

        SpendStaminaFromBaseline(JumpStaminaCost);
    }

    // ------------------------------------------------------------------
    //  ★ 动作代价：冲刺（在冲刺启动期间"压制"体力）
    // ------------------------------------------------------------------
    // 由 On.Celeste.Player.StartDash 钩子调用。
    // StartDash 是原版所有冲刺（普通 / 红冲 / 羽毛）的唯一入口，
    // 挂它就能保证一次冲刺只扣一次，不会重复。
    //
    // ★★ 为什么不能只扣一次，而要"压制"？★★
    //   StartDash 只是【发起】冲刺（设置状态、启动协程），玩家不会在同一帧
    //   离地，真正的位移和原版内部的 RefillStamina() 发生在之后若干帧。
    //   实测：即便把扣费推迟一帧，那 35 点仍会被后面某帧的回满覆盖 ——
    //   表现就是"地面上向上冲刺时体力不扣"。
    //
    //   靠猜帧数不可靠，所以改成：
    //     记下冲刺发生【之前】的体力值，然后在冲刺启动期间持续把它压回去。
    //   这样不管原版在什么时候回满、回满几次，结果都是"体力停在该停的位置"。
    public void OnPlayerDashAction() {
        if (!PeakStaminaModule.Settings.Enabled) {
            return;
        }

        // 和其他动作一样：从"上一帧收口后的干净体力"上扣，
        // 不能直接读 player.Stamina（冲刺键处理时它可能刚被原版回满）。
        float baseline = Calc.Min(staminaAtFrameStart, MaxStamina);
        float target = Calc.Max(baseline - DashStaminaCost, 0f);

        // 立刻压到目标值，让体力条的反馈及时
        if (player.Stamina > target) {
            player.Stamina = target;
        }

        // 冲刺的启动是协程，原版的回满可能发生在好几帧之后，
        // 所以要靠"保持"来挡住 —— 这也是最初修这个 bug 的原因。
        BeginStaminaHold(target);

        // 顺手处理"扣完见底"的惩罚（冲刺次数归零）
        ApplyZeroStaminaPenalty();
    }

    // ------------------------------------------------------------------
    //  ★ 体力归零 → 冲刺次数归零
    // ------------------------------------------------------------------
    // 放在 SpendStaminaFromBaseline / OnPlayerDashAction 里统一调用，
    // 所以【所有】让体力见底的动作都会触发，不用在每处单独写。
    //
    // 注意：原版 Celeste 里，玩家只要【站在地面上】冲刺次数就会被自动补满，
    //   所以这条规则实际只在【空中】体力耗尽时看得出效果。
    //   这是原版机制，我们没有去改它。
    private void ApplyZeroStaminaPenalty() {
        if (player.Stamina > 0f) {
            return;
        }

        // Dashes 是原版的冲刺次数字段，归零后玩家就无法再冲刺
        player.Dashes = 0;
    }

    // ==================================================================
    //  ★ 饥饿
    // ==================================================================
    // 从"重生 / 重开章节"开始，每 HungerInterval 秒受到一次饥饿伤害。
    //
    // 计时器在 ApplyStartLevelReset() 里归零，所以：
    //   死亡重生   → 重新计时
    //   按 R 重试  → 重新计时
    //   切换章节   → 重新计时
    // 这和"从重生或重新开始章节的一刻开始"的要求一致。
    //
    // 用 Engine.DeltaTime 计时，所以游戏暂停 / 过场冻结时不会饿 ——
    // 它计的是"玩家真正在玩的时间"。

    private void UpdateHunger() {
        // 已经死了就别再饿（死亡流程期间不该继续扣上限）
        if (player.Dead) {
            return;
        }

        hungerTimer -= Engine.DeltaTime;

        if (hungerTimer > 0f) {
            return;
        }

        // 用 while 而不是 if：万一某帧卡顿很久（DeltaTime 很大），
        // 该扣几次就扣几次，不会漏掉。
        while (hungerTimer <= 0f) {
            hungerTimer += HungerInterval;
            ApplyHungerDamage();
        }
    }

    private void ApplyHungerDamage() {
        // 走统一的承伤入口，这样：
        //   · 会和已有的饥饿伤害合并到同一个条目上
        //   · 会触发致死判定（饥饿伤害 > 当前上限 时死亡）
        //   · 颜色由 DamageColors 统一决定（黄色）
        TakeDamage(DamageColors.Create(DamageColors.Hunger, HungerDamage));
    }

    // ------------------------------------------------------------------
    //  ★ 承伤函数
    // ------------------------------------------------------------------
    // 这是本 mod 唯一的"掉上限"入口。所有会削减体力上限的东西
    //（摔落、将来的尖刺、Boss 攻击……）都必须调用它，
    // 不要自己去改 Session 里的伤害表，那样以后改规则会漏掉。
    //
    // 参数 damage：一条伤害记录，包含类型 / 数值 / 显示颜色
    // 返回：本类型伤害累计后的总量。死亡或无效伤害时返回 0。
    //
    // 累积规则由 DamageAccumulator 负责：
    //   伤害表里没有这个类型 → 插入新条目
    //   已经有这个类型       → 数值累加到该条目上
    //
    // ★ 致死规则：这次伤害会让上限掉到【下限】或以下 → 死亡。
    //   下限 StaminaCapFloor 取 0，所以规则就是最简单的一句：
    //
    //       伤害 >= 当前上限  →  死亡
    //
    //   也就是"伤害把上限吃光"就死。举例：
    //     上限 75，受 74 点 → 新上限 1 > 0  → 活（只剩 1 点！）
    //     上限 75，受 75 点 → 新上限 0      → 死
    //     上限 75，受 90 点 → 新上限 -15    → 死
    //
    //   因为只有"伤害 < 上限"才会活下来，所以存活时上限最小是 1，
    //   永远不可能是 0 —— 画体力条时不会遇到 0 或负数。
    public int TakeDamage(Damage damage) {
        // 防御：空引用或非正数值直接忽略，避免"负伤害"反而加回上限
        if (damage == null || damage.Number <= 0) {
            return 0;
        }

        // 已经死了就别重复处理（比如同一个事件被多路径触发）
        if (player.Dead) {
            return 0;
        }

        // 注意：上限必须是"这次伤害生效之前"的值。
        // DamageAccumulator.Apply 会立刻改动伤害表，所以要先取。
        float before = MaxStamina;

        // ------------------------------------------------------------------
        //  致死判定
        // ------------------------------------------------------------------
        // 必须精确用 float 比较，不要用 (int)before：
        // 因为 damage.Number 是 int，如果写成 (int)before，那么
        // 60.9 会被截断成 60，导致边界情况被误判。
        if (damage.Number >= before - StaminaCapFloor) {
            float wouldBe = before - damage.Number;

            Logger.Log(LogLevel.Info, "PeakStamina",
                $"受到 {damage.DamageType} 伤害 {damage.Number} 点，" +
                $"上限将从 {before} 掉到 {wouldBe}（下限 {StaminaCapFloor}）→ 判定死亡");

            // 让玩家以摔落方向死亡。
            // player.Speed 是当前速度，作为下落方向传给 Die —— 和原版摔死一致，
            // 角色会朝那个方向弹飞出去。
            player.Die(player.Speed);
            return 0;
        }

        // 记进伤害表。同类伤害会在里面累加，不会新增条目。
        int accumulated = DamageAccumulator.Apply(PeakStaminaModule.Session.Damages, damage);

        // 关键：上限降下来之后，玩家身上"还没花完"的体力也要一起压到上限以下。
        // 否则上限从 110 掉到 55，玩家手里还攥着 110 的体力，等于这次摔伤没有惩罚。
        ClampPlayerStamina();

        return accumulated;
    }

    // ------------------------------------------------------------------
    //  ★ 治疗函数
    // ------------------------------------------------------------------
    // 和 TakeDamage 正好相反：把某一种伤害从伤害表里减掉，上限随之回升。
    //
    // 规则由 DamageAccumulator.Heal 负责：
    //   伤害表里没有这个类型 → 什么都不做
    //   伤害表里已有这个类型 → 该条目的数值 -=，减到 <= 0 就把整条移除
    //
    // 参数 heal.Number 表示"要治疗多少点"，必须是正数。
    // 返回：这次实际治好了多少点（可能少于请求值，因为该类型伤害本来就不够）。
    //
    // ★ 和治疗相关的一点说明：
    //   上限回升后，玩家当前体力【不会】自动跟着涨。
    //   这和 TakeDamage 的做法是对称的 —— 那边是上限下降时把体力压下来，
    //   这边是上限上升时不动体力，多出来的余量靠原版的落地 / 冲刺水晶回满。
    //   这样"治病"只恢复上限，不额外送体力，数值上更好控制。
    public int Heal(Damage heal) {
        if (heal == null || heal.Number <= 0) {
            return 0;
        }

        return DamageAccumulator.Heal(PeakStaminaModule.Session.Damages, heal);
    }

    // ------------------------------------------------------------------
    //  ★ 重置上限（TakeDamage 的反操作）
    // ------------------------------------------------------------------
    // 三个地方会调用它：
    //   · 进入一张新地图 / 关卡重试（经 RequestStartLevelReset → ApplyStartLevelReset）
    //   · 玩家死亡 —— 不让玩家陷入"上限剩 5、连墙都爬不动"的死局
    //   · ApplyStartLevelReset 里作为重置的一部分
    public void ResetMaxStamina() {
        // 清空伤害表 —— 因为没有伤害 = 上限就是满值 110。
        // 上限是由伤害表算出来的，所以"重置上限"就是"清空伤害"。
        PeakStaminaModule.Session.Damages.Clear();

        // 上限补满之后，把玩家当前体力一起抬到上限。
        // 否则玩家会带着"上一局残留的残血体力"进图，那就不叫重置了。
        player.Stamina = MaxStamina;
    }

    // ------------------------------------------------------------------
    //  ★ 关卡请求重置（只打标记，真正重置推迟到 Update）
    // ------------------------------------------------------------------
    // 由 On.Celeste.Level.LoadLevel 和 On.Celeste.Level.Reload 两个钩子调用。
    //
    // ★★ 为什么不在这里立刻重置？★★
    //   因为 Everest 是在关卡构建【之后】才把 Session 存档读取并覆盖到
    //   Session 对象上的。如果我们在 LoadLevel 里就把 MaxStamina 设成 110，
    //   紧接着就被存档里的旧值（比如 70）盖掉 —— 这正是"加载存档后体力没还原"
    //   的根源。日志里能清楚看到：组件创建时 MaxStamina=70，
    //   而 player.Stamina 已经是 110，两者对不上就是这个时序差造成的。
    //
    //   所以这里只立一个 flag，等 Update 跑到第一帧时再真正重置。
    //   那时序一定在存档载入之后，拿到的 Session 是最终值。
    public void RequestStartLevelReset() {
        pendingStartReset = true;
    }

    // ------------------------------------------------------------------
    //  内部：真正执行关卡开始时的重置
    // ------------------------------------------------------------------
    private void ApplyStartLevelReset() {
        // ------------------------------------------------------------------
        // 体力上限：由设置决定要不要在这一关开始时恢复
        // ------------------------------------------------------------------
        // 打开（默认）：每进一个房间 / 重试一次，上限都补满 110。
        // 关闭        ：上限跨房间保留 —— 摔伤的代价会一直累积，
        //               只有玩家死亡时的那次重置才会把它清掉。
        if (PeakStaminaModule.Settings.RestoreStaminaCapOnRoomTransition) {
            ResetMaxStamina();
        }

        // 清掉摔落追踪状态，让这一局从零开始
        fallReferenceInitialized = false;
        fallReferenceY = player.Y;
        graceTimer = SpawnGraceTime;

        // 重置"上一帧状态"，避免刚重试就误判成"刚落地 / 刚进冲刺"
        wasOnGround = true;
        wasDashing  = player.StateMachine.State == Player.StDash
                   || player.StateMachine.State == Player.StRedDash;

        // 冲刺豁免标记也要清掉，不然上一次跳跃的冲刺会跨关豁免
        downwardDashSinceTakeoff = false;

        // ultra 判定状态和押后的伤害都要清干净
        ultraDucked = false;
        collidingV = false;
        ClearPendingFallDamage();

        // ------------------------------------------------------------------
        // 饥饿计时归零
        // ------------------------------------------------------------------
        // 这个方法由 LoadLevel / Reload 触发，也就是：
        //   死亡重生 / 按 R 重试 / 切换章节
        // 都会走到这里，饥饿从这一刻重新开始计时。
        hungerTimer = HungerInterval;

        // 跨关卡时清掉动作扣费的"体力保持"状态，免得把上一关的压制带过来
        staminaHoldTimer = 0f;
    }

    // ------------------------------------------------------------------
    //  Update：检测摔落 + 强制收口体力
    // ------------------------------------------------------------------
    public override void Update() {
        base.Update();

        // ------------------------------------------------------------------
        // ⓪ 总开关：玩家在设置里关掉本 mod 后，立刻停止一切介入
        // ------------------------------------------------------------------
        // 这一关的体力条实体和组件都已经生成好了，没法中途撤销，
        // 所以改在这里"空转"：不扣体力、不检测摔落，顺便把上限复原，
        // 免得关掉之后玩家还留着被扣过的上限。
        if (!PeakStaminaModule.Settings.Enabled) {
            PeakStaminaModule.Session.Damages.Clear();
            return;
        }

        // ------------------------------------------------------------------
        // ① 关卡开始的重置（必须放在最前面，先于体力收口）
        // ------------------------------------------------------------------
        // 两条触发路径，互为补充：
        //   · pendingStartReset —— LoadLevel / Reload 钩子打的标记
        //                          覆盖"同一关卡对象内的重试"
        //   · 关卡对象变了       —— 兜底，覆盖任何我们没挂到的入口
        //                          （加载别的存档、从菜单直接进图、传送等）
        Level currentLevel = Scene as Level;

        if (pendingStartReset || (currentLevel != null && currentLevel != lastLevel)) {
            pendingStartReset = false;
            lastLevel = currentLevel;
            ApplyStartLevelReset();
        }

        // 每帧都把玩家体力压到上限以下。
        // 这一步必须每帧做，因为 RefillStamina() 会把体力直接设成 110，
        // 而我们没法（也不该）去改原版那个方法。
        ClampPlayerStamina();

        // 结算动作之后的"体力保持"：原版可能在动作前后把体力回满，
        // 这里把它压回"扣费后该有的值"，保证动作代价不会被抹掉。
        // 放在 ClampPlayerStamina 之后，成为这一帧的最终值。
        ApplyStaminaHold();

        // ------------------------------------------------------------------
        // ★ 记录这一帧"干净"的体力值，供下一次动作扣费当基准
        // ------------------------------------------------------------------
        // 此时玩家自己的 Update 已经跑完、ClampPlayerStamina 也已经执行，
        // 所以这个值一定不超过上限，是可靠的读数。
        //
        // 为什么不能等到动作发生时再读 player.Stamina？
        //   因为跳跃 / 冲刺的按键都是在玩家自己的 Update 里处理的，
        //   而那时原版可能刚刚把体力回满到 110（例如"落地那一帧"）。
        //   实测日志里能看到 上限=70 但体力=110 的状态 —— 拿它当基准就会扣错，
        //   表现成"从最高体力上限开始扣"。
        //   详见 SpendStaminaFromBaseline 的注释。
        staminaAtFrameStart = player.Stamina;

        // 推进 ultra 豁免的"缓期执行"倒计时。
        // 做出 ultra 会提前把押后的伤害清掉；超时则照常扣血。
        UpdateUltraDeferral();

        // 推进饥饿倒计时（到点就扣一次上限）。
        UpdateHunger();

        // 递减宽限期
        if (graceTimer > 0f) {
            graceTimer -= Engine.DeltaTime;
        }

        bool onGround = player.OnGround();
        bool climbing = player.StateMachine.State == Player.StClimb;
        bool dashing  = player.StateMachine.State == Player.StDash
                     || player.StateMachine.State == Player.StRedDash;

        // ------------------------------------------------------------------
        // ② 冲刺相关的记录 + 摔落参考高度重置
        //
        //    ★ 这里有两件【互相独立】的事，不要合并成一个条件：
        //
        //      (a) 记录"本次滞空做过向下的冲刺"
        //          只看 DashDir.Y > 0，用于 ultra 豁免。
        //
        //      (b) 重置摔落参考高度
        //          任何方向的空中冲刺都要重置 —— 这是机动性补偿：
        //          玩家从高处掉下来时可以用冲刺"接住"自己，
        //          冲刺的起点成为新的摔落起点，从而少受伤。
        //          如果只认向下冲刺，水平/向上冲刺就救不了命了。
        //
        //    ★ 注意 (a) 不能用 `!wasDashing` 来限定"刚进入冲刺那一帧"：
        //      实测（日志）进入冲刺状态的那一帧 player.DashDir 还是 (0,0)，
        //      原版是在冲刺开始后的下一帧才给它赋值的。
        //      所以要在"处于冲刺状态期间"持续检查，一旦看到向下就置位。
        //
        //    而 (b) 用 `!wasDashing` 限定只做一次 —— 不能每帧都重置，
        //    否则参考高度会跟着玩家一起往下滑，最后算出接近 0 的落差。
        //
        //    整段必须放在落地结算【之前】——
        //    因为玩家很可能就是"冲刺中直接砸到地面"（斜下冲刺落地时
        //    State 还是 StDash），如果先结算再记录，这一帧就漏判，白扣一次血。
        // ------------------------------------------------------------------
        bool airborneDash = dashing && !onGround;

        // (b) 任何空中冲刺都重置参考高度（只做一次）
        if (airborneDash && !wasDashing) {
            fallReferenceY = player.Y;
            fallReferenceInitialized = true;
        }

        // (a) 向下冲刺才置位 ultra 豁免标记（本段滞空只记一次）
        if (airborneDash && player.DashDir.Y > 0f) {
            downwardDashSinceTakeoff = true;
        }

        if (onGround) {
            // 这一帧刚落地（上一帧还在空中）→ 结算摔落
            if (!wasOnGround) {
                ResolveFallDamage();

                // 落地之后本次滞空结束，清除冲刺豁免标记。
                // 放在结算【之后】清，保证刚落地这一帧的豁免仍然有效。
                downwardDashSinceTakeoff = false;
            }

            // 站在地面上，参考高度就一直跟着脚底走。
            // 这样玩家走出悬崖时，参考高度正好是悬崖边的高度。
            fallReferenceY = player.Y;
            fallReferenceInitialized = true;

        } else if (climbing) {
            // 规则 2：攀爬期间【每一帧】都重置参考高度。
            //
            // 效果：只要人在攀爬状态，摔落起点就始终是"当前所在高度"。
            // 所以挂在墙上怎么下滑都不算摔落，等到从墙上松手时，
            // 落差也是从松手那一刻的位置开始算。
            //
            // 这里刻意【不加】`!wasClimbing` 之类的"只重置一次"限制 ——
            // 那会让参考高度停在刚抓住墙的位置，于是"爬到墙顶再松手掉下来"
            // 会被算成从抓墙点起算的巨大落差。
            fallReferenceY = player.Y;
            fallReferenceInitialized = true;

        } else {
            // 下降冲刺的重置已经在上面 ② 里处理掉了（只记一次）。
            // 这里不需要做任何事：空中非攀爬时，参考高度保持不动。
        }

        wasOnGround = onGround;
        wasDashing  = dashing;
    }

    // ------------------------------------------------------------------
    //  内部：落地结算
    // ------------------------------------------------------------------
    private void ResolveFallDamage() {
        // 宽限期内不算摔落。出生下落、出场动画都会落在这里。
        if (graceTimer > 0f) {
            return;
        }

        // 还没建立起参考高度（第一次落地之前）→ 没有可算的落差
        if (!fallReferenceInitialized) {
            return;
        }

        float landingY = player.Y;

        // 摔落高度 = 落地高度 - 参考高度。
        // 如果玩家落得比参考点还高（比如跳起来落在平台上），这就是负数，
        // FallDamageHandler 会直接返回 0。
        int damage = FallDamageHandler.Calculate(landingY - fallReferenceY);

        if (damage <= 0) {
            return;
        }

        // 把这次摔落包装成一条伤害记录。
        // 类型用 DamageColors.Harm，颜色由注册表统一决定。
        //
        // ★ 这里不再直接扣血，改成交给"缓期执行"：
        //   如果本次滞空做过下降冲刺，就给玩家 0.5 秒时间去做出 ultra，
        //   做出来免伤，做不出来照罚。详见 FreezeFallDamageIfUltra 的说明。
        FreezeFallDamageIfUltra(DamageColors.Create(DamageColors.Harm, damage));
    }

    // ------------------------------------------------------------------
    //  内部：把玩家体力收口到上限以内
    // ------------------------------------------------------------------
    private void ClampPlayerStamina() {
        float cap = MaxStamina;

        if (player.Stamina > cap) {
            player.Stamina = cap;
        } else if (player.Stamina < 0f) {
            // 顺手兜一下负数，防止原版某些极端状态下体力算成负数
            player.Stamina = 0f;
        }
    }
}
