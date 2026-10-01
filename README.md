# PeakStamina

给《蔚蓝》(Celeste) 加一套**体力经济系统**的 Everest mod。

原版 Celeste 里，体力（Climb Stamina）只影响"抓墙、爬墙"，而且站在地上、吃到冲刺水晶都会回满，基本不构成资源压力。这个 mod 把体力变成了需要精打细算的核心资源：**摔落会永久削减体力上限、跳跃和冲刺都要付体力、呆久了还会饿、体力见底还会连冲刺一起锁掉。**

系统围绕一张**伤害向量**组织：每种永久扣上限的来源都是一条 `Damage`（类型 / 数值 / 显示颜色），体力上限 = 110 − 所有伤害之和。所以体力条上每种伤害都有自己的颜色段。

本项目101.0000%vibe coding

> ⚠️ 本文档里的数值都是当前代码的实际值。想调整手感，直接改源码里的常量后重新编译即可（各数值均标注了所在文件）。

---

## 玩法规则

### 一、体力上限与摔落伤害

体力上限默认 **110 点**（原版满值）。

> 📌 本 mod 也**封禁了"偷体力跳"**（爬墙跳后返体力），详见「二、动作代价」一节。

从高空落地时会扣**上限**——不是临时消耗，而是永久削减，直到死亡或重进关卡才恢复：

| 规则 | 数值 |
|---|---|
| 免伤高度 | **5 格**以内不扣血 |
| 超过免伤高度后 | 每满 / 不满 **2 格** → 扣 **5 点上限** |
| 单位换算 | 1 格 = **8 像素**（Celeste 地图的标准网格） |

举个例子：

| 摔落高度 | 扣的上限 |
|---|---|
| 5 格以内 | 0 |
| 6 格 | 5 |
| 7 格 | 5 |
| 9 格 | 10 |
| 11 格 | 15 |

**上限的下限是 0**：伤害一旦把上限吃光（伤害 ≥ 当前上限）就当场死亡，所以存活时上限最小是 1。

调整位置：`Source/FallDamageHandler.cs`
```csharp
internal const float FreeFallTiles  = 5f;   // 免伤格数
internal const float TilesPerTick   = 2f;   // 几格一跳
internal const int   DamagePerTick  = 5;    // 每跳扣多少
```

#### 摔落高度从哪算起

摔落高度 = 起跳时的 Y 坐标 − 落地时的 Y 坐标。**起跳点**会在下面三种时刻被重置：

1. **起跳时**（跳跃 / 墙跳）
2. **攀爬期间（每一帧都重置）**
3. **刚开始空中冲刺时**（任何方向都算）

所以跳起来再落回原高度是 **0 摔落**，不会因为"跳跃的最高点"而白白扣血。走出悬崖时则以悬崖边的高度为起点。

> 💡 **第 2 条是"整段攀爬持续重置"**，不是只在抓住墙那一帧重置。
> 效果是：只要人在攀爬状态，摔落起点就始终是**当前所在高度**——
> 挂在墙上怎么上下移动都不算摔落，从墙上松手时落差也是从松手位置开始算。
>
> （反过来说，如果改成"只在抓墙那一帧重置"，那"爬到墙顶再松手掉下来"
> 就会被算成从抓墙点起算的巨大落差。当前设定是刻意选择前者的。）

> 💡 **第 3 条是机动性补偿**：从高处掉下来时可以用冲刺"接住"自己，
> 冲刺的起点会成为新的摔落起点，从而少受伤。
> 所以它**不限冲刺方向**——水平、向上、向下冲刺都算（这一条修过一个 bug：
> 早期实现只认向下冲刺，导致摔下来时水平冲刺救不了命）。

#### 致死规则

**如果这次伤害会把体力上限吃光 → 当场死亡**（角色朝受力方向弹飞，回到重生点）。

因为上限的下限是 **0**，规则就是最简单的一句：

> **伤害 ≥ 当前上限 → 死亡**

换成表来看：

| 当前上限 | 伤害 | 新上限会是 | 结果 |
|---|---|---|---|
| 35 | 34 | 1 | 存活（只剩 1 点！）|
| 35 | **35** | 0 | **死亡** |
| 35 | 40 | −5 | **死亡** |
| 20 | 19 | 1 | 存活 |
| 20 | **20** | 0 | **死亡** |
| 6 | 5 | 1 | 存活 |
| 6 | **6** | 0 | **死亡** |

> **存活时上限最小是 1，永远不会是 0** —— 因为只有"伤害 < 上限"才会活下来。
> 这也意味着画体力条时不会遇到除零问题。

死亡后上限恢复满值 110，不会留下残废状态。

调整位置：`Source/StaminaTracker.cs`
```csharp
public const float StaminaCapFloor = 0f;   // 下限，同时也是致死阈值
```

#### ultra 豁免（缓期执行）

**斜下冲刺落地时，摔落伤害不会立刻结算，而是被"押后" 0.5 秒。** 在这 0.5 秒内做出 **ultra**，这次伤害就完全免除；做不出来（或者干脆没跳），时间一到照常扣血。

判定 ultra 的条件（复刻 TechAnnouncer 的做法，三条必须同时满足）：

| 条件 | 说明 |
|---|---|
| 落地瞬间处于"贴地下蹲"状态 | 撞到地面时按下了下蹲键（`Ducking`） |
| 起跳时处于普通状态 | `StateMachine.State == StNormal` |
| 起跳时速度足够快 | 速度平方 > 40000，即 **200 px/s** |

> 所以豁免**不是**"冲刺过就无条件免伤"，而是必须真正把 ultra 做出来。哪怕斜下冲刺后直接落地不跳，也会照常受伤。

> 💡 **速度门槛为什么是 200 而不是 TechAnnouncer 的 240？**
> TechAnnouncer 拿这个值只是决定"要不要播报 ultradash 音效"，门槛偏严无所谓。
> 但我们把它当作**伤害豁免的闸门**，太严会很难受——实测一次真正做出前置的 ultra
> 是 `Speed²=52777`（约 230px/s），用 240 会以"差 4%"判失败，
> 从完全免伤直接跳成全额受伤，没有中间状态。
> 而普通跳跃（没做前置）只有 `Speed²=1247 / 8100` 这个量级，
> 所以降到 200 仍能准确区分，同时给真 ultra 留出余量。

调整位置：`Source/StaminaTracker.cs`
```csharp
private const float UltraJumpWindow   = 0.5f;      // 押后窗口
private const float UltraSpeedSquared = 40000f;    // 200²
```

由于一些bug，这个判定比较宽松。有时即使ultra失败了也不会扣除体力上限

---

### 二、动作代价

做这些动作要额外付**当前体力**：

| 动作 | 体力消耗 |
|---|---|
| 跳跃（地面跳） | **−10** |
| 墙跳（含中性跳） | **−10** |
| 爬墙跳 | **原版数值 −27.5**（本 mod 不对它额外计费） |
| 冲刺 | **−35** |

> 爬墙跳在原版本来就消耗 **27.5 体力**（`ClimbJumpCost`），所以这里写"保持原版"。
>
> ⚠️ 实现上有个坑：原版 `ClimbJump()`（IL 实测）会**先扣 27.5、再调用 `Jump()`**。
> 所以必须识别出"这次 `Jump` 来自爬墙跳"并跳过我们自己的计费，
> 否则会用"−10"覆盖掉原版那次扣费，净消耗反而只剩 10 点。
> 这个识别由 `ClimbJump` 钩子上的标记（`BeginClimbJump` / `EndClimbJump`）完成。

#### 封禁"偷体力跳"（wall boost）

原版有一个公认的技巧：**爬墙跳后在短暂窗口内朝墙方向推，会把刚扣掉的 27.5 体力还回来**，
于是爬墙跳变成零消耗。本 mod **把它封掉了**。

机制（Cecil 扫遍 `Player` 所有方法得到的确切写入点）：

```
ClimbJump
  ├─ Stamina = Stamina - 27.5f      ← 原版先扣
  ├─ Jump()                          ← 再调 Jump
  ├─ wallBoostDir   = dir
  └─ wallBoostTimer = 0.2f           ← 开启 0.2 秒窗口（只有这里会设正值）
        ↓
NormalUpdate 里：
  if (wallBoostTimer > 0 && moveX == wallBoostDir) {
      Speed.X = 130f * moveX;        // 位移加速
      Stamina += 27.5f;              // ★ 把体力还回来 = 偷体力
      wallBoostTimer = 0f;
  }
```

**我们的做法**：在 `ClimbJump` 钩子里，`orig` 返回后立刻把 `wallBoostTimer` 清零 ——
那个 `if` 就永远不成立，体力不会再被返还。

> ⚠️ **副作用**：同一个代码块里的**位移加速也一起没了**。
> 两者无法只砍一个，但那个加速只在"朝墙推"时触发，
> 而墙跳之后朝墙推本身就是自毁操作，所以实战代价可以忽略。

> 📌 实现位置：`Source/PeakStaminaModule.cs` 的 `OnPlayerClimbJump`
> （和上面那条"爬墙跳不重复计费"共用同一个钩子，没有重复挂钩子）。

体力**不足时动作照常执行**，只是扣到 0 为止——不会因为"付不起"而做不出动作。

调整位置：`Source/StaminaTracker.cs`
```csharp
public const int JumpStaminaCost = 10;
public const int DashStaminaCost = 35;
```

---

### 三、饥饿

从**重生 / 重开章节 / 切换房间**的那一刻开始计时，**每 60 秒受到 5 点饥饿伤害**。

| 项目 | 数值 |
|---|---|
| 间隔 | **60 秒** |
| 每次伤害 | **5 点上限** |
| 显示颜色 | **黄色**（`#F2C744`） |

计时会在下面这些时刻**重新开始**：

- 死亡重生
- 按 R 重试
- 切换章节
- 切换房间（因为关卡重载也会走重置）

> 💡 计时用的是游戏内时间（`Engine.DeltaTime`），所以**游戏暂停、过场冻结期间不会饿**——它算的是你真正在玩的时间。

> ⚠️ **饥饿会真的饿死你**（因为致死规则是"伤害把上限吃光即死"）。
> 每次 5 点、下限 0，从满上限 110 开始：
> **约 22 分钟**后上限被压到 5，再饿一次就会死亡。
> 所以长时间卡在一张图上会越来越危险——不只要小心摔落，还要注意时间。

调整位置：`Source/StaminaTracker.cs`
```csharp
private const float HungerInterval = 60f;   // 每隔多少秒饿一次
private const int   HungerDamage   = 5;     // 每次扣多少点上限
```

饥饿伤害的类型名是 `"Hunger"`，颜色登记在 `DamageColors`：

```csharp
public const string Hunger = "Hunger";
// 表里：
{ Hunger, new Color(0xF2, 0xC7, 0x44) },   // 黄色
```

---

### 四、体力归零的惩罚

体力归零时会触发两个后果：

#### 1. 禁止中性跳

中性跳（Neutral Jump）指的是**跳离墙时不按左右方向键**——这样人飞得很近，能立刻贴回墙上，反复操作就能零体力爬墙。这是原版就有的进阶技巧。

本 mod 的规则是：

| 条件 | 结果 |
|---|---|
| 体力 > 0，跳墙时没按左右方向键 | 允许（原版行为），体力 −10 |
| **体力 = 0**，跳墙时没按左右方向键 | **禁止** |
| 按了左右方向键（普通墙跳） | 无论体力多少都允许 |

> 判定**只看左右方向键，不看抓取键**。因为决定跳出去多远的是方向键（原版靠它决定那 0.16 秒的离墙控制权），抓取键对跳出距离没有影响。若把抓取键也算进判定，玩家只要一直按着抓取键就能绕过这个门槛。

#### 2. 禁止冲刺

**体力归零时，按冲刺键没有任何反应。**

这条检查放在**发起冲刺的那一刻**，所以它能覆盖所有让体力归零的途径——爬墙、跳跃、上一次冲刺、摔落……而不只是"被动作扣到 0"的情况。也就是说，只要你爬到体力见底，就别想再靠冲刺逃生了。

同时，体力归零时冲刺次数（Dashes）也会被清成 0，作为视觉提示（角色头发颜色会变化）。

要恢复冲刺有两个办法：

- **落地**——原版机制，站到地面上冲刺次数会自动补满
- **吃冲刺水晶**（dash refill）——体力回满，冲刺也恢复

> ⚠️ **原版机制提醒**：原版 Celeste 里玩家只要**站在地面上**，冲刺次数就会自动补满。所以"体力归零禁冲"这条规则**实际只在空中感受明显**——落地之后一切照常。这是原版行为，本 mod 没有去改它。

---

### 五、伤害系统与体力条

#### 伤害记录（`Damage`）

每一种"永久削减体力上限"的来源都记成一条 `Damage`：

| 属性 | 类型 | 含义 |
|---|---|---|
| `DamageType` | `string` | 伤害类型名，同类伤害会合并到同一条 |
| `Number` | `int` | 这种伤害累计扣掉了多少点上限 |
| `DamageColor` | `Color` | 这种伤害在体力条上显示的颜色 |

```csharp
// 摔落伤害的例子
new Damage {
    DamageType  = "Harm",
    Number      = 5,
    DamageColor = Color.Red,
}
```

**累积规则**（由 `DamageAccumulator.Apply` 负责）：

- 伤害表里**没有**这个类型 → 插入新条目
- 伤害表里**已有**这个类型 → 数值累加到该条目上（不新增条目）

**治疗规则**（由 `DamageAccumulator.Heal` 负责，正好相反）：

- 伤害表里**没有**这个类型 → **什么都不做**
- 伤害表里**已有**这个类型 → 数值减掉，**减到 ≤ 0 就把整条移除**

**设置规则**（由 `DamageAccumulator.Set` 负责，绝对赋值）：

- 伤害表里**已有**这个类型：
  - `Number <= 0` → **移除**该条目
  - 否则 → 把数值**设置**为 `Number`
- 伤害表里**没有**这个类型：
  - `Number <= 0` → **什么都不做**
  - 否则 → **追加到最后面**

> 三种操作的分工：`Apply` 是**累加**、`Heal` 是**减掉**、`Set` 是**赋值**。
>
> `Set` 里 **`0` 和负数都表示"移除"**：伤害表是"扣了多少上限"的账本，
> 数值为 0 意味着这种伤害没造成任何扣减，留一条 0 在表里没有意义
> （还会在体力条上占一个零宽度段）。所以 `<= 0` 一律当作"删除这条记录"。

**体力上限是派生值**，不单独存储：

```
体力上限 = 110 − 伤害表里所有 Number 之和     （下限 0；存活时最小为 1）
```

这样就**不可能出现"伤害表和上限对不上"的状态**——改伤害表就是改上限，两者永远同步。

#### 登记新的伤害类型

颜色统一在 `Source/DamageAccumulator.cs` 的 `DamageColors` 表里登记，避免"类型 → 颜色"散落在各处：

```csharp
public static class DamageColors {
    public const string Harm   = "Harm";     // 摔落
    public const string Hunger = "Hunger";   // 饥饿
    public const string Weight = "Weight";   // 负重

    private static readonly Dictionary<string, Color> Table = new() {
        { Harm,   new Color(0xE0, 0x3A, 0x3A) },   // 红
        { Hunger, new Color(0xF2, 0xC7, 0x44) },   // 黄
        { Weight, new Color(0x8B, 0x5A, 0x2B) },   // 棕
        // 新增类型在这里加两行（一个常量 + 一条颜色）即可
    };
}
```

**当前已登记的类型：**

| 常量 | 字符串值 | 颜色 | 色号 | 来源 |
|---|---|---|---|---|
| `DamageColors.Harm` | `"Harm"` | 🔴 红 | `#E03A3A` | 摔落伤害 |
| `DamageColors.Hunger` | `"Hunger"` | 🟡 黄 | `#F2C744` | 饥饿伤害 |
| `DamageColors.Weight` | `"Weight"` | 🟤 棕 | `#8B5A2B` | 负重伤害 |
| （未登记的任意名字）| — | 🟣 洋红 | `#FF00FF` | 兜底，提示"忘了登记" |

> 💡 `damageType` 是 `string` 而不是 `enum`，所以**编译器不会检查拼写**。
> 尽量用 `DamageColors.XXX` 常量，别手写字符串——写错会被当成一种全新类型。

给玩家施加伤害（比如将来的尖刺、Boss 攻击）：

```csharp
tracker.TakeDamage(DamageColors.Create("Spike", 10));
```

治疗（把某一种伤害从表里减掉，上限相应回升）：

```csharp
// 治掉 10 点摔落伤害。如果表里原本只有 6 点，就只治 6 点、并把该条目移除。
int healed = tracker.Heal(DamageColors.Create(DamageColors.Harm, 10));

// 从没受过这种伤害 → healed 为 0，什么都不发生
tracker.Heal(DamageColors.Create("Spike", 10));
```

> `Heal` 返回**实际治好了多少点**（可能少于请求值，因为该类型伤害本来就不够）。
>
> 注意：治疗只恢复**上限**，不会顺手把当前的体力也涨上去。这和伤害是对称的——
> 伤害会在上限下降时把体力压下来，治疗则不动体力，多出来的余量靠原版落地 / 冲刺水晶回满。

未登记的类型会显示成**洋红色**，方便一眼看出"忘了登记颜色"。

#### 给其他 mod 调用（公开接口）

其他 mod 可以直接引用 `PeakStamina.dll`，通过 **`PeakStaminaInterop`** 调用，不需要了解内部结构：

```csharp
using Celeste.Mod.PeakStamina;

Player player = level.Tracker.GetEntity<Player>();

// 治好 20 点摔落伤害（上限随之回升），返回实际治好的点数
int healed = PeakStaminaInterop.Heal(player, DamageColors.Harm, 20);

// 治饥饿
PeakStaminaInterop.Heal(player, DamageColors.Hunger, 10);

// 把摔落伤害【直接设定】为 20 点（不是累加，也不是减）
PeakStaminaInterop.SetDamage(player, DamageColors.Harm, 20);

// 负数 = 移除该类型的伤害
PeakStaminaInterop.SetDamage(player, "Spike", -1);

// 查询类
float cap        = PeakStaminaInterop.GetStaminaCap(player);            // 当前体力上限
int   harmAmount = PeakStaminaInterop.GetDamageAmount(player, DamageColors.Harm);  // 摔落伤害累计
bool  enabled    = PeakStaminaInterop.IsEnabled();                     // 本 mod 是否启用
```

| 方法 | 说明 |
|---|---|
| `Heal(player, damageType, amount)` | 治掉指定类型的伤害，返回**实际治好**的点数 |
| `SetDamage(player, damageType, amount)` | **绝对设置**该类型伤害，返回设置后的数值 |
| `GetStaminaCap(player)` | 当前体力上限（= 110 − 所有伤害之和）|
| `GetDamageAmount(player, damageType)` | 该类型累计扣掉了多少上限 |
| `IsEnabled()` | 本 mod 当前是否启用（玩家可能在设置里关掉了）|

##### `Heal` 和 `SetDamage` 的区别

这两个容易混，区别在于**是"加减"还是"赋值"**：

| | 原本 25 点 | 调用后 |
|---|---|---|
| `Heal(..., 10)` | 25 | **15**（减掉 10）|
| `SetDamage(..., 10)` | 25 | **10**（设定成 10）|

`SetDamage` 的完整规则：

| 向量里 | `amount` | 行为 |
|---|---|---|
| **已有**该类型 | `<= 0` | **移除**该条目 |
| **已有**该类型 | `> 0` | 把数值**设置**为 `amount` |
| **没有**该类型 | `<= 0` | **什么都不做**（没有东西可移除）|
| **没有**该类型 | `> 0` | **追加到最后面**（位置影响体力条的渲染顺序）|

> 💡 `0` 和负数效果相同，都是"移除"——因为数值为 0 的伤害条目没有意义。

> 💡 `SetDamage` 适合"保证某种状态"的需求，例如"戴着某道具时摔伤恰好为 20 点"——
> 不受之前已经累积了多少的影响。

> **安全性**：所有方法内部都做了空引用检查和启用状态检查。
> 玩家为空、mod 被关掉、伤害表里没有该类型——都会安全返回 `0`，**不会抛异常**，调用方不需要自己判空。
>
> **为什么不用 Everest 的 interop 注册？** Everest 的跨 mod 互操作是**反射驱动的动态机制**，
> 并没有一个强类型的"注册 API"可以调用（`Celeste.dll` / `Celeste.Mod.mm.dll` 里都不存在
> `RegisterAPI` 这类成员）。用普通 `public static` 类，任何 Everest 版本都能用，而且编译期就能检查参数。

#### 体力条

角色脚下会显示一条浮空的体力条（`SubHUD` 层，永远画在地形之上，不会被前景遮挡）。整条**由左到右**渲染：

| 顺序 | 颜色 | 含义 | 长度 |
|---|---|---|---|
| ① | **绿色** | 剩余体力 | 当前体力 ÷ 110 |
| ② | **黑色** | 已消耗的体力 | (当前上限 − 当前体力) ÷ 110 |
| ③④⑤… | **各伤害类型的颜色** | 按伤害表顺序逐段渲染 | 该类型 Number ÷ 110 |

伤害段用的就是上面「登记新的伤害类型」里的颜色表——目前是**红（摔落）/ 黄（饥饿）/ 棕（负重）**。

关键在于**绿 + 黑的总长 = 当前上限**，所以黑段的右端就是"现在的上限位置"，之后接的就是各种永久伤害。

举例（整条 240 屏幕像素）：

| 场景 | 绿 | 黑 | 伤害段 |
|---|---|---|---|
| 满体力、未受伤 | 240px | 0 | — |
| 花掉 30 体力 | 175px | 66px | — |
| 摔伤扣 55 上限、体力满 | 120px | 0 | 红 120px |
| 摔伤扣 55 上限、体力用掉一半 | 60px | 60px | 红 120px |
| 摔伤 55 + 另一种伤害 30 | 55px | 0 | 红 120px + 该类型 65px |
| 体力耗尽（上限仍 110） | 0 | 240px | — |
| 上限被打到最低 1（累积伤害 109） | 2px | 0 | 红色 238px |

调整位置：`Source/StaminaBarEntity.cs`
```csharp
private const float BoxWidth  = 40f;   // 世界单位，×6 后是屏幕像素
private const float BoxHeight = 5f;
private const float Gap       = 3f;    // 距离脚底的间隙
private const float OffsetX   = 0f;    // 位置微调
private const float OffsetY   = 0f;

private static readonly Color RemainingColor = new Color(0x85, 0xD1, 0x45);  // 绿：剩余体力
private static readonly Color ConsumedColor  = new Color(0x1A, 0x1A, 0x1A);  // 黑：已消耗
private static readonly Color OutlineColor   = new Color(0xF2, 0xF2, 0xF2);  // 描边
// 各伤害类型的颜色不在这里，统一登记在 DamageColors（见上）
```

体力条上的**绿 + 黑**部分：黑段的右端就是"当前上限"。所以：

- **黑段变长** → 体力被动作花掉了（落地或吃水晶能回满）
- **伤害段变长** → 上限被永久扣掉了（要死亡或重进关卡才恢复）

---

### 六、设置项

游戏内 **「模组选项 → PeakStamina」** 里有两个开关：

#### 1. 启用（Enabled）

总开关，默认**开**。

关掉之后本 mod 完全不介入：不生成体力条、不检测摔落、不扣体力、不限制动作——手感就是纯原版。

#### 2. Restore Stamina Cap on Room Transition（切换房间时恢复体力上限）

默认**开**。

| 状态 | 行为 |
|---|---|
| **开** | 每次切换房间 / 加载关卡 / 重试，体力上限都恢复成满值 **110** |
| **关** | 上限**跨房间保留**，摔伤的代价一直累积，只有**玩家死亡**时才会恢复 |

关掉之后就变成"一命制"的耐力挑战——一次摔伤会跟着你走完整个关卡，直到死亡为止。

> ⚠️ 关掉这个开关后，摔伤会一直累积，很容易逼近致死阈值（**伤害 ≥ 当前上限 就会死**）。建议配合数值调整一起使用。

设置会自动保存到 `Saves/modsettings-PeakStamina.celeste`。

---

## 安装

1. 确保已安装 [Everest](https://everestapi.github.io/)（Celeste 的 mod 加载器）
2. 把 `PeakStamina` 文件夹放进 `Celeste/Mods/` 目录
3. 启动游戏

（或使用打包好的 `PeakStamina.zip`，直接丢进 `Mods/` 即可）

---

## 从源码编译

需要 **.NET 8 SDK**。

```bash
dotnet build Source/PeakStamina.csproj -c Debug
```

编译产物会自动复制到 `bin/PeakStamina.dll`（Everest 实际读取的位置）。

发布版本（会额外生成 `PeakStamina.zip`）：

```bash
dotnet build Source/PeakStamina.csproj -c Release
```

> 编译时会自动从游戏根目录（`../../..`）读取 `Celeste.dll`、`MMHOOK_Celeste.dll`、`FNA.dll`、`YamlDotNet.dll` 作为引用。如果没装游戏，会退回使用项目自带的 `lib-stripped` 目录。
>
> 其中 `YamlDotNet.dll` 只是为了在 `Damage` 上用 `[YamlIgnore]`（让 `Color` 这类不适合序列化的属性不写进存档）。Everest 本来就依赖它。

---

## 代码结构

| 文件 | 作用 |
|---|---|
| `Source/PeakStaminaModule.cs` | 主入口。注册所有钩子（`LoadLevel` / `Reload` / `Die` / `Jump` / `WallJump` / `StartDash` / `OnCollideV` / `Ducking` 属性） |
| `Source/StaminaTracker.cs` | 核心逻辑。挂在 Player 上的组件：上限管理、摔落检测、动作代价、体力收口、ultra 判定、零体力惩罚 |
| `Source/Damage.cs` | `Damage` 记录：类型 / 数值 / 显示颜色 |
| `Source/DamageAccumulator.cs` | `DamageColors` 类型→颜色注册表 + `DamageAccumulator` 累积与求和 |
| `Source/FallDamageHandler.cs` | 纯计算：摔落像素高度 → 该扣多少上限（按格数） |
| `Source/StaminaBarEntity.cs` | 体力条实体 + 渲染（绿 / 黑 / 各伤害类型分段） |
| `Source/PeakStaminaModuleSession.cs` | 存伤害向量，体力上限由它派生（生命周期 = 一局游戏） |
| `Source/PeakStaminaModuleSettings.cs` | 两个设置项 |
| `Source/PeakStaminaInterop.cs` | 对外公开接口（供其他 mod 调用治疗 / 设置 / 查询）|
| `Source/PeakStaminaModuleSaveData.cs` | 预留（目前为空） |

### 几个关键设计

**为什么 `StaminaTracker` 挂在 `Player` 身上，而不是体力条实体上？**

因为"把体力压到上限以下"这件事必须紧跟玩家的 `Update`。体力条实体是关卡加载后期才被加进场景的，它的 `Update` 排在很后面——如果把收口逻辑放在那里，玩家每帧都能先以超过上限的体力爬一下墙，再被补算，形成"偷取体力"的漏洞。挂在 `Player` 上就能保证同一帧内立刻收口。

**为什么体力条用 `TagsExt.SubHUD`？**

加上这个标签后，Everest 会把它交给 `SubHudRenderer` 渲染，于是：永远画在关卡画面之上（不被地形遮挡）、坐标系变成屏幕像素（1920×1080，以 1080p 窗口为例）、不需要自己算摄像机矩阵。

**为什么重置体力上限要推迟到第一帧？**

因为 Everest 是在关卡构建**之后**才把 Session 存档加载并覆盖上去的。如果在 `LoadLevel` 里就把上限设成 110，紧接着会被存档里的旧值盖掉——这曾是一个真实 bug（表现为"进图体力条不是满的"）。所以现在只立一个标记，真正的重置由 `StaminaTracker.Update` 在第一帧执行。

**为什么动作扣费不能直接读 `player.Stamina`？**

因为跳跃 / 冲刺的按键是在**玩家自己的 `Update` 里**处理的，而那一刻原版可能刚刚调用 `RefillStamina()` 把体力设回满值（最典型的是"落地那一帧"）。直接减就会变成"从体力上限开始扣"。

所以改成两步：

1. **基准值** `staminaAtFrameStart`：在我们自己的 `Update` 里、收口之后记录，永远满足 `体力 ≤ 上限`
2. **体力保持** `ApplyStaminaHold`：扣费后 0.25 秒内，只要体力超过"扣费后该有的值"就压回去

第 2 步是必要的，因为原版的回满既可能发生在扣费**之前**，也可能发生在**之后**（冲刺的启动是协程，回满可能在好几帧后）。只在扣费那一刻压一次挡不住后者——这正是"地上向上冲刺体力不扣"那个 bug 的成因。

所有动作扣费都走同一个 `SpendStaminaFromBaseline()`，避免以后新增动作时又漏掉某一处。

**但是"体力保持"会误伤玩家主动恢复。**

`ApplyStaminaHold` 是个**无差别**的压制：它只看"体力有没有超过目标值"，分不清那份体力是

- 原版自己偷偷回满的（**应该**压制），还是
- 玩家主动吃恢复水晶恢复的（**不该**压制）

结果就是 **冲刺后 0.25 秒内吃到恢复水晶，体力会被压制又按回去**，表现成"冲刺中吃水晶不回体力"。

**修法**：挂 `On.Celeste.Player.UseRefill`，在真的恢复成功时把压制窗口取消掉。

> ⚠️ 必须挂 `UseRefill` 而**不是** `RefillStamina`：
> `RefillStamina()` 只是"把体力设成 110"的底层函数，原版有十几个调用点
> （`Bounce` / `BoostBegin` / `DreamDashEnd` / `StartStarFly` …），
> 那些正是**需要压制的内部回满**，无脑挂钩会把压制机制破坏掉。
> 而 `UseRefill` 全游戏只有一个调用方：`Refill::OnPlayer`，也就是"碰到恢复水晶"。

---

## 已知限制与待查 bug

- **ultra 判定偏宽松**：少数情况下 ultra 失败却仍然免除了伤害。这是设计所允许的，避免玩家受到严苛的惩罚

- **累计伤害达到基础上限 110 时，体力条会画到框外**。
  因为上限的下限是 0，一旦累计伤害到 110，各伤害段长度之和正好等于整条宽度，
  再多就会溢出。实际上玩不到——**致死规则保证累计伤害最多到 109 就会先杀死玩家**，
  只有直接用控制台改存档数据才可能触发。

- **体力条位置按 1080p 窗口写死了缩放系数 6**（1920 ÷ 320）。如果窗口分辨率不同，体力条的位置和大小都会有偏移。要通用的话可以改成按 `Engine.ViewWidth / 320f` 动态计算。

- **`Stamina` 是原版的字段，本 mod 直接读写它**。这意味着它可能和其他修改体力的 mod 冲突。

- **体力为 0 时禁止冲刺，但玩家在平地上仍然可以正常冲刺**（原版落地自动补满冲刺次数，本 mod 没有去改这个机制）。

---

## 兼容性

- 需要 **Everest 1.5935.0** 或更高（见 `everest.yaml`）
- 不依赖任何其他 mod
- 动作代价相关的钩子挂在 `Player.Jump` / `Player.WallJump` / `Player.ClimbJump` / `Player.StartDash` 上，与同样修改这些方法的 mod 可能互相影响
- 恢复水晶相关挂在 `Player.UseRefill` 上（用来取消冲刺期间的体力压制，让水晶能正常回体力）
- ultra 判定用到 `Player.OnCollideV` 和 `Player.Ducking` 属性的 setter（用 `Hook` 挂钩子），与其他监听这些地方（比如 TechAnnouncer）的 mod 共存没有问题

---

## 待办 / 想法

- [ ] 受伤时的反馈（屏幕震动、伤害数字、音效）
- [ ] 体力条位置自适应分辨率
- [ ] 数值平衡测试（当前各项数值都是初值，未做手感调优）
- [ ] 更多动作代价（爬墙、抓取等）
- [ ] 把规则数值做成可配置的设置项（目前写死在代码常量里，需重新编译才能改）
