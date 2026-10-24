using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2AgentBridge;

/// <summary>
/// 语义请求 / 应答。
///
/// 目标：把「游戏正在等玩家做决定」变成一个结构化请求（GET /request），agent 用
/// POST /respond 交答案（只交 key），剩下的「在本地把这次选择演出来」全部由 mod 负责。
///
/// <b>通道一览（按 /request 的优先级）</b>：selector 卡牌选择 → 游戏结束（收尾界面）→ 奖励列表 →
/// 宝箱 → 药水目标 → 商店 → 营火 → 事件 → 推进（战斗结束等奖励的空档）→ 菜单
/// （主菜单 / 单人子菜单 / 角色选择 / 确认弹窗）。每条通道都是
/// 「一个 pending + 一套 key 校验 + 一个包裹层应答 + 失败可重答」。
///
/// 只有下面两条是「读界面」的路，其中**只有第 2 条是 fallback**：
///
/// 1. <b>各房间/界面通道</b>（奖励、宝箱、药水、商店、营火、事件、菜单）：候选全部来自
///    游戏**模型或官方节点自己的属性**，应答调的是游戏自己的处理方法（<c>ForceClick()</c> /
///    私有 <c>OnSelected()</c> / 同步器入口 …），不点坐标。正常流程走这些。
///
/// 2. <b>screen 通道（fallback，仅调试/紧急回退）</b>：扫界面 + 模拟点击那条老路，只在
///    <c>STS2_AGENT_SELECTOR=0</c> 或代理模式（<see cref="AgentUi.AgentMode"/>）关闭时才用。
///    它不碰 CardSelectCmd，让游戏自己弹出原生选牌界面 / 进入原生手牌选择模式，然后我们去
///    「观察界面」并把 agent 的答案变成真实点击。这条路的点击有 uid 过期、点空的已知 bug，
///    **不要**默认走；它返回的请求/应答都带 <c>"fallback": true</c>，方便一眼认出。
///
/// 两条卡牌通道共用同一套 id / 候选 key（标题#同标题序号）/ 校验 / 返回字段。
///
/// screen 通道的「正在选牌」有三种发现方式（见 <see cref="Capture"/>）：
///   * 战斗中的手牌选择（FromHand / FromHandForUpgrade / FromHandForDiscard）：
///     游戏不弹浮层，而是让 NPlayerHand 进入 SimpleSelect / UpgradeSelect 模式，
///     选择规则在这个节点自己的 private <c>_prefs</c> 里。判定：CurrentMode ∈
///     {SimpleSelect, UpgradeSelect}，或者选牌遮板 <c>_selectModeBackstop</c> 可见。
///   * 浮层选牌界面（NCardGridSelectionScreen 及其派生，必须沿继承链判断）：
///     选择规则在派生类自己的 private <c>_prefs</c> 字段里。
///   * 战斗奖励三选一（NCardRewardSelectionScreen）：没有 Prefs，按单选处理。
/// 都发现不了时的最后手段见 <see cref="TryGuessHandSelection"/>（confidence=low）。
/// </summary>
public static partial class RequestApi
{
    private const BindingFlags AnyMember =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// 各派生选牌界面（以及 NPlayerHand）都把选择规则放在一个 private 字段里，官方字段名是
    /// <c>_prefs</c>（NDeckUpgradeSelectScreen / NDeckCardSelectScreen / NDeckTransformSelectScreen /
    /// NDeckEnchantSelectScreen / NSimpleCardSelectScreen / NCombatPileCardSelectScreen /
    /// NPlayerHand 各自声明一份，基类 NCardGridSelectionScreen 上没有），所以必须沿继承链找，
    /// 属性名也一起试。
    /// </summary>
    private static readonly string[] PrefsMemberNames =
    {
        "_prefs", "Prefs", "prefs", "SelectorPrefs", "CardSelectorPrefs", "_cardSelectorPrefs",
    };

    /// <summary>
    /// 界面自己的确认键（NConfirmButton 没有文字，靠文本启发式常常找不到，只能靠这些字段）。
    /// 最后那个是手牌选择模式的确认键，挂在 NPlayerHand 上。
    /// </summary>
    private static readonly string[] ConfirmMemberNames =
    {
        "_confirmButton", "_previewConfirmButton", "_multiPreviewConfirmButton",
        "_singlePreviewConfirmButton", "_selectModeConfirmButton",
    };

    /// <summary>装候选牌的容器字段：网格界面用 <c>_grid</c>，二选一用 <c>_cardRow</c>。</summary>
    private static readonly string[] CardContainerMemberNames =
    {
        "_grid", "_cardRow", "_bundleRow",
    };

    /// <summary>两次点击之间的间隔：短了动画来不及播（选中的牌还没飞出去就点下一张），长了太慢。</summary>
    private const int ClickGapMs = 520;

    /// <summary>轮询状态的间隔。</summary>
    private const int PollMs = 300;

    // ---- mod 端动画（selector 通道；游戏不弹界面，只能自己补） ----
    /// <summary>
    /// 抬起停留时间：选中的手牌先在原位抬起来（目标位置上移 120px）+ 放大（HoverScale）+ 摆平（角度 0），
    /// 牌组/网格退化成金色高亮 + tween 轻微抬起；停这么久让玩家看清「就是这张」，
    /// 然后结果才落地（这一刻游戏才真正升级）。
    /// </summary>
    private const int AnimSelectMs = 520;

    /// <summary>升级之后、复位抬起姿态之前再停这么久：让「它真的升级了」看得见。</summary>
    private const int AnimTailMs = 260;

    /// <summary>收尾之后高亮再留多久才淡出（淡出本身不阻塞 /respond）。</summary>
    private const int AnimGlowHoldMs = 450;

    /// <summary>给 /request 的 animated=true 配的说明：这次动画具体做什么。</summary>
    private const string AnimationPlan =
        "应答时：先把选中的牌悬停抬起（手牌：SetTargetPosition(当前位置 + (0,-120)) + SetTargetScale(HoverScale) + SetTargetAngle(0)；"
        + "牌组/网格退化成金色高亮 + tween 轻微抬起），停 520ms 再让游戏真正升级，"
        + "升级后再停 260ms 才收尾（SetDefaultTargets 复位 + Flash + UpdateCard，高亮淡出）";

    // ---- 删牌动画（selector 通道，只给 kind=remove / caller=FromDeckForRemoval） ----
    /// <summary>
    /// 那张牌在牌库里亮起来之后停留多久：500~700ms，够眼睛看清「选中了哪一张」。
    /// </summary>
    private const int RemoveHoldMs = 620;

    /// <summary>
    /// 等牌库网格把那张牌摆出来最多等这么久。牌库界面是异步初始化的
    /// （SetCards → InitGrid 是 async），刚 ShowScreen 时 GetCardHolder 还可能是 null。
    /// </summary>
    private const int RemoveGridWaitMs = 1100;

    /// <summary>整套删牌动画的时间预算；超时就收尾（关界面 + 交答案），返回里如实标记。</summary>
    private const int RemoveBudgetMs = 2500;

    /// <summary>轮询「那张牌在牌库里出现了没有」的间隔。</summary>
    private const int RemovePollMs = 70;

    /// <summary>从总预算里先留出来给「关牌库界面」的一段，避免亮了牌却来不及关。</summary>
    private const int RemoveCloseReserveMs = 450;

    /// <summary>删牌动画真走完（打开 → 点亮 → 关闭都成了）时的 visual。</summary>
    private const string VisualRemoveDeckAnim = "remove-deck-anim";

    /// <summary>删牌动画没走完（打不开牌库 / 牌库里找不到这张 / 关不掉）时如实降级的 visual。</summary>
    private const string VisualRemoveDelay = "remove-delay";

    /// <summary>给 /request 的 animation 字段配的删牌版说明。</summary>
    private const string RemoveAnimationPlan =
        "删牌：用游戏自己的 NDeckViewScreen.ShowScreen(player) 打开牌库界面 → 在 NCardGrid 里按 CardModel 找到那张牌，"
        + "调 NCardGrid.HighlightCard()（内部就是 NCardHighlight.AnimShow）+ tween 轻微抬起 → 停 620ms → "
        + "NCapstoneContainer.Close()（= 牌库的返回按钮）关掉界面 → 再把答案交回游戏（这一刻牌才真的被删）";

    // ---- 奖励通道（NRewardsScreen） ----
    /// <summary>奖励请求的 prompt：一条说清「可以都拿，也可以一项都不拿」。</summary>
    private const string RewardPrompt = "战斗奖励：选择要领取的项目（可以都拿，也可以不要）";

    /// <summary>每领一项之间停这么久：让奖励项自己的动画（遗物飞进背包 / 药水飞进槽位）看得见。</summary>
    private const int RewardGapMs = 320;

    /// <summary>
    /// 单项领取最多等这么久。卡牌奖励的领取会一直挂在 <c>NCardRewardSelectionScreen.OptionSelected()</c>
    /// 上等玩家选牌，**不能**在这里等到底（否则 /respond 永不返回、agent 也看不到选牌请求），
    /// 超时就当「已经受理、下一步是选牌」交回给 /request。
    /// </summary>
    private const int RewardSettleMs = 1500;

    /// <summary>
    /// 卡片奖励三选一可以「放弃」时拼在 prompt 后面的提示。拼的是我们自己的一句，
    /// 不动的还是游戏自己的横幅文字（banner），所以 promptFallback 的语义不变。
    /// </summary>
    private const string CardRewardSkipHint = "（也可以放弃，不选）";

    /// <summary>「放弃这张奖励牌」这条路在返回里用的 applied 文案。</summary>
    private const string CardRewardSkipApplied = "（放弃这张牌）";

    /// <summary>
    /// 游戏给「跳过这张卡」用的替代项 OptionId（<c>CardRewardAlternative.Generate</c> 里
    /// <c>new CardRewardAlternative("Skip", EndSelectionAndDoNotCompleteReward)</c>）。
    /// 重 roll 那个是 "REROLL"，别混。
    /// </summary>
    private const string CardRewardSkipOptionId = "Skip";

    /// <summary>给 /request 的 animation 字段配的说明。</summary>
    private const string RewardAnimationPlan =
        "应答时逐项调用奖励项自己的领取入口（NRewardButton.GetReward / Reward.SelectUnsynchronized），"
        + "每项间隔 320ms；遗物会飞进遗物栏、药水会飞进药水槽、按钮自己会从列表里消失，"
        + "全部应用完才返回 ok（卡牌奖励会接着弹三选一，那一步是下一个 request）";

    // ---- 商店通道（NMerchantInventory） ----
    /// <summary>商店请求的 prompt：一条说清「可以都买、也可以一件都不买」。</summary>
    private const string ShopPrompt = "商店：选择要买的东西（中途也可以不买）";

    /// <summary>
    /// 每买一件之间停这么久：商店格子自己会播「买走 / 售罄」表现（由
    /// <c>MerchantEntry.PurchaseCompleted</c> 信号驱动界面），留出拍子才看得见。
    /// </summary>
    private const int ShopGapMs = 340;

    /// <summary>
    /// 单项购买最多等这么久。买「卡牌移除服务」会挂在
    /// <c>CardSelectCmd.FromDeckForRemoval</c> 上等玩家选牌，**不能**在这里等到底
    /// （否则 /respond 永不返回、agent 也看不到选牌请求），超时就当「已经受理、下一步是选牌」。
    /// </summary>
    private const int ShopSettleMs = 1500;

    /// <summary>真买成（或游戏已受理、只是还在等下一步选牌）时，items[] 里的 visual。</summary>
    private const string ShopVisualBought = "shop-bought";

    /// <summary>
    /// **没买成**（item.ok=false）时 items[] 里的 visual。以前这里沿用 shop-bought，
    /// 药水槽满这种失败也会谎报「已经买到了」。现在失败一律用这个值，只有真买成才写
    /// <see cref="ShopVisualBought"/> / "shop-model-delay" / "card-choice"。
    /// </summary>
    private const string ShopVisualNone = "shop-none";

    /// <summary>给 /request 的 animation 字段配的说明。</summary>
    private const string ShopAnimationPlan =
        "应答时逐项调用格子自己的点击处理方法（NMerchantSlot.OnSelected → Entry.OnTryPurchaseWrapper），"
        + "每件间隔 340ms；格子会自己播买走/售罄表现，商家会说话；"
        + "全部应用完才返回 ok（卡牌移除服务会接着弹选牌，那一步是下一个 request）";

    // ---- 商店通道 · 阶段 1（面板还没打开：给 agent 选择权，不硬猜） ----
    /// <summary>
    /// <c>pending.stage</c>：商店面板还没打开。这时候只问「要不要打开」，**不**暴露商品，
    /// 也不替 agent 买东西（开着面板时只有你能推进，但它先得知道有这条路）。
    /// </summary>
    private const string ShopStageClosed = "closed";

    /// <summary><c>pending.stage</c>：面板已打开、商品摆出来了（原来的商店请求，行为不变）。</summary>
    private const string ShopStageItems = "items";

    /// <summary>
    /// 阶段 1 的 prompt：一条说清「可以打开、也可以直接用药水」，并点明只有污浊药水能对商人用
    /// （商店里其它药水游戏自己就不让用，见 <c>PotionUsage</c>）。
    /// </summary>
    private const string ShopOpenPrompt =
        "商店：要打开看看吗（打开前也可以直接用药水；商店里只有污浊药水能对商人用，打开面板后就来不及了）";

    /// <summary>阶段 1 唯一候选的标题（key 就是「标题#1」）。</summary>
    private const string ShopOpenTitle = "打开商店";

    /// <summary>阶段 1 的 key：固定一个 —— 「打开」这个动作跟店里卖什么无关。</summary>
    private const string ShopOpenKey = ShopOpenTitle + "#1";

    /// <summary>
    /// 开店最多等这么久让 <c>NMerchantInventory.IsOpen</c> 变 true。
    /// <c>Open()</c> 里先 <c>RunSafely(DoOpenAnimation())</c>（不 await）、再 <c>set_IsOpen(true)</c>，
    /// 所以正常是一两步就 true，3 秒只是兜底。
    /// </summary>
    private const int ShopOpenMs = 3000;

    /// <summary>轮询 IsOpen 的间隔。</summary>
    private const int ShopOpenPollMs = 100;

    /// <summary>阶段 1 应答「打开」成功时的 visual。</summary>
    private const string ShopVisualOpened = "shop-opened";

    /// <summary>阶段 1 应答 <c>picks:[]</c>（不打开）时的 visual。</summary>
    private const string ShopVisualOpenSkipped = "shop-not-opened";

    /// <summary>阶段 1 给 /request 的 animation 字段配的说明。</summary>
    private const string ShopOpenAnimationPlan =
        "阶段 1：应答「打开商店」时走游戏自己的开面板入口 —— 房间节点的 OpenInventory()"
        + "（= 点商人按钮：NMerchantButton.OnRelease → EmitSignalMerchantOpened → "
        + "NMerchantRoom/NFakeMerchant.OnMerchantOpened → OpenInventory：先 Disable「前进」按钮 → "
        + "NMerchantInventory.Open()（里面 RunSafely(DoOpenAnimation) + 欢迎音效 + set_IsOpen(true)）→ "
        + "Disable 商人按钮 → 连上 InventoryClosed 再恢复），不点坐标；"
        + "然后轮询 IsOpen 变 true（最多 3s）。这一步**不买任何东西**。";

    // ---- 药水目标通道（/potion/use 在非战斗场景建出来的） ----
    /// <summary><c>pending.stage</c>：药水在等 agent 挑一个目标。</summary>
    private const string PotionStageTarget = "target";

    /// <summary>药水请求的 kind。</summary>
    private const string PotionKind = "potion";

    /// <summary>商人目标候选的标题（key 就是「标题#1」）。</summary>
    private const string PotionMerchantTitle = "商人";

    /// <summary>商人目标候选的 key。</summary>
    private const string PotionMerchantKey = PotionMerchantTitle + "#1";

    /// <summary>「取消」候选的标题 / key：选中它等于这次不喝。</summary>
    private const string PotionCancelTitle = "取消";
    private const string PotionCancelKey = PotionCancelTitle + "#1";

    /// <summary>药水真喝下去时的 visual。</summary>
    private const string PotionVisualUsed = "potion-used";

    /// <summary>agent 取消（picks:[] 或选「取消」）时的 visual：药水留在槽里。</summary>
    private const string PotionVisualCanceled = "potion-canceled";

    /// <summary>药水目标请求给 /request 的 animation 字段配的说明。</summary>
    private const string PotionTargetAnimationPlan =
        "应答时按 agent 给的目标调 PotionModel.EnqueueManualUse（= 界面「扔出去」按钮最后那一步）"
        + "：战斗里带 enemy 下标对应的 Creature，非战斗里带 null（TargetedNoCreature 的商人本来就没有 "
        + "Creature，游戏自己在 FoulPotion.OnUse 里认商人按钮）；然后轮询药水槽，那一瓶真的消失才返回 ok。";

    // ---- 宝箱通道（NTreasureRoom + TreasureRoomRelicSynchronizer） ----
    /// <summary>
    /// 宝箱是**两阶段**的，一条 prompt 只描述当前这一阶段能做什么：
    ///   阶段 1（<c>chest-closed</c>，箱子还关着）= 只问「要不要打开」，**不提前暴露箱里的东西**；
    ///   阶段 2（<c>relics</c>，箱子已开、遗物格子摆出来了）= 问「要拿走哪件」，picks:[] = 都不拿。
    /// </summary>
    private const string TreasureOpenPrompt = "宝箱：要打开吗";

    /// <summary>阶段 2 的 prompt：遗物已经摆出来，可以拿一件，也可以不拿（直接离开）。</summary>
    private const string TreasurePrompt = "宝箱：要拿走哪件（picks:[] = 都不拿，直接离开）";

    /// <summary>阶段 1 唯一候选的标题（key 就是「标题#1」）。</summary>
    private const string TreasureOpenTitle = "打开宝箱";

    /// <summary>阶段 1 的 key：固定一个 —— 「打开」这个动作跟箱子里的内容无关。</summary>
    private const string TreasureOpenKey = TreasureOpenTitle + "#1";

    /// <summary><c>pending.stage</c>：箱子还关着（阶段 1）。</summary>
    private const string TreasureStageChestClosed = "chest-closed";

    /// <summary><c>pending.stage</c>：箱子已开、遗物摆出来了（阶段 2）。</summary>
    private const string TreasureStageRelics = "relics";

    /// <summary>
    /// 开箱最多等这么久。<c>NTreasureRoom.OpenChest()</c> 在 <c>InitializeRelics()</c> +
    /// <c>AnimIn()</c> 之后才把 <c>_isRelicCollectionOpen</c> 置 true，这之前遗物格子还没摆好，
    /// 格子自己的 <c>PickRelic</c> 也认不出下标。
    /// </summary>
    private const int TreasureOpenMs = 3000;

    /// <summary>
    /// 遗物真正进遗物栏最多等这么久：<c>RelicsAwarded</c> → <c>AnimateRelicAwards</c> →
    /// <c>RelicCmd.Obtain</c>，中间夹着好几段 <c>Cmd.Wait</c>（那几段必须让动画播完）。
    /// </summary>
    private const int TreasureSettleMs = 2600;

    /// <summary>开箱到位之后、真正领取之前留的拍子（箱子开盖 / 金光 / 遗物格子淡入）。</summary>
    private const int TreasureOpenGapMs = 340;

    /// <summary>拿到之后、核对之前留的拍子：遗物飞进遗物栏（NRelicInventory.AnimateRelic）在播。</summary>
    private const int TreasureGapMs = 420;

    /// <summary>走遗物格子自己的领取入口（有飞入表现）时的 visual。</summary>
    private const string TreasureVisualTaken = "treasure-taken";

    /// <summary>只走了模型层（PickRelicLocally）时的 visual：动画照样由 RelicsAwarded 驱动，但不是格子那条路。</summary>
    private const string TreasureVisualModelDelay = "treasure-model-delay";

    /// <summary><c>picks:[]</c>（阶段 2 不拿）时的 visual。</summary>
    private const string TreasureVisualSkipped = "treasure-skipped";

    /// <summary>阶段 1 应答「打开」成功时的 visual。</summary>
    private const string TreasureVisualChestOpened = "chest-opened";

    /// <summary>阶段 1 应答 <c>picks:[]</c>（不打开）时的 visual。</summary>
    private const string TreasureVisualChestSkipped = "chest-skipped";

    /// <summary>候选里的 visual 占位（没拿到时用）。</summary>
    private const string TreasureVisualNone = "treasure-none";

    /// <summary>
    /// 阶段 2「都不拿」之后最多等这么久让 <c>_singleplayerSkipped</c> 变 true。
    /// <c>SkipRelicLocally()</c> 只是 <c>ActionQueueSynchronizer.RequestEnqueue(PickRelicAction(null))</c>，
    /// 真正置位发生在排队执行到的 <c>OnPicked(player, null)</c> 里，不是同步返回。
    /// </summary>
    private const int TreasureSkipSettleMs = 1500;

    /// <summary>
    /// 阶段 2「都不拿」之后最多等这么久让「前进」按钮亮起来：单人开箱时
    /// <c>OpenChest</c> 会 <c>EnableSkipAfterDelay(2.5f)</c>，2.5 秒后按钮才 <c>Enable()</c>。
    /// </summary>
    private const int TreasureProceedReadyMs = 3500;

    /// <summary>阶段 1 给 /request 的 animation 字段配的说明。</summary>
    private const string TreasureOpenAnimationPlan =
        "阶段 1：应答「打开宝箱」时用游戏自己的开箱入口 NTreasureRoom.OpenChest()（= 点宝箱："
        + "banner 滑入 + 关「前进」+ 开盖音效 + 金光粒子 + DoNormalRewards 的金币 + "
        + "InitializeRelics/AnimIn 把遗物摆出来），**不 await 它**（它内部 await RelicPickingBegan() "
        + "要等玩家拿走遗物才结束），只轮询 _isRelicCollectionOpen 变 true（最多 3s）。"
        + "这一步不拿任何遗物：遗物摆出来之后再 GET /request 才会给出 stage:\"relics\" 的内容。";

    /// <summary>阶段 2 给 /request 的 animation 字段配的说明。</summary>
    private const string TreasureAnimationPlan =
        "阶段 2：应答时调遗物格子自己的领取入口 NTreasureRoomRelicCollection.PickRelic(holder)"
        + "（= 松开鼠标那一下：TreasureRoomRelicSynchronizer.PickRelicLocally(holder.Index) → AwardRelics → "
        + "RelicsAwarded → RelicCmd.Obtain + NRelicInventory.AnimateRelic 把遗物飞进遗物栏），不点坐标；"
        + "然后等遗物真的进了遗物栏才返回 ok。picks:[] = 都不拿：调游戏自己的跳过入口 "
        + "TreasureRoomRelicSynchronizer.SkipRelicLocally()（= 点「跳过」时 OnProceedButtonPressed 里的第一句），"
        + "等 _singleplayerSkipped 落地、「前进」按钮亮起来，之后用 /proceed 离开宝箱房。";

    // ---- 营火通道（NRestSiteRoom + RestSiteRoom.Options） ----
    /// <summary>
    /// 营火请求的 prompt：一条说清「只能选一项」和「离开不归这里管」。
    /// </summary>
    private const string RestPrompt = "营火：选择一项（离开营火用 /proceed，不通过 /respond）";

    /// <summary>
    /// 单项选择最多等这么久。「锻造」会挂在 <c>CardSelectCmd.FromDeckForUpgrade</c> 上等玩家选牌，
    /// **不能**在这里等到底（否则 /respond 永不返回、agent 也看不到选牌请求），
    /// 超时就当「已经受理、下一步是选牌」交回给 /request。
    /// </summary>
    private const int RestSettleMs = 1200;

    /// <summary>选完之后、核对之前留的拍子：选项自己的 PostSelectVfx（火焰/铁砧/铲子）在播。</summary>
    private const int RestGapMs = 420;

    /// <summary>
    /// 走按钮自己的选中方法（<c>NRestSiteButton.SelectOption</c> → <c>AfterSelectingOption</c>：
    /// 隐藏选项 → 选项自己的 PostSelectVfx → 亮出「前进」）时的 visual。
    /// </summary>
    private const string RestVisualApplied = "rest-applied";

    /// <summary>候选里的 visual 占位（没应用成功时用）。</summary>
    private const string RestVisualNone = "rest-none";

    /// <summary>给 /request 的 animation 字段配的说明。</summary>
    private const string RestAnimationPlan =
        "应答时调营火按钮自己的选中方法 NRestSiteButton.SelectOption(option)（= 玩家点一下："
        + "DisableOptions → RefreshTextState → RunManager.RestSiteSynchronizer.ChooseLocalOption(index)"
        + " → 成功后 NRestSiteRoom.AfterSelectingOption：隐藏选项 + 选项自己的 PostSelectVfx + 亮出「前进」），"
        + "不点坐标；拿不到按钮时退化成 ChooseLocalOption + 补一次 AfterSelectingOption。"
        + "选完停 420ms 再核对（选项列表 / HP / 牌组升级张数 / 遗物数 / 「前进」是否亮起）。";

    // ---- 事件通道（NEventRoom + EventModel.CurrentOptions） ----
    /// <summary>事件 prompt 的兜底（读不到事件标题/描述时用）。</summary>
    private const string EventPromptFallback = "事件：选择一个选项";

    /// <summary>
    /// 单项选择最多等这么久：事件选项经常先弹卡牌选择（升级/移除/变形），
    /// 那一步是**下一个** request，不能在这里等到底。
    /// </summary>
    private const int EventSettleMs = 1200;

    /// <summary>选完之后、核对之前留的拍子：事件页切换 / 卡牌飞出的动画。</summary>
    private const int EventGapMs = 420;

    /// <summary>走 <c>NEventRoom.OptionButtonClicked</c> 时的 visual。</summary>
    private const string EventVisualApplied = "event-applied";

    /// <summary>候选里的 visual 占位（没应用成功时用）。</summary>
    private const string EventVisualNone = "event-none";

    /// <summary>给 /request 的 animation 字段配的说明。</summary>
    private const string EventAnimationPlan =
        "应答时调 NEventRoom.OptionButtonClicked(option, index)（= 点选项按钮：非共享事件先 ClearOptions，"
        + "再 RunManager.EventSynchronizer.ChooseLocalOption(index) → 那个选项的 Chosen()），"
        + "index 是选项在 EventModel.CurrentOptions 里的下标；IsProceed 的选项走 option.Chosen()（= 离开事件）。"
        + "不点坐标；选完停 420ms 再核对（选项集合 / 事件描述 / IsFinished / 是否进了内嵌战斗）。";

    // ---- 共用 ----
    private static readonly object Gate = new();
    private static long _nextId;

    // ---- screen 通道 ----
    private static long _currentId;
    private static string _currentSignature;

    // ---- selector 通道 ----
    private static readonly object SelectorGate = new();

    /// <summary>栈：最后一个是当前最新的请求。前后嵌套两层选择时只允许回答最上面那个。</summary>
    private static readonly List<PendingSelection> Pending = new();

    private static object _selectorScope;
    private static string _selectorState = "new";        // new / active / unavailable / disabled
    private static string _selectorDetail = "";
    private static long _lastInstallAttempt;
    private static long _lastAnsweredId;

    /// <summary>
    /// selector 通道的应答动画正在播（约 0.8 秒）。这期间游戏也处在「等玩家」状态，
    /// 界面扫描会误以为还有一次手牌选择，所以 /request 直接回 pending:null、/respond 直接拒。
    /// </summary>
    private static volatile bool _animating;

    /// <summary>本次应答动画的现场：主线程写、主线程读。OnMain 等超时了也要能靠它把牌收回去。</summary>
    private static List<CardVisual> _activeVisuals;

    /// <summary>本次动画已经收尾过了：万一 Begin 被主线程队列拖到收尾之后才跑，就不要再动界面。</summary>
    private static bool _animationClosed;

    // ---- 奖励通道（NRewardsScreen） ----
    /// <summary>奖励通道的待答请求。单槽：游戏同一时刻只可能开着一个奖励界面。</summary>
    private static PendingSelection _rewardsPending;

    /// <summary>
    /// 已经发出领取、但游戏还没把 <c>Reward.SuccessfullySelected</c> 置位的奖励（按引用比）。
    /// 典型是卡牌奖励：选择挂在「等玩家选牌」上，标记要等选完才翻 —— 这段时间不能再把它当候选发一遍，
    /// 否则 agent 会看到重复项、重复提交还会把同一个奖励领两次。
    /// </summary>
    private static readonly List<Reward> RewardDispatched = new();

    /// <summary>当前奖励界面的身份（界面节点 + RewardsSet）；换了就把上面的「已发出」记录清掉。</summary>
    private static string _rewardsScope;

    /// <summary>RewardDispatched 的锁：/request 在主线程写，/respond 在 HTTP 线程读。</summary>
    private static readonly object RewardGate = new();

    // ---- 商店通道（NMerchantInventory） ----
    private static PendingSelection _shopPending;

    /// <summary>「面板加载了但没打开」这个状态只记一次日志，避免 /request 每轮刷屏。</summary>
    private static ulong _shopClosedLoggedFor;

    // ---- 药水目标通道（/potion/use 建出来的） ----
    /// <summary>
    /// 药水的目标请求。单槽：同一时刻只会有一瓶在等目标（agent 一次只会丢一瓶）。
    /// 它排在高优先级通道（选牌 / 奖励 / 宝箱）之后、商店之前 —— 因为它就是在商店/事件现场产生的。
    /// </summary>
    private static PendingSelection _potionPending;

    // ---- 宝箱通道（NTreasureRoom + TreasureRoomRelicSynchronizer） ----
    /// <summary>宝箱通道的待答请求。单槽：游戏同一时刻只可能开着一间宝箱房。</summary>
    private static PendingSelection _treasurePending;

    // ---- 营火通道（NRestSiteRoom） ----
    /// <summary>营火通道的待答请求。单槽：游戏同一时刻只可能开着一间营火。</summary>
    private static PendingSelection _restPending;

    // ---- 事件通道（NEventRoom） ----
    /// <summary>事件通道的待答请求。单槽：游戏同一时刻只可能开着一个事件。</summary>
    private static PendingSelection _eventPending;

    // ---- 菜单通道（NMainMenu / NSingleplayerSubmenu / NCharacterSelectScreen） ----
    /// <summary>菜单通道的待答请求。单槽：游戏同一时刻只可能停在一个菜单页上。</summary>
    private static PendingSelection _menuPending;

    private sealed class Candidate
    {
        public string Key;
        public string Title;
        public bool Upgraded;
        public int Cost;
        public CardModel Card;
        public Control Node;      // screen / hand / 奖励通道才有（奖励通道里是 NRewardButton）
        public ulong Uid;         // screen / hand 通道才有

        // ---- 奖励通道专用（卡牌候选上都是 null/空） ----
        public Reward RewardRef;  // 这一项对应的 MegaCrit.Sts2.Core.Rewards.Reward
        public string RewardType; // RewardType 名：Gold / Potion / Relic / Card / SpecialCard / RemoveCard
        public string Detail;     // 奖励自己的 Description（人话），只作说明，不参与校验

        // ---- 商店通道专用 ----
        public MerchantEntry Entry;  // 这一格对应的 MerchantEntry 模型
        public NMerchantSlot Slot;   // 这一格对应的界面节点（只用来调它自己的处理方法，不点坐标）
        public int Price;            // MerchantEntry.Cost（游戏里的真实价格）
        public bool Affordable;      // Player.Gold >= Price，由 mod 算
        // ---- 稳定 id（中文标题会抄错，靠这个兜底）----
        // 例如商店条目用 slot:3；respond 同时接受 Key 和 Stable。
        public string Stable;

        public string ItemType;      // Card / Relic / Potion / Service
        public bool ShopOpenAction;  // 阶段 1 的「打开商店」动作候选（不是商品）

        // ---- 药水目标通道专用（其余候选上都是 null/默认值）----
        public bool PotionCancel;                                 // 「取消#1」
        public bool PotionMerchant;                               // 这个目标是商人（不是 Creature）
        public MegaCrit.Sts2.Core.Entities.Creatures.Creature TargetCreature;  // 联机里的 Creature 目标

        // ---- 营火通道专用（其余候选上都是 null/默认值） ----
        public RestSiteOption RestOption;   // 这一项对应的 RestSiteOption 模型
        public NRestSiteButton RestButton;  // 这一项对应的界面按钮（只用来调它自己的选中方法）
        public string OptionId;             // 营火的 OptionId / 事件的 TextKey
        public bool Enabled = true;         // 模型自己的 IsEnabled / !IsLocked

        // ---- 事件通道专用 ----
        public EventOption EventOptionRef;        // 这一项对应的 EventOption 模型
        public NEventOptionButton EventButton;    // 这一项对应的界面按钮（只用来做自己的反馈）
        public int EventIndex = -1;               // 在 EventModel.CurrentOptions 里的下标
        public bool IsProceed;                    //「继续/离开」而不是普通分支
        public bool WillKill;                     // EventOption.WillKillPlayer(本地玩家) 的结果

        // ---- 宝箱通道专用（其余候选上都是 null / 默认值） ----
        public RelicModel RelicRef;        // 宝箱里这一件遗物（TreasureRoomRelicSynchronizer.CurrentRelics[i]）
        public int TreasureIndex = -1;     // 在 CurrentRelics 里的下标（= 遗物格子的 Index）
        public bool TreasureAction;        // 阶段 1 的「打开宝箱」动作候选（不是遗物，TreasureIndex = -1）

        // ---- 菜单通道专用（其余候选上都是 null / 默认值） ----
        public NClickableControl MenuButton;      // 主菜单 / 子菜单按钮 / 收尾界面的推进按钮（只调它自己的 ForceClick）
        public NCharacterSelectButton MenuCharacterButton;  // 角色选择页的角色卡
        public CharacterModel MenuCharacter;      // 角色卡背后的模型（Title / Id）
        public bool MenuRandom;                   // NCharacterSelectButton.IsRandom
        public bool MenuLocked;                   // NCharacterSelectButton.IsLocked
        public string MenuId;                     // 角色 Id 末尾名字（ironclad / silent / … / random）
        public string MenuStage;                  // 这个候选属于哪个菜单阶段（main / mode / submenu / character / confirm）
        public int MenuAscension = -1;            // 进阶阶段：这个候选对应的 Ascension 等级（0~max；其它阶段恒为 -1）

        // ---- 选牌包（bundle）通道专用 ----
        /// <summary>
        /// 这个候选对应的那一捆牌（<c>NCardBundle</c>）。key / title / detail 都由它的
        /// <c>Bundle</c>（组内 CardModel 列表）算出来，应答时**只按这个引用**定位那一捆。
        /// </summary>
        public MegaCrit.Sts2.Core.Nodes.Cards.NCardBundle BundleRef;

        // ---- 游戏结束通道专用 ——
        /// <summary>
        /// 界面上不给文字的按钮（例如收尾界面里那个「空按钮」）：靠**类型 + 相对位置**认出来，
        /// 标题只能退化成节点名。带这个标记的候选在返回里会多一个 <c>emptyButton: true</c>。
        /// </summary>
        public bool EmptyButton;

        // ---- 占卜通道专用 ----
        public NClickableControl DivinationButton;  // 占卜格 / 大小幅占卜按钮（只调它自己的 ForceClick）
        public bool DivinationIsTool;               // 这是「切换工具」的候选，不是格子
        public bool? DivinationRevealed;            // 格子：雾揭开没有（读不到就是 null，不发这个字段）
        public string DivinationContentType;        // 格子：**已揭示**时里面是什么（Relic / Gold / …），否则 null
    }

    private sealed class PendingSelection
    {
        public long Id;
        public int Min;
        public int Max;
        public string Kind = "simple";
        public string Caller;
        public List<Candidate> Cards = new();
        public TaskCompletionSource<IEnumerable<CardModel>> Completion;
        public DateTime CreatedUtc = DateTime.UtcNow;

        // ---- 奖励通道专用 ----
        public string Prompt;      // 空 → 用 DefaultPrompt(kind)
        public string Signature;   // 奖励通道：界面+奖励集合+候选的指纹，变了就换 id
        public Node Screen;        // NRewardsScreen（商店通道里是 NMerchantInventory）
        public RewardsSet RewardSet;

        // ---- 商店通道专用 ----
        public int ShopGold;       // 这次请求发出时的金币（/request 的 pending.gold）
        public string ShopStage;   // closed（面板没打开）/ items（面板已打开、商品已列出），也进签名

        // ---- 药水目标通道专用 ----
        public string PotionKey;             // 这一瓶的稳定 key（标题#同标题序号）
        public string PotionTitle;
        public string PotionDescription;
        public bool PotionMerchantTarget;    // 目标是不是商人（应答时用来复查目标还在不在）

        // ---- 宝箱通道专用 ----
        public bool TreasureChestOpened;   // 发请求时宝箱是否已经开过（/request 的 pending.chestOpened）
        public string TreasureStage;       // chest-closed（阶段 1）/ relics（阶段 2），也进签名

        // ---- 菜单通道专用 ----
        public string MenuStage;           // main / mode / submenu / character / confirm，也进签名

        // ---- 游戏结束通道（NGameOverScreen）专用 ----
        public string GameOverStage;       // page（收尾主页）/ summary（结算动画没播完），也进签名

        // ---- 推进通道（kind=proceed）专用 ----
        public string ProceedStage;        // 目前只有 combat-end，也进签名

        // ---- 占卜通道专用（水晶球小游戏）----
        public Node DivinationScreen;      // NCrystalSphereScreen
        public string DivinationKind;      // "cell"（翻格子）/ "tool"（切大小幅占卜）
        public int? DivinationLeft;        // 小游戏自己的 DivinationCount（读不到就是 null，不发这个字段）
    }

    /// <summary>
    /// 一张被选中的牌在界面上的「可亮 / 可抬起」目标：selector 通道的候选没有节点引用，
    /// 只能在 /respond 的那一刻再去界面树里按 CardModel 找回来（找不到就是 null）。
    /// </summary>
    private sealed class CardVisual
    {
        public Candidate Candidate;
        public Node Holder;                                    // NCardHolder
        public MegaCrit.Sts2.Core.Nodes.Cards.NCardHighlight Highlight;  // 有就点亮它

        // ---- 悬停抬起（手牌：原地抬高 + 放大，不动游戏的选中容器） ----
        public bool Lifted;
        public Vector2 SavedPosition;
        public Vector2 SavedScale;
    }

    private sealed class Snapshot
    {
        public Node Screen;             // 浮层界面节点，或者手牌节点
        public Node Hand;               // 只有手牌通道有
        public string Route = "grid";   // grid / reward / hand / hand-guess / selector
        public string Kind = "simple";
        public string Prompt = "";
        public int Min = 1;
        public int Max = 1;
        public bool HasPrefs;
        public bool RequireConfirm;
        public bool Cancelable;

        /// <summary>这条请求能用 <c>picks:[]</c> 放弃（目前只有卡片奖励三选一有）。</summary>
        public bool CanSkip;
        public bool Degraded;
        public string DegradedReason;
        public bool PromptFallback;
        public string Confidence = "high";
        public int AlreadySelected;
        public List<Candidate> Candidates = new();
    }

    /// <summary>
    /// 菜单通道的一帧现场：当前停在哪个菜单阶段、这一页有哪些可选项、已经选中了什么。
    /// 全部在主线程读（<see cref="ReadMenuView"/>），不缓存任何节点引用以外的状态。
    /// </summary>
    private sealed class MenuView
    {
        public string Stage;                    // main / mode / submenu / character
        public string Prompt;
        public string Caller;                   // 节点类型名，进 pending.caller
        public Node Screen;                     // 这一页的界面节点（角色阶段要用它读 _embarkButton）
        public string Selected;                 // 角色阶段：已经选中的那个 key（没有就是 null）
        public List<Candidate> Candidates = new();
    }

    /// <summary>「按下之后页面变了没有」的观测结果：<c>Now == null</c> 表示菜单整个消失（进了别的场景）。</summary>
    private sealed class MenuChange
    {
        public bool Changed;
        public MenuView Now;
    }

    // =================================================================== selector 通道（默认）

    /// <summary>
    /// 默认接管（不需要任何环境变量）。要关掉时启动游戏前显式设
    /// <c>STS2_AGENT_SELECTOR=0</c>（也认 off / false / no / disable / disabled），
    /// 关掉之后退回界面扫描通道 —— 那条路有 uid 过期、点空的已知 bug，只作为保留通道。
    /// </summary>
    private static bool SelectorRequested()
    {
        // 只有「代理模式」开着才接管卡牌选择。玩家手动玩的时候必须让游戏弹它自己的界面，
        // 否则点了按钮会觉得没反应（钱照扣、界面不出来，全是 selector 截走了）。
        if (!AgentUi.AgentMode) return false;

        var env = System.Environment.GetEnvironmentVariable("STS2_AGENT_SELECTOR");
        if (string.IsNullOrWhiteSpace(env)) return true;          // 代理模式下默认接管
        env = env.Trim();
        return !(env == "0"
                 || env.Equals("off", StringComparison.OrdinalIgnoreCase)
                 || env.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || env.Equals("no", StringComparison.OrdinalIgnoreCase)
                 || env.Equals("disable", StringComparison.OrdinalIgnoreCase)
                 || env.Equals("disabled", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把自己从 CardSelectCmd 的栈上摘掉（代理模式关掉时用），让游戏恢复弹自己的选牌界面。</summary>
    private static void UninstallSelector()
    {
        try
        {
            if (MegaCrit.Sts2.Core.Commands.CardSelectCmd.Selector is not Selector) return;
            MegaCrit.Sts2.Core.Commands.CardSelectCmd.Reset();
            BridgeLog.Info("selector detached: 代理模式关闭，游戏恢复自己的选牌界面");
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"selector detach 失败（{e.GetType().Name}）：{e.Message}");
        }
    }

    /// <summary>
    /// 把自己挂到 CardSelectCmd 上，接管卡牌选择（默认就装）。装不上（或被别人占用）就保持界面扫描的
    /// fallback，绝不抛异常。可以反复调用：真的还挂在栈上才免装。
    /// </summary>
    public static void InstallSelector()
    {
        lock (SelectorGate)
        {
            // 不要因为「上一次是 disabled」就永久放弃：代理模式是后加载的，
            // 启动瞬间读到 false 会把 selector 永久挡在门外（玩家一开代理却不生效）。
            // 每次 Tick 都重新判定一次，状态只在真变化时才写日志。
            _lastInstallAttempt = System.Environment.TickCount64;

            if (!SelectorRequested())
            {
                // 代理模式关掉（或环境变量禁用）：正在挂着就把自己摘下来，
                // 让游戏恢复弹自己的选牌界面——玩家手动操作必须看得见界面。
                UninstallSelector();
                SetState("disabled",
                    AgentUi.AgentMode
                        ? "STS2_AGENT_SELECTOR 关了 selector 接管，退回界面扫描通道（仅供调试）"
                        : "代理模式未开启：不接管卡牌选择，游戏弹自己的界面");
                return;
            }

            try
            {
                var existing = MegaCrit.Sts2.Core.Commands.CardSelectCmd.Selector;
                if (existing is Selector)
                {
                    SetState("active", "已经装好了（selector 栈顶就是本 mod）");
                    return;
                }
                if (existing != null)
                {
                    // 别的 mod 装了：不抢，退回界面扫描。
                    SetState("unavailable", "CardSelectCmd 上已经挂着别的 ICardSelector：" + existing.GetType().FullName);
                    return;
                }

                string testMode;
                try { testMode = MegaCrit.Sts2.Core.TestSupport.TestMode.IsOn ? "on" : "off"; }
                catch { testMode = "unknown"; }

                _selectorScope = MegaCrit.Sts2.Core.Commands.CardSelectCmd.UseSelector(new Selector());

                var live = MegaCrit.Sts2.Core.Commands.CardSelectCmd.Selector is Selector;
                if (live)
                    SetState("active", $"已接管（TestMode={testMode}）：界面不弹，/respond 时由 mod 播本地动画");
                else
                    SetState("unavailable", $"UseSelector 没报错但 Selector 不是我们（TestMode={testMode}），退回界面扫描");
            }
            catch (Exception e)
            {
                SetState("unavailable", e.GetType().Name + ": " + e.Message);
            }
        }
    }

    /// <summary>状态只在变化时写日志，避免每 2 秒刷一条。</summary>
    private static void SetState(string state, string detail)
    {
        var changed = _selectorState != state || _selectorDetail != detail;
        _selectorState = state;
        _selectorDetail = detail;
        if (changed) BridgeLog.Info("selector: " + state + " — " + detail);
    }

    /// <summary>
    /// 每帧由 BridgePump 调一次：selector 会被 <c>CardSelectCmd.Reset()</c>（RunManager.CleanUp，
    /// 一局结束）清掉，所以「active」也不能当成永久状态 —— 2 秒探一次 selector 栈，掉了就重装。
    /// 只有被 <c>STS2_AGENT_SELECTOR=0</c> 关掉（disabled）时才连探都不探。
    /// </summary>
    public static void Tick()
    {
        // 不再因为 disabled 就永久停摆：代理模式可能后开（开关在主菜单/状态里），
        // 这里每次都重新判定，由 InstallSelector 自己决定装/摘。
        if (System.Environment.TickCount64 - _lastInstallAttempt < 2000) return;
        InstallSelector();
    }

    /// <summary>真正的 hook：CardSelectCmd 把候选牌和 min/max 交到这里，我们等 /respond。</summary>
    private sealed class Selector : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        Task<IEnumerable<CardModel>> MegaCrit.Sts2.Core.TestSupport.ICardSelector.GetSelectedCards(
            IEnumerable<CardModel> cards, int min, int max)
            => BeginCardRequest(cards, min, max);

        /// <summary>
        /// 奖励三选一这条路（CardReward.OnSelect）只在「奖励界面没被显示」时才会问 selector，
        /// 本地单人一定显示了界面，所以这里和 vanilla 里 Selector==null 的分支保持一致：
        /// 交回 default，不要自己替玩家挑奖励。
        /// </summary>
        MegaCrit.Sts2.Core.TestSupport.CardRewardSelection
            MegaCrit.Sts2.Core.TestSupport.ICardSelector.GetSelectedCardReward(
                System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult> options,
                System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative> alternatives)
            => default;
    }

    /// <summary>把一次「选牌」登记成 pending 请求，返回一个等 /respond 的 Task。</summary>
    private static Task<IEnumerable<CardModel>> BeginCardRequest(IEnumerable<CardModel> cards, int min, int max)
    {
        var models = new List<CardModel>();
        try
        {
            if (cards != null)
                foreach (var card in cards) if (card != null) models.Add(card);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("selector: enumerate cards failed: " + e.Message);
        }

        if (models.Count == 0)
            return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());

        var (kind, caller) = DetectKind();

        // min/max 一定要夹进候选数里，否则「min 比候选还多」会让任何回答都过不了校验，把游戏卡死。
        var wantMin = Math.Clamp(min, 0, models.Count);
        var wantMax = max <= 0 ? models.Count : Math.Min(max, models.Count);
        if (wantMax < wantMin) wantMax = wantMin;

        var pending = new PendingSelection
        {
            Id = NextId(),
            Min = wantMin,
            Max = wantMax,
            Kind = kind,
            Caller = caller,
            Cards = ToCandidates(models),
            Completion = new TaskCompletionSource<IEnumerable<CardModel>>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };

        lock (SelectorGate) Pending.Add(pending);

        BridgeLog.Info($"selector request id={pending.Id} kind={pending.Kind} caller={caller} " +
                       $"min={pending.Min} max={pending.Max} cards={pending.Cards.Count}");
        try
        {
            // 鲸鱼气泡只说「人话」，不报 id/数量/接口名——那些只在 BridgeLog 里给后台看。
            // 这里不发声：等 /respond 落地时再用 BalloonFor 说一句角色口吻的话。
        }
        catch { }

        return pending.Completion.Task;
    }

    private static PendingSelection TopPending()
    {
        lock (SelectorGate) return Pending.Count > 0 ? Pending[^1] : null;
    }

    /// <summary>
    /// selector 通道现在有没有挂着待答的选牌请求（只读，不动队列）。
    /// 给 <see cref="Batch"/> 用：/acts 里「回合没推进」时要能分清「游戏在等选牌」和
    /// 「战斗已经结束」，这两种都不是失败。
    /// </summary>
    internal static bool HasPendingCardRequest() => TopPending() != null;

    /// <summary>
    /// 当前挂着待答的选牌请求 id（没有就是 null）。给 <see cref="Batch"/> 用：
    /// /acts 里撞上选牌时要能把 pendingId 一起交回给 agent，它好直接 GET /request 对 id。
    /// </summary>
    internal static long? PendingCardRequestId() => TopPending()?.Id;

    private static Dictionary<string, object> DescribePending(PendingSelection pending)
    {
        var dict = new Dictionary<string, object>
        {
            ["id"] = pending.Id,
            ["kind"] = pending.Kind,
            ["prompt"] = !string.IsNullOrEmpty(pending.Prompt) && pending.DivinationScreen != null
                ? pending.Prompt
                : DefaultPrompt(pending.Kind),
            ["min"] = pending.Min,
            ["max"] = pending.Max,
            ["requireConfirm"] = false,
            ["cancelable"] = pending.Min <= 0,
            ["caller"] = pending.Caller,
            ["confidence"] = "high",     // 游戏直接把候选和 min/max 交给我们，不存在读错的可能
            ["animated"] = true,         // /respond 时先把牌悬停抬起、停 520ms，然后才真升级
            ["animation"] = IsDeckRemovalRequest(pending) ? RemoveAnimationPlan : AnimationPlan,
            ["route"] = pending.DivinationScreen != null ? "divination" : "selector",
            ["candidates"] = pending.Cards.Select(CandidateJson).ToList(),
        };

        // 新增字段（不改既有字段）：这条请求会在游戏自己的牌库界面里演一遍「打开 → 点亮 → 关闭」。
        if (IsDeckRemovalRequest(pending)) dict["deckAnimation"] = true;

        // selector 通道里 kind="reward" 的是「卡牌奖励」那条（CardSelectCmd.FromSimpleGridForRewards）。
        // 跟奖励列表请求（kind 也是 reward）用 stage 区分：这里同样是「选牌」这一步。
        if (string.Equals(pending.Kind, "reward", StringComparison.Ordinal))
            dict["stage"] = "card-choice";

        // 占卜通道：小游戏自己还剩几次（CrystalSphereMinigame.DivinationCount）。
        // 读不到就不发这个字段——宁可没有，也不给一个编出来的数字。
        if (pending.DivinationScreen != null && pending.DivinationLeft.HasValue)
            dict["divinationsLeft"] = pending.DivinationLeft.Value;

        return dict;
    }

    /// <summary>selector 通道的应答：校验通过 → 先播 mod 端动画 → 再把那几张 CardModel 交回游戏。</summary>
    private static async Task<Dictionary<string, object>> AnswerPending(PendingSelection pending, string[] picks)
    {
        var failure = Validate(pending.Min, pending.Max, pending.Cards, picks, out var chosen);
        if (failure != null) return failure;

        // 先从栈里摘掉：动画在等的时候不能再被 /request 看到，也不能被答第二次。
        lock (SelectorGate) Pending.Remove(pending);
        _lastAnsweredId = pending.Id;
        _animating = true;

        try
        {
            var applied = chosen.Count == 0 ? "（跳过）" : string.Join("、", chosen.Select(c => c.Key));
            var sw = Stopwatch.StartNew();

            // ---- 0) 删牌专用：在游戏自己的牌库界面里演「打开 → 点亮这张 → 停一下 → 关闭」，
            //         关上之后才把答案交回游戏（这一刻牌才真的从牌组里消失）。----
            if (IsDeckRemovalRequest(pending) && chosen.Count > 0)
            {
                // 动画再怎么样都不能挡住「把答案交回游戏」：抛了就降级，还是要 TrySetResult，
                // 否则游戏会一直卡在等这张牌上。
                DeckRemovalStage deck;
                try { deck = await PlayDeckRemovalAnimation(chosen); }
                catch (Exception e)
                {
                    LogAnimError("删牌动画", e);
                    deck = new DeckRemovalStage { Why = "删牌动画抛异常：" + e.Message };
                }

                if (!pending.Completion.TrySetResult(chosen.Select(c => c.Card).ToList()))
                    return Fail("这个请求已经被游戏取消了");

                // 交回之后停一拍，让游戏自己的删除结算落地（界面上是看不到的，牌库已经关上了）。
                await Task.Delay(AnimTailMs);

                var waitedDeck = sw.ElapsedMilliseconds;
                var deckOk = deck.Opened && deck.Highlighted && deck.Closed;
                var deckVisual = deckOk ? VisualRemoveDeckAnim : VisualRemoveDelay;

                BridgeLog.Info($"selector answered id={pending.Id} picks={applied} visual={deckVisual} " +
                               $"opened={deck.Opened} highlighted={deck.Highlighted} closed={deck.Closed} " +
                               $"waitedMs={waitedDeck}");

                var deckAnim = new Dictionary<string, object>
                {
                    ["screen"] = deck.ScreenName ?? "",
                    ["opened"] = deck.Opened,
                    ["highlighted"] = deck.Highlighted,
                    ["closed"] = deck.Closed,
                    ["waitedMs"] = deck.WaitedMs,
                    ["budgetMs"] = RemoveBudgetMs,
                };
                if (!string.IsNullOrEmpty(deck.Why)) deckAnim["note"] = deck.Why;

                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["source"] = "selector",
                    ["route"] = "selector",
                    ["applied"] = applied,
                    ["how"] = "selector",
                    ["animated"] = true,
                    ["waitedMs"] = waitedDeck,
                    // remove-deck-anim：牌库真打开了、真点亮了那张、真关上了；
                    // remove-delay：没走完，如实降级（原因在 deckAnim.note）。
                    ["visual"] = deckVisual,
                    ["highlighted"] = deck.Highlighted ? 1 : 0,
                    ["selected"] = 0,
                    ["lifted"] = deck.Visual != null && deck.Visual.Lifted ? 1 : 0,
                    ["deckAnim"] = deckAnim,
                };
            }

            // ---- 1) 先让牌抬起（手牌原地抬高 + 放大 / 网格金色高亮 + tween），停 AnimSelectMs ----
            var visuals = new List<CardVisual>();
            _animationClosed = false;
            try
            {
                visuals = await BridgePump.OnMain(() => BeginAnswerAnimation(pending, chosen), 5000)
                          ?? new List<CardVisual>();
            }
            catch (Exception e)
            {
                BridgeLog.Warn($"animation: 抬起动画失败（{e.GetType().Name}）：{e.Message}");
            }
            // 主线程可能已经把牌抬起来了，只是这次等超时：以主线程写下的现场为准，否则牌会一直悬在半空。
            if (_activeVisuals != null) visuals = _activeVisuals;

            LogVisuals(applied, visuals);

            await Task.Delay(AnimSelectMs);

            // ---- 2) 结果落地：把答案交回游戏，游戏自己接着播升级/移除的结算 ----
            if (!pending.Completion.TrySetResult(chosen.Select(c => c.Card).ToList()))
            {
                await EndAnimationOrIgnore(visuals);   // 游戏已经不等了：动画白演，把抬起的牌放回去
                return Fail("这个请求已经被游戏取消了");
            }

            // ---- 3) 升级后停 AnimTailMs，再把抬起姿态收掉（SetDefaultTargets + Flash + UpdateCard），最后才返回 ok ----
            // 抬起张数必须在收尾之前取：RestoreLift 会把 Lifted 复位成 false，
            // 放到后面统计会永远是 0（visual 就会误报成 highlight-then-upgrade）。
            var liftedCount = visuals.Count(v => v.Lifted);
            var litCount = visuals.Count(v => v.Highlight != null);
            await Task.Delay(AnimTailMs);
            await EndAnimationOrIgnore(visuals);

            var waited = sw.ElapsedMilliseconds;
            _ = FadeHighlights(visuals, AnimGlowHoldMs);

            var lifted = liftedCount;      // 真抬起：手牌抬高 + 放大（网格是 tween 轻微抬起）
            var lit = litCount;
            var visual = lifted > 0
                ? "lift-then-upgrade"                        // 抬起 → 停 520ms → 升级 → 停 260ms → 复位 + 闪光
                : lit > 0
                    ? "highlight-then-upgrade"               // 抬不起来：只剩金色高亮
                    : "whale+delay";                         // 界面树上根本没这张牌，只有气泡 + 延迟
            BridgeLog.Info($"selector answered id={pending.Id} picks={applied} " +
                           $"visual={visual} selected=0 lifted={lifted} lit={lit} of {chosen.Count} waitedMs={waited}");

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = "selector",
                ["route"] = "selector",
                ["applied"] = applied,
                ["how"] = "selector",
                ["animated"] = true,
                ["waitedMs"] = waited,
                ["visual"] = visual,        // lift-then-upgrade / highlight-then-upgrade / whale+delay
                ["highlighted"] = lit,      // 点亮了卡牌高亮的张数
                ["selected"] = 0,           // 不再往 NSelectedHandCardContainer 里塞，恒为 0（字段保留兼容）
                ["lifted"] = lifted,        // 真被抬起来（手牌 SetTargetPosition/Scale/Angle）的张数
            };
        }
        finally
        {
            // 动画结束（含异常路径）：恢复 /request、/respond 的正常判断。
            _animating = false;
            _activeVisuals = null;
        }
    }

    /// <summary>
    /// 从调用栈反推这次是哪种选择：CardSelectCmd 的每个 From* 都是 async 状态机，
    /// 帧上的声明类型长成 <c>CardSelectCmd+&lt;FromDeckForUpgrade&gt;d__18</c>。
    /// 只是给 kind 用，认不出来就当 simple，不影响正确性。
    /// </summary>
    private static (string Kind, string Caller) DetectKind()
    {
        string caller = null;
        try
        {
            var frames = new StackTrace(false).GetFrames();
            if (frames != null)
            {
                foreach (var frame in frames)
                {
                    var type = frame?.GetMethod()?.DeclaringType?.Name;
                    if (string.IsNullOrEmpty(type)) continue;
                    var start = type.IndexOf("<From", StringComparison.Ordinal);
                    if (start < 0) continue;
                    var name = type.Substring(start + 1);
                    var end = name.IndexOf('>');
                    if (end > 0) name = name.Substring(0, end);
                    caller = name;
                    break;
                }
            }
        }
        catch { }

        switch (caller)
        {
            case "FromDeckForUpgrade":
            case "FromHandForUpgrade":
                return ("upgrade", caller);
            case "FromDeckForRemoval":
                return ("remove", caller);
            case "FromDeckForTransformation":
                return ("transform", caller);
            case "FromDeckForEnchantment":
                return ("enchant", caller);
            case "FromHandForDiscard":
                return ("discard", caller);
            case "FromSimpleGridForRewards":
                return ("reward", caller);
            default:
                return ("simple", caller);
        }
    }

    private static string DefaultPrompt(string kind) => kind switch
    {
        "upgrade" => "选择一张牌升级",
        "remove" => "选择一张牌移除",
        "transform" => "选择一张牌变化",
        "enchant" => "选择一张牌附魔",
        "exhaust" => "选择一张牌消耗",
        "discard" => "选择一张牌弃置",
        "reward" => "选择一张牌加入牌组",
        _ => "选择一张牌",
    };

    // =================================================================== 公共入口

    /// <summary>
    /// GET /request：有等待中的决定就返回 {source, pending:{...}}，否则 {source, pending:null}。
    /// selector 通道优先（默认就是它）；被 STS2_AGENT_SELECTOR=0 关掉时才观察当前界面 / 手牌。
    /// </summary>
    public static Dictionary<string, object> Current()
    {
        var pending = TopPending();
        if (pending != null)
            return new Dictionary<string, object>
            {
                ["source"] = "selector",
                ["pending"] = DescribePending(pending),
                ["selector"] = SelectorInfo(),
            };

        // 正在播应答动画：这段时间游戏也在「等玩家」，千万别把界面扫描的猜测当成新请求发出去。
        if (_animating)
            return new Dictionary<string, object>
            {
                ["source"] = "selector",
                ["pending"] = null,
                ["animating"] = true,
                ["selector"] = SelectorInfo(),
            };

        // 游戏结束（死亡 / 胜利）那块收尾界面是模态浮层，优先级仅次于 selector：
        // 它一在场，别的通道看到的都是「过期现场」。
        var gameOverPending = DescribeGameOverPending();
        if (gameOverPending != null)
        {
            DropRewardsPending("游戏结束界面在场，先答收尾");
            DropDivinationPending("游戏结束界面在场，先答收尾");
            DropTreasurePending("游戏结束界面在场，先答收尾");
            DropPotionPending("游戏结束界面在场，先答收尾");
            DropShopPending("游戏结束界面在场，先答收尾");
            DropRestPending("游戏结束界面在场，先答收尾");
            DropEventPending("游戏结束界面在场，先答收尾");
            DropProceedPending("游戏结束界面在场，先答收尾");
            DropMenuPending("游戏结束界面在场，先答收尾");
            DropBundlePending("游戏结束界面在场，先答收尾");
            return new Dictionary<string, object>
            {
                ["source"] = "screen",
                ["pending"] = gameOverPending,
                ["selector"] = SelectorInfo(),
            };
        }

        // 选牌包（NChooseABundleSelectionScreen）：`CardSelectCmd.FromChooseABundleScreen` 不经过
        // ICardSelector（IL 核实：它直接 ShowScreen + await CardsSelected），所以 selector 通道接不住它，
        // 只能像奖励/宝箱那样单独读界面。它是模态界面，排在 gameover 之后、其它通道之前。
        var bundlePending = DescribeBundlePending();
        if (bundlePending != null)
        {
            DropRewardsPending("选牌包界面在场，先答选牌包");
            DropDivinationPending("选牌包界面在场，先答选牌包");
            DropTreasurePending("选牌包界面在场，先答选牌包");
            DropPotionPending("选牌包界面在场，先答选牌包");
            DropShopPending("选牌包界面在场，先答选牌包");
            DropRestPending("选牌包界面在场，先答选牌包");
            DropEventPending("选牌包界面在场，先答选牌包");
            DropProceedPending("选牌包界面在场，先答选牌包");
            DropMenuPending("选牌包界面在场，先答选牌包");
            DropGameOverPending("选牌包界面在场，先答选牌包");
            return new Dictionary<string, object>
            {
                ["source"] = "screen",
                ["pending"] = bundlePending,
                ["selector"] = SelectorInfo(),
            };
        }

        Snapshot snap;
        try { snap = Capture(); }
        catch (Exception e)
        {
            BridgeLog.Warn("request capture failed: " + e.Message);
            return new Dictionary<string, object>
            {
                ["source"] = "screen",
                ["pending"] = null,
                ["selector"] = SelectorInfo(),
                ["error"] = e.Message,
            };
        }

        if (snap == null)
        {
            // 没有「选牌 / 手牌选择 / 奖励三选一」这类界面时，再看战斗奖励列表（NRewardsScreen）。
            // 它比这些界面低一层：卡牌奖励三选一弹在奖励列表上面，必须先答三选一。
            var rewardsPending = DescribeRewardsPending();
            if (rewardsPending != null)
            {
                // 奖励列表和商店不会同时在场；真同时在场时以奖励为先，商店那份作废（重开会有新 id）。
                DropShopPending("奖励列表在场，先答奖励");
                DropTreasurePending("奖励列表在场，先答奖励");
                DropRestPending("奖励列表在场，先答奖励");
                DropEventPending("奖励列表在场，先答奖励");
                DropMenuPending("奖励列表在场，先答奖励");
                DropGameOverPending("奖励列表在场，先答奖励");
                DropProceedPending("奖励列表在场，先答奖励");
                return new Dictionary<string, object>
                {
                    ["source"] = "screen",
                    ["pending"] = rewardsPending,
                    ["selector"] = SelectorInfo(),
                };
            }

            // 水晶球占卜（NCrystalSphereScreen）：模态小游戏，优先级高于事件选项——
            // 事件选项这时已经被用掉了，再把 INITIAL 选项发出去就是过期请求。
            var divinationPending = DescribeDivinationPending();
            if (divinationPending != null)
            {
                DropShopPending("占卜界面在场，先答占卜");
                DropTreasurePending("占卜界面在场，先答占卜");
                DropRestPending("占卜界面在场，先答占卜");
                DropEventPending("占卜界面在场，先答占卜");
                DropMenuPending("占卜界面在场，先答占卜");
                DropGameOverPending("占卜界面在场，先答占卜");
                DropProceedPending("占卜界面在场，先答占卜");
                return new Dictionary<string, object>
                {
                    ["source"] = "screen",
                    ["pending"] = divinationPending,
                    ["selector"] = SelectorInfo(),
                };
            }

            // 奖励列表也没有：看宝箱房（NTreasureRoom）。它排在商店之前、奖励之后：
            // 宝箱里只有「拿不拿这件遗物」一个决定；拿到之后可能马上接着弹选牌 / 结算，
            // 那些是更高优先级的请求（下一次 /request 会先给出来）。
            var treasurePending = DescribeTreasurePending();
            if (treasurePending != null)
            {
                DropShopPending("宝箱在场，先答宝箱");
                DropRestPending("宝箱在场，先答宝箱");
                DropEventPending("宝箱在场，先答宝箱");
                DropMenuPending("宝箱在场，先答宝箱");
                DropGameOverPending("宝箱在场，先答宝箱");
                DropProceedPending("宝箱在场，先答宝箱");
                return new Dictionary<string, object>
                {
                    ["source"] = "screen",
                    ["pending"] = treasurePending,
                    ["selector"] = SelectorInfo(),
                };
            }

            // 药水目标请求（/potion/use 在非战斗场景里建出来的）：排在商店前面 ——
            // 它就是在商店/假商人现场产生的（污浊药水打商人），比「买不买东西」更急。
            var potionPending = DescribePotionPending();
            if (potionPending != null)
            {
                DropShopPending("药水目标请求优先，先答药水");
                DropRestPending("药水目标请求优先，先答药水");
                DropEventPending("药水目标请求优先，先答药水");
                DropMenuPending("药水目标请求优先，先答药水");
                DropGameOverPending("药水目标请求优先，先答药水");
                DropProceedPending("药水目标请求优先，先答药水");
                return new Dictionary<string, object>
                {
                    ["source"] = "screen",
                    ["pending"] = potionPending,
                    ["selector"] = SelectorInfo(),
                };
            }

            // 奖励列表也没有：看商店（NMerchantInventory）。它比上面这些都低一层：
            // 商店里买「卡牌移除服务」会接着弹选牌，那一步是更高优先级的请求。
            var shopPending = DescribeShopPending();
            if (shopPending != null)
            {
                DropTreasurePending("商店在场，先答商店");
                DropRestPending("商店在场，先答商店");
                DropEventPending("商店在场，先答商店");
                DropMenuPending("商店在场，先答商店");
                DropGameOverPending("商店在场，先答商店");
                DropProceedPending("商店在场，先答商店");
                return new Dictionary<string, object>
                {
                    ["source"] = "screen",
                    ["pending"] = shopPending,
                    ["selector"] = SelectorInfo(),
                };
            }

            // 商店也没有：看营火（NRestSiteRoom）。营火里选「锻造」会接着弹选牌，
            // 那一步是更高优先级的请求（下一次 /request 会先给出选牌）。
            var restPending = DescribeRestPending();
            if (restPending != null)
            {
                DropEventPending("营火在场，先答营火");
                DropMenuPending("营火在场，先答营火");
                DropGameOverPending("营火在场，先答营火");
                DropProceedPending("营火在场，先答营火");
                return new Dictionary<string, object>
                {
                    ["source"] = "screen",
                    ["pending"] = restPending,
                    ["selector"] = SelectorInfo(),
                };
            }

            // 最后是事件（NEventRoom）：事件选项也经常先弹选牌，同样由下一次 /request 接手。
            var eventPending = DescribeEventPending();
            if (eventPending != null)
            {
                DropMenuPending("事件在场，先答事件");
                DropGameOverPending("事件在场，先答事件");
                DropProceedPending("事件在场，先答事件");
                return new Dictionary<string, object>
                {
                    ["source"] = "screen",
                    ["pending"] = eventPending,
                    ["selector"] = SelectorInfo(),
                };
            }

            // 战斗打完了、奖励界面还没出现的空档：给一条「推进」请求（Boss 奖励就卡在这一步）。
            // 排在事件之后、菜单之前：前面那些界面在场时它们才是真正的决策点。
            var proceedPending = DescribeProceedPending();
            if (proceedPending != null)
            {
                DropMenuPending("战斗结束等推进，先答推进");
                return new Dictionary<string, object>
                {
                    ["source"] = "screen",
                    ["pending"] = proceedPending,
                    ["selector"] = SelectorInfo(),
                };
            }

            // 事件也没有：最后看菜单（主菜单 / 单人子菜单 / 角色选择 / 其它子菜单）。
            // 只有「不在对局里、而且真的停在某一页菜单上」时才会给出请求。
            var menuPending = DescribeMenuPending();
            if (menuPending != null)
                return new Dictionary<string, object>
                {
                    ["source"] = "screen",
                    ["pending"] = menuPending,
                    ["selector"] = SelectorInfo(),
                };

            // 界面没了：清掉签名，这样下次再开出同样的界面会拿到新的 id。
            lock (Gate) _currentSignature = null;
            return new Dictionary<string, object>
            {
                ["source"] = _selectorState == "active" ? "selector" : "screen",
                ["pending"] = null,
                ["selector"] = SelectorInfo(),
            };
        }

        // 已经有更高优先级的请求（选牌 / 手牌 / 奖励三选一）：奖励列表和商店这两份先让位，
        // 否则同一时刻会有两个未决请求。已经发出去的领取记录要留着（见 RewardDispatched）。
        DropRewardsPending("被 route=" + snap.Route + " 的请求顶掉");
        DropShopPending("被 route=" + snap.Route + " 的请求顶掉");
        DropTreasurePending("被 route=" + snap.Route + " 的请求顶掉");
        DropRestPending("被 route=" + snap.Route + " 的请求顶掉");
        DropEventPending("被 route=" + snap.Route + " 的请求顶掉");
        DropPotionPending("被 route=" + snap.Route + " 的请求顶掉");
        DropMenuPending("被 route=" + snap.Route + " 的请求顶掉");
        DropGameOverPending("被 route=" + snap.Route + " 的请求顶掉");
        DropProceedPending("被 route=" + snap.Route + " 的请求顶掉");
        DropBundlePending("被 route=" + snap.Route + " 的请求顶掉");

        var id = EnsureId(snap);
        var dict = new Dictionary<string, object>
        {
            ["id"] = id,
            ["kind"] = snap.Kind,
            ["prompt"] = snap.Prompt ?? "",
            ["min"] = snap.Min,
            ["max"] = snap.Max,
            ["requireConfirm"] = snap.RequireConfirm,
            ["cancelable"] = snap.Cancelable,
            ["confidence"] = snap.Confidence,
            ["animated"] = true,          // 这条请求是用游戏自己的界面/手牌选择演的，会播动画
            ["route"] = snap.Route,
            ["fallback"] = true,          // ★ 这是 screen 通道（扫界面 + 模拟点击）的请求，只作紧急回退
            ["candidates"] = snap.Candidates.Select(CandidateJson).ToList(),
        };

        if (snap.AlreadySelected > 0)
        {
            // 手牌选择里已经有牌被选中（等确认）：剩下的候选是「还要再选几张」，
            // min/max 已经把已选数量扣掉了；此时用 picks:[] 就是「按确认」。
            dict["alreadySelected"] = snap.AlreadySelected;
            if (snap.Max == 0) dict["confirmPending"] = true;
        }

        if (snap.Route == "reward")
        {
            // 同样是 kind="reward"，但这是「卡牌奖励三选一」，不是奖励列表：
            // 两者用 stage 区分（奖励列表是 stage="rewards-list"，route="rewards"）。
            dict["stage"] = "card-choice";
            // 新增字段（不改既有字段）：这条请求能不能用 picks:[] 放弃。
            // true 时 min 已经是 0、cancelable 也是 true，prompt 里带一句「也可以放弃」。
            dict["canSkip"] = snap.CanSkip;
        }

        if (snap.Degraded)
        {
            dict["degraded"] = true;
            dict["degradedReason"] = snap.DegradedReason;
        }
        else if (snap.PromptFallback)
        {
            dict["promptFallback"] = true;
        }

        return new Dictionary<string, object> { ["source"] = "screen", ["pending"] = dict, ["selector"] = SelectorInfo() };
    }

    /// <summary>
    /// POST /respond：校验 picks 是不是这个请求的合法答案。
    ///
    /// selector 通道（默认）：先播 mod 端动画（亮牌 + 鲸鱼气泡 → 停 560ms → 交答案 → 停 220ms），
    /// 返回 <c>ok/applied/how:"selector"/animated/waitedMs</c>。
    /// screen 通道（只在 STS2_AGENT_SELECTOR=0 时）：点界面节点，等游戏自己播动画/结算。
    /// </summary>
    public static async Task<Dictionary<string, object>> Respond(long id, string[] picks)
    {
        picks ??= Array.Empty<string>();
        var sw = Stopwatch.StartNew();

        var pending = TopPending();
        if (pending != null)
        {
            if (pending.Id != id)
            {
                long olderId;
                lock (SelectorGate)
                    olderId = Pending.FirstOrDefault(p => p.Id == id)?.Id ?? 0;
                if (olderId != 0)
                    return Fail($"先回答最新的请求（id={pending.Id}）：更早的 {id} 要等它结完。");
                return Fail($"id 过期：当前请求是 {pending.Id}，你给的是 {id}（重新 GET /request）");
            }
            return await AnswerPending(pending, picks);
        }

        // ---- 游戏结束通道（NGameOverScreen：继续 / 主菜单 / 时间线 / 那个没文字的按钮）----
        var gameOver = _gameOverPending;
        if (gameOver != null)
        {
            if (id == _lastAnsweredId && _lastAnsweredId != 0)
                return Fail($"请求 {id} 已经回答过了");
            if (gameOver.Id != id)
                return Fail($"id 过期：当前游戏结束请求是 {gameOver.Id}，你给的是 {id}（重新 GET /request）");
            return await AnswerGameOver(gameOver, picks);
        }

        // ---- 占卜通道（水晶球小游戏）----
        var divination = _divinationPending;
        if (divination != null)
        {
            if (id == _lastAnsweredId && _lastAnsweredId != 0)
                return Fail($"请求 {id} 已经回答过了");
            if (divination.Id != id)
                return Fail($"id 过期：当前占卜请求是 {divination.Id}，你给的是 {id}（重新 GET /request）");
            return await AnswerDivination(divination, picks);
        }

        // ---- 选牌包通道（NChooseABundleSelectionScreen）----
        var bundle = _bundlePending;
        if (bundle != null)
        {
            if (id == _lastAnsweredId && _lastAnsweredId != 0)
                return Fail($"请求 {id} 已经回答过了");
            if (bundle.Id != id)
                return Fail($"id 过期：当前选牌包请求是 {bundle.Id}，你给的是 {id}（重新 GET /request）");
            return await AnswerBundle(bundle, picks);
        }

        // ---- 奖励通道（NRewardsScreen）----
        var rewards = _rewardsPending;
        if (rewards != null)
        {
            if (id == _lastAnsweredId && _lastAnsweredId != 0)
                return Fail($"请求 {id} 已经回答过了");
            if (rewards.Id != id)
                return Fail($"id 过期：当前奖励请求是 {rewards.Id}，你给的是 {id}（重新 GET /request）");
            return await AnswerRewards(rewards, picks);
        }

        // ---- 宝箱通道（NTreasureRoom）----
        var treasure = _treasurePending;
        if (treasure != null)
        {
            if (id == _lastAnsweredId && _lastAnsweredId != 0)
                return Fail($"请求 {id} 已经回答过了");
            if (treasure.Id != id)
                return Fail($"id 过期：当前宝箱请求是 {treasure.Id}，你给的是 {id}（重新 GET /request）");
            return await AnswerTreasure(treasure, picks);
        }

        // ---- 药水目标通道（/potion/use 建出来的；排在商店之前）----
        var potion = _potionPending;
        if (potion != null)
        {
            if (id == _lastAnsweredId && _lastAnsweredId != 0)
                return Fail($"请求 {id} 已经回答过了");
            if (potion.Id != id)
                return Fail($"id 过期：当前药水请求是 {potion.Id}，你给的是 {id}（重新 GET /request）");
            return await AnswerPotion(potion, picks);
        }

        // ---- 商店通道（NMerchantInventory）----
        var shop = _shopPending;
        if (shop != null)
        {
            if (id == _lastAnsweredId && _lastAnsweredId != 0)
                return Fail($"请求 {id} 已经回答过了");
            if (shop.Id != id)
                return Fail($"id 过期：当前商店请求是 {shop.Id}，你给的是 {id}（重新 GET /request）");
            return await AnswerShop(shop, picks);
        }

        // ---- 营火通道（NRestSiteRoom）----
        var rest = _restPending;
        if (rest != null)
        {
            if (id == _lastAnsweredId && _lastAnsweredId != 0)
                return Fail($"请求 {id} 已经回答过了");
            if (rest.Id != id)
                return Fail($"id 过期：当前营火请求是 {rest.Id}，你给的是 {id}（重新 GET /request）");
            return await AnswerRest(rest, picks);
        }

        // ---- 事件通道（NEventRoom）----
        var eventPending = _eventPending;
        if (eventPending != null)
        {
            if (id == _lastAnsweredId && _lastAnsweredId != 0)
                return Fail($"请求 {id} 已经回答过了");
            if (eventPending.Id != id)
                return Fail($"id 过期：当前事件请求是 {eventPending.Id}，你给的是 {id}（重新 GET /request）");
            return await AnswerEvent(eventPending, picks);
        }

        // ---- 推进通道（战斗结束、奖励还没出来的空档）----
        var proceed = _proceedPending;
        if (proceed != null)
        {
            if (id == _lastAnsweredId && _lastAnsweredId != 0)
                return Fail($"请求 {id} 已经回答过了");
            if (proceed.Id != id)
                return Fail($"id 过期：当前推进请求是 {proceed.Id}，你给的是 {id}（重新 GET /request）");
            return await AnswerProceed(proceed, picks);
        }

        // ---- 菜单通道（NMainMenu / NSingleplayerSubmenu / NCharacterSelectScreen）----
        var menu = _menuPending;
        if (menu != null)
        {
            if (id == _lastAnsweredId && _lastAnsweredId != 0)
                return Fail($"请求 {id} 已经回答过了");
            if (menu.Id != id)
                return Fail($"id 过期：当前菜单请求是 {menu.Id}，你给的是 {id}（重新 GET /request）");
            return await AnswerMenu(menu, picks);
        }

        if (id == _lastAnsweredId && _lastAnsweredId != 0)
            return Fail($"请求 {id} 已经回答过了");

        // 刚答完正在播动画：这期间界面扫描会把「游戏在等玩家」误判成一次新的手牌选择，一律拒绝。
        if (_animating)
            return Fail("正在播刚才那次应答的动画（约 1 秒），别抢答：稍等一下再 GET /request");

        // ---- screen 通道（fallback：扫界面 + 模拟点击，只在 selector 关掉时才会走到这里）----
        Snapshot snap;
        try { snap = await BridgePump.OnMain(Capture); }
        catch (Exception e) { return FailFallback("读取当前界面失败：" + e.Message); }

        if (snap == null) return FailFallback("现在没有等待选择的界面（GET /request 应该是 pending:null）");

        var currentId = EnsureId(snap);
        if (id != currentId)
            return FailFallback($"id 过期：当前请求是 {currentId}，你给的是 {id}（界面已经变了，重新 GET /request 再答）");

        if (snap.Candidates.Count == 0 && snap.Min > 0)
            return FailFallback(snap.Route == "hand" || snap.Route == "hand-guess"
                ? "手牌的候选牌没收集到（可能牌还没飞到位，或者规则过滤掉了全部手牌），没法应答"
                : "界面上没找到可选的卡牌，没法应答");

        var bad = Validate(snap.Min, snap.Max, snap.Candidates, picks, out var chosen);
        if (bad != null) return bad;

        // 手牌选择里「已经选够、只差确认」时 min 会被扣成 0：这时 picks 允许是空的。
        if (chosen.Count == 0 && snap.Min > 0)
            return FailFallback($"picks 是空的：这个界面至少要选 {Math.Max(1, snap.Min)} 张");

        // 卡牌奖励三选一：候选牌就是那 3 张，交给界面自己的 SelectCard(NCardHolder)，
        // 不点坐标、也不需要「在不在屏幕外」这种检查。
        // picks 为空（且这条请求允许放弃）时走游戏自己的「跳过」替代项，见 RespondCardRewardSkip。
        if (snap.Route == "reward")
        {
            var rewardResult = chosen.Count == 0
                ? await RespondCardRewardSkip(snap, sw)
                : await RespondCardReward(snap, chosen, sw);
            if (IsTrue(rewardResult, "ok")) _lastAnsweredId = currentId;
            if (IsTrue(rewardResult, "ok") && !rewardResult.ContainsKey("waitedMs"))
                rewardResult["waitedMs"] = sw.ElapsedMilliseconds;
            return rewardResult;
        }

        // 在屏幕外的卡（滚动牌堆时被裁掉）不能点：ScreenApi 会把坐标夹回视口，可能点到别的牌。
        // 手牌是例外：它本来就摆在屏幕上，扇形排布只会让牌身一半伸出视口底部，不能算「屏幕外」。
        var handRoute = snap.Route == "hand" || snap.Route == "hand-guess";
        foreach (var c in chosen)
        {
            if (c.Node == null)
                return FailFallback($"{c.Key} 没有可点的界面节点，没法驱动点击");
            if (!IsReachable(c.Node, handRoute))
                return FailFallback($"{c.Key} 在屏幕外");
        }

        Dictionary<string, object> result;
        if (snap.Route == "hand" || snap.Route == "hand-guess")
            result = await RespondHand(snap, chosen);
        else
            result = await RespondOverlay(snap, chosen);

        if (IsTrue(result, "ok")) _lastAnsweredId = currentId;
        // 交付时间也报出去（selector 通道自己已经填了，别覆盖它的精确值）。
        if (IsTrue(result, "ok") && !result.ContainsKey("waitedMs"))
            result["waitedMs"] = sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>给 Batch/诊断用：现在有没有 selector 通道的请求在等回答。</summary>
    internal static bool HasPending => TopPending() != null;

    /// <summary>selector 通道的状态，跟着 /request 一起返回，方便判断现在走的是哪条路。</summary>
    private static Dictionary<string, object> SelectorInfo()
    {
        int count;
        lock (SelectorGate) count = Pending.Count;
        return new Dictionary<string, object>
        {
            ["state"] = _selectorState,
            ["detail"] = _selectorDetail,
            ["scopeHeld"] = _selectorScope != null,
            ["pendingCount"] = count,
        };
    }

    // =================================================================== 奖励通道（NRewardsScreen）

    /*
     * 战斗结束后的「战斗奖励」列表（金币 / 药水 / 遗物 / 卡牌奖励 …）不是选牌，走不了
     * CardSelectCmd 的 selector 钩子，所以单独一条通道，但结构跟 selector 通道一样：
     * 一个 pending 请求 + 一套校验 + 一个应答，全部复用。
     *
     * 数据源（都在主线程读）：
     *   * 界面：MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen（注意在 Nodes.Screens 下，不在 Nodes.Rewards）
     *   * 候选：它的 private 字段 _rewardsSet : RewardsSet → RewardsSet.Rewards : List<Reward>
     *     （LinkedRewardSet 展开成它自己的 Rewards）；已经领过的按 Reward.SuccessfullySelected 过滤。
     *     每个 Reward 的标题从它自己的数据来（GoldReward.Amount / PotionReward.Potion.Title /
     *     RelicReward.Relic.Title / 其余用 Description），不用节点 uid。
     *
     * 应答（不模拟鼠标、不用坐标）：
     *   ① NRewardButton.GetReward()（private，反射调）——这就是 OnRelease 里「点下去」调的那个
     *      处理方法，它自己走 RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(reward)，
     *      然后播遗物飞进背包 / 药水飞进槽位的动画，再发 RewardClaimed 让界面把这一行摘掉。
     *   ② 拿不到按钮时退化成模型层：RewardsSetSynchronizer.SelectLocalReward(reward)，
     *      再补一次界面记账 NRewardsScreen.RewardCollectedFrom(button)。
     *   ③ 两个都拿不到才算失败（返回 ok:false，不谎报）。
     *
     * 连锁：CardReward 的领取挂在 NCardRewardSelectionScreen.OptionSelected() 上等玩家选牌，
     *   所以单项最多等 RewardSettleMs，超时就把「下一步是选牌」交回给 /request（route=reward，
     *   stage=card-choice）。**不能**在这里 await 到底，否则 /respond 永不返回。
     *
     * 跳过：picks 为空 = 全部跳过，跟界面上按「跳过」一样：
     *   RewardsSetSynchronizer.SkipLocalRewardsSet() + 把界面从 NOverlayStack 摘掉
     *   （= NRewardsScreen.OnProceedButtonPressed 的非终局分支；终局奖励屏交给
     *   RunManager.ProceedFromTerminalRewardsScreen）。
     */

    /// <summary>一次领取的结果：ok / 用了哪条路 / 有没有结算完 / 失败原因。</summary>
    private sealed class RewardApplyResult
    {
        public bool Ok;
        public string How = "none";
        public bool Settled;
        public string Why;
        public string Visual;
    }

    /// <summary>主线程里「把这次领取跑起来」的结果：拿到游戏自己的 Task 才算真的开始。</summary>
    private sealed class RewardStart
    {
        public Task Task;
        public string How = "none";
        public string Why;
    }

    /// <summary>
    /// 奖励列表的发现 + pending 构造（必须在主线程调用，由 /request 触发）。
    /// 返回 null 表示「现在没有要答的奖励」（界面不在 / 已经领完只剩继续按钮）。
    /// </summary>
    private static Dictionary<string, object> DescribeRewardsPending()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return null;

        var screen = FindFirst(tree.Root, "NRewardsScreen");
        if (screen == null)
        {
            // 界面消失 = 这批奖励结束：待答请求和「已发出」记录一起清掉，别留僵尸请求。
            var stale = _rewardsPending;
            if (stale != null)
            {
                _rewardsPending = null;
                BridgeLog.Info($"rewards: 界面消失，清掉请求 id={stale.Id}");
            }
            ClearRewardDispatched();
            return null;
        }

        if (!(ReadMember(screen, "_rewardsSet") is RewardsSet set))
        {
            DropRewardsPending("读不到 NRewardsScreen._rewardsSet");
            return null;
        }

        // 换了一个奖励界面 / 换了一份 RewardsSet：上一批的「已发出」记录作废。
        var scope = screen.GetInstanceId() + "|" + SafeSetId(set);
        if (!string.Equals(scope, _rewardsScope, StringComparison.Ordinal))
        {
            _rewardsScope = scope;
            ClearRewardDispatched();
        }

        var candidates = CollectRewardCandidates(screen, set);
        if (candidates.Count == 0)
        {
            // 只剩「跳过 / 继续」：不是奖励选择，回 pending:null（推进由 /proceed 负责）。
            DropRewardsPending("没有未领的奖励项（只剩跳过/继续）");
            return null;
        }

        var signature = RewardSignature(screen, set, candidates);
        var state = _rewardsPending;
        if (state == null || !string.Equals(state.Signature, signature, StringComparison.Ordinal))
        {
            state = new PendingSelection
            {
                Id = NextId(),
                Min = 0,                      // 可以一项都不要（picks:[] = 全部跳过）
                Max = candidates.Count,       // 也可以全要
                Kind = "reward",
                Caller = "NRewardsScreen",
                Prompt = RewardPrompt,
                Screen = screen,
                RewardSet = set,
                Cards = candidates,
                CreatedUtc = DateTime.UtcNow,
            };
            state.Signature = signature;
            _rewardsPending = state;
            BridgeLog.Info($"rewards: 新请求 id={state.Id} max={state.Max} " +
                           $"keys={DescribeRewardKeys(candidates)}");
        }
        else
        {
            // 同一份请求：只刷新节点引用（界面重绘后按钮会换新实例），id 不变。
            state.Screen = screen;
            state.RewardSet = set;
            state.Min = 0;
            state.Max = candidates.Count;
            state.Cards = candidates;
        }

        return DescribeRewardPending(state);
    }

    private static Dictionary<string, object> DescribeRewardPending(PendingSelection pending) => new()
    {
        ["id"] = pending.Id,
        ["kind"] = "reward",
        ["stage"] = "rewards-list",          // 和卡牌奖励三选一（stage=card-choice）区分
        ["prompt"] = pending.Prompt ?? RewardPrompt,
        ["min"] = pending.Min,
        ["max"] = pending.Max,
        ["requireConfirm"] = false,
        ["cancelable"] = true,               // min=0：picks:[] 永远合法（= 全部跳过）
        ["caller"] = pending.Caller,
        ["confidence"] = "high",             // 候选和标题都来自 RewardsSet 模型，不是扫界面的猜测
        ["animated"] = true,
        ["animation"] = RewardAnimationPlan,
        ["route"] = "rewards",
        ["candidates"] = pending.Cards.Select(CandidateJson).ToList(),
    };

    /// <summary>奖励界面的候选：就是 <c>RewardsSet.Rewards</c>（成组的展开），已领的剔掉。</summary>
    private static List<Candidate> CollectRewardCandidates(Node screen, RewardsSet set)
    {
        var list = new List<Candidate>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (set?.Rewards == null) return list;

        var buttons = RewardButtonMap(screen);
        foreach (var reward in set.Rewards)
        {
            if (reward == null) continue;

            // 成组奖励（LinkedRewardSet）：界面上每个子奖励都有自己的按钮，逐个当候选。
            if (reward is LinkedRewardSet linked && linked.Rewards != null)
            {
                foreach (var sub in linked.Rewards) AddRewardCandidate(list, counts, buttons, sub);
                continue;
            }

            AddRewardCandidate(list, counts, buttons, reward);
        }
        return list;
    }

    private static void AddRewardCandidate(
        List<Candidate> list, Dictionary<string, int> counts,
        List<(Reward Reward, Control Button)> buttons, Reward reward)
    {
        if (reward == null) return;
        if (SafeSelected(reward)) return;              // 已经领过
        if (IsRewardDispatched(reward)) return;       // 已经发出领取、还在等游戏结算（例如卡牌奖励在等选牌）

        var (title, type, detail) = DescribeReward(reward);
        list.Add(new Candidate
        {
            Key = NextKey(counts, title),
            Title = title,
            Upgraded = false,
            Cost = -1,
            RewardRef = reward,
            RewardType = type,
            Detail = detail,
            Node = RewardButtonFor(buttons, reward),
        });
    }

    /// <summary>奖励项的人话标题（key 的前半截）＋ RewardType 名 ＋ 它自己的 Description。</summary>
    private static (string Title, string Type, string Detail) DescribeReward(Reward reward)
    {
        var type = "";
        // RewardType 是 protected 属性（子类各自 override），只能反射读。
        try { type = ReadMember(reward, "RewardType")?.ToString() ?? ""; } catch { }

        var detail = "";
        try { detail = LocText(reward.Description); } catch { }

        string title = null;
        try
        {
            switch (reward)
            {
                case GoldReward gold:
                    {
                        var amount = SafeInt(() => gold.Amount);
                        title = amount > 0 ? amount + "金币" : "金币";
                        break;
                    }
                case PotionReward potion:
                    title = SafeText(() => LocText(potion.Potion?.Title)) ?? "药水";
                    break;
                case RelicReward relic:
                    title = SafeText(() => LocText(relic.Relic?.Title)) ?? "遗物";
                    break;
                case CardReward _:
                    title = "卡牌奖励";
                    break;
                case SpecialCardReward _:
                    title = SafeText(() => detail) ?? "特殊卡牌";
                    break;
                case CardRemovalReward _:
                    title = "移除卡牌";
                    break;
                case LinkedRewardSet _:
                    title = SafeText(() => detail) ?? "成组奖励";
                    break;
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("rewards: 读奖励标题失败（" + e.GetType().Name + "）：" + e.Message);
        }

        if (string.IsNullOrWhiteSpace(title)) title = SafeText(() => detail) ?? type;
        if (string.IsNullOrWhiteSpace(title)) title = "奖励";
        title = title.Trim();
        // key 是给 agent 抄的：太长的描述截断，别把整段说明塞进 key。
        if (title.Length > 20) title = title.Substring(0, 20);
        return (title, type, detail ?? "");
    }

    /// <summary>
    /// 奖励项在界面上对应的按钮：扫界面子树里的 NRewardButton，按 <c>NRewardButton.Reward</c>
    /// 的**引用**配对（成组奖励里的子按钮也在这棵子树里，自然就配上了）。
    /// 只用于「调它自己的领取方法」和「界面记账」，不用于点坐标。
    /// </summary>
    private static List<(Reward Reward, Control Button)> RewardButtonMap(Node screen)
    {
        var map = new List<(Reward, Control)>();
        if (screen == null) return map;

        var stack = new Stack<Node>();
        stack.Push(screen);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 8000)
        {
            var node = stack.Pop();
            if (node == null) continue;

            if (node is Control control && ScreenApi.TypeChainContains(node, "NRewardButton"))
            {
                if (ReadMember(node, "Reward") is Reward reward) map.Add((reward, control));
            }

            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return map;
    }

    private static Control RewardButtonFor(List<(Reward Reward, Control Button)> buttons, Reward reward)
    {
        if (buttons == null || reward == null) return null;
        foreach (var entry in buttons)
        {
            if (!ReferenceEquals(entry.Reward, reward)) continue;
            try
            {
                if (entry.Button != null && GodotObject.IsInstanceValid(entry.Button) && entry.Button.IsInsideTree())
                    return entry.Button;
            }
            catch { }
        }
        return null;
    }

    private static bool SafeSelected(Reward reward)
    {
        try { return reward != null && reward.SuccessfullySelected; }
        catch { return false; }
    }

    private static string SafeSetId(RewardsSet set)
    {
        try { return set?.Id.ToString() ?? "?"; }
        catch { return "?"; }
    }

    private static string SafeText(Func<string> read)
    {
        try { return read(); } catch { return null; }
    }

    private static int SafeInt(Func<int> read)
    {
        try { return read(); } catch { return 0; }
    }

    /// <summary>读一个可能为 null / 可能抛的引用（模型对象的字段）。</summary>
    private static T SafeModel<T>(Func<T> read) where T : class
    {
        try { return read(); } catch { return null; }
    }

    /// <summary>奖励请求的签名：界面 + 奖励集合 + 候选 key 列表。变了就换 id。</summary>
    private static string RewardSignature(Node screen, RewardsSet set, List<Candidate> candidates)
    {
        var parts = new List<string>
        {
            screen.GetInstanceId().ToString(),
            SafeSetId(set),
            RewardPrompt,
            candidates.Count.ToString(),
        };
        foreach (var c in candidates)
        {
            parts.Add(c.Key + "=" + (c.RewardType ?? "") + ":" + (c.Detail ?? ""));
        }
        return string.Join("|", parts);
    }

    private static string DescribeRewardKeys(List<Candidate> candidates)
        => string.Join("、", candidates.Take(8).Select(c => c.Key))
           + (candidates.Count > 8 ? $" …（共 {candidates.Count} 项）" : "");

    private static bool IsRewardDispatched(Reward reward)
    {
        if (reward == null) return false;
        lock (RewardGate)
        {
            foreach (var r in RewardDispatched) if (ReferenceEquals(r, reward)) return true;
        }
        return false;
    }

    private static void MarkRewardDispatched(Reward reward)
    {
        if (reward == null) return;
        lock (RewardGate)
        {
            foreach (var r in RewardDispatched) if (ReferenceEquals(r, reward)) return;
            RewardDispatched.Add(reward);
        }
    }

    /// <summary>批量撤掉「已发出」记录（整批放弃时用）。</summary>
    private static void UnmarkAllDispatched(List<Candidate> chosen)
    {
        if (chosen == null) return;
        foreach (var c in chosen) UnmarkRewardDispatched(c.RewardRef);
    }

    /// <summary>应用失败时把记录撤掉：这一项应该还能在 /request 里再看到、再答一次。</summary>
    private static void UnmarkRewardDispatched(Reward reward)
    {
        if (reward == null) return;
        lock (RewardGate)
        {
            for (var i = RewardDispatched.Count - 1; i >= 0; i--)
                if (ReferenceEquals(RewardDispatched[i], reward)) RewardDispatched.RemoveAt(i);
        }
    }

    private static void ClearRewardDispatched()
    {
        lock (RewardGate) RewardDispatched.Clear();
    }

    /// <summary>把待答的奖励请求摘掉（不碰「已发出」记录：那份记录按界面/奖励集合的生命周期清）。</summary>
    private static void DropRewardsPending(string why)
    {
        var pending = _rewardsPending;
        if (pending == null) return;
        _rewardsPending = null;
        BridgeLog.Info($"rewards: 清掉请求 id={pending.Id}（{why}）");
    }

    // ------------------------------------------------------------------ 应答

    /// <summary>
    /// 奖励请求的应答：校验 → 逐项应用（每项之间停 RewardGapMs）→ 全部应用完才 ok。
    /// picks 为空 = 全部跳过（用游戏自己的 SkipLocalRewardsSet）。
    /// </summary>
    private static async Task<Dictionary<string, object>> AnswerRewards(PendingSelection pending, string[] picks)
    {
        var failure = Validate(pending.Min, pending.Max, pending.Cards, picks, out var chosen, "项");
        if (failure != null) return failure;      // 校验没过：请求还在槽里，本来就能再答

        var sw = Stopwatch.StartNew();

        // 先从槽里摘掉：动画在等的时候不能再被 /request 看到，也不能被答第二次。
        _rewardsPending = null;
        _lastAnsweredId = pending.Id;
        _animating = true;

        Dictionary<string, object> result;
        try { result = await ApplyRewardsAnswer(pending, chosen, sw); }
        catch (Exception e)
        {
            BridgeLog.Warn($"rewards: 应答 id={pending.Id} 抛异常（{e.GetType().Name}）：{e.Message}");
            result = Fail("领取时出错：" + e.Message);
        }
        finally { _animating = false; }

        // 任何 ok:false 都不是「已经答过了」：把请求挂回槽里、清掉回答标记，
        // 这样药水槽满 / 界面不在 / 某项没领成这些情况下，agent 还能改一道答案再答。
        if (!IsTrue(result, "ok")) RestoreRewardsAfterReject(pending);
        return result;
    }

    /// <summary>真正去领：调用方已经摘掉槽、置了 _animating，这里只管逐项领取与核对。</summary>
    private static async Task<Dictionary<string, object>> ApplyRewardsAnswer(
        PendingSelection pending, List<Candidate> chosen, Stopwatch sw)
    {
        var screen = pending.Screen;

        try
        {
            bool shown;
            try { shown = await BridgePump.OnMain(() => IsShown(screen), 3000); }
            catch { shown = false; }
            if (!shown)
                return Fail("奖励界面已经不在了（可能已经被关掉），重新 GET /request");

            // 先记账：游戏把 SuccessfullySelected 置位之前，这一项不能再被当成候选发一遍。
            foreach (var c in chosen) MarkRewardDispatched(c.RewardRef);

            if (chosen.Count == 0)
                return await SkipAllRewards(screen, pending, sw);

            // 药水槽满了就别装没事人：领了也放不下，游戏会静默丢掉一瓶。
            // 先拦下来，把「槽位已满 + 当前有哪些药水」明明白白告诉 agent，让它自己决定丢哪瓶。
            if (chosen.Any(c => c.RewardRef is PotionReward))
            {
                var full = false;
                var used = 0;
                var max = 0;
                var currentKeys = new List<string>();
                try
                {
                    await BridgePump.OnMain(() =>
                    {
                        full = Potions.SlotsFull(out used, out max, out currentKeys);
                        return true;
                    }, 3000);
                }
                catch { }

                if (full)
                {
                    // 这次没收下这瓶药水：把它从「已发出」里撤掉，下一次 /request 还能再看到它。
                    // 「已应答」标记和 _rewardsPending 由 AnswerRewards 的失败收尾统一还原
                    // （见 RestoreRewardsAfterReject），这里不要自己动，免得两处状态打架。
                    UnmarkAllDispatched(chosen);
                    BridgeLog.Warn($"rewards id={pending.Id}: 药水槽已满 {used}/{max}，拒绝领取 {used} 瓶装不下的药水奖励");
                    var warn = Fail($"药水槽已满（{used}/{max}），这次无法领取药水。");
                    warn["warning"] = "potions-full";
                    warn["potionsUsed"] = used;
                    warn["potionsMax"] = max;
                    warn["availableKeys"] = currentKeys;
                    return warn;
                }
            }

            var items = new List<Dictionary<string, object>>();
            var appliedKeys = new List<string>();
            var failed = new List<string>();
            var anySettled = false;
            var cardChoicePending = false;

            // 卡牌奖励会弹一个模态的三选一，会挡住别的奖励的飞入动画：把它放到最后应用
            // （OrderBy 是稳定排序，其它项的相对顺序不变）。
            var ordered = chosen.OrderBy(c => c.RewardRef is CardReward ? 1 : 0).ToList();

            foreach (var c in ordered)
            {
                var result = await ApplyOneReward(screen, c);
                var item = new Dictionary<string, object>
                {
                    ["key"] = c.Key,
                    ["how"] = result.How,
                    ["visual"] = result.Visual ?? RewardVisualTag(c),
                    ["settled"] = result.Settled,
                    ["ok"] = result.Ok,
                };
                if (!string.IsNullOrEmpty(result.Why)) item["note"] = result.Why;
                items.Add(item);

                if (result.Ok)
                {
                    appliedKeys.Add(c.Key);
                    if (result.Settled) anySettled = true;
                    else cardChoicePending = true;      // 游戏在等下一步（典型是选牌）
                }
                else
                {
                    // 没应用成功就把「已发出」记录撤掉，下一次 /request 还能再看到这一项。
                    UnmarkRewardDispatched(c.RewardRef);
                    failed.Add(c.Key + "（" + (result.Why ?? "游戏没接受") + "）");
                }

                // 每项之后都留出动画时间（飞入 + 按钮消失 / 选牌窗口弹出），这是「延迟播放」的节拍。
                await Task.Delay(RewardGapMs);
            }

            var waited = sw.ElapsedMilliseconds;
            var applied = appliedKeys.Count == 0 ? "（无）" : string.Join("、", appliedKeys);
            var anyGameEntry = items.Any(i =>
                i.TryGetValue("how", out var howValue) &&
                string.Equals(howValue as string, "reward-button", StringComparison.Ordinal));

            if (failed.Count > 0)
            {
                BridgeLog.Warn($"rewards answered id={pending.Id} picks={applied} failed={failed.Count} " +
                               $"waitedMs={waited}");
                var dict = Fail($"有 {failed.Count} 项没应用成功：{string.Join("；", failed)}" +
                                (appliedKeys.Count > 0 ? $"（已经生效的：{applied}）" : "") +
                                "。重新 GET /request 看还剩哪些。");
                dict["applied"] = applied;
                dict["items"] = items;
                dict["waitedMs"] = waited;
                return dict;
            }

            var remaining = pending.Cards.Count - chosen.Count;
            BridgeLog.Info($"rewards answered id={pending.Id} picks={applied} waitedMs={waited} " +
                           $"settled={anySettled} cardChoicePending={cardChoicePending}");

            var ok = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = "screen",
                ["route"] = "rewards",
                ["applied"] = applied,
                ["how"] = chosen.Count == 1 ? items[0]["how"] : "reward-batch",
                ["animated"] = true,
                ["waitedMs"] = waited,
                // 真的走了奖励项自己的界面流程（按钮飞入/消失）才算 reward-applied；
                // 只走了模型层 + 延迟就如实报 reward-delay。
                ["visual"] = anyGameEntry ? "reward-applied" : "reward-delay",
                ["settled"] = anySettled,
                ["claimed"] = appliedKeys.Count,
                ["items"] = items,
            };
            if (cardChoicePending)
            {
                // 卡牌奖励会接着弹三选一：那是**下一个** request，别在这里等。
                ok["next"] = "card-choice";
                ok["note"] = "奖励已受理，游戏正在等下一步（选牌）：GET /request 拿新请求（route=reward, stage=card-choice）";
            }
            else if (remaining > 0)
            {
                ok["remaining"] = remaining;
                ok["note"] = $"还有 {remaining} 项没领：GET /request 可以接着答";
            }
            return ok;
        }
        finally
        {
            // 动画结束（含异常路径）：恢复 /request、/respond 的正常判断。
            _animating = false;
        }
    }

    /// <summary>
    /// 这次奖励请求没被接受（任何 ok:false）：把它恢复成「还挂在槽里、还能再答」。
    /// 只清「已应答」标记、把 pending 挂回去，不碰游戏状态 —— 所以被拒的请求不会把 id 永久锁死
    /// （实测：药水槽满被拒之后，想改领别的会返回「请求 2 已经回答过了」）。
    ///
    /// 这里**不**批量撤「已发出」记录（RewardDispatched）：每一项的成败已经由领取流程自己记账
    /// （失败项当场撤掉、成功项等 SuccessfullySelected 翻位），在这里再撤一遍会把「已经发给
    /// 游戏、正在等选牌」的卡牌奖励重新变成候选，可能领两遍。
    /// 真正的 ok:true 仍然保持锁（不进这里），防止重复领取。
    /// </summary>
    private static void RestoreRewardsAfterReject(PendingSelection pending)
    {
        if (pending == null) return;
        if (_lastAnsweredId == pending.Id) _lastAnsweredId = -1;
        _rewardsPending = pending;
        BridgeLog.Info($"rewards: 请求 id={pending.Id} 没被接受，恢复成可再答（已挂回 _rewardsPending）");
    }

    /// <summary>
    /// 单项奖励：先让游戏自己的领取入口跑起来（主线程），再最多等 RewardSettleMs。
    /// 超时不是错 —— 卡牌奖励本来就要等玩家选牌，那一步走的是下一个 request。
    /// </summary>
    private static async Task<RewardApplyResult> ApplyOneReward(Node screen, Candidate candidate)
    {
        var reward = candidate?.RewardRef;
        if (reward == null) return new RewardApplyResult { Ok = false, Why = "候选里没有 Reward 对象" };

        // 幂等保护：请求发出去到 agent 应答之间，玩家可能自己点了这一项。
        // 已经领过的绝不能再调一次 SelectLocalReward（那会把金币/遗物发两遍）。
        if (SafeSelected(reward))
            return new RewardApplyResult
            {
                Ok = true,
                How = "already-selected",
                Settled = true,
                Visual = RewardVisualTag(candidate),
                Why = "应答之前就已经领过了（不重复领取）",
            };

        RewardStart start;
        try { start = await BridgePump.OnMain(() => StartRewardApply(screen, candidate), 5000); }
        catch (Exception e)
        {
            return new RewardApplyResult { Ok = false, How = "none", Why = "启动领取失败：" + e.Message };
        }

        if (start?.Task == null)
            return new RewardApplyResult { Ok = false, How = start?.How ?? "none", Why = start?.Why ?? "没拿到游戏的领取任务" };

        var state = await WaitOrTimeout(start.Task, RewardSettleMs, candidate.Key);
        if (state == "timeout")
        {
            // 游戏受理了，但还在等下一步（选牌）。没结算不算失败，如实标出来。
            return new RewardApplyResult
            {
                Ok = true,
                How = start.How,
                Settled = false,
                Why = "游戏还在等下一步（选牌），没有立刻结算",
                Visual = RewardVisualTag(candidate),
            };
        }

        if (state == "faulted")
            return new RewardApplyResult { Ok = false, How = start.How, Why = "游戏的领取任务抛异常（看 BridgeLog）" };

        var selected = SafeSelected(reward);
        return new RewardApplyResult
        {
            Ok = selected,
            How = start.How,
            Settled = true,
            Visual = RewardVisualTag(candidate),
            Why = selected ? null : "游戏没把这项标记为已领取（可能槽位满了 / 被跳过）",
        };
    }

    /// <summary>
    /// 主线程：把这次领取交给游戏自己的入口。两条路，优先界面按钮自己的处理方法。
    /// </summary>
    private static RewardStart StartRewardApply(Node screen, Candidate candidate)
    {
        var reward = candidate.RewardRef;
        if (reward == null) return new RewardStart { Why = "候选里没有 Reward 对象" };

        // ① 界面按钮自己的领取方法：NRewardButton.OnRelease 里调的就是 GetReward()。
        //    它会走 RewardsSetSynchronizer.SelectLocalReward + 遗物/药水的飞入动画 +
        //    RewardClaimed 信号让界面把这一行摘掉 —— 除了「不是鼠标点的」，跟玩家点按钮一模一样。
        var button = candidate.Node;
        if (button != null && GodotObject.IsInstanceValid(button) && button.IsInsideTree())
        {
            // 按钮已经被禁用 = 这一项正在被处理（游戏在 GetReward 一开始就 Disable，也可能是玩家刚点了）：
            // 这时候再调一次会把同一份奖励领两遍，直接拒绝。
            if (ReadMember(button, "_isEnabled") is bool enabled && !enabled)
                return new RewardStart { Why = "这个奖励按钮已经被禁用（可能玩家正在点它），不重复领取" };

            var getReward = FindMethod(button.GetType(), "GetReward", 0);
            if (getReward != null)
            {
                try
                {
                    if (getReward.Invoke(button, null) is Task task)
                        return new RewardStart { Task = task, How = "reward-button" };
                }
                catch (Exception e)
                {
                    BridgeLog.Warn($"rewards: NRewardButton.GetReward 调失败（{e.GetType().Name}）：{e.Message}，改用模型层");
                }
            }
            else
            {
                BridgeLog.Warn("rewards: NRewardButton 上没找到 GetReward()，改用模型层");
            }
        }

        // ② 模型层兜底：跟界面同一个领取入口（SelectLocalReward 会调 Reward.SelectUnsynchronized）。
        var sync = RunManager.Instance?.RewardsSetSynchronizer;
        if (sync == null) return new RewardStart { Why = "RunManager.RewardsSetSynchronizer 不可用" };

        try
        {
            var task = sync.SelectLocalReward(reward);
            if (task == null) return new RewardStart { Why = "SelectLocalReward 返回 null" };

            // 模型层不会发 RewardClaimed 信号，界面那一行得自己记账摘掉（等领取真的成功了再摘）。
            if (button != null && GodotObject.IsInstanceValid(button))
                WatchRewardThenCollect(screen, button, task);

            return new RewardStart { Task = task, How = "reward-model" };
        }
        catch (Exception e)
        {
            return new RewardStart { Why = "SelectLocalReward 抛了：" + e.Message };
        }
    }

    /// <summary>兜底路径的收尾：领取成功之后把界面那一行摘掉（NRewardsScreen.RewardCollectedFrom）。</summary>
    private static void WatchRewardThenCollect(Node screen, Control button, Task<bool> task)
    {
        _ = CollectAfterReward(screen, button, task);
    }

    private static async Task CollectAfterReward(Node screen, Control button, Task<bool> task)
    {
        try
        {
            var ok = await task;
            if (!ok) return;
            await BridgePump.OnMain(() =>
            {
                if (screen == null || !GodotObject.IsInstanceValid(screen) || !IsShown(screen)) return 0;
                if (button == null || !GodotObject.IsInstanceValid(button)) return 0;
                if (screen is MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen rewardsScreen)
                    rewardsScreen.RewardCollectedFrom(button);
                return 0;
            }, 3000);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("rewards: 兜底领取的界面收尾失败（" + e.GetType().Name + "）：" + e.Message);
        }
    }

    /// <summary>
    /// 全部跳过：跟 <c>NRewardsScreen.OnProceedButtonPressed</c> 的跳过分支一致 ——
    /// <c>SkipLocalRewardsSet()</c>（对每个没领的奖励调 OnSkipped + 完结这份奖励集合），
    /// 然后把界面从 overlay 栈上摘掉（终局奖励屏交给 RunManager）。
    /// </summary>
    private static async Task<Dictionary<string, object>> SkipAllRewards(
        Node screen, PendingSelection pending, Stopwatch sw)
    {
        string why = null;
        try
        {
            why = await BridgePump.OnMain(() =>
            {
                if (!IsShown(screen)) return "界面已经不在了";

                var sync = RunManager.Instance?.RewardsSetSynchronizer;
                if (sync == null) return "RunManager.RewardsSetSynchronizer 不可用";

                // SkipLocalRewardsSet 只对「还在奖励栈顶」的集合有效：已经完结的集合再调会抛
                // InvalidOperationException（"Tried to skip reward set ... but they are not
                // currently viewing any reward set!"）。先问一句，别拿异常当控制流。
                var alreadyDone = false;
                try { alreadyDone = pending.RewardSet != null && sync.IsRewardsSetCompleted(pending.RewardSet); }
                catch { }

                if (!alreadyDone)
                {
                    try { sync.SkipLocalRewardsSet(); }
                    catch (Exception e) { return "SkipLocalRewardsSet 失败：" + e.Message; }
                }

                // 收尾跟 OnProceedButtonPressed 一样：终局（BOSS）奖励屏交给 RunManager，
                // 普通奖励屏从 overlay 栈上摘掉，否则界面会一直停在那儿。
                try
                {
                    if (ReadMember(screen, "_isTerminal") is bool terminal && terminal)
                    {
                        var task = RunManager.Instance?.ProceedFromTerminalRewardsScreen();
                        if (task != null) _ = WatchSafely(task, "ProceedFromTerminalRewardsScreen");
                    }
                    else if (screen is MegaCrit.Sts2.Core.Nodes.Screens.Overlays.IOverlayScreen overlay)
                    {
                        MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.Remove(overlay);
                    }
                }
                catch (Exception e)
                {
                    // 奖励已经跳过了，界面没关掉不算失败：写日志，让 /request 之后按界面状态处理。
                    BridgeLog.Warn("rewards: 跳过后的界面收尾失败（" + e.GetType().Name + "）：" + e.Message);
                }

                return null;
            }, 5000);
        }
        catch (Exception e)
        {
            return Fail("全部跳过时出错：" + e.Message);
        }

        if (why != null) return Fail("全部跳过失败：" + why);

        // 留一点时间让界面淡出/收尾，别让 agent 立刻轮询到「同一个界面还在」。
        await Task.Delay(RewardGapMs);

        BridgeLog.Info($"rewards answered id={pending.Id} 全部跳过（{pending.Cards.Count} 项）");
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["source"] = "screen",
            ["route"] = "rewards",
            ["applied"] = "（全部跳过）",
            ["how"] = "reward-skip-set",
            ["animated"] = true,
            ["waitedMs"] = sw.ElapsedMilliseconds,
            ["visual"] = "reward-skipped",
            ["claimed"] = 0,
            ["skipped"] = pending.Cards.Count,
        };
    }

    /// <summary>等一个游戏任务，但最多等 ms；返回 done / timeout / faulted。</summary>
    private static async Task<string> WaitOrTimeout(Task task, int ms, string what)
    {
        if (task == null) return "done";
        try
        {
            var winner = await Task.WhenAny(task, Task.Delay(ms));
            if (winner != task)
            {
                // 超时也要观察异常，否则会变成 UnobservedTaskException。
                _ = WatchSafely(task, what);
                return "timeout";
            }
            await task;
            return "done";
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"rewards: {what} 的领取任务抛了（{e.GetType().Name}）：{e.Message}");
            return "faulted";
        }
    }

    /// <summary>只为「把异常吃掉并写日志」：不改变任何行为。</summary>
    private static async Task WatchSafely(Task task, string what)
    {
        try { await task; }
        catch (Exception e)
        {
            BridgeLog.Warn($"rewards: {what} 后续抛了（{e.GetType().Name}）：{e.Message}");
        }
    }

    private static string RewardVisualTag(Candidate candidate) => candidate?.RewardRef switch
    {
        RelicReward _ => "relic-fly",
        PotionReward _ => "potion-fly",
        GoldReward _ => "gold",
        CardReward _ => "card-choice",
        SpecialCardReward _ => "special-card",
        CardRemovalReward _ => "card-removal",
        _ => "reward-apply",
    };

    /// <summary>沿继承链找方法（校验参数个数）：private / protected 也要能找到。</summary>
    private static MethodInfo FindMethod(Type type, string name, int parameterCount)
    {
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            try
            {
                foreach (var method in t.GetMethods(AnyMember | BindingFlags.DeclaredOnly))
                {
                    if (!string.Equals(method.Name, name, StringComparison.Ordinal)) continue;
                    if (method.GetParameters().Length == parameterCount) return method;
                }
            }
            catch { }
        }
        return null;
    }

    // =================================================================== 商店通道（NMerchantInventory）

    /*
     * 商店不是「必须选 N 个」的决策，而是「可以买 0~N 件、也可以直接走」：
     * min = 0、max = 当前可买条目数、cancelable = true，picks:[] = 什么都不买（不清空商店）。
     *
     * 数据源（都在主线程读）：
     *   * 界面：MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory（NFakeMerchantInventory 派生它），
     *     它的 Inventory 属性 = 数据层 MerchantInventory，IsOpen = 商店面板真的打开了。
     *   * 条目：MerchantInventory.AllEntries（IL 里的顺序：CardEntries → RelicEntries → PotionEntries →
     *     CardRemovalEntry）。
     *   * 价格：MerchantEntry.Cost（int，内部已经过 Hook.ModifyMerchantPrice，就是界面上显示的数）；
     *     MerchantEntry.EnoughGold = Cost <= Player.Gold。
     *   * 已售出：MerchantEntry.IsStocked 由各子类 override（都核对过 IL）：
     *       MerchantCardEntry.IsStocked        = CreationResult != null
     *       MerchantRelicEntry.IsStocked       = Model != null
     *       MerchantPotionEntry.IsStocked      = Model != null
     *       MerchantCardRemovalEntry.IsStocked = !Used
     *   * 标题 / 效果：卡牌 CreationResult.Card（Title / Description + DynamicVars），
     *     遗物·药水 Model（Title / DynamicDescription）。
     *
     * 应答（不模拟鼠标、不用坐标）：
     *   ① NMerchantSlot.OnSelected()（private，反射调）—— 鼠标松开时调的就是它：
     *      ClearHoverTip → OnTryPurchase(Inventory) → Entry.OnTryPurchaseWrapper(inventory, false)，
     *      之后 MerchantEntry.PurchaseCompleted 信号让 NMerchantInventory 播商家音效/对话、把焦点移到下一格。
     *   ② 拿不到格子节点时退化成模型层 MerchantEntry.OnTryPurchaseWrapper(inventory, false)；
     *      卡牌移除是 3 参重载 (inventory, ignoreCost=false, cancelable=true)。
     *   ③ 两个都拿不到才算失败（返回 ok:false，不谎报）。
     *
     * 验证（每件都做，不靠猜）：
     *   金币必须减少至少 Cost；该条目必须变成「不再待售」（IsStocked 变 false，
     *   或 RestockAfterPurchase 换成了另一件商品 —— 用模型 Id 比对）。
     *   任一条不满足就 ok:false 并把事实写清楚（购买前/后金币、价格、是否仍待售）。
     *
     * 连锁：买「卡牌移除服务」走 OneOffSynchronizer.DoLocalMerchantCardRemoval →
     *   CardSelectCmd.FromDeckForRemoval(prefs{MinSelect=1, Cancelable=true, RequireManualConfirmation=true})，
     *   选完才 LoseGold + RemoveFromDeck + OnCardRemovalUsed()。选牌这一步**不能等到底**
     *   （否则 /respond 永远不返回），单项最多等 ShopSettleMs，超时按「已受理、下一步是选牌」
     *   返回 next:"card-choice"，由下一次 /request 给出选牌请求（selector 通道 kind=remove，
     *   或界面扫描 route=grid）。金币是在选完之后才扣的，所以这一项这里不做金币校验，如实标 settled:false。
     */

    /// <summary>一次购买的现场核对结果（都在主线程读）。</summary>
    private sealed class ShopView
    {
        public bool Ok;
        public string Why;
        public bool Stocked;
        public int Price;
        public int Gold;
        public string Identity = "";
    }

    /// <summary>单项购买的结果：成没成 / 走了哪条路 / 有没有结算完 / 是不是在等选牌 / 失败原因。</summary>
    private sealed class ShopApplyResult
    {
        public bool Ok;
        public string How = "none";
        public bool Settled;
        public bool WaitingChoice;
        public string Why;
        public string Visual;
    }

    /// <summary>主线程里「把这次购买跑起来」的结果：拿到游戏自己的 Task 才算真的开始。</summary>
    private sealed class ShopStart
    {
        public Task Task;
        public string How = "none";
        public string Why;
    }

    /// <summary>
    /// 商店的发现 + pending 构造（必须在主线程调用，由 /request 触发）。**两阶段**：
    ///   * <c>stage:"closed"</c>（面板还没打开）：只问「要不要打开」，候选就一个「打开商店#1」。
    ///     这一步**不**暴露商品、**不**替 agent 买东西，也**不**再用 <c>IsOpen</c> 当唯一门槛把请求吞掉。
    ///     顺手附上 <c>gold</c> 和 <c>potions</c>（能不能用由游戏自己的可用性检查给，见 Potions）。
    ///   * <c>stage:"items"</c>（面板已打开）：原来的逻辑，一字不改 —— 商品、价格、购买。
    /// 返回 null 表示「现在没有要答的商店请求」（界面不在 / 读不到模型 / 打开着但没货）。
    /// </summary>
    private static Dictionary<string, object> DescribeShopPending()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return null;

        var node = FindShopInventory(tree.Root);
        if (node == null)
        {
            // 离开商店 = 这批购买结束：待答请求一起清掉，别留僵尸请求。
            DropShopPending("商店界面不在场景树里");
            return null;
        }

        var inventory = ReadShopInventory(node);
        if (inventory == null)
        {
            DropShopPending("读不到 NMerchantInventory.Inventory（数据层 MerchantInventory）");
            return null;
        }

        var gold = ShopGold(inventory);
        bool open;
        try { open = node.IsOpen; }
        catch { open = false; }

        // ================= 阶段 1：面板还没打开 =================
        if (!open)
        {
            // 只在「真的站在商店 / 假商人房间里」才发：面板节点可能被缓存留在树上，
            // 那种情况下（比如已经离开房间进了战斗）绝不能凭空冒出商店请求。
            // 注意这条只挡新增的 closed 阶段，items 阶段（面板真打开着）保持原行为不变。
            if (!ShopRoomPresent(out var roomWhy))
            {
                DropShopPending("当前房间不是商店（" + roomWhy + "）");
                return null;
            }

            ulong nodeId = 0;
            try { nodeId = node.GetInstanceId(); } catch { }
            if (_shopClosedLoggedFor != nodeId)
            {
                _shopClosedLoggedFor = nodeId;
                BridgeLog.Info("shop: 商店面板已加载但没打开（IsOpen=false）——发 stage=closed 请求，"
                               + "agent 可以直接「打开商店」，也可以先用药水");
            }

            var candidates = new List<Candidate> { ShopOpenCandidate() };
            var signature = ShopSignature(node, ShopStageClosed, candidates);
            var state = _shopPending;
            if (state == null || !string.Equals(state.Signature, signature, StringComparison.Ordinal))
            {
                state = new PendingSelection
                {
                    Id = NextId(),
                    Min = 0,                       // picks:[] = 不打开，永远合法
                    Max = 1,
                    Kind = "shop",
                    Caller = "NMerchantInventory",
                    Prompt = ShopOpenPrompt,
                    Screen = node,
                    Cards = candidates,
                    ShopGold = gold,
                    ShopStage = ShopStageClosed,
                    CreatedUtc = DateTime.UtcNow,
                };
                state.Signature = signature;
                _shopPending = state;
                BridgeLog.Info($"shop: 新请求 id={state.Id} stage={ShopStageClosed} min=0 max=1 gold={gold} " +
                               $"key={ShopOpenKey}");
            }
            else
            {
                state.Screen = node;
                state.Min = 0;
                state.Max = 1;
                state.Prompt = ShopOpenPrompt;
                state.Cards = candidates;
                state.ShopGold = gold;
                state.ShopStage = ShopStageClosed;
            }

            return DescribeShopPendingJson(state);
        }

        // ================= 阶段 2：面板已打开（原有逻辑） =================
        _shopClosedLoggedFor = 0;

        var items = CollectShopCandidates(node, inventory, gold);
        if (items.Count == 0)
        {
            // 全买光了 / 全售罄：没有可买的，就不是决策（离开商店用 /proceed）。
            DropShopPending("没有可买的条目（都售罄或者已经买光）");
            return null;
        }

        var itemsSignature = ShopSignature(node, ShopStageItems, items);
        var itemsState = _shopPending;
        if (itemsState == null || !string.Equals(itemsState.Signature, itemsSignature, StringComparison.Ordinal))
        {
            itemsState = new PendingSelection
            {
                Id = NextId(),
                Min = 0,                      // 一件都不买永远合法
                Max = items.Count,
                Kind = "shop",
                Caller = "NMerchantInventory",
                Prompt = ShopPrompt,
                Screen = node,
                Cards = items,
                ShopGold = gold,
                ShopStage = ShopStageItems,
                CreatedUtc = DateTime.UtcNow,
            };
            itemsState.Signature = itemsSignature;
            _shopPending = itemsState;
            BridgeLog.Info($"shop: 新请求 id={itemsState.Id} stage={ShopStageItems} max={itemsState.Max} " +
                           $"gold={gold} keys={DescribeShopKeys(items)}");
        }
        else
        {
            // 同一份请求：只刷新节点引用和金币（玩家可能刚自己买了一件，签名变了才会换 id）。
            itemsState.Screen = node;
            itemsState.Min = 0;
            itemsState.Max = items.Count;
            itemsState.Cards = items;
            itemsState.ShopGold = gold;
            itemsState.ShopStage = ShopStageItems;
        }

        return DescribeShopPendingJson(itemsState);
    }

    /// <summary>
    /// 现在是不是「真的在商店里」：<c>CurrentRoom is MerchantRoom</c>，或者
    /// <c>CurrentRoom is EventRoom && CanonicalEvent is FakeMerchant</c>（假商人事件）。
    /// 按类型判断，不看节点类名，也不看界面在不在树上。
    /// </summary>
    private static bool ShopRoomPresent(out string why)
    {
        why = null;
        try
        {
            var room = RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom;
            if (room is MegaCrit.Sts2.Core.Rooms.MerchantRoom) return true;
            if (room is MegaCrit.Sts2.Core.Rooms.EventRoom eventRoom
                && eventRoom.CanonicalEvent is MegaCrit.Sts2.Core.Models.Events.FakeMerchant) return true;
            why = room == null ? "room=null" : room.GetType().Name;
            return false;
        }
        catch (Exception e) { why = e.GetType().Name + "：" + e.Message; return false; }
    }

    /// <summary>
    /// 阶段 1 的唯一候选：「打开商店#1」。它是**动作**不是商品 —— 不读 AllEntries，
    /// 所以店里卖什么在这一阶段完全不会泄露（和宝箱「打开宝箱」同一个套路）。
    /// </summary>
    private static Candidate ShopOpenCandidate() => new()
    {
        Key = ShopOpenKey,
        Title = ShopOpenTitle,
        Upgraded = false,
        Cost = -1,
        Detail = "打开后能看到商品和价格",
        ShopOpenAction = true,
        Enabled = true,
    };

    private static Dictionary<string, object> DescribeShopPendingJson(PendingSelection pending)
    {
        var closed = string.Equals(pending.ShopStage, ShopStageClosed, StringComparison.Ordinal);
        var dict = new Dictionary<string, object>
        {
            ["id"] = pending.Id,
            ["kind"] = "shop",
            // ★ 新增字段：两阶段（closed / items）。既有字段名与语义都没动。
            ["stage"] = closed ? ShopStageClosed : ShopStageItems,
            ["prompt"] = pending.Prompt ?? (closed ? ShopOpenPrompt : ShopPrompt),
            ["min"] = pending.Min,
            ["max"] = pending.Max,
            ["requireConfirm"] = false,
            ["cancelable"] = true,               // closed：picks:[] = 不打开；items：picks:[] = 什么都不买
            ["caller"] = pending.Caller,
            ["confidence"] = "high",             // 候选/价格都来自 MerchantInventory 模型，不是扫界面的猜测
            ["animated"] = true,
            ["animation"] = closed ? ShopOpenAnimationPlan : ShopAnimationPlan,
            ["route"] = "shop",
            ["gold"] = pending.ShopGold,         // 当前金币，方便 agent 判断预算
        };

        // ★ 新增字段（只加不改）：药水槽现状。closed 阶段 agent 可以直接决定「先丢一瓶」，
        // 所以这里给出每瓶能不能用；usable 只是参考，真正的拦截在 /potion/use 那一刻。
        var potions = Potions.DescribeSlotsForRequest(ShopPlayer(pending));
        if (potions != null) dict["potions"] = potions;

        dict["candidates"] = pending.Cards.Select(CandidateJson).ToList();
        if (closed)
            dict["next"] = "picks:[\"打开商店#1\"] = 打开面板（之后重新 GET /request 会给出 stage:\"items\" 的商品）；"
                           + "picks:[] = 不打开。"
                           + "想先用药水就 POST /potion/use（只有污浊药水能对商人用）。";
        return dict;
    }

    /// <summary>这次商店请求对应的玩家：优先 MerchantInventory.Player，再退回 players[0]。</summary>
    private static MegaCrit.Sts2.Core.Entities.Players.Player ShopPlayer(PendingSelection pending)
    {
        try
        {
            var node = pending?.Screen as NMerchantInventory;
            var inventory = ReadShopInventory(node);
            var player = inventory?.Player;
            if (player != null) return player;
        }
        catch { }

        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            return state?.Players != null && state.Players.Count > 0 ? state.Players[0] : null;
        }
        catch { return null; }
    }

    /// <summary>场景树里的商店面板（NFakeMerchantInventory 也派生自它，自然一起认）。</summary>
    private static NMerchantInventory FindShopInventory(Node root)
    {
        if (root == null) return null;
        var stack = new Stack<Node>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 40000)
        {
            var node = stack.Pop();
            if (node == null) continue;
            if (node is NMerchantInventory inventory)
            {
                try
                {
                    if (GodotObject.IsInstanceValid(inventory) && inventory.IsInsideTree()) return inventory;
                }
                catch { }
            }
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return null;
    }

    /// <summary>数据层 MerchantInventory：优先面板自己的 Inventory，读不到再看当前商店房间。</summary>
    private static MerchantInventory ReadShopInventory(NMerchantInventory node)
    {
        try
        {
            if (node != null && GodotObject.IsInstanceValid(node) && node.Inventory != null)
                return node.Inventory;
        }
        catch { }

        try
        {
            if (RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom is MegaCrit.Sts2.Core.Rooms.MerchantRoom room)
                return room.GetLocalInventory();
        }
        catch { }
        return null;
    }

    /// <summary>当前金币：优先 MerchantInventory.Player（这间商店的持有者），再退回本地玩家。</summary>
    private static int ShopGold(MerchantInventory inventory)
    {
        try
        {
            var player = inventory?.Player;
            if (player != null) return player.Gold;
        }
        catch { }

        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            var player = state?.Players != null && state.Players.Count > 0 ? state.Players[0] : null;
            return player?.Gold ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>商店候选：MerchantInventory.AllEntries，已售出的格子剔除。</summary>
    private static List<Candidate> CollectShopCandidates(
        NMerchantInventory node, MerchantInventory inventory, int gold)
    {
        var list = new List<Candidate>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        IEnumerable<MerchantEntry> entries;
        try { entries = inventory.AllEntries; }
        catch (Exception e)
        {
            BridgeLog.Warn("shop: 读 MerchantInventory.AllEntries 失败（" + e.GetType().Name + "）：" + e.Message);
            return list;
        }
        if (entries == null) return list;

        var slots = ShopSlots(node);
        foreach (var entry in entries)
        {
            if (entry == null) continue;

            bool stocked;
            try { stocked = entry.IsStocked; }
            catch { continue; }
            if (!stocked) continue;                      // 已买走 / 空格子

            var price = SafeInt(() => entry.Cost);
            var (title, type, detail, energy) = DescribeShopEntry(entry);

            list.Add(new Candidate
            {
                Key = NextKey(counts, title),
                Title = title,
                Upgraded = false,
                Cost = energy,
                Entry = entry,
                Slot = ShopSlotFor(slots, entry),
                Price = price,
                Affordable = gold >= price,               // 由 mod 算，不让 agent 自己比
                ItemType = type,
                Detail = detail,
                // 稳定 id：中文标题抄错一个字也能买对。序号 = 本场的陈列顺序（同一场里不变）。
                Stable = "slot:" + (list.Count + 1),
            });
        }
        return list;
    }

    /// <summary>面板上的全部格子（卡片 / 无色卡 / 遗物 / 药水 / 移除服务）。</summary>
    private static List<NMerchantSlot> ShopSlots(NMerchantInventory node)
    {
        var list = new List<NMerchantSlot>();
        try
        {
            foreach (var slot in node.GetAllSlots())
            {
                if (slot == null) continue;
                if (!GodotObject.IsInstanceValid(slot)) continue;
                list.Add(slot);
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("shop: NMerchantInventory.GetAllSlots 失败（" + e.GetType().Name + "）：" + e.Message);
        }
        return list;
    }

    /// <summary>按 <c>NMerchantSlot.Entry</c> 的**引用**把条目和格子配对（只用于调格子自己的方法）。</summary>
    private static NMerchantSlot ShopSlotFor(List<NMerchantSlot> slots, MerchantEntry entry)
    {
        if (slots == null || entry == null) return null;
        foreach (var slot in slots)
        {
            try { if (ReferenceEquals(slot.Entry, entry)) return slot; }
            catch { }
        }
        return null;
    }

    /// <summary>商店条目的人话标题（key 的前半截）＋ itemType ＋ 效果文本 ＋ 卡牌能量费用。</summary>
    private static (string Title, string Type, string Detail, int Energy) DescribeShopEntry(MerchantEntry entry)
    {
        string title = null, type = "", detail = "";
        var energy = -1;

        try
        {
            switch (entry)
            {
                case MerchantCardEntry cardEntry:
                    {
                        type = "Card";
                        var card = SafeCard(cardEntry);
                        if (card != null)
                        {
                            title = CardTitle(card);
                            detail = CardText(card);
                            energy = CardCost(card);
                        }
                        break;
                    }
                case MerchantRelicEntry relicEntry:
                    {
                        type = "Relic";
                        var relic = SafeModel(() => relicEntry.Model);
                        if (relic != null)
                        {
                            title = SafeText(() => LocText(relic.Title));
                            detail = DynamicText(relic, "DynamicDescription", "Description") ?? "";
                        }
                        break;
                    }
                case MerchantPotionEntry potionEntry:
                    {
                        type = "Potion";
                        var potion = SafeModel(() => potionEntry.Model);
                        if (potion != null)
                        {
                            title = SafeText(() => LocText(potion.Title));
                            detail = DynamicText(potion, "DynamicDescription", "Description") ?? "";
                        }
                        break;
                    }
                case MerchantCardRemovalEntry removalEntry:
                    {
                        type = "Service";
                        title = "卡牌移除";
                        var increase = SafeInt(() => MerchantCardRemovalEntry.PriceIncrease);
                        detail = "从牌组里移除一张牌（买下后会弹选牌）"
                                 + (increase > 0 ? $"；每买一次之后价格 +{increase}" : "");
                        break;
                    }
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("shop: 读条目标题失败（" + e.GetType().Name + "）：" + e.Message);
        }

        if (string.IsNullOrWhiteSpace(title)) title = type.Length > 0 ? type : "商品";
        title = title.Trim();
        // key 是给 agent 抄的：太长的描述截断，别把整段说明塞进 key。
        if (title.Length > 20) title = title.Substring(0, 20);

        return (title, type, detail ?? "", energy);
    }

    private static CardModel SafeCard(MerchantCardEntry entry)
    {
        try
        {
            var created = entry?.CreationResult;
            return created?.Card;
        }
        catch { return null; }
    }

    /// <summary>卡牌效果文本：Description 本地化 + 把 DynamicVars 的占位符换成实际数值。</summary>
    private static string CardText(CardModel card)
    {
        if (card == null) return "";
        string text = null;
        try { text = DynamicText(card, "Description"); } catch { }
        if (string.IsNullOrEmpty(text)) return "";

        try
        {
            var vars = card.DynamicVars;
            if (vars != null)
            {
                foreach (var variable in vars.Values)
                {
                    var value = variable.IntValue.ToString();
                    text = Regex.Replace(text, "\\{" + Regex.Escape(variable.Name) + ":[^}]*\\}", value);
                    text = Regex.Replace(text, "\\{" + Regex.Escape(variable.Name) + "\\}", value);
                }
            }
        }
        catch { }
        return text;
    }

    /// <summary>按名字顺序找一个非空的本地化文本（private getter 也能读到 —— 遗物/药水的 Description 就是 private）。</summary>
    private static string DynamicText(object model, params string[] names)
    {
        if (model == null || names == null) return null;
        foreach (var name in names)
        {
            var text = LocText(ReadMember(model, name) as MegaCrit.Sts2.Core.Localization.LocString);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    /// <summary>
    /// 商店请求的签名：阶段 + 面板 + 候选（key 已经带标题，再加价格和类型）。
    /// 故意**不**放 detail 文本：本地化/动态数值每帧都可能变，塞进去会让 id 无谓地抖。
    /// stage 必须进签名：面板一打开就从 closed 换成 items，那是另一份请求（新 id）。
    /// </summary>
    private static string ShopSignature(NMerchantInventory node, string stage, List<Candidate> candidates)
    {
        var parts = new List<string>
        {
            node.GetInstanceId().ToString(),
            stage ?? ShopStageItems,
            string.Equals(stage, ShopStageClosed, StringComparison.Ordinal) ? ShopOpenPrompt : ShopPrompt,
            candidates.Count.ToString(),
        };
        foreach (var c in candidates)
        {
            parts.Add(c.Key + "=" + (c.ItemType ?? "") + ":" + c.Price);
        }
        return string.Join("|", parts);
    }

    private static string DescribeShopKeys(List<Candidate> candidates)
        => string.Join("、", candidates.Take(8).Select(c => c.Key + "(" + c.Price + ")"))
           + (candidates.Count > 8 ? $" …（共 {candidates.Count} 件）" : "");

    /// <summary>把待答的商店请求摘掉（不动游戏状态，也不清空商店）。</summary>
    private static void DropShopPending(string why)
    {
        var pending = _shopPending;
        if (pending == null) return;
        _shopPending = null;
        BridgeLog.Info($"shop: 清掉请求 id={pending.Id}（{why}）");
    }

    // ------------------------------------------------------------------ 应答

    /// <summary>
    /// 商店请求的应答入口（包裹层）：按 <c>pending.stage</c> 分成两条完全不同的路 ——
    ///   * <c>closed</c>：只开面板（或干脆不开），**不买任何东西**；
    ///   * <c>items</c>：原来的逐件购买逻辑，一字不改。
    /// 任何 ok:false 都会把请求挂回槽里（见 <see cref="RestoreShopAfterReject"/>），所以能改一道答案再答。
    /// </summary>
    private static async Task<Dictionary<string, object>> AnswerShop(PendingSelection pending, string[] picks)
    {
        if (string.Equals(pending.ShopStage, ShopStageClosed, StringComparison.Ordinal))
            return await AnswerShopOpen(pending, picks);

        var failure = Validate(pending.Min, pending.Max, pending.Cards, picks, out var chosen, "项");
        if (failure != null) return failure;      // 校验没过：请求还在槽里，本来就能再答

        var sw = Stopwatch.StartNew();

        // 先从槽里摘掉：购买在跑的时候不能再被 /request 看到，也不能被答第二次。
        _shopPending = null;
        _lastAnsweredId = pending.Id;

        if (chosen.Count == 0)
        {
            // 什么都不买：不动商店、不发任何购买请求。
            BridgeLog.Info($"shop answered id={pending.Id} 什么都没买（gold={pending.ShopGold}）");
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = "screen",
                ["route"] = "shop",
                ["stage"] = ShopStageItems,
                ["applied"] = "（什么也没买）",
                ["how"] = "shop-none",
                ["animated"] = false,
                ["waitedMs"] = sw.ElapsedMilliseconds,
                ["visual"] = "shop-none",
                ["bought"] = 0,
                ["gold"] = pending.ShopGold,
                ["items"] = new List<object>(),
            };
        }

        _animating = true;
        Dictionary<string, object> result;
        try { result = await ApplyShopAnswer(pending, chosen, sw); }
        catch (Exception e)
        {
            BridgeLog.Warn($"shop: 应答 id={pending.Id} 抛异常（{e.GetType().Name}）：{e.Message}");
            result = Fail("购买时出错：" + e.Message);
        }
        finally { _animating = false; }

        // 任何 ok:false 都不是「已经答过了」：把请求挂回槽里、清掉回答标记。
        // 药水槽满 / 金币不够 / 已售出 / 界面不在这些情况下，agent 都能改一道答案再答同一个 id。
        if (!IsTrue(result, "ok")) RestoreShopAfterReject(pending);
        return result;
    }

    /// <summary>
    /// 阶段 1（<c>closed</c>）的应答：只开面板。
    /// picks = [「打开商店#1」] → 调房间自己的 <c>OpenInventory()</c>，等 <c>IsOpen == true</c>（最多 3s）；
    /// picks = [] → 不打开，面板保持关闭（想反悔就重新 GET /request，会再问一次）。
    /// 无论哪条，**都不买东西**：打开之后要重新 GET /request 才会给出 stage:"items" 的商品。
    /// </summary>
    private static async Task<Dictionary<string, object>> AnswerShopOpen(
        PendingSelection pending, string[] picks)
    {
        picks ??= Array.Empty<string>();
        if (picks.Length > 1)
            return Fail($"只能选 1 个动作，你给了 {picks.Length} 个");

        Candidate chosen = null;
        if (picks.Length == 1)
        {
            var key = (picks[0] ?? "").Trim();
            chosen = pending.Cards.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));
            if (chosen == null)
                return Fail($"{key} 不在候选里（可选：{DescribeKeys(pending.Cards)}；picks:[] = 不打开）");
        }

        var sw = Stopwatch.StartNew();

        // 先从槽里摘掉：开面板在跑的时候不能再被 /request 看到，也不能被答第二次。
        _shopPending = null;
        _lastAnsweredId = pending.Id;

        if (chosen == null)
        {
            BridgeLog.Info($"shop(open) answered id={pending.Id} 没打开");
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = "screen",
                ["route"] = "shop",
                ["stage"] = ShopStageClosed,
                ["applied"] = "（没打开）",
                ["how"] = "shop-open-skip",
                ["animated"] = false,
                ["waitedMs"] = sw.ElapsedMilliseconds,
                ["visual"] = ShopVisualOpenSkipped,
                ["opened"] = false,
                ["bought"] = 0,
                ["items"] = new List<object>(),
                ["next"] = "面板保持关闭：想打开就重新 GET /request（stage:\"closed\" 会再问一次）；"
                           + "想用药水就直接 POST /potion/use。",
            };
        }

        _animating = true;
        Dictionary<string, object> result;
        try { result = await OpenShopPanel(pending, sw); }
        catch (Exception e)
        {
            BridgeLog.Warn($"shop(open): 应答 id={pending.Id} 抛异常（{e.GetType().Name}）：{e.Message}");
            result = Fail("打开商店面板时出错：" + e.Message);
        }
        finally { _animating = false; }

        if (!IsTrue(result, "ok")) RestoreShopAfterReject(pending);
        return result;
    }

    /// <summary>
    /// 阶段 1 真正干活：走游戏自己的开面板入口 + 等 <c>IsOpen</c> 变 true，**不买任何东西**。
    /// 调用方已经摘掉槽、置了 _animating。
    /// </summary>
    private static async Task<Dictionary<string, object>> OpenShopPanel(PendingSelection pending, Stopwatch sw)
    {
        var node = pending.Screen as NMerchantInventory;
        if (node == null) return Fail("商店面板节点丢了，没法打开");

        // 注意：**不能**用 IsShown —— 面板没打开时通常是 Visible=false，那样会把「还没开」误判成
        // 「界面没了」。这里只要求节点还在树上（和 FindShopInventory 的判据一致）。
        bool here;
        try { here = await BridgePump.OnMain(() => InTree(node), 3000); }
        catch { here = false; }
        if (!here) return Fail("商店界面已经不在了（已经离开商店），没法打开");

        // 已经开着（玩家自己点了商人按钮）：不再调第二次，直接算成功。
        bool alreadyOpen;
        try { alreadyOpen = await BridgePump.OnMain(() => SafeIsOpen(node), 3000); }
        catch { alreadyOpen = false; }

        string why = null;
        var openedNow = false;
        if (!alreadyOpen)
        {
            try
            {
                why = await BridgePump.OnMain(() => OpenShopEntry(node), 5000);
            }
            catch (Exception e) { why = "调用开面板入口失败：" + e.Message; }

            if (!string.IsNullOrEmpty(why)) return Fail(why);
            openedNow = true;
        }

        var (opened, waited) = await WaitShopOpen(node);
        if (!opened)
        {
            var dict = Fail($"打开面板之后 {ShopOpenMs}ms 内 IsOpen 一直是 false"
                            + "（NMerchantInventory.Open() 走到 set_IsOpen(true) 这一段没跑完）");
            dict["waitedMs"] = waited;
            dict["opened"] = false;
            return dict;
        }

        BridgeLog.Info($"shop(open) answered id={pending.Id} opened waitedMs={waited} openedNow={openedNow}");

        var ok = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["source"] = "screen",
            ["route"] = "shop",
            ["stage"] = ShopStageClosed,
            ["applied"] = "（打开商店）",
            ["how"] = openedNow ? "shop-open" : "shop-open-awaited",
            ["animated"] = true,
            ["waitedMs"] = waited,
            ["visual"] = ShopVisualOpened,
            ["opened"] = true,
            ["bought"] = 0,
            ["items"] = new List<object>(),
            ["next"] = "面板已打开、什么都没买：重新 GET /request 会给出 stage:\"items\" 的商品列表。",
        };
        if (!openedNow)
            ok["note"] = "面板本来就已经开着（可能是玩家自己点的商人按钮）：这次只等它稳定，没有重复触发开店";
        return ok;
    }

    /// <summary>
    /// 主线程：调**房间节点自己的**开面板入口（= 点商人按钮那一整条链）：
    ///   <c>NMerchantButton.OnRelease</c> → <c>EmitSignalMerchantOpened</c> →
    ///   <c>NMerchantRoom.OnMerchantOpened / NFakeMerchant.OnMerchantOpened</c>（都只有一句）
    ///   → <c>OpenInventory()</c>：若已开则 return；否则 Disable「前进」→ <c>Inventory.Open()</c>
    ///   （里面 <c>RunSafely(DoOpenAnimation())</c> + 欢迎音效 + <c>set_IsOpen(true)</c>）
    ///   → Disable 商人按钮 → 连 <c>InventoryClosed</c> 以便关面板后恢复。
    /// 真商人（<c>NMerchantRoom</c>）和假商人（<c>NFakeMerchant</c>）各有一份同名方法，所以先沿父链找
    /// 持有这个面板的房间节点（按类型认），找不到才退回 <c>NMerchantInventory.Open()</c>。
    /// 返回 null 表示成功；非空是失败原因。
    /// </summary>
    private static string OpenShopEntry(NMerchantInventory node)
    {
        if (node == null) return "商店面板节点为空";

        Node owner = null;
        try
        {
            for (var parent = node.GetParent(); parent != null; parent = parent.GetParent())
            {
                if (parent is NMerchantRoom || parent is NFakeMerchant) { owner = parent; break; }
            }
        }
        catch { }

        if (owner != null)
        {
            var method = FindMethod(owner.GetType(), "OpenInventory", 0);
            if (method != null)
            {
                try
                {
                    method.Invoke(owner, null);
                    BridgeLog.Info($"shop: 调 {owner.GetType().Name}.OpenInventory() 开店");
                    return null;
                }
                catch (Exception e)
                {
                    var inner = e.InnerException ?? e;
                    BridgeLog.Warn($"shop: {owner.GetType().Name}.OpenInventory 抛了（{inner.GetType().Name}）：{inner.Message}");
                }
            }
            else
            {
                BridgeLog.Warn($"shop: {owner.GetType().Name} 上没找到 OpenInventory()，退回 NMerchantInventory.Open()");
            }
        }
        else
        {
            BridgeLog.Warn("shop: 找不到持有这个面板的房间节点（NMerchantRoom / NFakeMerchant），退回 NMerchantInventory.Open()");
        }

        // 兜底：直接调面板自己的 Open()（IsOpen 就是在它里面置 true 的）。
        try
        {
            node.Open();
            BridgeLog.Info("shop: 调 NMerchantInventory.Open() 开店（兜底路径）");
            return null;
        }
        catch (Exception e)
        {
            var inner = e.InnerException ?? e;
            return $"开面板失败：NMerchantInventory.Open() 抛了 {inner.GetType().Name}：{inner.Message}";
        }
    }

    private static bool SafeIsOpen(NMerchantInventory node)
    {
        try { return node != null && GodotObject.IsInstanceValid(node) && node.IsOpen; }
        catch { return false; }
    }

    /// <summary>节点还在不在树上（不看可见性 —— 商店面板关着时就是不可见的）。</summary>
    private static bool InTree(Node node)
    {
        try { return node != null && GodotObject.IsInstanceValid(node) && node.IsInsideTree(); }
        catch { return false; }
    }

    /// <summary>等 <c>IsOpen</c> 变 true（最多 <see cref="ShopOpenMs"/>），顺便记花了多久。</summary>
    private static async Task<(bool Opened, long Waited)> WaitShopOpen(NMerchantInventory node)
    {
        var sw = Stopwatch.StartNew();
        var tries = Math.Max(1, ShopOpenMs / ShopOpenPollMs);
        for (var i = 0; i < tries; i++)
        {
            bool open;
            try { open = await BridgePump.OnMain(() => SafeIsOpen(node), 3000); }
            catch { open = false; }
            if (open) return (true, sw.ElapsedMilliseconds);
            await Task.Delay(ShopOpenPollMs);
        }
        return (false, sw.ElapsedMilliseconds);
    }

    /// <summary>真正去买：调用方已经摘掉槽、置了 _animating，这里只管逐件购买与核对。</summary>
    private static async Task<Dictionary<string, object>> ApplyShopAnswer(
        PendingSelection pending, List<Candidate> chosen, Stopwatch sw)
    {
        var node = pending.Screen as NMerchantInventory;

        _animating = true;
        try
        {
            bool shown;
            try { shown = await BridgePump.OnMain(() => IsShown(node), 3000); }
            catch { shown = false; }
            if (!shown)
                return Fail("商店界面已经不在了（已经被关掉），没法购买");

            var items = new List<Dictionary<string, object>>();
            var appliedKeys = new List<string>();
            var failed = new List<string>();
            var waitingChoice = false;
            var slotPath = 0;

            // 卡牌移除服务会弹一个模态的选牌界面，会挡住别的购买：把它放到最后买
            // （OrderBy 是稳定排序，其它件的相对顺序不变）。
            var ordered = chosen.OrderBy(c => c.Entry is MerchantCardRemovalEntry ? 1 : 0).ToList();

            foreach (var c in ordered)
            {
                var result = await ApplyOneShopEntry(node, c);

                var item = new Dictionary<string, object>
                {
                    ["key"] = c.Key,
                    ["itemType"] = c.ItemType ?? "",
                    ["price"] = c.Price,
                    ["how"] = result.How,
                    // 只有真买成（ok:true）才允许报 shop-bought / shop-model-delay / card-choice；
                    // 没买成的一律 shop-none —— 别再谎报「已经买到了」。
                    ["visual"] = result.Ok ? (result.Visual ?? ShopVisualBought) : ShopVisualNone,
                    ["settled"] = result.Settled,
                    ["ok"] = result.Ok,
                };
                if (!string.IsNullOrEmpty(result.Why)) item["note"] = result.Why;
                items.Add(item);

                if (result.Ok)
                {
                    appliedKeys.Add(c.Key);
                    if (result.WaitingChoice) waitingChoice = true;
                    if (string.Equals(result.How, "shop-slot", StringComparison.Ordinal)) slotPath++;
                }
                else
                {
                    failed.Add(c.Key + "（" + (result.Why ?? "游戏没接受") + "）");
                }

                // 每件之后都留出拍子：商店格子的买走/售罄表现和商家台词需要时间播。
                await Task.Delay(ShopGapMs);
            }

            var waited = sw.ElapsedMilliseconds;
            var applied = appliedKeys.Count == 0 ? "（无）" : string.Join("、", appliedKeys);

            int goldNow;
            try
            {
                goldNow = await BridgePump.OnMain(
                    () => ShopGold(ReadShopInventory(node)), 3000);
            }
            catch { goldNow = -1; }

            if (failed.Count > 0)
            {
                BridgeLog.Warn($"shop answered id={pending.Id} picks={applied} failed={failed.Count} " +
                               $"waitedMs={waited}");
                var dict = Fail($"有 {failed.Count} 件没买成：{string.Join("；", failed)}"
                                + (appliedKeys.Count > 0 ? $"（已经买到的：{applied}）" : ""));
                dict["applied"] = applied;
                dict["items"] = items;
                dict["waitedMs"] = waited;
                dict["gold"] = goldNow;
                return dict;
            }

            BridgeLog.Info($"shop answered id={pending.Id} picks={applied} waitedMs={waited} " +
                           $"gold={goldNow} waitingChoice={waitingChoice} slotPath={slotPath}");

            var ok = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = "screen",
                ["route"] = "shop",
                ["applied"] = applied,
                ["how"] = chosen.Count == 1 ? items[0]["how"] : "shop-batch",
                ["animated"] = true,
                ["waitedMs"] = waited,
                // 真走了格子自己的点击处理方法（有买走/售罄表现）才算 shop-bought；
                // 只走模型层的如实报 shop-model-delay。
                ["visual"] = slotPath > 0 ? ShopVisualBought : "shop-model-delay",
                ["settled"] = !waitingChoice,
                ["bought"] = appliedKeys.Count,
                ["gold"] = goldNow,
                ["items"] = items,
            };
            if (waitingChoice)
            {
                // 卡牌移除服务会接着弹选牌：那是**下一个** request，别在这里等。
                ok["next"] = "card-choice";
                ok["note"] = "购买已受理，游戏正在等下一步（选牌）：下一次 GET /request 是 kind=remove 的选牌请求";
            }
            return ok;
        }
        finally
        {
            _animating = false;
        }
    }

    /// <summary>
    /// 这次商店请求没被接受（任何 ok:false）：把它恢复成「还挂在槽里、还能再答」。
    /// 只清「已应答」标记、把 pending 挂回去，不碰游戏状态 —— 所以已经被拒的请求不会
    /// 把 id 永久锁死（实测：药水槽满被拒之后，想改买别的会返回「请求 2 已经回答过了」）。
    ///
    /// 已经买到的那些件不会因此重复扣钱：下一次 ApplyOneShopEntry 会先核对
    /// 「还待售 + 金币够」，已售出的直接报 already-sold，不会再发给游戏。
    /// 真正的 ok:true 仍然保持锁（不进这里），防止重复购买 / 重复领取。
    /// </summary>
    private static void RestoreShopAfterReject(PendingSelection pending)
    {
        if (pending == null) return;
        if (_lastAnsweredId == pending.Id) _lastAnsweredId = -1;
        _shopPending = pending;
        BridgeLog.Info($"shop: 请求 id={pending.Id} 没被接受，恢复成可再答（已挂回 _shopPending）");
    }

    /// <summary>
    /// 单件商品：先核对「还待售 + 金币够」（事实写清楚），再让游戏自己的购买入口跑起来，
    /// 最多等 ShopSettleMs，然后用「金币减少」+「这一格不再待售」两条核对结果。
    /// </summary>
    private static async Task<ShopApplyResult> ApplyOneShopEntry(NMerchantInventory node, Candidate candidate)
    {
        var entry = candidate?.Entry;
        if (entry == null) return new ShopApplyResult { Ok = false, Why = "候选里没有 MerchantEntry 对象" };

        ShopView before;
        try { before = await BridgePump.OnMain(() => ReadShopView(node, entry), 3000); }
        catch (Exception e) { return new ShopApplyResult { Ok = false, Why = "读取商店状态失败：" + e.Message }; }
        if (before == null) return new ShopApplyResult { Ok = false, Why = "读取商店状态超时" };
        if (!before.Ok) return new ShopApplyResult { Ok = false, Why = "读取商店状态失败：" + (before.Why ?? "?") };

        // 幂等保护：请求发出去到 agent 应答之间，玩家可能自己点掉了这一格。
        if (!before.Stocked)
            return new ShopApplyResult
            {
                Ok = false,
                How = "already-sold",
                Settled = true,
                Why = "应答之前就已经卖掉了（不重复购买）",
            };

        // 金币不够：不调游戏，直接把「当前金币 vs 这件价格」摊开说。
        if (before.Gold < before.Price)
            return new ShopApplyResult
            {
                Ok = false,
                How = "gold",
                Settled = true,
                Why = $"金币不够：当前金币 {before.Gold}，这件价格 {before.Price}",
            };

        // 药水槽满的时候就别装没事人：买了也放不下，游戏会返回 FailureSpace（格子只抖一下）。
        if (entry is MerchantPotionEntry)
        {
            var full = false;
            var used = 0;
            var max = 0;
            try
            {
                await BridgePump.OnMain(() =>
                {
                    full = Potions.SlotsFull(out used, out max, out _);
                    return true;
                }, 3000);
            }
            catch { }

            if (full)
                return new ShopApplyResult
                {
                    Ok = false,
                    How = "potions-full",
                    Settled = true,
                    Why = $"药水槽已满（{used}/{max}），这件药水放不下",
                };
        }

        ShopStart start;
        try { start = await BridgePump.OnMain(() => StartShopApply(node, candidate), 5000); }
        catch (Exception e) { return new ShopApplyResult { Ok = false, Why = "启动购买失败：" + e.Message }; }

        if (start?.Task == null)
            return new ShopApplyResult { Ok = false, How = start?.How ?? "none", Why = start?.Why ?? "没拿到游戏的购买任务" };

        var (state, accepted) = await WaitShopPurchase(start.Task, ShopSettleMs, candidate.Key);

        if (state == "timeout")
        {
            // 游戏受理了，但还在等下一步。目前只有「卡牌移除服务」会这样（等选牌），
            // 没结算不算失败，如实标出来；别的情况只报「没在时限内结算」。
            var removal = candidate.Entry is MerchantCardRemovalEntry;
            return new ShopApplyResult
            {
                Ok = true,
                How = start.How,
                Settled = false,
                WaitingChoice = removal,
                Visual = removal ? "card-choice" : "shop-bought",
                Why = removal
                    ? "游戏还在等下一步（选牌），金币要等选完才扣"
                    : $"游戏受理了，但 {ShopSettleMs}ms 内没有结算（看 BridgeLog）",
            };
        }

        if (state == "faulted")
            return new ShopApplyResult
            {
                Ok = false,
                How = start.How,
                Why = "游戏的购买任务抛异常（看 BridgeLog）",
            };

        ShopView after;
        try { after = await BridgePump.OnMain(() => ReadShopView(node, entry), 3000); }
        catch (Exception e) { return new ShopApplyResult { Ok = false, How = start.How, Why = "读取购买后的商店状态失败：" + e.Message }; }
        if (after == null || !after.Ok)
            return new ShopApplyResult { Ok = false, How = start.How, Why = "读取购买后的商店状态失败（超时或异常）" };

        if (!accepted)
            return new ShopApplyResult
            {
                Ok = false,
                How = start.How,
                Settled = true,
                Why = $"游戏没成交（返回 false）：购买前金币 {before.Gold}，价格 {before.Price}，" +
                      $"购买后金币 {after.Gold}，这一格{(after.Stocked ? "仍待售" : "已售出")}",
            };

        var spent = before.Gold - after.Gold;
        var sameItemStillSold = after.Stocked &&
                                string.Equals(before.Identity, after.Identity, StringComparison.Ordinal);

        if (spent < before.Price)
            return new ShopApplyResult
            {
                Ok = false,
                How = start.How,
                Settled = true,
                Why = $"金币没有按价格减少：购买前 {before.Gold}，购买后 {after.Gold}，价格 {before.Price}",
            };

        if (sameItemStillSold)
            return new ShopApplyResult
            {
                Ok = false,
                How = start.How,
                Settled = true,
                Why = $"金币减少了（{before.Gold} → {after.Gold}），但这一格还是同一件商品在售（{candidate.Title}）",
            };

        return new ShopApplyResult
        {
            Ok = true,
            How = start.How,
            Settled = true,
            Visual = "shop-bought",
            Why = $"花费 {spent}（{before.Gold} → {after.Gold}）",
        };
    }

    /// <summary>主线程：这件商品现在什么状态（金币 / 价格 / 是否待售 / 卖的是哪一件）。</summary>
    private static ShopView ReadShopView(NMerchantInventory node, MerchantEntry entry)
    {
        var view = new ShopView();
        try
        {
            var inventory = ReadShopInventory(node);
            view.Gold = ShopGold(inventory);
            if (entry != null)
            {
                view.Stocked = entry.IsStocked;
                view.Price = entry.Cost;
                view.Identity = ShopItemIdentity(entry);
            }
            view.Ok = true;
        }
        catch (Exception e)
        {
            view.Why = e.GetType().Name + ": " + e.Message;
        }
        return view;
    }

    /// <summary>这一格现在摆的是哪一件（用来区分「真的卖掉了」和「RestockAfterPurchase 换了新货」）。</summary>
    private static string ShopItemIdentity(MerchantEntry entry)
    {
        if (entry == null) return "";
        try
        {
            switch (entry)
            {
                case MerchantCardEntry cardEntry:
                    {
                        var card = SafeCard(cardEntry);
                        return "card:" + (SafeText(() => card?.Id?.ToString()) ?? "?");
                    }
                case MerchantRelicEntry relicEntry:
                    {
                        var relic = SafeModel(() => relicEntry.Model);
                        return "relic:" + (SafeText(() => relic?.Id?.ToString()) ?? "?");
                    }
                case MerchantPotionEntry potionEntry:
                    {
                        var potion = SafeModel(() => potionEntry.Model);
                        return "potion:" + (SafeText(() => potion?.Id?.ToString()) ?? "?");
                    }
                case MerchantCardRemovalEntry removalEntry:
                    return "removal:" + SafeInt(() => removalEntry.Used ? 1 : 0);
            }
        }
        catch { }
        return entry.GetType().Name;
    }

    /// <summary>
    /// 主线程：把这次购买交给游戏自己的入口。两条路，优先格子自己的点击处理方法
    /// （就是 <c>_GuiInput</c> → <c>OnMouseReleased</c> → <c>OnSelected</c> 那条）。
    /// </summary>
    private static ShopStart StartShopApply(NMerchantInventory node, Candidate candidate)
    {
        var entry = candidate?.Entry;
        if (entry == null) return new ShopStart { Why = "候选里没有 MerchantEntry 对象" };

        var inventory = ReadShopInventory(node);
        if (inventory == null) return new ShopStart { Why = "读不到 MerchantInventory（NMerchantInventory.Inventory 为空）" };

        // ① 格子自己的处理方法：鼠标松开时调的就是 NMerchantSlot.OnSelected()，
        //    它再走 OnTryPurchase(Inventory) → Entry.OnTryPurchaseWrapper。
        var slot = candidate.Slot;
        if (slot != null && GodotObject.IsInstanceValid(slot) && slot.IsInsideTree())
        {
            var onSelected = FindMethod(slot.GetType(), "OnSelected", 0);
            if (onSelected != null)
            {
                try
                {
                    if (onSelected.Invoke(slot, null) is Task task)
                        return new ShopStart { Task = task, How = "shop-slot" };
                    BridgeLog.Warn("shop: NMerchantSlot.OnSelected 没返回 Task，改用模型层");
                }
                catch (Exception e)
                {
                    BridgeLog.Warn($"shop: NMerchantSlot.OnSelected 调失败（{e.GetType().Name}）：{e.Message}，改用模型层");
                }
            }
            else
            {
                BridgeLog.Warn("shop: NMerchantSlot 上没找到 OnSelected()，改用模型层");
            }
        }

        // ② 模型层兜底：跟格子同一条购买入口（OnSelected → OnTryPurchase → 这里）。
        try
        {
            if (entry is MerchantCardRemovalEntry removal)
                return new ShopStart
                {
                    Task = removal.OnTryPurchaseWrapper(inventory, false, true),
                    How = "shop-model",
                };

            var onTry = FindMethod(entry.GetType(), "OnTryPurchaseWrapper", 2);
            if (onTry == null)
                return new ShopStart { Why = "MerchantEntry 上没找到 OnTryPurchaseWrapper(inventory, ignoreCost)" };

            if (onTry.Invoke(entry, new object[] { inventory, false }) is Task<bool> task)
                return new ShopStart { Task = task, How = "shop-model" };

            return new ShopStart { Why = "OnTryPurchaseWrapper 没返回 Task<bool>" };
        }
        catch (Exception e)
        {
            return new ShopStart { Why = "OnTryPurchaseWrapper 抛了：" + e.Message };
        }
    }

    /// <summary>等一个购买任务，但最多等 ms；返回 (done/timeout/faulted, 任务布尔结果)。</summary>
    private static async Task<(string State, bool Result)> WaitShopPurchase(Task task, int ms, string what)
    {
        if (task == null) return ("done", false);
        try
        {
            var winner = await Task.WhenAny(task, Task.Delay(ms));
            if (winner != task)
            {
                // 超时也要观察异常，否则会变成 UnobservedTaskException。
                _ = WatchShopSafely(task, what);
                return ("timeout", false);
            }
            if (task is Task<bool> boolTask) return ("done", await boolTask);
            await task;
            return ("done", true);
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"shop: {what} 的购买任务抛了（{e.GetType().Name}）：{e.Message}");
            return ("faulted", false);
        }
    }

    /// <summary>只为「把异常吃掉并写日志」：不改变任何行为。</summary>
    private static async Task WatchShopSafely(Task task, string what)
    {
        try { await task; }
        catch (Exception e)
        {
            BridgeLog.Warn($"shop: {what} 后续抛了（{e.GetType().Name}）：{e.Message}");
        }
    }

    // =================================================================== 药水目标通道（PotionModel.TargetType）

    /*
     * 什么时候会有这条请求：
     *   POST /potion/use 不带 target、这瓶药水的 TargetType 又需要选目标、而且当前不在战斗里
     *   （典型是「污浊药水打商人」）。这时 **不立刻喝**，而是建一条 pending 让 agent 挑目标。
     *
     * 为什么非战斗里要单开一条：战斗里的目标就是 `CombatState.HittableEnemies[target]`（下标法，
     * 老用法保持不变）；非战斗里没有敌人，`TargetType.TargetedNoCreature` 的目标是**商人按钮**
     * （`FoulPotion.GetFoulPotionMerchantTarget(room)` 返回 `Item1`），根本不是 Creature，
     * 所以老代码那个 `targetIndex -> FindMerchant()` 永远解析不出东西。
     *
     * 应答：
     *   * picks:["商人#1"] → 拿这个目标真的使用药水（Potions.UseResolved），等槽位空掉才算 ok；
     *   * picks:[] 或 picks:["取消#1"] → 不喝，药水留在槽里。
     */

    /// <summary>agent 可选的药水目标（目前只有商人；留成列表是为了以后能加队友/敌人）。</summary>
    public sealed class PotionTargetOption
    {
        /// <summary>候选标题（也是 key 的前半截）。</summary>
        public string Title;

        /// <summary>true = 商人（<c>TargetedNoCreature</c>，模型层的 target 就是 null）。</summary>
        public bool Merchant;

        /// <summary>联机里投队友用的 Creature 目标（单机恒为 null）。</summary>
        public MegaCrit.Sts2.Core.Entities.Creatures.Creature Creature;
    }

    /// <summary>
    /// 非战斗场景里「这瓶药水需要选目标」→ 建一条 <c>kind:"potion"</c> 的 pending，
    /// 返回 <c>/potion/use</c> 的应答（<c>{"ok":false,"needTarget":true,"request":{...}}</c>）。
    /// 一个合法目标都没有时返回明确失败 —— 绝不再「喝了但没打中」还报 ok:true。
    /// 必须在主线程调用（Potions.Probe 是从主线程入口进来的）。
    /// </summary>
    public static Dictionary<string, object> BeginPotionTargetRequest(
        string potionKey, string potionTitle, string potionDescription,
        IReadOnlyList<PotionTargetOption> targets)
    {
        if (targets == null || targets.Count == 0)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "这瓶药水需要目标，但当前场景没有可选的商人/敌人目标",
                ["usable"] = false,
            };
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new List<Candidate>();
        var merchant = false;
        foreach (var target in targets)
        {
            if (target == null) continue;
            var title = string.IsNullOrWhiteSpace(target.Title) ? "目标" : target.Title.Trim();
            candidates.Add(new Candidate
            {
                Key = NextKey(counts, title),
                Title = title,
                Upgraded = false,
                Cost = -1,
                PotionMerchant = target.Merchant,
                TargetCreature = target.Creature,
                Enabled = true,
            });
            if (target.Merchant) merchant = true;
        }

        // 取消永远是最后一个候选（key 固定「取消#1」）。
        candidates.Add(new Candidate
        {
            Key = PotionCancelKey,
            Title = PotionCancelTitle,
            Upgraded = false,
            Cost = -1,
            PotionCancel = true,
            Enabled = true,
        });

        var state = new PendingSelection
        {
            Id = NextId(),
            Min = 0,                       // picks:[] = 取消
            Max = 1,                       // 一次只会挑一个目标
            Kind = PotionKind,
            Caller = "PotionModel",
            Prompt = (string.IsNullOrWhiteSpace(potionTitle) ? "药水" : potionTitle.Trim()) + "：选一个目标",
            Cards = candidates,
            PotionKey = potionKey,
            PotionTitle = potionTitle,
            PotionDescription = potionDescription,
            PotionMerchantTarget = merchant,
            CreatedUtc = DateTime.UtcNow,
        };

        _potionPending = state;
        BridgeLog.Info($"potion: 新目标请求 id={state.Id} key={potionKey} " +
                       $"keys={string.Join("、", candidates.Select(c => c.Key))}");

        return new Dictionary<string, object>
        {
            ["ok"] = false,
            ["needTarget"] = true,
            ["used"] = null,
            ["confirmed"] = false,
            ["request"] = DescribePotionPendingJson(state),
        };
    }

    private static Dictionary<string, object> DescribePotionPendingJson(PendingSelection pending)
    {
        var dict = new Dictionary<string, object>
        {
            ["id"] = pending.Id,
            ["kind"] = PotionKind,
            ["stage"] = PotionStageTarget,
            ["prompt"] = pending.Prompt ?? "药水：选一个目标",
            ["min"] = 1,                        // 要么给一个目标，要么 picks:[] / 「取消#1」
            ["max"] = 1,
            ["requireConfirm"] = false,
            ["cancelable"] = true,
            ["caller"] = pending.Caller,
            ["confidence"] = "high",            // 目标来自房间模型（MerchantRoom / FakeMerchant），不是扫类名
            ["animated"] = true,
            ["animation"] = PotionTargetAnimationPlan,
            ["route"] = "potion",
            ["potion"] = new Dictionary<string, object>
            {
                ["key"] = pending.PotionKey,
                ["title"] = pending.PotionTitle ?? "",
                ["description"] = pending.PotionDescription ?? "",
            },
            ["candidates"] = pending.Cards.Select(CandidateJson).ToList(),
            ["next"] = "picks:[\"商人#1\"] = 对这瓶的目标用掉；picks:[] 或 picks:[\"取消#1\"] = 不喝（药水留在槽里）",
        };
        return dict;
    }

    /// <summary>
    /// 药水目标请求的发现（必须在主线程调用，由 /request 触发）。
    /// 药水没了、商人没了、或者这一局结束了 → 作废并返回 null。
    /// </summary>
    private static Dictionary<string, object> DescribePotionPending()
    {
        var pending = _potionPending;
        if (pending == null) return null;

        bool still;
        string why = null;
        try { still = Potions.TargetRequestStillValid(pending.PotionKey, pending.PotionMerchantTarget, out why); }
        catch (Exception e) { still = false; why = e.GetType().Name + "：" + e.Message; }

        if (!still)
        {
            DropPotionPending(why ?? "请求已经失效");
            return null;
        }

        return DescribePotionPendingJson(pending);
    }

    /// <summary>把待答的药水请求摘掉（不动游戏状态，也不碰药水）。</summary>
    private static void DropPotionPending(string why)
    {
        var pending = _potionPending;
        if (pending == null) return;
        _potionPending = null;
        BridgeLog.Info($"potion: 清掉请求 id={pending.Id}（{why}）");
    }

    /// <summary>
    /// 药水目标请求的应答入口（包裹层）：校验 → 用目标喝掉 / 取消。
    /// 任何 ok:false 都把请求挂回槽里（见 <see cref="RestorePotionAfterReject"/>），所以能改一道答案再答。
    /// </summary>
    private static async Task<Dictionary<string, object>> AnswerPotion(PendingSelection pending, string[] picks)
    {
        picks ??= Array.Empty<string>();
        if (picks.Length > 1)
            return Fail($"一次只能选 1 个目标，你给了 {picks.Length} 个");

        Candidate chosen = null;
        if (picks.Length == 1)
        {
            var key = (picks[0] ?? "").Trim();
            chosen = pending.Cards.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));
            if (chosen == null)
                return Fail($"{key} 不在候选里（可选：{DescribeKeys(pending.Cards)}；picks:[] = 取消）");
            if (chosen.PotionCancel) chosen = null;
        }

        var sw = Stopwatch.StartNew();

        // 先从槽里摘掉：使用在跑的时候不能再被 /request 看到，也不能被答第二次。
        _potionPending = null;
        _lastAnsweredId = pending.Id;

        if (chosen == null)
        {
            BridgeLog.Info($"potion answered id={pending.Id} 取消（药水留在槽里）");
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = "screen",
                ["route"] = "potion",
                ["stage"] = PotionStageTarget,
                ["applied"] = "（取消，没喝）",
                ["how"] = "potion-cancel",
                ["animated"] = false,
                ["waitedMs"] = sw.ElapsedMilliseconds,
                ["visual"] = PotionVisualCanceled,
                ["used"] = null,
                ["target"] = null,
                ["canceled"] = true,
                ["note"] = "药水还在槽里，想喝就重新 POST /potion/use",
            };
        }

        _animating = true;
        Dictionary<string, object> result;
        try { result = await ApplyPotionTarget(pending, chosen, sw); }
        catch (Exception e)
        {
            BridgeLog.Warn($"potion: 应答 id={pending.Id} 抛异常（{e.GetType().Name}）：{e.Message}");
            result = Fail("使用药水时出错：" + e.Message);
        }
        finally { _animating = false; }

        if (!IsTrue(result, "ok")) RestorePotionAfterReject(pending);
        return result;
    }

    /// <summary>
    /// 真正使用：把 agent 选的那个目标交给 <see cref="Potions.UseResolved"/>（它会再复查一次
    /// 药水还在不在、目标还在不在，然后调游戏的 <c>EnqueueManualUse</c> 并等槽位空掉）。
    /// 调用方已经摘掉槽、置了 _animating。
    /// </summary>
    private static async Task<Dictionary<string, object>> ApplyPotionTarget(
        PendingSelection pending, Candidate chosen, Stopwatch sw)
    {
        var used = await Potions.UseResolved(pending.PotionKey, chosen.PotionMerchant, chosen.Title);

        var waited = sw.ElapsedMilliseconds;
        if (!IsTrue(used, "ok"))
        {
            // 药水没能用掉：把游戏的失败原样带回去（不谎报），并把请求留给它自己重答。
            if (used != null)
            {
                used["route"] = "potion";
                used["target"] = chosen.Title;
                used["waitedMs"] = waited;
            }
            BridgeLog.Warn($"potion answered id={pending.Id} 目标={chosen.Title} 失败："
                           + (used != null && used.TryGetValue("error", out var err) ? err?.ToString() : "?"));
            return used ?? Fail("使用药水失败（没有返回结果）");
        }

        BridgeLog.Info($"potion answered id={pending.Id} used={used.GetValueOrDefault("used")} " +
                       $"target={chosen.Title} waitedMs={waited}");

        var ok = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["source"] = "screen",
            ["route"] = "potion",
            ["stage"] = PotionStageTarget,
            ["applied"] = (used.GetValueOrDefault("used") ?? pending.PotionTitle ?? "药水") + " → " + chosen.Title,
            ["how"] = "potion-target",
            ["animated"] = true,
            ["waitedMs"] = waited,
            ["visual"] = PotionVisualUsed,
            ["used"] = used.GetValueOrDefault("used"),
            ["target"] = chosen.Title,
            ["confirmed"] = used.GetValueOrDefault("confirmed"),
            ["canceled"] = false,
        };
        foreach (var kv in used)
        {
            if (kv.Key == "ok") continue;
            if (ok.ContainsKey(kv.Key)) continue;
            ok[kv.Key] = kv.Value;
        }
        return ok;
    }

    /// <summary>ok:false 时把药水请求挂回槽里，让 agent 能改一道答案再答同一个 id。</summary>
    private static void RestorePotionAfterReject(PendingSelection pending)
    {
        if (pending == null) return;
        if (_lastAnsweredId == pending.Id) _lastAnsweredId = -1;
        _potionPending = pending;
        BridgeLog.Info($"potion: 请求 id={pending.Id} 没被接受，恢复成可再答（已挂回 _potionPending）");
    }

    // =================================================================== 宝箱通道（NTreasureRoom + TreasureRoomRelicSynchronizer）

    /*
     * 宝箱房（room=Treasure）是**两阶段**决策，一条应答只做一件事：
     *
     *   阶段 1 stage="chest-closed"（箱子还没开）：只问「要不要打开」。
     *     min = max = 1、cancelable = true，候选只有一个 action「打开宝箱#1」，**不暴露箱里的东西**
     *     （虽然 _currentRelics 进房间时就已经生成了，但那不是给 agent 看的）。
     *     应答 = NTreasureRoom.OpenChest()，**只开箱、不拿遗物**；picks:[] = 不打开（箱子保持关闭）。
     *
     *   阶段 2 stage="relics"（箱子已开、遗物格子摆出来了）：问「要拿走哪件」。
     *     min = 0、max = 遗物数（单人恒为 1）、cancelable = true；picks:[] = 都不拿、直接离开。
     *
     * 两个阶段是两个 id（stage 进签名）：阶段 1 答完 → 箱子开、遗物摆出来 → 下一次 /request
     * 才会拿到阶段 2 的内容。开箱动画（3s 左右）期间 _isRelicCollectionOpen 还是 false，
     * 那时候**不发**阶段 2 请求（候选还没摆出来）。
     *
     * 数据源（都在主线程读，**绝不写死遗物**）：
     *   * 房间节点：MegaCrit.Sts2.Core.Nodes.Rooms.NTreasureRoom。
     *     NRun.Instance.TreasureRoom = NRun 房间容器里**当前**场景（不在宝箱房时返回 null），
     *     所以不会读到上一个房间的僵尸节点（跟 NRestSiteRoom / NEventRoom 同一个套路）。
     *   * 箱子开没开：NTreasureRoom 的 private bool 字段 _hasChestBeenOpened
     *     （MetaPriv 核对过：NTreasureRoom 上就是这两个 bool，没有同名属性）。
     *     「遗物格子摆出来了」= NTreasureRoom._isRelicCollectionOpen。
     *     阶段判定 = !_hasChestBeenOpened → 阶段 1；_hasChestBeenOpened && _isRelicCollectionOpen → 阶段 2；
     *     _hasChestBeenOpened && !_isRelicCollectionOpen → 开箱动画还没播完，**不发请求**。
     *   * 遗物集合：RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics
     *     : IReadOnlyList<RelicModel>（private _currentRelics）。这份列表是**进房间那一刻**
     *     TreasureRoom.EnterInternal 里 BeginRelicPicking() 生成的：对每个玩家
     *     Hook.ShouldGenerateTreasure → RelicFactory.RollRarity → RelicGrabBag.PullFromFront
     *     （教学遗物优先，拉空才 FallbackRelic）→ 塞进 _currentRelics。
     *     所以「宝箱里有什么」只认模型，不认界面，也不需要任何特判。
     *   * 什么时候算「已经拿过 / 空箱」：AwardRelics() 之后 EndRelicVoting() 会把 _currentRelics
     *     置 null；空箱走 CompleteWithNoRelics() 也置 null。所以 CurrentRelics == null（或 Count == 0）
     *     就等于「没有要拿的」——这时**不发请求**。
     *   * 每件遗物：RelicModel（Title / DynamicDescription / Description / Id）。遗物的 Description
     *     是 private getter，沿用商店通道那套容错读取：DynamicText(model, "DynamicDescription", "Description")。
     *   * 宝箱 / 格子节点：NTreasureRoom 的 private _relicCollection : NTreasureRoomRelicCollection；
     *     遗物格子 _holdersInUse : List<NTreasureRoomRelicHolder>（单人只有 SingleplayerRelicHolder，
     *     Index = 0，由 InitializeRelics() 在开箱时摆好）。
     *
     * 阶段 1 应答（不模拟鼠标、不点坐标）：
     *   开箱：NTreasureRoom.OpenChest()（private，返回 Task）—— 就是 OnChestButtonReleased 里
     *   OnRelease 调的那一句：banner 滑入 → 关「前进」→ 宝箱开盖皮肤/音效 →
     *   await DoNormalRewards()（金币，返回数量给金光粒子）→ await DoExtraRewardsIfNeeded() →
     *   _relicCollection.InitializeRelics()（摆遗物格子）+ AnimIn() → _isRelicCollectionOpen = true
     *   →（单人）UpdateText(SkipLoc) + EnableSkipAfterDelay(2.5s) → _hasChestBeenOpened = true
     *   → await RelicPickingBegan()（**卡在这里等玩家拿/跳过**）……
     *   它是个长任务，**不能 await 到底**（否则 /respond 永不返回）：只把它推起来，
     *   再轮询等 _isRelicCollectionOpen 变 true。箱子按钮已经被禁用（_chestButton.Disable()）
     *   说明玩家自己在开箱：这时**不**调第二次 OpenChest（会重复发金币、重复摆格子），只等格子摆好。
     *
     * 阶段 2 应答：
     *   ① 领取：NTreasureRoomRelicCollection.PickRelic(holder)（private，反射）——玩家松开鼠标走的
     *      就是它（<InitializeRelics>b__21_0 → PickRelic(SingleplayerRelicHolder)）：
     *      核对「开箱满 200ms」→ RunManager.Instance.TreasureRoomRelicSynchronizer.
     *      PickRelicLocally(holder.Index) → PickRelicAction → OnPicked → 票到齐 →
     *      AwardRelics()（发 RelicsAwarded 信号）+ EndRelicVoting()。
     *      拿不到格子时退化成 PickRelicLocally(index)。
     *      遗物真正进背包发生在 RelicsAwarded → NTreasureRoomRelicCollection.AnimateRelicAwards →
     *      **RelicCmd.Obtain** + NRelicInventory.AnimateRelic（遗物飞进遗物栏）。
     *   ② 两个入口都拿不到才算失败（ok:false，不谎报）。
     *
     * 阶段 2 的不拿（picks:[]）= 走游戏自己的「跳过」，两句话都不能省：
     *   ① TreasureRoomRelicSynchronizer.SkipRelicLocally() —— 就是玩家点「跳过」时
     *     NTreasureRoom.OnProceedButtonPressed 里 IsSkip 分支的第一句（= PickRelicLocally(null)）：
     *     PickRelicAction(null) → OnPicked(player, null)，单人会置 _singleplayerSkipped = true 然后
     *     **直接 return**（不发 RelicsAwarded、_currentRelics 仍非 null）。
     *   ② 真正的收尾在离开房间：TreasureRoom.Exit() → synchronizer.OnRoomExited() → 读到
     *     _singleplayerSkipped 才 EndRelicVoting()（_currentRelics = null、_singleplayerSkipped = false）。
     *     离开走 /proceed（按 NProceedButton）= OnProceedButtonPressed，它自己还会再调一次
     *     SkipRelicLocally() 再 ProceedFromTerminalRewardsScreen()，重复调用是无害的。
     *   ⇒ 所以我们只做 ①（并且等 _singleplayerSkipped 真的落地）、并等「前进」按钮亮起来
     *     （单人开箱时 EnableSkipAfterDelay(2.5s) 会 Enable 它），然后让 agent 用 /proceed 走人。
     *   抑制重复请求：_singleplayerSkipped 为 true 期间**不再发阶段 2 请求**（见 DescribeTreasurePending），
     *   否则遗物还挂在 _currentRelics 里、agent 会被反复问同一个决定。
     *
     * 验证（每次都做，不靠猜）：领取之后必须命中「本地玩家的遗物栏里出现了这件遗物」或
     *   「遗物数 +1」之一（RelicCmd.Obtain 会加进去，最多等 TreasureSettleMs）；
     *   一条都不命中就 ok:false，并把前后两组事实写进 error（领取前/后遗物数、宝箱里那件还在不在）。
     *   跳过之后必须命中「_singleplayerSkipped == true」或「CurrentRelics 已清」之一，否则 ok:false。
     */

    /// <summary>一次领取 / 一次阶段判定时的现场核对结果（都在主线程读）。</summary>
    private sealed class TreasureView
    {
        public bool Ok;
        public string Why;
        public bool InChest;        // 这件遗物还在 CurrentRelics 里（还没被领走）
        public bool Obtained;       // 本地玩家的遗物栏里已经有它
        public int RelicCount;
        public int Gold;
        public bool ChestOpened;    // NTreasureRoom._hasChestBeenOpened
        public bool CollectionOpen; // NTreasureRoom._isRelicCollectionOpen（遗物格子摆着）
        public bool ChestEnabled;   // _chestButton 还能点 = 还没人开始开箱
        public bool SkipVoted;      // TreasureRoomRelicSynchronizer._singleplayerSkipped（已经投了「跳过」）
        public bool RelicsCleared;  // CurrentRelics 已经清掉（遗物结算完 / 跳过被 EndRelicVoting 收尾）
        public bool ProceedReady;   // _proceedButton 现在可用（/proceed 能把人带出去）
        public bool ProceedIsSkip;  // _proceedButton.IsSkip：文字还是「跳过」而不是「前进」
    }

    /// <summary>单项领取的结果：成没成 / 走了哪条路 / 结算完没 / 失败原因。</summary>
    private sealed class TreasureApplyResult
    {
        public bool Ok;
        public string How = "none";
        public bool Settled;
        public string Why;
        public string Visual;
    }

    /// <summary>主线程里「把这次领取推起来」的结果。</summary>
    private sealed class TreasureStart
    {
        public string How = "none";
        public string Why;
    }

    /// <summary>开箱这一步的结果：Reason 非空表示开不了（事实写清楚）。</summary>
    private sealed class TreasureOpenResult
    {
        public string Reason;
        public bool OpenedNow;
    }

    /// <summary>
    /// 宝箱的发现 + pending 构造（必须在主线程调用，由 /request 触发）。
    /// 返回 null 表示「现在没有要答的宝箱请求」：不在宝箱房 / 空箱 / 已经拿过 /
    /// 已经跳过（等 /proceed 离开）/ 开箱动画还没播完。
    /// </summary>
    private static Dictionary<string, object> DescribeTreasurePending()
    {
        if (Engine.GetMainLoop() is not SceneTree) return null;

        var room = FindTreasureRoom();
        if (room == null)
        {
            // 离开宝箱房 = 这次领取结束：待答请求一起清掉，别留僵尸请求。
            DropTreasurePending("宝箱房界面不在场景树里（已经离开宝箱房）");
            return null;
        }

        // 地图已经开着（刚按了「前进」，或者玩家自己打开地图）：这一刻不是「等玩家做决定」。
        if (MapScreenOpen())
        {
            DropTreasurePending("地图已经打开（宝箱房已经结束）");
            return null;
        }

        var sync = TreasureRelicSynchronizer();
        if (sync == null)
        {
            DropTreasurePending("RunManager.TreasureRoomRelicSynchronizer 不可用");
            return null;
        }

        // 已经投过「跳过」：遗物不要了，房间在等 /proceed 走人。
        // 单人 SkipRelicLocally() 只置 _singleplayerSkipped，_currentRelics 要到
        // TreasureRoom.Exit → OnRoomExited → EndRelicVoting 才清，这段时间再发请求会让 agent
        // 反复决定同一件遗物（阶段 2 已经答过 picks:[] 了）。DropTreasurePending 只在真有请求时记日志。
        if (TreasureSkipVoted(sync))
        {
            DropTreasurePending("已经按过「跳过」（_singleplayerSkipped = true），等离开宝箱房");
            return null;
        }

        var relics = CurrentTreasureRelics(sync);
        if (relics.Count == 0)
        {
            // 空箱 / 已经拿走：CurrentRelics 被 EndRelicVoting / CompleteWithNoRelics 置空。
            DropTreasurePending("宝箱里没有待拿的遗物（空箱，或者已经拿走了）");
            return null;
        }

        // ---- 阶段判定（两个 bool 都来自 NTreasureRoom 的 private 字段，MetaPriv 核对过）----
        var view = ReadTreasureRoomView(room);
        if (view == null || !view.Ok)
        {
            DropTreasurePending("读不出宝箱状态（NTreasureRoom 已失效？"
                                + (view?.Why ?? "?") + "）");
            return null;
        }

        string stage;
        List<Candidate> candidates;
        // 先看「遗物格子摆出来了没有」：它是更强的信号（OpenChest 里先置 _isRelicCollectionOpen、
        // 再置 _hasChestBeenOpened，两者之间没有 await，但按这个顺序判断更稳）。
        if (view.CollectionOpen)
        {
            // 阶段 2：箱子已开、遗物格子摆出来了 —— 这时才给出真正的内容。
            stage = TreasureStageRelics;
            candidates = CollectTreasureCandidates(relics);
            if (candidates.Count == 0)
            {
                DropTreasurePending("宝箱里的遗物读不出模型/名字");
                return null;
            }
        }
        else if (!view.ChestOpened)
        {
            // 阶段 1：箱子还关着 —— 只问「要不要打开」，不暴露箱里的东西。
            stage = TreasureStageChestClosed;
            candidates = new List<Candidate> { TreasureOpenCandidate() };
        }
        else
        {
            // 箱子开过、但遗物格子还没摆出来 = 开箱动画还在播（3s 左右）。
            // 这时候候选还没摆出来，**绝不发**阶段 2 请求，这一轮就当没有请求。
            DropTreasurePending("开箱动画还没播完（_isRelicCollectionOpen 还是 false），等下一轮");
            return null;
        }

        var prompt = string.Equals(stage, TreasureStageChestClosed, StringComparison.Ordinal)
            ? TreasureOpenPrompt
            : TreasurePrompt;
        var min = string.Equals(stage, TreasureStageChestClosed, StringComparison.Ordinal) ? 1 : 0;
        var max = string.Equals(stage, TreasureStageChestClosed, StringComparison.Ordinal)
            ? 1
            : candidates.Count;

        var signature = TreasureSignature(room, stage, candidates);
        var state = _treasurePending;
        if (state == null || !string.Equals(state.Signature, signature, StringComparison.Ordinal))
        {
            state = new PendingSelection
            {
                Id = NextId(),
                Min = min,                      // 阶段 1：必须明确选「打开」；阶段 2：不拿永远合法
                Max = max,                      // 阶段 1 恒为 1；阶段 2 = 宝箱里的遗物数（单人恒为 1）
                Kind = "treasure",
                Caller = "NTreasureRoom",
                Prompt = prompt,
                Screen = room,
                Cards = candidates,
                TreasureChestOpened = view.ChestOpened,
                TreasureStage = stage,
                CreatedUtc = DateTime.UtcNow,
            };
            state.Signature = signature;
            _treasurePending = state;
            BridgeLog.Info($"treasure: 新请求 id={state.Id} stage={stage} min={min} max={max} " +
                           $"chestOpened={view.ChestOpened} collectionOpen={view.CollectionOpen} " +
                           $"keys={string.Join("、", candidates.Select(c => c.Key))}");
        }
        else
        {
            // 同一份请求：只刷新节点引用和现场事实（签名变了才会换 id）。
            state.Screen = room;
            state.Min = min;
            state.Max = max;
            state.Prompt = prompt;
            state.Cards = candidates;
            state.TreasureChestOpened = view.ChestOpened;
            state.TreasureStage = stage;
        }

        return DescribeTreasurePendingJson(state);
    }

    /// <summary>
    /// 阶段 1 的唯一候选：「打开宝箱#1」。它是**动作**不是遗物 —— 不读 CurrentRelics，
    /// 所以箱里的内容在这个阶段完全不会泄露。
    /// </summary>
    private static Candidate TreasureOpenCandidate() => new()
    {
        Key = TreasureOpenKey,
        Title = TreasureOpenTitle,
        Upgraded = false,
        Cost = -1,
        Detail = "打开后会看到里面的东西",
        TreasureAction = true,
        TreasureIndex = -1,
        Enabled = true,
    };

    private static Dictionary<string, object> DescribeTreasurePendingJson(PendingSelection pending)
    {
        var closed = string.Equals(pending.TreasureStage, TreasureStageChestClosed, StringComparison.Ordinal);
        return new Dictionary<string, object>
        {
            ["id"] = pending.Id,
            ["kind"] = "treasure",
            // ★ 新增字段：两阶段（chest-closed / relics）。既有字段名与语义都没动。
            ["stage"] = closed ? TreasureStageChestClosed : TreasureStageRelics,
            ["prompt"] = pending.Prompt ?? (closed ? TreasureOpenPrompt : TreasurePrompt),
            ["min"] = pending.Min,
            ["max"] = pending.Max,
            ["requireConfirm"] = false,
            ["cancelable"] = true,               // 两个阶段都允许 picks:[]：阶段 1 = 不打开，阶段 2 = 都不拿
            ["caller"] = pending.Caller,
            ["confidence"] = "high",             // 阶段判定读 NTreasureRoom 的两个 bool；候选来自模型
            ["animated"] = true,
            ["animation"] = closed ? TreasureOpenAnimationPlan : TreasureAnimationPlan,
            ["route"] = "treasure",
            ["chestOpened"] = pending.TreasureChestOpened,   // 箱子开没开（不影响候选/key）
            ["candidates"] = pending.Cards.Select(CandidateJson).ToList(),
        };
    }

    /// <summary>场景树里当前的宝箱房（不在宝箱房时 null）。</summary>
    private static NTreasureRoom FindTreasureRoom()
    {
        try
        {
            var room = MegaCrit.Sts2.Core.Nodes.NRun.Instance?.TreasureRoom;
            if (room != null && GodotObject.IsInstanceValid(room) && room.IsInsideTree()) return room;
        }
        catch { }

        // Instance 读不到时再扫一遍（NRun 还没把场景挂上之类的边界情况）。
        if (Engine.GetMainLoop() is not SceneTree tree) return null;
        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 40000)
        {
            var node = stack.Pop();
            if (node == null) continue;
            if (node is NTreasureRoom found)
            {
                try { if (GodotObject.IsInstanceValid(found) && found.IsInsideTree()) return found; } catch { }
            }
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return null;
    }

    /// <summary>数据层：遗物领取的同步器（RunManager 上的那一份）。</summary>
    private static TreasureRoomRelicSynchronizer TreasureRelicSynchronizer()
    {
        try { return RunManager.Instance?.TreasureRoomRelicSynchronizer; }
        catch { return null; }
    }

    /// <summary>宝箱里还没被拿走的遗物（CurrentRelics 为 null 时返回空表 = 空箱 / 已经拿过）。</summary>
    private static List<RelicModel> CurrentTreasureRelics(TreasureRoomRelicSynchronizer sync)
    {
        var list = new List<RelicModel>();
        if (sync == null) return list;
        try
        {
            var relics = sync.CurrentRelics;
            if (relics == null) return list;
            foreach (var relic in relics) if (relic != null) list.Add(relic);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("treasure: 读 TreasureRoomRelicSynchronizer.CurrentRelics 失败（"
                           + e.GetType().Name + "）：" + e.Message);
        }
        return list;
    }

    /// <summary>宝箱候选：CurrentRelics 里每一件遗物一项（title / detail 都来自 RelicModel）。</summary>
    private static List<Candidate> CollectTreasureCandidates(List<RelicModel> relics)
    {
        var list = new List<Candidate>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < relics.Count; i++)
        {
            var relic = relics[i];
            string title = null, detail = "";
            try
            {
                title = SafeText(() => LocText(relic.Title));
                detail = DynamicText(relic, "DynamicDescription", "Description") ?? "";
            }
            catch (Exception e)
            {
                BridgeLog.Warn("treasure: 读遗物标题失败（" + e.GetType().Name + "）：" + e.Message);
            }

            if (string.IsNullOrWhiteSpace(title)) title = "遗物";
            title = title.Trim();
            // key 是给 agent 抄的：太长的标题截断，别把整段说明塞进 key。
            if (title.Length > 20) title = title.Substring(0, 20);

            list.Add(new Candidate
            {
                Key = NextKey(counts, title),
                Title = title,
                Upgraded = false,
                Cost = -1,
                Detail = detail,
                RelicRef = relic,
                TreasureIndex = i,
                Enabled = true,
            });
        }
        return list;
    }

    /// <summary>
    /// 宝箱请求的签名：房间 + **阶段** + 候选（key 已经带标题，遗物再加 ModelId）。
    /// 阶段一定要进签名：阶段 1 → 阶段 2 必须换 id（否则 agent 会拿旧 id 答新内容）。
    /// 故意**不**放 detail 文本和 bool：它们变了不该让 id 抖。
    /// </summary>
    private static string TreasureSignature(NTreasureRoom room, string stage, List<Candidate> candidates)
    {
        var parts = new List<string>
        {
            room.GetInstanceId().ToString(),
            stage ?? "",
            candidates.Count.ToString(),
        };
        foreach (var c in candidates)
            parts.Add(c.Key + "=" + TreasureRelicId(c.RelicRef));
        return string.Join("|", parts);
    }

    /// <summary>遗物的身份（ModelId 的文本形式；读不到就空串）。</summary>
    private static string TreasureRelicId(RelicModel relic)
    {
        try { return relic?.Id?.ToString() ?? ""; }
        catch { return ""; }
    }

    /// <summary>两件遗物是不是同一件（同一份模型引用，或者同一个 ModelId）。</summary>
    private static bool SameTreasureRelic(RelicModel a, RelicModel b)
    {
        if (a == null || b == null) return false;
        if (ReferenceEquals(a, b)) return true;
        var ia = TreasureRelicId(a);
        var ib = TreasureRelicId(b);
        return ia.Length > 0 && string.Equals(ia, ib, StringComparison.Ordinal);
    }

    /// <summary>把待答的宝箱请求摘掉（不动游戏状态，也不碰宝箱）。</summary>
    private static void DropTreasurePending(string why)
    {
        var pending = _treasurePending;
        if (pending == null) return;
        _treasurePending = null;
        BridgeLog.Info($"treasure: 清掉请求 id={pending.Id}（{why}）");
    }

    // ------------------------------------------------------------------ 应答

    /// <summary>
    /// 宝箱请求的应答入口（包裹层）：按 <c>pending.stage</c> 分成两条完全不同的路 ——
    ///   * 阶段 1（chest-closed）：只开箱，不拿遗物；
    ///   * 阶段 2（relics）：逐件走格子自己的领取入口，picks:[] = 走游戏自己的「跳过」。
    /// 任何 ok:false 都会把请求挂回槽里（见 RestoreTreasureAfterReject），所以能改一道答案再答。
    /// </summary>
    private static async Task<Dictionary<string, object>> AnswerTreasure(PendingSelection pending, string[] picks)
    {
        if (string.Equals(pending.TreasureStage, TreasureStageChestClosed, StringComparison.Ordinal))
            return await AnswerTreasureOpen(pending, picks);

        var failure = Validate(pending.Min, pending.Max, pending.Cards, picks, out var chosen, "件");
        if (failure != null) return failure;      // 校验没过：请求还在槽里，本来就能再答

        var sw = Stopwatch.StartNew();

        // 先从槽里摘掉：领取在跑的时候不能再被 /request 看到，也不能被答第二次。
        _treasurePending = null;
        _lastAnsweredId = pending.Id;

        _animating = true;
        Dictionary<string, object> result;
        try
        {
            result = chosen.Count == 0
                ? await SkipTreasureRelics(pending, sw)      // 都不拿 → 游戏自己的跳过
                : await ApplyTreasureAnswer(pending, chosen, sw);
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"treasure: 应答 id={pending.Id} 抛异常（{e.GetType().Name}）：{e.Message}");
            result = Fail(chosen.Count == 0
                ? "跳过宝箱遗物时出错：" + e.Message
                : "领取宝箱遗物时出错：" + e.Message);
        }
        finally { _animating = false; }

        // 任何 ok:false 都不是「已经答过了」：把请求挂回槽里、清掉回答标记。
        if (!IsTrue(result, "ok")) RestoreTreasureAfterReject(pending);
        return result;
    }

    /// <summary>
    /// 阶段 1 的应答：只开箱。
    /// picks = [「打开宝箱#1」] → 调 <c>NTreasureRoom.OpenChest()</c> 并等遗物格子摆好；
    /// picks = [] → 不打开，箱子保持关闭（玩家可以自己后面再开，会重新发一次阶段 1）。
    /// 无论哪条，**都不拿遗物**：开箱之后要重新 GET /request 才会给出阶段 2 的内容。
    /// </summary>
    private static async Task<Dictionary<string, object>> AnswerTreasureOpen(
        PendingSelection pending, string[] picks)
    {
        picks ??= Array.Empty<string>();
        if (picks.Length > 1)
            return Fail($"只能选 1 个动作，你给了 {picks.Length} 个");

        Candidate chosen = null;
        if (picks.Length == 1)
        {
            var key = (picks[0] ?? "").Trim();
            chosen = pending.Cards.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));
            if (chosen == null)
                return Fail($"{key} 不在候选里（可选：{DescribeKeys(pending.Cards)}；picks:[] = 不打开）");
        }

        var sw = Stopwatch.StartNew();

        // 先从槽里摘掉：开箱在跑的时候不能再被 /request 看到，也不能被答第二次。
        _treasurePending = null;
        _lastAnsweredId = pending.Id;

        if (chosen == null)
        {
            // 不打开：不碰宝箱、不碰遗物，箱子保持关闭。
            BridgeLog.Info($"treasure(open) answered id={pending.Id} 没打开");
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = "screen",
                ["route"] = "treasure",
                ["stage"] = TreasureStageChestClosed,
                ["applied"] = "（没打开）",
                ["how"] = "treasure-chest-skip",
                ["animated"] = false,
                ["waitedMs"] = sw.ElapsedMilliseconds,
                ["visual"] = TreasureVisualChestSkipped,
                ["opened"] = false,
                ["chestOpened"] = false,
                ["taken"] = 0,
                ["items"] = new List<object>(),
                ["next"] = "箱子保持关闭：想开就重新 GET /request（阶段 1 会再问一次）。"
                           + "注意单人宝箱房在开箱前「前进」按钮是关着的（OnActiveScreenChanged 用"
                           + " _hasChestBeenOpened 判），所以不打开就没法用 /proceed 走人",
            };
        }

        _animating = true;
        Dictionary<string, object> result;
        try { result = await OpenTreasureChest(pending, sw); }
        catch (Exception e)
        {
            BridgeLog.Warn($"treasure(open): 应答 id={pending.Id} 抛异常（{e.GetType().Name}）：{e.Message}");
            result = Fail("打开宝箱时出错：" + e.Message);
        }
        finally { _animating = false; }

        if (!IsTrue(result, "ok")) RestoreTreasureAfterReject(pending);
        return result;
    }

    /// <summary>
    /// 阶段 1 真正干活：调游戏自己的开箱入口 + 等遗物格子摆好，**不拿任何遗物**。
    /// 调用方已经摘掉槽、置了 _animating。
    /// </summary>
    private static async Task<Dictionary<string, object>> OpenTreasureChest(
        PendingSelection pending, Stopwatch sw)
    {
        var room = pending.Screen as NTreasureRoom;
        if (room == null) return Fail("宝箱房节点丢了，没法开箱");

        bool shown;
        try { shown = await BridgePump.OnMain(() => IsShown(room), 3000); }
        catch { shown = false; }
        if (!shown) return Fail("宝箱房已经不在了（已经离开宝箱房），没法开箱");

        var open = await EnsureTreasureChestOpen(room);
        if (!string.IsNullOrEmpty(open.Reason)) return Fail(open.Reason);

        // 再读一次现场：确认箱子确实开了、遗物格子确实摆出来了（不是靠猜）。
        TreasureView view = null;
        try { view = await BridgePump.OnMain(() => ReadTreasureRoomView(room), 3000); }
        catch { view = null; }
        if (view == null || !view.Ok)
            return Fail("开箱之后读不出宝箱状态：" + (view?.Why ?? "超时"));

        var waited = sw.ElapsedMilliseconds;
        if (!view.ChestOpened || !view.CollectionOpen)
        {
            var dict = Fail("开箱之后状态不对：_hasChestBeenOpened=" + view.ChestOpened
                            + "、_isRelicCollectionOpen=" + view.CollectionOpen
                            + "（遗物格子没摆出来，阶段 2 没法给）");
            dict["waitedMs"] = waited;
            dict["chestOpened"] = view.ChestOpened;
            dict["relicCollectionOpen"] = view.CollectionOpen;
            return dict;
        }

        BridgeLog.Info($"treasure(open) answered id={pending.Id} opened waitedMs={waited} " +
                       $"openedNow={open.OpenedNow} collectionOpen={view.CollectionOpen} " +
                       $"chestEnabled={view.ChestEnabled}");

        var ok = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["source"] = "screen",
            ["route"] = "treasure",
            ["stage"] = TreasureStageChestClosed,
            ["applied"] = "（打开宝箱）",
            ["how"] = open.OpenedNow ? "treasure-open-chest" : "treasure-open-awaited",
            ["animated"] = true,
            ["waitedMs"] = waited,
            ["visual"] = TreasureVisualChestOpened,
            ["opened"] = true,
            ["chestOpened"] = true,
            ["relicCollectionOpen"] = true,
            ["taken"] = 0,
            ["items"] = new List<object>(),
            ["next"] = "箱子已经打开、遗物没拿：重新 GET /request 会给出 stage:\"relics\" 的内容",
        };
        if (!open.OpenedNow)
            ok["note"] = "箱子是玩家自己开的（_chestButton 已经禁用）：这次只等遗物格子摆好，没有重复发金币";
        return ok;
    }

    /// <summary>
    /// 阶段 2 <c>picks:[]</c>（都不拿）：走**游戏自己的跳过**，然后让房间停在「可以走」的状态。
    ///
    /// 单人不拿遗物的完整游戏路径（IL 逐条核对过）：
    ///   ① <c>NTreasureRoom.OnProceedButtonPressed</c>（「跳过」按钮，Pressed 信号接的就是它）→
    ///      <c>TreasureRoomRelicSynchronizer.SkipRelicLocally()</c> = <c>PickRelicLocally(null)</c> →
    ///      <c>PickRelicAction(localPlayer, null)</c> → <c>OnPicked(player, null)</c>：单人分支
    ///      （<c>Players.Count == 1</c> 且 index 没值）只 <c>_singleplayerSkipped = true</c> 然后 return，
    ///      **不**发 RelicsAwarded、**不** EndRelicVoting（所以 _currentRelics 还在）；
    ///   ② 按钮后半段：单人 → <c>NMapScreen.SetTravelEnabled(true)</c> +
    ///      <c>RunManager.ProceedFromTerminalRewardsScreen()</c>（这句只是 <c>NMapScreen.Open(false)</c>
    ///      + 放开移动，**不是**退房间）；
    ///   ③ 真正的退房间发生在玩家在地图上走到下一个节点：<c>TreasureRoom.Exit()</c> →
    ///      <c>OnRoomExited()</c>：读到 <c>_singleplayerSkipped</c> 才 <c>EndRelicVoting()</c>
    ///      （_currentRelics = null、_singleplayerSkipped = false）。
    ///   所以我们只做 ①（并等它真的落地），再等「前进」按钮亮起来（单人开箱时 EnableSkipAfterDelay(2.5s)
    ///   会 Enable 它），剩下的交给 /proceed（= 按一下同一个按钮，自己还会再调一次 SkipRelicLocally，
    ///   重复调用无害）—— 不硬改任何状态。
    /// </summary>
    private static async Task<Dictionary<string, object>> SkipTreasureRelics(
        PendingSelection pending, Stopwatch sw)
    {
        var room = pending.Screen as NTreasureRoom;
        if (room == null) return Fail("宝箱房节点丢了，没法跳过");

        bool shown;
        try { shown = await BridgePump.OnMain(() => IsShown(room), 3000); }
        catch { shown = false; }
        if (!shown) return Fail("宝箱房已经不在了（已经离开宝箱房），没法跳过");

        string why = null;
        try
        {
            why = await BridgePump.OnMain(() =>
            {
                var sync = TreasureRelicSynchronizer();
                if (sync == null) return "RunManager.TreasureRoomRelicSynchronizer 不可用";
                // PickRelicLocally 在 _currentRelics == null 时会抛：
                // 那说明遗物已经结算/清掉了，跳过这一步本来就不需要。
                if (CurrentTreasureRelics(sync).Count == 0) return null;
                try
                {
                    sync.SkipRelicLocally();
                    return null;
                }
                catch (Exception e)
                {
                    return "SkipRelicLocally 抛了：" + e.GetType().Name + "：" + e.Message;
                }
            }, 5000);
        }
        catch (Exception e) { why = "调用 SkipRelicLocally 失败：" + e.Message; }
        if (!string.IsNullOrEmpty(why)) return Fail(why);

        // ① 等 _singleplayerSkipped 真的落地（PickRelicAction 是排队执行的，不是同步返回）。
        var voted = await WaitTreasureSkipVoted(room);
        // ② 等「前进」按钮亮起来（单人 EnableSkipAfterDelay(2.5s)）。
        var proceed = await WaitTreasureProceedReady(room);

        var waited = sw.ElapsedMilliseconds;
        if (!voted && !proceed.SkipVoted && !proceed.RelicsCleared)
        {
            var dict = Fail($"{TreasureSkipSettleMs}ms 内没有确认「跳过」落地"
                            + "（_singleplayerSkipped 还是 false、CurrentRelics 也没清）");
            dict["waitedMs"] = waited;
            dict["chestOpened"] = proceed.ChestOpened;
            dict["proceedReady"] = proceed.ProceedReady;
            return dict;
        }

        BridgeLog.Info($"treasure skip answered id={pending.Id} waitedMs={waited} " +
                       $"singleplayerSkipped={proceed.SkipVoted} relicsCleared={proceed.RelicsCleared} " +
                       $"proceedReady={proceed.ProceedReady} proceedIsSkip={proceed.ProceedIsSkip} " +
                       $"proceedWaitedMs={proceed.WaitedMs}");

        var ok = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["source"] = "screen",
            ["route"] = "treasure",
            ["stage"] = TreasureStageRelics,
            ["applied"] = "（都不拿）",
            ["how"] = "treasure-skip",
            ["animated"] = true,
            ["waitedMs"] = waited,
            ["visual"] = TreasureVisualSkipped,
            ["skipped"] = true,
            ["taken"] = 0,
            ["items"] = new List<object>(),
            ["chestOpened"] = proceed.ChestOpened,
            // 前进按钮可用 = 现在就能 /proceed 走人；不可用多半是 EnableSkipAfterDelay 的 2.5s 还没到。
            ["proceedReady"] = proceed.ProceedReady,
            ["proceedIsSkip"] = proceed.ProceedIsSkip,
            ["proceedWaitedMs"] = proceed.WaitedMs,
            ["next"] = proceed.ProceedReady
                ? "GET /request 不会再发宝箱请求：用 /proceed 按「跳过」按钮（它自己还会再调一次 "
                  + "SkipRelicLocally，然后 ProceedFromTerminalRewardsScreen 打开地图并放开移动），"
                  + "在地图上走到下一个节点才会真正离开房间（TreasureRoom.Exit → OnRoomExited）"
                : "「前进」按钮还没亮（单人开箱要等 2.5s）：过一会儿 /proceed，或再 GET /request 看看",
        };
        if (!proceed.ProceedReady)
            ok["note"] = "跳过已经落地，但「前进」按钮这一刻还不可用 —— 它由 EnableSkipAfterDelay 定时点亮";
        return ok;
    }

    /// <summary>真正去拿：调用方已经摘掉槽、置了 _animating，这里只管开箱、领取与核对。</summary>
    private static async Task<Dictionary<string, object>> ApplyTreasureAnswer(
        PendingSelection pending, List<Candidate> chosen, Stopwatch sw)
    {
        var room = pending.Screen as NTreasureRoom;

        try
        {
            bool shown;
            try { shown = await BridgePump.OnMain(() => IsShown(room), 3000); }
            catch { shown = false; }
            if (!shown)
                return Fail("宝箱房已经不在了（已经离开宝箱房），没法领取");

            // 开箱必须放在领取之前：格子没摆出来时，格子自己的 PickRelic 连下标都对不上。
            var open = await EnsureTreasureChestOpen(room);
            if (!string.IsNullOrEmpty(open.Reason))
                return Fail(open.Reason);

            var items = new List<Dictionary<string, object>>();
            var appliedKeys = new List<string>();
            var failed = new List<string>();
            var holderPath = 0;

            foreach (var c in chosen)
            {
                var result = await ApplyOneTreasureRelic(room, c);

                var item = new Dictionary<string, object>
                {
                    ["key"] = c.Key,
                    ["itemType"] = "Relic",
                    ["how"] = result.How,
                    // 只有真拿到（ok:true）才允许报 treasure-taken / treasure-model-delay；
                    // 没拿到一律 treasure-none —— 别再谎报「已经拿到了」。
                    ["visual"] = result.Ok ? (result.Visual ?? TreasureVisualTaken) : TreasureVisualNone,
                    ["settled"] = result.Settled,
                    ["ok"] = result.Ok,
                };
                if (!string.IsNullOrEmpty(result.Why)) item["note"] = result.Why;
                items.Add(item);

                if (result.Ok)
                {
                    appliedKeys.Add(c.Key);
                    if (string.Equals(result.How, "treasure-holder", StringComparison.Ordinal)) holderPath++;
                }
                else
                {
                    failed.Add(c.Key + "（" + (result.Why ?? "游戏没接受") + "）");
                }

                // 留出拍子：遗物飞进遗物栏（NRelicInventory.AnimateRelic）在播。
                await Task.Delay(TreasureGapMs);
            }

            var waited = sw.ElapsedMilliseconds;
            var applied = appliedKeys.Count == 0 ? "（无）" : string.Join("、", appliedKeys);

            int relicCountNow, goldNow;
            try
            {
                var snapshot = await BridgePump.OnMain(() =>
                {
                    var player = LocalPlayerModel();
                    return new[] { player?.Relics?.Count ?? -1, player?.Gold ?? -1 };
                }, 3000);
                relicCountNow = snapshot.Length > 0 ? snapshot[0] : -1;
                goldNow = snapshot.Length > 1 ? snapshot[1] : -1;
            }
            catch { relicCountNow = -1; goldNow = -1; }

            if (failed.Count > 0)
            {
                BridgeLog.Warn($"treasure answered id={pending.Id} picks={applied} failed={failed.Count} " +
                               $"waitedMs={waited}");
                var dict = Fail($"有 {failed.Count} 件没拿到：{string.Join("；", failed)}"
                                + (appliedKeys.Count > 0 ? $"（已经拿到的：{applied}）" : ""));
                dict["applied"] = applied;
                dict["items"] = items;
                dict["waitedMs"] = waited;
                dict["relics"] = relicCountNow;
                dict["gold"] = goldNow;
                return dict;
            }

            BridgeLog.Info($"treasure answered id={pending.Id} picks={applied} waitedMs={waited} " +
                           $"relics={relicCountNow} gold={goldNow} holderPath={holderPath} " +
                           $"openedNow={open.OpenedNow}");

            var ok = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = "screen",
                ["route"] = "treasure",
                ["stage"] = TreasureStageRelics,
                ["applied"] = applied,
                ["how"] = chosen.Count == 1 ? items[0]["how"] : "treasure-batch",
                ["animated"] = true,
                ["waitedMs"] = waited,
                // 走了遗物格子自己的领取入口才算 treasure-taken（有飞入表现）；
                // 只走同步器（PickRelicLocally）的如实报 treasure-model-delay。
                ["visual"] = holderPath > 0 ? TreasureVisualTaken : TreasureVisualModelDelay,
                ["settled"] = true,
                ["chestOpened"] = true,
                ["taken"] = appliedKeys.Count,
                ["relics"] = relicCountNow,
                ["gold"] = goldNow,
                ["items"] = items,
            };
            if (open.OpenedNow)
                ok["note"] = "宝箱是这次应答里打开的：箱里的金币同时结算了（gold 字段是当前金币）";
            return ok;
        }
        finally
        {
            _animating = false;
        }
    }

    /// <summary>
    /// 这次宝箱请求没被接受（任何 ok:false）：恢复成「还挂在槽里、还能再答」。
    /// 只清「已应答」标记、把 pending 挂回去，不碰游戏状态 —— 被拒的请求不会把 id 永久锁死，
    /// 也不会因为重答而对同一件遗物领两遍（ApplyOneTreasureRelic 会先核对「还在不在箱子里」）。
    /// 真正的 ok:true 仍然保持锁（不进这里），防止重复领取。
    /// </summary>
    private static void RestoreTreasureAfterReject(PendingSelection pending)
    {
        if (pending == null) return;
        if (_lastAnsweredId == pending.Id) _lastAnsweredId = -1;
        _treasurePending = pending;
        BridgeLog.Info($"treasure: 请求 id={pending.Id} 没被接受，恢复成可再答（已挂回 _treasurePending）");
    }

    /// <summary>
    /// 让宝箱处于「遗物格子摆好了」的状态。玩家还没开箱时用游戏自己的开箱入口
    /// <c>NTreasureRoom.OpenChest()</c> 开（= 点宝箱那一整条链），然后等
    /// <c>_isRelicCollectionOpen</c> 变 true；玩家自己正在开/已经开过时只等，不重复开。
    /// </summary>
    private static async Task<TreasureOpenResult> EnsureTreasureChestOpen(NTreasureRoom room)
    {
        var view = new TreasureView();
        try
        {
            await BridgePump.OnMain(() =>
            {
                view.ChestOpened = ReadBool(room, "_hasChestBeenOpened", false);
                view.CollectionOpen = ReadBool(room, "_isRelicCollectionOpen", false);
                var chest = ReadMember(room, "_chestButton") as Control;
                view.ChestEnabled = chest == null || ReadBool(chest, "_isEnabled", true);
                return true;
            }, 3000);
        }
        catch (Exception e)
        {
            return new TreasureOpenResult { Reason = "读取宝箱状态失败：" + e.Message };
        }

        // 格子已经摆着：直接领。
        if (view.CollectionOpen) return new TreasureOpenResult();

        // 箱子按钮已经被禁用 = 已经有人开始开箱（OpenChest 一开始就把它 MouseFilter 关掉，
        // OnChestButtonReleased 末尾再 Disable）：绝不调第二次，只等格子摆好。
        if (!view.ChestEnabled)
        {
            var already = await WaitTreasureCollection(room);
            already.OpenedNow = false;      // 不是我们开的，别在返回里揽这个功
            return already;
        }

        if (view.ChestOpened) return new TreasureOpenResult();

        // 走游戏自己的开箱。OpenChest() 内部会 await RelicPickingBegan()/RelicPickingFinished()，
        // 只有玩家拿走遗物才结束，所以**不能 await 它**，只把它推起来。
        string why = null;
        try
        {
            why = await BridgePump.OnMain(() =>
            {
                var openChest = FindMethod(room.GetType(), "OpenChest", 0);
                if (openChest == null) return "NTreasureRoom 上没找到 OpenChest()";
                try
                {
                    if (openChest.Invoke(room, null) is Task task)
                    {
                        _ = WatchTreasureTask(task, "OpenChest");
                        return null;
                    }
                    return "OpenChest() 没返回 Task";
                }
                catch (Exception e) { return "OpenChest 抛了：" + e.GetType().Name + "：" + e.Message; }
            }, 5000);
        }
        catch (Exception e) { why = "调用 OpenChest 失败：" + e.Message; }

        if (!string.IsNullOrEmpty(why))
            return new TreasureOpenResult { Reason = why + "（宝箱没能打开，遗物格子也就没摆出来）" };

        var waited = await WaitTreasureCollection(room);
        waited.OpenedNow = string.IsNullOrEmpty(waited.Reason);
        return waited;
    }

    /// <summary>等遗物格子摆好（<c>_isRelicCollectionOpen</c> = true），再多留一拍让淡入播开。</summary>
    private static async Task<TreasureOpenResult> WaitTreasureCollection(NTreasureRoom room)
    {
        var tries = Math.Max(1, TreasureOpenMs / 100);
        for (var i = 0; i < tries; i++)
        {
            await Task.Delay(100);
            bool ready;
            try { ready = await BridgePump.OnMain(() => ReadBool(room, "_isRelicCollectionOpen", false), 3000); }
            catch { ready = false; }
            if (!ready) continue;
            await Task.Delay(TreasureOpenGapMs);
            return new TreasureOpenResult { OpenedNow = true };
        }
        return new TreasureOpenResult
        {
            Reason = $"开箱 {TreasureOpenMs}ms 内遗物格子没有摆出来（NTreasureRoom._isRelicCollectionOpen 一直是 false）",
        };
    }

    /// <summary>「跳过」之后现场快照：跳过落没落地 + 「前进」按钮能不能用。</summary>
    private sealed class TreasureSkipState
    {
        public bool SkipVoted;      // _singleplayerSkipped（单人跳过投出去了）
        public bool RelicsCleared;  // CurrentRelics 已经清掉（联机路线 / 已结算）
        public bool ChestOpened;    // _hasChestBeenOpened
        public bool ProceedReady;   // _proceedButton 现在可用（/proceed 能走）
        public bool ProceedIsSkip;  // _proceedButton.IsSkip
        public int WaitedMs;        // 等「前进」按钮用了多久
    }

    /// <summary>
    /// 等 <c>_singleplayerSkipped</c> 落地（最多 <see cref="TreasureSkipSettleMs"/>）。
    /// <c>SkipRelicLocally()</c> 只是把 <c>PickRelicAction(null)</c> 排进 action queue，
    /// 真正置位在 <c>OnPicked(player, null)</c> 里，所以必须轮询，不能当同步调用。联机路线则直接清
    /// <c>CurrentRelics</c>，同样算落地。
    /// </summary>
    private static async Task<bool> WaitTreasureSkipVoted(NTreasureRoom room)
    {
        var tries = Math.Max(1, TreasureSkipSettleMs / 100);
        for (var i = 0; i < tries; i++)
        {
            TreasureView view = null;
            try { view = await BridgePump.OnMain(() => ReadTreasureRoomView(room), 3000); }
            catch { view = null; }
            if (view != null && view.Ok && (view.SkipVoted || view.RelicsCleared)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>
    /// 等「前进」按钮亮起来（最多 <see cref="TreasureProceedReadyMs"/>：单人开箱时
    /// <c>EnableSkipAfterDelay(2.5f)</c> 到点才 <c>Enable()</c>）。等的时候顺手刷新跳过状态。
    /// 超时不算失败：跳过已经落地就照实报 <c>proceedReady:false</c>。
    /// </summary>
    private static async Task<TreasureSkipState> WaitTreasureProceedReady(NTreasureRoom room)
    {
        var state = new TreasureSkipState();
        var sw = Stopwatch.StartNew();
        var tries = Math.Max(1, TreasureProceedReadyMs / 100);
        for (var i = 0; i < tries; i++)
        {
            TreasureView view = null;
            try { view = await BridgePump.OnMain(() => ReadTreasureRoomView(room), 3000); }
            catch { view = null; }
            if (view != null && view.Ok)
            {
                state.SkipVoted = view.SkipVoted;
                state.ChestOpened = view.ChestOpened;
                state.ProceedReady = view.ProceedReady;
                state.ProceedIsSkip = view.ProceedIsSkip;
                state.RelicsCleared = view.RelicsCleared;
                if (state.ProceedReady) break;
            }
            await Task.Delay(100);
        }
        state.WaitedMs = (int)sw.ElapsedMilliseconds;
        return state;
    }

    /// <summary>
    /// 单件遗物：先核对「还在箱子里 / 还没进遗物栏」（事实写清楚），再让游戏自己的领取入口跑起来，
    /// 最多等 TreasureSettleMs，最后用「遗物进了遗物栏」或「遗物数 +1」核对结果。
    /// </summary>
    private static async Task<TreasureApplyResult> ApplyOneTreasureRelic(NTreasureRoom room, Candidate candidate)
    {
        var relic = candidate?.RelicRef;
        if (relic == null) return new TreasureApplyResult { Ok = false, Why = "候选里没有 RelicModel 对象" };

        TreasureView before;
        try { before = await BridgePump.OnMain(() => ReadTreasureView(room, relic), 3000); }
        catch (Exception e) { return new TreasureApplyResult { Ok = false, Why = "读取宝箱状态失败：" + e.Message }; }
        if (before == null) return new TreasureApplyResult { Ok = false, Why = "读取宝箱状态超时" };
        if (!before.Ok) return new TreasureApplyResult { Ok = false, Why = "读取宝箱状态失败：" + (before.Why ?? "?") };

        // 幂等保护：请求发出去到 agent 应答之间，玩家可能自己点掉了这件遗物。
        if (!before.InChest)
            return new TreasureApplyResult
            {
                Ok = false,
                How = "already-taken",
                Settled = true,
                Why = before.Obtained
                    ? "应答之前这件遗物就已经被拿走了（不重复领取）"
                    : "应答之前这件遗物就已经不在宝箱里了（不重复领取）",
            };

        TreasureStart start;
        try { start = await BridgePump.OnMain(() => StartTreasureApply(room, candidate), 5000); }
        catch (Exception e) { return new TreasureApplyResult { Ok = false, Why = "启动领取失败：" + e.Message }; }

        if (start == null || string.Equals(start.How, "none", StringComparison.Ordinal))
            return new TreasureApplyResult
            {
                Ok = false,
                How = start?.How ?? "none",
                Why = start?.Why ?? "没拿到游戏的领取入口",
            };

        // 遗物进背包是 RelicsAwarded → AnimateRelicAwards → RelicCmd.Obtain，中间夹着几段
        // Cmd.Wait：轮询到「遗物真的进了遗物栏」为止，别用一个固定 delay 瞎猜。
        TreasureView after = null;
        var tries = Math.Max(1, TreasureSettleMs / 150);
        for (var i = 0; i < tries; i++)
        {
            await Task.Delay(150);
            try { after = await BridgePump.OnMain(() => ReadTreasureView(room, relic), 3000); }
            catch { after = null; }
            if (after != null && after.Ok && after.Obtained) break;
        }

        if (after == null || !after.Ok)
            return new TreasureApplyResult
            {
                Ok = false,
                How = start.How,
                Why = "读取领取后的状态失败（超时或异常）",
            };

        if (!after.Obtained && after.RelicCount <= before.RelicCount)
            return new TreasureApplyResult
            {
                Ok = false,
                How = start.How,
                Settled = false,
                Why = $"遗物没有进遗物栏：领取前遗物 {before.RelicCount} 件，"
                      + $"{TreasureSettleMs}ms 后 {after.RelicCount} 件，"
                      + $"宝箱里那件{(after.InChest ? "还在" : "已经不在了")}",
            };

        return new TreasureApplyResult
        {
            Ok = true,
            How = start.How,
            Settled = true,
            Visual = string.Equals(start.How, "treasure-holder", StringComparison.Ordinal)
                ? TreasureVisualTaken
                : TreasureVisualModelDelay,
            Why = after.Obtained
                ? $"遗物已经进了遗物栏（{before.RelicCount} → {after.RelicCount}）"
                : $"遗物数 +1（{before.RelicCount} → {after.RelicCount}）",
        };
    }

    /// <summary>主线程：这件遗物现在什么状态（还在不在箱子里 / 进没进遗物栏 / 遗物数 / 金币 / 箱子开没开）。</summary>
    private static TreasureView ReadTreasureView(NTreasureRoom room, RelicModel relic)
    {
        var view = new TreasureView();
        try
        {
            var sync = TreasureRelicSynchronizer();
            if (sync != null)
            {
                var relics = sync.CurrentRelics;
                if (relics != null)
                    foreach (var inChest in relics)
                        if (SameTreasureRelic(inChest, relic)) { view.InChest = true; break; }
            }

            var player = LocalPlayerModel();
            var owned = player?.Relics;
            view.RelicCount = owned?.Count ?? 0;
            view.Gold = player?.Gold ?? -1;
            if (owned != null)
                foreach (var mine in owned)
                    if (SameTreasureRelic(mine, relic)) { view.Obtained = true; break; }

            FillTreasureRoomFields(room, view);
            view.Ok = true;
        }
        catch (Exception e)
        {
            view.Why = e.GetType().Name + ": " + e.Message;
        }
        return view;
    }

    /// <summary>
    /// 主线程：两个阶段的判定全靠这组字段（读不到就返回 <c>Ok = false</c>，上层不发请求）。
    ///   * <c>_hasChestBeenOpened</c>：箱子开过没有（阶段 1 / 阶段 2 的分界）；
    ///   * <c>_isRelicCollectionOpen</c>：遗物格子摆出来了没有（开箱动画没播完时是 false）；
    ///   * <c>_chestButton._isEnabled</c>：没人开始开箱时才还能点（不会重复调 OpenChest）；
    ///   * <c>_proceedButton._isEnabled</c> / <c>IsSkip</c>：能不能 /proceed、文字是「跳过」还是「前进」；
    ///   * synchronizer 的 <c>_singleplayerSkipped</c>：已经投过「跳过」。
    /// </summary>
    private static TreasureView ReadTreasureRoomView(NTreasureRoom room)
    {
        var view = new TreasureView();
        try
        {
            FillTreasureRoomFields(room, view);
            view.Ok = true;
        }
        catch (Exception e)
        {
            view.Why = e.GetType().Name + ": " + e.Message;
        }
        return view;
    }

    /// <summary>房间节点 / 同步器上的那几个 bool（主线程）。</summary>
    private static void FillTreasureRoomFields(NTreasureRoom room, TreasureView view)
    {
        if (room != null)
        {
            view.ChestOpened = ReadBool(room, "_hasChestBeenOpened", false);
            view.CollectionOpen = ReadBool(room, "_isRelicCollectionOpen", false);
            var chest = ReadMember(room, "_chestButton") as Control;
            view.ChestEnabled = chest == null || ReadBool(chest, "_isEnabled", true);
            var proceed = ReadMember(room, "_proceedButton") as Control;
            view.ProceedReady = proceed != null && ReadBool(proceed, "_isEnabled", false);
            view.ProceedIsSkip = proceed != null && ReadBool(proceed, "IsSkip", false);
        }
        var sync = TreasureRelicSynchronizer();
        view.SkipVoted = TreasureSkipVoted(sync);
        view.RelicsCleared = CurrentTreasureRelics(sync).Count == 0;
    }

    /// <summary>已经投过「跳过」没有（<c>TreasureRoomRelicSynchronizer._singleplayerSkipped</c>）。</summary>
    private static bool TreasureSkipVoted(TreasureRoomRelicSynchronizer sync)
    {
        try { return sync != null && ReadBool(sync, "_singleplayerSkipped", false); }
        catch { return false; }
    }

    /// <summary>
    /// 主线程：把这次领取交给游戏自己的入口。两条路，优先遗物格子自己的领取方法。
    /// </summary>
    private static TreasureStart StartTreasureApply(NTreasureRoom room, Candidate candidate)
    {
        var relic = candidate?.RelicRef;
        if (relic == null) return new TreasureStart { Why = "候选里没有 RelicModel 对象" };

        var sync = TreasureRelicSynchronizer();
        if (sync == null) return new TreasureStart { Why = "RunManager.TreasureRoomRelicSynchronizer 不可用" };

        // ① 遗物格子自己的领取入口：玩家松开鼠标走的就是它 ——
        //    <InitializeRelics>b__21_0 → NTreasureRoomRelicCollection.PickRelic(holder)。
        //    它内部核对「开箱满 200ms」，然后调 PickRelicLocally(holder.Index)。
        var collection = TreasureCollection(room);
        var holder = TreasureHolderByIndex(collection, candidate.TreasureIndex);
        if (collection != null && holder != null)
        {
            var pick = FindMethod(collection.GetType(), "PickRelic", 1);
            if (pick != null)
            {
                try
                {
                    pick.Invoke(collection, new object[] { holder });
                    return new TreasureStart { How = "treasure-holder" };
                }
                catch (Exception e)
                {
                    BridgeLog.Warn($"treasure: PickRelic 调失败（{e.GetType().Name}）：{e.Message}，改用模型层");
                }
            }
            else
            {
                BridgeLog.Warn("treasure: NTreasureRoomRelicCollection 上没找到 PickRelic(holder)，改用模型层");
            }
        }

        // ② 模型层兜底：PickRelic 里就是这一句（游戏的「跳过」= 同一个方法传 null）。
        try
        {
            sync.PickRelicLocally(candidate.TreasureIndex);
            return new TreasureStart { How = "treasure-model" };
        }
        catch (Exception e)
        {
            return new TreasureStart { Why = "PickRelicLocally 抛了：" + e.Message };
        }
    }

    /// <summary>宝箱房上的遗物格子容器（NTreasureRoom 的 private _relicCollection）。</summary>
    private static NTreasureRoomRelicCollection TreasureCollection(NTreasureRoom room)
    {
        try { return ReadMember(room, "_relicCollection") as NTreasureRoomRelicCollection; }
        catch { return null; }
    }

    /// <summary>按 CurrentRelics 的下标找摆着这件遗物的格子（开箱之后 Index 才有意义）。</summary>
    private static NTreasureRoomRelicHolder TreasureHolderByIndex(
        NTreasureRoomRelicCollection collection, int index)
    {
        if (collection == null || index < 0) return null;
        try
        {
            if (ReadMember(collection, "_holdersInUse") is IEnumerable<NTreasureRoomRelicHolder> used)
                foreach (var holder in used)
                    if (holder != null && GodotObject.IsInstanceValid(holder) && holder.Index == index)
                        return holder;

            var single = collection.SingleplayerRelicHolder;
            if (single != null && GodotObject.IsInstanceValid(single) && single.Index == index) return single;
        }
        catch { }
        return null;
    }

    /// <summary>只为「把异常吃掉并写日志」：OpenChest 是长任务，不 await 它，但不能留下未观察异常。</summary>
    private static async Task WatchTreasureTask(Task task, string what)
    {
        try { await task; }
        catch (Exception e)
        {
            BridgeLog.Warn($"treasure: {what} 抛了（{e.GetType().Name}）：{e.Message}");
        }
    }

    // =================================================================== 营火通道（NRestSiteRoom + RestSiteRoom.Options）

    /*
     * 营火是「选一项」：休息 / 锻造 / 挖掘 …，选完只剩「前进」离开。所以 min = max = 1、
     * cancelable = false；「前进」不通过 /respond（离开用 /proceed）。
     *
     * 数据源（都在主线程读，**绝不写死选项**）：
     *   * 房间节点：MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom。
     *     NRestSiteRoom.Instance = NRun.Instance.RestSiteRoom = NRun 房间容器里**当前**场景
     *     （不是营火时返回 null），所以不会读到上一个房间的僵尸节点。
     *   * 选项集合：NRestSiteRoom.Options（= private 字段 _room.Options）
     *     → RestSiteRoom.Options → RestSiteSynchronizer.GetLocalOptions() → 本玩家那份 options。
     *     这份列表是**进房间那一刻**由 RestSiteOption.Generate(player) 生成的：
     *       [ HealRestSiteOption(休息), SmithRestSiteOption(锻造) ]
     *       + 多人时 MendRestSiteOption
     *       + Hook.ModifyRestSiteOptions(runState, player, list)   ← 遗物在这里加选项
     *     所以「铲子加挖掘」这类遗物加成不需要任何特判：读模型就有；我们只认模型，不认按钮文本。
     *   * 每个选项：OptionId(string) / Title(LocString) / Description(LocString) / IsEnabled(bool)。
     *     注意 RestSiteOption.IsEnabled 的**基类实现恒为 true**（HP 满时「休息」仍然可选，只是回血 0），
     *     只有部分子类 override（SmithRestSiteOption.IsEnabled = Deck.UpgradableCardCount > 0）。
     *     界面拿它做「变灰」（NRestSiteButton.Reload 里把 HSV 饱和度压到 0、亮度 0.6），
     *     并不代表点不动 —— 所以候选里如实报 enabled，但**不**拿它当可答性判据。
     *
     * 应答（不模拟鼠标、不点坐标）：走玩家点一下按钮时的同一条链：
     *   NRestSiteButton.OnRelease → SelectOption(option)
     *     → Room.Options.IndexOf(option) → _executingOption = true → NRestSiteRoom.Instance.DisableOptions()
     *     → RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(index)
     *         → restSites[slot].options[index] → BeforePlayerOptionChosen → await option.OnSelect()
     *         → lastChosenOptionIndex = index → AfterPlayerOptionChosen
     *     → 成功才 NRestSiteRoom.AfterSelectingOption(option)（隐藏选项 → 选项自己的 PostSelectVfx
     *       → 亮出「前进」→ 重建选项按钮）
     *   拿不到按钮时退化成 RestSiteSynchronizer.ChooseLocalOption(index)（Task<bool>，能拿到成败），
     *   并自己补一次 AfterSelectingOption(option) 把界面表现补上。
     *
     * 验证（每次都做，不靠猜）：选完之后至少命中一条才算生效 ——
     *   ① RestSiteSynchronizer.GetChosenOptionIndex(本地玩家 NetId) 从 null 变成下标；
     *   ② 选项列表（OptionId + enabled）变了；
     *   ③ 玩家状态变了：HP / 最大 HP / 牌组张数 / 已升级张数 / 遗物数；
     *   ④「前进」按钮从禁用变成启用（游戏只在选成功之后才 Enable）。
     *   一条都不命中就 ok:false，并把前后两组事实写进 error。
     *
     * 连锁：「锻造」→ SmithRestSiteOption.OnSelect →
     *   CardSelectCmd.FromDeckForUpgrade(player, prefs{UpgradeSelectionPrompt, MaxSelect=SmithCount,
     *   Cancelable=true, RequireManualConfirmation=true})，**选完才** CardCmd.Upgrade + AfterRestSiteSmith。
     *   所以不能把 ChooseLocalOption 等到底：最多等 RestSettleMs，超时按「已受理、下一步是选牌」返回
     *   next:"card-choice"，由下一次 /request 给出选牌请求（selector 通道 kind=upgrade，
     *   caller=FromDeckForUpgrade，会走已有的牌库动画）。
     *
     * 什么时候算「已经选完」：GetChosenOptionIndex(本地玩家) 非 null 就不再发营火请求
     *   （选完之后营火只剩「前进」，那不是决策）。进房间时 BeginRestSite 会把这份记录清空，
     *   所以不会串到上一间营火。
     */

    /// <summary>营火现场的指纹（选之前读一次、选之后读一次，用来判断「到底生效了没有」）。</summary>
    private sealed class RestView
    {
        public bool Ok;
        public string Why;
        public bool Shown;
        public List<string> OptionIds = new();
        public List<bool> Enabled = new();
        public int ChosenIndex = -1;      // 本玩家在这间营火里已经选过的下标（-1 = 还没选）
        public bool ProceedEnabled;       // 「前进」按钮是否已启用
        public bool Busy;                 // 有按钮正在执行选中（NRestSiteButton._executingOption）
        public int Hp = -1, MaxHp = -1;
        public int DeckSize = -1, Upgraded = -1, Relics = -1;

        /// <summary>选项列表的人话指纹：<c>HEAL+</c> / <c>SMITH-</c>（+ = enabled，- = 变灰）。</summary>
        public string OptionsKey
        {
            get
            {
                var parts = new List<string>();
                for (var i = 0; i < OptionIds.Count; i++)
                    parts.Add(OptionIds[i] + (i < Enabled.Count && Enabled[i] ? "+" : "-"));
                return string.Join(",", parts);
            }
        }
    }

    /// <summary>单项营火选项的结果：成没成 / 走了哪条路 / 有没有结算完 / 是不是在等选牌 / 失败原因。</summary>
    private sealed class RestApplyResult
    {
        public bool Ok;
        public string How = "none";
        public bool Settled;
        public bool WaitingChoice;
        public string Why;
        public string Visual;
        public string OptionId;
    }

    /// <summary>主线程里「把这次选项跑起来」的结果：拿到游戏自己的 Task 才算真的开始。</summary>
    private sealed class RestStart
    {
        public Task Task;
        public string How = "none";
        public string Why;
    }

    /// <summary>
    /// 营火的发现 + pending 构造（必须在主线程调用，由 /request 触发）。
    /// 返回 null 表示「现在没有要答的营火」（不在营火 / 已经选过 / 没有选项）。
    /// </summary>
    private static Dictionary<string, object> DescribeRestPending()
    {
        if (Engine.GetMainLoop() is not SceneTree) return null;

        var room = FindRestSiteRoom();
        if (room == null)
        {
            // 离开营火 = 这次选择结束：待答请求一起清掉，别留僵尸请求。
            DropRestPending("营火界面不在场景树里");
            return null;
        }

        var view = ReadRestView(room);
        if (!view.Ok)
        {
            DropRestPending("读营火状态失败：" + (view.Why ?? "?"));
            return null;
        }

        // 选过一项不代表营火结束了：原版选完选项会全部消失，但有些场合（遗物/调试改动）
        // 还能继续选。所以判据是「界面上还剩几个可用选项」，而不是「有没有选过」。
        if (view.ChosenIndex >= 0 && view.OptionIds.Count == 0)
        {
            DropRestPending("营火选项已经全部用完，只剩「前进」");
            return null;
        }

        // 有选项正在执行（典型是「锻造」在等选牌）：这一刻不是「等玩家选营火」。
        if (view.Busy)
        {
            DropRestPending("营火选项正在执行中（按钮 _executingOption=true，可能在等选牌）");
            return null;
        }

        // 地图已经开着（刚按了「前进」，或者玩家自己打开地图）：这一刻不是「等玩家选营火」。
        if (MapScreenOpen())
        {
            DropRestPending("地图已经打开（营火已经结束）");
            return null;
        }

        var options = RestOptions(room);
        var candidates = CollectRestCandidates(room, options);
        if (candidates.Count == 0)
        {
            DropRestPending("营火没有可选项");
            return null;
        }

        var signature = RestSignature(room, candidates);
        var state = _restPending;
        if (state == null || !string.Equals(state.Signature, signature, StringComparison.Ordinal))
        {
            state = new PendingSelection
            {
                Id = NextId(),
                Min = 1,                      // 必须选一项
                Max = 1,                      // 也只能选一项
                Kind = "rest",
                Caller = "NRestSiteRoom",
                Prompt = RestPrompt,
                Screen = room,
                Cards = candidates,
                CreatedUtc = DateTime.UtcNow,
            };
            state.Signature = signature;
            _restPending = state;
            BridgeLog.Info($"rest: 新请求 id={state.Id} options={view.OptionsKey} " +
                           $"keys={DescribeRestKeys(candidates)}");
        }
        else
        {
            // 同一份请求：只刷新房间/按钮引用（界面重建后按钮会换新实例），id 不变。
            state.Screen = room;
            state.Min = 1;
            state.Max = 1;
            state.Cards = candidates;
        }

        return DescribeRestPendingJson(state);
    }

    private static Dictionary<string, object> DescribeRestPendingJson(PendingSelection pending) => new()
    {
        ["id"] = pending.Id,
        ["kind"] = "rest",
        ["prompt"] = pending.Prompt ?? RestPrompt,
        ["min"] = pending.Min,
        ["max"] = pending.Max,
        ["requireConfirm"] = false,
        ["cancelable"] = false,              // 营火必须选一项；离开不是这里的事（走 /proceed）
        ["caller"] = pending.Caller,
        ["confidence"] = "high",             // 候选/title/enabled 都来自 RestSiteOption 模型，不是扫界面
        ["animated"] = true,
        ["animation"] = RestAnimationPlan,
        ["route"] = "rest",
        ["candidates"] = pending.Cards.Select(CandidateJson).ToList(),
    };

    /// <summary>场景树里当前的营火房间（不在营火时 null）。</summary>
    private static NRestSiteRoom FindRestSiteRoom()
    {
        try
        {
            var room = NRestSiteRoom.Instance;
            if (room != null && GodotObject.IsInstanceValid(room) && room.IsInsideTree()) return room;
        }
        catch { }

        // Instance 读不到时再扫一遍（NRun 还没把场景挂上之类的边界情况）。
        if (Engine.GetMainLoop() is not SceneTree tree) return null;
        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 40000)
        {
            var node = stack.Pop();
            if (node == null) continue;
            if (node is NRestSiteRoom found)
            {
                try { if (GodotObject.IsInstanceValid(found) && found.IsInsideTree()) return found; } catch { }
            }
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return null;
    }

    /// <summary>营火的选项集合（RestSiteRoom.Options → 本玩家那份，含遗物加的选项）。</summary>
    private static IReadOnlyList<RestSiteOption> RestOptions(NRestSiteRoom room)
    {
        var empty = (IReadOnlyList<RestSiteOption>)Array.Empty<RestSiteOption>();
        if (room == null) return empty;
        try
        {
            var options = room.Options;
            return options ?? empty;
        }
        catch (Exception e)
        {
            BridgeLog.Warn("rest: 读 NRestSiteRoom.Options 失败（" + e.GetType().Name + "）：" + e.Message);
            return empty;
        }
    }

    private static string RestOptionId(RestSiteOption option)
    {
        if (option == null) return "?";
        try
        {
            var id = option.OptionId;
            if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
        }
        catch { }
        try { return option.GetType().Name; } catch { return "?"; }
    }

    /// <summary>「前进」按钮是否已启用（游戏只在选项选成功之后才 Enable，见 ShowProceedButton）。</summary>
    private static bool RestProceedEnabled(NRestSiteRoom room)
    {
        try
        {
            var button = room?.ProceedButton;
            if (button == null || !GodotObject.IsInstanceValid(button)) return false;
            return button.IsEnabled;
        }
        catch { return false; }
    }

    /// <summary>
    /// 这个选项的按钮是不是正在执行选中（<c>NRestSiteButton._executingOption</c>）。
    /// 「锻造」在等玩家选牌时会一直挂在这里 —— 这时营火不该再发一次请求，也不该再被触发一次。
    /// </summary>
    private static bool RestButtonBusy(NRestSiteRoom room, RestSiteOption option)
    {
        try
        {
            var button = room?.GetButtonForOption(option);
            if (button == null || !GodotObject.IsInstanceValid(button)) return false;
            return ReadMember(button, "_executingOption") is bool executing && executing;
        }
        catch { return false; }
    }

    /// <summary>主线程：把营火现场（选项 / 已选下标 / HP / 牌组 / 遗物 / 前进按钮）拍一张快照。</summary>
    private static RestView ReadRestView(NRestSiteRoom room)
    {
        var view = new RestView();
        if (room == null) { view.Why = "营火房间节点是 null"; return view; }

        view.Shown = IsShown(room);

        try
        {
            foreach (var option in RestOptions(room))
            {
                if (option == null)
                {
                    view.OptionIds.Add("?");
                    view.Enabled.Add(false);
                    continue;
                }
                view.OptionIds.Add(RestOptionId(option));
                try { view.Enabled.Add(option.IsEnabled); } catch { view.Enabled.Add(false); }
                try
                {
                    // 有按钮还在执行选中（典型是「锻造」在等选牌）：这时不能再发一次营火请求。
                    if (RestButtonBusy(room, option)) view.Busy = true;
                }
                catch { }
            }
        }
        catch (Exception e)
        {
            view.Why = "读营火选项失败：" + e.Message;
            return view;
        }

        try
        {
            var player = LocalPlayerModel();
            if (player != null)
            {
                try { view.Hp = player.Creature?.CurrentHp ?? -1; } catch { }
                try { view.MaxHp = player.Creature?.MaxHp ?? -1; } catch { }
                try
                {
                    var cards = player.Deck?.Cards;
                    if (cards != null)
                    {
                        view.DeckSize = cards.Count;
                        view.Upgraded = cards.Count(SafeUpgraded);
                    }
                }
                catch { }
                try { view.Relics = player.Relics?.Count ?? 0; } catch { }
                try
                {
                    var sync = RunManager.Instance?.RestSiteSynchronizer;
                    if (sync != null) view.ChosenIndex = sync.GetChosenOptionIndex(player.NetId) ?? -1;
                }
                catch { }
            }
            view.ProceedEnabled = RestProceedEnabled(room);
        }
        catch (Exception e)
        {
            view.Why = e.GetType().Name + ": " + e.Message;
            return view;
        }

        view.Ok = true;
        return view;
    }

    /// <summary>
    /// 选完之后现场到底变没变。四条任一条命中就算生效（前后都必须是成功读到的现场）。
    /// </summary>
    private static bool RestProgressed(RestView before, RestView after)
    {
        if (before == null || after == null || !before.Ok || !after.Ok) return false;

        if (after.ChosenIndex >= 0 && after.ChosenIndex != before.ChosenIndex) return true;
        if (!string.Equals(before.OptionsKey, after.OptionsKey, StringComparison.Ordinal)) return true;
        if (before.Hp != after.Hp || before.MaxHp != after.MaxHp) return true;
        if (before.DeckSize != after.DeckSize || before.Upgraded != after.Upgraded) return true;
        if (before.Relics != after.Relics) return true;
        if (!before.ProceedEnabled && after.ProceedEnabled) return true;
        return false;
    }

    /// <summary>营火候选：一项一条，key 还是「标题#同标题序号」，动态选项自然就进来了。</summary>
    private static List<Candidate> CollectRestCandidates(
        NRestSiteRoom room, IReadOnlyList<RestSiteOption> options)
    {
        var list = new List<Candidate>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (options == null) return list;

        foreach (var option in options)
        {
            if (option == null) continue;

            var title = SafeText(() => LocText(option.Title));
            if (string.IsNullOrWhiteSpace(title)) title = RestOptionId(option);
            if (string.IsNullOrWhiteSpace(title)) title = "选项";
            title = title.Trim();
            // key 是给 agent 抄的：太长的标题截断，别把整段说明塞进 key。
            if (title.Length > 20) title = title.Substring(0, 20);

            var detail = SafeText(() => LocText(option.Description)) ?? "";
            var enabled = true;
            try { enabled = option.IsEnabled; } catch { }

            list.Add(new Candidate
            {
                Key = NextKey(counts, title),
                Title = title,
                Upgraded = false,
                Cost = -1,
                RestOption = option,
                RestButton = RestButtonFor(room, option),
                OptionId = RestOptionId(option),
                Detail = detail,
                Enabled = enabled,
            });
        }
        return list;
    }

    /// <summary>这项在界面上的按钮：<c>NRestSiteRoom.GetButtonForOption(option)</c>（只用来调它自己的方法）。</summary>
    private static NRestSiteButton RestButtonFor(NRestSiteRoom room, RestSiteOption option)
    {
        if (room == null || option == null) return null;
        try
        {
            var button = room.GetButtonForOption(option);
            if (button != null && GodotObject.IsInstanceValid(button) && button.IsInsideTree()) return button;
        }
        catch (Exception e)
        {
            BridgeLog.Warn("rest: NRestSiteRoom.GetButtonForOption 失败（" + e.GetType().Name + "）：" + e.Message);
        }
        return null;
    }

    /// <summary>营火请求的签名：房间 + 候选（key 已含标题，再加 OptionId 和 enabled）。</summary>
    private static string RestSignature(NRestSiteRoom room, List<Candidate> candidates)
    {
        var parts = new List<string>
        {
            room != null ? room.GetInstanceId().ToString() : "?",
            candidates.Count.ToString(),
        };
        foreach (var c in candidates)
            parts.Add(c.Key + "=" + (c.OptionId ?? "") + (c.Enabled ? "+" : "-"));
        return string.Join("|", parts);
    }

    private static string DescribeRestKeys(List<Candidate> candidates)
        => string.Join("、", candidates.Take(8).Select(c => c.Key + "(" + (c.OptionId ?? "") + ")"))
           + (candidates.Count > 8 ? $" …（共 {candidates.Count} 项）" : "");

    /// <summary>把待答的营火请求摘掉（不动游戏状态，也不替玩家选）。</summary>
    private static void DropRestPending(string why)
    {
        var pending = _restPending;
        if (pending == null) return;
        _restPending = null;
        BridgeLog.Info($"rest: 清掉请求 id={pending.Id}（{why}）");
    }

    // ------------------------------------------------------------------ 应答

    /// <summary>
    /// 营火请求的应答：校验 → 应用（min=max=1，所以就是一项）→ 核对生效 → 才 ok。
    /// 任何 ok:false 都会把请求挂回槽里（见 RestoreRestAfterReject），所以能改一道答案再答。
    /// </summary>
    private static async Task<Dictionary<string, object>> AnswerRest(PendingSelection pending, string[] picks)
    {
        var failure = Validate(pending.Min, pending.Max, pending.Cards, picks, out var chosen, "项");
        if (failure != null) return failure;      // 校验没过：请求还在槽里，本来就能再答

        var sw = Stopwatch.StartNew();

        // 先从槽里摘掉：选择在跑的时候不能再被 /request 看到，也不能被答第二次。
        _restPending = null;
        _lastAnsweredId = pending.Id;
        _animating = true;

        Dictionary<string, object> result;
        try { result = await ApplyRestAnswer(pending, chosen, sw); }
        catch (Exception e)
        {
            BridgeLog.Warn($"rest: 应答 id={pending.Id} 抛异常（{e.GetType().Name}）：{e.Message}");
            result = Fail("选择营火选项时出错：" + e.Message);
        }
        finally { _animating = false; }

        // 任何 ok:false 都不是「已经答过了」：把请求挂回槽里、清掉回答标记。
        if (!IsTrue(result, "ok")) RestoreRestAfterReject(pending);
        return result;
    }

    /// <summary>真正去选：调用方已经摘掉槽、置了 _animating，这里只管选中与核对。</summary>
    private static async Task<Dictionary<string, object>> ApplyRestAnswer(
        PendingSelection pending, List<Candidate> chosen, Stopwatch sw)
    {
        var room = pending.Screen as NRestSiteRoom;

        try
        {
            bool shown;
            try { shown = await BridgePump.OnMain(() => IsShown(room), 3000); }
            catch { shown = false; }
            if (!shown)
                return Fail("营火界面已经不在了（已经离开营火），没法选择");

            var items = new List<Dictionary<string, object>>();
            var appliedKeys = new List<string>();
            var failed = new List<string>();
            var waitingChoice = false;
            var settled = true;
            var buttonPath = 0;

            foreach (var c in chosen)
            {
                var result = await ApplyOneRestOption(room, c);

                var item = new Dictionary<string, object>
                {
                    ["key"] = c.Key,
                    ["optionId"] = result.OptionId ?? c.OptionId ?? "",
                    ["how"] = result.How,
                    // 只有真应用成功才允许报 rest-applied；没应用成功一律 rest-none。
                    ["visual"] = result.Ok ? (result.Visual ?? RestVisualApplied) : RestVisualNone,
                    ["settled"] = result.Settled,
                    ["ok"] = result.Ok,
                };
                if (!string.IsNullOrEmpty(result.Why)) item["note"] = result.Why;
                items.Add(item);

                if (result.Ok)
                {
                    appliedKeys.Add(c.Key);
                    if (result.WaitingChoice) waitingChoice = true;
                    if (!result.Settled) settled = false;
                    if (string.Equals(result.How, "rest-button", StringComparison.Ordinal)) buttonPath++;
                }
                else
                {
                    failed.Add(c.Key + "（" + (result.Why ?? "游戏没接受") + "）");
                }

                // 留出拍子：选项自己的 PostSelectVfx（火焰 / 铁砧 / 铲子）在播。
                await Task.Delay(RestGapMs);
            }

            var waited = sw.ElapsedMilliseconds;
            var applied = appliedKeys.Count == 0 ? "（无）" : string.Join("、", appliedKeys);

            if (failed.Count > 0)
            {
                BridgeLog.Warn($"rest answered id={pending.Id} picks={applied} failed={failed.Count} " +
                               $"waitedMs={waited}");
                var dict = Fail($"有 {failed.Count} 项没应用成功：{string.Join("；", failed)}"
                                + (appliedKeys.Count > 0 ? $"（已经生效的：{applied}）" : ""));
                dict["applied"] = applied;
                dict["items"] = items;
                dict["waitedMs"] = waited;
                return dict;
            }

            BridgeLog.Info($"rest answered id={pending.Id} picks={applied} waitedMs={waited} " +
                           $"settled={settled} waitingChoice={waitingChoice} buttonPath={buttonPath}");

            var ok = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = "screen",
                ["route"] = "rest",
                ["applied"] = applied,
                ["how"] = chosen.Count == 1 ? items[0]["how"] : "rest-batch",
                ["animated"] = true,
                ["waitedMs"] = waited,
                // 走了按钮自己的选中方法（有选项 VFX + 「前进」亮起）才算 rest-applied；
                // 只走同步器 + 延迟的如实报 rest-model-delay。
                ["visual"] = buttonPath > 0 ? RestVisualApplied : "rest-model-delay",
                ["settled"] = settled,
                ["items"] = items,
            };
            if (chosen.Count == 1)
                ok["optionId"] = items[0]["optionId"];

            if (waitingChoice)
            {
                // 锻造会接着弹选牌：那是**下一个** request，别在这里等。
                ok["next"] = "card-choice";
                ok["note"] = "营火选项已受理，游戏正在等下一步（选牌）：GET /request 拿新请求（kind=upgrade）";
            }
            return ok;
        }
        finally
        {
            _animating = false;
        }
    }

    /// <summary>
    /// 这次营火请求没被接受（任何 ok:false）：恢复成「还挂在槽里、还能再答」。
    /// 只清「已应答」标记、把 pending 挂回去，不碰游戏状态 —— 所以被拒的请求不会把 id 永久锁死。
    /// 真正的 ok:true 仍然保持锁（不进这里），防止重复选择。
    /// </summary>
    private static void RestoreRestAfterReject(PendingSelection pending)
    {
        if (pending == null) return;
        if (_lastAnsweredId == pending.Id) _lastAnsweredId = -1;
        _restPending = pending;
        BridgeLog.Info($"rest: 请求 id={pending.Id} 没被接受，恢复成可再答（已挂回 _restPending）");
    }

    /// <summary>
    /// 单项营火选项：先读现场（幂等 + 事实），再让游戏自己的选中入口跑起来，
    /// 最多等 RestSettleMs，最后用现场变化核对「真的生效了没有」。
    /// </summary>
    private static async Task<RestApplyResult> ApplyOneRestOption(NRestSiteRoom room, Candidate candidate)
    {
        var option = candidate?.RestOption;
        if (option == null) return new RestApplyResult { Ok = false, Why = "候选里没有 RestSiteOption 对象" };

        RestView before;
        try { before = await BridgePump.OnMain(() => ReadRestView(room), 3000); }
        catch (Exception e) { return new RestApplyResult { Ok = false, Why = "读取营火状态失败：" + e.Message }; }
        if (before == null || !before.Ok)
            return new RestApplyResult { Ok = false, Why = "读取营火状态失败（超时或异常）：" + (before?.Why ?? "?") };

        if (!before.Shown)
            return new RestApplyResult { Ok = false, Why = "营火界面已经不在了（已经离开营火）" };

        // 幂等保护：只拦「已经没东西可选」的情况。选过一项但还有剩余选项时，
        // 这是正常的多选流程，不能按「重复选择」拒掉。
        if (before.ChosenIndex >= 0 && before.OptionIds.Count == 0)
            return new RestApplyResult
            {
                Ok = false,
                How = "already-chosen",
                Settled = true,
                Why = "营火选项已经全部用完，没有可以再选的了",
            };

        var index = RestOptionIndex(room, option);
        if (index < 0 && candidate.RestButton == null)
            return new RestApplyResult { Ok = false, Why = "这个选项已经不在营火的选项列表里了（可能刚换了一页）" };

        RestStart start;
        try { start = await BridgePump.OnMain(() => StartRestApply(room, candidate, index), 5000); }
        catch (Exception e) { return new RestApplyResult { Ok = false, Why = "启动营火选项失败：" + e.Message }; }

        if (start?.Task == null)
            return new RestApplyResult
            {
                Ok = false,
                How = start?.How ?? "none",
                Why = start?.Why ?? "没拿到游戏的选项任务",
            };

        var (state, accepted) = await WaitRestOption(start.Task, RestSettleMs, candidate.Key);

        if (state == "timeout")
        {
            // 先看一眼现场：可能已经落地了，只是那个 Task 还挂在「等下一步选牌」上。
            RestView mid = null;
            try { mid = await BridgePump.OnMain(() => ReadRestView(room), 3000); } catch { }
            var progressed = RestProgressed(before, mid);
            var waiting = HasPending;          // selector 通道已经接住了下一步选牌
            var buttonHow = string.Equals(start.How, "rest-button", StringComparison.Ordinal);

            if (!progressed && !waiting)
                return new RestApplyResult
                {
                    Ok = false,
                    How = start.How,
                    Settled = false,
                    Why = $"{RestSettleMs}ms 内游戏没有结算，现场也没变化（选项列表、" +
                          "HP、牌组、遗物、前进按钮都没动）",
                };

            return new RestApplyResult
            {
                Ok = true,
                How = start.How,
                Settled = progressed,
                WaitingChoice = waiting,
                Visual = buttonHow ? RestVisualApplied : "rest-model-delay",
                Why = waiting ? "游戏还在等下一步（选牌），这个选项要等选完才真正结算" : null,
                OptionId = candidate.OptionId,
            };
        }

        if (state == "faulted")
            return new RestApplyResult
            {
                Ok = false,
                How = start.How,
                Why = "游戏的选项任务抛异常（看 BridgeLog）",
            };

        // 模型层那条路能直接拿到 bool：false = 游戏没接受（选牌被取消之类）。
        if (!string.Equals(start.How, "rest-button", StringComparison.Ordinal) && !accepted)
            return new RestApplyResult
            {
                Ok = false,
                How = start.How,
                Settled = true,
                Why = "游戏返回 false（这次选择没被接受）",
            };

        RestView after;
        try { after = await BridgePump.OnMain(() => ReadRestView(room), 3000); }
        catch (Exception e)
        {
            return new RestApplyResult
            {
                Ok = false,
                How = start.How,
                Why = "读取选择后的营火状态失败：" + e.Message,
            };
        }
        if (after == null || !after.Ok)
            return new RestApplyResult
            {
                Ok = false,
                How = start.How,
                Why = "读取选择后的营火状态失败（超时或异常）",
            };

        if (!RestProgressed(before, after))
            return new RestApplyResult
            {
                Ok = false,
                How = start.How,
                Settled = true,
                Why = $"选项没有生效：选项列表 {before.OptionsKey} → {after.OptionsKey}，" +
                      $"HP {before.Hp}/{before.MaxHp} → {after.Hp}/{after.MaxHp}，" +
                      $"牌组 {before.DeckSize} 张（已升级 {before.Upgraded}）→ " +
                      $"{after.DeckSize} 张（已升级 {after.Upgraded}），" +
                      $"遗物 {before.Relics} → {after.Relics}，" +
                      $"前进按钮 {(before.ProceedEnabled ? "已启用" : "未启用")} → " +
                      $"{(after.ProceedEnabled ? "已启用" : "未启用")}",
            };

        return new RestApplyResult
        {
            Ok = true,
            How = start.How,
            Settled = true,
            Visual = string.Equals(start.How, "rest-button", StringComparison.Ordinal)
                ? RestVisualApplied
                : "rest-model-delay",
            OptionId = candidate.OptionId,
        };
    }

    /// <summary>这个选项在当前列表里的下标（先按引用，再按 OptionId 兜底）；没有就是 -1。</summary>
    private static int RestOptionIndex(NRestSiteRoom room, RestSiteOption option)
    {
        if (option == null) return -1;
        var options = RestOptions(room);
        for (var i = 0; i < options.Count; i++)
            if (ReferenceEquals(options[i], option)) return i;

        var wanted = RestOptionId(option);
        for (var i = 0; i < options.Count; i++)
            if (string.Equals(RestOptionId(options[i]), wanted, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// 主线程：把这次选择交给游戏自己的入口。两条路，优先按钮自己的选中方法。
    /// </summary>
    private static RestStart StartRestApply(NRestSiteRoom room, Candidate candidate, int index)
    {
        var option = candidate?.RestOption;
        if (option == null) return new RestStart { Why = "候选里没有 RestSiteOption 对象" };

        // ① 按钮自己的选中方法：NRestSiteButton.OnRelease 里调的就是 SelectOption(Option)：
        //    DisableOptions → RefreshTextState → RestSiteSynchronizer.ChooseLocalOption(index)
        //    → 成功才 AfterSelectingOption（隐藏选项 + 选项 PostSelectVfx + 亮出「前进」）。
        var button = candidate.RestButton;
        if (button == null || !GodotObject.IsInstanceValid(button) || !button.IsInsideTree())
            button = RestButtonFor(room, option);

        if (button != null && GodotObject.IsInstanceValid(button) && button.IsInsideTree())
        {
            if (ReadMember(button, "_isUnclickable") is bool unclickable && unclickable)
                return new RestStart { Why = "这个营火按钮是不可点的（_isUnclickable=true）" };

            // 已经在执行了（玩家刚亲手点过，或者上一次触发还挂在选牌上）：绝不重复触发。
            if (ReadMember(button, "_executingOption") is bool executing && executing)
                return new RestStart { Why = "这个营火按钮正在执行选中（_executingOption=true），不重复触发" };

            var select = FindMethod(button.GetType(), "SelectOption", 1);
            if (select != null)
            {
                try
                {
                    if (select.Invoke(button, new object[] { option }) is Task task)
                        return new RestStart { Task = task, How = "rest-button" };
                    BridgeLog.Warn("rest: NRestSiteButton.SelectOption 没返回 Task，改用同步器");
                }
                catch (Exception e)
                {
                    BridgeLog.Warn($"rest: NRestSiteButton.SelectOption 调失败（{e.GetType().Name}）：{e.Message}，改用同步器");
                }
            }
            else
            {
                BridgeLog.Warn("rest: NRestSiteButton 上没找到 SelectOption(option)，改用同步器");
            }
        }

        // ② 模型层兜底：就是 SelectOption 里那一句。它返回 Task<bool>（成败拿得到），
        //    但不会补界面表现 —— 成功后我们自己调一次 AfterSelectingOption(option)。
        var sync = RunManager.Instance?.RestSiteSynchronizer;
        if (sync == null) return new RestStart { Why = "RunManager.RestSiteSynchronizer 不可用" };
        if (index < 0) return new RestStart { Why = "算不出这个选项在营火列表里的下标" };

        try
        {
            var task = sync.ChooseLocalOption(index);
            if (task == null) return new RestStart { Why = "ChooseLocalOption 返回 null" };
            _ = AfterRestSelecting(room, option, task);
            return new RestStart { Task = task, How = "rest-model" };
        }
        catch (Exception e)
        {
            return new RestStart { Why = "ChooseLocalOption 抛了：" + e.Message };
        }
    }

    /// <summary>兜底路径的收尾：选择真的成功之后，补一次界面表现（AfterSelectingOption）。</summary>
    private static async Task AfterRestSelecting(NRestSiteRoom room, RestSiteOption option, Task<bool> task)
    {
        try
        {
            if (!await task) return;
            await BridgePump.OnMain(() =>
            {
                if (!IsShown(room)) return 0;
                room.AfterSelectingOption(option);   // 隐藏选项 + PostSelectVfx + 亮出「前进」
                return 0;
            }, 3000);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("rest: 兜底路径补界面表现失败（" + e.GetType().Name + "）：" + e.Message);
        }
    }

    /// <summary>等一个营火任务，但最多等 ms；返回 (done/timeout/faulted, 任务布尔结果)。</summary>
    private static async Task<(string State, bool Result)> WaitRestOption(Task task, int ms, string what)
    {
        if (task == null) return ("done", false);
        try
        {
            var winner = await Task.WhenAny(task, Task.Delay(ms));
            if (winner != task)
            {
                // 超时也要观察异常，否则会变成 UnobservedTaskException。
                _ = WatchRestSafely(task, what);
                return ("timeout", false);
            }
            if (task is Task<bool> boolTask) return ("done", await boolTask);
            await task;
            return ("done", true);
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"rest: {what} 的选项任务抛了（{e.GetType().Name}）：{e.Message}");
            return ("faulted", false);
        }
    }

    /// <summary>只为「把异常吃掉并写日志」：不改变任何行为。</summary>
    private static async Task WatchRestSafely(Task task, string what)
    {
        try { await task; }
        catch (Exception e)
        {
            BridgeLog.Warn($"rest: {what} 后续抛了（{e.GetType().Name}）：{e.Message}");
        }
    }

    // =================================================================== 事件通道（NEventRoom + EventModel.CurrentOptions）

    /*
     * 事件 = 「选一个选项推进剧情」。min = max = 1；「离开」如果作为选项存在（IsProceed），
     * 也照常在候选里，一样用 /respond 答（它走 option.Chosen()，等于界面上的「继续」）。
     *
     * 数据源（都在主线程读）：
     *   * 房间节点：MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom
     *     （NEventRoom.Instance = NRun.Instance.EventRoom = 房间容器当前场景，不在事件时 null）。
     *   * 事件模型：NEventRoom 的 private 字段 _event : EventModel
     *     （界面 SetOptions/RefreshEventState 用的就是它）；
     *     读不到再退到 RunManager.Instance.EventSynchronizer.GetLocalEvent()，
     *     最后退到当前房间 EventRoom.LocalMutableEvent。
     *   * 候选：EventModel.CurrentOptions : IReadOnlyList<EventOption>，每项：
     *       TextKey(string) / Title(LocString) / Description(LocString) /
     *       IsLocked(bool，锁住 = 点了没反应) / IsProceed(bool，是「继续/离开」而不是分支) /
     *       WasChosen(bool) / WillKillPlayer(Func<Player,bool>，会致死) / Relic。
     *     事件结束时 CurrentOptions 会被清空，界面另外造一个 IsProceed=true 的 PROCEED 选项按钮
     *     （NEventRoom.SetOptions 里 new EventOption(event, Proceed, "PROCEED", false, true)），
     *     这个合成选项不在模型列表里，我们按「界面上真有的那个按钮」把它也当候选发出去，
     *     这样 agent 用 /respond 就能离开事件，不用指望 /proceed。
     *
     * 应答（不模拟鼠标、不点坐标）：调公开方法 NEventRoom.OptionButtonClicked(option, index)，
     * 就是 NEventOptionButton.OnRelease 里那一句：
     *   IsLocked → 直接 return（点了没反应）；
     *   IsProceed → option.Chosen()（= NEventRoom.Proceed()：打开地图）；
     *   否则：共享事件之外先 Layout.ClearOptions()，
     *        再 RunManager.Instance.EventSynchronizer.ChooseLocalOption(index)
     *        → ChooseOptionForEvent：option = CurrentOptions[index]（**这个 index 就是 CurrentOptions 的下标**，
     *          越界会抛 InvalidOperationException，所以必须先核对），
     *          _pendingOptionTasks.Add(RunSafely(option.Chosen()))，
     *          再 SaveEventOptionToHistory。
     *   所以「等它跑完」不能用 OptionButtonClicked 的返回值（void），
     *   要等 RunManager.Instance.EventSynchronizer.AwaitPendingOptionTasks()。
     *   IsProceed 的选项没有进那张表，直接 await option.Chosen()。
     *
     * 验证：选完之后至少命中一条才算推进 ——
     *   ① 选项集合 / 事件描述文本变了（事件换页）；② IsFinished 从 false 变 true；
     *   ③ 事件房间没了；④ 进了内嵌战斗（CombatManager.IsInProgress 从 false 变 true）；
     *   ⑤ IsProceed 的选项：地图真的打开了（NMapScreen.IsOpen）。
     *   一条都不命中就 ok:false，并把前后两组事实写进 error。
     *
     * 连锁：事件选项经常先弹卡牌选择（升级 / 移除 / 变形）或衍生奖励，那一步是**下一个** request：
     *   单项最多等 EventSettleMs，超时按「已受理、下一步是选牌」返回 next:"card-choice"。
     */

    /// <summary>事件现场的指纹（选之前读一次、选之后读一次）。</summary>
    private sealed class EventView
    {
        public bool Ok;
        public string Why;
        public bool Shown;
        public bool Finished;
        public bool InCombat;
        public int ButtonCount;
        public int VisibleButtons;
        public string Key = "";
        public string Keys = "";
    }

    /// <summary>单项事件选项的结果。</summary>
    private sealed class EventApplyResult
    {
        public bool Ok;
        public string How = "none";
        public bool Settled;
        public bool WaitingChoice;
        public bool Proceed;
        public string Why;
        public string Visual;
        public string OptionId;
    }

    /// <summary>主线程里「把这次选项跑起来」的结果。</summary>
    private sealed class EventStart
    {
        public Task Task;
        public string How = "none";
        public string Why;
        public int Index = -1;
    }

    /// <summary>
    /// 事件的发现 + pending 构造（必须在主线程调用，由 /request 触发）。
    /// 返回 null 表示「现在没有要答的事件」（不在事件 / 内嵌战斗 / 选项全锁 / 没有选项）。
    /// </summary>
    private static Dictionary<string, object> DescribeEventPending()
    {
        if (Engine.GetMainLoop() is not SceneTree) return null;

        var room = FindEventRoom();
        if (room == null)
        {
            DropEventPending("事件界面不在场景树里");
            return null;
        }

        var model = ReadEventModel(room);
        if (model == null)
        {
            DropEventPending("读不到 NEventRoom 的事件模型");
            return null;
        }

        var view = ReadEventView(room, model);
        if (!view.Ok)
        {
            DropEventPending("读事件状态失败：" + (view.Why ?? "?"));
            return null;
        }
        if (!view.Shown)
        {
            DropEventPending("事件房间节点不可见");
            return null;
        }

        // 内嵌战斗 / 选项动画中：选项按钮都不可见，现在不是「在等玩家选事件」。
        if (view.ButtonCount > 0 && view.VisibleButtons == 0)
        {
            DropEventPending("事件选项按钮都不可见（内嵌战斗或动画中）");
            return null;
        }

        // 地图已经开着（事件刚结束、刚点过「继续」）：这一刻不是「等玩家选事件」。
        if (MapScreenOpen())
        {
            DropEventPending("地图已经打开（事件已经结束）");
            return null;
        }

        var candidates = CollectEventCandidates(room, model);
        if (candidates.Count == 0)
        {
            DropEventPending(model.IsFinished ? "事件已经结束（没有可选按钮）" : "事件的当前选项为空");
            return null;
        }

        // 全是 IsLocked 的选项时游戏根本不接受任何回答（OnRelease 直接 return），
        // 那种状态下不发请求，免得 agent 反复答一个必然失败的请求。
        if (!candidates.Any(c => c.Enabled))
        {
            DropEventPending("所有事件选项都是锁住的（IsLocked）");
            return null;
        }

        var signature = EventSignature(room, candidates, view);
        var state = _eventPending;
        if (state == null || !string.Equals(state.Signature, signature, StringComparison.Ordinal))
        {
            state = new PendingSelection
            {
                Id = NextId(),
                Min = 1,
                Max = 1,
                Kind = "event",
                Caller = "NEventRoom",
                Prompt = EventPromptFor(model),
                Screen = room,
                Cards = candidates,
                CreatedUtc = DateTime.UtcNow,
            };
            state.Signature = signature;
            _eventPending = state;
            BridgeLog.Info($"event: 新请求 id={state.Id} finished={view.Finished} " +
                           $"keys={DescribeEventKeys(candidates)}");
        }
        else
        {
            state.Screen = room;
            state.Min = 1;
            state.Max = 1;
            state.Cards = candidates;
            state.Prompt = EventPromptFor(model);
        }

        return DescribeEventPendingJson(state, model);
    }

    private static Dictionary<string, object> DescribeEventPendingJson(PendingSelection pending, EventModel model) => new()
    {
        ["id"] = pending.Id,
        ["kind"] = "event",
        ["prompt"] = pending.Prompt ?? EventPromptFallback,
        ["min"] = pending.Min,
        ["max"] = pending.Max,
        ["requireConfirm"] = false,
        ["cancelable"] = false,              // 事件必须选一项
        ["caller"] = pending.Caller,
        ["confidence"] = "high",             // 候选都来自 EventModel.CurrentOptions / 界面自己的按钮
        ["animated"] = true,
        ["animation"] = EventAnimationPlan,
        ["route"] = "event",
        ["candidates"] = pending.Cards.Select(CandidateJson).ToList(),
    };

    /// <summary>事件 prompt：优先「事件『标题』：选择一项」，标题读不到就用兜底句。</summary>
    private static string EventPromptFor(EventModel model)
    {
        var title = SafeText(() => LocText(model?.Title));
        return string.IsNullOrWhiteSpace(title)
            ? EventPromptFallback
            : "事件「" + title.Trim() + "」：选择一个选项";
    }

    /// <summary>场景树里当前的事件房间（不在事件时 null）。</summary>
    private static NEventRoom FindEventRoom()
    {
        try
        {
            var room = NEventRoom.Instance;
            if (room != null && GodotObject.IsInstanceValid(room) && room.IsInsideTree()) return room;
        }
        catch { }

        if (Engine.GetMainLoop() is not SceneTree tree) return null;
        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 40000)
        {
            var node = stack.Pop();
            if (node == null) continue;
            if (node is NEventRoom found)
            {
                try { if (GodotObject.IsInstanceValid(found) && found.IsInsideTree()) return found; } catch { }
            }
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return null;
    }

    /// <summary>事件模型：界面自己的 _event 优先，其次事件同步器，最后当前房间的 LocalMutableEvent。</summary>
    private static EventModel ReadEventModel(NEventRoom room)
    {
        if (room != null)
        {
            try { if (ReadMember(room, "_event") is EventModel model) return model; } catch { }
        }
        try
        {
            var local = RunManager.Instance?.EventSynchronizer?.GetLocalEvent();
            if (local != null) return local;
        }
        catch { }
        try
        {
            if (RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom is MegaCrit.Sts2.Core.Rooms.EventRoom eventRoom)
                return eventRoom.LocalMutableEvent;
        }
        catch { }
        return null;
    }

    private static IReadOnlyList<EventOption> EventOptions(EventModel model)
    {
        var empty = (IReadOnlyList<EventOption>)Array.Empty<EventOption>();
        if (model == null) return empty;
        try { return model.CurrentOptions ?? empty; }
        catch (Exception e)
        {
            BridgeLog.Warn("event: 读 EventModel.CurrentOptions 失败（" + e.GetType().Name + "）：" + e.Message);
            return empty;
        }
    }

    /// <summary>一个选项的稳定指纹：TextKey + 状态位（TextKey 是 string，比标题稳）。</summary>
    private static string EventOptionKey(EventOption option)
    {
        if (option == null) return "?";
        var key = "";
        try { key = option.TextKey ?? ""; } catch { }
        var locked = false;
        try { locked = option.IsLocked; } catch { }
        var proceed = false;
        try { proceed = option.IsProceed; } catch { }
        var chosen = false;
        try { chosen = option.WasChosen; } catch { }
        if (string.IsNullOrEmpty(key)) key = option.GetType().Name;
        return key + (locked ? "!" : "") + (proceed ? "@" : "") + (chosen ? "~" : "");
    }

    /// <summary>主线程：把事件现场（选项集合 / 描述 / 是否结束 / 是否在战斗 / 按钮可见性）拍一张快照。</summary>
    private static EventView ReadEventView(NEventRoom room, EventModel model)
    {
        var view = new EventView();
        if (room == null) { view.Why = "事件房间节点是 null"; return view; }

        view.Shown = IsShown(room);
        try { view.InCombat = CombatInProgress(); } catch { }

        var keys = new List<string>();
        try
        {
            if (model != null) view.Finished = model.IsFinished;
            foreach (var option in EventOptions(model)) keys.Add(EventOptionKey(option));
            var description = SafeText(() => LocText(model?.Description)) ?? "";
            keys.Add("desc=" + description);
        }
        catch (Exception e)
        {
            view.Why = "读事件选项失败：" + e.Message;
            return view;
        }

        // 界面上的选项按钮：用来判断「现在是不是真的在等玩家选事件」（内嵌战斗/动画时都不可见），
        // 也用来把事件结束后的合成 PROCEED 按钮当成候选。
        try
        {
            var layout = room.Layout;
            var buttons = layout?.OptionButtons;
            if (buttons != null)
            {
                foreach (var button in buttons)
                {
                    if (button == null || !GodotObject.IsInstanceValid(button)) continue;
                    view.ButtonCount++;
                    if (IsShown(button)) view.VisibleButtons++;
                }
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("event: 读 NEventLayout.OptionButtons 失败（" + e.GetType().Name + "）：" + e.Message);
        }

        view.Keys = string.Join(",", keys);
        view.Key = view.Finished + "|" + view.Keys;
        view.Ok = true;
        return view;
    }

    private static bool CombatInProgress()
    {
        try { return MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.IsInProgress ?? false; }
        catch { return false; }
    }

    /// <summary>事件到底推进了没有（前后都必须是成功读到的现场）。</summary>
    private static bool EventProgressed(EventView before, EventView after)
    {
        if (before == null || after == null || !before.Ok || !after.Ok) return false;
        if (!before.Finished && after.Finished) return true;
        if (!string.Equals(before.Keys, after.Keys, StringComparison.Ordinal)) return true;
        if (!before.InCombat && after.InCombat) return true;
        if (!after.Shown) return true;                    // 房间没了
        if (after.ButtonCount != before.ButtonCount) return true;
        return false;
    }

    /// <summary>
    /// 事件候选：CurrentOptions 一项一条；事件已经结束时，把界面上那个合成 PROCEED 按钮当一项
    /// （IsProceed=true），这样离开事件也能用 /respond 答。
    /// </summary>
    private static List<Candidate> CollectEventCandidates(NEventRoom room, EventModel model)
    {
        var list = new List<Candidate>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var player = LocalPlayerModel();

        var buttons = EventButtons(room);
        var options = EventOptions(model);
        for (var i = 0; i < options.Count; i++)
            AddEventCandidate(list, counts, player, options[i], i, EventButtonFor(buttons, options[i]));

        // 事件结束：模型里的选项已经清空，界面上只剩一个 IsProceed 的合成 PROCEED 按钮。
        if (list.Count == 0)
        {
            foreach (var entry in buttons)
            {
                var option = entry.Option;
                if (option == null) continue;
                var proceed = false;
                try { proceed = option.IsProceed; } catch { }
                if (!proceed) continue;
                AddEventCandidate(list, counts, player, option, 0, entry.Button);
                break;
            }
        }

        return list;
    }

    private static void AddEventCandidate(
        List<Candidate> list, Dictionary<string, int> counts, MegaCrit.Sts2.Core.Entities.Players.Player player,
        EventOption option, int index, NEventOptionButton button)
    {
        if (option == null) return;

        var title = SafeText(() => LocText(option.Title));
        if (string.IsNullOrWhiteSpace(title)) title = SafeText(() => option.TextKey);
        if (string.IsNullOrWhiteSpace(title)) title = "选项";
        title = title.Trim();
        if (title.Length > 20) title = title.Substring(0, 20);

        var detail = SafeText(() => LocText(option.Description)) ?? "";
        var textKey = SafeText(() => option.TextKey) ?? "";

        var locked = false;
        try { locked = option.IsLocked; } catch { }
        var proceed2 = false;
        try { proceed2 = option.IsProceed; } catch { }
        var willKill = false;
        try
        {
            var fn = option.WillKillPlayer;
            willKill = fn != null && player != null && fn(player);
        }
        catch { }

        list.Add(new Candidate
        {
            Key = NextKey(counts, title),
            Title = title,
            Upgraded = false,
            Cost = -1,
            EventOptionRef = option,
            EventButton = button,
            EventIndex = index,
            IsProceed = proceed2,
            OptionId = textKey,
            Detail = detail,
            Enabled = !locked,
            WillKill = willKill,
        });
    }

    /// <summary>界面上现在挂了哪些选项按钮（按 Layout.OptionButtons 的顺序）。</summary>
    private static List<(EventOption Option, NEventOptionButton Button)> EventButtons(NEventRoom room)
    {
        var list = new List<(EventOption, NEventOptionButton)>();
        if (room == null) return list;
        try
        {
            var layout = room.Layout;
            var buttons = layout?.OptionButtons;
            if (buttons == null) return list;
            foreach (var button in buttons)
            {
                if (button == null || !GodotObject.IsInstanceValid(button)) continue;
                EventOption option = null;
                try { option = button.Option; } catch { }
                if (option == null) continue;
                list.Add((option, button));
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("event: 枚举事件选项按钮失败（" + e.GetType().Name + "）：" + e.Message);
        }
        return list;
    }

    private static NEventOptionButton EventButtonFor(
        List<(EventOption Option, NEventOptionButton Button)> buttons, EventOption option)
    {
        if (buttons == null || option == null) return null;
        foreach (var entry in buttons)
            if (ReferenceEquals(entry.Option, option)) return entry.Button;
        return null;
    }

    /// <summary>
    /// 事件请求的签名：房间 + 候选（key 已含标题，再加 TextKey / 锁 / 继续 / 致死）+ 事件是否已结束。
    /// 故意**不**放描述文本：它随事件页变，但也可能被本地化/动态数值抖动。
    /// </summary>
    private static string EventSignature(NEventRoom room, List<Candidate> candidates, EventView view)
    {
        var parts = new List<string>
        {
            room != null ? room.GetInstanceId().ToString() : "?",
            candidates.Count.ToString(),
            view != null && view.Finished ? "finished" : "open",
        };
        foreach (var c in candidates)
            parts.Add(c.Key + "=" + (c.OptionId ?? "") + (c.Enabled ? "+" : "-")
                      + (c.IsProceed ? "@" : "") + (c.WillKill ? "!" : ""));
        return string.Join("|", parts);
    }

    private static string DescribeEventKeys(List<Candidate> candidates)
        => string.Join("、", candidates.Take(8).Select(c => c.Key + (c.IsProceed ? "(继续)" : "")))
           + (candidates.Count > 8 ? $" …（共 {candidates.Count} 项）" : "");

    /// <summary>把待答的事件请求摘掉（不动游戏状态）。</summary>
    private static void DropEventPending(string why)
    {
        var pending = _eventPending;
        if (pending == null) return;
        _eventPending = null;
        BridgeLog.Info($"event: 清掉请求 id={pending.Id}（{why}）");
    }

    // ------------------------------------------------------------------ 应答

    /// <summary>
    /// 事件请求的应答：校验 → 应用（min=max=1）→ 核对推进 → 才 ok。
    /// 任何 ok:false 都会把请求挂回槽里（见 RestoreEventAfterReject），所以能改一道答案再答。
    /// </summary>
    private static async Task<Dictionary<string, object>> AnswerEvent(PendingSelection pending, string[] picks)
    {
        var failure = Validate(pending.Min, pending.Max, pending.Cards, picks, out var chosen, "项");
        if (failure != null) return failure;

        var sw = Stopwatch.StartNew();

        _eventPending = null;
        _lastAnsweredId = pending.Id;
        _animating = true;

        Dictionary<string, object> result;
        try { result = await ApplyEventAnswer(pending, chosen, sw); }
        catch (Exception e)
        {
            BridgeLog.Warn($"event: 应答 id={pending.Id} 抛异常（{e.GetType().Name}）：{e.Message}");
            result = Fail("选择事件选项时出错：" + e.Message);
        }
        finally { _animating = false; }

        if (!IsTrue(result, "ok")) RestoreEventAfterReject(pending);
        return result;
    }

    /// <summary>真正去选：调用方已经摘掉槽、置了 _animating，这里只管选中与核对。</summary>
    private static async Task<Dictionary<string, object>> ApplyEventAnswer(
        PendingSelection pending, List<Candidate> chosen, Stopwatch sw)
    {
        var room = pending.Screen as NEventRoom;

        try
        {
            bool shown;
            try { shown = await BridgePump.OnMain(() => IsShown(room), 3000); }
            catch { shown = false; }
            if (!shown)
                return Fail("事件界面已经不在了（已经离开事件），没法选择");

            var items = new List<Dictionary<string, object>>();
            var appliedKeys = new List<string>();
            var failed = new List<string>();
            var waitingChoice = false;
            var settled = true;

            foreach (var c in chosen)
            {
                var result = await ApplyOneEventOption(room, c);

                var item = new Dictionary<string, object>
                {
                    ["key"] = c.Key,
                    ["optionId"] = result.OptionId ?? c.OptionId ?? "",
                    ["isProceed"] = c.IsProceed,
                    ["how"] = result.How,
                    ["visual"] = result.Ok ? (result.Visual ?? EventVisualApplied) : EventVisualNone,
                    ["settled"] = result.Settled,
                    ["ok"] = result.Ok,
                };
                if (!string.IsNullOrEmpty(result.Why)) item["note"] = result.Why;
                items.Add(item);

                if (result.Ok)
                {
                    appliedKeys.Add(c.Key);
                    if (result.WaitingChoice) waitingChoice = true;
                    if (!result.Settled) settled = false;
                }
                else
                {
                    failed.Add(c.Key + "（" + (result.Why ?? "游戏没接受") + "）");
                }

                await Task.Delay(EventGapMs);
            }

            var waited = sw.ElapsedMilliseconds;
            var applied = appliedKeys.Count == 0 ? "（无）" : string.Join("、", appliedKeys);

            if (failed.Count > 0)
            {
                BridgeLog.Warn($"event answered id={pending.Id} picks={applied} failed={failed.Count} " +
                               $"waitedMs={waited}");
                var dict = Fail($"有 {failed.Count} 项没应用成功：{string.Join("；", failed)}"
                                + (appliedKeys.Count > 0 ? $"（已经生效的：{applied}）" : ""));
                dict["applied"] = applied;
                dict["items"] = items;
                dict["waitedMs"] = waited;
                return dict;
            }

            BridgeLog.Info($"event answered id={pending.Id} picks={applied} waitedMs={waited} " +
                           $"settled={settled} waitingChoice={waitingChoice}");

            var ok = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = "screen",
                ["route"] = "event",
                ["applied"] = applied,
                ["how"] = chosen.Count == 1 ? items[0]["how"] : "event-batch",
                ["animated"] = true,
                ["waitedMs"] = waited,
                ["visual"] = EventVisualApplied,
                ["settled"] = settled,
                ["items"] = items,
            };
            if (chosen.Count == 1)
            {
                ok["optionId"] = items[0]["optionId"];
                if (chosen[0].IsProceed) ok["proceed"] = true;
            }

            if (waitingChoice)
            {
                ok["next"] = "card-choice";
                ok["note"] = "事件选项已受理，游戏正在等下一步（选牌）：GET /request 拿新请求";
            }
            return ok;
        }
        finally
        {
            _animating = false;
        }
    }

    /// <summary>
    /// 这次事件请求没被接受（任何 ok:false）：恢复成「还挂在槽里、还能再答」。
    /// 只清「已应答」标记、把 pending 挂回去，不碰游戏状态。
    /// </summary>
    private static void RestoreEventAfterReject(PendingSelection pending)
    {
        if (pending == null) return;
        if (_lastAnsweredId == pending.Id) _lastAnsweredId = -1;
        _eventPending = pending;
        BridgeLog.Info($"event: 请求 id={pending.Id} 没被接受，恢复成可再答（已挂回 _eventPending）");
    }

    /// <summary>
    /// 单项事件选项：先读现场（幂等 + 事实），再让游戏自己的入口跑起来，
    /// 最多等 EventSettleMs，最后用现场变化核对「事件真的推进了没有」。
    /// </summary>
    private static async Task<EventApplyResult> ApplyOneEventOption(NEventRoom room, Candidate candidate)
    {
        var option = candidate?.EventOptionRef;
        if (option == null) return new EventApplyResult { Ok = false, Why = "候选里没有 EventOption 对象" };

        EventView before;
        try { before = await BridgePump.OnMain(() => ReadEventView(room, ReadEventModel(room)), 3000); }
        catch (Exception e) { return new EventApplyResult { Ok = false, Why = "读取事件状态失败：" + e.Message }; }
        if (before == null || !before.Ok)
            return new EventApplyResult { Ok = false, Why = "读取事件状态失败（超时或异常）：" + (before?.Why ?? "?") };

        if (!before.Shown)
            return new EventApplyResult { Ok = false, Why = "事件界面已经不在了（已经离开事件）" };

        // 幂等保护：这个选项已经被选过了（DisableOnChosen 的选项游戏自己也会拒绝）。
        var wasChosen = false;
        try { wasChosen = option.WasChosen; } catch { }
        if (wasChosen)
            return new EventApplyResult
            {
                Ok = false,
                How = "already-chosen",
                Settled = true,
                Why = "应答之前这个选项就已经被选过了（不重复选择）",
            };

        EventStart start;
        try { start = await BridgePump.OnMain(() => StartEventApply(room, candidate), 5000); }
        catch (Exception e) { return new EventApplyResult { Ok = false, Why = "启动事件选项失败：" + e.Message }; }

        if (start?.Task == null)
            return new EventApplyResult
            {
                Ok = false,
                How = start?.How ?? "none",
                Why = start?.Why ?? "没拿到游戏的选项任务",
            };

        var (state, _) = await WaitEventOption(start.Task, EventSettleMs, candidate.Key);

        if (state == "timeout")
        {
            var waiting = HasPending;          // selector 通道已经接住了下一步选牌
            EventView mid = null;
            try { mid = await BridgePump.OnMain(() => ReadEventView(room, ReadEventModel(room)), 3000); }
            catch { }
            var progressed = EventProgressed(before, mid);

            if (!progressed && !waiting)
                return new EventApplyResult
                {
                    Ok = false,
                    How = start.How,
                    Settled = false,
                    Why = $"{EventSettleMs}ms 内游戏没有结算，现场也没变化（选项集合、事件描述、" +
                          "IsFinished、战斗状态都没动）",
                };

            return new EventApplyResult
            {
                Ok = true,
                How = start.How,
                Settled = progressed,
                WaitingChoice = waiting,
                Proceed = candidate.IsProceed,
                Visual = EventVisualApplied,
                Why = waiting ? "游戏还在等下一步（选牌），事件要等选完才推进" : null,
                OptionId = candidate.OptionId,
            };
        }

        if (state == "faulted")
            return new EventApplyResult
            {
                Ok = false,
                How = start.How,
                Why = "游戏的选项任务抛异常（看 BridgeLog）",
            };

        EventView after;
        try { after = await BridgePump.OnMain(() => ReadEventView(room, ReadEventModel(room)), 3000); }
        catch (Exception e)
        {
            return new EventApplyResult
            {
                Ok = false,
                How = start.How,
                Why = "读取选择后的事件状态失败：" + e.Message,
            };
        }
        if (after == null || !after.Ok)
            return new EventApplyResult
            {
                Ok = false,
                How = start.How,
                Why = "读取选择后的事件状态失败（超时或异常）",
            };

        // 「继续/离开」的验收标准是地图真的开了（NEventRoom.Proceed 就是打开地图）；
        // 万一这个 IsProceed 选项的 OnChosen 干的是别的事，事件有推进也算成功。
        if (candidate.IsProceed)
        {
            var mapOpen = MapScreenOpen();
            var moved = EventProgressed(before, after);
            if (!mapOpen && !moved)
                return new EventApplyResult
                {
                    Ok = false,
                    How = start.How,
                    Settled = true,
                    Proceed = true,
                    Why = "「继续」已经发出去了，但地图没打开、事件也没推进（事件还在）",
                };
            return new EventApplyResult
            {
                Ok = true,
                How = start.How,
                Settled = true,
                Proceed = true,
                Visual = EventVisualApplied,
                OptionId = candidate.OptionId,
            };
        }

        if (!EventProgressed(before, after))
            return new EventApplyResult
            {
                Ok = false,
                How = start.How,
                Settled = true,
                Why = $"事件没有推进：选项 {ShortKeys(before.Keys)} → {ShortKeys(after.Keys)}，" +
                      $"IsFinished {before.Finished} → {after.Finished}，" +
                      $"按钮数 {before.ButtonCount} → {after.ButtonCount}，" +
                      $"战斗中 {before.InCombat} → {after.InCombat}",
            };

        return new EventApplyResult
        {
            Ok = true,
            How = start.How,
            Settled = true,
            Visual = EventVisualApplied,
            OptionId = candidate.OptionId,
        };
    }

    /// <summary>日志/文案里用的短指纹（选项集合可能很长）。</summary>
    private static string ShortKeys(string keys)
    {
        if (string.IsNullOrEmpty(keys)) return "（空）";
        return keys.Length <= 80 ? keys : keys.Substring(0, 80) + "…";
    }

    private static bool MapScreenOpen()
    {
        try
        {
            var map = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance;
            return map != null && GodotObject.IsInstanceValid(map) && map.IsOpen;
        }
        catch { return false; }
    }

    /// <summary>这个选项在 EventModel.CurrentOptions 里的下标（先按引用，再按 TextKey 兜底）；没有就是 -1。</summary>
    private static int IndexOfEventOption(EventModel model, EventOption option)
    {
        if (option == null) return -1;
        var options = EventOptions(model);
        for (var i = 0; i < options.Count; i++)
            if (ReferenceEquals(options[i], option)) return i;

        var wanted = "";
        try { wanted = option.TextKey ?? ""; } catch { }
        if (wanted.Length == 0) return -1;
        for (var i = 0; i < options.Count; i++)
        {
            var key = "";
            try { key = options[i]?.TextKey ?? ""; } catch { }
            if (string.Equals(key, wanted, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>
    /// 主线程：把这次选择交给游戏自己的入口。
    /// 普通选项走公开方法 <c>NEventRoom.OptionButtonClicked(option, index)</c>；
    /// 「继续/离开」走 <c>option.Chosen()</c>（OptionButtonClicked 对它是同一句，
    /// 只是它把 Task 丢给 RunSafely，我们要拿到 Task 才能等）。
    /// </summary>
    private static EventStart StartEventApply(NEventRoom room, Candidate candidate)
    {
        var option = candidate?.EventOptionRef;
        if (option == null) return new EventStart { Why = "候选里没有 EventOption 对象" };

        var model = ReadEventModel(room);
        if (model == null) return new EventStart { Why = "读不到事件模型（EventModel）" };

        var isProceed = false;
        try { isProceed = option.IsProceed; } catch { }
        var isLocked = false;
        try { isLocked = option.IsLocked; } catch { }

        // ① 「继续/离开」：OptionButtonClicked 走的就是 option.Chosen()（= NEventRoom.Proceed()）。
        if (isProceed)
        {
            if (isLocked) return new EventStart { Why = "这个「继续」选项是锁住的（IsLocked）" };
            try
            {
                var task = option.Chosen();
                if (task == null) return new EventStart { Why = "option.Chosen() 返回 null" };
                FlashEventButton(candidate);
                return new EventStart { Task = task, How = "event-proceed", Index = 0 };
            }
            catch (Exception e)
            {
                return new EventStart { Why = "option.Chosen() 抛了：" + e.Message };
            }
        }

        if (isLocked) return new EventStart { Why = "这个选项是锁住的（IsLocked），游戏会直接忽略点击" };

        var index = IndexOfEventOption(model, option);
        if (index < 0)
            return new EventStart { Why = "这个选项已经不在事件的当前选项里了（可能刚换了一页）" };
        if (candidate.EventIndex >= 0 && candidate.EventIndex != index)
            BridgeLog.Info($"event: 选项下标从 {candidate.EventIndex} 变成 {index}（事件换过页），按最新的来");

        // ChooseOptionForEvent 里还要拿这个 index 索引 CurrentOptions，越界会抛
        // InvalidOperationException，所以上面先核对过。
        var sync = RunManager.Instance?.EventSynchronizer;
        if (sync == null) return new EventStart { Why = "RunManager.EventSynchronizer 不可用" };

        try
        {
            room.OptionButtonClicked(option, index);
        }
        catch (Exception e)
        {
            return new EventStart
            {
                Why = "NEventRoom.OptionButtonClicked 抛了：" + (e.InnerException ?? e).Message,
                Index = index,
            };
        }

        // 选项按钮自己的确认反馈（NEventOptionButton.FlashConfirmation）是 best effort：
        // 这个按钮马上可能被销毁（事件换页会 ClearOptions），所以丢后台跑，失败只写日志。
        FlashEventButton(candidate);

        // OptionButtonClicked 是 void（内部把 option.Chosen() 丢给 RunSafely 并挂进 _pendingOptionTasks），
        // 所以等它跑完要靠 EventSynchronizer.AwaitPendingOptionTasks()。
        try
        {
            var waited = sync.AwaitPendingOptionTasks();
            if (waited == null)
                return new EventStart { How = "event-option-button", Index = index };
            return new EventStart
            {
                Task = waited,
                How = "event-option-button",
                Index = index,
            };
        }
        catch (Exception e)
        {
            return new EventStart { Why = "AwaitPendingOptionTasks 抛了：" + e.Message, Index = index };
        }
    }

    /// <summary>
    /// 让被选中的那个选项按钮播一下它自己的确认反馈（FlashConfirmation）。
    /// 纯表现、best effort：按钮可能已经被换页销毁，任何异常只写日志，不影响结果。
    /// </summary>
    private static void FlashEventButton(Candidate candidate)
    {
        var button = candidate?.EventButton;
        if (button == null || !GodotObject.IsInstanceValid(button)) return;

        var flash = FindMethod(button.GetType(), "FlashConfirmation", 0);
        if (flash == null) return;

        try
        {
            // 已经在主线程（StartEventApply 由 BridgePump.OnMain 调进来），直接调即可。
            if (flash.Invoke(button, null) is Task task)
                _ = WatchEventSafely(task, "FlashConfirmation " + (candidate.Key ?? ""));
            else
                BridgeLog.Info($"event: FlashConfirmation 没返回 Task（{candidate.Key}）");
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"event: FlashConfirmation 失败（{e.GetType().Name}）：{e.Message}（{candidate.Key}）");
        }
    }

    /// <summary>等一个事件任务，但最多等 ms；返回 (done/timeout/faulted, 任务布尔结果)。</summary>
    private static async Task<(string State, bool Result)> WaitEventOption(Task task, int ms, string what)
    {
        if (task == null) return ("done", false);
        try
        {
            var winner = await Task.WhenAny(task, Task.Delay(ms));
            if (winner != task)
            {
                _ = WatchEventSafely(task, what);
                return ("timeout", false);
            }
            if (task is Task<bool> boolTask) return ("done", await boolTask);
            await task;
            return ("done", true);
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"event: {what} 的选项任务抛了（{e.GetType().Name}）：{e.Message}");
            return ("faulted", false);
        }
    }

    /// <summary>只为「把异常吃掉并写日志」：不改变任何行为。</summary>
    private static async Task WatchEventSafely(Task task, string what)
    {
        try { await task; }
        catch (Exception e)
        {
            BridgeLog.Warn($"event: {what} 后续抛了（{e.GetType().Name}）：{e.Message}");
        }
    }

    // =================================================================== 菜单通道（main / mode / character）

    /*
     * 开局（主菜单 → 单人模式 → 标准模式 → 角色 → 开始）以前只能靠 /screen 枚举控件 + /tap
     * 模拟点击，或者整套跳过直接 POST /start 从模型层硬造一局。这里补一条和房间通道同构的
     * 语义通道：kind:"menu"，一次 /respond 只做一件事，靠「页面变了没有」核对。
     *
     * 阶段（stage）：
     *   main      主菜单 NMainMenu。候选 = NMainMenu.MainMenuButtons（NButton[]，8 个：
     *             继续 / 放弃本局 / 单人 / 多人 / 时间线 / 设置 / 图鉴 / 退出）。
     *             「退出」按**引用**剔除（private _quitButton）：协议不提供关游戏这条路，
     *             要关只走调试用的 /tap。其余按钮一律保留（放弃本局没有对局时是 disabled，
     *             会以 enabled:false 出现）。
     *   mode      单人子菜单 NSingleplayerSubmenu。候选 = 它自己的三个 NSubmenuButton
     *             （private _standardButton / _dailyButton / _customButton，IL：_Ready 里
     *             分别连到 OpenCharacterSelect / OpenDailyScreen / OpenCustomScreen）。
     *   character 角色选择 NCharacterSelectScreen。候选 = _charButtonContainer 里的
     *             NCharacterSelectButton（InitCharacterButtons 按 ModelDb.AllCharacters 顺序
     *             生成，最后补一个 IsRandom 的「随机」卡）。
     *   submenu   （附加）其它子菜单页（多人 / 图鉴 / 时间线 / 设置 / 每日 / 自定义 …）：
     *             没有专门的语义通道，就把这一页可见的 N*SubmenuButton 原样列出来，
     *             外加返回键 NBackButton，作为「协议没覆盖的菜单页」的通用出口。
     *
     * 应答一律调**按钮自己的点击入口**，不点坐标：
     *   * 普通按钮 = NClickableControl.ForceClick()。IL 里它就是 OnRelease() +
     *     EmitSignal(Released)，和玩家真点一下走的是同一个信号；Released 上挂的正是
     *     NMainMenu / NSingleplayerSubmenu / 各子菜单自己的处理方法。
     *   * 角色卡 = NCharacterSelectButton.Select()（IL：置 _isSelected 后调
     *     delegate.SelectCharacter(button, _character)，NCharacterSelectScreen.SelectCharacter
     *     里才 _embarkButton.Enable() + 刷新信息面板），再按角色选择页自己的「开始」按钮
     *     （private _embarkButton : NConfirmButton，Released 上挂 OnEmbarkPressed）——
     *     拿不到按钮时才退回反射调 private OnEmbarkPressed()。
     *   * 角色身份只读官方三种属性：NCharacterSelectButton.IsRandom / Character / IsLocked，
     *     不按节点名或标题猜（ScreenApi.ModelLabel 的同一套读法，这里顺便把 Title 按
     *     LocString 取文本 —— CharacterModel.Title 是 LocString，不是 string）。
     *
     * 包裹层 + 核心体：AnswerMenu（校验 + 现场重读菜单 + 失败挂回）→
     * ApplyMenuChoice / ApplyMenuCharacter（只负责按下去 + 核对）。所有 ok:false 都能
     * 重答同一个 id（RestoreMenuAfterReject）。错误文案只陈述现场事实。
     */

    private const string MenuKind = "menu";
    private const string MenuStageMain = "main";
    private const string MenuStageMode = "mode";
    private const string MenuStageSubmenu = "submenu";
    private const string MenuStageCharacter = "character";

    /// <summary>
    /// 「放弃本局」这类按钮按下去只会弹一个确认框（<c>NAbandonRunConfirmPopup</c>），
    /// 菜单页本身不变 —— 以前这里会被判成「点击入口调过，但界面没变」。
    /// 自动点掉确认框失败时，下一次 /request 就用这个 stage 把确认框的按钮发给 agent。
    /// </summary>
    private const string MenuStageConfirm = "confirm";

    private const string MenuPromptMain = "主菜单：选择要做什么";
    private const string MenuPromptMode = "单人模式：选择要玩哪种模式";
    private const string MenuPromptSubmenu = "子菜单：选择要做什么";
    private const string MenuPromptCharacter = "选择角色（选中之后还会问进阶难度，那一阶段才按「开始」开局）";
    private const string MenuPromptConfirm = "确认：这个操作要再确认一次（放弃本局不可撤销）";

    /// <summary>按「放弃本局」之后，等确认框冒出来的时间（弹窗是异步 Instantiate + Add 到模态层）。</summary>
    private const int AbandonConfirmWaitMs = 1400;

    /// <summary>「随机」那张卡的 key 前半截（NCharacterSelectButton.IsRandom 为真时用它，不读角色模型）。</summary>
    private const string MenuRandomId = "random";
    private const string MenuRandomTitle = "随机";

    /// <summary>走了按钮自己的点击入口（ForceClick）时的 visual。</summary>
    private const string MenuVisualApplied = "menu-applied";

    /// <summary>按钮按下去 + 顺带把确认弹窗点掉了（放弃本局那条）。</summary>
    private const string MenuVisualConfirmed = "menu-applied-confirmed";

    /// <summary>角色卡选中 + 「开始」按下、RunManager 真的进了进行中时的 visual。</summary>
    private const string MenuVisualCharacter = "menu-character";

    /// <summary>没走成按钮入口时的 visual 占位。</summary>
    private const string MenuVisualNone = "menu-none";

    /// <summary>轮询菜单/对局状态的间隔。</summary>
    private const int MenuPollMs = 120;

    /// <summary>等「这一页真的变了」/「RunManager 真的进了进行中」的时间预算。</summary>
    private const int MenuSettleMs = 2400;

    /// <summary>单人子菜单三个按钮的私有字段名（读不到时退回按类型扫）。</summary>
    private static readonly string[] ModeButtonFields = { "_standardButton", "_dailyButton", "_customButton" };

    /// <summary>
    /// 菜单的发现（必须在主线程调用，由 /request 触发）。
    /// 对局进行中 / 没有任何菜单页 / 这一页没有可选按钮 → 返回 null 并清掉 pending。
    /// </summary>
    private static Dictionary<string, object> DescribeMenuPending()
    {
        MenuView view;
        try { view = ReadMenuView(); }
        catch (Exception e)
        {
            BridgeLog.Warn("menu: 读菜单失败（" + e.GetType().Name + "）：" + e.Message);
            DropMenuPending("读菜单失败：" + e.Message);
            return null;
        }

        if (view == null || view.Candidates.Count == 0)
        {
            DropMenuPending(view == null ? "没有停在菜单页上（或者对局已经在进行中）" : "这一页没有可选按钮");
            return null;
        }

        var signature = MenuSignature(view);
        var state = _menuPending;
        if (state == null || !string.Equals(state.Signature, signature, StringComparison.Ordinal))
        {
            state = new PendingSelection
            {
                Id = NextId(),
                Min = 1,                      // 每个阶段都必须给出一个 key
                Max = 1,                      // 也只需要一个
                Kind = MenuKind,
                Caller = view.Caller,
                Prompt = view.Prompt,
                Cards = view.Candidates,
                MenuStage = view.Stage,
                CreatedUtc = DateTime.UtcNow,
            };
            state.Signature = signature;
            _menuPending = state;
            BridgeLog.Info($"menu: 新请求 id={state.Id} stage={view.Stage} " +
                           $"keys={DescribeMenuKeys(view.Candidates)}");
        }
        else
        {
            // 同一页：只刷新节点引用（界面重建后按钮会换新实例）和提示语，id 不变。
            state.Cards = view.Candidates;
            state.Prompt = view.Prompt;
            state.Caller = view.Caller;
            state.MenuStage = view.Stage;
            state.Min = 1;
            state.Max = 1;
        }

        return DescribeMenuPendingJson(state, view);
    }

    private static Dictionary<string, object> DescribeMenuPendingJson(PendingSelection pending, MenuView view)
    {
        var dict = new Dictionary<string, object>
        {
            ["id"] = pending.Id,
            ["kind"] = MenuKind,
            ["stage"] = pending.MenuStage,
            ["prompt"] = pending.Prompt ?? MenuPromptMain,
            ["min"] = pending.Min,
            ["max"] = pending.Max,
            ["requireConfirm"] = false,
            ["cancelable"] = false,             // 这三个阶段的 min 都是 1；不答就什么都不发生
            ["caller"] = pending.Caller,
            ["confidence"] = "high",            // 候选全部来自官方节点自己的属性/子节点，不是扫坐标猜的
            ["animated"] = false,               // mod 不补动画：走的是游戏自己的页面切换
            ["route"] = "menu",
            ["candidates"] = pending.Cards.Select(MenuCandidateJson).ToList(),
        };

        if (!string.IsNullOrEmpty(view?.Selected)) dict["selected"] = view.Selected;

        // 进阶阶段（stage=ascension）的补充字段：上限 / 当前等级 / 上限来源 / 读不到时的现场事实。
        // 只在这个阶段出现，别的菜单阶段一个字段都不变。
        FillAscensionFields(dict, view);
        return dict;
    }

    /// <summary>菜单候选的 JSON：只有这里才带 itemType / characterId / locked / random。</summary>
    private static Dictionary<string, object> MenuCandidateJson(Candidate c)
    {
        var dict = new Dictionary<string, object>
        {
            ["key"] = c.Key,
            ["title"] = c.Title ?? "",
            ["enabled"] = c.Enabled,
        };

        if (c.MenuCharacterButton != null)
        {
            dict["itemType"] = "Character";
            dict["characterId"] = c.MenuId ?? "";
            dict["random"] = c.MenuRandom;
            dict["locked"] = c.MenuLocked;
            if (c.MenuLocked) dict["detail"] = "未解锁（NCharacterSelectButton.IsLocked=true），游戏不接受选中";
        }
        else
        {
            dict["itemType"] = "Menu";
            if (c.MenuButton != null) dict["buttonType"] = c.MenuButton.GetType().Name;
        }

        return dict;
    }

    /// <summary>菜单签名：阶段 + 提示语 + 已选角色 + 每个候选的 key/enabled/locked。变了就换 id。</summary>
    private static string MenuSignature(MenuView view)
    {
        if (view == null) return "";
        var parts = new List<string>
        {
            view.Stage ?? "",
            view.Prompt ?? "",
            view.Selected ?? "",
        };
        foreach (var c in view.Candidates)
            parts.Add(c.Key + "=" + (c.Enabled ? "1" : "0") + (c.MenuLocked ? "L" : ""));
        return string.Join("|", parts);
    }

    private static string DescribeMenuKeys(List<Candidate> candidates)
    {
        var keys = candidates.Take(8).Select(c => c.Key).ToList();
        return string.Join("、", keys) + (candidates.Count > 8 ? $" …（共 {candidates.Count} 项）" : "");
    }

    /// <summary>把待答的菜单请求摘掉（不动游戏状态）。</summary>
    private static void DropMenuPending(string why)
    {
        var pending = _menuPending;
        if (pending == null) return;
        _menuPending = null;
        BridgeLog.Info($"menu: 清掉请求 id={pending.Id}（{why}）");
    }

    /// <summary>ok:false 时把菜单请求挂回槽里，让 agent 能改一道答案再答同一个 id。</summary>
    private static void RestoreMenuAfterReject(PendingSelection pending)
    {
        if (pending == null) return;
        if (_lastAnsweredId == pending.Id) _lastAnsweredId = -1;
        _menuPending = pending;
        BridgeLog.Info($"menu: 请求 id={pending.Id} 没被接受，恢复成可再答（已挂回 _menuPending）");
    }

    /// <summary>
    /// 读当前菜单现场（必须在主线程跑）。一趟扫树把三种菜单节点都找出来，然后从最上面那一层往
    /// 下判：确认弹窗 → 角色选择 → 单人子菜单 → 主菜单（主菜单在没有子菜单打开时才是「等玩家选」的那一页）。
    /// </summary>
    private static MenuView ReadMenuView()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return null;

        // 确认弹窗（放弃本局）是模态，压在任何菜单页 / 对局上面，必须先答它。
        var popup = FindAbandonPopup();
        if (popup != null) return ReadConfirmMenu(popup);

        // 对局进行中：主菜单节点在开局过渡时还会挂在树上，那一刻它不是「等玩家选什么」。
        if (RunInProgress())
        {
            // 已经开起来了：进阶阶段那个 mod 侧标记没用了（角色选择页也已经不在了）。
            _ascensionArmed = false;
            return null;
        }

        NCharacterSelectScreen character = null;
        NSingleplayerSubmenu single = null;
        NMainMenu main = null;

        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 40000)
        {
            var node = stack.Pop();
            if (node == null) continue;

            // 角色选择在子菜单上面，找到就可以收工。
            if (node is NCharacterSelectScreen c && IsShown(node)) { character = c; break; }
            if (node is NSingleplayerSubmenu s && single == null && IsShown(node)) single = s;
            if (node is NMainMenu m && main == null && IsShown(node)) main = m;

            foreach (var child in node.GetChildren()) stack.Push(child);
        }

        // 角色已经由 agent 选中、正在等它选进阶（stage=ascension）。
        // 只看界面「有没有已选角色」会把玩家手动点选/页面预选也当成这一步，所以用自己的标记。
        if (character != null && _ascensionArmed) return ReadAscensionMenu(character);

        // 停在角色选择页以外的地方（主菜单 / 子菜单 / 退回上一页）：那个标记就作废，
        // 免得下次再进角色页时直接跳到进阶阶段。
        if (character == null) _ascensionArmed = false;

        if (character != null) return ReadCharacterMenu(character);
        if (single != null) return ReadModeMenu(single);
        if (main == null) return null;

        var top = TopSubmenu(main);
        if (top != null) return ReadGenericSubmenu(top);
        return ReadMainMenu(main);
    }

    /// <summary>RunManager 是否真的在一局里（开局成功与否就看这一位）。</summary>
    private static bool RunInProgress()
    {
        try { return RunManager.Instance != null && RunManager.Instance.IsInProgress; }
        catch { return false; }
    }

    /// <summary>
    /// 场景树里第一个「is T 且可见」的节点。**按具体类型**判断，不按类型名模糊匹配 ——
    /// 否则 <c>NMainMenuSubmenuStack</c> 会被当成 <c>NMainMenu</c>。
    /// </summary>
    private static T FindVisibleNode<T>(Node root) where T : Node
    {
        if (root == null) return null;
        var stack = new Stack<Node>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 40000)
        {
            var node = stack.Pop();
            if (node == null) continue;
            if (node is T typed && IsShown(node)) return typed;
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return null;
    }

    /// <summary>主菜单当前压在最上面的子菜单（<c>NMainMenu.SubmenuStack.Peek()</c>）；没打开时 null。</summary>
    private static Node TopSubmenu(NMainMenu main)
    {
        try
        {
            var stack = ReadMember(main, "SubmenuStack");
            if (stack == null) return null;
            if (ReadMember(stack, "SubmenusOpen") is bool open && !open) return null;
            var peek = stack.GetType().GetMethod("Peek", BindingFlags.Public | BindingFlags.Instance);
            return peek?.Invoke(stack, null) as Node;
        }
        catch { return null; }
    }

    /// <summary>阶段 1：主菜单。<c>MainMenuButtons</c> 是官方属性（8 个 NButton），按引用剔除「退出」。</summary>
    private static MenuView ReadMainMenu(NMainMenu main)
    {
        var view = new MenuView
        {
            Stage = MenuStageMain,
            Prompt = MenuPromptMain,
            Caller = "NMainMenu",
            Screen = main,
        };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var quit = SafeModel(() => ReadMember(main, "_quitButton") as Node);

        var buttons = MenuButtons(main);
        foreach (var button in buttons)
        {
            if (button == null) continue;
            if (quit != null && ReferenceEquals(button, quit)) continue;    // 「退出」不进协议
            AddMenuCandidate(view, counts, button);
        }
        return view;
    }

    /// <summary>主菜单的按钮集合：优先官方属性 <c>MainMenuButtons</c>，读不到才扫子树兜底。</summary>
    private static List<NClickableControl> MenuButtons(NMainMenu main)
    {
        var list = new List<NClickableControl>();
        try
        {
            if (ReadMember(main, "MainMenuButtons") is System.Collections.IEnumerable array)
            {
                foreach (var item in array)
                {
                    if (item is NClickableControl control && !list.Contains(control)) list.Add(control);
                }
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("menu: 读 NMainMenu.MainMenuButtons 失败（" + e.GetType().Name + "）：" + e.Message);
        }

        if (list.Count == 0) list.AddRange(ButtonsIn(main));
        list.Sort(CompareByPosition);
        return list;
    }

    /// <summary>阶段 2：单人子菜单。三个按钮有私有字段名，读不到才按 N*SubmenuButton 扫。</summary>
    private static MenuView ReadModeMenu(NSingleplayerSubmenu sub)
    {
        var view = new MenuView
        {
            Stage = MenuStageMode,
            Prompt = MenuPromptMode,
            Caller = "NSingleplayerSubmenu",
            Screen = sub,
        };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        var buttons = new List<NClickableControl>();
        foreach (var field in ModeButtonFields)
        {
            if (ReadMember(sub, field) is NClickableControl control && !buttons.Contains(control))
                buttons.Add(control);
        }
        if (buttons.Count == 0) buttons.AddRange(SubmenuButtons(sub));
        buttons.Sort(CompareByPosition);

        foreach (var button in buttons) AddMenuCandidate(view, counts, button);
        return view;
    }

    /// <summary>
    /// 附加阶段：其它子菜单页。没有专门通道，就把这一页可见的 N*SubmenuButton 原样列出来，
    /// 外加返回键 NBackButton —— 这是协议没覆盖的菜单页的通用出口。
    /// </summary>
    private static MenuView ReadGenericSubmenu(Node top)
    {
        var view = new MenuView
        {
            Stage = MenuStageSubmenu,
            Prompt = MenuPromptSubmenu,
            Caller = top.GetType().Name,
            Screen = top,
        };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        var buttons = SubmenuButtons(top);
        foreach (var button in buttons) AddMenuCandidate(view, counts, button);

        var back = FindVisibleNode<MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton>(top);
        if (back != null && !buttons.Contains(back)) AddMenuCandidate(view, counts, back);

        return view;
    }

    /// <summary>
    /// 确认弹窗这一阶段：`NAbandonRunConfirmPopup`（点「放弃本局」时由
    /// `NMainMenu.OnAbandonRunButtonPressed` 造出来，`NModalContainer.Add(popup)` 压进模态层）。
    ///
    /// 弹窗自己的结构（MetaPriv + IL 核对过）：
    ///   * private `_verticalPopup : NVerticalPopup`，`_Ready` 里 SetText(header/body) +
    ///     InitYesButton(main_menu_ui.GENERIC_POPUP.confirm, OnYesButtonPressed) +
    ///     InitNoButton(main_menu_ui.GENERIC_POPUP.cancel, OnNoButtonPressed)；
    ///   * `NVerticalPopup.YesButton` / `.NoButton : NPopupYesNoButton : NButton`，文字在它们自己的 `_label`；
    ///   * `OnYesButtonPressed` = `_mainMenuNode.AbandonRun()`（`_mainMenuNode` 为 null 时走
    ///     `RunManager.Instance.Abandon()`）+ `QueueFreeSafely()`。
    ///
    /// 所以这两个按钮就是「好的 / 取消」，ForceClick 等于玩家点一下，不点坐标。
    /// </summary>
    private static MenuView ReadConfirmMenu(MegaCrit.Sts2.Core.Nodes.CommonUi.NAbandonRunConfirmPopup popup)
    {
        var view = new MenuView
        {
            Stage = MenuStageConfirm,
            Prompt = MenuPromptConfirm,
            Caller = "NAbandonRunConfirmPopup",
            Screen = popup,
        };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        var vertical = SafeModel(() => ReadMember(popup, "_verticalPopup") as Node);
        var yes = SafeModel(() => ReadMember(vertical, "YesButton") as NClickableControl);
        var no = SafeModel(() => ReadMember(vertical, "NoButton") as NClickableControl);

        AddMenuCandidate(view, counts, yes, "好的");
        AddMenuCandidate(view, counts, no, "取消");
        return view;
    }

    /// <summary>树里第一个可见的「放弃本局确认弹窗」（必须在主线程调用）。</summary>
    private static MegaCrit.Sts2.Core.Nodes.CommonUi.NAbandonRunConfirmPopup FindAbandonPopup()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return null;
            var stack = new Stack<Node>();
            stack.Push(tree.Root);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 40000)
            {
                var node = stack.Pop();
                if (node == null) continue;
                if (node is MegaCrit.Sts2.Core.Nodes.CommonUi.NAbandonRunConfirmPopup popup && IsShown(popup))
                    return popup;
                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch (Exception e) { BridgeLog.Warn("menu: 找确认弹窗失败（" + e.GetType().Name + "）：" + e.Message); }
        return null;
    }

    /// <summary>等确认弹窗冒出来（按「放弃本局」之后它是异步 Instantiate 的）。</summary>
    private static async Task<Node> WaitAbandonPopup(int ms)
    {
        for (var waited = 0; waited < ms; waited += MenuPollMs)
        {
            Node popup = null;
            try { popup = await BridgePump.OnMain(() => (Node)FindAbandonPopup()); }
            catch { return null; }
            if (popup != null) return popup;
            await Task.Delay(MenuPollMs);
        }
        return null;
    }

    /// <summary>等弹窗真的关掉（QueueFreeSafely 是延迟到帧末的）。</summary>
    private static async Task<bool> WaitPopupGone(Node popup, int ms)
    {
        if (popup == null) return true;
        for (var waited = 0; waited < ms; waited += MenuPollMs)
        {
            bool alive;
            try { alive = await BridgePump.OnMain(() => IsShown(popup)); }
            catch { alive = false; }
            if (!alive) return true;
            await Task.Delay(MenuPollMs);
        }
        return false;
    }

    /// <summary>自动确认的结果（进 /respond 的返回体）。</summary>
    private sealed class AbandonConfirmResult
    {
        public bool Tried;          // 真的等到了确认弹窗
        public bool Confirmed;      // 确认键按下去且弹窗关掉了
        public string Button;       // 确认键自己的文字（读不到就是 null）
        public string Why;          // 没按成时的现场事实
    }

    /// <summary>
    /// 这个候选是不是主菜单的「放弃本局」按钮：按引用比 <c>NMainMenu._abandonRunButton</c>
    /// （候选是从**现场重读**出来的，引用一定对得上）。字段读不到时保守返回 true（多等一次弹窗）。
    /// </summary>
    private static bool IsAbandonRunButton(MenuView view, Candidate chosen)
    {
        if (chosen?.MenuButton == null) return false;
        var abandon = SafeModel(() => ReadMember(view?.Screen, "_abandonRunButton") as NClickableControl);
        if (abandon == null) return true;
        return ReferenceEquals(abandon, chosen.MenuButton);
    }

    /// <summary>
    /// 按完菜单按钮之后替玩家点掉「放弃本局」的确认框。
    ///
    /// 为什么选**自动确认**而不是分两步：agent 选「放弃本局」时意图已经表达完，
    /// 弹出来那个框只有「好的 / 取消」两个按钮、而且它不对应任何新信息（标题正文是固定文案）；
    /// 再让 agent 答一次只会把「一次操作」变成两轮往返，也容易在第二轮之前被别的通道顶掉。
    /// 安全边界：只认 `NAbandonRunConfirmPopup` 这一种弹窗类型（其它弹窗一律不碰），
    /// 而且必须真的按到它自己的 YesButton —— 按不到就什么都不做，把结果如实报回去，
    /// 下一次 /request 会用 `stage:"confirm"` 把「好的 / 取消」交给 agent 自己决定（可重答，不卡死）。
    /// </summary>
    private static async Task<AbandonConfirmResult> HandleAbandonConfirm()
    {
        var result = new AbandonConfirmResult();
        var popup = await WaitAbandonPopup(AbandonConfirmWaitMs);
        if (popup == null) return result;

        result.Tried = true;

        // 读弹窗结构 + 按确认键必须在主线程（Godot 节点）。
        (bool Ok, string Label, string Why) hit;
        try
        {
            hit = await BridgePump.OnMain(() =>
            {
                var vertical = ReadMember(popup, "_verticalPopup") as Node;
                var yes = ReadMember(vertical, "YesButton") as NClickableControl;
                if (yes == null || !SafeValid(yes) || !yes.IsInsideTree() || !IsShown(yes) || !SafeEnabled(yes))
                    return (false, (string)null, "确认弹窗出现了，但里面找不到可用的确认键");

                var label = MenuLabel(yes);
                try
                {
                    yes.ForceClick();      // = OnRelease() + EmitSignal(Released)，和玩家点一下同一个信号
                    return (true, label, (string)null);
                }
                catch (Exception e)
                {
                    return (false, label, "确认键按不下去（" + e.GetType().Name + "）：" + e.Message);
                }
            }, 3000);
        }
        catch (Exception e)
        {
            result.Why = "点确认键失败：" + e.Message;
            return result;
        }

        result.Button = string.IsNullOrWhiteSpace(hit.Label) ? "好的" : hit.Label;
        if (!hit.Ok)
        {
            result.Why = hit.Why ?? "确认键没按下去";
            return result;
        }

        result.Confirmed = await WaitPopupGone(popup, MenuSettleMs);
        if (!result.Confirmed) result.Why = "确认键按了，但弹窗没有关掉";
        return result;
    }

    /// <summary>
    /// 阶段 3：角色选择。身份只读官方三种属性：<c>IsRandom</c> / <c>Character</c> / <c>IsLocked</c>；
    /// 标题取 <c>CharacterModel.Title</c>（LocString），key 用角色 Id 的末尾名字。
    /// </summary>
    private static MenuView ReadCharacterMenu(NCharacterSelectScreen screen)
    {
        var view = new MenuView
        {
            Stage = MenuStageCharacter,
            Prompt = MenuPromptCharacter,
            Caller = "NCharacterSelectScreen",
            Screen = screen,
        };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var button in CharacterButtons(screen))
        {
            if (button == null || !IsShown(button)) continue;

            var random = SafeBool(() => button.IsRandom);
            var locked = SafeBool(() => button.IsLocked);
            var selected = SafeBool(() => button.IsSelected);
            var model = random ? null : SafeModel(() => button.Character);

            string id;
            string title;
            if (random)
            {
                id = MenuRandomId;
                title = MenuRandomTitle;
            }
            else
            {
                id = CharacterKeyOf(model);
                title = SafeText(() => LocText(model?.Title));
                if (string.IsNullOrWhiteSpace(title)) title = id;
            }

            counts.TryGetValue(id, out var seen);
            seen++;
            counts[id] = seen;

            var candidate = new Candidate
            {
                Key = id + "#" + seen,
                Title = title,
                Upgraded = false,
                Cost = -1,
                Enabled = !locked,
                MenuCharacterButton = button,
                MenuCharacter = model,
                MenuRandom = random,
                MenuLocked = locked,
                MenuId = id,
                MenuStage = MenuStageCharacter,
            };
            view.Candidates.Add(candidate);
            if (selected) view.Selected = candidate.Key;
        }

        return view;
    }

    /// <summary>角色卡集合：官方容器 <c>_charButtonContainer</c> 里的全部 NCharacterSelectButton（含随机卡）。</summary>
    private static List<NCharacterSelectButton> CharacterButtons(NCharacterSelectScreen screen)
    {
        var list = new List<NCharacterSelectButton>();
        var root = SafeModel(() => ReadMember(screen, "_charButtonContainer") as Node) ?? screen;

        var stack = new Stack<Node>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 4000)
        {
            var node = stack.Pop();
            if (node == null) continue;
            if (node is NCharacterSelectButton button) list.Add(button);
            foreach (var child in node.GetChildren()) stack.Push(child);
        }

        list.Sort((a, b) => CompareByPosition(a, b));
        return list;
    }

    /// <summary>子菜单里的菜单行按钮（NSubmenuButton / NShortSubmenuButton，后者名字里也含 SubmenuButton）。</summary>
    private static List<NClickableControl> SubmenuButtons(Node root)
    {
        var list = new List<NClickableControl>();
        if (root == null) return list;

        var stack = new Stack<Node>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 4000)
        {
            var node = stack.Pop();
            if (node == null) continue;
            if (node is NClickableControl control && IsShown(node)
                && ScreenApi.TypeChainContains(node, "SubmenuButton"))
                list.Add(control);
            foreach (var child in node.GetChildren()) stack.Push(child);
        }

        list.Sort(CompareByPosition);
        return list;
    }

    /// <summary>子树里所有可见的 NClickableControl（只在官方按钮集合读不到时兜底用）。</summary>
    private static List<NClickableControl> ButtonsIn(Node root)
    {
        var list = new List<NClickableControl>();
        if (root == null) return list;

        var stack = new Stack<Node>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 4000)
        {
            var node = stack.Pop();
            if (node == null) continue;
            if (node is NClickableControl control && IsShown(node)) list.Add(control);
            foreach (var child in node.GetChildren()) stack.Push(child);
        }

        list.Sort(CompareByPosition);
        return list;
    }

    /// <summary>
    /// 把一个按钮登记成菜单候选：key = 标题#同标题序号，标题取自按钮自己身上的文字。
    /// <paramref name="fallbackTitle"/> 只在按钮身上读不到任何文字时用
    /// （确认弹窗那两个 <c>NPopupYesNoButton</c> 走这条）。
    /// </summary>
    private static void AddMenuCandidate(
        MenuView view, Dictionary<string, int> counts, NClickableControl button, string fallbackTitle = null)
    {
        if (view == null || button == null || !IsShown(button)) return;

        var title = MenuLabel(button);
        if (title.Length == 0 && !string.IsNullOrWhiteSpace(fallbackTitle)) title = fallbackTitle.Trim();
        if (title.Length == 0) title = SafeNodeName(button);

        view.Candidates.Add(new Candidate
        {
            Key = NextKey(counts, title),
            Title = title,
            Upgraded = false,
            Cost = -1,
            Enabled = SafeEnabled(button),
            MenuButton = button,
            MenuStage = view.Stage,
        });
    }

    /// <summary>
    /// 按钮身上的标题文字。分两趟：先找 <c>Label</c>（标题都是 MegaLabel : Label；Godot 原生按钮用
    /// <c>Text</c>），找不到才退回 <c>RichTextLabel</c>（说明栏）。理由：<c>NSubmenuButton</c> 的
    /// <c>_title</c> 是 MegaLabel、<c>_description</c> 是 MegaRichTextLabel，只跑一遍 DFS 有把整段
    /// 说明当成标题的风险。
    /// </summary>
    private static string MenuLabel(Node node)
    {
        var title = FirstNodeText(node, false);
        return title.Length > 0 ? title : FirstNodeText(node, true);
    }

    private static string FirstNodeText(Node node, bool rich)
    {
        if (node == null) return "";
        try
        {
            if (!rich && node is Label label)
            {
                var text = ScreenApi.Clean(label.Text);
                if (text.Length > 0) return text;
            }
            if (!rich && node is Button button)
            {
                var text = ScreenApi.Clean(button.Text);
                if (text.Length > 0) return text;
            }
            if (rich && node is RichTextLabel richLabel)
            {
                var text = ScreenApi.Clean(richLabel.Text);
                if (text.Length > 0) return text;
            }
            foreach (var child in node.GetChildren())
            {
                var text = FirstNodeText(child, rich);
                if (text.Length > 0) return text;
            }
        }
        catch { }
        return "";
    }

    private static string SafeNodeName(Node node)
    {
        try
        {
            var name = node?.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { }
        return "按钮";
    }

    /// <summary>按钮当前是否可用（NClickableControl.IsEnabled）；读不出来时按「可用」处理，不拦。</summary>
    private static bool SafeEnabled(NClickableControl control)
    {
        try { return control != null && control.IsEnabled; }
        catch { return true; }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try { return read(); } catch { return false; }
    }

    /// <summary>按屏幕顺序（上→下、左→右）排，key 的序号才稳定。</summary>
    private static int CompareByPosition(NClickableControl a, NClickableControl b)
    {
        float ax = 0f, ay = 0f, bx = 0f, by = 0f;
        try { ax = a.GlobalPosition.X; ay = a.GlobalPosition.Y; } catch { }
        try { bx = b.GlobalPosition.X; by = b.GlobalPosition.Y; } catch { }
        var dy = ay.CompareTo(by);
        return dy != 0 ? dy : ax.CompareTo(bx);
    }

    /// <summary>角色 Id 的末尾名字（"CHARACTER.SILENT" → "silent"）；读不出来时用 "character"。</summary>
    private static string CharacterKeyOf(CharacterModel model)
    {
        try
        {
            var raw = model?.Id.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var part = raw.Split('.').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(part)) return part.Trim().ToLowerInvariant();
            }
        }
        catch { }
        return "character";
    }

    /// <summary>
    /// 菜单通道的应答入口（包裹层）：校验 → 现场重读菜单 → 交给核心体按下去。
    /// 任何 ok:false 都把请求挂回槽里（<see cref="RestoreMenuAfterReject"/>），所以能改一道答案再答。
    /// </summary>
    private static async Task<Dictionary<string, object>> AnswerMenu(PendingSelection pending, string[] picks)
    {
        picks ??= Array.Empty<string>();
        var failure = Validate(pending.Min, pending.Max, pending.Cards, picks, out var chosen, "项");
        if (failure != null) return failure;

        // 现场重读：按钮节点会随界面重建换新实例，不能拿 /request 那一刻的引用直接按。
        MenuView view;
        try { view = await BridgePump.OnMain(ReadMenuView); }
        catch (Exception e) { return Fail("读取当前菜单失败：" + e.Message); }
        if (view == null)
            return Fail($"菜单已经不在（{pending.MenuStage} 阶段的界面从场景树里消失了）");

        var key = chosen[0].Key;
        var fresh = view.Candidates.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));
        if (fresh == null)
            return Fail($"{key} 不在当前菜单的候选里（可选：{DescribeMenuKeys(view.Candidates)}）");

        var sw = Stopwatch.StartNew();

        // 先摘掉：按下去的过程中不能再被 /request 看到，也不能被答第二次。
        _menuPending = null;
        _lastAnsweredId = pending.Id;
        _animating = true;

        // 答的不是进阶阶段（例如在角色页按了返回）：角色选中的标记就作废，别让下一次 /request
        // 莫名其妙地给出 ascension 阶段。
        if (!string.Equals(view.Stage, MenuStageAscension, StringComparison.Ordinal)) _ascensionArmed = false;

        Dictionary<string, object> result;
        try
        {
            result = string.Equals(view.Stage, MenuStageAscension, StringComparison.Ordinal)
                ? await ApplyMenuAscension(pending, fresh, view, sw)
                : string.Equals(view.Stage, MenuStageCharacter, StringComparison.Ordinal)
                    ? await ApplyMenuCharacter(pending, fresh, view, sw)
                    : await ApplyMenuChoice(pending, fresh, view, sw);
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"menu: 应答 id={pending.Id} 抛异常（{e.GetType().Name}）：{e.Message}");
            result = Fail("菜单操作出错：" + e.Message);
        }
        finally { _animating = false; }

        if (!IsTrue(result, "ok")) RestoreMenuAfterReject(pending);
        return result;
    }

    /// <summary>核心体（main / mode / submenu / confirm）：调按钮自己的 ForceClick()，然后等这一页真的变了。</summary>
    private static async Task<Dictionary<string, object>> ApplyMenuChoice(
        PendingSelection pending, Candidate chosen, MenuView view, Stopwatch sw)
    {
        // 「放弃本局」按下去只会弹确认框，菜单页本身不变 —— 先认那个弹窗并替玩家点掉「好的」，
        // 否则这里会误判成「点击入口调过，但界面没变」。
        // 只对「放弃本局」这一个按钮做（按引用比 `NMainMenu._abandonRunButton`，必须在主线程读），
        // 别的按钮不多等这 1.4 秒；已经在 confirm 阶段时也不再自动确认，避免重复执行。
        bool mayConfirm;
        (bool Ok, string How, string Why) press;
        try
        {
            (press, mayConfirm) = await BridgePump.OnMain(() =>
            {
                var result = PressMenuButton(chosen);
                var abandon = view.Stage != MenuStageConfirm && IsAbandonRunButton(view, chosen);
                return (result, abandon);
            }, 4000);
        }
        catch (Exception e) { return Fail("按下菜单按钮失败：" + e.Message); }

        if (!press.Ok) return Fail(press.Why ?? $"{chosen.Key} 没按下去");

        var confirm = mayConfirm ? await HandleAbandonConfirm() : new AbandonConfirmResult();

        var change = await WaitMenuChanged(view);
        var waited = sw.ElapsedMilliseconds;
        if (!change.Changed)
            return Fail($"{chosen.Key} 的点击入口调过（{press.How}）" +
                        (confirm.Tried
                            ? "，确认弹窗也处理过（" + (confirm.Confirmed ? "已按「" + (confirm.Button ?? "好的") + "」" : "没按成：" + (confirm.Why ?? "原因不明")) + "）"
                            : "") +
                        $"，但界面没变（还停在 stage=\"{view.Stage}\"）");

        BridgeLog.Info($"menu answered id={pending.Id} stage={view.Stage} key={chosen.Key} " +
                       $"how={press.How} next={change.Now?.Stage ?? "(菜单消失)"} waitedMs={waited} " +
                       $"confirm={(confirm.Tried ? (confirm.Confirmed ? "yes:" + (confirm.Button ?? "?") : "failed") : "none")}");

        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["source"] = "screen",
            ["route"] = "menu",
            ["stage"] = view.Stage,
            ["applied"] = chosen.Key,
            ["how"] = confirm.Confirmed ? press.How + "+confirm" : press.How,
            ["animated"] = false,          // mod 不补动画：播的是游戏自己的页面切换
            ["waitedMs"] = waited,
            ["visual"] = confirm.Confirmed ? MenuVisualConfirmed : MenuVisualApplied,
            ["button"] = chosen.Title ?? "",
            ["settled"] = true,
            ["nextStage"] = change.Now?.Stage,   // 观测到的下一阶段；菜单整个消失时是 null
        };
        if (confirm.Tried)
        {
            result["confirmed"] = confirm.Confirmed;
            if (!string.IsNullOrEmpty(confirm.Button)) result["confirmButton"] = confirm.Button;
            if (!confirm.Confirmed) result["confirmNote"] = confirm.Why ?? "确认弹窗没有关掉";
        }
        return result;
    }

    /// <summary>
    /// 核心体（character）：只**选中**角色卡，不按「开始」。
    /// 选中之后下一次 /request 会给出 <c>stage:"ascension"</c>（见 <see cref="ApplyMenuAscension"/>），
    /// 那一阶段才设进阶并真开打 —— 这样每个角色自己的 Ascension 才选得上。
    /// </summary>
    private static async Task<Dictionary<string, object>> ApplyMenuCharacter(
        PendingSelection pending, Candidate chosen, MenuView view, Stopwatch sw)
    {
        if (chosen.MenuLocked)
            return Fail($"{chosen.Key} 是锁着的角色（NCharacterSelectButton.IsLocked=true），游戏不接受选中");

        var pick = await BridgePump.OnMain(() => SelectCharacterButton(chosen.MenuCharacterButton));
        if (!pick.Ok) return Fail(pick.Why ?? $"{chosen.Key} 没选中");

        // 角色已经选中：立起「等进阶」的标记，并把界面上算好的解锁上限读出来给返回体用。
        _ascensionArmed = true;
        _ascensionArmedCharacter = chosen.MenuId;
        var info = await BridgePump.OnMain(() => DescribeAscensionAvailability(view.Screen as NCharacterSelectScreen));

        var waited = sw.ElapsedMilliseconds;
        BridgeLog.Info($"menu answered id={pending.Id} stage=character key={chosen.Key} selected=true " +
                       $"ascensionMax={info.Max} waitedMs={waited}");

        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["source"] = "screen",
            ["route"] = "menu",
            ["stage"] = MenuStageCharacter,
            ["applied"] = chosen.Key,
            ["how"] = "menu-character-select",
            ["animated"] = false,
            ["waitedMs"] = waited,
            ["visual"] = MenuVisualCharacter,
            ["characterId"] = chosen.MenuId ?? "",
            ["random"] = chosen.MenuRandom,
            ["selected"] = true,
            // 这一版不再直接开打：下一阶段是选进阶，那一阶段才按「开始」。
            ["started"] = false,
            ["nextStage"] = MenuStageAscension,
            ["ascensionMax"] = info.Max,
        };
        if (!string.IsNullOrEmpty(info.Source)) result["ascensionSource"] = info.Source;
        if (!string.IsNullOrEmpty(info.Unreadable)) result["ascensionNote"] = info.Unreadable;
        return result;
    }

    /// <summary>
    /// 按下一个菜单候选（必须在主线程跑）。走的是按钮自己的 <c>ForceClick()</c>：
    /// IL 里它就是 <c>OnRelease()</c> + <c>EmitSignal(Released)</c>，和玩家真点一下同一个信号。
    /// </summary>
    private static (bool Ok, string How, string Why) PressMenuButton(Candidate chosen)
    {
        var button = chosen?.MenuButton;
        if (button == null)
            return (false, MenuVisualNone, $"{chosen?.Key ?? "?"} 没有对应的按钮节点");
        if (!SafeValid(button) || !button.IsInsideTree())
            return (false, MenuVisualNone, $"{chosen.Key} 的按钮已经不在场景树里了");
        if (!SafeEnabled(button))
            return (false, MenuVisualNone, $"{chosen.Key} 的按钮当前不可用（IsEnabled=false）");

        button.ForceClick();
        return (true, "menu-button", null);
    }

    /// <summary>
    /// 选中一张角色卡：<c>NCharacterSelectButton.Select()</c>（IL：置 _isSelected →
    /// delegate.SelectCharacter → NCharacterSelectScreen.SelectCharacter 里 Enable「开始」）。
    /// </summary>
    private static (bool Ok, string How, string Why) SelectCharacterButton(NCharacterSelectButton button)
    {
        if (button == null) return (false, MenuVisualNone, "角色卡节点不在了");
        if (!SafeValid(button) || !button.IsInsideTree())
            return (false, MenuVisualNone, "角色卡已经不在场景树里了");
        if (SafeBool(() => button.IsLocked))
            return (false, MenuVisualNone, "角色卡是锁着的（IsLocked=true）");

        button.Select();
        if (!SafeBool(() => button.IsSelected))
            return (false, MenuVisualNone, "调了 Select()，但 IsSelected 还是 false");
        return (true, "menu-character-select", null);
    }

    /// <summary>
    /// 按角色选择页自己的「开始」按钮（private <c>_embarkButton : NConfirmButton</c>，Released
    /// 上挂 OnEmbarkPressed）。拿不到按钮才退回反射调 private <c>OnEmbarkPressed()</c>。
    /// 两条都等价于玩家点「开始」，都不点坐标。
    /// </summary>
    private static (bool Ok, string How, string Why) PressEmbark(Node screen)
    {
        if (screen == null) return (false, MenuVisualNone, "角色选择界面不在了");

        if (ReadMember(screen, "_embarkButton") is NClickableControl embark
            && SafeValid(embark) && embark.IsInsideTree())
        {
            if (!SafeEnabled(embark))
                return (false, MenuVisualNone, "「开始」按钮当前不可用（_embarkButton.IsEnabled=false）");
            embark.ForceClick();
            return (true, "menu-embark", null);
        }

        var method = FindMethod(screen.GetType(), "OnEmbarkPressed", 0);
        if (method == null)
            return (false, MenuVisualNone, "既没有 _embarkButton，也没有 OnEmbarkPressed()");
        method.Invoke(screen, null);
        return (true, "menu-onembark", null);
    }

    private static bool SafeValid(GodotObject obj)
    {
        try { return obj != null && GodotObject.IsInstanceValid(obj); } catch { return false; }
    }

    /// <summary>等这一页真的变了（阶段变了 / 候选变了 / 菜单整个消失）。</summary>
    private static async Task<MenuChange> WaitMenuChanged(MenuView before)
    {
        var beforeSignature = MenuSignature(before);
        for (var waited = 0; waited < MenuSettleMs; waited += MenuPollMs)
        {
            await Task.Delay(MenuPollMs);
            MenuView now;
            try { now = await BridgePump.OnMain(ReadMenuView); }
            catch { return new MenuChange { Changed = true, Now = null }; }
            if (now == null) return new MenuChange { Changed = true, Now = null };   // 菜单没了：进了别的场景
            if (!string.Equals(now.Stage, before.Stage, StringComparison.Ordinal))
                return new MenuChange { Changed = true, Now = now };
            if (!string.Equals(MenuSignature(now), beforeSignature, StringComparison.Ordinal))
                return new MenuChange { Changed = true, Now = now };
        }
        return new MenuChange { Changed = false, Now = before };
    }

    /// <summary>等 RunManager 真的进入进行中（最多 <see cref="MenuSettleMs"/>）。</summary>
    private static async Task<bool> WaitRunStarted()
    {
        for (var waited = 0; waited < MenuSettleMs; waited += MenuPollMs)
        {
            bool running;
            try { running = await BridgePump.OnMain(RunInProgress); }
            catch { return false; }
            if (running) return true;
            await Task.Delay(MenuPollMs);
        }

        try { return await BridgePump.OnMain(RunInProgress); }
        catch { return false; }
    }

    /// <summary>开局没生效时补一句现场事实（有教程弹窗就写出来）。只陈述事实。</summary>
    private static string RunStartBlocker()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return null;
            if (FindFirst(tree.Root, "NAcceptTutorialsFtue") != null)
                return "界面上有教程确认框 NAcceptTutorialsFtue";
        }
        catch { }
        return null;
    }

    // =================================================================== 游戏结束通道（NGameOverScreen）
    /*
     * 死亡 / 胜利之后那块收尾界面既不是「选牌」，也不归任何房间通道管：它是一块模态浮层
     * （NGameOverScreen，由 NGameOverScreen.Create(runState, serializableRun) 造出来压进 NOverlayStack）。
     *
     * 以前 agent 只能靠记忆坐标点三下：
     *   GAME OVER → 点「继续」→【一个看不见的空按钮】→ 才亮出「主菜单」→ 点「主菜单」。
     * 中间那个空按钮没有任何文字，所以「按文字找按钮」永远找不到它。
     *
     * ---- 数据源（全部在主线程读；官方私有字段，MetaPriv 核对过 sts2.dll）----
     *   _continueButton    : NGameOverContinueButton  （Released → OpenSummaryScreen）
     *   _viewRunButton     : NViewRunButton           （Released → OpenRunHistoryScreen）
     *   _mainMenuButton    : NReturnToMainMenuButton  （Released → OnMainMenuButtonPressed：
     *                                                  有解锁则 OpenTimeline，否则 ReturnToMainMenu）
     *   _leaderboardButton : NGameOverContinueButton  （每日挑战的排行榜，只在每日局里 Visible）
     *   _summaryContainer  : NRunSummary              （结算动画的容器）
     *   _badgeContainer    : Control                  （徽章；里面的可点节点是装饰，不当候选）
     *   _history           : RunHistory               （Win = 本局是赢是输）
     *
     * ---- 关键时序（AnimateRunSummary 的 IL 实证）----
     *   点「继续」= OpenSummaryScreen：先 Disable 继续、显示结算容器，然后
     *   AnimateScoreLines → AnimateBadges → AnimateScoreBar → AnimateDiscoveries 一路 await；
     *   **最后**才 `_mainMenuButton.Visible = true; _mainMenuButton.Enable();`
     *   （每日局换成排行榜按钮）。也就是说「主菜单」在整段动画播完之前既不可见也不可用 ——
     *   这正是「必须点那个空按钮」的成因。
     *   游戏自带的 AutoSlay（Handlers/Screens/GameOverScreenHandler）走的也是这条路：
     *   点继续 → WaitHelper.Until(() => mainMenuButton.Visible && IsEnabled, 90s) → 再点主菜单。
     *   所以我们按同一条时序走，只是等待有上限，并且在「等不到、但界面上有个没文字的按钮」时
     *   把那个按钮也作为候选发出去（按**类型 + 屏幕相对位置**认，绝不写死坐标）。
     *
     * ---- RunState.IsGameOver 的真实语义（核实过 IL）----
     *   `get_IsGameOver` = `Players.Count > 0 && Players.All(p => p.Creature.IsDead)`，
     *   即「全员阵亡」。**胜利**时这块界面同样会出现，而 IsGameOver 是 false，
     *   所以判定只能用「界面在树上」，IsGameOver 只作为辅助字段发出去（见 pending.died）。
     *
     * 应答一律调按钮自己的 NClickableControl.ForceClick()（= OnRelease() + EmitSignal(Released)），
     * 不点坐标。包裹层 AnswerGameOver → 核心体 ApplyGameOverChoice，失败可重答。
     */

    private const string GameOverKind = "gameover";
    private const string GameOverRoute = "gameover";
    private const string GameOverCaller = "NGameOverScreen";

    /// <summary>收尾主页：继续 / 主菜单 /（有解锁时是）时间线 / 查看本局。</summary>
    private const string GameOverStagePage = "page";

    /// <summary>结算动画没播完：这时「主菜单」还没亮，能点的可能只有那个空按钮。</summary>
    private const string GameOverStageSummary = "summary";

    private const string GameOverPromptPage =
        "游戏结束：选择要做什么（「继续」看本局结算；结算动画播完才会亮出「主菜单」/「时间线」）";

    private const string GameOverPromptSummary =
        "游戏结束：结算动画还在播（「主菜单」要等它播完才亮）；也可以先按那个没有文字的按钮推进";

    /// <summary>走了按钮自己的点击入口时的 visual。</summary>
    private const string GameOverVisualApplied = "gameover-applied";

    /// <summary>没按成时的 visual 占位。</summary>
    private const string GameOverVisualNone = "gameover-none";

    /// <summary>普通的收尾界面按钮按下之后，等界面变化的预算。</summary>
    private const int GameOverSettleMs = 3200;

    /// <summary>
    /// 点「继续」之后等「主菜单」亮起来的预算。结算动画（分数行 / 徽章 / 分数条 / 新发现）
    /// 一路 await，实测常常要好几秒；游戏自己的 AutoSlay 给到 90s，这里取一个 HTTP 能接受的上限，
    /// 到点没亮就如实回报 settled:false，下一次 GET /request 接着给（或者给那个空按钮）。
    /// </summary>
    private const int GameOverSummaryMs = 8000;

    /// <summary>轮询收尾界面的间隔（每轮都要扫一遍场景树，别太密）。</summary>
    private const int GameOverPollMs = 200;

    /// <summary>游戏结束通道的待答请求。单槽：同一时刻只可能开着一块收尾界面。</summary>
    private static PendingSelection _gameOverPending;

    /// <summary>收尾界面这一帧的现场：哪个阶段、有哪些现在真能按的按钮。</summary>
    private sealed class GameOverView
    {
        public NGameOverScreen Screen;
        public string Stage;
        public string Prompt;
        public bool SummaryVisible;
        public bool ContinueVisible;
        public bool ContinueEnabled;
        public bool MainMenuVisible;
        public bool MainMenuEnabled;
        public bool? Died;              // RunState.IsGameOver（null = 读不到）
        public bool? Won;               // RunHistory.Win（null = 读不到）
        public List<Candidate> Candidates = new();
    }

    /// <summary>按下之后观测到的变化。</summary>
    private sealed class GameOverChange
    {
        public bool Changed;
        public GameOverView Now;
        public bool MainMenuReady;
    }

    /// <summary>树里第一块「可见的」NGameOverScreen（必须在主线程调用）。</summary>
    private static NGameOverScreen FindGameOverScreen()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return null;
            var stack = new Stack<Node>();
            stack.Push(tree.Root);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 40000)
            {
                var node = stack.Pop();
                if (node == null) continue;
                if (node is NGameOverScreen screen && IsShown(screen)) return screen;
                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch (Exception e) { BridgeLog.Warn("gameover: 找收尾界面失败（" + e.GetType().Name + "）：" + e.Message); }
        return null;
    }

    /// <summary>RunState.IsGameOver（= 全员阵亡）；读不到就 null，不猜。</summary>
    private static bool? RunStateGameOver()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            return state?.IsGameOver;
        }
        catch { return null; }
    }

    /// <summary>
    /// 读收尾界面现场（必须在主线程跑）。候选只放「现在真的能按」的按钮：
    /// 不可见 / 被游戏 Disable 的一律不发（这个通道里它们不是决策点，只是还没播完的动画）。
    /// </summary>
    private static GameOverView ReadGameOverView()
    {
        var screen = FindGameOverScreen();
        if (screen == null) return null;

        var continueBtn = SafeModel(() => ReadMember(screen, "_continueButton") as NClickableControl);
        var mainMenuBtn = SafeModel(() => ReadMember(screen, "_mainMenuButton") as NClickableControl);
        var viewRunBtn = SafeModel(() => ReadMember(screen, "_viewRunButton") as NClickableControl);
        var boardBtn = SafeModel(() => ReadMember(screen, "_leaderboardButton") as NClickableControl);
        var summary = SafeModel(() => ReadMember(screen, "_summaryContainer") as Node);
        var badge = SafeModel(() => ReadMember(screen, "_badgeContainer") as Node);

        var view = new GameOverView
        {
            Screen = screen,
            ContinueVisible = IsShown(continueBtn),
            ContinueEnabled = SafeEnabled(continueBtn),
            MainMenuVisible = IsShown(mainMenuBtn),
            MainMenuEnabled = SafeEnabled(mainMenuBtn),
            SummaryVisible = IsShown(summary),
        };

        view.Stage = view.SummaryVisible && !view.MainMenuVisible ? GameOverStageSummary : GameOverStagePage;
        view.Prompt = view.Stage == GameOverStageSummary ? GameOverPromptSummary : GameOverPromptPage;
        view.Died = RunStateGameOver();
        var history = SafeModel(() => ReadMember(screen, "_history"));
        view.Won = history == null ? null : ReadMemberAny(history, "Win") as bool?;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var known = new List<NClickableControl>();

        AddGameOverButton(view, counts, known, continueBtn, "继续",
            "打开本局结算（结算动画播完才会亮出「主菜单」）");
        AddGameOverButton(view, counts, known, mainMenuBtn, "主菜单",
            "回主菜单；有新解锁时这个按钮会变成「时间线」，点了先进时间线");
        AddGameOverButton(view, counts, known, viewRunBtn, "查看本局",
            "打开本局历史，只是查看，不推进收尾");
        AddGameOverButton(view, counts, known, boardBtn, "排行榜",
            "每日挑战的排行榜（只在每日局里出现）");

        // 界面上没挂文字、但真能点的按钮（那个 200x110 的「空按钮」就在这里被认出来）。
        AddTextlessAdvanceButtons(view, counts, screen, known, badge);

        return view;
    }

    /// <summary>
    /// 收尾界面的一个官方按钮 → 候选。不可见 / 不可用 / 不在树上一律跳过。
    /// 标题取按钮自己的 Label；读不到才用语义兜底名。
    /// </summary>
    private static void AddGameOverButton(
        GameOverView view, Dictionary<string, int> counts, List<NClickableControl> known,
        NClickableControl button, string fallbackTitle, string detail)
    {
        if (view == null || button == null) return;
        known.Add(button);
        if (!SafeValid(button) || !button.IsInsideTree()) return;
        if (!IsShown(button) || !SafeEnabled(button)) return;

        var title = MenuLabel(button);
        if (title.Length == 0) title = fallbackTitle;
        if (title.Length == 0) title = SafeNodeName(button);

        view.Candidates.Add(new Candidate
        {
            Key = NextKey(counts, title),
            Title = title,
            Upgraded = false,
            Cost = -1,
            Enabled = true,
            MenuButton = button,
            MenuStage = view.Stage,
            Detail = detail,
        });
    }

    /// <summary>
    /// 把这块界面里「没有文字、但可以点」的 NClickableControl 也变成候选。
    /// 识别方式（都不含硬编码坐标）：
    ///   * 类型：`NClickableControl` 派生（含 NButton 全家）；
    ///   * 可见且在树里、`IsEnabled`；
    ///   * 自身子树里找不到任何 Label / Button 文字（`MenuLabel` 为空）；
    ///   * 不在 `_badgeContainer` 里（徽章是装饰，点它没意义）；
    ///   * 不是已知的官方按钮（继续 / 主菜单 / 查看本局 / 排行榜），也不是界面根节点。
    /// 顺序按屏幕位置（上→下、左→右），key 才稳定；`detail` 里写相对视口的位置百分比，
    /// agent 想核对「是不是那个右下角的空按钮」时不用看坐标。
    /// </summary>
    private static void AddTextlessAdvanceButtons(
        GameOverView view, Dictionary<string, int> counts, Node screen,
        List<NClickableControl> known, Node badgeContainer)
    {
        if (screen == null) return;

        var found = new List<NClickableControl>();
        try
        {
            var stack = new Stack<Node>();
            stack.Push(screen);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 20000)
            {
                var node = stack.Pop();
                if (node == null) continue;

                if (node is NClickableControl control && !ReferenceEquals(node, screen))
                {
                    if (!known.Any(k => ReferenceEquals(k, control))
                        && IsShown(control) && SafeEnabled(control)
                        && MenuLabel(control).Length == 0
                        && !IsUnderNode(control, badgeContainer))
                    {
                        found.Add(control);
                    }
                }

                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch (Exception e) { BridgeLog.Warn("gameover: 扫无文字按钮失败（" + e.GetType().Name + "）：" + e.Message); }

        found.Sort(CompareByPosition);
        foreach (var control in found)
        {
            var title = SafeNodeName(control);
            if (string.IsNullOrWhiteSpace(title) || title == "按钮") title = "无文字按钮";

            var placement = RelativePlacement(control);
            var detail = "没有文字的按钮（" + control.GetType().Name
                         + (placement.Length > 0 ? "，" + placement : "")
                         + "）：游戏自己没给它挂文案，点它等于玩家点一下";

            view.Candidates.Add(new Candidate
            {
                Key = NextKey(counts, title),
                Title = title,
                Upgraded = false,
                Cost = -1,
                Enabled = true,
                MenuButton = control,
                MenuStage = view.Stage,
                EmptyButton = true,
                Detail = detail,
            });
        }
    }

    /// <summary>这个控件在视口里的相对位置（百分比 + 尺寸），不写死任何坐标。</summary>
    private static string RelativePlacement(Control control)
    {
        try
        {
            var viewport = control.GetViewportRect().Size;
            if (viewport.X <= 1f || viewport.Y <= 1f) return "";
            var origin = control.GetScreenPosition();
            var size = control.Size;
            var cx = (origin.X + size.X / 2f) / viewport.X;
            var cy = (origin.Y + size.Y / 2f) / viewport.Y;
            return $"屏幕 {cx * 100f:0}% x {cy * 100f:0}%，大小 {size.X:0}x{size.Y:0}";
        }
        catch { return ""; }
    }

    /// <summary>node 是不是 container 自己或它的后代（沿父链走，最多 64 层防环）。</summary>
    private static bool IsUnderNode(Node node, Node container)
    {
        if (node == null || container == null) return false;
        try
        {
            var cur = node;
            for (var depth = 0; cur != null && depth < 64; depth++)
            {
                if (ReferenceEquals(cur, container)) return true;
                cur = cur.GetParent();
            }
        }
        catch { }
        return false;
    }

    /// <summary>菜单 / 收尾通道共用的签名：阶段 + 每个候选的 key（空按钮另标记）。</summary>
    private static string GameOverSignature(GameOverView view)
    {
        if (view == null) return "";
        var parts = new List<string>
        {
            view.Stage ?? "",
            view.SummaryVisible ? "s1" : "s0",
            view.ContinueEnabled ? "c1" : "c0",
            view.MainMenuVisible ? "m1" : "m0",
        };
        foreach (var c in view.Candidates) parts.Add(c.Key + (c.EmptyButton ? "E" : ""));
        return string.Join("|", parts);
    }

    /// <summary>
    /// 收尾界面的发现（必须在主线程调用）。
    /// 界面不在 / 一个能按的候选都没有 → 返回 null 并清掉 pending（下一次 GET 再看）。
    /// </summary>
    private static Dictionary<string, object> DescribeGameOverPending()
    {
        GameOverView view;
        try { view = ReadGameOverView(); }
        catch (Exception e)
        {
            BridgeLog.Warn("gameover: 读界面失败（" + e.GetType().Name + "）：" + e.Message);
            DropGameOverPending("读界面失败：" + e.Message);
            return null;
        }

        if (view == null || view.Candidates.Count == 0)
        {
            DropGameOverPending(view == null
                ? "场景树里没有可见的 NGameOverScreen"
                : "收尾界面上没有现在就能按的按钮（动画还没播完？）");
            return null;
        }

        var signature = GameOverSignature(view);
        var state = _gameOverPending;
        if (state == null || !string.Equals(state.Signature, signature, StringComparison.Ordinal))
        {
            state = new PendingSelection
            {
                Id = NextId(),
                Min = 1,
                Max = 1,
                Kind = GameOverKind,
                Caller = GameOverCaller,
                Prompt = view.Prompt,
                Cards = view.Candidates,
                GameOverStage = view.Stage,
                Screen = view.Screen,
                CreatedUtc = DateTime.UtcNow,
            };
            state.Signature = signature;
            _gameOverPending = state;
            BridgeLog.Info($"gameover: 新请求 id={state.Id} stage={view.Stage} " +
                           $"summary={view.SummaryVisible} continueEnabled={view.ContinueEnabled} " +
                           $"mainMenuReady={view.MainMenuVisible && view.MainMenuEnabled} " +
                           $"keys={DescribeMenuKeys(view.Candidates)}");
        }
        else
        {
            // 同一页：只刷新节点引用（界面重建会换实例）和提示语，id 不变。
            state.Cards = view.Candidates;
            state.Prompt = view.Prompt;
            state.Caller = GameOverCaller;
            state.GameOverStage = view.Stage;
            state.Screen = view.Screen;
            state.Min = 1;
            state.Max = 1;
        }

        return DescribeGameOverPendingJson(state, view);
    }

    private static Dictionary<string, object> DescribeGameOverPendingJson(PendingSelection pending, GameOverView view)
    {
        var dict = new Dictionary<string, object>
        {
            ["id"] = pending.Id,
            ["kind"] = GameOverKind,
            ["stage"] = pending.GameOverStage ?? GameOverStagePage,
            ["prompt"] = pending.Prompt ?? GameOverPromptPage,
            ["min"] = pending.Min,
            ["max"] = pending.Max,
            ["requireConfirm"] = false,
            ["cancelable"] = false,             // min 恒为 1：不答就停在收尾界面上
            ["caller"] = pending.Caller ?? GameOverCaller,
            ["confidence"] = "high",            // 候选全部来自官方节点的私有字段 / 自身文字，不是猜坐标
            ["animated"] = false,               // mod 不补动画：播的是游戏自己的收尾动效
            ["route"] = GameOverRoute,
            ["candidates"] = pending.Cards.Select(SceneActionCandidateJson).ToList(),
        };

        if (view != null)
        {
            dict["summaryVisible"] = view.SummaryVisible;
            dict["continueEnabled"] = view.ContinueEnabled;
            dict["mainMenuReady"] = view.MainMenuVisible && view.MainMenuEnabled;
            if (view.Died.HasValue) dict["died"] = view.Died.Value;   // RunState.IsGameOver（全员阵亡）
            if (view.Won.HasValue) dict["won"] = view.Won.Value;      // RunHistory.Win
        }
        return dict;
    }

    /// <summary>scene 动作候选的 JSON（收尾 / 推进两条通道共用）：比菜单候选多一个 emptyButton。</summary>
    private static Dictionary<string, object> SceneActionCandidateJson(Candidate c)
    {
        var dict = new Dictionary<string, object>
        {
            ["key"] = c.Key,
            ["title"] = c.Title ?? "",
            ["enabled"] = c.Enabled,
            ["itemType"] = c.EmptyButton ? "EmptyButton" : "Action",
            ["buttonType"] = c.MenuButton != null ? c.MenuButton.GetType().Name : "",
            ["detail"] = c.Detail ?? "",
        };
        if (c.EmptyButton) dict["emptyButton"] = true;
        return dict;
    }

    /// <summary>把待答的收尾请求摘掉（不动游戏状态）。</summary>
    private static void DropGameOverPending(string why)
    {
        var pending = _gameOverPending;
        if (pending == null) return;
        _gameOverPending = null;
        BridgeLog.Info($"gameover: 清掉请求 id={pending.Id}（{why}）");
    }

    /// <summary>ok:false 时把请求挂回槽里，让 agent 改一道答案再答同一个 id。</summary>
    private static void RestoreGameOverAfterReject(PendingSelection pending)
    {
        if (pending == null) return;
        if (_lastAnsweredId == pending.Id) _lastAnsweredId = -1;
        _gameOverPending = pending;
        BridgeLog.Info($"gameover: 请求 id={pending.Id} 没被接受，恢复成可再答（已挂回 _gameOverPending）");
    }

    /// <summary>
    /// 收尾通道的应答入口（包裹层）：校验 → 现场重读 → 交给核心体按下去。
    /// 任何 ok:false 都把请求挂回槽里，所以能改一道答案再答。
    /// </summary>
    private static async Task<Dictionary<string, object>> AnswerGameOver(PendingSelection pending, string[] picks)
    {
        picks ??= Array.Empty<string>();
        var failure = Validate(pending.Min, pending.Max, pending.Cards, picks, out var chosen, "项");
        if (failure != null) return failure;

        GameOverView view;
        try { view = await BridgePump.OnMain(ReadGameOverView); }
        catch (Exception e) { return Fail("读取游戏结束界面失败：" + e.Message); }
        if (view == null)
            return Fail("游戏结束界面已经不在场景树里了（可能已经回到主菜单，重新 GET /request）");

        var key = chosen[0].Key;
        var fresh = view.Candidates.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));
        if (fresh == null)
            return Fail($"{key} 不在当前收尾界面的候选里（可选：{DescribeMenuKeys(view.Candidates)}）");

        var sw = Stopwatch.StartNew();

        // 先摘掉：按下去的过程中不能再被 /request 看到，也不能被答第二次。
        _gameOverPending = null;
        _lastAnsweredId = pending.Id;
        _animating = true;

        Dictionary<string, object> result;
        try
        {
            result = await ApplyGameOverChoice(pending, fresh, view, sw);
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"gameover: 应答 id={pending.Id} 抛异常（{e.GetType().Name}）：{e.Message}");
            result = Fail("游戏结束界面操作出错：" + e.Message);
        }
        finally { _animating = false; }

        if (!IsTrue(result, "ok")) RestoreGameOverAfterReject(pending);
        return result;
    }

    /// <summary>
    /// 核心体：ForceClick 那个按钮 → 等界面真的推进。
    /// 点的是「继续」时给更长的预算（结算动画一路 await，最后才亮「主菜单」）。
    /// </summary>
    private static async Task<Dictionary<string, object>> ApplyGameOverChoice(
        PendingSelection pending, Candidate chosen, GameOverView view, Stopwatch sw)
    {
        var press = await BridgePump.OnMain(() => PressSceneButton(chosen));
        if (!press.Ok) return Fail(press.Why ?? $"{chosen.Key} 没按下去");

        // 点的是「继续」/ 那个空按钮时给更长的预算（结算动画一路 await，最后才亮「主菜单」）。
        bool longWait;
        try { longWait = await BridgePump.OnMain(() => chosen.EmptyButton || IsContinueButton(view, chosen), 3000); }
        catch { longWait = chosen.EmptyButton; }
        var budget = longWait ? GameOverSummaryMs : GameOverSettleMs;

        var change = await WaitGameOverProgress(view, budget);
        var waited = sw.ElapsedMilliseconds;

        if (!change.Changed)
        {
            return Fail($"{chosen.Key} 的点击入口调过（{press.How}），但收尾界面在 {waited}ms 内没有任何变化" +
                        "（结算动画可能还在播：稍等再 GET /request，或者点那个没有文字的按钮）");
        }

        BridgeLog.Info($"gameover answered id={pending.Id} stage={view.Stage} key={chosen.Key} " +
                       $"how={press.How} next={change.Now?.Stage ?? "(界面关了)"} " +
                       $"mainMenuReady={change.MainMenuReady} waitedMs={waited}");

        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["source"] = "screen",
            ["route"] = GameOverRoute,
            ["stage"] = view.Stage,
            ["applied"] = chosen.Key,
            ["how"] = press.How,
            ["animated"] = false,          // mod 不补动画：播的是游戏自己的收尾动效
            ["waitedMs"] = waited,
            ["visual"] = GameOverVisualApplied,
            ["button"] = chosen.Title ?? "",
            ["settled"] = true,
            ["nextStage"] = change.Now?.Stage,       // 观测到的下一阶段；界面关了就是 null
            ["mainMenuReady"] = change.MainMenuReady,
        };
        if (chosen.EmptyButton) result["emptyButton"] = true;
        return result;
    }

    /// <summary>这个候选是不是「继续」那个按钮（按引用比，不按标题猜）。</summary>
    private static bool IsContinueButton(GameOverView view, Candidate chosen)
    {
        var cont = SafeModel(() => ReadMember(view?.Screen, "_continueButton") as NClickableControl);
        return cont != null && chosen?.MenuButton != null && ReferenceEquals(cont, chosen.MenuButton);
    }

    /// <summary>
    /// 等收尾界面推进：① 界面整个消失（主菜单接上了）②「主菜单」按钮真的亮起来
    /// ③ 阶段 / 候选变了。都不是就等到预算用完，如实返回未变化。
    /// </summary>
    private static async Task<GameOverChange> WaitGameOverProgress(GameOverView before, int budgetMs)
    {
        var beforeSignature = GameOverSignature(before);
        for (var waited = 0; waited < budgetMs; waited += GameOverPollMs)
        {
            await Task.Delay(GameOverPollMs);

            GameOverView now;
            try { now = await BridgePump.OnMain(ReadGameOverView); }
            catch { return new GameOverChange { Changed = true, Now = null }; }

            if (now == null) return new GameOverChange { Changed = true, Now = null };   // 界面没了：回主菜单了
            var ready = now.MainMenuVisible && now.MainMenuEnabled;
            if (ready) return new GameOverChange { Changed = true, Now = now, MainMenuReady = true };
            if (!string.Equals(GameOverSignature(now), beforeSignature, StringComparison.Ordinal))
                return new GameOverChange { Changed = true, Now = now, MainMenuReady = ready };
        }
        return new GameOverChange { Changed = false, Now = before };
    }

    // =================================================================== 推进通道（kind=proceed）
    /*
     * 战斗刚结束、但奖励界面还没出现的那个空档：`/request` 以前是 pending:null，
     * agent 只能干等或者去点记忆里的 (1940,1226)。Boss 战尤其明显 —— 点了那个「空按钮」
     * 之后奖励列表才弹出来。
     *
     * 这里把它做成一条显式请求：kind:"proceed"。
     *   * 判定：CombatManager.Instance.IsOverOrEnding（public prop，MetaPriv 核对过）
     *     且当前房间还是战斗房（RoomType ∈ {Monster, Elite, Boss}）
     *     且 RunState.IsGameOver == false（死了走 gameover 通道）
     *     且没有任何奖励 / 宝箱 / 事件 / 营火 / 商店界面在场（在场的是更高优先级的通道）。
     *   * 候选：优先 **NProceedButton**（MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton，
     *     就是那个右下角的「前进」控件，`ScreenApi.Proceed` 走的是同一个类型）；
     *     找不到才退化成「可见、可用、没有文字、位于屏幕右下半区」的 NClickableControl
     *     （纯相对位置判断，不写死坐标）。
     *   * 触发：**不自动点**，而是发给 agent 让它答 —— 这个按钮在「奖励界面已经出来」时
     *     是奖励屏自己的「跳过/离开」键，自动点会跳过奖励，风险太大。
     *     位置由场景状态保证（有奖励屏就不发这条请求），所以 agent 看到的必然是「该推进了」。
     */

    private const string ProceedKind = "proceed";
    private const string ProceedRoute = "proceed";
    private const string ProceedStageCombatEnd = "combat-end";
    private const string ProceedPrompt =
        "战斗结束：按「继续 / 前进」推进（点了之后奖励列表才会出现；没有奖励时就是离开这个房间）";
    private const string ProceedVisualApplied = "proceed-applied";
    private const int ProceedSettleMs = 3600;
    private const int ProceedPollMs = 150;

    /// <summary>
    /// 「战斗刚结束」这个状态要先稳定存在这么久才发请求。
    /// 正常战斗结束时奖励界面 ~1 秒内就自己弹出来了，这段窗口期不该打扰 agent；
    /// 只有像 Boss 那样真卡住了（奖励一直不出现），才把「推进」交给它。
    /// </summary>
    private const int ProceedArmMs = 1200;

    /// <summary>第一次看到「战斗结束、还在战斗房」的时刻；状态一结束就归零。</summary>
    private static DateTime _proceedFirstSeenUtc;

    /// <summary>推进通道的待答请求。单槽：一次只有一场战斗刚结束。</summary>
    private static PendingSelection _proceedPending;

    /// <summary>推进面这一帧的现场。</summary>
    private sealed class ProceedView
    {
        public Candidate Candidate;
        public string Stage = ProceedStageCombatEnd;
        public string Prompt = ProceedPrompt;
        public string Caller = "NProceedButton";
        public string Detail;
    }

    /// <summary>`CombatManager.IsOverOrEnding`，且房间还是战斗房、没判负、没有更高优先级界面。</summary>
    private static bool CombatJustEndedInCombatRoom()
    {
        try
        {
            var run = RunManager.Instance;
            if (run == null || !run.IsInProgress) return false;

            var combat = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
            if (combat == null || !combat.IsOverOrEnding) return false;

            var state = run.DebugOnlyGetState();
            if (state == null || state.IsGameOver) return false;

            var room = state.CurrentRoom;
            if (room == null) return false;
            var type = room.RoomType;
            if (type != MegaCrit.Sts2.Core.Rooms.RoomType.Monster
                && type != MegaCrit.Sts2.Core.Rooms.RoomType.Elite
                && type != MegaCrit.Sts2.Core.Rooms.RoomType.Boss) return false;

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 找「推进」这个控件（必须在主线程跑）：先认类型 NProceedButton（可见 + 可用），
    /// 再退化到「可见、可用、没有文字、在屏幕右下半区」的 NClickableControl。
    /// </summary>
    private static NClickableControl FindAdvanceControl()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return null;

        MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton proceed = null;
        var extras = new List<NClickableControl>();

        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 40000)
        {
            var node = stack.Pop();
            if (node == null) continue;

            if (node is MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton typed)
            {
                if (proceed == null && IsShown(typed) && SafeEnabled(typed)) proceed = typed;
            }
            else if (node is NClickableControl control
                     && IsShown(control) && SafeEnabled(control)
                     && MenuLabel(control).Length == 0
                     && InRightLowerHalf(control)
                     && !UnderScreens(control))
            {
                extras.Add(control);
            }

            foreach (var child in node.GetChildren()) stack.Push(child);
        }

        if (proceed != null) return proceed;

        extras.Sort(CompareByPosition);
        return extras.FirstOrDefault();
    }

    /// <summary>控件中心是否落在视口右下半区（相对位置，不写死像素）。</summary>
    private static bool InRightLowerHalf(Control control)
    {
        try
        {
            var viewport = control.GetViewportRect().Size;
            if (viewport.X <= 1f || viewport.Y <= 1f) return false;
            var origin = control.GetScreenPosition();
            var size = control.Size;
            if (size.X < 40f || size.Y < 24f) return false;     // 太小的是图标/箭头，不是推进键
            var cx = (origin.X + size.X / 2f) / viewport.X;
            var cy = (origin.Y + size.Y / 2f) / viewport.Y;
            return cx > 0.5f && cy > 0.5f;
        }
        catch { return false; }
    }

    /// <summary>这个节点的祖先里有没有「别的界面」（奖励 / 宝箱 / 事件 / 营火 / 商店 / 收尾 / 菜单）。</summary>
    private static bool UnderScreens(Node node)
    {
        string[] fragments =
        {
            "RewardsScreen", "TreasureRoom", "EventRoom", "RestSiteRoom", "MerchantInventory",
            "GameOverScreen", "MainMenu", "CrystalSphere", "SelectionScreen",
        };
        try
        {
            var cur = node;
            for (var depth = 0; cur != null && depth < 64; depth++)
            {
                foreach (var fragment in fragments)
                    if (ScreenApi.TypeChainContains(cur, fragment)) return true;
                cur = cur.GetParent();
            }
        }
        catch { }
        return false;
    }

    private static ProceedView ReadProceedView()
    {
        if (!CombatJustEndedInCombatRoom())
        {
            _proceedFirstSeenUtc = default;      // 状态结束：下次重新计时
            return null;
        }
        if (FindGameOverScreen() != null) return null;

        // 先让这个状态稳定存在 ProceedArmMs（正常战斗的奖励界面会自己在这段时间里弹出来），
        // 真卡住了才把「推进」作为请求发出去。
        if (_proceedFirstSeenUtc == default) _proceedFirstSeenUtc = DateTime.UtcNow;
        if ((DateTime.UtcNow - _proceedFirstSeenUtc).TotalMilliseconds < ProceedArmMs) return null;

        var control = FindAdvanceControl();
        if (control == null) return null;

        var title = MenuLabel(control);
        var isProceedButton = control is MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton;
        if (title.Length == 0) title = isProceedButton ? "继续" : SafeNodeName(control);
        if (string.IsNullOrWhiteSpace(title) || title == "按钮") title = "继续";

        var placement = RelativePlacement(control);
        var detail = isProceedButton
            ? "NProceedButton（游戏的「前进」控件）"
            : "没有文字的推进控件（" + control.GetType().Name + "）";
        if (placement.Length > 0) detail += "，" + placement;

        return new ProceedView
        {
            Candidate = new Candidate
            {
                Key = title + "#1",
                Title = title,
                Upgraded = false,
                Cost = -1,
                Enabled = true,
                MenuButton = control,
                MenuStage = ProceedStageCombatEnd,
                Detail = detail,
            },
            Caller = control.GetType().Name,
            Detail = detail,
        };
    }

    private static string ProceedSignature(ProceedView view) =>
        view?.Stage + "|" + view?.Candidate?.Key + "|" + view?.Caller;

    private static Dictionary<string, object> DescribeProceedPending()
    {
        ProceedView view;
        try { view = ReadProceedView(); }
        catch (Exception e)
        {
            BridgeLog.Warn("proceed: 读现场失败（" + e.GetType().Name + "）：" + e.Message);
            DropProceedPending("读现场失败：" + e.Message);
            return null;
        }

        if (view?.Candidate == null)
        {
            DropProceedPending("战斗还没结束 / 不在战斗房 / 已经有更高优先级的界面");
            return null;
        }

        var signature = ProceedSignature(view);
        var state = _proceedPending;
        if (state == null || !string.Equals(state.Signature, signature, StringComparison.Ordinal))
        {
            state = new PendingSelection
            {
                Id = NextId(),
                Min = 1,
                Max = 1,
                Kind = ProceedKind,
                Caller = view.Caller,
                Prompt = view.Prompt,
                Cards = new List<Candidate> { view.Candidate },
                ProceedStage = view.Stage,
                CreatedUtc = DateTime.UtcNow,
            };
            state.Signature = signature;
            _proceedPending = state;
            BridgeLog.Info($"proceed: 新请求 id={state.Id} key={view.Candidate.Key} how={view.Detail}");
        }
        else
        {
            state.Cards = new List<Candidate> { view.Candidate };
            state.Prompt = view.Prompt;
            state.Caller = view.Caller;
            state.Min = 1;
            state.Max = 1;
        }

        return new Dictionary<string, object>
        {
            ["id"] = state.Id,
            ["kind"] = ProceedKind,
            ["stage"] = state.ProceedStage ?? ProceedStageCombatEnd,
            ["prompt"] = state.Prompt ?? ProceedPrompt,
            ["min"] = state.Min,
            ["max"] = state.Max,
            ["requireConfirm"] = false,
            ["cancelable"] = false,
            ["caller"] = state.Caller ?? "NProceedButton",
            ["confidence"] = "high",
            ["animated"] = false,
            ["route"] = ProceedRoute,
            ["candidates"] = state.Cards.Select(SceneActionCandidateJson).ToList(),
        };
    }

    private static void DropProceedPending(string why)
    {
        var pending = _proceedPending;
        if (pending == null) return;
        _proceedPending = null;
        BridgeLog.Info($"proceed: 清掉请求 id={pending.Id}（{why}）");
    }

    private static void RestoreProceedAfterReject(PendingSelection pending)
    {
        if (pending == null) return;
        if (_lastAnsweredId == pending.Id) _lastAnsweredId = -1;
        _proceedPending = pending;
        BridgeLog.Info($"proceed: 请求 id={pending.Id} 没被接受，恢复成可再答（已挂回 _proceedPending）");
    }

    private static async Task<Dictionary<string, object>> AnswerProceed(PendingSelection pending, string[] picks)
    {
        picks ??= Array.Empty<string>();
        var failure = Validate(pending.Min, pending.Max, pending.Cards, picks, out var chosen, "项");
        if (failure != null) return failure;

        ProceedView view;
        try { view = await BridgePump.OnMain(ReadProceedView); }
        catch (Exception e) { return Fail("读取推进按钮失败：" + e.Message); }
        if (view?.Candidate == null)
            return Fail("现在没有可用的推进按钮了（战斗 / 房间已经变了，重新 GET /request）");

        var key = chosen[0].Key;
        if (!string.Equals(view.Candidate.Key, key, StringComparison.Ordinal))
            return Fail($"{key} 不在当前候选里（可选：{view.Candidate.Key}）");

        var sw = Stopwatch.StartNew();
        _proceedPending = null;
        _lastAnsweredId = pending.Id;
        _animating = true;

        Dictionary<string, object> result;
        try
        {
            var press = await BridgePump.OnMain(() => PressSceneButton(view.Candidate));
            if (!press.Ok)
            {
                result = Fail(press.Why ?? $"{key} 没按下去");
            }
            else
            {
                var (changed, rewardsUp, leftRoom) = await WaitProceedProgress();
                var waited = sw.ElapsedMilliseconds;
                BridgeLog.Info($"proceed answered id={pending.Id} key={key} how={press.How} " +
                               $"rewardsUp={rewardsUp} leftRoom={leftRoom} waitedMs={waited}");
                result = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["source"] = "screen",
                    ["route"] = ProceedRoute,
                    ["stage"] = ProceedStageCombatEnd,
                    ["applied"] = key,
                    ["how"] = press.How,
                    ["animated"] = false,
                    ["waitedMs"] = waited,
                    ["visual"] = ProceedVisualApplied,
                    ["button"] = view.Candidate.Title ?? "",
                    ["settled"] = changed,
                    ["rewardsUp"] = rewardsUp,     // 奖励列表真的弹出来了（Boss 那一幕）
                    ["leftRoom"] = leftRoom,       // 没奖励，直接进下一个房间
                };
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"proceed: 应答 id={pending.Id} 抛异常（{e.GetType().Name}）：{e.Message}");
            result = Fail("推进操作出错：" + e.Message);
        }
        finally { _animating = false; }

        if (!IsTrue(result, "ok")) RestoreProceedAfterReject(pending);
        return result;
    }

    /// <summary>按下推进键之后：奖励屏出现 / 战斗空档结束（换房间了）/ 超时。</summary>
    private static async Task<(bool Changed, bool RewardsUp, bool LeftRoom)> WaitProceedProgress()
    {
        for (var waited = 0; waited < ProceedSettleMs; waited += ProceedPollMs)
        {
            await Task.Delay(ProceedPollMs);

            var rewardsUp = false;
            var stillEnding = true;
            try
            {
                rewardsUp = await BridgePump.OnMain(() =>
                {
                    if (Engine.GetMainLoop() is not SceneTree tree) return false;
                    return FindVisibleNode<MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen>(tree.Root) != null;
                });
                if (!rewardsUp)
                    stillEnding = await BridgePump.OnMain(CombatJustEndedInCombatRoom);
            }
            catch { return (true, rewardsUp, false); }

            if (rewardsUp) return (true, true, false);
            if (!stillEnding) return (true, false, true);
        }
        return (false, false, false);
    }

    /// <summary>
    /// 按下一个场景按钮（收尾 / 推进两条通道共用）：走按钮自己的
    /// NClickableControl.ForceClick()（= OnRelease() + EmitSignal(Released)），不点坐标。
    /// 不可见 / 不可用的按钮**不按**：ForceClick 本身不拦，这里必须替游戏拦住。
    /// </summary>
    private static (bool Ok, string How, string Why) PressSceneButton(Candidate chosen)
    {
        var button = chosen?.MenuButton;
        if (button == null) return (false, GameOverVisualNone, $"{chosen?.Key ?? "?"} 没有对应的按钮节点");
        if (!SafeValid(button) || !button.IsInsideTree())
            return (false, GameOverVisualNone, $"{chosen.Key} 的按钮已经不在场景树里了");
        if (!IsShown(button))
            return (false, GameOverVisualNone, $"{chosen.Key} 的按钮当前不可见");
        if (!SafeEnabled(button))
            return (false, GameOverVisualNone, $"{chosen.Key} 的按钮当前不可用（IsEnabled=false）");

        button.ForceClick();
        return (true, chosen.EmptyButton ? "force-click-empty" : "force-click", null);
    }

    // =================================================================== mod 端动画（selector 通道）

    /// <summary>
    /// selector 通道的动画前半段（必须在主线程跑，用 BridgePump.OnMain 调进来）：
    ///
    /// 1. 给每张被选中的牌在界面树里找回它的 <c>NCardHolder</c>（selector 通道的候选没有节点引用，
    ///    只能按 <see cref="CardModel"/> 现找），然后做**悬停抬起效果**：
    ///    * 手牌里的牌：在原来的位置上 <c>SetTargetPosition(TargetPosition + (0,-120))</c> 抬起来、
    ///      <c>SetTargetScale(HoverScale)</c> 放大、<c>SetTargetAngle(0)</c> 摆平。这套就是游戏鼠标悬停
    ///      用的目标动画（<c>SetDefaultTargets()</c> 是它的复位版），**不碰** <c>NSelectedHandCardContainer</c>：
    ///      selector 模式下游戏没进 Select 模式，往那个容器 Add 不生效（已实测 selected=0）。
    ///    * 其它界面（牌组/网格/正在出牌）：退化成 <c>NCard.CardHighlight.AnimShow()</c> + 金色 modulate
    ///      + 一条 tween 轻微抬起。
    /// 2. 让鲸鱼气泡报一句「升级 防御」，让玩家知道刚才那张牌被判成了什么。
    ///
    /// 这里**只动表现**：不写 <c>NPlayerHand._selectedCards</c>、不调 <c>SelectCardInSimpleMode</c>
    /// 这类会改游戏选择状态的方法，游戏数据一行都不碰。
    /// 停留由调用方控制：抬起 520ms → 结果落地（真升级）→ 260ms → <see cref="EndAnswerAnimation"/> 收尾。
    /// </summary>
    private static List<CardVisual> BeginAnswerAnimation(PendingSelection pending, List<Candidate> chosen)
    {
        var visuals = new List<CardVisual>();
        if (_animationClosed) return visuals;      // 这次应答已经收过尾了（主线程被拖住才会走到这里）
        _activeVisuals = visuals;

        foreach (var c in chosen)
        {
            var visual = new CardVisual { Candidate = c };
            try
            {
                var holder = FindCardHolder(c.Card);
                if (holder != null)
                {
                    visual.Holder = holder;
                    visual.Highlight = LightUp(holder);
                    LiftForSelection(visual);          // 手牌：悬停抬起；其它界面：tween 抬起
                }
                else
                {
                    BridgeLog.Info($"animation: 界面树里找不到「{c.Key}」的 holder，只能高亮/气泡");
                }
            }
            catch (Exception e)
            {
                // FindCardHolder / LightUp 失败时这张牌还没抬起来；抬起本身的异常在 LiftForSelection 里
                // 已经处理并复位，所以这里不用再收尾（否则会在升级前白闪一下）。
                LogAnimError($"准备抬起「{c.Key}」", e);
            }
            visuals.Add(visual);
        }

        // 鲸鱼只说 agent 让它说的话（sts2_whale_say）；这里不再自动冒一句模板，
        // 否则玩家会看到“系统在自言自语”。过程信息只写 BridgeLog。

        return visuals;
    }

    /// <summary>
    /// 点亮 holder 上那张牌的高亮 shader：<c>AnimShow()</c>（游戏自己的 0.5s 渐显，停在微光）
    /// + 金色 modulate（<c>UpdateCard</c> 里给「这张可选/被选中」上色用的就是 <c>NCardHighlight.gold</c>）。
    /// 返回点亮的高亮节点；取不到就是 null。
    /// </summary>
    private static MegaCrit.Sts2.Core.Nodes.Cards.NCardHighlight LightUp(Node holder)
    {
        var highlight = HighlightOf(holder);
        if (highlight == null) return null;
        try
        {
            highlight.Modulate = MegaCrit.Sts2.Core.Nodes.Cards.NCardHighlight.gold;
            highlight.AnimShow();
            return highlight;
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"animation: 高亮点亮失败（{e.GetType().Name}）：{e.Message}");
            return null;
        }
    }

    /// <summary>动画出错时把异常类型也写进日志：只写 Message 分不清空引用 / Godot 的 native 错。</summary>
    private static void LogAnimError(string what, Exception e)
    {
        BridgeLog.Warn($"animation: {what}失败（{e.GetType().Name}）：{e.Message}");
    }

    /// <summary>
    /// 悬停抬起（纯视觉，收尾时 <see cref="RestoreLift"/> 还原）：
    /// * 手牌：用游戏自己的 <c>NHandCardHolder.SetTargetPosition/SetTargetScale/SetTargetAngle</c> ——
    ///   和鼠标悬停同一组目标动画（<c>SetDefaultTargets()</c> 就是它们的复位版）。目标位置上移 120px、
    ///   缩放到 <c>HoverScale</c>（NCardHolder 上的 protected 属性，反射读；手牌基础缩放是 0.8，
    ///   悬停到 1.0，肉眼可见放大），角度摆平。
    /// * 网格/牌组：没有这套目标动画，用一条 tween 轻微上移 + 放大。
    /// 成功时置 <see cref="CardVisual.Lifted"/> = true（返回里的 lifted / visual 靠它判断）。
    /// </summary>
    private static void LiftForSelection(CardVisual v)
    {
        if (v?.Holder is not MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder holder) return;
        if (!GodotObject.IsInstanceValid(holder)) return;

        v.SavedPosition = holder.Position;
        v.SavedScale = holder.Scale;

        try
        {
            if (holder is MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder hand)
            {
                // HoverScale 是 NCardHolder 上的 protected 属性（get_HoverScale），只能反射读。
                // 读不到（或读到 0）就按当前 scale × 1.25 放大，保证一定看得出来。
                var readHover = ReadMember(hand, "HoverScale") is Vector2 hv && hv.X > 0f && hv.Y > 0f
                    ? hv
                    : (Vector2?)null;
                var hover = readHover ?? v.SavedScale * 1.25f;
                if (readHover == null)
                    BridgeLog.Info($"animation: 读不到「{v.Candidate?.Key}」的 HoverScale，改用 scale×1.25={hover}");

                hand.SetTargetPosition(hand.TargetPosition + new Vector2(0f, -120f));
                hand.SetTargetScale(hover);
                hand.SetTargetAngle(0f);
                hand.ZIndex = Math.Max(hand.ZIndex, 1);
                v.Lifted = true;
                BridgeLog.Info($"animation: 抬起「{v.Candidate?.Key}」→ target={hand.TargetPosition} scale={hover} angle=0");
                return;
            }

            var tween = holder.CreateTween();
            tween.SetParallel(true);
            tween.TweenProperty(holder, "position", v.SavedPosition + new Vector2(0f, -40f), 0.18);
            tween.TweenProperty(holder, "scale", v.SavedScale * 1.08f, 0.18);
            v.Lifted = true;
            BridgeLog.Info($"animation: 抬起「{v.Candidate?.Key}」（网格 tween）");
        }
        catch (Exception e)
        {
            v.Lifted = false;
            LogAnimError($"抬起「{v.Candidate?.Key}」", e);
            // 可能已经设了一半目标（比如位置抬起来了、缩放抛了）：立刻让游戏恢复默认，别把牌留在半空。
            try
            {
                if (holder is MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder half)
                    half.SetDefaultTargets();
            }
            catch (Exception e2) { LogAnimError($"还原「{v.Candidate?.Key}」", e2); }
        }
    }

    /// <summary>收尾的入口（主线程）：把每张牌的抬起姿态还原，best effort，单张失败不影响其它。</summary>
    private static int EndAnswerAnimation(List<CardVisual> visuals)
    {
        _animationClosed = true;
        if (visuals == null) return 0;
        foreach (var v in visuals)
        {
            try { RestoreLift(v); }
            catch (Exception e)
            {
                LogAnimError($"复位「{v?.Candidate?.Key}」", e);
            }
        }
        return visuals.Count;
    }

    /// <summary>收尾的 await 版：主线程做还原，超时/异常都写日志，绝不让 /respond 挂住。</summary>
    private static async Task EndAnimationOrIgnore(List<CardVisual> visuals)
    {
        try { await BridgePump.OnMain(() => EndAnswerAnimation(visuals), 3000); }
        catch (Exception e) { LogAnimError("收尾", e); }
    }

    /// <summary>
    /// 抬起后的收尾（主线程）：
    /// * 手牌：<c>SetDefaultTargets()</c>（z-index / 位置 / 角度 / 缩放一起缓动回默认）→ <c>Flash()</c>
    ///   （升级完成的闪光）→ <c>UpdateCard()</c>（按升级后的数据重画牌面）。三步各自 try，任何一步失败都写日志。
    /// * 网格：tween 回原来的 position / scale。
    /// 节点可能已经被游戏回收（弃牌/消耗类选择会拿走这张牌），先做 IsInstanceValid 判断。
    /// </summary>
    private static void RestoreLift(CardVisual v)
    {
        v.Lifted = false;
        if (v?.Holder == null || !GodotObject.IsInstanceValid(v.Holder)) return;

        if (v.Holder is MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder hand)
        {
            try { hand.SetDefaultTargets(); }              // 游戏自己的「回到手牌默认姿态」
            catch (Exception e) { LogAnimError($"复位「{v.Candidate?.Key}」", e); }
            try { hand.Flash(); }
            catch (Exception e) { LogAnimError($"闪光「{v.Candidate?.Key}」", e); }
            try { hand.UpdateCard(); }
            catch (Exception e) { LogAnimError($"重画「{v.Candidate?.Key}」", e); }
            return;
        }

        if (v.Holder is Control control)
        {
            try
            {
                var tween = control.CreateTween();
                tween.SetParallel(true);
                tween.TweenProperty(control, "position", v.SavedPosition, 0.18);
                tween.TweenProperty(control, "scale", v.SavedScale, 0.18);
            }
            catch (Exception e) { LogAnimError($"复位「{v.Candidate?.Key}」", e); }
        }
    }

    /// <summary>按 CardModel 找回它在界面上的 holder：先问手牌（最准最便宜），再扫整棵树。</summary>
    private static Node FindCardHolder(CardModel card)
    {
        if (card == null) return null;

        // 1) 战斗中的手牌：NPlayerHand 自己就能按 CardModel 找到 holder（「武装」这类选牌走这里）。
        MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand hand = null;
        try { hand = MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance; } catch { }
        if (hand != null && IsShown(hand))
        {
            try
            {
                var holder = hand.GetCardHolder(card);
                if (holder != null && IsShown(holder)) return holder;
            }
            catch (Exception e) { BridgeLog.Warn("animation: NPlayerHand.GetCardHolder 失败：" + e.Message); }
        }

        // 2) 其它界面上（牌堆/奖励/预览——通常 selector 接管后它们压根不出现，这里只是兜底）。
        //    预览窗（NPreviewCardHolder）里的牌不是「那张牌本身」，跳过，免得闪错地方。
        if (Engine.GetMainLoop() is not SceneTree tree) return null;
        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 20000)
        {
            var node = stack.Pop();
            if (node is MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder holder
                && node is not MegaCrit.Sts2.Core.Nodes.Cards.Holders.NPreviewCardHolder)
            {
                try
                {
                    if (ReferenceEquals(holder.CardModel, card) && IsShown(holder)) return holder;
                }
                catch { }
            }
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return null;
    }

    /// <summary>holder（或 NCard 本体）上的高亮节点；取不到就是 null。</summary>
    private static MegaCrit.Sts2.Core.Nodes.Cards.NCardHighlight HighlightOf(Node holder)
    {
        try
        {
            var cardNode = holder switch
            {
                MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder h => h.CardNode,
                MegaCrit.Sts2.Core.Nodes.Cards.NCard n => n,
                _ => null,
            };
            if (cardNode == null) return null;
            var highlight = cardNode.CardHighlight;
            return highlight != null && GodotObject.IsInstanceValid(highlight) ? highlight : null;
        }
        catch { return null; }
    }

    /// <summary>真拦下来的时候留一条日志，方便下次看「这次到底抬起了哪张、抬没抬起来」。</summary>
    private static void LogVisuals(string applied, List<CardVisual> visuals)
    {
        try
        {
            var parts = visuals.Select(v =>
            {
                var how = v.Lifted ? "lifted"
                    : v.Highlight != null ? "highlight"
                    : v.Holder != null ? "holder"
                    : "none";
                return $"{v.Candidate.Key}={how}";
            });
            BridgeLog.Info($"animation: {applied} → " + string.Join(" ", parts));
        }
        catch (Exception e) { LogAnimError("写动画日志", e); }
    }

    /// <summary>
    /// 收尾（best effort，不阻塞 /respond）：<paramref name="delayMs"/> 之后把闪亮淡出。
    /// 节点可能已经被游戏回收，所以每一步都单独 try + IsInstanceValid。
    /// </summary>
    private static async Task FadeHighlights(List<CardVisual> visuals, int delayMs)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            if (visuals == null || visuals.Count == 0) return;

            await BridgePump.OnMain(() =>
            {
                foreach (var v in visuals)
                {
                    var highlight = v?.Highlight;
                    if (highlight == null) continue;
                    try
                    {
                        if (!GodotObject.IsInstanceValid(highlight)) continue;
                        highlight.AnimHide();
                    }
                    catch (Exception e) { LogAnimError($"淡出「{v?.Candidate?.Key}」高亮", e); }
                }
                return 0;
            }, 2000);
        }
        catch (Exception e) { LogAnimError("淡出高亮", e); }
    }

    /// <summary>
    /// 鲸鱼气泡那句话。只说话角色口吻的人话，不要出现 id、数量、接口名这些后台细节
    /// （那些写 BridgeLog 就够了）。
    /// </summary>
    private static string BubbleLineFor(string kind, List<Candidate> chosen)
    {
        if (chosen == null || chosen.Count == 0) return "这波不要了";

        var verb = kind switch
        {
            "upgrade" => "这张先升了",
            "remove" => "这张踢掉",
            "transform" => "这张变一下",
            "enchant" => "给它附个魔",
            "exhaust" => "这张烧了",
            "discard" => "丢掉这张",
            "reward" => "我要这个",
            _ => "就它了",
        };

        var titles = string.Join("、", chosen.Select(c => c.Title + (c.Upgraded ? "+" : "")));
        return verb + " · " + titles;
    }

    // =================================================================== 删牌动画（牌库界面）

    /*
     * 为什么需要这一段：selector 通道把 CardSelectCmd 的选择接管了，游戏自己的选牌界面不弹，
     * 于是「商店买卡牌移除 → 选一张牌删掉」在实际画面上什么都不发生（牌凭空少一张）。
     * 这里在交答案之前，用**游戏自己的**牌库界面把它演一遍：
     *
     *   ① 打开：NDeckViewScreen.ShowScreen(player)（public static；牌库按钮 OnRelease 走的就是它，
     *      IL 里是 Instantiate(ScenePath) → NCapstoneContainer.Instance.Open(screen)）。
     *      TestMode 开着时它直接返回 null —— 那就降级，不假装演过。
     *   ② 点亮：NCardsViewScreen._grid : NCardGrid（protected 字段，反射读）→
     *      NCardGrid.GetCardHolder(CardModel) 找到那张牌的 holder（网格异步初始化，要轮询）→
     *      NCardGrid.HighlightCard(CardModel)（游戏自己的高亮入口，内部 _highlightedCards.Add +
     *      NCard.GetCardNode(model).CardHighlight.AnimShow()），再复用 LiftForSelection 轻微抬起。
     *   ③ 停 RemoveHoldMs（620ms）让人看清选中哪张。
     *   ④ 关闭：NCapstoneContainer.Instance.Close()（= 牌库返回按钮 OnReturnButtonPressed 里那一句；
     *      关完 NCardsViewScreen.AfterCapstoneClosed 会 Visible=false + QueueFreeSafely）。
     *   ⑤ 关上之后才把答案交回游戏（Completion.TrySetResult），这一刻牌才真的从牌组里消失。
     *
     * 只在 kind=remove / caller=FromDeckForRemoval 时走；升级、弃牌等选择完全不变。
     * 任何一步失败都如实降级成 remove-delay（不打开就不打开、只做延迟），绝不留一个亮着的牌库界面。
     */

    /// <summary>删牌动画的现场：哪一步成了、哪一步没成、为什么。</summary>
    private sealed class DeckRemovalStage
    {
        public bool Opened;                 // 牌库界面真打开了
        public bool Highlighted;            // 真在牌库里点亮了那张牌
        public bool Closed;                 // 真关掉了（或者本来就没了）
        public int WaitedMs;                // 这一段花了多久
        public string Why;                  // 降级/超时的原因（人话）
        public string ScreenName;           // 用到的界面类型名（回查用）
        public Node Screen;                 // NDeckViewScreen 实例
        public CardVisual Visual;           // 点亮+抬起的那张（关不掉时要还原）
    }

    /// <summary>
    /// 这条选择是不是「从牌组里挑一张」。
    /// 不能只认 FromDeckForRemoval / kind=remove：商店买卡牌移除走的是 CardSelectCmd.FromDeckGeneric
    /// （kind=simple），原来那样判断会漏掉，退化成「只有延迟、没有牌库动画」。
    /// 所以只要 caller 是 FromDeck* 就算——删牌、牌组升级、牌组变形、牌组附魔都在里面。
    /// </summary>
    private static bool IsDeckRemovalRequest(PendingSelection pending)
        => pending != null
           && (string.Equals(pending.Kind, "remove", StringComparison.Ordinal)
               || (pending.Caller != null
                   && pending.Caller.StartsWith("FromDeck", StringComparison.Ordinal)));

    /// <summary>把一条降级原因追加到现场里（多条用「；」连接）。</summary>
    private static void StageWhy(DeckRemovalStage stage, string why)
    {
        if (stage == null || string.IsNullOrEmpty(why)) return;
        stage.Why = string.IsNullOrEmpty(stage.Why) ? why : stage.Why + "；" + why;
    }

    /// <summary>
    /// 演一整段删牌动画（HTTP 线程）：打开 → 点亮 → 停 → 关闭。全程 BridgePump.OnMain + Task.Delay，
    /// 不用 Thread.Sleep、不点坐标。返回的现场会写进 /respond 的 deckAnim 字段，visual 由它决定。
    /// </summary>
    private static async Task<DeckRemovalStage> PlayDeckRemovalAnimation(List<Candidate> chosen)
    {
        var sw = Stopwatch.StartNew();

        DeckRemovalStage stage;
        var openTimedOut = false;
        try { stage = await BridgePump.OnMain(OpenDeckForRemoval, RemoveBudgetMs); }
        catch (Exception e)
        {
            openTimedOut = true;      // 主线程那次可能只是迟到：它真弹出来了就得替它收尾
            stage = new DeckRemovalStage { Why = "打开牌库超时/失败：" + e.Message };
        }
        if (stage == null) stage = new DeckRemovalStage { Why = "打开牌库没有返回结果" };

        var card = chosen?.FirstOrDefault(c => c?.Card != null);
        if (card == null) StageWhy(stage, "候选里没有 CardModel");

        if (!stage.Opened || card == null)
        {
            // 打不开牌库（TestMode / 已经开着别的界面 / 读不到玩家…）：跳过打开和关闭，只做延迟。
            await Task.Delay(Math.Min(RemoveHoldMs, RemoveBudgetMs));

            // 等超时的那种「打不开」要防一手：主线程可能之后才把界面弹出来，别留一个关不掉的牌库。
            if (openTimedOut)
            {
                try { await BridgePump.OnMain(CloseStrayDeckView, 1500); }
                catch (Exception e) { StageWhy(stage, "超时补关失败：" + e.Message); }
            }

            stage.WaitedMs = (int)sw.ElapsedMilliseconds;
            BridgeLog.Warn($"deck-remove: 跳过牌库动画，只做延迟（{stage.Why ?? "原因未知"}）");
            return stage;
        }

        // 网格是异步铺的：轮询到这张牌的 holder 出现为止（先给关界面留出预算）。
        var gridDeadline = Math.Min(RemoveGridWaitMs,
            Math.Max(0, RemoveBudgetMs - RemoveHoldMs - RemoveCloseReserveMs));
        while (!stage.Highlighted && sw.ElapsedMilliseconds < gridDeadline)
        {
            bool lit;
            try { lit = await BridgePump.OnMain(() => HighlightDeckCard(stage, card), 2000); }
            catch (Exception e)
            {
                StageWhy(stage, "点亮失败：" + e.Message);
                break;
            }
            if (lit) break;
            await Task.Delay(RemovePollMs);
        }
        if (!stage.Highlighted)
            StageWhy(stage, "牌库界面里没摆出这张牌（网格还没铺好，或者这张牌不在可视窗口里）");

        // 停一下，让人看清点亮的是哪一张（预算不够就压缩，不超时）。
        var hold = (int)Math.Clamp(RemoveHoldMs, 0,
            Math.Max(0, RemoveBudgetMs - sw.ElapsedMilliseconds - RemoveCloseReserveMs));
        if (hold > 0) await Task.Delay(hold);

        // 关掉牌库（游戏自己的 NCapstoneContainer.Close）。
        var closeTimeout = (int)Math.Clamp(RemoveBudgetMs - sw.ElapsedMilliseconds,
            RemoveCloseReserveMs, 3000);
        var closed = false;
        try { closed = await BridgePump.OnMain(() => CloseDeckForRemoval(stage), closeTimeout); }
        catch (Exception e) { StageWhy(stage, "关牌库超时：" + e.Message); }

        if (!closed)
        {
            // 关不掉就别留一个亮着的牌库界面在那儿：先还原高亮/抬起，再补关一次。
            try
            {
                await BridgePump.OnMain(() => UndoDeckHighlight(stage), 2000);
                await Task.Delay(RemovePollMs);
                closed = await BridgePump.OnMain(() => CloseDeckForRemoval(stage), 1500);
            }
            catch (Exception e) { StageWhy(stage, "补关牌库失败：" + e.Message); }
        }

        stage.Closed = closed;
        if (!closed) StageWhy(stage, "牌库界面没关掉");

        stage.WaitedMs = (int)sw.ElapsedMilliseconds;
        if (stage.WaitedMs > RemoveBudgetMs)
            StageWhy(stage, $"超出 {RemoveBudgetMs}ms 预算（{stage.WaitedMs}ms）");

        BridgeLog.Info($"deck-remove: opened={stage.Opened} highlighted={stage.Highlighted} " +
                       $"closed={stage.Closed} screen={stage.ScreenName} waitedMs={stage.WaitedMs}" +
                       (string.IsNullOrEmpty(stage.Why) ? "" : " note=" + stage.Why));
        return stage;
    }

    /// <summary>
    /// 主线程：超时降级后的保险 —— 如果顶上正好是牌库界面（多半是刚才那次超时的打开迟到落地了），
    /// 替它关掉，别把玩家卡在一个我们开的、没人收尾的界面里。
    /// </summary>
    private static bool CloseStrayDeckView()
    {
        var container = DeckCapstoneContainer();
        if (container == null) return false;
        try
        {
            if (container.CurrentCapstoneScreen is MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen)
            {
                BridgeLog.Warn("deck-remove: 打开超时了，但牌库界面已经弹出来 → 补关一次");
                container.Close();
                return true;
            }
        }
        catch (Exception e) { BridgeLog.Warn("deck-remove: 补关牌库失败：" + e.Message); }
        return false;
    }

    /// <summary>主线程：打开游戏自己的牌库界面（NDeckViewScreen.ShowScreen）。</summary>
    private static DeckRemovalStage OpenDeckForRemoval()
    {
        var stage = new DeckRemovalStage();

        var container = DeckCapstoneContainer();
        if (container == null) { StageWhy(stage, "NCapstoneContainer 不可用"); return stage; }

        // 已经开着别的 capstone 界面（地图 / 图鉴 / 玩家自己开的牌库）：不去抢，直接降级。
        try
        {
            var current = container.CurrentCapstoneScreen;
            if (current != null)
            {
                StageWhy(stage, "已经有别的界面开着（" + current.GetType().Name + "），不去抢");
                return stage;
            }
        }
        catch (Exception e) { StageWhy(stage, "读 CurrentCapstoneScreen 失败：" + e.Message); return stage; }

        var player = LocalPlayerModel();
        if (player == null) { StageWhy(stage, "读不到本地玩家"); return stage; }

        MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen screen;
        try { screen = MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen.ShowScreen(player); }
        catch (Exception e) { StageWhy(stage, "NDeckViewScreen.ShowScreen 抛了：" + e.Message); return stage; }

        if (screen == null)
        {
            StageWhy(stage, "NDeckViewScreen.ShowScreen 返回 null（TestMode 开着时游戏直接不弹）");
            return stage;
        }

        stage.Screen = screen;
        stage.ScreenName = screen.GetType().Name;
        stage.Opened = IsShown(screen);
        if (!stage.Opened)
        {
            StageWhy(stage, "牌库界面打开后不可见");
            try { container.Close(); } catch { }     // 别留一个看不见的 capstone 顶在那儿
        }
        return stage;
    }

    /// <summary>
    /// 主线程：在牌库界面里点亮这张牌。holder 还没铺出来时返回 false（调用方会再轮询）。
    /// </summary>
    private static bool HighlightDeckCard(DeckRemovalStage stage, Candidate candidate)
    {
        if (stage?.Screen == null || candidate?.Card == null) return false;
        if (!GodotObject.IsInstanceValid(stage.Screen) || !IsShown(stage.Screen)) return false;

        var grid = ReadMember(stage.Screen, "_grid") as MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid;
        if (grid == null) { StageWhy(stage, "读不到牌库界面的 _grid（NCardGrid）"); return false; }

        Node holder;
        try { holder = grid.GetCardHolder(candidate.Card); }
        catch (Exception e) { StageWhy(stage, "NCardGrid.GetCardHolder 失败：" + e.Message); return false; }
        if (holder == null) return false;        // 网格还没把这张牌摆出来：下一轮再试

        try
        {
            // 游戏自己的高亮入口：_highlightedCards.Add(model) + NCard.CardHighlight.AnimShow()
            grid.HighlightCard(candidate.Card);
        }
        catch (Exception e) { StageWhy(stage, "NCardGrid.HighlightCard 失败：" + e.Message); return false; }

        // 跟 selector 通道的网格动画一致：再轻轻抬起来一点，让「选中的是哪张」一眼能看见。
        var visual = new CardVisual { Candidate = candidate, Holder = holder };
        try { LiftForSelection(visual); }
        catch (Exception e) { LogAnimError($"抬起牌库里的「{candidate.Key}」", e); }

        stage.Visual = visual;
        stage.Highlighted = true;
        BridgeLog.Info($"deck-remove: 点亮「{candidate.Key}」holder={holder.Name} lifted={visual.Lifted}");
        return true;
    }

    /// <summary>
    /// 主线程：关掉**我们自己开的那块**牌库界面。顶上的已经不是它（玩家自己换了界面）就不动，
    /// 免得把别人的界面关掉。界面本来就已经没了也算成功。
    /// </summary>
    private static bool CloseDeckForRemoval(DeckRemovalStage stage)
    {
        var screen = stage?.Screen;
        if (screen == null || !GodotObject.IsInstanceValid(screen) || !IsShown(screen))
            return true;                                   // 已经没了

        var container = DeckCapstoneContainer();
        if (container == null) { StageWhy(stage, "NCapstoneContainer 不可用，关不了牌库"); return false; }

        try
        {
            if (!ReferenceEquals(container.CurrentCapstoneScreen, screen))
            {
                StageWhy(stage, "顶上已经不是我们开的牌库界面了，不去关它");
                return false;
            }
            container.Close();                             // = 牌库返回按钮 OnReturnButtonPressed
            BridgeLog.Info("deck-remove: 已调 NCapstoneContainer.Close() 关掉牌库");
            return true;
        }
        catch (Exception e) { StageWhy(stage, "NCapstoneContainer.Close 失败：" + e.Message); return false; }
    }

    /// <summary>主线程：关不掉界面时的收尾 —— 把高亮撤掉、抬起姿态还原。</summary>
    private static bool UndoDeckHighlight(DeckRemovalStage stage)
    {
        if (stage?.Visual == null) return true;
        try
        {
            var grid = ReadMember(stage.Screen, "_grid") as MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid;
            var card = stage.Visual.Candidate?.Card;
            if (grid != null && card != null)
            {
                try { grid.UnhighlightCard(card); }        // AnimHide
                catch (Exception e) { LogAnimError("撤掉牌库高亮", e); }
            }
            RestoreLift(stage.Visual);
            return true;
        }
        catch (Exception e) { LogAnimError("还原牌库高亮", e); return false; }
    }

    /// <summary>主线程能拿到的 capstone 容器（牌库/地图这些「大界面」都挂在它上面）。</summary>
    private static MegaCrit.Sts2.Core.Nodes.Screens.Capstones.NCapstoneContainer DeckCapstoneContainer()
    {
        try { return MegaCrit.Sts2.Core.Nodes.Screens.Capstones.NCapstoneContainer.Instance; }
        catch { return null; }
    }

    /// <summary>本地玩家模型（牌库界面要它来读牌组；和别的通道一致，取 players[0]）。</summary>
    private static MegaCrit.Sts2.Core.Entities.Players.Player LocalPlayerModel()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            return state?.Players != null && state.Players.Count > 0 ? state.Players[0] : null;
        }
        catch { return null; }
    }

    // =================================================================== 校验（两条通道共用）

    /// <summary>返回 null 表示通过；否则是给 agent 的错误字典。</summary>
    private static Dictionary<string, object> Validate(
        int min, int max, List<Candidate> candidates, string[] picks, out List<Candidate> chosen,
        string unit = "张")
    {
        chosen = null;

        var effectiveMin = Math.Max(0, min);
        var effectiveMax = max > 0 ? Math.Min(max, candidates.Count) : candidates.Count;
        if (effectiveMax < effectiveMin) effectiveMax = effectiveMin;

        if (picks.Length < effectiveMin || picks.Length > effectiveMax)
            return Fail(DescribeCount(effectiveMin, effectiveMax, picks.Length, unit));

        var list = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in picks)
        {
            var key = (raw ?? "").Trim();
            if (key.Length == 0) return Fail("picks 里有空的 key");
            var cand = candidates.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));
            if (cand == null)
                return Fail($"{key} 不在候选里（可选：{DescribeKeys(candidates)}）");
            if (!seen.Add(key))
                return Fail($"{key} 重复了，同一张牌不能选两次");
            list.Add(cand);
        }

        chosen = list;
        return null;
    }

    // =================================================================== 发现「正在选牌」

    /// <summary>
    /// 观察当前界面，构造一个请求。顺序：手牌选择模式 → 浮层选牌界面 → 奖励三选一 → 最后手段的猜测。
    /// 必须在主线程调用。
    /// </summary>
    private static Snapshot Capture()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return null;

        // 1) 战斗中的手牌选择：游戏不弹浮层，直接把 NPlayerHand 切进选择模式。
        var hand = TryHandSelection();
        if (hand != null) return hand;

        // 2) 浮层选牌界面（NCardGridSelectionScreen 及其全部派生）。
        var grid = FindFirst(tree.Root, "NCardGridSelectionScreen");
        if (grid != null) return FromGridScreen(grid);

        // 2b) 「从最多 3 张里挑一张」（CardSelectCmd.FromChooseACardScreen）。这个界面不是
        //     NCardGridSelectionScreen 的派生类（直接是 Godot.Control），必须单独认。
        var choose = FindFirst(tree.Root, "NChooseACardSelectionScreen");
        if (choose != null) return FromChooseScreen(choose);

        // 3) 战斗奖励「三选一」不是 NCardGridSelectionScreen（没有 Prefs），但同样是「等玩家决定」。
        var reward = FindFirst(tree.Root, "NCardRewardSelectionScreen");
        if (reward != null) return FromRewardScreen(reward);

        // 4) 都认不出来时的最后手段：战斗在等玩家 + 手牌可以点。
        return TryGuessHandSelection();
    }

    /// <summary>沿继承链找界面：派生类名里根本没有基类名，只比运行时类名会全漏掉。</summary>
    private static Node FindFirst(Node root, string typeFragment)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 40000)
        {
            var node = stack.Pop();
            if (node != root && ScreenApi.TypeChainContains(node, typeFragment) && IsShown(node))
                return node;
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return null;
    }

    /// <summary>
    /// 战斗里「从手牌选牌」（FromHand / FromHandForUpgrade / FromHandForDiscard）走的是
    /// <c>NPlayerHand.SelectCards</c>：没有浮层界面，手牌自己进入选择模式，规则放在 NPlayerHand
    /// 自己的 private <c>_prefs</c> 里。这里读它。
    /// </summary>
    private static Snapshot TryHandSelection()
    {
        var hand = PlayerHand();
        if (hand == null || !IsShown(hand)) return null;

        var mode = ModeName(hand);
        var backstop = ReadMember(hand, "_selectModeBackstop") as Node;
        var backstopShown = backstop != null && IsShown(backstop);

        // 官方写法：IsInCardSelection 就是 CurrentMode ∈ {SimpleSelect, UpgradeSelect}；
        // 再补一个「_selectionCompletionSource 还没完成」（这个 TCS 是进入选择模式时建的）。
        // 遮板 _selectModeBackstop 只在「模式读不出来」时当作旁证 —— 它的默认可见性写在场景里，
        // 静态确认不了，不能拿它单独当判定。
        var modeSelecting = mode == "SimpleSelect" || mode == "UpgradeSelect";
        var active = modeSelecting || SelectionTaskPending(hand) || (mode == null && backstopShown);
        if (!active) return null;

        var snap = new Snapshot { Screen = hand, Hand = hand, Route = "hand" };

        var prefsObj = ReadPrefs(hand);
        if (prefsObj is CardSelectorPrefs prefs && !IsEmptyPrefs(prefs))
        {
            snap.HasPrefs = true;
            snap.Min = prefs.MinSelect;
            snap.Max = prefs.MaxSelect;
            snap.RequireConfirm = prefs.RequireManualConfirmation;
            snap.Cancelable = prefs.Cancelable;
            snap.Prompt = LocText(prefs.Prompt);
        }
        else
        {
            // 读不到规则（模式在，但 prefs 是零值）：按单选处理，并明确标注不可靠。
            snap.Min = 1;
            snap.Max = 1;
            MarkDegraded(snap, "手牌选择模式已确认，但读不到 NPlayerHand._prefs（试过 " +
                               string.Join("/", PrefsMemberNames) + "），按 min=1,max=1 处理");
        }

        // 已经选中的牌不再当候选，min/max 把已选数量扣掉：剩下的就是「还要再选几张」。
        var selected = SelectedInHand(hand);
        snap.AlreadySelected = selected.Count;
        if (selected.Count > 0)
        {
            snap.Min = Math.Max(0, snap.Min - selected.Count);
            snap.Max = Math.Max(0, snap.Max - selected.Count);
        }

        var filter = ReadMember(hand, "_currentSelectionFilter") as Func<CardModel, bool>;
        snap.Candidates = ToCandidates(HandHits(hand, filter, selected));

        if (string.IsNullOrWhiteSpace(snap.Prompt))
        {
            snap.Prompt = HeaderText(hand) ?? DefaultPrompt(KindOf(hand, null));
            snap.PromptFallback = true;
        }

        NormalizeCounts(snap);
        snap.Kind = KindOf(hand, snap.Prompt);
        if (!modeSelecting)
        {
            // 不是靠官方的 CurrentMode 认出来的（靠的还是 TCS / 遮板这类旁证）：打个折扣。
            snap.Confidence = "low";
        }
        return snap;
    }

    /// <summary>
    /// 最后手段：认不出选择模式，但「战斗在等玩家 + 手牌里有多张可以点的牌」。
    /// 条件卡得比较紧（动作队列必须是 NotPlayPhase，且不是敌人回合，且没有浮层），
    /// 因为这条路的置信度低，误判的代价是 agent 去点一张不该点的牌。
    /// </summary>
    private static Snapshot TryGuessHandSelection()
    {
        var hand = PlayerHand();
        if (hand == null || !IsShown(hand)) return null;

        // 有浮层的话就是界面类选择，不该走手牌这条路。
        if (OverlayCount() > 0) return null;

        var hits = HandHits(hand, null, null);
        if (hits.Count < 2) return null;

        if (!CombatWaitingOnPlayer()) return null;

        var snap = new Snapshot
        {
            Screen = hand,
            Hand = hand,
            Route = "hand-guess",
            Min = 1,
            Max = 1,
            Candidates = ToCandidates(hits),
        };
        snap.Prompt = HeaderText(hand) ?? "选择一张手牌";
        snap.PromptFallback = true;
        MarkDegraded(snap, "没有检测到手牌选择模式（CurrentMode/_selectModeBackstop 都读不到），" +
                           "是「动作队列不在 PlayPhase + 还是玩家这一侧 + 手牌可点」猜出来的，可能误判");
        NormalizeCounts(snap);
        snap.Kind = KindOf(hand, snap.Prompt);
        return snap;
    }

    /// <summary>动作队列停在 NotPlayPhase、且当前还是玩家这一侧 —— 典型是某张牌的效果在等玩家选牌。</summary>
    private static bool CombatWaitingOnPlayer()
    {
        try
        {
            var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
            if (cm == null || !cm.IsInProgress) return false;

            var queue = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.ActionQueueSynchronizer;
            if (queue == null) return false;
            // 「NotPlayPhase」= 有个动作正在执行/等玩家输入；PlayPhase 时玩家在正常出牌，不算。
            if (!string.Equals(queue.CombatState.ToString(), "NotPlayPhase", StringComparison.Ordinal)) return false;

            var combat = cm.DebugOnlyGetState();
            if (combat != null && string.Equals(combat.CurrentSide.ToString(), "Enemy", StringComparison.Ordinal))
                return false;   // 敌人回合也会是 NotPlayPhase，别猜

            return true;
        }
        catch { return false; }
    }

    private static Snapshot FromGridScreen(Node screen)
    {
        var snap = new Snapshot { Screen = screen, Route = "grid" };

        var prefsObj = ReadPrefs(screen);
        if (prefsObj is CardSelectorPrefs prefs && !IsEmptyPrefs(prefs))
        {
            snap.HasPrefs = true;
            snap.Min = prefs.MinSelect;
            snap.Max = prefs.MaxSelect;
            snap.RequireConfirm = prefs.RequireManualConfirmation;
            snap.Cancelable = prefs.Cancelable;
            snap.Prompt = LocText(prefs.Prompt);
        }
        else if (prefsObj != null)
        {
            // 万一以后 Prefs 换了类型：成员名照读，别写死。
            snap.HasPrefs = true;
            snap.Min = ReadInt(prefsObj, "MinSelect", 1);
            snap.Max = ReadInt(prefsObj, "MaxSelect", snap.Min);
            snap.RequireConfirm = ReadBool(prefsObj, "RequireManualConfirmation", false);
            snap.Cancelable = ReadBool(prefsObj, "Cancelable", false);
            snap.Prompt = ReadLocString(prefsObj, "Prompt");
        }

        // 读不到（或结构体是零值，界面还没初始化完）时别把 min=0,max=0 当成真规则发出去。
        if (!snap.HasPrefs || (snap.Min <= 0 && snap.Max <= 0 && string.IsNullOrEmpty(snap.Prompt)))
        {
            MarkDegraded(snap, "读不到 CardSelectorPrefs（试过 " + string.Join("/", PrefsMemberNames) +
                               "，含属性与字段），按 min=1,max=1 处理");
            snap.Min = 1;
            snap.Max = 1;
        }

        snap.Candidates = CollectCards(screen);

        if (string.IsNullOrWhiteSpace(snap.Prompt))
        {
            // Prefs 读到了但 Prompt 是空的（或者压根没读到 Prefs）：用界面上的 Label 兜底。
            snap.Prompt = PromptFromLabels(screen, snap.Candidates);
            if (!string.IsNullOrWhiteSpace(snap.Prompt) && !snap.Degraded) snap.PromptFallback = true;
        }

        NormalizeCounts(snap);
        snap.Kind = KindOf(screen, snap.Prompt);
        return snap;
    }

    /// <summary>
    /// <c>NChooseACardSelectionScreen</c>：CardSelectCmd.FromChooseACardScreen 用的「最多 3 张里挑一张」。
    /// 它不派生自 NCardGridSelectionScreen，也没有 CardSelectorPrefs —— 规则是写死在类里的
    /// （选一张就 SetResult），所以 min=max=1；能不能跳过看它的 <c>_canSkip</c>。
    /// </summary>
    private static Snapshot FromChooseScreen(Node screen)
    {
        var snap = new Snapshot
        {
            Screen = screen,
            Route = "choose",
            Min = 1,
            Max = 1,
            Cancelable = ReadMember(screen, "_canSkip") is bool canSkip && canSkip,
            HasPrefs = true,          // 这条规则不是猜的：界面自己就是单选，没有 Prefs 可言
        };

        snap.Candidates = CollectCards(screen);
        snap.Prompt = PromptFromLabels(screen, snap.Candidates) ?? "选择一张牌";
        snap.PromptFallback = true;   // prompt 是从界面文字里挑的，不是 Prefs 带过来的

        if (snap.Candidates.Count == 0)
            MarkDegraded(snap, "NChooseACardSelectionScreen 上没找到可点的卡牌节点");

        NormalizeCounts(snap);
        snap.Kind = KindOf(screen, snap.Prompt);
        return snap;
    }

    /// <summary>
    /// 战斗奖励「三选一」（<c>NCardRewardSelectionScreen</c>，<c>CardReward.OnSelect</c> 弹的那个）。
    ///
    /// 候选不再靠扫节点猜：直接读界面自己的 <c>_options : IReadOnlyList&lt;CardCreationResult&gt;</c>
    /// （每项的 <c>Card</c> 就是三张牌里的一个）。这张界面只有一个结果
    /// <c>OptionSelected() : Task&lt;int?&gt;</c>，所以 max=1 是**结构决定的**，不是猜的。
    /// 应答走界面自己的 <c>SelectCard(NCardHolder)</c>（= 点那张牌的处理方法），不点坐标。
    ///
    /// 「放弃这张牌」也走界面自己的入口：三选一界面除了三张牌，还挂着一排替代项
    /// <c>_extraOptions : IReadOnlyList&lt;CardRewardAlternative&gt;</c>（「跳过」和「重 roll」），
    /// 每个替代项按钮按下后调的都是 private <c>OnAlternateRewardSelected(int)</c>，
    /// 传的是「替代项在 _extraOptions 里的下标」（方法内部自己加 <c>_options.Count</c>，
    /// 给出 <c>_options.Count + index</c> 这个一维答案，<c>CardReward.OnSelect</c> 再按
    /// <c>index - _cards.Count</c> 反查替代项）。所以放弃 = 找到 OptionId=="Skip" 的下标，
    /// 调 <c>OnAlternateRewardSelected(那个下标)</c>；<c>CardReward.OnSelect</c> 拿到
    /// <c>EndSelectionAndDoNotCompleteReward</c> 就结束选择、把界面从 overlay 栈摘掉、
    /// 返回 false（不领），<c>NRewardButton.GetReward</c> 于是走「跳过」分支发 RewardSkipped，
    /// 奖励列表那一行由游戏自己的 <c>NRewardsScreen.RewardSkippedFrom</c> 记账。
    /// 这条界面能不能放弃，以 <c>_extraOptions</c> 里真有没有 "Skip" 为准（<c>CardReward.CanSkip</c>
    /// 为 false 时游戏压根不生成这个替代项）。
    ///
    /// prompt 优先读界面自己的横幅（<c>_banner.label.Text</c>），读不到再用界面文字兜底。
    /// </summary>
    private static Snapshot FromRewardScreen(Node screen)
    {
        var snap = new Snapshot
        {
            Screen = screen,
            Route = "reward",
            Kind = "reward",
            Min = 1,
            Max = 1,
            HasPrefs = true,          // 单选是界面结构决定的：OptionSelected 只回一个下标
        };

        snap.Candidates = CardRewardCandidates(screen);

        // 能不能放弃不是猜的：读界面自己的 _extraOptions，看有没有 OptionId="Skip" 的替代项。
        // 那个替代项就是界面上「跳过」按钮，按下之后游戏调 OnAlternateRewardSelected(它的下标)。
        snap.CanSkip = FindAlternativeIndex(screen, CardRewardSkipOptionId) >= 0;
        snap.Cancelable = snap.CanSkip;
        if (snap.CanSkip) snap.Min = 0;          // min=0：picks:[] 就是「放弃这张」

        var prompt = BannerText(screen);
        if (string.IsNullOrWhiteSpace(prompt)) prompt = PromptFromLabels(screen, snap.Candidates);
        snap.Prompt = string.IsNullOrWhiteSpace(prompt) ? "选择一张牌加入牌组" : prompt;
        // 把「可以放弃」写进 prompt，agent 不用先答一次才知道能空手走。
        if (snap.CanSkip) snap.Prompt += CardRewardSkipHint;
        snap.PromptFallback = true;   // 兜底：提示语是从界面上的文字里读的

        if (snap.Candidates.Count == 0)
            MarkDegraded(snap, "NCardRewardSelectionScreen 的 _options 和界面节点都没读到可选的牌");

        NormalizeCounts(snap);
        snap.Kind = "reward";
        return snap;
    }

    /// <summary>奖励三选一的候选：优先读 <c>_options</c>（模型），读不到才退回扫卡牌节点。</summary>
    private static List<Candidate> CardRewardCandidates(Node screen)
    {
        var list = new List<Candidate>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var card in ReadCardRewardOptions(screen))
        {
            var title = CardTitle(card);
            var holder = CardRewardHolder(screen, card);
            list.Add(new Candidate
            {
                Key = NextKey(counts, title),
                Title = title,
                Upgraded = SafeUpgraded(card),
                Cost = CardCost(card),
                Card = card,
                Node = holder,
                Uid = holder != null && GodotObject.IsInstanceValid(holder) ? holder.GetInstanceId() : 0,
            });
        }

        if (list.Count == 0) list = CollectCards(screen);
        return list;
    }

    /// <summary><c>NCardRewardSelectionScreen._options</c> → CardModel 列表。</summary>
    private static List<CardModel> ReadCardRewardOptions(Node screen)
    {
        var cards = new List<CardModel>();
        if (!(ReadMember(screen, "_options") is IEnumerable options)) return cards;
        try
        {
            foreach (var option in options)
            {
                var card = (ReadMember(option, "Card") ?? ReadMember(option, "originalCard")) as CardModel;
                if (card != null) cards.Add(card);
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("request: 读 NCardRewardSelectionScreen._options 失败：" + e.Message);
        }
        return cards;
    }

    /// <summary>这张牌在奖励界面上的 holder：先问界面自己的 <c>GetCardHolder(CardModel)</c>，再扫树。</summary>
    private static Control CardRewardHolder(Node screen, CardModel card)
    {
        if (screen == null || card == null) return null;
        try
        {
            if (FindMethod(screen.GetType(), "GetCardHolder", 1) is { } getter
                && getter.Invoke(screen, new object[] { card }) is Control direct
                && GodotObject.IsInstanceValid(direct))
            {
                return direct;
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("request: NCardRewardSelectionScreen.GetCardHolder 失败：" + e.Message);
        }
        return FindCardHolder(card) as Control;
    }

    /// <summary>界面横幅上的文字（游戏自己写给玩家看的提示语）。读不到就是 null。</summary>
    private static string BannerText(Node screen)
    {
        try
        {
            var banner = ReadMember(screen, "_banner");
            var label = ReadMember(banner, "label") as Label;
            var text = label?.Text;
            return string.IsNullOrWhiteSpace(text) ? null : ScreenApi.Clean(text);
        }
        catch { return null; }
    }

    /// <summary>
    /// 奖励三选一的应答：把 agent 选的那张交给界面自己的 <c>SelectCard(NCardHolder)</c>。
    /// 界面刚弹出来时 <c>_completionSource</c> 还没建好（要等 CardReward 那边 await OptionSelected），
    /// 所以最多重试 3 次；「界面关掉」仍然用 <see cref="WaitScreenGone"/> 判定。
    /// </summary>
    private static async Task<Dictionary<string, object>> RespondCardReward(
        Snapshot snap, List<Candidate> chosen, Stopwatch sw)
    {
        if (chosen.Count != 1)
            return FailFallback($"奖励三选一只能选 1 张，你给了 {chosen.Count} 张");

        var applied = chosen[0].Key;
        string last = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Dictionary<string, object> r;
            try { r = await BridgePump.OnMain(() => SelectCardRewardOnMain(snap.Screen, chosen[0]), 5000); }
            catch (Exception e) { return FailFallback("选奖励牌时出错：" + e.Message); }

            if (IsTrue(r, "ok"))
            {
                var gone = await WaitScreenGone(snap.Screen, 6);
                BridgeLog.Info($"respond: 奖励三选一 → {applied}（via SelectCard）settled={gone}");
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["source"] = "screen",
                    ["route"] = "reward",
                    ["applied"] = applied,
                    ["how"] = "select-card",
                    ["animated"] = true,
                    ["visual"] = "reward-applied",
                    ["settled"] = gone,
                    ["waitedMs"] = sw.ElapsedMilliseconds,
                    ["fallback"] = true,
                };
            }

            last = r.TryGetValue("error", out var err) ? err as string : null;
            if (IsTrue(r, "fatal")) break;
            await Task.Delay(220);      // 界面还没初始化完：等一帧再试
        }

        return FailFallback("没把奖励牌交回游戏：" + (last ?? "界面已经不在了"));
    }

    /// <summary>主线程：把选择交给 NCardRewardSelectionScreen.SelectCard(holder)。</summary>
    private static Dictionary<string, object> SelectCardRewardOnMain(Node screen, Candidate candidate)
    {
        if (!IsShown(screen))
            return new Dictionary<string, object> { ["ok"] = false, ["fatal"] = true, ["error"] = "界面已经关了" };

        var card = candidate?.Card;
        if (card == null)
            return new Dictionary<string, object> { ["ok"] = false, ["fatal"] = true, ["error"] = "候选里没有 CardModel" };

        var holder = CardRewardHolder(screen, card) ?? candidate.Node as Control;
        if (holder == null || !GodotObject.IsInstanceValid(holder))
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = "找不到这张牌在界面上的 holder" };

        // _completionSource 是 OptionSelected() 建出来的；界面刚弹出来时可能还是 null。
        if (ReadMember(screen, "_completionSource") == null)
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = "界面还没准备好（_completionSource 是 null）" };

        var select = FindMethod(screen.GetType(), "SelectCard", 1);
        if (select == null)
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["fatal"] = true,
                ["error"] = "NCardRewardSelectionScreen.SelectCard 不存在",
            };

        try
        {
            select.Invoke(screen, new object[] { holder });
        }
        catch (Exception e)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["fatal"] = true,
                ["error"] = "SelectCard 抛了：" + e.Message,
            };
        }

        return new Dictionary<string, object> { ["ok"] = true };
    }

    // ------------------------------------------------------------------ 放弃这张奖励牌

    /// <summary>
    /// 在界面的替代项列表里找某个 OptionId 的下标（0-based，相对 <c>_extraOptions</c>）；
    /// 没有就是 -1。只读模型字段，不扫节点：替代项按钮是按这个列表顺序建出来的，
    /// 按钮的 Released 回调里捕获的也正是这个下标。
    /// </summary>
    private static int FindAlternativeIndex(Node screen, string optionId)
    {
        if (screen == null) return -1;
        if (!(ReadMember(screen, "_extraOptions") is IEnumerable alternatives)) return -1;

        var index = 0;
        try
        {
            foreach (var alternative in alternatives)
            {
                var id = ReadMember(alternative, "OptionId") as string;
                if (!string.IsNullOrEmpty(id) &&
                    string.Equals(id, optionId, StringComparison.OrdinalIgnoreCase))
                    return index;
                index++;
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("request: 读 NCardRewardSelectionScreen._extraOptions 失败：" + e.Message);
        }
        return -1;
    }

    /// <summary>
    /// 放弃这张奖励牌：把 game 自己的「跳过」替代项交回去。
    /// 界面刚弹出来时 <c>_completionSource</c> 还没建好（要等 CardReward 那边 await OptionSelected），
    /// 这时候 <c>OnAlternateRewardSelected</c> 是**静默 return**，所以先判空、再重试几次。
    /// 返回里的 <c>applied</c> 说「放弃这张牌」、<c>how</c> 是 "skip"、<c>visual</c> 是 "reward-skipped"。
    /// </summary>
    private static async Task<Dictionary<string, object>> RespondCardRewardSkip(Snapshot snap, Stopwatch sw)
    {
        string last = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Dictionary<string, object> r;
            try { r = await BridgePump.OnMain(() => SkipCardRewardOnMain(snap.Screen), 5000); }
            catch (Exception e) { return FailFallback("放弃奖励牌时出错：" + e.Message); }

            if (IsTrue(r, "ok"))
            {
                var gone = await WaitScreenGone(snap.Screen, 8);
                var how = r.TryGetValue("how", out var h) ? h as string : "skip";
                BridgeLog.Info($"respond: 奖励三选一 → 放弃（via {how}）settled={gone}");
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["source"] = "screen",
                    ["route"] = "reward",
                    ["applied"] = CardRewardSkipApplied,
                    ["how"] = "skip",
                    ["animated"] = true,
                    ["visual"] = "reward-skipped",
                    ["settled"] = gone,
                    ["waitedMs"] = sw.ElapsedMilliseconds,
                    ["skipped"] = 1,
                    // 走的到底是哪一个游戏入口，方便回查（目前就是界面自己的跳过替代项）。
                    ["entry"] = how,
                    ["fallback"] = true,
                };
            }

            last = r.TryGetValue("error", out var err) ? err as string : null;
            if (IsTrue(r, "fatal")) break;
            await Task.Delay(220);      // 界面还没初始化完：等一帧再试
        }

        return FailFallback("没把「放弃这张牌」交回游戏：" + (last ?? "界面已经不在了"));
    }

    /// <summary>
    /// 主线程：调 <c>NCardRewardSelectionScreen.OnAlternateRewardSelected(int)</c> ——
    /// 这就是界面上「跳过」按钮按下时游戏自己调的那个方法（证据：
    /// <c>NCardRewardAlternativeButton</c> 的 Released 回调 <c>&lt;RefreshOptions&gt;b__1</c> 里
    /// 直接 call 它，参数是按钮创建时捕获的 <c>_extraOptions</c> 下标）。
    ///
    /// 注意参数是**替代项下标**，不是一维答案：方法内部自己加 <c>_options.Count</c>，
    /// 所以这里必须传 <see cref="FindAlternativeIndex"/> 的结果（Skip 一般是 0）。
    /// 传成 <c>_options.Count + index</c> 会把答案算成 3+3，<c>CardReward.OnSelect</c> 会落进
    /// 「Received bad player choice index」那条错误分支。
    /// </summary>
    private static Dictionary<string, object> SkipCardRewardOnMain(Node screen)
    {
        if (!IsShown(screen))
            return new Dictionary<string, object> { ["ok"] = false, ["fatal"] = true, ["error"] = "界面已经关了" };

        // _completionSource 是 OptionSelected() 建出来的；为 null 时 OnAlternateRewardSelected 什么都不做。
        if (ReadMember(screen, "_completionSource") == null)
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = "界面还没准备好（_completionSource 是 null）" };

        var index = FindAlternativeIndex(screen, CardRewardSkipOptionId);
        if (index < 0)
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["fatal"] = true,
                ["error"] = "这个奖励界面没有「跳过」替代项（_extraOptions 里没有 OptionId=Skip）",
            };

        var method = FindMethod(screen.GetType(), "OnAlternateRewardSelected", 1);
        if (method == null)
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["fatal"] = true,
                ["error"] = "NCardRewardSelectionScreen.OnAlternateRewardSelected 不存在",
            };

        try
        {
            method.Invoke(screen, new object[] { index });
        }
        catch (Exception e)
        {
            // TCS 已经 SetResult 过会抛 InvalidOperationException：这时候界面其实已经在收尾了。
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["fatal"] = true,
                ["error"] = "OnAlternateRewardSelected 抛了：" + (e.InnerException ?? e).Message,
            };
        }

        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["how"] = "skip-alternative",
            ["index"] = index,
            ["optionId"] = CardRewardSkipOptionId,
        };
    }

    private static void MarkDegraded(Snapshot snap, string reason)
    {
        snap.Degraded = true;
        snap.DegradedReason = reason;
        snap.Confidence = "low";
    }

    /// <summary>max 不能超过候选数，min 不能超过 max（否则校验会自相矛盾）。</summary>
    private static void NormalizeCounts(Snapshot snap)
    {
        if (snap.Candidates.Count == 0)
        {
            // 已经选够、等确认（手牌通道会把 min/max 扣成 0）：保持 0/0，别硬掰成 1。
            if (snap.Max < 0) snap.Max = 0;
            if (snap.Min < 0) snap.Min = 0;
            if (snap.Min > snap.Max) snap.Min = snap.Max;
            return;
        }

        if (snap.Max <= 0) snap.Max = Math.Max(1, snap.Candidates.Count);
        if (snap.Max > snap.Candidates.Count) snap.Max = snap.Candidates.Count;
        if (snap.Min < 0) snap.Min = 0;
        if (snap.Min > snap.Max) snap.Min = snap.Max;
    }

    /// <summary>类型链 → kind。prompt 里的动词比类名更准（同一个类会被多个场景复用）。</summary>
    private static string KindOf(Node screen, string prompt)
    {
        var p = prompt ?? "";

        if (p.Contains("升级")) return "upgrade";
        if (p.Contains("附魔") || p.Contains("注入")) return "enchant";
        if (p.Contains("变化") || p.Contains("变形") || p.Contains("转换")) return "transform";
        if (p.Contains("消耗")) return "exhaust";
        if (p.Contains("丢弃") || p.Contains("弃掉") || p.Contains("弃置")) return "discard";
        if (p.Contains("移除") || p.Contains("删除")) return "remove";
        if (p.Contains("弃")) return "discard";

        if (screen == null) return "simple";
        if (ScreenApi.TypeChainContains(screen, "NDeckUpgradeSelectScreen")) return "upgrade";
        if (ScreenApi.TypeChainContains(screen, "NDeckEnchantSelectScreen")) return "enchant";
        if (ScreenApi.TypeChainContains(screen, "NDeckTransformSelectScreen")) return "transform";
        return "simple";
    }

    // =================================================================== 驱动界面（本地播动画）

    /// <summary>
    /// 手牌选择（**screen 通道 / fallback**：先点手牌节点再等结算）：逐张点手牌节点
    /// （点与点之间留出动画时间），然后等游戏自己结算；
    /// 结算不了（需要手动确认）就点手牌选择自己的确认键。确认「选择模式真的退出了」才算成功。
    /// </summary>
    private static async Task<Dictionary<string, object>> RespondHand(Snapshot snap, List<Candidate> chosen)
    {
        var appliedKeys = chosen.Count == 0 ? "（按确认）" : string.Join("、", chosen.Select(c => c.Key));

        foreach (var c in chosen)
        {
            var r = await BridgePump.OnMain(() => ScreenApi.Tap(c.Uid));
            if (!(r.TryGetValue("ok", out var ok) && ok is bool okb && okb))
            {
                var why = r.TryGetValue("error", out var e) ? e as string : null;
                return FailFallback($"点手牌「{c.Key}」失败：{why ?? "控件已经不在了"}");
            }
            BridgeLog.Info($"respond: tapped hand card {c.Key} uid={c.Uid}");
            // 动画窗口：选中的牌会飞到中间/预览区，下一张要等它飞完再点。
            await Task.Delay(ClickGapMs);
        }

        if (await WaitHandSettled(10))
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = "screen",
                ["route"] = snap.Route,
                ["applied"] = appliedKeys,
                ["how"] = chosen.Count == 0 ? "hand-confirm-auto" : "hand-tap",
                ["animated"] = true,
                ["fallback"] = true,
            };

        // 还没退出选择模式：多半是「选够了但要按确认」。用选择界面自己的确认键。
        // 确认键会滑进来，第一次可能点空，所以给两轮。
        for (var round = 0; round < 2; round++)
        {
            var used = await ClickOwnConfirm(snap.Hand, 4);
            if (used == null) break;
            if (await WaitHandSettled(12))
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["source"] = "screen",
                    ["route"] = snap.Route,
                    ["applied"] = appliedKeys,
                    ["how"] = "hand-tap+" + used,
                    ["animated"] = true,
                    ["fallback"] = true,
                };
        }

        // 到这儿说明点完还不够：把「已选几张 / 需要几张」报出来，别乱点别的按钮。
        var picked = -1;
        var need = -1;
        try
        {
            (int Selected, int Min) state;
            state = await BridgePump.OnMain(() => HandPickState(snap.Hand));
            picked = state.Selected;
            need = state.Min;
        }
        catch { }

        return FailFallback($"已点 {appliedKeys}，但手牌还停在选择模式（已选 {picked} 张、至少需要 {need} 张）");
    }

    /// <summary>手牌选择当前「已选几张 / 至少需要几张」——只给失败时的诊断用。</summary>
    private static (int Selected, int Min) HandPickState(Node hand)
    {
        var selected = SelectedInHand(hand).Count;
        var min = 0;
        if (ReadPrefs(hand) is CardSelectorPrefs prefs) min = prefs.MinSelect;
        return (selected, min);
    }

    /// <summary>
    /// 浮层选牌界面（**screen 通道 / fallback**）：单选复用已验证过的「点卡 → 等 ✓ 滑进来 → 点 ✓」流程；
    /// 多选逐张点、每张之间留动画时间，然后等界面自己关，不关就按界面自己的确认键。
    /// 成功判定沿用现有信号（界面关掉 / 可点的牌变少），不假设成功。
    /// </summary>
    private static async Task<Dictionary<string, object>> RespondOverlay(Snapshot snap, List<Candidate> chosen)
    {
        var appliedKeys = chosen.Count == 0 ? "（按确认）" : string.Join("、", chosen.Select(c => c.Key));

        if (chosen.Count == 1)
        {
            Dictionary<string, object> single;
            try { single = await ScreenApi.SelectWait(chosen[0].Uid); }
            catch (Exception e) { return FailFallback("选牌时出错：" + e.Message); }

            if (IsTrue(single, "confirmed"))
            {
                var gone = await WaitScreenGone(snap.Screen, 4);
                return Done(snap.Route, appliedKeys, "select-card", gone);
            }

            // 兜底 1：点界面自己的确认键（NConfirmButton 没有文字，文本启发式常常找不到）。
            var used = await ClickOwnConfirm(snap.Screen, 5);
            if (used != null)
            {
                var gone = await WaitScreenGone(snap.Screen, 10);
                if (gone) return Done(snap.Route, appliedKeys, "select-card+" + used, true);
            }

            // 兜底 2：通用确认（先按确认词找文字，找不到再按右侧 ✓ 的形状找）。
            if (await ClickGenericConfirm(4))
            {
                var gone = await WaitScreenGone(snap.Screen, 10);
                if (gone) return Done(snap.Route, appliedKeys, "confirm", true);
            }

            var hint = single.TryGetValue("hint", out var h) ? h as string : null;
            if (single.TryGetValue("ok", out var okv) && okv is bool b && !b)
                hint ??= single.TryGetValue("error", out var err) ? err as string : null;
            return FailFallback($"点选没生效：{appliedKeys}" +
                                (string.IsNullOrEmpty(hint) ? "（界面还开着）" : "（" + hint + "）"));
        }

        // 多选：逐个点，然后等界面自己关；不关就按一次界面自己的确认键。
        foreach (var c in chosen)
        {
            var r = await BridgePump.OnMain(() => ScreenApi.Tap(c.Uid));
            if (r.TryGetValue("ok", out var ok) && ok is bool okb && !okb)
            {
                var why = r.TryGetValue("error", out var e) ? e as string : null;
                return FailFallback($"点「{c.Key}」失败：{why ?? "控件已经不在了"}");
            }
            BridgeLog.Info($"respond: tapped card {c.Key} uid={c.Uid}");
            await Task.Delay(ClickGapMs);
        }

        {
            var gone = await WaitScreenGone(snap.Screen, 6);
            if (gone) return Done(snap.Route, appliedKeys, "multi-select", true);
        }

        var own = await ClickOwnConfirm(snap.Screen, 5);
        if (own != null)
        {
            var gone = await WaitScreenGone(snap.Screen, 10);
            if (gone) return Done(snap.Route, appliedKeys, "multi-select+" + own, true);

            // 确认键是滑进来的，第一次可能点空：再来一轮。
            var again = await ClickOwnConfirm(snap.Screen, 4);
            if (again != null)
            {
                gone = await WaitScreenGone(snap.Screen, 10);
                if (gone) return Done(snap.Route, appliedKeys, "multi-select+" + again, true);
            }
        }

        // 兜底：等 ✓ 滑进可视区，再点一次（点之前先确认它在屏幕内，别点空）。
        if (await ClickGenericConfirm(4))
        {
            var gone = await WaitScreenGone(snap.Screen, 10);
            if (gone) return Done(snap.Route, appliedKeys, "multi-select+confirm", true);
        }

        return FailFallback($"已点 {appliedKeys}，但界面没关（可能还差几张没选，或者确认键在屏幕外）");
    }

    /// <summary>
    /// 浮层通道（fallback）成功返回体：<c>animated</c> 恒为 true（这条路真的是在点游戏自己的界面），
    /// 并且带 <c>"fallback": true</c>，标明「这是扫界面 + 模拟点击那条应急路」。
    /// </summary>
    private static Dictionary<string, object> Done(string route, string applied, string how, bool settled)
    {
        var d = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["source"] = "screen",
            ["route"] = route,
            ["applied"] = applied,
            ["how"] = how,
            ["animated"] = true,
            ["fallback"] = true,
        };
        if (!settled)
            d["note"] = "点选/确认已发出，但界面还开着（可能还有下一步选择）。";
        return d;
    }

    /// <summary>
    /// 点通用确认键（ScreenApi 里既有的 FindConfirm / ClickConfirm）：先等它滑进可视区
    /// （没滑进来就点不到），再点。返回是否真的点下去了。
    /// </summary>
    private static async Task<bool> ClickGenericConfirm(int tries)
    {
        for (var i = 0; i < tries; i++)
        {
            bool ready;
            try { ready = await BridgePump.OnMain(ScreenApi.ConfirmReachable); }
            catch { return false; }

            if (ready)
            {
                Dictionary<string, object> r;
                try { r = await BridgePump.OnMain(ScreenApi.ClickConfirmInternal); }
                catch { return false; }
                if (r.ContainsKey("clicked"))
                {
                    BridgeLog.Info("respond: clicked generic confirm");
                    return true;
                }
            }
            await Task.Delay(PollMs);
        }
        return false;
    }

    /// <summary>
    /// 点界面自己的确认键：从 <see cref="ConfirmMemberNames"/> 里找那个可见、可用、在屏幕内的
    /// 确认控件，点它。返回用到的字段名（给 how 用），找不到返回 null。
    /// </summary>
    private static async Task<string> ClickOwnConfirm(Node screen, int tries)
    {
        if (screen == null) return null;

        for (var attempt = 0; attempt < tries; attempt++)
        {
            (ulong Uid, string Name) found;
            try { found = await BridgePump.OnMain(() => FindOwnConfirm(screen)); }
            catch { return null; }

            if (found.Uid != 0)
            {
                var r = await BridgePump.OnMain(() => ScreenApi.Tap(found.Uid));
                if (r.TryGetValue("ok", out var ok) && ok is bool b && b)
                {
                    BridgeLog.Info($"respond: clicked {found.Name}");
                    return found.Name;
                }
            }
            await Task.Delay(PollMs);
        }
        return null;
    }

    /// <summary>界面自己的确认键：可见 + 可用（NClickableControl.IsEnabled）+ 已经滑进视口。</summary>
    private static (ulong Uid, string Name) FindOwnConfirm(Node screen)
    {
        if (screen == null) return (0, null);
        foreach (var name in ConfirmMemberNames)
        {
            if (ReadMember(screen, name) is not Control control) continue;
            if (!IsShown(control)) continue;
            if (ReadMember(control, "IsEnabled") is bool enabled && !enabled) continue;
            if (!IsReachable(control)) continue;
            return (control.GetInstanceId(), name);
        }
        return (0, null);
    }

    /// <summary>
    /// 手牌选择模式是否还在（游戏还在等玩家选牌）。
    /// 注意 <c>_selectionCompletionSource</c> 在结算之后不会被清空，所以必须看 Task 是否已完成。
    /// </summary>
    private static bool HandSelectionActive()
    {
        var hand = PlayerHand();
        if (hand == null || !IsShown(hand)) return false;

        var mode = ModeName(hand);
        if (mode == "SimpleSelect" || mode == "UpgradeSelect") return true;
        return SelectionTaskPending(hand);
    }

    /// <summary>NPlayerHand._selectionCompletionSource 是否「还没完成」（= 正在等玩家选）。</summary>
    private static bool SelectionTaskPending(Node hand)
    {
        if (ReadMember(hand, "_selectionCompletionSource") is not { } tcs) return false;
        try
        {
            var task = tcs.GetType().GetProperty("Task")?.GetValue(tcs) as Task;
            return task != null && !task.IsCompleted;
        }
        catch { return false; }
    }

    private static async Task<bool> WaitHandSettled(int tries)
    {
        for (var i = 0; i < tries; i++)
        {
            await Task.Delay(PollMs);
            bool active;
            try { active = await BridgePump.OnMain(HandSelectionActive); }
            catch { return true; }
            if (!active) return true;
        }
        return false;
    }

    /// <summary>界面真正结束：节点被释放，或者虽然还在树里但已经隐藏。</summary>
    private static async Task<bool> WaitScreenGone(Node screen, int tries)
    {
        for (var i = 0; i < tries; i++)
        {
            await Task.Delay(250);
            bool alive;
            try { alive = await BridgePump.OnMain(() => IsShown(screen)); }
            catch { alive = false; }
            if (!alive) return true;
        }
        return false;
    }

    private static bool IsShown(Node node)
    {
        try
        {
            if (node == null || !GodotObject.IsInstanceValid(node)) return false;
            if (!node.IsInsideTree()) return false;
            return node is not CanvasItem item || item.IsVisibleInTree();
        }
        catch { return false; }
    }

    /// <summary>确认键那条路（<see cref="FindOwnConfirm"/>）用的重载：不享受手牌豁免。</summary>
    private static bool IsReachable(Control node) => IsReachable(node, false);

    /// <summary>
    /// 这个控件现在点不点得到。
    ///
    /// 判据只有一条：它的（全局）矩形和视口矩形**有交集**就算可达。理由：手牌是扇形排布、还会
    /// 随悬停缩放，牌身常常有一大截伸到视口底部之外，要求「整个矩形都在视口里」会把肉眼可见的牌
    /// 判成屏幕外。真正在屏幕外的牌（牌堆滚出可视区的）跟视口一点交集都没有，照样拦得住。
    ///
    /// 老实现（踩过的三个坑，别退回去）：
    ///   1. 只拿 <c>Rect2(node.GetScreenPosition(), node.Size)</c> 一个矩形：屏幕位置是过了 canvas
    ///      变换的，<c>Size</c> 却是局部尺寸；holder 的局部矩形还不能当可点区域用 ——
    ///      <c>NCardHolder</c> 这类节点真正可点的范围在它的 <c>Hitbox</c> 子节点上，自身尺寸可能退化
    ///      成 0，于是交集恒为空，手牌全被判成「在屏幕外」；
    ///   2. 要求交集两轴各 ≥5px：牌只露一条边（或者尺寸是 0）就整张判否；
    ///   3. 没有任何「手牌本来就在屏幕上」的豁免。
    /// 现在：自身矩形 + Hitbox 矩形都算（多边形外接矩形，缩放/旋转也准），任一有交集即放行；
    /// 矩形全退化就用中心点/贴边点兜底；还是量不出来就**放行** —— 真点不到的话
    /// <see cref="ScreenApi.Tap"/> 会如实报错，比在这里误拒强。
    /// </summary>
    /// <param name="trustVisible">
    /// route 是 hand / hand-guess 的候选传 true：手牌就在屏幕上，只要还在树里、还可见，直接算可达。
    /// </param>
    private static bool IsReachable(Node node, bool trustVisible)
    {
        try
        {
            if (!IsShown(node)) return false;   // 不在树里 / 被隐藏：点了也没用

            if (trustVisible) return true;      // 手牌：可见即在屏幕上

            var viewport = ViewportRectOf(node);
            if (viewport.Size.X < 1f || viewport.Size.Y < 1f)
                return true;                    // 视口都量不出来，别拦

            foreach (var rect in NodeRects(node))
            {
                var visible = rect.Intersection(viewport);
                if (visible.Size.X > 0f && visible.Size.Y > 0f) return true;
            }

            // 所有矩形都退化（holder 只是个锚点，Size≈0）：退到「点在不在视口里」。
            if (node is Control control && AnyPointInside(control, viewport)) return true;

            LogOffscreen(node, viewport);
            return false;
        }
        catch { return true; }                  // 判不了就放行，交给真实点击
    }

    /// <summary>视口矩形（视口坐标；<c>GetViewportRect</c> 取不到时退回根视口的可见矩形）。</summary>
    private static Rect2 ViewportRectOf(Node node)
    {
        try
        {
            if (node is CanvasItem canvasItem)
            {
                var rect = canvasItem.GetViewportRect();
                if (rect.Size.X >= 1f && rect.Size.Y >= 1f) return rect;
            }
        }
        catch { }

        try
        {
            var viewport = node.GetViewport();
            if (viewport != null)
            {
                var visible = viewport.GetVisibleRect();
                if (visible.Size.X >= 1f && visible.Size.Y >= 1f) return visible;
            }
        }
        catch { }

        return default;
    }

    /// <summary>
    /// 一个控件在视口坐标系里的候选矩形：它自己的、以及它 <c>Hitbox</c> 子节点的
    /// （holder 的可点区域常常挂在子节点上，两者坐标/尺寸都可能不同）。
    /// </summary>
    private static List<Rect2> NodeRects(Node node)
    {
        var rects = new List<Rect2>();
        AddScreenRect(rects, node);

        var hitbox = HitboxOf(node);
        if (hitbox != null && !ReferenceEquals(hitbox, node)) AddScreenRect(rects, hitbox);

        return rects;
    }

    private static void AddScreenRect(List<Rect2> rects, Node node)
    {
        if (node is not Control control) return;

        // 1) ScreenApi 一直用的那套：屏幕位置 + 局部尺寸（在根画布上和视口坐标一致）。
        try { rects.Add(new Rect2(control.GetScreenPosition(), control.Size)); } catch { }

        // 2) 全局变换 × 局部矩形：把祖先和 CanvasLayer 的位移/缩放/旋转全算进去，取四个角的外接矩形。
        try { rects.Add(GlobalRectOf(control)); } catch { }
    }

    /// <summary>局部矩形 → 视口矩形（四角变换后的外接矩形，旋转过的手牌也是准的）。</summary>
    private static Rect2 GlobalRectOf(Control control)
    {
        var local = new Rect2(Vector2.Zero, control.Size);
        var xform = control.GetGlobalTransformWithCanvas();

        var p0 = xform * local.Position;
        var p1 = xform * new Vector2(local.End.X, local.Position.Y);
        var p2 = xform * local.End;
        var p3 = xform * new Vector2(local.Position.X, local.End.Y);

        var min = new Vector2(
            Mathf.Min(Mathf.Min(p0.X, p1.X), Mathf.Min(p2.X, p3.X)),
            Mathf.Min(Mathf.Min(p0.Y, p1.Y), Mathf.Min(p2.Y, p3.Y)));
        var max = new Vector2(
            Mathf.Max(Mathf.Max(p0.X, p1.X), Mathf.Max(p2.X, p3.X)),
            Mathf.Max(Mathf.Max(p0.Y, p1.Y), Mathf.Max(p2.Y, p3.Y)));

        return new Rect2(min, max - min);
    }

    /// <summary>节点的 <c>Hitbox</c> 成员（NCardHolder 等把真正可点的 NClickableControl 放在这里）。</summary>
    private static Control HitboxOf(Node node)
    {
        try
        {
            var prop = node.GetType().GetProperty("Hitbox", BindingFlags.Instance | BindingFlags.Public);
            if (prop?.GetValue(node) is Control hitbox && GodotObject.IsInstanceValid(hitbox)) return hitbox;
        }
        catch { }
        return null;
    }

    /// <summary>矩形退化成点时用：中心点、或者 ScreenApi.Tap 贴边点击的那个点，落在视口里就算可达。</summary>
    private static bool AnyPointInside(Control control, Rect2 viewport)
    {
        try
        {
            var xform = control.GetGlobalTransformWithCanvas();
            if (viewport.HasPoint(xform * (control.Size / 2f))) return true;
            var corner = xform * Vector2.Zero;
            if (viewport.HasPoint(new Vector2(corner.X + 4f, corner.Y + 4f))) return true;
        }
        catch { }
        return false;
    }

    /// <summary>真拦下来的时候留一条日志，方便下次看「到底算成了什么矩形」。</summary>
    private static void LogOffscreen(Node node, Rect2 viewport)
    {
        try
        {
            var rects = string.Join(" ", NodeRects(node));
            BridgeLog.Info($"reachable: {node.Name} 跟视口没有交集 viewport={viewport} rects=[{rects}]");
        }
        catch { }
    }

    // =================================================================== 游戏节点

    /// <summary>战斗中的手牌节点（不在战斗里就是 null）。</summary>
    private static Node PlayerHand()
    {
        try { return MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance; }
        catch { return null; }
    }

    private static int OverlayCount()
    {
        try { return MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.ScreenCount ?? 0; }
        catch { return 0; }
    }

    /// <summary>NPlayerHand.CurrentMode 的枚举名：None / Play / SimpleSelect / UpgradeSelect。</summary>
    private static string ModeName(Node hand)
    {
        var mode = ReadMember(hand, "CurrentMode");
        return mode?.ToString();
    }

    /// <summary>手牌选择顶部的说明文字（_selectionHeader 是 MegaRichTextLabel，带 [center] 之类标记）。</summary>
    private static string HeaderText(Node hand)
    {
        if (ReadMember(hand, "_selectionHeader") is not Node header) return null;
        var text = ReadMember(header, "Text") as string;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = ScreenApi.Clean(text);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    /// <summary>手牌选择里已经被选中的牌（_selectedCards 是 List&lt;CardModel&gt;）。</summary>
    private static List<CardModel> SelectedInHand(Node hand)
    {
        var list = new List<CardModel>();
        try
        {
            if (ReadMember(hand, "_selectedCards") is IEnumerable raw)
                foreach (var item in raw)
                    if (item is CardModel card) list.Add(card);
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 手牌的候选：只认 NPlayerHand.Holders（CardHolderContainer 的子节点）。
    /// 不能像浮层界面那样扫子树 —— 手节点下面还挂着预览窗（_upgradePreviewContainer）和
    /// 「已选牌」容器（_selectedHandCardContainer），它们里面的牌不是候选。
    /// </summary>
    private static List<KeyValuePair<Control, CardModel>> HandHits(
        Node hand, Func<CardModel, bool> filter, List<CardModel> alreadySelected)
    {
        var hits = new List<KeyValuePair<Control, CardModel>>();

        IEnumerable holders = null;
        try { holders = ReadMember(hand, "Holders") as IEnumerable; } catch { }
        if (holders == null && ReadMember(hand, "CardHolderContainer") is Node container)
            holders = container.GetChildren();
        if (holders == null) return hits;

        foreach (var item in holders)
        {
            if (item is not Node holder) continue;
            if (!IsShown(holder)) continue;

            var card = Choices.CardOf(holder);
            if (card == null) continue;

            if (filter != null)
            {
                try { if (!filter(card)) continue; }
                catch { }
            }
            if (alreadySelected != null && alreadySelected.Any(s => ReferenceEquals(s, card))) continue;

            if (holder is Control control)
                hits.Add(new KeyValuePair<Control, CardModel>(control, card));
        }

        // 手牌是扇形排布：y 会随位置连续变化（还会随悬停/选中的动画抖动），
        // 所以这里只按 x 排，保证 key 的序号稳定。
        SortByPosition(hits, 0);
        return hits;
    }

    // =================================================================== 候选（浮层界面）

    /// <summary>
    /// 收集界面上真实可点的卡牌节点 → CardModel。
    /// 优先只在界面装牌的容器里找：升级界面的大预览窗（NPreviewCardHolder）也在界面子树里，
    /// 但它不是候选，点数就点错了。容器字段名：网格界面是 <c>_grid</c>（基类 protected），
    /// 「二选一」界面是 <c>_cardRow</c>，bundle 界面是 <c>_bundleRow</c>。
    /// </summary>
    private static List<Candidate> CollectCards(Node screen)
    {
        Node root = null;
        foreach (var name in CardContainerMemberNames)
        {
            if (ReadMember(screen, name) is Node container && container.IsInsideTree())
            {
                root = container;
                break;
            }
        }
        if (root == null) root = screen;

        var hits = WalkCardNodes(root, requireVisible: true);
        if (hits.Count == 0) hits = WalkCardNodes(root, requireVisible: false);
        if (hits.Count == 0 && root != screen) hits = WalkCardNodes(screen, requireVisible: false);

        SortByPosition(hits, 24.0);
        return ToCandidates(hits);
    }

    private static List<KeyValuePair<Control, CardModel>> WalkCardNodes(Node root, bool requireVisible)
    {
        var hits = new List<KeyValuePair<Control, CardModel>>();
        var stack = new Stack<(Node Node, int Depth)>();
        stack.Push((root, 0));
        var visited = 0;

        while (stack.Count > 0 && visited++ < 20000)
        {
            var (node, depth) = stack.Pop();
            if (node == null || depth > 14) continue;

            // 隐藏节点的整棵子树都不用看（visibility 是继承的）。
            if (requireVisible && !IsShown(node)) continue;

            // 预览窗里的卡不是「候选」，点它们等于没点。
            if (depth > 0 && ScreenApi.TypeChainContains(node, "NPreviewCardHolder")) continue;

            if (node is Control control)
            {
                var card = OwnCard(control);
                if (card != null)
                {
                    // 一个卡只取最外层那个节点，并且不再往里挖（不然 holder 的每个子节点都会命中）。
                    hits.Add(new KeyValuePair<Control, CardModel>(control, card));
                    continue;
                }
            }

            foreach (var child in node.GetChildren()) stack.Push((child, depth + 1));
        }

        return hits;
    }

    /// <summary>
    /// 只认节点自身（含自身继承链）上的 CardModel 成员，不像 Choices.CardOf 那样往上找祖先：
    /// 往上找会让一张牌的每个子节点都变成一张候选牌。
    /// </summary>
    private static CardModel OwnCard(Control node)
    {
        for (var t = node.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            try
            {
                foreach (var p in t.GetProperties(AnyMember | BindingFlags.DeclaredOnly))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    if (!typeof(CardModel).IsAssignableFrom(p.PropertyType)) continue;
                    if (p.GetValue(node) is CardModel card) return card;
                }
                foreach (var f in t.GetFields(AnyMember | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(CardModel).IsAssignableFrom(f.FieldType)) continue;
                    if (f.GetValue(node) is CardModel card) return card;
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 按界面上的排布排序：key 的序号依赖顺序，顺序不稳 key 就会串位。
    /// 行高取 <paramref name="rowHeight"/> 一档，避免同一行里几像素的抖动把两张牌对调；
    /// <paramref name="rowHeight"/> 传 0 表示「只按 x 排」（手牌那种单行扇形布局用）。
    /// </summary>
    private static void SortByPosition(List<KeyValuePair<Control, CardModel>> hits, double rowHeight)
    {
        var pos = new Dictionary<Control, Vector2>();
        foreach (var h in hits)
        {
            try { pos[h.Key] = h.Key.GetScreenPosition(); }
            catch { pos[h.Key] = Vector2.Zero; }
        }

        hits.Sort((a, b) =>
        {
            var pa = pos[a.Key];
            var pb = pos[b.Key];
            if (rowHeight <= 0)
            {
                var dx = pa.X.CompareTo(pb.X);
                return dx != 0 ? dx : pa.Y.CompareTo(pb.Y);
            }
            var ra = (int)Math.Round(pa.Y / rowHeight);
            var rb = (int)Math.Round(pb.Y / rowHeight);
            return ra != rb ? ra.CompareTo(rb) : pa.X.CompareTo(pb.X);
        });
    }

    private static List<Candidate> ToCandidates(List<KeyValuePair<Control, CardModel>> hits)
    {
        var list = new List<Candidate>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var hit in hits)
        {
            var card = hit.Value;
            var title = CardTitle(card);
            list.Add(new Candidate
            {
                Key = NextKey(counts, title),
                Title = title,
                Upgraded = SafeUpgraded(card),
                Cost = CardCost(card),
                Card = card,
                Node = hit.Key,
                Uid = hit.Key.GetInstanceId(),
            });
        }

        return list;
    }

    private static List<Candidate> ToCandidates(List<CardModel> models)
    {
        var list = new List<Candidate>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var card in models)
        {
            var title = CardTitle(card);
            list.Add(new Candidate
            {
                Key = NextKey(counts, title),
                Title = title,
                Upgraded = SafeUpgraded(card),
                Cost = CardCost(card),
                Card = card,
            });
        }

        return list;
    }

    /// <summary>稳定 key：标题#同标题序号（从 1 开始）。</summary>
    private static string NextKey(Dictionary<string, int> counts, string title)
    {
        counts.TryGetValue(title, out var n);
        n++;
        counts[title] = n;
        return title + "#" + n;
    }

    private static string CardTitle(CardModel card)
    {
        try
        {
            var title = card?.Title;
            if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
        }
        catch { }
        return "?";
    }

    private static bool SafeUpgraded(CardModel card)
    {
        try { return card != null && card.IsUpgraded; }
        catch { return false; }
    }

    private static int CardCost(CardModel card)
    {
        try
        {
            var cost = card?.EnergyCost;
            return cost?.Canonical ?? -1;
        }
        catch { return -1; }
    }

    // =================================================================== Prefs 反射

    /// <summary>零值结构体（界面还没初始化完）等同于读不到。</summary>
    private static bool IsEmptyPrefs(CardSelectorPrefs prefs)
        => prefs.MinSelect <= 0 && prefs.MaxSelect <= 0 && prefs.Prompt == null;

    private static object ReadPrefs(Node screen)
    {
        foreach (var name in PrefsMemberNames)
        {
            var value = ReadMember(screen, name);
            if (value == null) continue;
            if (value is CardSelectorPrefs prefs)
            {
                if (!IsEmptyPrefs(prefs)) return prefs;
                continue;
            }
            // 类型换了也照读：只要它带着 MinSelect/MaxSelect 就当 Prefs 用。
            if (ReadMember(value, "MinSelect") is int) return value;
        }
        return null;
    }

    /// <summary>沿继承链读成员（字段优先于属性：官方各界面都是 private <c>_prefs</c> 字段）。</summary>
    private static object ReadMember(object target, string name)
    {
        if (target == null) return null;
        for (var t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            try
            {
                var f = t.GetField(name, AnyMember | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(target);
                var p = t.GetProperty(name, AnyMember | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(target);
            }
            catch { }
        }
        return null;
    }

    private static int ReadInt(object target, string name, int fallback)
    {
        var v = ReadMember(target, name);
        try { return v == null ? fallback : Convert.ToInt32(v); }
        catch { return fallback; }
    }

    private static bool ReadBool(object target, string name, bool fallback)
    {
        var v = ReadMember(target, name);
        return v is bool b ? b : fallback;
    }

    private static string ReadLocString(object target, string name)
    {
        var v = ReadMember(target, name);
        return v is MegaCrit.Sts2.Core.Localization.LocString loc ? LocText(loc) : null;
    }

    /// <summary>
    /// LocString → 人话。只认两种「真有文案」的来源：<c>GetFormattedText()</c>（本地化 + 动态变量）
    /// 和 <c>GetRawText()</c>（表里的原文）。
    ///
    /// **取不到就返回 null，绝不退回 <c>LocString.ToString()</c>。** 那个 ToString 是调试用的内部表示
    /// （"LocString table ancients entry PROCEED.description"），一旦当成 detail 发出去，agent 看到的就是
    /// 内部 key 而不是文案 —— 涅奥（ancients）事件结束后的合成「继续」选项就是这样：它的
    /// <c>PROCEED.description</c> 在语言表里是空串（英文表也是空串），GetFormattedText/GetRawText 都返回
    /// ""，旧代码于是落到 ToString 上把 key 漏了出去。宁可没有 detail，也不能发假文案。
    /// </summary>
    private static string LocText(MegaCrit.Sts2.Core.Localization.LocString loc)
    {
        if (loc == null) return null;

        try
        {
            var text = loc.GetFormattedText();
            if (UsableLocText(text)) return ScreenApi.CleanLong(text);
        }
        catch { }
        try
        {
            var raw = loc.GetRawText();
            if (UsableLocText(raw)) return ScreenApi.CleanLong(raw);
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 这段文本能不能当「给人看的文案」：非空白，且不是 <c>LocString.ToString()</c> 的内部表示
    /// （"LocString table &lt;表&gt; entry &lt;key&gt;"）。后一种是给日志/调试看的，绝不能发给 agent。
    /// </summary>
    private static bool UsableLocText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return !(text.StartsWith("LocString table ", StringComparison.Ordinal)
                 && text.Contains(" entry ", StringComparison.Ordinal));
    }

    /// <summary>
    /// prompt 兜底：界面上的 Label/RichTextLabel 里挑一句最像提示语的（不是卡名、不是按钮字）。
    /// </summary>
    private static string PromptFromLabels(Node screen, List<Candidate> candidates)
    {
        if (screen == null) return null;

        var titles = new HashSet<string>(candidates.Select(c => c.Title), StringComparer.Ordinal);
        var texts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<Node>();
        stack.Push(screen);
        var visited = 0;

        while (stack.Count > 0 && visited++ < 4000)
        {
            var node = stack.Pop();
            string raw = node switch
            {
                Label label => label.Text,
                RichTextLabel rich => rich.Text,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var text = ScreenApi.Clean(raw);
                if (text.Length >= 4 && !titles.Contains(text) && seen.Add(text)) texts.Add(text);
            }
            foreach (var child in node.GetChildren()) stack.Push(child);
        }

        // 提示语通常是这类句子里最长的那个；排除没有中文/日文的碎片（数字、按键提示）。
        return texts
            .Where(t => t.Any(ch => ch > 0x2e80))
            .OrderByDescending(t => t.Length)
            .FirstOrDefault();
    }

    // =================================================================== id / 文案

    private static long NextId()
    {
        lock (Gate) return ++_nextId;
    }

    /// <summary>
    /// screen 通道的 id：界面内容（route/kind/prompt/min/max/候选的牌与顺序）不变就复用同一个 id，
    /// 变了就发新号，agent 拿着旧 id 来答会被明确拒绝，而不是点错牌。
    /// </summary>
    private static long EnsureId(Snapshot snap)
    {
        var signature = Signature(snap);
        lock (Gate)
        {
            if (_currentId == 0 || !string.Equals(_currentSignature, signature, StringComparison.Ordinal))
            {
                _currentId = ++_nextId;
                _currentSignature = signature;
                BridgeLog.Info($"request id={_currentId} route={snap.Route} kind={snap.Kind} " +
                               $"min={snap.Min} max={snap.Max} candidates={snap.Candidates.Count} " +
                               $"confidence={snap.Confidence}");
            }
            return _currentId;
        }
    }

    private static string Signature(Snapshot snap)
    {
        var parts = new List<string>
        {
            snap.Route ?? "",
            snap.Kind ?? "",
            snap.Prompt ?? "",
            snap.Min.ToString(),
            snap.Max.ToString(),
            snap.HasPrefs ? "1" : "0",
        };
        foreach (var c in snap.Candidates)
        {
            string id;
            try { id = c.Card?.Id?.ToString() ?? "?"; }
            catch { id = "?"; }
            parts.Add(c.Key + "=" + id + (c.Upgraded ? "+" : ""));
        }
        return string.Join("|", parts);
    }

    /// <summary>
    /// 一条候选 → JSON。各通道的 <c>detail</c> 一律写成 <c>c.Detail ?? ""</c>（字段还在，老客户端不受影响）。
    ///
    /// 这里的关键约束：**空 detail 只表示「这条没有文案」**，绝不能是
    /// <c>LocString.ToString()</c> 的内部 key（"LocString table X entry Y"）—— 那会把内部 key
    /// 当文案发给 agent。文案解析统一走 <see cref="LocText"/>，解析不出来就是 null → 这里成 ""。
    /// </summary>
    private static Dictionary<string, object> CandidateJson(Candidate c)
    {
        var dict = new Dictionary<string, object>
        {
            ["key"] = c.Key,
            ["title"] = c.Title,
            ["upgraded"] = c.Upgraded,
            ["cost"] = c.Cost,
        };

        // 占卜候选：格子 / 工具。带上是否已揭示和揭示出的东西类型。
        if (c.DivinationButton != null)
        {
            dict["itemType"] = c.DivinationIsTool ? "Tool" : "Cell";
            dict["enabled"] = c.Enabled;
            dict["detail"] = c.Detail ?? "";
            if (!c.DivinationIsTool)
            {
                dict["grid"] = c.OptionId;
                // 新增字段（不改既有字段）：雾揭开没有 / 已揭示时里面是什么。
                // 读不到就不发，绝不编。
                if (c.DivinationRevealed.HasValue) dict["revealed"] = c.DivinationRevealed.Value;
                if (!string.IsNullOrEmpty(c.DivinationContentType))
                    dict["contentType"] = c.DivinationContentType;
            }
        }

        // 奖励候选额外带两个说明字段（卡牌候选上不出现，老客户端不受影响）。
        if (!string.IsNullOrEmpty(c.RewardType))
        {
            dict["rewardType"] = c.RewardType;
            dict["detail"] = c.Detail ?? "";
        }

        // 宝箱阶段 1 的「打开宝箱」：只有这一个动作候选，不是遗物（没有 treasureIndex / 价格）。
        if (c.TreasureAction)
        {
            dict["itemType"] = "Action";
            dict["enabled"] = c.Enabled;
            dict["detail"] = c.Detail ?? "";
        }

        // 商店阶段 1 的「打开商店」：同样是动作候选（不是商品，没有价格）。
        if (c.ShopOpenAction)
        {
            dict["itemType"] = "Action";
            dict["enabled"] = c.Enabled;
            dict["detail"] = c.Detail ?? "";
        }

        // 药水目标候选：「商人#1」带 enemy:true，「取消#1」带 cancel:true。
        if (c.PotionMerchant)
        {
            dict["itemType"] = "Target";
            dict["enemy"] = true;
            dict["enabled"] = c.Enabled;
        }
        if (c.PotionCancel)
        {
            dict["itemType"] = "Cancel";
            dict["cancel"] = true;
            dict["enabled"] = c.Enabled;
        }

        // 稳定 id：中文名抄错时用它（respond 同时接受 key 和 id）。
        if (!string.IsNullOrEmpty(c.Stable)) dict["id"] = c.Stable;

        // 商店候选额外带价格 / 买不买得起 / 条目类型 / 效果文本（同样只在这些候选上出现）。
        if (!string.IsNullOrEmpty(c.ItemType))
        {
            dict["price"] = c.Price;
            dict["affordable"] = c.Affordable;
            dict["itemType"] = c.ItemType;
            dict["detail"] = c.Detail ?? "";
        }

        // 营火候选额外带 enabled / optionId / 效果文本（同样只在这些候选上出现）。
        // enabled 是模型的真值（界面拿它做变灰），**不**代表「点不动」：
        // RestSiteOption.IsEnabled 的基类实现恒为 true（HP 满时「休息」依然可选，只是回血 0）。
        if (c.RestOption != null)
        {
            dict["enabled"] = c.Enabled;
            dict["optionId"] = c.OptionId ?? "";
            dict["detail"] = c.Detail ?? "";
        }

        // 事件候选额外带 enabled / isProceed / willKillPlayer / optionId（TextKey）/ 效果文本。
        if (c.EventOptionRef != null)
        {
            dict["enabled"] = c.Enabled;
            dict["isProceed"] = c.IsProceed;
            dict["willKillPlayer"] = c.WillKill;
            dict["optionId"] = c.OptionId ?? "";
            dict["detail"] = c.Detail ?? "";
        }

        // 宝箱候选额外带 itemType / enabled / detail / treasureIndex（同样只在这些候选上出现）。
        if (c.TreasureIndex >= 0)
        {
            dict["itemType"] = "Relic";
            dict["enabled"] = c.Enabled;
            dict["detail"] = c.Detail ?? "";
            dict["treasureIndex"] = c.TreasureIndex;
        }

        return dict;
    }

    private static string DescribeCount(int min, int max, int given, string unit = "张")
    {
        if (min == max) return $"只能选 {min} {unit}，你给了 {given} {unit}";
        return $"只能选 {min}~{max} {unit}，你给了 {given} {unit}";
    }

    private static string DescribeKeys(List<Candidate> candidates)
    {
        var keys = candidates.Take(8).Select(c => c.Key).ToList();
        return string.Join("、", keys) + (candidates.Count > 8 ? $" …（共 {candidates.Count} 张）" : "");
    }

    /// <summary>
    /// key 归一化：去掉空白/标点/符号，全角转半角，转小写。
    /// 中文标题当 key 太脆（抄错一个字就失败），归一化后能救回一部分近形/带标点的输入。
    /// </summary>
    private static string NormalizeKey(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var c = ch;
            if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0);   // 全角 → 半角
            if (c == '　') continue;
            if (char.IsWhiteSpace(c)) continue;
            if (char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>编辑距离，只用来在报错里给「最接近的候选」。</summary>
    private static int EditDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>给报错用的「最接近的 N 个候选 key」：先按包含关系，再按编辑距离。</summary>
    private static List<string> NearestKeys(List<Candidate> candidates, string key, int n)
    {
        var norm = NormalizeKey(key);
        if (norm.Length == 0) return new List<string>();
        var scored = new List<(string key, int score)>();
        foreach (var c in candidates)
        {
            var cn = NormalizeKey(c.Key);
            if (cn.Length == 0) continue;
            if (cn.Contains(norm) || norm.Contains(cn)) { scored.Add((c.Key, 0)); continue; }
            var d = EditDistance(norm, cn);
            if (d <= Math.Max(2, norm.Length / 3)) scored.Add((c.Key, d));
        }
        return scored.OrderBy(x => x.score).Select(x => x.key).Distinct().Take(Math.Max(1, n)).ToList();
    }

    private static bool IsTrue(Dictionary<string, object> dict, string key) =>
        dict != null && dict.TryGetValue(key, out var v) && v is bool b && b;

    private static Dictionary<string, object> Fail(string message) =>
        new() { ["ok"] = false, ["error"] = message };

    /// <summary>
    /// screen 通道（fallback）的失败体：除了错误文案，还带 <c>"fallback": true</c>。
    /// 一看就知道这条是「扫界面 + 模拟点击」那条老路给出的结论，不是各房间通道。
    /// </summary>
    private static Dictionary<string, object> FailFallback(string message)
    {
        var result = Fail(message);
        result["fallback"] = true;
        return result;
    }

    // ==================================================================
    // 占卜通道（水晶球小游戏）——事件「水晶球」选完分支后弹出的 NCrystalSphereScreen。
    //
    // 界面：NCrystalSphereScreen（模态小游戏）。
    // 格子：NCrystalSphereCell 节点，每个携带一个 CrystalSphereCell 模型。
    // 操作：格子与「大幅/小幅占卜」按钮都是 NClickableControl —— 调它们自己的 ForceClick()，
    //       等于玩家点一下（游戏的 Released 回调再去调 CrystalSphereMinigame.CellClicked）。
    // 不用坐标、不扫文本。优先级高于事件选项：事件选项这时已经用掉了，再发就是过期请求。
    //
    // ---- 「翻过没翻过」的真值（sts2.dll 里 ClearCell 的 IL 实证）----
    //   CrystalSphereMinigame.ClearCell(x, y):
    //       if (!cells[x, y].IsHidden) return;    // 已经揭示过的格子直接返回（不重复揭示）
    //       cells[x, y].IsHidden = false;         // ← 揭示动作真正改的只有这一个字段
    //       if (cells[x, y].Item != null && AreAllOccupiedCellsClear(Item))
    //           Item.RevealItem(owner);           // ← 再把「道具本身」露出来
    //   所以唯一真值是 CrystalSphereCell 的私有字段 <c>_isHidden</c>（属性 IsHidden 的 setter
    //   在写完之后额外发一次 FogUpdated 给界面）。雾的视觉是 NCrystalSphereMask.UpdateMat
    //   写 shader 参数，那只是渲染，不是状态 —— 不能拿 mask 当状态源。
    //
    // ---- 剧透红线：Item 在揭示**之前**就有值 ----
    //   构造时 PopulateItems() → PlaceItem() → Cell.SetItem(item)，而 CanPlaceHere 要求
    //   「目标格 IsHidden == true 且 Item == null」—— 道具从一开始就压在雾下面。
    //   因此 IsHidden == true 时**绝不能**把 Item 的类型发出去。
    //   另外多格道具要等覆盖的格子**全清**才真正露出来（AreAllOccupiedCellsClear），
    //   所以「格子的雾没了」≠「这件东西玩家已经看见了」：
    //   contentType 只认「小游戏 _revealed 表里有它」或「它覆盖的格子全都没雾了」。
    // ==================================================================

    private const string DivinationScreenTypeName =
        "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen";

    private const string DivinationCellTypeName =
        "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereCell";

    private static PendingSelection _divinationPending;

    /// <summary>已经打过状态探针的屏幕实例（每块屏幕只打一条，免得刷日志）。</summary>
    private static readonly HashSet<ulong> _divinationProbed = new();

    /// <summary>树里所有水晶球界面。正常只有一个；残留/联机时可能不止一个。</summary>
    private static List<Node> FindCrystalSphereScreens()
    {
        var found = new List<Node>();
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return found;
            var stack = new Stack<Node>();
            stack.Push(tree.Root);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 20000)
            {
                var node = stack.Pop();
                if (node.GetType().FullName == DivinationScreenTypeName) found.Add(node);
                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch (Exception e) { BridgeLog.Warn("divination: 找水晶球界面失败：" + e.Message); }
        return found;
    }

    /// <summary>
    /// 挑「当前正在用的那块」水晶球界面：树里可能留着已经用过的旧屏幕，
    /// 拿错一块就会读到一块早已翻完的空板子。优先可见 → 优先在树上 → 第一个。
    /// </summary>
    private static Node FindCrystalSphereScreen()
    {
        var all = FindCrystalSphereScreens();
        if (all.Count == 0) return null;
        if (all.Count == 1) return all[0];

        foreach (var s in all)
        {
            try
            {
                if (GodotObject.IsInstanceValid(s) && s.IsInsideTree()
                    && s is Control control && control.IsVisibleInTree())
                    return s;
            }
            catch { }
        }
        foreach (var s in all)
        {
            try { if (GodotObject.IsInstanceValid(s) && s.IsInsideTree()) return s; } catch { }
        }
        BridgeLog.Warn($"divination: 树上有 {all.Count} 块水晶球界面，没有一块可见，退回第一块");
        return all[0];
    }

    /// <summary>一个格子的真实状态（模型侧 + 节点侧兜底都收在这里）。</summary>
    private sealed class DivinationCellState
    {
        public int X;
        public int Y;
        public object Cell;          // CrystalSphereCell（拿不到就是 null）
        public bool? Hidden;         // true = 雾还在（没翻过）；null = 读不到
        public string HiddenSource;  // 这个结论是从哪儿读到的：field / prop / node / none
        public object Item;          // 这一格压着的道具（**只在已揭示时才允许发出去**）
        public bool ItemRevealed;    // 这件道具是否已经真的露给玩家看了
    }

    /// <summary>占卜界面这一帧的现场：格子状态 + 剩余次数 + 道具/已揭示道具的计数。</summary>
    private sealed class DivinationView
    {
        public Node Screen;
        public object Minigame;
        public string MinigameSource = "none";
        public int Width;
        public int Height;
        public int? DivinationsLeft;
        public bool Finished;
        public int ItemCount;
        public int RevealedItemCount;
        public readonly Dictionary<int, DivinationCellState> Cells = new();

        /// <summary>(x, y) → 字典 key。格子数远小于 1000，不会撞。</summary>
        public static int Key(int x, int y) => x * 1000 + y;
    }

    /// <summary>
    /// 读占卜现场：小游戏实例 → cells 二维数组 → 每格的雾状态和压在下面的道具 → 哪些道具已经露出来了。
    /// 全部走模型字段直读（不动 Godot、不点坐标、不触发任何游戏逻辑）。
    /// </summary>
    private static DivinationView ReadDivinationView(Node screen)
    {
        var view = new DivinationView { Screen = screen };

        var minigame = FindCrystalSphereMinigame(screen, out var how);
        view.Minigame = minigame;
        view.MinigameSource = how;

        if (minigame != null)
        {
            view.DivinationsLeft = ReadIntAny(minigame, "_divinationCount", "DivinationCount");
            view.Finished = ReadBoolAny(minigame, "IsFinished")
                            ?? (view.DivinationsLeft.HasValue && view.DivinationsLeft.Value <= 0);
        }

        // ---- 一、逐格读雾状态 + 压在下面的道具 ----
        if (minigame != null && ReadFieldRaw(minigame, "cells") is Array grid)
        {
            view.Width = grid.GetLength(0);
            view.Height = grid.GetLength(1);
            for (var x = 0; x < view.Width; x++)
            {
                for (var y = 0; y < view.Height; y++)
                {
                    object cell = null;
                    try { cell = grid.GetValue(x, y); } catch { }
                    if (cell == null) continue;

                    var hidden = ReadCellHidden(cell, out var source);
                    view.Cells[DivinationView.Key(x, y)] = new DivinationCellState
                    {
                        X = x,
                        Y = y,
                        Cell = cell,
                        Hidden = hidden,
                        HiddenSource = source,
                        Item = ReadCellItem(cell),
                    };
                }
            }
        }

        // ---- 二、哪些道具已经真的露出来了 ----
        // (a) 游戏自己记的账：小游戏 _revealed 里就是「已经给玩家看过」的那些。
        // (b) 兜底：它覆盖的格子全都没雾了（= 游戏 ClearCell 里的 AreAllOccupiedCellsClear）。
        var revealed = new HashSet<object>(ReferenceEqualityComparer.Instance);   // 按引用比，不碰 Equals
        foreach (var item in ReadObjectList(minigame, "_revealed"))
            if (item != null) revealed.Add(item);

        var items = new List<object>();
        foreach (var item in ReadObjectList(minigame, "_items"))
            if (item != null) items.Add(item);
        view.ItemCount = items.Count;

        foreach (var item in items)
            if (!revealed.Contains(item) && ItemFootprintAllClear(item, view))
                revealed.Add(item);

        view.RevealedItemCount = revealed.Count;
        foreach (var state in view.Cells.Values)
            state.ItemRevealed = state.Item != null && revealed.Contains(state.Item);

        return view;
    }

    /// <summary>
    /// 道具覆盖的每一格是不是都已经没雾了。多格道具（2x1 / 2x2 …）只有在这个条件成立时
    /// 才会被游戏 RevealItem —— 也就是说「已经露给玩家看了」。
    /// </summary>
    private static bool ItemFootprintAllClear(object item, DivinationView view)
    {
        if (item == null || view.Cells.Count == 0) return false;
        var (px, py) = ReadVec2I(ReadMemberAny(item, "Position"));
        var (sx, sy) = ReadVec2I(ReadMemberAny(item, "Size"));
        if (sx <= 0 || sy <= 0) return false;
        for (var dx = 0; dx < sx; dx++)
        {
            for (var dy = 0; dy < sy; dy++)
            {
                if (!view.Cells.TryGetValue(DivinationView.Key(px + dx, py + dy), out var state)) return false;
                if (state.Hidden != false) return false;   // 雾还在 / 读不到 → 不算露出来
            }
        }
        return true;
    }

    /// <summary>读取占卜界面上的格子与工具按钮（主线程）。</summary>
    private static Dictionary<string, object> DescribeDivinationPending()
    {
        var screen = FindCrystalSphereScreen();
        if (screen == null)
        {
            DropDivinationPending("不在占卜界面");
            return null;
        }

        var view = ReadDivinationView(screen);
        ProbeDivinationOnce(view);

        // 界面还在树上 ≠ 还能占卜：次数用完 / 小游戏结束后，水晶球界面不会立刻消失。
        // 这时再发请求，agent 会点一堆「点了没反应」的格子，还以为是成功的。
        // （DivinationCount 是 ClearCell 里最先减的那个数，IsFinished 就是 DivinationCount == 0。）
        if (view.Minigame != null && (view.Finished || view.DivinationsLeft == 0))
        {
            DropDivinationPending(
                $"占卜已结束（finished={view.Finished} 剩余次数={(view.DivinationsLeft?.ToString() ?? "?")}）");
            return null;
        }

        var cells = new List<Candidate>();
        var tools = new List<Candidate>();
        try
        {
            var stack = new Stack<Node>();
            stack.Push(screen);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 20000)
            {
                var node = stack.Pop();
                var full = node.GetType().FullName ?? "";
                if (full == DivinationCellTypeName && node is NClickableControl cellButton)
                {
                    var entity = ReadFieldRaw(node, "Entity") ?? ReadMemberAny(node, "Entity");
                    var (x, y) = DivinationEntityCoords(entity, view);
                    var state = view.Cells.TryGetValue(DivinationView.Key(x, y), out var st) ? st : null;

                    // 迷雾状态：模型字段 → 模型属性 → 节点自己的 MouseFilter（_Ready/EntityClicked 会同步）。
                    bool? hidden;
                    string source;
                    if (state != null)
                    {
                        hidden = state.Hidden;
                        source = state.HiddenSource;
                    }
                    else
                    {
                        hidden = ReadCellHidden(entity, out source);
                    }
                    if (hidden == null)
                    {
                        hidden = ReadCellHiddenFromNode(cellButton);
                        if (hidden != null) source = "node";
                    }

                    // 道具：模型上「揭示前就有值」，所以**只能**在确认已揭示之后才输出类型。
                    // 拿不到小游戏实例就判断不了「道具露没露」（多格道具要全清才露），
                    // 这时一律不给 contentType —— 宁可不给，也不剧透。
                    var item = state != null ? state.Item : null;
                    var shown = state != null && state.ItemRevealed;
                    var contentType = hidden == false && shown ? DivinationContentType(item) : null;

                    string detail;
                    if (hidden == true) detail = "未揭示";
                    else if (hidden == false)
                        detail = contentType == null ? "已揭示：空" : "已揭示：" + DivinationContentLabel(contentType);
                    else detail = "状态未知";

                    cells.Add(new Candidate
                    {
                        Key = $"格{x}-{y}#1",
                        Title = $"格 {x}-{y}",
                        Detail = detail,
                        DivinationButton = cellButton,
                        DivinationRevealed = hidden.HasValue ? hidden == false : null,
                        DivinationContentType = contentType,
                        Enabled = true,
                        OptionId = $"{x},{y}",
                    });
                }
                else if (full == "MegaCrit.Sts2.Core.Nodes.Events.NDivinationButton"
                         && node is NClickableControl divButton)
                {
                    var label = ScreenApi.Clean(DivinationTextOf(node)).Trim();
                    if (!string.IsNullOrEmpty(label) && label.Contains("占卜"))
                    {
                        tools.Add(new Candidate
                        {
                            Key = label + "#1",
                            Title = label,
                            Detail = "切换占卜工具（不消耗次数）",
                            DivinationButton = divButton,
                            DivinationIsTool = true,
                            Enabled = true,
                        });
                    }
                }
                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch (Exception e) { BridgeLog.Warn("divination: 读格子失败：" + e.Message); }

        if (cells.Count == 0)
        {
            DropDivinationPending("占卜界面上没有可点的格子");
            return null;
        }

        var signature = "div:" + string.Join(",", tools.Select(t => t.Key)) +
                        "|" + string.Join(",", cells.Select(c => c.Key + ":" + c.Detail)) +
                        "|left:" + (view.DivinationsLeft?.ToString() ?? "?");

        if (_divinationPending != null)
        {
            _divinationPending.DivinationLeft = view.DivinationsLeft;
            if (_divinationPending.Signature == signature)
                return DescribePending(_divinationPending);
        }

        var all = new List<Candidate>();
        all.AddRange(tools);
        all.AddRange(cells);

        _divinationPending = new PendingSelection
        {
            Id = NextId(),
            Kind = "divination",
            Caller = "NCrystalSphereScreen",
            Min = 1,
            Max = 1,

            Prompt = "水晶球：选择要占卜的格子（开局可选大幅/小幅占卜切换工具）",
            Signature = signature,
            Screen = screen,
            DivinationScreen = screen,
            DivinationKind = "grid",
            DivinationLeft = view.DivinationsLeft,
            Cards = all,
        };
        BridgeLog.Info($"divination: 新请求 id={_divinationPending.Id} cells={cells.Count} tools={tools.Count} " +
                       $"revealed={cells.Count(c => c.DivinationRevealed == true)} " +
                       $"left={view.DivinationsLeft?.ToString() ?? "?"} minigame={view.MinigameSource}");
        return DescribePending(_divinationPending);
    }

    /// <summary>
    /// 每块屏幕只打一条状态探针：把三路读法（模型字段 / 模型属性 / 节点侧）都摊开写进日志。
    /// 下次对着 agent_bridge.log 一眼就能看出到底哪一路是通的、板子处于什么状态。
    /// </summary>
    private static void ProbeDivinationOnce(DivinationView view)
    {
        try
        {
            var screen = view.Screen;
            if (screen == null) return;
            var id = screen.GetInstanceId();
            if (_divinationProbed.Count > 8) _divinationProbed.Clear();
            if (!_divinationProbed.Add(id)) return;

            var hidden = 0;
            var clear = 0;
            var unknown = 0;
            var withItem = 0;
            foreach (var st in view.Cells.Values)
            {
                if (st.Hidden == true) hidden++;
                else if (st.Hidden == false) clear++;
                else unknown++;
                if (st.Item != null) withItem++;
            }

            string sample = "-";
            var first = view.Cells.Values.OrderBy(v => v.X).ThenBy(v => v.Y).FirstOrDefault();
            if (first != null)
            {
                sample = $"[{first.X},{first.Y}] field={DivinationFmt(ReadFieldRaw(first.Cell, "_isHidden"))}" +
                         $" prop={DivinationFmt(ReadMemberAny(first.Cell, "IsHidden"))}" +
                         $" item={DivinationFmt(first.Item == null ? null : first.Item.GetType().Name)}";
            }

            BridgeLog.Info($"divination: 探针 screen={id} minigame={view.MinigameSource} " +
                           $"grid={view.Width}x{view.Height} hidden={hidden} clear={clear} unknown={unknown} " +
                           $"items={view.ItemCount} withItem={withItem} revealedItems={view.RevealedItemCount} " +
                           $"left={view.DivinationsLeft?.ToString() ?? "?"} finished={view.Finished} sample={sample}");
        }
        catch { }
    }

    private static string DivinationFmt(object value) => value == null ? "null" : value.ToString();

    private static void DropDivinationPending(string why)
    {
        if (_divinationPending == null) return;
        BridgeLog.Info("divination: 丢弃请求 — " + why);
        _divinationPending = null;
    }

    private static async Task<Dictionary<string, object>> AnswerDivination(PendingSelection pending, string[] picks)
    {
        var sw = Stopwatch.StartNew();
        var failure = Validate(pending.Min, pending.Max, pending.Cards, picks, out var chosen, "格");
        if (failure != null) return failure;

        var pick = chosen[0];
        if (pick.DivinationButton == null || !GodotObject.IsInstanceValid(pick.DivinationButton))
            return Fail($"「{pick.Key}」对应的控件已经不在了（重新 GET /request）");

        var ok = await BridgePump.OnMain(() =>
        {
            try
            {
                pick.DivinationButton.ForceClick();
                return true;
            }
            catch (Exception e)
            {
                BridgeLog.Warn($"divination: 点「{pick.Key}」失败（{e.GetType().Name}）：{e.Message}");
                return false;
            }
        }, 3000);

        if (!ok) return Fail($"点「{pick.Key}」失败");

        await Task.Delay(420);
        var waited = sw.ElapsedMilliseconds;
        _divinationPending = null;

        // 点完之后立刻回读一次剩余次数（小游戏自己的 DivinationCount）。
        // 读不到就不给这个字段 —— 不编数字。纯读，不碰任何游戏逻辑。
        // 走 OnMain：/respond 不在主线程上，扫节点树必须回主线程。
        int? leftAfter = null;
        var revealedAfter = 0;
        try
        {
            await BridgePump.OnMain(() =>
            {
                try
                {
                    var screen = FindCrystalSphereScreen();
                    if (screen == null || !GodotObject.IsInstanceValid(screen)) return true;
                    var view = ReadDivinationView(screen);
                    if (view.Minigame != null) leftAfter = view.DivinationsLeft;
                    revealedAfter = view.Cells.Values.Count(v => v.Hidden == false);
                }
                catch { }
                return true;
            }, 2000);
        }
        catch { }

        BridgeLog.Info($"divination answered id={pending.Id} pick={pick.Key} waitedMs={waited} " +
                       $"left={(leftAfter?.ToString() ?? "?")} clearCells={revealedAfter}");

        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["source"] = "screen",
            ["route"] = "divination",
            ["applied"] = pick.Key,
            ["how"] = pick.DivinationIsTool ? "divination-tool" : "divination-cell",
            ["itemType"] = pick.DivinationIsTool ? "Tool" : "Cell",
            ["animated"] = false,
            ["waitedMs"] = waited,
            ["note"] = "占卜次数和揭示结果以游戏为准；继续 GET /request 看还剩几次",
        };
        if (leftAfter.HasValue) result["divinationsLeft"] = leftAfter.Value;
        return result;
    }

    /// <summary>
    /// 拿到水晶球小游戏的实例（<c>CrystalSphereMinigame</c>）：
    /// 1) 屏幕自己的 <c>_entity</c> 字段（<c>NCrystalSphereScreen.ShowScreen</c> 里 Instantiate 之后
    ///    直接赋给 <c>_entity</c>，就是这块界面的唯一实例）；
    /// 2) 屏幕上任意同类型字段；
    /// 3) 本地事件模型里挂着的那份。
    /// 拿到它才能读 <c>DivinationCount</c>（剩余次数）/ <c>IsFinished</c> / <c>cells</c> / <c>_revealed</c>。
    /// </summary>
    private static object FindCrystalSphereMinigame(Node screen, out string how)
    {
        how = "none";
        if (screen == null) return null;

        var owned = ReadFieldRaw(screen, "_entity");
        if (owned != null && owned.GetType().Name.Contains("CrystalSphereMinigame"))
        {
            how = "_entity";
            return owned;
        }

        var byType = FindFieldOfTypeName(screen, "CrystalSphereMinigame", out var fieldName);
        if (byType != null)
        {
            how = "screen.field:" + fieldName;
            return byType;
        }

        try
        {
            var ev = RunManager.Instance?.EventSynchronizer?.GetLocalEvent();
            var fromEvent = FindFieldOfTypeName(ev, "CrystalSphereMinigame", out var eventField);
            if (fromEvent != null)
            {
                how = "event.field:" + eventField;
                return fromEvent;
            }
        }
        catch { }
        return null;
    }

    /// <summary>沿继承链找第一个「类型名包含 fragment」的实例字段值；顺便带出字段名（诊断用）。</summary>
    private static object FindFieldOfTypeName(object target, string fragment, out string fieldName)
    {
        fieldName = null;
        if (target == null) return null;
        const System.Reflection.BindingFlags F =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;
        try
        {
            for (var t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(F))
                {
                    if (field.FieldType.Name.Contains(fragment))
                    {
                        var value = field.GetValue(target);
                        if (value != null)
                        {
                            fieldName = t.Name + "." + field.Name;
                            return value;
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>读控件的可见文本（Label / RichTextLabel）；ScreenApi.TextOf 是私有的。</summary>
    private static string DivinationTextOf(Node node)
    {
        try
        {
            if (node is Label l) return l.Text ?? "";
            if (node is RichTextLabel r) return r.Text ?? "";
            foreach (var child in node.GetChildren())
            {
                var text = DivinationTextOf(child);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// 按字段名直读（含编译器生成的 <c>&lt;Name&gt;k__BackingField</c>），沿继承链找。
    /// 属性 getter 在 Godot 派生的节点上偶尔会走生成代码，字段直读是最后一道保险。
    /// </summary>
    private static object ReadFieldRaw(object target, string name)
    {
        if (target == null || string.IsNullOrEmpty(name)) return null;
        const System.Reflection.BindingFlags F =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;
        try
        {
            for (var t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var field = t.GetField(name, F) ?? t.GetField("<" + name + ">k__BackingField", F);
                if (field != null) return field.GetValue(target);
            }
        }
        catch { }
        return null;
    }

    /// <summary>读对象上第一个拿得到的 int 成员（字段优先，其次属性）。</summary>
    private static int? ReadIntAny(object target, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadFieldRaw(target, name) ?? ReadMemberAny(target, name);
            if (value is int i) return i;
            if (value == null || value is bool) continue;
            try { return Convert.ToInt32(value); } catch { }
        }
        return null;
    }

    /// <summary>读对象上第一个拿得到的 bool 成员（字段优先，其次属性）。</summary>
    private static bool? ReadBoolAny(object target, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadFieldRaw(target, name) ?? ReadMemberAny(target, name);
            if (value is bool b) return b;
        }
        return null;
    }

    /// <summary>
    /// 一个 <c>CrystalSphereCell</c> 的迷雾状态。真值只有一个：私有字段 <c>_isHidden</c>
    /// （<c>ClearCell</c> 里 <c>cells[x,y].IsHidden = false</c> 就是「揭开了」）。
    /// 读法：字段直读 → 属性 <c>IsHidden</c> → null（交给节点侧兜底）。
    /// </summary>
    private static bool? ReadCellHidden(object cell, out string source)
    {
        source = "none";
        if (cell == null) return null;

        if (ReadFieldRaw(cell, "_isHidden") is bool byField)
        {
            source = "field";
            return byField;
        }
        if (ReadMemberAny(cell, "IsHidden") is bool byProp)
        {
            source = "prop";
            return byProp;
        }
        return null;
    }

    /// <summary>
    /// 一个格子压着的道具（<c>CrystalSphereCell.Item</c>，走编译器生成的
    /// <c>&lt;Item&gt;k__BackingField</c>）。**注意：揭示之前就有值，别直接发给玩家。**
    /// </summary>
    private static object ReadCellItem(object cell)
    {
        if (cell == null) return null;
        return ReadFieldRaw(cell, "Item") ?? ReadMemberAny(cell, "Item");
    }

    /// <summary>
    /// 节点侧兜底：<c>NCrystalSphereCell</c> 在 <c>_Ready</c> 和 <c>EntityClicked</c> 里
    /// 按迷雾状态设置 <c>MouseFilter</c>（隐藏 → Stop(0)，已揭示 → Ignore(2) 不可点）。
    /// 模型侧整个读不到时，用这个反推。
    /// </summary>
    private static bool? ReadCellHiddenFromNode(Control node)
    {
        if (node == null) return null;
        try
        {
            var filter = (int)node.MouseFilter;
            if (filter == (int)Control.MouseFilterEnum.Ignore) return false;   // 已揭示：点不动了
            if (filter == (int)Control.MouseFilterEnum.Stop) return true;      // 还盖着雾：可以点
        }
        catch { }
        return null;
    }

    /// <summary>读一个 List 字段（<c>_items</c> / <c>_revealed</c>）。</summary>
    private static IEnumerable ReadObjectList(object target, string field)
    {
        if (target == null) return Array.Empty<object>();
        return ReadFieldRaw(target, field) is IEnumerable list && list is not string
            ? list
            : Array.Empty<object>();
    }

    /// <summary>读 Godot 的 <c>Vector2I</c>（结构体，走反射拿 X / Y，不依赖调用方类型）。</summary>
    private static (int X, int Y) ReadVec2I(object vec)
    {
        var x = vec == null ? null : ReadMemberAny(vec, "X");
        var y = vec == null ? null : ReadMemberAny(vec, "Y");
        return (x is int a ? a : 0, y is int b ? b : 0);
    }

    /// <summary>
    /// 格子的坐标：模型上的 <c>X</c> / <c>Y</c>；读不到就用引用去小游戏的 cells 表里反查。
    /// </summary>
    private static (int X, int Y) DivinationEntityCoords(object entity, DivinationView view)
    {
        var x = entity == null ? null : ReadMemberAny(entity, "X");
        var y = entity == null ? null : ReadMemberAny(entity, "Y");
        if (x is int ix && y is int iy) return (ix, iy);

        if (entity != null)
        {
            foreach (var state in view.Cells.Values)
                if (ReferenceEquals(state.Cell, entity)) return (state.X, state.Y);
        }
        return (0, 0);
    }

    /// <summary>道具类型 → contentType。认不得就退化成类型名去掉前缀。</summary>
    private static string DivinationContentType(object item)
    {
        if (item == null) return null;
        var name = item.GetType().Name;
        switch (name)
        {
            case "CrystalSphereRelic": return "Relic";
            case "CrystalSphereGold": return "Gold";
            case "CrystalSpherePotion": return "Potion";
            case "CrystalSphereCardReward": return "Card";
            case "CrystalSphereCurse": return "Curse";
        }
        return name.StartsWith("CrystalSphere", StringComparison.Ordinal)
            ? name.Substring("CrystalSphere".Length)
            : name;
    }

    /// <summary>contentType → detail 里的中文（遗物 / 金币 / 药水 / 卡牌 / 诅咒）。</summary>
    private static string DivinationContentLabel(string contentType) => contentType switch
    {
        "Relic" => "遗物",
        "Gold" => "金币",
        "Potion" => "药水",
        "Card" => "卡牌",
        "Curse" => "诅咒",
        _ => contentType,
    };

    /// <summary>读对象的任意成员（公有/私有都试），字段优先、其次属性，沿继承链找。</summary>
    private static object ReadMemberAny(object target, string name)
    {
        if (target == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            var byField = ReadFieldRaw(target, name);
            if (byField != null) return byField;

            const System.Reflection.BindingFlags F =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            for (var t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var property = t.GetProperty(name, F);
                if (property == null || property.GetIndexParameters().Length != 0) continue;
                return property.GetValue(target);
            }
        }
        catch { }
        return null;
    }
}
