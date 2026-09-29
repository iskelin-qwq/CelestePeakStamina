using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.PeakStamina;

// ============================================================================
//  体力条实体
//
//  它本身什么都不画，只是挂着一张"我是界面元素"的标签：
//      TagsExt.SubHUD
//  有了这个标签，Everest 会把它交给 SubHudRenderer 渲染，于是：
//    · 永远画在关卡画面之上 —— 再也不会被地形挡住
//    · 坐标系自动变成"屏幕像素"（0,0 = 左上角，右下角 = 1920,1080）
//    · 不需要任何摄像机矩阵运算
// ============================================================================
public class StaminaBarEntity : Entity {
    // 当前关卡的体力上限管理器。
    // 目前渲染用的是 StaminaBarDisplay 里自己那份引用，
    // 这个属性是留着给以后的功能用的（比如做"低体力时闪红"之类的表现）。
    public StaminaTracker Tracker { get; private set; }

    public StaminaBarEntity() {
        Tag = Tags.TransitionUpdate | Tags.PauseUpdate | TagsExt.SubHUD;
    }

    public override void Added(Scene scene) {
        base.Added(scene);

        Level level = scene as Level;
        Player player = level?.Tracker.GetEntity<Player>();

        // 理论上 OnLoadLevel 已经确认过玩家存在，这里再兜一次，
        // 免得万一玩家在 Added 之前就被移除了，后面到处空引用。
        if (player == null) {
            return;
        }

        // 把"体力上限管理 + 摔落检测"挂到玩家自己身上，
        // 这样它的 Update 紧跟玩家的 Update 执行。
        Tracker = new StaminaTracker(player);
        player.Add(Tracker);

        // 再挂上负责画图的组件
        Add(new StaminaBarDisplay(player));
    }
}


// ============================================================================
//  真正负责"算位置 + 绘制"的组件
// ============================================================================
public class StaminaBarDisplay : Component {
    // ------------------------------------------------------------------
    //  ★ 所有可调的数值都在这里，改完重新编译即可
    // ------------------------------------------------------------------

    // 世界 1 单位 = 屏幕多少像素。
    // Celeste 内部按 320×180 运算，窗口是 1920×1080，所以是 6 倍。
    private const float Scale = 6f;

    // 方框尺寸（单位：世界单位，1 单位 = 屏幕 6 像素）
    private const float BoxWidth  = 40f;
    private const float BoxHeight = 5f;
    private const float Gap       = 3f;    // 方框距离锚点的空隙

    // 位置微调（单位：世界单位）。正数 = 往右 / 往下
    private const float OffsetX = 0f;
    private const float OffsetY = 0f;

    // ------------------------------------------------------------------
    //  体力条由左到右分成若干段
    // ------------------------------------------------------------------
    //   ① 绿：剩余体力（当前体力）
    //   ② 黑：已消耗的体力（当前上限 − 当前体力）
    //   ③ 之后每种"伤害类型"一段，颜色由该伤害自己决定，
    //      按伤害向量（Session.Damages）里的顺序从 0 开始依次渲染
    //
    //   绿 + 黑 的总长 = 当前上限，所以黑段的右端就是"现在的上限位置"。
    //   上限 = 110 − 所有伤害之和，所以伤害段越靠后越靠右。

    // 绿段：剩余体力。网页色号 #85D145
    private static readonly Color RemainingColor = new Color(0x85, 0xD1, 0x45);

    // 黑段：已消耗掉的体力（还能靠休息/回满恢复的那部分）
    private static readonly Color ConsumedColor = new Color(0x1A, 0x1A, 0x1A);

    // 整条的外描边。因为黑段在深色场景里几乎和背景融为一体，
    // 没有描边就看不出一条到底有多长、黑段从哪里开始。
    private static readonly Color OutlineColor = new Color(0xF2, 0xF2, 0xF2);

    // ------------------------------------------------------------------

    private readonly Player player;
    private Level level;

    public StaminaBarDisplay(Player player) : base(active: true, visible: true) {
        this.player = player;
    }

    public override void Added(Entity entity) {
        base.Added(entity);

        // 组件挂上去之后场景就已经确定了，缓存一次，免得每帧都去问一遍
        level = SceneAs<Level>();
    }

    public override void Render() {
        if (player == null || level == null) {
            return;
        }

        // 玩家在设置里关掉了本 mod → 不画体力条
        if (!PeakStaminaModule.Settings.Enabled) {
            return;
        }

        // ① 锚点 = 玩家位置（碰撞箱底部中点，也就是脚底）+ 微调
        Vector2 worldAnchor = player.Position + new Vector2(OffsetX, OffsetY);

        // ② 世界坐标 → 屏幕像素
        //    CameraToScreen 做摄像机变换（用的是摄像机当前真实位置，永远同步），
        //    再乘 Scale 映射到 1920×1080 的窗口像素。
        Vector2 screenAnchor = level.Camera.CameraToScreen(worldAnchor) * Scale;

        // ③ 先算好所有像素尺寸，避免下面公式里重复相乘看不清
        float pixelWidth  = BoxWidth  * Scale;
        float pixelHeight = BoxHeight * Scale;
        float left   = screenAnchor.X - pixelWidth / 2f;   // 左边缘（整条条的左端）
        float top    = screenAnchor.Y + Gap * Scale;       // 上边缘

        // ④ 算各段占比。分母统一用基础上限 110，
        //    这样所有段落的长度可以直接相加、永远铺满整条。
        float baseMax = StaminaTracker.BaseMaxStamina;

        float currentStamina = Calc.Clamp(player.Stamina, 0f, baseMax);
        float currentCap     = Calc.Clamp(PeakStaminaModule.Session.StaminaCap, 0f, baseMax);

        // 防御：体力理论上不会超过上限，但万一其他 mod 干扰了，
        // 这里兜一下，避免黑段算出负数、把后面的伤害段推歪。
        currentStamina = Calc.Min(currentStamina, currentCap);

        float greenWidth = pixelWidth * (currentStamina / baseMax);
        float blackWidth = pixelWidth * ((currentCap - currentStamina) / baseMax);

        // ⑤ 从左到右画：绿 → 黑 → 各伤害段
        float x = left;

        if (greenWidth > 0f) {
            Draw.Rect(x, top, greenWidth, pixelHeight, RemainingColor);
            x += greenWidth;
        }

        if (blackWidth > 0f) {
            Draw.Rect(x, top, blackWidth, pixelHeight, ConsumedColor);
            x += blackWidth;
        }

        // ⑥ 按伤害向量的顺序渲染每种伤害。
        //    每种伤害的颜色由它自己携带（Damage.DamageColor），
        //    长度 = 该类型累计的数值。
        //
        //    先补一次颜色：从存档读回来的伤害没有颜色
        //   （颜色不参与序列化），渲染前统一由注册表补上。
        List<Damage> damages = PeakStaminaModule.Session.Damages;
        DamageColors.Apply(damages);

        if (damages != null) {
            foreach (Damage damage in damages) {
                if (damage == null || damage.Number <= 0) {
                    continue;
                }

                float width = pixelWidth * (damage.Number / baseMax);

                if (width <= 0f) {
                    continue;
                }

                Draw.Rect(x, top, width, pixelHeight, damage.DamageColor);
                x += width;
            }
        }

        // ⑦ 给整条画一圈描边。
        //    因为黑段在深色场景里几乎看不见，没有边框的话玩家无法判断
        //    "这条到底有多长、已经消耗掉多少"。
        Draw.HollowRect(left, top, pixelWidth, pixelHeight, OutlineColor);
    }
}
