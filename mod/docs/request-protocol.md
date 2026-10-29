# 杀戮尖塔 2 语义请求/应答协议 —— 调研与设计建议

> 目标：把 mod 网桥从「列出可点控件 + 模拟点击」改造成「结构化 request + picks 应答」。
> 本文只做调研与设计，不含实现。所有结论都标注了出处类型名/成员名；查不到的写「未确认」。
>
> 数据来源：
> - `sts2agent\meta\types.txt`（类型名 + 基类 + 特性）
> - `sts2agent\meta\members.txt`（成员签名，以 `== 完整类型名` 分节）
> - `sts2agent\mod\Choices.cs`、`mod\ScreenApi.cs`（现有实现，用于对照现状）

---

## 0. 三个最重要的发现（先说结论）

### 0.1 卡牌选择有唯一的数据层收口：`CardSelectCmd`

`MegaCrit.Sts2.Core.Commands.CardSelectCmd` 是**游戏自己**做卡牌选择的统一入口，所有卡牌选择场景都从这里下去，再由它去创建对应界面节点。它的公开成员（`members.txt` L36081-36103）：

| 方法 | 用途 |
|---|---|
| `FromHand(ctx, player, prefs, filter, source)` | 从手牌选（多选） |
| `FromHandForDiscard(ctx, player, prefs, filter, source)` | 从手牌选弃牌 |
| `FromHandForUpgrade(ctx, player, source)` | 从手牌选升级牌（战斗内，如「武装」） |
| `FromDeckForUpgrade(player, prefs)` | 从牌组选升级 |
| `FromDeckForTransformation(player, prefs, transform)` | 从牌组选变形 |
| `FromDeckForEnchantment(player, enchantment, amount, prefs)`（3 个重载） | 从牌组选附魔 |
| `FromDeckForRemoval(player, prefs, filter)` | 从牌组移除 |
| `FromDeckGeneric(player, prefs, filter, indexOf)` | 通用牌组选 |
| `FromCombatPile(ctx, pile, player, prefs[, filter])`（2 个重载） | 从战斗中的牌堆选（抽/弃/耗/出牌堆） |
| `FromSimpleGrid(ctx, cards, player, prefs)` | 简单网格选（牌堆浏览式） |
| `FromSimpleGridForRewards(ctx, creationResults, player, prefs)` | 奖励牌网格选 |
| `FromChooseACardScreen(ctx, cards, player, canSkip)` | 「多选一」屏 |
| `FromChooseABundleScreen(player, bundles)` | 「选一捆」屏 |

另外它还有**可插拔的 selector**（同节）：

```
prop  MegaCrit.Sts2.Core.TestSupport.ICardSelector Selector
method public static System.IDisposable UseSelector(ICardSelector)
method public static System.IDisposable PushSelector(ICardSelector)
method public static System.IDisposable SuspendSelectorForTest()
method public static void Reset()
```

`MegaCrit.Sts2.Core.TestSupport.ICardSelector` 只有两个方法：

```
method public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> cards, int min, int max)
method public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult>, IReadOnlyList<CardRewardAlternative>)
```

**这意味着：如果我们实现一个 `ICardSelector`，用 `CardSelectCmd.UseSelector(...)` 挂上去，就可以在「不点任何 UI」的情况下拿到 (候选卡, minSelect, maxSelect)，把 request 推给 AI，等 picks 回来再返回结果。** 这是整个协议里性价比最高的一刀，一次覆盖 §1.1 全部 10 个场景。

> ⚠️ 注意 `ICardSelector.GetSelectedCards` 的签名里**没有 prompt、没有 Prefs**，只有 `min` / `max`。所以走这条路拿不到 `CardSelectorPrefs.Prompt` / `UpgradeSelectionPrompt` 这些提示语，也拿不到 `Cancelable` / `RequireManualConfirmation`。要拿这些必须改从 `CardSelectCmd` 各方法的 `prefs` 参数或界面上截（见 §4 风险 R2）。

### 0.2 五个「房间级」决策点各有独立模型，候选集合都能直接读

| 房间 | 模型 | 候选集合 |
|---|---|---|
| 事件 | `MegaCrit.Sts2.Core.Models.EventModel` | `EventModel.CurrentOptions : IReadOnlyList<EventOption>` |
| 营火 | `MegaCrit.Sts2.Core.Rooms.RestSiteRoom` | `RestSiteRoom.Options : IReadOnlyList<RestSiteOption>` |
| 商店 | `MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory` | `CharacterCardEntries` / `ColorlessCardEntries` / `RelicEntries` / `PotionEntries` / `CardRemovalEntry` / `AllEntries` |
| 地图 | `MegaCrit.Sts2.Core.Map.ActMap` + `MapPoint` | `MegaCrit.Sts2.Core.Map.MapTravel.GetTravelablePointsFrom(IRunState, MapPoint)` |
| 宝箱 | `MegaCrit.Sts2.Core.Rooms.TreasureRoom` | 只能从 `TreasureRoom.DoNormalRewards()` 走奖励链（候选集合的公开读取方式 **未确认**） |

### 0.3 游戏自己就带一份「决策点清单」：`MegaCrit.Sts2.Core.AutoSlay.Handlers`

官方内置的 AutoSlay 把需要玩家决策的地方切成了 handler，可以直接当 checklist 对照（`types.txt` L3364-3394）：

- Screens：`CardRewardScreenHandler` / `ChooseABundleScreenHandler` / `ChooseACardScreenHandler` / `ChooseARelicScreenHandler` / `CrystalSphereScreenHandler` / `DeckCardSelectScreenHandler` / `DeckEnchantScreenHandler` / `DeckTransformScreenHandler` / `DeckUpgradeScreenHandler` / `GameOverScreenHandler` / `MapScreenHandler` / `RewardsScreenHandler` / `SimpleCardSelectScreenHandler`
- Rooms：`CombatRoomHandler` / `EventRoomHandler` / `RestSiteRoomHandler` / `ShopRoomHandler` / `TreasureRoomHandler` / `VictoryRoomHandler`

> ⚠️ 里面**没有** `CombatPileCardSelectScreen` / 手牌选择 / 药水选目标 的 handler —— 这三块是 AutoSlay 的盲区，也恰好是我们最容易漏的地方。

---

## 1. 决策点清单

一共 9 大类、23 个具体场景。按「共用哪套界面代码」分组。

### 1.1 A 类：卡牌选择族（共用 `NCardGridSelectionScreen` 基类）

基类：`MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardGridSelectionScreen`
- 接口 `MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.ICardSelector`：
  `Task<IEnumerable<CardModel>> CardsSelected()`
- 基类自身成员只有：`ScreenType`、`PeekButtonTargets`、`UseSharedBackstop`、`DefaultFocusedControl`、`FocusedControlFromTopBar`（`members.txt` L9460-9476）
- **注意：基类和子类都没有公开的 `Prefs` 属性** —— 全库 grep `Prefs` 只命中 `CardSelectCmd` / 各屏幕的 `Create(...)` / `ShowScreen(...)` 参数以及 `CardSelectCmd` 私有字段（`members.txt` L52198、L57425、L74244+）。这是 R2 风险的来源。

`CardSelectorPrefs`（`MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs`，`members.txt` L36832-36870）是全部 A 类场景的约束载体：

```
prop  LocString Prompt / UpgradeSelectionPrompt / RemoveSelectionPrompt / TransformSelectionPrompt
             / ExhaustSelectionPrompt / EnchantSelectionPrompt / DiscardSelectionPrompt
prop  int  MinSelect
prop  int  MaxSelect
prop  bool RequireManualConfirmation
prop  bool Cancelable
prop  Comparison<CardModel> Comparison
prop  bool UnpoweredPreviews / PretendCardsCanBePlayed
prop  Func<CardModel,bool> ShouldGlowGold
ctor  .ctor(LocString prompt, int maxSelect)  /  .ctor(LocString prompt, int minSelect, int maxSelect)
```

> `Prompt` 是**静态** getter（`get_Prompt()` 是 static，和 `get_UpgradeSelectionPrompt()` 并列），而 `MinSelect`/`MaxSelect` 是实例的（有实例 `get_MinSelect()`）。

> ⚠️ 下表中「候选过滤条件」一列（如 `IsUpgradable` / `IsRemovable` / `IsTransformable`）是**由 `CardSelectCmd+<>c` 里的 lambda 字段反推的推断**（例如 `<>9__18_0 : Func<CardModel,bool>` 属于 `FromDeckForUpgrade`），没有逐行看过过滤逻辑体。`CardModel.IsUpgradable` / `IsRemovable` / `IsTransformable` 这三个属性本身是确认存在的（`members.txt` L15013-15019）。协议实现时不要依赖过滤条件，直接吃 `CardSelectCmd` 传下来的候选列表。

| # | 场景 | 界面节点 | 数据模型 / 候选来源 | 玩家入口 | 合法性约束 |
|---|---|---|---|---|---|
| A1 | 升级一张牌（营火「锻造」、事件、遗物） | `NDeckUpgradeSelectScreen`（`: NCardGridSelectionScreen`） | `CardSelectCmd.FromDeckForUpgrade(player, prefs)`；候选 = `Player.Deck`（`CardPile`），过滤 `CardModel.IsUpgradable`（`FromDeckForUpgrade` 内部的 `<>9__18_0` 过滤器） | 静态工厂 `NDeckUpgradeSelectScreen.ShowScreen(IReadOnlyList<CardModel>, CardSelectorPrefs, IRunState)`；取回走 `CardsSelected()` | 张数 = `Prefs.MinSelect..MaxSelect`（升级通常 1）；`RequireManualConfirmation` 决定是否需要再按确认；营火`SmithRestSiteOption.SmithCount` 可让一次升级多张 |
| A2 | **战斗内**从手牌选升级（如「武装」类牌） | ⚠️ **没有独立节点**，见 R1 | `CardSelectCmd.FromHandForUpgrade(ctx, player, source)` → 落到 `NPlayerHand.SelectCards(prefs, filter, source, NPlayerHand.Mode.UpgradeSelect)` | `NPlayerHand.SelectCards(...)`（`members.txt` L11877）；界面状态 `NPlayerHand.IsInCardSelection == true`、`CurrentMode == UpgradeSelect` | 1 张；`Mode` 枚举值 `None / Play / SimpleSelect / UpgradeSelect`（L57320-57325） |
| A3 | 移除一张牌（商店移除服务、事件、遗物） | `NDeckCardSelectScreen`（`: NCardGridSelectionScreen`） | `CardSelectCmd.FromDeckForRemoval(player, prefs, filter)`；候选 = `Player.Deck`，过滤 `CardModel.IsRemovable` | 静态 `NDeckCardSelectScreen.Create(IReadOnlyList<CardModel>, CardSelectorPrefs)` + `CardsSelected()` | 1 张；`Cancelable` 可为 true；移除后商店侧 `NMerchantInventory.OnCardRemovalUsed()`、`MerchantCardRemovalEntry.Used = true`（有 `SetUsed()`） |
| A4 | 变形一张牌 | `NDeckTransformSelectScreen` | `CardSelectCmd.FromDeckForTransformation(player, prefs, Func<CardModel,CardTransformation>)`；候选过滤 `CardModel.IsTransformable` | `NDeckTransformSelectScreen.ShowScreen(cards, transformFn, prefs)` | 张数由 prefs；变形结果由 `CardTransformation` / `CardFactory.CreateRandomCardForTransform` 决定，**不由玩家选** |
| A5 | 附魔一张牌 | `NDeckEnchantSelectScreen` | `CardSelectCmd.FromDeckForEnchantment(player, EnchantmentModel, int amount, prefs[, filter])` | `NDeckEnchantSelectScreen.ShowScreen(cards, EnchantmentModel, int amount, prefs)`；另有 `UseSingleSelection` 属性 | 张数 = prefs / `amount`；附魔对象 `CardModel.Enchantment` |
| A6 | 通用网格选牌（「从牌堆/牌组挑一张」，牌堆浏览式） | `NSimpleCardSelectScreen`（`: NCardGridSelectionScreen`） | `CardSelectCmd.FromSimpleGrid(...)` / `FromSimpleGridForRewards(...)`；两个 `Create` 重载分别吃 `IReadOnlyList<CardModel>` 和 `IReadOnlyList<CardCreationResult>` | `NSimpleCardSelectScreen.Create(cards, prefs)` + `CardsSelected()` | `MinSelect`/`MaxSelect`；`Cancelable`；有 `FlashRelicsOnModifiedCards` 协程 |
| A7 | 从战斗中某个牌堆选牌（弃牌/消耗/回手/放回抽牌堆顶等） | `NCombatPileCardSelectScreen`（`: NCardGridSelectionScreen`） | `CardSelectCmd.FromCombatPile(ctx, CardPile pile, player, prefs[, filter])`；`CardPile` 由 `PlayerCombatState.Hand/DrawPile/DiscardPile/ExhaustPile/PlayPile` 或 `CardPile.Get(PileType, Player)` 取；`PileType` = `None/Draw/Hand/Discard/Exhaust/Play/Deck` | 静态 `NCombatPileCardSelectScreen.Create(CardPile, CardSelectorPrefs, Func<CardModel,bool>)` | 张数 prefs；「洗牌相关」的选择（把某张牌放到抽牌堆顶之类）也走这里，`PileType.Draw` |
| A8 | 三选一（「选择一张牌」多选一屏） | `NChooseACardSelectionScreen` | `CardSelectCmd.FromChooseACardScreen(ctx, cards, player, bool canSkip)`；`canSkip` 直接进 `ShowScreen(cards, canSkip)` | `NChooseACardSelectionScreen.ShowScreen(IReadOnlyList<CardModel>, bool canSkip)` + `CardsSelected()` | 恰好 1 张；`canSkip` 决定能不能空手走 |
| A9 | 选一捆（bundle） | `NChooseABundleSelectionScreen` | `CardSelectCmd.FromChooseABundleScreen(player, IReadOnlyList<IReadOnlyList<CardModel>> bundles)` | `NChooseABundleSelectionScreen.ShowScreen(bundles)`；返回 `Task<IEnumerable<IReadOnlyList<CardModel>>>` | 恰好选 1 捆；**picks 的语义是「捆」而不是「卡」** |
| A10 | 从手牌选（多选 / 弃牌） | ⚠️ **没有独立节点**，见 R1 | `CardSelectCmd.FromHand(ctx, player, prefs, filter, source)` / `FromHandForDiscard(...)` → `NPlayerHand.SelectCards(prefs, filter, source, NPlayerHand.Mode.SimpleSelect)` | `NPlayerHand.SelectCards(...)`；`NPlayerHand.IsInCardSelection`、`CurrentMode == SimpleSelect` | 张数 prefs；`Cancelable` 影响能否取消；`NPlayerHand_` 的 `SelectModeGoldGlowOverride` 会给可选牌加金色高亮 |

### 1.2 B 类：奖励族（`NRewardsScreen` 及其子奖励）

`NRewardsScreen`（`members.txt` L7326-7356）：
```
prop  bool IsComplete
prop  MegaCrit.Sts2.Core.Entities.Multiplayer.NetScreenType ScreenType
event Completed
static ShowScreen(RewardsSet, bool, IRunState)
method RewardCollectedFrom(Godot.Control) / RewardSkippedFrom(Godot.Control)
```
候选来自 `MegaCrit.Sts2.Core.Rewards.RewardsSet.Rewards : List<Reward>`（L5195-5221），每个 `Reward` 有 `RewardType` / `Description` / `IsPopulated` / `SuccessfullySelected` / `SelectUnsynchronized() : Task<bool>` / `OnSkipped()`。
`RewardType` = `None/Card/Gold/Potion/Relic/RemoveCard/SpecialCard`（L5223-5231）。
界面节点 `MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton`：`prop Reward Reward`、`Create(Reward, NRewardsScreen)`、事件 `RewardClaimed` / `RewardSkipped`。

| # | 场景 | 界面节点 | 数据模型 / 候选来源 | 玩家入口 | 约束 |
|---|---|---|---|---|---|
| B1 | 战斗/事件后奖励列表（领金币、领药水、领遗物、跳过） | `NRewardsScreen` + `NRewardButton`（`NLinkedRewardSet` 处理成组的奖励） | `RewardsSet.Rewards`；分组用 `LinkedRewardSet.Rewards` | `NRewardButton.Create(reward, screen)` 的点击 → `Reward.SelectUnsynchronized()` | 可跳过（`RewardsSet.DisallowSkipping` 为 false 时）；`RewardsSet.AllRewardsSuccessfullySelected` 表示收完 |
| B2 | 三张奖励牌选一张（含跳过 / 重 roll / 替代选项） | `NCardRewardSelectionScreen`（**不是** NCardGridSelectionScreen 子类，自己一个） | `CardReward`（`MegaCrit.Sts2.Core.Rewards.CardReward`）：`Cards : IEnumerable<CardModel>`、`OptionCount`、`CanReroll`、`CanSkip`、`Reroll()`、`OnSkipped()`、`Options : CardCreationOptions`；界面侧吃 `CardCreationResult` | 静态 `NCardRewardSelectionScreen.ShowScreen(IReadOnlyList<CardCreationResult>, IReadOnlyList<CardRewardAlternative>)`；答案 `OptionSelected() : Task<int?>`（null = 跳过） | 选 1 张 或 跳过；`CanReroll` 为 true 时可重 roll；`CardRewardAlternative`（`OptionId`/`Title`/`OnSelect`/`AfterSelected`，`Generate(CardReward)`）是「不选牌，改领别的」的额外按钮，节点 `NCardRewardAlternativeButton` |
| B3 | 三选一遗物（BOSS 遗物、事件奖励遗物） | `NChooseARelicSelection`（注意在 `Nodes.Screens` 下，不在 CardSelection 下） | `RelicSelectCmd.FromChooseARelicScreen(player, IReadOnlyList<RelicModel>)`；候选 = 传入的 `RelicModel` 列表 | 静态 `NChooseARelicSelection.ShowScreen(IReadOnlyList<RelicModel>)` + `RelicsSelected() : Task<IEnumerable<RelicModel>>` | 通常选 1；`RelicModel.Id`（`AbstractModel.Id : ModelId`）可作 key |

> `NChooseARelicSelection` 的 `ScreenType` 是 `NetScreenType.SharedRelicPicking`（枚举里有这个值，L34182）—— 可以用它把「选遗物」和「选牌」区分开。

### 1.3 C 类：房间选项族

| # | 场景 | 界面节点 | 数据模型 / 候选来源 | 玩家入口 | 约束 |
|---|---|---|---|---|---|
| C1 | 事件选项（普通事件 / 古代事件 / 神龛等） | `MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom` + `MegaCrit.Sts2.Core.Nodes.Events.NEventOptionButton`（派生布局 `NEventLayout` / `NAncientEventLayout` / `NCombatEventLayout`） | `EventModel.CurrentOptions : IReadOnlyList<EventOption>`（注意 `EventOption` 在 `MegaCrit.Sts2.Core.Events` 下，**不是** `Core.Models`）；房间侧 `EventRoom.LocalMutableEvent`，界面侧 `NEventRoom` 的 private `_event` | `NEventRoom.OptionButtonClicked(EventOption, int)`（**public、返回 void**，`index` = `CurrentOptions` 的下标；`IsProceed` 分支直接 `option.Chosen()`；**可直接调用，不用点击**）；推进用静态 `NEventRoom.Proceed()`；按钮节点有 `Event` / `Option` / `Index` 三个属性可反查 | 恰好 1 个选项；`EventOption.IsLocked` 的不可选（`OptionButtonClicked` 对它直接 `return`）；`EventOption.IsProceed` 是「继续/离开」而不是分支；`EventOption.WillKillPlayer` / `ThatWillKillPlayerIf` 表示会致死（**需要单独提示 AI**，别让它误选）；`EventOption.WasChosen` 可判断已选过。**已实现，见 §7.4** |
| C2 | 开局 Neow（及所有 Ancient）奖励 | 事件节点同 C1，布局 `NAncientEventLayout`；对话节点 `NAncientDialogueLine` / `NAncientDialogueHitbox` | `MegaCrit.Sts2.Core.Models.AncientEventModel`（`: EventModel`）：`GeneratedOptions`、`AllPossibleOptions`、`StartPreFinished()`；`Neow` = `MegaCrit.Sts2.Core.Models.Events.Neow`；房间来源 `MegaCrit.Sts2.Core.Rooms.RoomSet.Ancient : AncientEventModel` | 同 C1（走 `CurrentOptions` + `OptionButtonClicked`） | 通常 3~4 选 1；**Ancient 会给遗物/诅咒/最大生命等多种"代价型"选项**，`EventOption.Relic` 指出该选项的遗物；注意古代事件有对话点击前置（`NAncientDialogueLine`），对话本身不是决策 |
| C3 | 营火选择（休息 / 锻造 / 其它 RestSiteOption） | `MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom` + `MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton` | `NRestSiteRoom.Options : IReadOnlyList<RestSiteOption>`（房间侧 `RestSiteRoom.Options` → `RestSiteSynchronizer.GetLocalOptions()`，**进房间时**由 `RestSiteOption.Generate(Player)` + `Hook.ModifyRestSiteOptions` 生成，遗物加成就在这里）；`RestSiteOption.OptionId` / `Title` / `Description` / `IsEnabled` / `OnSelect() : Task<bool>` / 静态 `RestSiteOption.Generate(Player)` | 按钮 `NRestSiteButton.Option` + private `SelectOption(option) : Task`（`OnRelease` 里调的就是它：`DisableOptions` → `RestSiteSynchronizer.ChooseLocalOption(index)` → 成功才 `NRestSiteRoom.AfterSelectingOption(option)`）；房间 `GetButtonForOption(option)` / `AfterSelectingOption(option)` / `DisableOptions()` / `EnableOptions()`；「前进」是 `NRestSiteRoom.ProceedButton`（**不是**营火选项） | 恰好 1 个；**`RestSiteOption.IsEnabled` 基类实现恒为 true**（HP 满时「休息」仍可选、只是回血 0，界面只把它变灰），子类才 override（`SmithRestSiteOption.IsEnabled = Deck.UpgradableCardCount > 0`）；选项大类见 `CloneRestSiteOption` / `CookRestSiteOption` / `DigRestSiteOption` / `HatchRestSiteOption` / `HealRestSiteOption` / `KindleRestSiteOption` / `LiftRestSiteOption` / `MendRestSiteOption` / `SmithRestSiteOption`，`SmithRestSiteOption.SmithCount` 决定升级几张。**已实现，见 §7.3** |
| C4 | 商店购买 | `MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom` + `MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory` + `NMerchantCard` / `NMerchantRelic` / `NMerchantPotion` / `NMerchantCardRemoval`（都 `: NMerchantSlot`） | `MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory`：`CharacterCardEntries` / `ColorlessCardEntries` / `RelicEntries` / `PotionEntries` / `CardRemovalEntry` / `AllEntries` / `CardEntries` / `CreateForNormalMerchant(Player)`；房间侧 `MerchantRoom.Inventories` / `GetLocalInventory()` | 数据层：`MerchantEntry.OnTryPurchaseWrapper(MerchantInventory, bool) : Task<bool>`（卡移除是 3 参重载）；UI 层：`NMerchantSlot.Entry` + **`NMerchantSlot.OnSelected()`（private，鼠标松开的回调；`/respond` 调它，不点坐标，见 §7.2）** + `NMerchantInventory.Open()` / `IsOpen` / `GetAllSlots()` / `OnCardRemovalUsed()` | 购买是**可多次、可 0 次**的决策（不是"必须选一个"）：`min = 0`；约束看 `MerchantEntry.Cost` vs `Player.Gold`（`MerchantEntry.EnoughGold`）、`IsStocked`、`MerchantInventory.CharacterCardEntries` 的 `IsOnSale`；结果码 `PurchaseStatus` = `Success` / `FailureGold` / `FailureSpace` / `FailureForbidden` / `FailureOutOfStock`；买「卡牌移除」后会**额外弹一个 A3 的牌组选牌屏**（选择要删哪张） |
| C5 | 宝箱开启 / 领宝箱遗物（**两阶段**） | `MegaCrit.Sts2.Core.Nodes.Rooms.NTreasureRoom` + `MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicCollection` / `NTreasureRoomRelicHolder` / `NHandImage` / `NHandImageCollection` | **遗物数据源已确认**：`RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics : IReadOnlyList<RelicModel>`（`TreasureRoom.EnterInternal` 末尾 `BeginRelicPicking()` 生成）；房间侧 `TreasureRoom.DoNormalRewards() : Task<int>`（金币）/ `DoExtraRewardsIfNeeded()`；开箱状态 `NTreasureRoom._hasChestBeenOpened` / `_isRelicCollectionOpen`（private bool） | 开箱 `NTreasureRoom.OpenChest()`（private `Task`）；拿遗物 `NTreasureRoomRelicCollection.PickRelic(holder)`（private）→ `TreasureRoomRelicSynchronizer.PickRelicLocally(index)`；跳过 `TreasureRoomRelicSynchronizer.SkipRelicLocally()`（= `PickRelicLocally(null)`，单人要等 `TreasureRoom.Exit → OnRoomExited` 才 `EndRelicVoting`） | 两个决定、两个阶段：① 箱子关着 → 只问「要不要打开」（`picks:[]` = 不打开），**不暴露箱里内容**；② 箱子开了 + `_isRelicCollectionOpen` → 才给出遗物候选（`picks:[]` = 都不拿，走游戏的 `SkipRelicLocally` 后 `/proceed` 离开）。开箱前「前进」按钮是关着的（`OnActiveScreenChanged` 用 `_hasChestBeenOpened` 判）。**已实现，见 §7.5** |
| C6 | 推进 / 跳过（很多房间的「继续」按钮） | `MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton` | `IsSkip : bool`（true 时按钮文字是 `SkipLoc`，否则 `ProceedLoc`） | 点击；`UpdateText(LocString)`、`SetPulseState(bool)` | 不是决策，但**是 request 生命周期的边界**：房间/奖励处理完后要等它出现再推进 |

### 1.4 D 类：地图（选节点）

| 场景 | 界面节点 | 数据模型 / 候选来源 | 玩家入口 | 约束 |
|---|---|---|---|---|
| D1 | 选下一个地图节点（含联机投票） | `MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen` + `NMapPoint`（派生 `NNormalMapPoint` / `NAncientMapPoint` / `NBossMapPoint`），房间 `NMapRoom` | 候选 = `MegaCrit.Sts2.Core.Map.MapTravel.GetTravelablePointsFrom(IRunState, MapPoint)`；每点有 `MapPoint.coord`（`MapCoord{col,row}`）、`PointType`（`Unassigned/Unknown/Shop/Treasure/RestSite/Monster/Elite/Boss/Ancient`）、`Children`；界面侧 `NMapPoint.Point` / `IsTravelable` / `State`（`MapPointState.None/Travelable/Traveled/Untravelable`） | `NMapScreen.OnMapPointSelectedLocally(NMapPoint)`，或直接 `Task NMapScreen.TravelToMapCoord(MapCoord)`（**数据层直达，不需要点击**）；状态 `IsTravelEnabled` / `IsTraveling` / `IsOpen`；联机投票 `PlayerVoteDictionary` | 恰好 1 个、且必须在 available 集合内；`IsTravelEnabled == false` 时不能走；`IsTraveling == true` 时已在动画中，不能重复提交 |

> 现有 `mcp__sts2__sts2_move` 工具就是 `(col,row)` 语义，和 `MapCoord` 完全一致 —— D1 其实已经做完了，协议里只需要把 kind 名字对齐。
>
> **2026-10-23 直播实测修复**：`move` 以前是 fire-and-forget（永远 `ok:true`），现在 `GameApi.MoveAndConfirm`
> 会等 `EnterMapCoord` 这个 Task 并轮询主线程，只有「目标坐标到了 **且** `CurrentRoom` 换成了另一个实例」才回
> `ok:true`。失败回结构化错误 + 完整 `available`（照抄 `NMapScreen.RecalculateTravelability`：visited 为空 →
> 起点节点；最后一行 → Boss；否则 `MapTravel.GetTravelablePointsFrom`）。`EnterMapCoordInternal` 的 NRE 根因是
> `State.Map.GetPoint(coord).PointType` 拿到 null —— 坐标不在本幕地图上（或新一幕地图还没生成）就会炸，而且
> `EnterMapCoord` 会先把脏坐标写进 `_visitedMapCoords`（游戏自己随后会「退回起点节点」兜底）。现在发起前用
> `ActMap.HasPoint` + 可走集合拦住。
> 顺带：`/state` 的 `players[i].handComplete` 与 `/acts` 的 `reason` / `pendingId` / `hint` 也见 README。

### 1.5 E 类：战斗内（出牌 / 目标 / 药水）

| # | 场景 | 界面节点 | 数据模型 / 候选来源 | 玩家入口 | 约束 |
|---|---|---|---|---|---|
| E1 | 出牌 + 选目标 | `NPlayerHand` / `NCardPlay`（`NMouseCardPlay` / `NControllerCardPlay`）/ `NTargetManager` / `NSelectionReticle` | 手牌 = `Player.PlayerCombatState.Hand : CardPile` 或 `CardPile.Get(PileType.Hand, player)`；目标 = `CombatState.HittableEnemies : IReadOnlyList<Creature>`；卡的目标类型 `CardModel.TargetType`（`TargetType.IsSingleTarget(TargetType)` 可判定是否必须给目标） | UI：拖牌（`NMouseCardPlay.Create(NHandCardHolder, ...)`）/ `NTargetManager.StartTargeting(TargetType, Vector2, TargetMode, ...)`；回合结束 `NEndTurnButton` / `PlayerCmd.EndTurn(Player, bool, Func<Task>)` | `PlayerCombatState.HasEnoughResourcesFor(CardModel, ref UnplayableReason)` 判能量/星数是否够；`CardModel.IsPlayable`；`PlayerCombatState.Phase == PlayerTurnPhase.Play` 才是可操作窗口（`PlayerTurnPhase` = `None/Start/AutoPrePlay/Play/AutoPostPlay/End`）；目标必须 ∈ `HittableEnemies`，`TargetType.Self/AllEnemies/RandomEnemy` 不需要目标 |
| E2 | 使用药水（要不要选目标、选谁） | `MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder`（`: NClickableControl`）、`NPotion`、`NPotionContainer`、`NPotionPopup` / `NPotionPopupButton` / `NPotionShortcutButton` | 药水槽 = `Player.PotionSlots : IReadOnlyList<PotionModel>` / `Player.Potions`；`PotionModel.TargetType`（复用 `TargetType`）、`IsValidTarget(Creature) : bool`、`Usage`、`SelectionScreenPrompt`、`IsQueued` | 数据层：`PotionModel.EnqueueManualUse(Creature)`、`OnUseWrapper(ctx, Creature)`、`Discard()`；UI：`NPotionHolder` 内部协程 `UsePotion` / `TargetNode`；丢弃 `PotionCmd.Discard(PotionModel)` | 若 `PotionModel.TargetType.IsSingleTarget()` 则**必须给目标**且 `IsValidTarget(creature)` 为 true；药水槽满时不能拿新药水（`Player.HasOpenPotionSlots`、`Player.MaxPotionCount`）；有 `PotionUsage` 限制（如仅战斗中可用） |
| E3 | 弃牌堆 / 抽牌堆浏览（非决策，但会被误判成决策） | `NDrawPileButton` / `NDiscardPileButton` / `NExhaustPileButton`（都 `: NCombatCardPile`）、`NDeckViewScreen` | `NCombatCardPile.Pile`、`NDeckViewScreen.ShowScreen(Player)` | 点击打开/关闭 | **没有任何选择语义**，协议里必须显式排除，否则 AI 会以为要选牌 |

### 1.6 F 类：特殊小游戏（必须单独协议）

| # | 场景 | 界面节点 | 数据模型 | 玩家入口 | 约束 |
|---|---|---|---|---|---|
| F1 | 水晶球挖矿（CrystalSphere 事件） | `MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen` + `NCrystalSphereCell`（`: NClickableControl`）/ `NCrystalSphereItem` / `NCrystalSphereMask` | `MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereCell`（`X` / `Y` / `IsHidden` / `IsHighlighted` / `Item`）；`CrystalSphereItem`（`Size` / `Position` / `IsGood`） | 点格子（`OnCellClicked` 协程） | 格子坐标而非卡牌；有挖掘次数限制（具体次数 **未确认**，未在 meta 里找到明确成员）。AutoSlay 有 `CrystalSphereScreenHandler` 可参考 |

### 1.7 汇总：23 个场景 → 9 类

1. **A 类 卡牌选择**（A1-A10，10 个）—— 共用 `NCardGridSelectionScreen` 家族 / `CardSelectCmd`
2. **B 类 奖励**（B1-B3，3 个）—— `NRewardsScreen` + `NCardRewardSelectionScreen` + `NChooseARelicSelection`
3. **C1/C2 事件与 Ancient**（2 个）—— `NEventRoom` + `EventModel.CurrentOptions`
4. **C3 营火**（1 个）—— `NRestSiteRoom` + `RestSiteOption`
5. **C4 商店**（1 个）—— `NMerchantInventory` + `MerchantEntry`（内含 1 个嵌套 A3）
6. **C5/C6 宝箱与推进**（2 个）
7. **D 地图**（1 个）—— `NMapScreen` / `MapCoord`
8. **E 战斗**（E1 出牌选目标、E2 药水、E3 排除项，2 个真决策）
9. **F 小游戏**（1 个）

---

## 2. 协议字段设计建议

### 2.1 通用信封

```jsonc
// 推送：需要玩家决策
{
  "id": 1729,                  // 单调递增，同一时刻只允许一个未决 request
  "kind": "card.select",       // 见 2.2 的 kind 表
  "prompt": "选择一张牌升级",   // 人话，来自 Prefs.Prompt / RestSiteOption.Description / EventModel.Description
  "min": 1,
  "max": 1,
  "cancelable": false,         // 空 picks 是否合法
  "candidates": [ { "key": "打击#0", "label": "打击", "index": 0, ... } ],
  "context": { }               // 每 kind 专属的补充字段，见 2.2
}

// 应答：AI 作答
{ "id": 1729, "picks": ["打击#0"] }
// → { "ok": true, "applied": ["打击#0"] }
// → { "ok": false, "error": "duplicate pick '打击#0'" }
```

必备字段理由：

| 字段 | 出处 | 说明 |
|---|---|---|
| `min` / `max` | `CardSelectorPrefs.MinSelect` / `MaxSelect` | 卡牌族直接对应。非卡牌族按语义填（地图/事件/营火恒为 1/1，商店恒为 0/N） |
| `cancelable` | `CardSelectorPrefs.Cancelable` | 决定 `picks: []` 是「取消」还是「非法」。非卡牌族：事件通常不可取消，商店是「买完为止」（`min=0` 且随时可走） |
| `prompt` | `CardSelectorPrefs.UpgradeSelectionPrompt` / `RemoveSelectionPrompt` / `TransformSelectionPrompt` / `EnchantSelectionPrompt` / `ExhaustSelectionPrompt` / `DiscardSelectionPrompt`；`RestSiteOption.Title/Description`；`EventModel.Description`；`PotionModel.SelectionScreenPrompt` | 这七个静态 prompt 正好对应 A1/A3/A4/A5/A7/A10 —— 可以用它反推「当前是哪种卡牌选择」（当拿不到 `Prefs` 时这是唯一线索，见 R2） |
| `id` | 自定义 | **必须**，因为选牌期间游戏是被 `await` 挂住的，旧 request 不能和新的混淆 |

### 2.2 kind 表与候选 key 规范

| kind | 场景 | 候选 key 规范 | 额外字段 |
|---|---|---|---|
| `card.upgrade` | A1/A2 | `牌名#序号`（同名卡按候选顺序编号） | `from`: `"deck"` \| `"hand"`；`count`: 可升级张数（`SmithRestSiteOption.SmithCount`） |
| `card.remove` | A3 | 同上 | `from`: `"deck"`；`shop`: bool（是否来自商店移除服务） |
| `card.transform` | A4 | 同上 | `enchant`/`transform` 的结果不需要玩家选，只填张数 |
| `card.enchant` | A5 | 同上 | `enchantment`: 附魔名（`EnchantmentModel.Title`），`amount` |
| `card.grid` | A6 | 同上 | `source`: `"reward"` \| `"pile"` \| `"deck"` |
| `card.pile` | A7 | 同上 | `pile`: `PileType` 名（`Draw`/`Discard`/`Exhaust`/`Hand`/`Play`） |
| `card.choose` | A8 | 同上 | `canSkip` |
| `card.bundle` | A9 | `捆#序号`，`candidates[i].cards`: 捆内牌名数组 | 只能选 1 捆 |
| `card.hand` | A10 | `牌名#序号`（手牌位置就是顺序） | `mode`: `SimpleSelect` \| `UpgradeSelect` |
| `card.reward` | B2 | `牌名#序号`；跳过用 `picks: []`；替代选项用 `alt:<OptionId>` | `canSkip`、`canReroll`（`CardReward.CanSkip` / `CanReroll`）、`alternatives`: `[{OptionId, Title}]`（`CardRewardAlternative`） |
| `relic.choose` | B3 | `遗物名#序号` | — |
| `reward.claim` | B1 | `reward:<index>`（`RewardsSet.Rewards` 顺序），可多选 | `candidates[i].rewardType`: `RewardType` 名；`skip` 合不合法看 `RewardsSet.DisallowSkipping` |
| `event.option` | C1/C2 | `opt:<EventOption.TextKey>`（`TextKey` 是 string，稳定） | `candidates[i].locked`（`IsLocked`）、`.isProceed`（`IsProceed`）、`.willKillPlayer`（`WillKillPlayer`）、`.relic`（`EventOption.Relic`）；`isAncient`: bool |
| `rest.option` | C3 | `rest:<RestSiteOption.OptionId>` | `candidates[i].enabled`（`IsEnabled`），`.description` |
| `shop` | C4 | `牌名/遗物名/药水名#同标题序号`（与其他候选同一套 `NextKey`；移除服务固定 `卡牌移除#1`） | `candidates[i].price`（`MerchantEntry.Cost`）、`.affordable`（mod 算的 `Player.Gold >= Cost`）、`.itemType`（`Card`/`Relic`/`Potion`/`Service`）、`.detail`；`pending.gold` = `Player.Gold`。已售出的格子（`IsStocked == false`）直接从候选里剔除 |
| `map.travel` | D1 | `map:<col>,<row>`（`MapPoint.coord.col/row`） | `candidates[i].pointType`（`MapPointType`）、`.row`/`.col`；`context.floor`、`context.act` |
| `combat.play` | E1 | `card:<牌名#序号>` + `target:<敌人下标>` | 建议 `picks` 用字符串，或额外 `target` 字段；`context.energy`/`maxEnergy`/`stars`、`candidates` 里每张牌带 `targetType`、`.playable`（`IsPlayable` / `HasEnoughResourcesFor`） |
| `combat.potion` | E2 | `potion:<药水名>#<slot>`，需要目标时配 `target:<敌人下标>` | `candidates[i].targetType`、`.needsTarget`、`.usage` |
| `crystal.cell` | F1 | `cell:<x>,<y>` | 已挖/已翻的格子要从候选里去掉（`IsHidden`） |

**关于卡牌 key 的选择：**

- 用户提的 `牌名#索引` 是对的方向，但要注意：
  - `CardModel.Id` 是 `AbstractModel.Id : ModelId`（`members.txt` L14503），每个模型实例一个。现有 `Choices.cs` 已把它当 `id` 上报，**可作为机器身份**；但它是否是「同一张牌刷新前后不变」——**未确认，需要实测**。
  - 因此建议 candidates 里**两个都给**：`key` 用人类可读的 `牌名#序号`（协议层唯一），`id` 用 `card.Id.ToString()`（排查/去重用）。校验时只认 `key`，`id` 只作为 `context` 附带信息。
  - `牌名#序号` 的序号必须**只在一次 request 内有效**，request 一结束就作废（这正是「节点 uid 每次刷新都变」的正确解药：我们的 key 由候选列表顺序定义，而不是由控件实例定义）。
  - 同名同升级状态要区分：`打击#0` / `打击#1`；升级过的建议写成 `打击+#0`（`CardModel.IsUpgraded` / `CurrentUpgradeLevel`，L15016-15018），避免「升级前/升级后同名」歧义。

### 2.3 校验规则清单（mod 侧必须逐条实现）

1. `id` 必须等于当前未决 request 的 id，否则 `ok:false, error:"stale request <id>"`（防止 AI 答迟到的旧题）。
2. `picks.len` 必须 ∈ `[min, max]`；若 `picks.len == 0`，只有 `cancelable == true`（卡牌族）或 `min == 0`（商店）才合法。
3. 每个 pick 必须**逐字等于**某个 `candidate.key`；不允许「模糊匹配牌名」「大小写无关」等宽容策略 —— 宁可报错让 AI 重答，也不要猜（猜错一张牌就是一手错牌）。
4. `picks` 内部不得重复（`card.hand` 尤其容易重复，因为同名卡多）。
5. 卡牌族额外：选中的牌必须真的在候选集合里（`CardSelectCmd` 会在 `await` 期间继续改状态，比如遗物触发抽牌 —— 提交前再 fetch 一次候选核对）。
6. `combat.play` 额外：目标下标必须 < `CombatState.HittableEnemies.Count`；目标类型不需要目标时禁止传 target；能量不足的单牌可以由 AI 选但必须 `ok:false, error:"not enough energy"`（或提前在 candidate 里标 `playable:false` 让 AI 不选）。
7. `shop.buy` 额外：`Gold >= Cost`，否则返回 `PurchaseStatus` 对应的错误串（用 `MerchantEntry.OnTryPurchaseWrapper` 的 bool 结果 + `PurchaseStatus` 事件）。
8. `RequireManualConfirmation == true` 的场景：**不要**把「确认」暴露成第二个 request。mod 内部在拿到 picks 后自己走确认按钮（现有 `ScreenApi.ConfirmWords` 已经在做这件事），对外仍是一个 request 一个应答。
9. 执行失败（`ok:false`）时，**request 保持未决**，允许 AI 重答；同 id 最多重试 N 次后自动放弃（避免挂死游戏）。

### 2.4 建议的执行路径（不用点击 UI）

能走数据层就别点：

| 场景 | 建议执行方式 | 出处 |
|---|---|---|
| A 类全部 | 安装自定义 `ICardSelector`，通过 `CardSelectCmd.UseSelector` / `PushSelector` 挂入，直接 `TaskCompletionSource` 返回 picks | `CardSelectCmd.Selector` / `UseSelector` / `PushSelector`；`ICardSelector.GetSelectedCards` |
| B2 卡牌奖励 | 同上（`ICardSelector.GetSelectedCardReward` 返回 `CardRewardSelection{card, alternative}`） | `TestSupport.CardRewardSelection` |
| C1/C2 事件 | `NEventRoom.OptionButtonClicked(EventOption, int)`（公开方法） | `members.txt` L9956 |
| D1 地图 | `NMapScreen.TravelToMapCoord(MapCoord)` | L8736 |
| C4 商店 | `MerchantEntry.OnTryPurchaseWrapper(inventory, false)` | L34286 |
| C3 营火 | `RestSiteOption.OnSelect()` 或 `NRestSiteRoom.AfterSelectingOption(option)` | L33666 / L10031 |
| E2 药水 | `PotionModel.EnqueueManualUse(Creature)` | L15978 |
| B1 奖励 | `Reward.SelectUnsynchronized()`（⚠️ 名字带 Unsynchronized，联机下要谨慎，见 R6） | L5189 |

---

## 3. 实现优先级建议

### 批次 1（一次覆盖最多场景）：A 类卡牌选择族

**为什么最先做：** 10 个场景（A1-A10）共用 `NCardGridSelectionScreen` 一个基类和一个 `CardsSelected()` 契约，而且 `CardSelectCmd` + `ICardSelector` 提供了**单一钩子**。做完这一个，游戏里所有「选牌」都不再需要点 UI，也不需要为「升级 / 移除 / 变形 / 附魔 / 手牌 / 牌堆 / 奖励网格」各写特例。

拆成两小步：
1. **先做 `ICardSelector` 钩子** → 覆盖 A1/A3/A4/A5/A6/A7/A10（它们全部经由 `CardSelectCmd` 的 `From*` 方法，且 `GetSelectedCards(cards, min, max)` 的参数足够构造 request）。
2. **再补三个不走 `GetSelectedCards` 的**：A8（`FromChooseACardScreen` 返回单个 `CardModel`，同样走 `From*`，应该在同一条链上 —— 需实测确认它也调 `GetSelectedCards`，**未确认**）、A9（bundle 返回 `IEnumerable<IReadOnlyList<CardModel>>`，与 `GetSelectedCards` 形状不同，很可能要单独适配）、B2（走 `GetSelectedCardReward`，独立方法）。

没有独立节点的 A2 / A10 反而**不需要额外工作** —— 它们同样经过 `CardSelectCmd`，钩子一装就自动覆盖。这是这套方案最大的好处：**UI 层没有节点，但数据层有收口**。

### 批次 2：奖励族 + 事件 + 营火 + 地图（B / C1 / C2 / C3 / D）

这四个是**每局必经**、且候选集合都能从模型直接读出（见 §0.2），没有「读不到候选」的风险，实现成本低、收益高。其中：

- **地图（D1）已经做完了**（`sts2_move` 就是 `(col,row)`），只需要把 kind 名字和候选协议对齐，工作量≈0。
  （2026-10-23 补：`move` 已改成「发起 + 确认房间真的变了」，失败带 `available` / `actTransition`。）
- **事件（C1/C2）**：`EventModel.CurrentOptions` 是完整候选，`OptionButtonClicked` 是公开入口，一次做完覆盖普通事件 + 所有 Ancient（含 Neow）。
- **营火（C3）**：`RestSiteRoom.Options` + `RestSiteOption.IsEnabled`，最小。
- **奖励（B1/B3）**：`RewardsSet.Rewards` + `NChooseARelicSelection.RelicsSelected()`。

### 批次 3（必须单独做，且风险最高）：商店 + 战斗 + 小游戏

- **商店（C4）**：语义和其他所有场景都不同 —— 它不是「必须选 N 个」，而是「可 0~N 次购买 + 随时离开」，`min/max` 模型不适用，得加一个 `budget`/`repeatable` 概念。而且买「移除服务」会嵌套触发 A3，需要 request 栈（嵌套 request 的 id 管理）。
- **战斗（E1/E2）**：不在「阻塞式选择」模型里 —— 玩家可以长时间不决策（思考），也可以连出多张牌，还涉及目标选择。建议战斗**单独一套**：request = "你的回合开始了，手牌/能量/敌人如下"，picks = 一组动作序列（复用现有 `Batch` / `mcp__sts2__sts2_acts` 的思路），而不是「一问一答」。
- **小游戏（F1）**：只有水晶球事件，出现频率低，`X/Y` 坐标语义独立，最后做。

---

## 4. 风险点

### R1（最高危）：有 3 个决策点在 UI 层**没有独立节点**

已在 `members.txt` 中确认的证据：

- **手牌选择（A2 升级 / A10 弃牌/多选）**：落点是 `NPlayerHand.SelectCards(CardSelectorPrefs, Func<CardModel,bool>, AbstractModel, NPlayerHand.Mode)`（L11877），状态挂在手牌容器上：`NPlayerHand.IsInCardSelection`、`NPlayerHand.CurrentMode`（`Mode` = `None/Play/SimpleSelect/UpgradeSelect`，L57320-57325）。
  - 用户已实测：升级选牌界面复用手牌容器 `NHandCardHolder`，`combatState` 仍是 `PlayPhase`（对应 `MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase`，L34018），`screenType` 为空（因为 `NPlayerHand` 上没有 `ScreenType` 属性 —— 只有 `NCardGridSelectionScreen`/`NRewardsScreen`/`NChooseARelicSelection`/`NMapScreen` 这些有）。
  - **结论：这类绝对不能靠「有没有选牌屏」判断**，必须用 `CardSelectCmd` 钩子或 `NPlayerHand.IsInCardSelection + CurrentMode != Play` 识别。
  - 额外变量：`NPlayerHand.SelectModeGoldGlowOverride`（会高亮"可选牌"）—— 如果要用 UI 探测，这是唯一能反推 filter 的线索。

- **宝箱内容物（C5）**：`TreasureRoom` 的公开成员只有 `DoNormalRewards() : Task<int>` / `DoExtraRewardsIfNeeded()`，**没有**返回候选遗物列表的公开成员 —— 但**数据层已经确认**：`RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics : IReadOnlyList<RelicModel>`（public prop，读 private `_currentRelics`），由 `TreasureRoom.EnterInternal` 末尾的 `BeginRelicPicking()` 在**进房间那一刻**生成。`NTreasureRoomRelicHolder` 有 `Relic` / `Index` / `Initialize(RelicModel, IRunState)`（UI 层，开箱后才摆）。开箱状态读 `NTreasureRoom._hasChestBeenOpened` / `_isRelicCollectionOpen`（MetaPriv 核对过这两个 private bool）。见 §7.5。

- **商店卡牌移除（C4 的嵌套 A3）**：购买 `MerchantCardRemovalEntry` 后触发的是 `CardSelectCmd.FromDeckForRemoval`（数据层），UI 上不一定出现 A3 那个屏 —— 取决于是否复用 `NDeckCardSelectScreen`，**未确认**。

### R2：`CardSelectorPrefs` 不是任何界面节点的公开属性

全 `members.txt` grep `Prefs` 的结果（L36832 / L9562 / L9577 / L9593 / L9606 / L9620 / L9630 / L74244+）表明：`CardSelectorPrefs` 只作为 `CardSelectCmd.From*` 与各屏幕 `Create`/`ShowScreen` 的**参数**出现，以及 `CardSelectCmd` 各状态机的私有字段（`field public ... prefs`，是编译器生成的字段，不是可依赖的公开 API）。

后果：
- 从界面上**读不到** `MinSelect` / `MaxSelect` / `Prompt` / `Cancelable` / `RequireManualConfirmation`。
- 只能靠：① 拦截 `CardSelectCmd`（最优，参数里有 prefs）；② 反射私有字段（字段名如 `prefs`，**未确认稳定性**）；③ 用「屏幕类型 + 候选数量」硬编码推测（最差，不建议）。

### R3：`ScreenType` / `NetScreenType` 粒度不够

`MegaCrit.Sts2.Core.Entities.Multiplayer.NetScreenType`（L34167-34183）只有：
`None / Room / Map / Settings / Compendium / DeckView / CardPile / SimpleCardsView / CardSelection / GameOver / PauseMenu / Rewards / Feedback / SharedRelicPicking / RemotePlayerExpandedState`

**它区分不了「升级 / 移除 / 变形 / 附魔 / 手牌 / 牌堆」** —— 全都是 `CardSelection`。所以 `screenType` 只能用来做「大类」判定，`kind` 细分必须靠节点运行时类型（`NDeckUpgradeSelectScreen` vs `NDeckCardSelectScreen` vs …）或者 `CardSelectCmd` 的调用点。

另外用户说的「`screenType` 为空」在 A2 场景是**必然**的：`NPlayerHand` 上根本没有 `ScreenType` 属性。

### R4：奖励屏的候选是 `CardCreationResult` 不是 `CardModel`

- `NCardRewardSelectionScreen.ShowScreen(IReadOnlyList<CardCreationResult>, ...)`、`CardSelectCmd.FromSimpleGridForRewards(..., List<CardCreationResult>, ...)`、`ICardSelector.GetSelectedCardReward(IReadOnlyList<CardCreationResult>, ...)` 全部用 `CardCreationResult`。
- 而 `CardReward.Cards` 是 `IEnumerable<CardModel>`。
- `CardCreationResult`（L34592-34602）内部有 `Card : CardModel`、`originalCard`、`ModifyingRelics`、`HasBeenModified` —— **`CardCreationResult.Card` 才算「玩家看到并会得到的牌」**（可能被遗物改写）。key 生成必须统一从 `Card` 取，不能混用两套类型，否则 B2 的 pick 对不上。

### R5：`IsProceed` / `IsLocked` / `WillKillPlayer` 这类「非普通选项」必须显式建模

`EventOption`（L33274-33319）有：
- `IsProceed : bool` —— 「继续/离开」按钮，不是分支。如果把它当普通选项丢给 AI，AI 可能在有真选项时点它，白白结束事件。
- `IsLocked : bool` —— 不可选（条件不满足），必须从候选里排除或标 `locked`。
- `WillKillPlayer : Func<Player,bool>` / `ThatWillKillPlayerIf(...)` —— 会致死的选项。**这是唯一会造成不可逆损失的信息，必须显式传给 AI**（哪怕只是 `willKillPlayer: true`）。
- `WasChosen : bool` —— 已选过（某些事件可以反复选）。

`NProceedButton.IsSkip` 同理：`IsSkip == true` 时按钮是「跳过」，语义和「继续」不同。

### R6：`Reward.SelectUnsynchronized()` / `CardSelectCmd.SuspendSelectorForTest()` 是联机/测试语义

- `Reward.SelectUnsynchronized()`（L5189）名字明示「不同步」，单机跑没问题，联机下绕过 `PlayerChoiceSynchronizer.SyncLocalChoice` 可能造成不同步。**未确认**它在单机下的完整副作用，建议只用它做「单机兜底」，联机走 UI 路径。
- `CardSelectCmd.UseSelector` / `PushSelector` 属于 `MegaCrit.Sts2.Core.TestSupport` 命名空间。游戏里已经有 `TestMode.IsOn` / `AssertOn` / `AssertOff` 这套机制 —— **`TestMode` 关闭时 `UseSelector` 是否仍然生效，未确认**。如果它内部调 `TestMode.AssertOn()`，那我们在正常游戏里挂 selector 会抛 `TestModeOffException`。**这是批次 1 必须先验证的一件事**；如果不生效，退路是在 `CardSelectCmd.Selector` 的 setter 上做文章，或者退回「监听 `CardsSelected()` 的 Task」。

### R7：request 可能被游戏内部自动消解（"假决策"）

- `CombatUi` 有 `ProceedWithoutRewards` / `ShowRewards`；`AutoSlay` 里也是直接调 helper。
- 有些奖励会自动领取、有些选项会被遗物自动执行（`CardModel.CanBeGeneratedInCombat` 之类）。
- 结论：**每次推送 request 前要再确认一次「候选集合非空且确实在等玩家」**，否则 AI 答了一个已经不存在的 request，会白白浪费一轮。判据建议用 `CardSelectCmd.Selector` 是否在等 + `PlayerTurnPhase.Play` + `NMapScreen.IsTravelEnabled` 这类状态位组合。

### R8：候选集合会在 `await` 期间变化

`CardSelectCmd.From*` 内部是 `async`，期间遗物/能力可能抽牌、弃牌、改造手牌（`CardPileCmd.Draw` / `CardPileCmd.Shuffle` 等）。所以：
- 提交 picks 时必须**用 key 重新解析成当前候选里的实体**，不能缓存「第 0 个位置的 CardModel 引用」。
- 如果 key 解析失败（那张牌已经不在了），返回 `ok:false, error:"candidate gone"`，让 AI 重答是正确行为。

### R9：`NCardGridSelectionScreen` 的 `CardsSelected()` 只能调一次

`CardsSelected()` 返回的是 `Task<IEnumerable<CardModel>>`，而 `NCardGridSelectionScreen+<CardsSelected>d__11` 是状态机（L51812）—— 说明它内部只 `await` 一次待决的 `TaskCompletionSource`。**不要用轮询调它来"偷答案"**（第二次调用很可能返回已完成的旧 Task 或抛异常，未确认）。要拿答案只能：① 替换 selector；② 等界面把 `Task` 交给我们；③ 反射那个私有 TCS。

---

## 5. 明确「未确认」的清单（不要在此基础上写实现）

1. `CardSelectCmd.UseSelector/PushSelector` 是否需要 `TestMode.IsOn == true` 才能工作。
2. `ICardSelector.GetSelectedCards` 的 `min`/`max` 是否等价于 `CardSelectorPrefs.MinSelect`/`MaxSelect`（签名一致，但内部是否被改写未确认）。
3. `FromChooseACardScreen` / `FromChooseABundleScreen` 是否经过 `Selector`（A8/A9 能否被同一钩子覆盖）。
4. `CardModel.Id`（`ModelId`）在同一张牌跨界面刷新时是否稳定。
5. 宝箱（`TreasureRoom`）内容物候选的公开读取方式。
6. 商店「卡牌移除」购买后，是否复用 `NDeckCardSelectScreen`。
7. `NCardGridSelectionScreen` 的 `Prefs` 私有字段名/是否可反射。
8. 水晶球事件的挖掘次数限制字段。
9. `NChoiceSelectionSkipButton`（跳过按钮）具体挂在哪些屏幕上（`members.txt` 只显示它自己 `_Ready` / `AnimateIn` / `Hotkeys`，没有反向引用）。
10. 联机模式下 `Reward.SelectUnsynchronized()` 的副作用，以及 `NEventRoom.OptionButtonClicked` / `NMapScreen.TravelToMapCoord` 是否会自己发同步消息（这两个看起来会，但未逐一确认）。

---

## 6. 落地顺序建议（一句话版）

> **先验证 R6（selector 钩子能不能在正常游戏里装上）→ 装上就吃掉 A 类 10 个场景 → 顺势接 B2/B3 → 再补 C1/C2/C3 三个房间级 → D1 只改名 → 商店和战斗各自独立设计。**

做完前两步，网桥的「特例补丁」需求会从「每个新界面一个特例」降到「只有商店、战斗、水晶球三个特例」。

---

## 7. 现状（实现补充，2026-10）

> 这一节是**已经落地**的代码，不是设计建议。改协议前先看这里。

- **默认通道 = selector**：`RequestApi.InstallSelector()` 默认就 `CardSelectCmd.UseSelector(...)`
  （不需要任何环境变量）。要退回「扫界面 + 模拟点击」的 screen 通道，启动游戏前显式设
  `STS2_AGENT_SELECTOR=0`（也认 off/false/no/disable）。screen 通道代码保留、不默认启用 ——
  它的点击有 uid 过期、点空的已知 bug。
- **mod 端动画**（因为 selector 一接管，游戏就不弹界面了）：`/respond` 的处理顺序是
  ① 在主线程按 `CardModel` 找回那张牌在界面上的 `NCardHolder`（手牌先问
  `NPlayerHand.GetCardHolder`，再退化为扫树），触发游戏自己的
  `NCard.CardHighlight.AnimFlash()`（有手牌 holder 时再加 `NHandCardHolder.Flash()`）；
  ② 鲸鱼气泡报一句「升级 防御」；③ 停 560ms（`AnimLeadMs`）—— 玩家在这段时间看清选的是哪张；
  ④ 才 `TrySetResult` 把答案交回游戏；⑤ 再停 220ms（`AnimTailMs`）让结算动画起步，然后把高亮
  淡出（不阻塞），最后返回 `ok:true`。
- **能真闪牌的场景**：候选牌在界面上有节点 —— 主要是战斗内手牌类选择（「武装」这类
  `FromHandForUpgrade` / `FromHand*`，手牌一直都在屏幕上）。
- **做不到真卡牌动画的场景**：从牌组/牌堆选（`FromDeckForUpgrade` / `FromDeckForRemoval` /
  `FromCombatPile` …）。selector 装上后 `CardSelectCmd` 直接 `GetSelectedCards` 返回，
  牌组界面根本不出现（IL 里 `get_Selector` 的非空分支在建屏之前），界面树上没有对应的牌节点，
  只能退化成「延迟 + 鲸鱼气泡」。返回体用 `visual: "whale+delay"` 如实标注（对着 `card-flash`）。
- **返回结构**（只增字段，不改旧字段语义）：
  - `/request` 的 `pending`：`id/kind/prompt/min/max/candidates[{key,title,upgraded,cost}]` 不变，
    `animated` 恒为 `true`（会播 mod 端动画），新增 `animation` 说明具体动画。
  - `/respond`：`{ok:true, applied, how:"selector", animated:true, waitedMs:<实际等待毫秒>}`，
    另带 `source/route/visual/highlighted`。
- **动画期间的风控**：`_animating` 为真时 `/request` 只回 `pending:null, animating:true`，
  `/respond` 直接拒绝 —— 否则界面扫描会把「游戏在等 selector」误判成一次新的手牌选择。
- **通道 / 优先级**（2026-10-23 定版；同日追加 gameover / proceed；2026-10-24 追加 bundle /
  ascension / save-quit / reload）：selector（卡牌选择）>
  **游戏结束**（`kind:"gameover"` / `route:"gameover"`，收尾界面，见 §7.8）>
  **选牌包**（`kind:"bundle"` / `route:"bundle"`，一捆一捆选，见 §7.10）>
  奖励列表（`kind:"reward"` / `route:"rewards"`，见 §7.1）> 宝箱（`kind:"treasure"` /
  `route:"treasure"`，见 §7.5）> 药水目标（`kind:"potion"` / `route:"potion"`，见 §7.6）>
  商店（`kind:"shop"`，见 §7.2）> 营火（`kind:"rest"`，见 §7.3）> 事件（`kind:"event"`，见 §7.4）>
  **推进**（`kind:"proceed"` / `route:"proceed"`，战斗结束等奖励的空档，见 §7.9）>
  **菜单**（`kind:"menu"` / `route:"menu"`，主菜单 / 单人子菜单 / 角色选择 / 进阶 / 确认弹窗，
  见 §7.7 与 §7.11）。
  后面这几条都是「一个 pending 请求 + 复用 `Validate` / `NextKey` / `Fail` / 包裹层+核心体 /
  失败可重答 / 离开该界面时清 pending」，都不点坐标。
- **screen 通道 = fallback**：`/request` 里那条「扫界面 + 模拟点击」的请求（`route` 是
  `grid` / `choose` / `reward` / `hand` / `hand-guess`）和它的应答**都带 `"fallback": true`**。
  它只在 `STS2_AGENT_SELECTOR=0` / 代理模式关闭时才会产生，正常流程不该看到这个字段。
- **旧路由已删除**（2026-10-23）：`/choices`、`/choose`（按索引选牌）、`/select`（旧的选牌点击）、
  `/buttons`（按钮枚举）、`/confirm`（通用确认点击）。对应代码（`Choices.List` / `Choices.Choose`、
  `ScreenApi.Confirm` / `ScreenApi.SelectMany`、`UiClicks.Buttons`）一并删掉。
  剩下的 `/screen`、`/tap`、`/click`、`/click_at`、`/drag`、`/key` **保留但只作调试兜底**：
  菜单类界面万一协议没覆盖，还可以手动点一下；正常流程必须走 `/request` + `/respond`。

### 7.1 奖励通道（B1 `NRewardsScreen`，2026-10-23 落地）

`RequestApi.cs` 里的「奖励通道」，与 selector 通道共用 id / 候选 key / 校验 / `_animating` 风控，
**不走** `CardSelectCmd`，也不点坐标。

**数据源（全部在主线程读）**

| 用途 | 出处 |
|---|---|
| 界面节点 | `MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen`（注意在 `Nodes.Screens` 下，不是 `Nodes.Rewards`） |
| 奖励集合 | private 字段 `_rewardsSet : RewardsSet` |
| 候选 | `RewardsSet.Rewards : List<Reward>`（`LinkedRewardSet` 展开成它自己的 `Rewards`） |
| 已领判定 | `Reward.SuccessfullySelected`（`RewardsSet.AllRewardsSuccessfullySelected` 就是对它 `All`） |
| 标题 | `GoldReward.Amount` → `37金币`；`PotionReward.Potion.Title`；`RelicReward.Relic.Title`；其余用 `Reward.Description` |
| 按钮 | 子树里的 `NRewardButton`，按 `NRewardButton.Reward` 的**引用**配对（只用于调它自己的方法，不用于点坐标） |

`Reward.RewardType` 是 **protected**（子类各自 override），只能反射读。

**`/request` 长这样**

```jsonc
{
  "source": "screen",
  "selector": { "state": "active", "...": "..." },
  "pending": {
    "id": 1729,
    "kind": "reward",
    "stage": "rewards-list",          // ★ 和卡牌奖励三选一（stage=card-choice）区分
    "prompt": "战斗奖励：选择要领取的项目（可以都拿，也可以不要）",
    "min": 0,                         // 一项都不要也合法
    "max": 4,                         // = 候选数
    "requireConfirm": false,
    "cancelable": true,
    "caller": "NRewardsScreen",
    "confidence": "high",
    "animated": true,
    "animation": "应答时逐项调用奖励项自己的领取入口……",
    "route": "rewards",
    "candidates": [
      { "key": "37金币#1",   "title": "37金币",   "upgraded": false, "cost": -1, "rewardType": "Gold",   "detail": "…" },
      { "key": "力量药水#1", "title": "力量药水", "upgraded": false, "cost": -1, "rewardType": "Potion", "detail": "…" },
      { "key": "闪亮口红#1", "title": "闪亮口红", "upgraded": false, "cost": -1, "rewardType": "Relic",  "detail": "…" },
      { "key": "卡牌奖励#1", "title": "卡牌奖励", "upgraded": false, "cost": -1, "rewardType": "Card",   "detail": "将一张牌添加到你的牌组。" }
    ]
  }
}
```

`candidates[].rewardType` / `.detail` 是**只给奖励候选新增**的字段（卡牌候选上不出现）。
key = `标题#同标题序号`，与卡牌 key 同一套 `NextKey`。

**`/respond` 怎么应用**

1. `Validate`（复用）：id 对、数量在 `min..max`、key 逐字命中候选、不重复；`picks` 为空永远合法。
2. 逐项、按顺序：
   - ① `NRewardButton.GetReward()`（private，反射调）——这就是 `OnRelease` 里「点下去」调的处理方法。
     它自己走 `RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(reward)`，然后播
     **遗物飞进遗物栏**（`NRelicInventory.AnimateRelic`）/ **药水飞进槽位**（`NPotionContainer.AnimatePotion`），
     再发 `RewardClaimed` 让界面把这一行摘掉。
   - ② 拿不到按钮 → 模型层 `SelectLocalReward(reward)` + 自己补 `NRewardsScreen.RewardCollectedFrom(button)`。
   - 两项之间 `Task.Delay(320ms)`；`picks` 逐项结束才返回。
3. `picks: []` = **全部跳过**：`RewardsSetSynchronizer.SkipLocalRewardsSet()`（对每个没领的奖励
   `OnSkipped()` + 完结奖励集合）→ 普通奖励屏 `NOverlayStack.Instance.Remove(screen)`，
   终局奖励屏 `RunManager.ProceedFromTerminalRewardsScreen()`。等价于界面上的「跳过」。
4. 幂等保护：`Reward.SuccessfullySelected` 已为真、或按钮 `_isEnabled == false`（游戏正在处理）时
   **不重复领取**，避免金币/遗物发两遍。

**`/respond` 返回**

```jsonc
{
  "ok": true, "source": "screen", "route": "rewards",
  "applied": "37金币#1、力量药水#1", "how": "reward-button",
  "animated": true, "waitedMs": 1420,
  "visual": "reward-applied",     // 走了奖励项自己的界面流程；只走模型层时是 "reward-delay"；全跳过是 "reward-skipped"
  "settled": true, "claimed": 2,
  "items": [ { "key": "37金币#1", "how": "reward-button", "visual": "gold", "settled": true, "ok": true } ],
  "next": "card-choice"           // 只有卡牌奖励会带：下一步是选牌
}
```

**连锁（卡牌奖励 → 三选一）**

`CardReward.OnSelect` 会 `NCardRewardSelectionScreen.ShowScreen(...)` 然后 **await**
`OptionSelected()`，所以在奖励通道里**不能**把 `SelectLocalReward` 等到底
（否则 `/respond` 永远不返回、agent 也看不到选牌请求）。单项最多等 `RewardSettleMs=1500ms`，
超时按「已受理、下一步是选牌」返回 `next:"card-choice"`，由下一次 `/request` 给出选牌请求。

**卡牌奖励三选一（route=reward / stage=card-choice）现在的做法**

- 候选：`NCardRewardSelectionScreen._options : IReadOnlyList<CardCreationResult>` 的 `.Card`
  （读不到才退回扫卡牌节点）；`max=1` 是界面结构决定的（`OptionSelected()` 只回一个下标）。
- prompt：`_banner.label.Text`（游戏自己的横幅），读不到再用界面文字兜底（`promptFallback:true`）。
- 应答：反射调 private `SelectCard(NCardHolder)`（`GetCardHolder(card)` 拿 holder）——
  等于「点那张牌」的处理方法，不点坐标；界面关掉与否用 `WaitScreenGone` 判定。
- **放弃（可空手走）**：`cancelable:true` / `min:0` / 新增字段 `canSkip:true`，prompt 末尾追加
  「（也可以放弃，不选）」；`picks:[]` = 放弃，返回 `how:"skip"`、`visual:"reward-skipped"`。
  走的是界面自己的跳过替代项：读 `_extraOptions : IReadOnlyList<CardRewardAlternative>` 找
  `OptionId=="Skip"` 的下标，反射调 private `OnAlternateRewardSelected(该下标)`
  （替代项按钮的 Released 回调就是调它；参数是**替代项下标**，方法内部自己加 `_options.Count`）。
  之后 `CardReward.OnSelect` 按 `AfterSelected==EndSelectionAndDoNotCompleteReward` 结束选择、
  把界面从 `NOverlayStack` 摘掉并返回 `false`（不领这张），`NRewardButton.GetReward` 因此走
  「跳过」分支发 `RewardSkipped`，奖励列表那一行由 `NRewardsScreen.RewardSkippedFrom` 记账。
  `CardReward.CanSkip` 为 false 时游戏不生成该替代项，那时 `canSkip:false`、`min` 回到 1。
- **仍未开放**：重 roll（`OptionId=="REROLL"`，`AfterSelected==DoNothing`，副作用未实测）。

**清场**：奖励界面从场景树里消失后，`/request` 会清掉对应 pending（不留僵尸请求）；
只剩「跳过 / 继续」一个按钮时不发请求（推进用 `/proceed`）。

### 7.2 商店通道（C4 `NMerchantInventory`，2026-10-23 落地）

`RequestApi.cs` 里的「商店通道」，和奖励通道同一套结构：一个 pending 请求 + 复用
`Validate` / `NextKey` / `Fail` / `_animating` 风控，**不走** `CardSelectCmd`，也不点坐标。

**类型核对结论（依据：`meta` 只读工具对 `sts2.dll` 的 metadata + IL）**

| 项 | 结论 | 依据 |
|---|---|---|
| 界面节点 | `MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory : Godot.Control`；事件里的假商人 `NFakeMerchantInventory : NMerchantInventory` 一起被认 | `types.txt` |
| 「商店打开了」 | `NMerchantInventory.IsOpen`（`Open()` 的最后一行 `set_IsOpen(true)`；`Close()` 里置回 false） | `NMerchantInventory::Open` / `Close` IL |
| **开面板入口** | `NMerchantRoom.OpenInventory()`（private）/ `NFakeMerchant.OpenInventory()`（private）—— 这才是点商人按钮那条链：`NMerchantButton.OnRelease` → `EmitSignalMerchantOpened` → `OnMerchantOpened()`（只有一句 `OpenInventory()`）；`OpenInventory` 里：`if (Inventory.IsOpen) return;` → `_proceedButton.Disable()` → `Inventory.Open()` → `MerchantButton.Disable()` → 连 `InventoryClosed` 再恢复 | 两个 `OpenInventory` IL + `NMerchantButton::OnRelease` IL |
| 数据层 | `NMerchantInventory.Inventory : MerchantInventory`（`Initialize(inventory, dialogue)` 里 set） | `Initialize` IL |
| 条目集合 | `MerchantInventory.AllEntries` = `CardEntries`→`RelicEntries`→`PotionEntries`→`CardRemovalEntry`（`get_AllEntries` IL 里 4 个数组元素 + `SelectMany`） | IL |
| 价格 | `MerchantEntry.Cost : int`（`_cost` 经 `Hook.ModifyMerchantPrice` 后 `decimal→int`，= 界面显示价） | `get_Cost` IL |
| 买不买得起 | `MerchantEntry.EnoughGold` = `Cost <= Player.Gold`（mod 自己算 `affordable`，不依赖它） | `get_EnoughGold` IL |
| 已售出 | `MerchantEntry.IsStocked`，各子类 override：`MerchantCardEntry`=`CreationResult != null`；`MerchantRelicEntry`/`MerchantPotionEntry`=`Model != null`；`MerchantCardRemovalEntry`=`!Used` | 各 `get_IsStocked` IL |
| 标题 / 效果 | 卡：`MerchantCardEntry.CreationResult.Card`（`Title` / `Description` + `DynamicVars` 占位符替换）；遗物·药水：`Model.Title` / `DynamicDescription`（`Description` 的 getter 是 private，用反射读） | `get_*` IL + `members.txt` |
| **购买入口** | `NMerchantSlot.OnSelected()`（private，`Task`）——`_GuiInput` → `OnMouseReleased` → `OnSelected`；它 `ClearHoverTip()` → `OnTryPurchase(Inventory)` → `Entry.OnTryPurchaseWrapper(inventory, false)` | `NMerchantSlot::<OnSelected>d__29::MoveNext` IL |
| 模型层兜底 | `MerchantEntry.OnTryPurchaseWrapper(MerchantInventory, bool) : Task<bool>`；卡牌移除是 3 参重载 `(inventory, ignoreCost=false, cancelable=true)` | `MerchantCardRemovalEntry::<OnTryPurchaseWrapper>d__12` IL |
| 表现从哪来 | 不是我们补的动画：`MerchantEntry.PurchaseCompleted` 信号 → `NMerchantInventory.OnPurchaseCompleted`（商家音效 `merchant_thank_yous`、台词、焦点移到下一格），而格子自己的 `UpdateVisual` / `OnSuccessfulPurchase` 在 `FillSlot` 里接到 `EntryUpdated` / `PurchaseCompleted` 上 —— 调不调界面方法都会触发 | `SubscribeToEntries` / `OnPurchaseCompleted` / `NMerchantCard::FillSlot` IL |

> 注意 `NMerchantSlot.OnTryPurchase` 与 `MerchantEntry.IsStocked` 的**基类**声明在 IL 里 RVA=0（抽象），
> 所以只能 `callvirt` / 走子类 —— 反射找方法时要沿继承链（`FindMethod` 已经这么做）。

**商店是两阶段请求**（2026-10-23 起）：`pending.stage` = `"closed"` / `"items"`。

| 阶段 | 什么时候 | prompt | min / max | 候选 |
|---|---|---|---|---|
| `closed` | 商店面板在树上但 `IsOpen == false`（刚进房间 / 玩家还没点商人） | `商店：要打开看看吗（打开前也可以直接用药水；商店里只有污浊药水能对商人用，打开面板后就来不及了）` | `0 / 1` | 只有 `打开商店#1`（`itemType:"Action"`） |
| `items` | `IsOpen == true`，商品摆出来了 | `商店：选择要买的东西（中途也可以不买）` | `0 / 可买条目数` | 商品（和以前完全一样） |

> 老行为是「`IsOpen == false` 就 `pending:null`」，agent 进店看不到任何东西、必须先自己点商人按钮。
> 现在 `closed` 阶段直接把选择权交给 agent：**打开** 或 **不打开**（`picks:[]`），顺手给出 `gold` 和
> `potions`（药水槽现状，见下）。这一阶段**不**读 `MerchantInventory.AllEntries`，所以店里卖什么
> 完全不会泄露（和宝箱 `chest-closed` 同一个套路）。
> stage 进签名 ⇒ 面板一打开就换新 id（`closed` 的应答作废，重新 GET /request 会拿到 `items`）。

**`stage:"closed"` 的 `/request` 长这样**

```jsonc
{
  "id": 1801, "kind": "shop", "stage": "closed",
  "prompt": "商店：要打开看看吗（打开前也可以直接用药水；商店里只有污浊药水能对商人用，打开面板后就来不及了）",
  "min": 0, "max": 1, "requireConfirm": false, "cancelable": true,
  "caller": "NMerchantInventory", "confidence": "high", "route": "shop",
  "animated": true, "animation": "……OpenInventory()……轮询 IsOpen……",
  "gold": 459,
  "potions": [                                  // ★ 新增字段（只加不改）：药水槽现状
    { "key": "力量药水#1", "title": "力量药水", "description": "获得 2 点力量。",
      "usable": false, "canTargetMerchant": false,
      "whyNot": "只有在战斗里才能用（当前不是战斗，没有可作用的目标）" },
    { "key": "污浊药水#1", "title": "污浊药水", "description": "……",
      "usable": true,  "canTargetMerchant": true }
  ],
  "candidates": [
    { "key": "打开商店#1", "title": "打开商店", "upgraded": false, "cost": -1,
      "itemType": "Action", "enabled": true, "detail": "打开后能看到商品和价格" }
  ],
  "next": "picks:[\"打开商店#1\"] = 打开面板……"
}
```

- `potions[].usable` / `canTargetMerchant` 只是**参考信息**，判定来自游戏自己的模型：
  `usable = (PotionUsage == AnyTime) && PotionModel.PassesCustomUsabilityCheck && 角色活着 &&
  Player.CanRemovePotions && !IsQueued`（规则照抄 `NPotionPopup._Ready` + `RefreshButtons`）；
  `canTargetMerchant = TargetType == TargetedNoCreature && 商人按钮在`。
  **真正的拦截发生在 `POST /potion/use` 那一刻**（见 §7.6），不靠这两个字段。
- 商店里能「对商人」用的只有 `TargetType == TargetedNoCreature` 的药水（`POTION.FOUL_POTION` 污浊药水）；
  其余药水的 `Usage` 是 `CombatOnly`（力量/火焰/…），在商店里游戏自己就不让用（`PotionPopup` 的
  使用按钮被 `RefreshButtons` 禁用），`usable` 会是 `false`。

**`stage:"closed"` 的 `/respond`**

```jsonc
// picks:["打开商店#1"]
{ "ok": true, "source": "screen", "route": "shop", "stage": "closed",
  "applied": "（打开商店）", "how": "shop-open", "animated": true, "waitedMs": 180,
  "visual": "shop-opened", "opened": true, "bought": 0, "items": [],
  "next": "面板已打开、什么都没买：重新 GET /request 会给出 stage:\"items\" 的商品列表。" }

// picks:[]（不打开）
{ "ok": true, "route": "shop", "stage": "closed", "applied": "（没打开）",
  "how": "shop-open-skip", "visual": "shop-not-opened", "opened": false }
```

- 打开走的是**游戏自己的入口**，不点坐标：沿父链找到持有这个面板的房间节点
  （`NMerchantRoom` / `NFakeMerchant`，按类型认），调它 private 的 `OpenInventory()`；
  找不到房间节点才退回 `NMerchantInventory.Open()`。然后**轮询** `IsOpen` 变 `true`（最多 3s）。
- 面板已经开着（玩家自己点的）→ 不再调第二次，`how:"shop-open-awaited"`，并在 `note` 里说明。
- 这一步**绝不**顺手买东西；买了会污染 agent 的预算判断。
- `openInventory` 的 `if (IsOpen) return;` 和「Disable 前进按钮 / Disable 商人按钮 / 连 InventoryClosed」
  都在房间节点那一层，直接调 `NMerchantInventory.Open()` 会漏掉这些副作用，所以优先走房间节点。

**`stage:"items"`：和以前一模一样**

**`stage:"items"` 的 `/request` 长这样**

```jsonc
{
  "source": "screen",
  "selector": { "state": "active", "...": "..." },
  "pending": {
    "id": 1730,
    "kind": "shop",
    "stage": "items",                  // ★ 新增字段（closed / items）
    "prompt": "商店：选择要买的东西（中途也可以不买）",
    "min": 0,                          // 一件都不买永远合法
    "max": 4,                          // = 当前可买的候选数
    "requireConfirm": false,
    "cancelable": true,
    "caller": "NMerchantInventory",
    "confidence": "high",              // 候选/价格都来自 MerchantInventory 模型
    "animated": true,
    "animation": "应答时逐项调用格子自己的点击处理方法……",
    "route": "shop",
    "gold": 274,                       // ★ 新增字段：当前金币
    "potions": [ /* 同 closed 阶段：key/title/description/usable/canTargetMerchant */ ],
    "candidates": [
      { "key": "闪亮口红#1",  "title": "闪亮口红",  "upgraded": false, "cost": -1,
        "price": 150, "affordable": true,  "itemType": "Relic", "detail": "每场战斗开始时……" },
      { "key": "打击#1",      "title": "打击",      "upgraded": false, "cost": 1,
        "price": 48,  "affordable": true,  "itemType": "Card",  "detail": "造成 6 点伤害。" },
      { "key": "力量药水#1",  "title": "力量药水",  "upgraded": false, "cost": -1,
        "price": 75,  "affordable": true,  "itemType": "Potion","detail": "获得 2 点力量。" },
      { "key": "卡牌移除#1",  "title": "卡牌移除",  "upgraded": false, "cost": -1,
        "price": 100, "affordable": true,  "itemType": "Service","detail": "从牌组里移除一张牌（买下后会弹选牌）。" }
    ]
  }
}
```

- `candidates[].cost` 仍是「卡牌能量费用」（卡牌候选才有，其余 -1，和奖励候选一致）；
  钱一律看 `price`。已售出的格子（`IsStocked == false`）**不出现**在候选里。
- key = `标题#同标题序号`，与其他通道同一套 `NextKey`；改名/换货 → 签名变 → 换新 id。
- **什么时候发 `items`**：`IsOpen == true` 才发（面板真的打开了，买走/售罄表现和商家台词才看得见）。
  面板在树上但没打开 → 发 `closed`（见上）；打开着但候选为空（全买光）→ `pending:null`
  （离开商店用 `/proceed`）。

**`/respond` 的购买流程**

1. `Validate`（复用）：id 对、数量在 `0..max`、key 逐字命中候选、不重复；`picks:[]` 永远合法。
2. `picks:[]` = **什么都不买**：不调任何购买入口、不动商店，直接返回
   `{ok:true, route:"shop", applied:"（什么也没买）", how:"shop-none", visual:"shop-none", bought:0}`。
3. 逐件、按顺序（卡牌移除服务**排到最后**，因为它会弹模态选牌挡住别的购买）：
   - 先读现场：`Gold` / `Price` / `IsStocked` / 这一格摆的是哪一件（模型 Id）。
     - 已经卖掉 → `ok:false`，`how:"already-sold"`，不重复购买。
     - `Gold < Price` → `ok:false`，`how:"gold"`，文案只陈述事实：
       `金币不够：当前金币 12，这件价格 48`。
   - ① 调 `NMerchantSlot.OnSelected()`（private，反射）→ `how:"shop-slot"`；
     ② 拿不到格子节点时退化成模型层 `MerchantEntry.OnTryPurchaseWrapper(inventory, false)`（移除服务用 3 参重载）
     → `how:"shop-model"`。两个都拿不到 → `ok:false`（不谎报）。
   - 最多等 `ShopSettleMs = 1500ms`（理由见下一条），然后**核对**：
     - 游戏返回 `false` → `ok:false`，写明「购买前金币 / 价格 / 购买后金币 / 这一格是否仍待售」；
     - 金币没有减少到至少 `Price` → `ok:false`，写明两个金币数和价格；
     - 金币确实少了、但这一格还是**同一件**商品在售 → `ok:false`（用模型 Id 判定；
       万一 `RestockAfterPurchase` 换了新货，Id 变了就算成功）。
   - 每件之间 `Task.Delay(ShopGapMs = 340ms)`，让买走/售罄表现和商家台词播出来。

**`/respond` 返回**

```jsonc
{
  "ok": true, "source": "screen", "route": "shop",
  "applied": "闪亮口红#1、打击#1", "how": "shop-batch",
  "animated": true, "waitedMs": 1420,
  "visual": "shop-bought",       // 走了格子自己的入口 = "shop-bought"；只走模型层 = "shop-model-delay"
  "settled": true, "bought": 2, "gold": 76,
  "items": [
    { "key": "闪亮口红#1", "itemType": "Relic", "price": 150, "how": "shop-slot",
      "visual": "shop-bought", "settled": true, "ok": true, "note": "花费 150（274 → 124）" },
    { "key": "打击#1", "itemType": "Card", "price": 48, "how": "shop-slot",
      "visual": "shop-bought", "settled": true, "ok": true, "note": "花费 48（124 → 76）" }
  ],
  "next": "card-choice"          // 只有卡牌移除服务会带：下一步是选牌
}
```

有任意一件没买成时整体是 `ok:false`（`error` 里逐件列原因），但**已经买成的那几件如实写进
`applied` / `items`**，不倒退、不谎报。

**连锁（移除服务 → 选牌）**

`MerchantCardRemovalEntry.OnTryPurchaseWrapper` → `OneOffSynchronizer.DoLocalMerchantCardRemoval(gold, cancelable)` →
`CardSelectCmd.FromDeckForRemoval(prefs{MinSelect=1, Cancelable=true, RequireManualConfirmation=true})`，
**选完才** `PlayerCmd.LoseGold` + `CardPileCmd.RemoveFromDeck` + `NMerchantInventory.OnCardRemovalUsed()`。
所以在 `/respond` 里**不能**把这个 Task 等到底（否则永不返回、agent 也看不到选牌请求）：

- 单项最多等 `ShopSettleMs`，超时就返回 `settled:false`、`next:"card-choice"`，
  note 写明「金币要等选完才扣」；
- 下一次 `/request` 给出的选牌请求走**已有**的卡牌通道：selector 开着时是 `kind:"remove"`
  （`DetectKind` 从 `FromDeckForRemoval` 认出来），退化成界面扫描时是 `route:"grid"`；
- 金币是在选完之后才扣的，所以这一项**不**做金币/售罄校验（选了哪张由选牌请求负责）；
- 玩家在选牌里取消（`Cancelable=true`）→ `FirstOrDefault()` 为 null → 不扣金币、`Used` 保持 false。

**优先级与清场**

`/request` 的优先级：selector 通道（选牌）> 奖励列表 > 宝箱 > 药水目标 > 商店 > 营火 > 事件。
买移除服务后选牌请求出现时，商店那份 pending 让位：走界面扫描那条路会 `DropShopPending`，走 selector
那条路则直接在 `Current()` 顶部返回选牌请求；选完牌后按当前金币/剩余条目重新生成（新 id），不会出现两个
未决请求。药水目标请求出现时商店那份同样让位（`/potion/use` 之后就轮不到它了，答完药水会重新生成）。
商店界面从场景树里消失（离开商店）、或者全部买光时 `/request` 直接清掉 pending。

**仍未实测的点（写在这里，别当已确认）**

- `NMerchantSlot.OnSelected()` 的反射调用在生产环境里的行为（IL 已确认它是 `OnMouseReleased`
  里调的那个 0 参 private 方法，且 `_merchantRug` 由 `NMerchantRoom._Ready → Initialize` 赋值）。
- `Hook.ShouldRefillMerchantEntry` 为 true 时格子会补货（`RestockAfterPurchase`），这时「已售出」
  判定不成立 —— 代码用模型 Id 变化区分，但没有真实样例跑过这条分支。
- `PurchaseStatus.FailureSpace` 的确切含义（只确认了基类 `OnTryPurchaseWrapper` 在
  `!IsStocked` 时发 `FailureOutOfStock`、`!EnoughGold` 时发 `FailureGold`）；药水槽满只按
  `Potions.SlotsFull` 预先拦，没去解析 `FailureSpace`。
- 移除服务「金币在选牌之后才扣」是按 IL 得出的结论，没在实机上核对过 settle 前后的金币曲线。
- **`closed → items` 的实机验证没做**（2026-10-23 用户正在玩、不允许调接口）：`OpenInventory()` 是通过
  反射调的，按 IL 它会同步把 `IsOpen` 置 true、并留下「前进按钮被 Disable、商人按钮被 Disable、
  InventoryClosed 连着恢复逻辑」这几个副作用；实机上还没看到过一次完整的「agent 应答 → 面板弹出」。
  万一 `OpenInventory` 找不到（游戏更新改了名字），代码会退回 `NMerchantInventory.Open()`
  并写一条 warn 日志——那条路会漏掉前进/商人按钮的处理。

### 7.3 营火通道（C3 `NRestSiteRoom` + `RestSiteRoom.Options`，2026-10-23 落地）

`RequestApi.cs` 里的「营火通道」，和商店通道同一套结构：一个 pending 请求 + 复用
`Validate` / `NextKey` / `Fail` / `_animating` 风控，**不走** `CardSelectCmd`，也不点坐标。

营火是**选一项**（休息 / 锻造 / 挖掘 …），选完只剩「前进」离开 ⇒ `min = max = 1`、`cancelable = false`。
「前进」不通过 `/respond`（离开房间走 `/proceed`）。

**类型核对结论（依据：`meta` 只读工具对 `sts2.dll` 的 metadata + IL）**

| 项 | 结论 | 依据 |
|---|---|---|
| 房间节点 | `MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom : Godot.Control`；`NRestSiteRoom.Instance` = `NRun.Instance.RestSiteRoom` = **房间容器当前场景**（不在营火时为 null，不会留僵尸节点） | `NRun::get_RestSiteRoom` = `_roomContainer.CurrentScene is NRestSiteRoom` |
| 选项集合 | `NRestSiteRoom.Options`（public prop，读 `private _room` 的 `Options`）→ `RestSiteRoom.Options` → `RestSiteSynchronizer.GetLocalOptions()` → 本玩家那份 `options` | `NRestSiteRoom::get_Options`、`RestSiteRoom::get_Options` IL |
| 选项哪来的 | **进房间时** `RestSiteOption.Generate(player)`：`[HealRestSiteOption(休息), SmithRestSiteOption(锻造)]` + 多人时 `MendRestSiteOption` + `Hook.ModifyRestSiteOptions(runState, player, list)` ← **遗物在这里加选项**（铲子加「挖掘」就是这样进来的） | `RestSiteOption::Generate` IL |
| 每项字段 | `OptionId : string`、`Title : LocString`、`Description : LocString`、`IsEnabled : bool`、`OnSelect() : Task<bool>`、静态 `Generate(Player)` | `members.txt` / `MetaPriv RestSiteOption` |
| `IsEnabled` 的语义 | **基类实现恒为 true**（HP 满时「休息」仍可选，只是回血 0）；只有子类 override（`SmithRestSiteOption.IsEnabled = Deck.UpgradableCardCount > 0`）。界面拿它把按钮**变灰**（`NRestSiteButton.Reload` 把 HSV 饱和度压 0、亮度 0.6），**不**代表点不动 | `RestSiteOption::get_IsEnabled`（`ldc.i4.1; ret`）、`SmithRestSiteOption::get_IsEnabled`、`NRestSiteButton::Reload` IL |
| **选中入口** | `NRestSiteButton.OnRelease` → `SelectOption(option)`（private，返回 `Task`）：`Options.IndexOf(option)` → `_executingOption=true` → `NRestSiteRoom.Instance.DisableOptions()` → `RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(index)` → 成功才 `NRestSiteRoom.AfterSelectingOption(option)` | `NRestSiteButton::OnRelease` / `<SelectOption>d__26::MoveNext` IL |
| 选中真正干活的那一句 | `RestSiteSynchronizer.ChooseLocalOption(int) : Task<bool>` → `ChooseOption(player, index)` → `restSites[slot].options[index]` → `BeforePlayerOptionChosen` → **`await option.OnSelect()`** → `lastChosenOptionIndex = index` → `AfterPlayerOptionChosen` | `RestSiteSynchronizer::<ChooseOption>d__28::MoveNext` IL |
| 「前进」按钮 | `NRestSiteRoom.ProceedButton : NProceedButton`（`IsEnabled` 是 `NClickableControl` 的 public prop）；`ShowProceedButton()` 只在选**成功**之后 `Enable()` + `NMapScreen.SetTravelEnabled(true)`；`前进` → `NMapScreen.Open()` | `ShowProceedButton` / `OnProceedButtonReleased` IL |
| 选完的界面表现 | `AfterSelectingOption(option)` → `AfterSelectingOptionAsync`：`HideChoices` + `option.DoLocalPostSelectVfx(cts)`（**每个选项自己的** PostSelectVfx）+ `ExtinguishFireIfAble` → `UpdateRestSiteOptions()` + `ShowProceedButton()` →（`Options.Count > 0` 时）`ShowChoices()` | `NRestSiteRoom::<AfterSelectingOptionAsync>d__58::MoveNext` IL |
| 「已经选过」怎么判 | `RestSiteSynchronizer.GetChosenOptionIndex(ulong netId) : int?`（= `restSites[slot].lastChosenOptionIndex`）；`BeginRestSite()` 每次进房间 `Clear()` 重建，所以不会串到上一间营火 | `GetChosenOptionIndex` / `BeginRestSite` IL |

**`/request` 长这样**

```jsonc
{
  "source": "screen",
  "selector": { "state": "active", "...": "..." },
  "pending": {
    "id": 1731,
    "kind": "rest",
    "prompt": "营火：选择一项（离开营火用 /proceed，不通过 /respond）",
    "min": 1,                          // 必须选一项
    "max": 1,                          // 也只能选一项
    "requireConfirm": false,
    "cancelable": false,                // 「前进」不是这里的事
    "caller": "NRestSiteRoom",
    "confidence": "high",               // 候选/title/enabled 全部来自 RestSiteOption 模型
    "animated": true,
    "animation": "应答时调营火按钮自己的选中方法 NRestSiteButton.SelectOption(option)……",
    "route": "rest",
    "candidates": [
      { "key": "休息#1", "title": "休息", "upgraded": false, "cost": -1,
        "enabled": true,  "optionId": "HEAL",  "detail": "回复 30% 最大生命。" },
      { "key": "锻造#1", "title": "锻造", "upgraded": false, "cost": -1,
        "enabled": true,  "optionId": "SMITH", "detail": "升级一张牌。" },
      { "key": "挖掘#1", "title": "挖掘", "upgraded": false, "cost": -1,
        "enabled": true,  "optionId": "DIG",   "detail": "挖出一个遗物。" }
    ]
  }
}
```

- `candidates[].enabled` / `.optionId` / `.detail` 是**只给营火候选新增**的字段。
- `enabled` 是模型真值（界面用它做变灰），**不**等于「点不动」：`IsEnabled` 的基类实现恒为 true。
- key = `标题#同标题序号`（同一套 `NextKey`）。
- **什么时候才发这条请求**：在营火房间里、`GetChosenOptionIndex(本地玩家) == null`（还没选过）、
  当前没有任何按钮在 `_executingOption`（典型是「锻造」正在等选牌）、并且地图没打开
  （玩家按了「前进」之后营火房间还在树上，但那一刻不该再发营火请求）。
  选过之后营火只剩「前进」，`/request` 回 `pending:null`（离开用 `/proceed`）。

**`/respond` 怎么应用**

1. `Validate`（复用）：id 对、数量 `1..1`、key 逐字命中候选。`picks:[]` 非法（`cancelable:false`）。
2. 先读现场 `RestView`：选项 `OptionId`+`enabled` 列表、`GetChosenOptionIndex`、`ProceedButton.IsEnabled`、
   HP/最大 HP、牌组张数/已升级张数、遗物数。
   - 已经选过（`GetChosenOptionIndex >= 0`）→ `ok:false`，`how:"already-chosen"`，不重复选择。
   - 按钮 `_isUnclickable` / `_executingOption` 为真 → `ok:false`（正在执行 / 点不动），不重复触发。
3. ① 调 `NRestSiteButton.SelectOption(option)`（private，反射；`Task`）—— 就是玩家点一下的那条链，
   `how:"rest-button"`；
   ② 拿不到按钮时退化成 `RestSiteSynchronizer.ChooseLocalOption(index)`（`Task<bool>`，成败拿得到）
   并自己补一次 `AfterSelectingOption(option)`，`how:"rest-model"`。
4. 最多等 `RestSettleMs = 1200ms`，然后**核对**（四条任一条命中才算生效，否则 `ok:false` 并写出前后事实）：
   - `GetChosenOptionIndex` 从 null 变成下标；
   - 选项列表（`OptionId` + `enabled`）变了；
   - 玩家状态变了：HP / 最大 HP / 牌组张数 / 已升级张数 / 遗物数；
   - 「前进」按钮从禁用变成启用。

**`/respond` 返回**

```jsonc
{
  "ok": true, "source": "screen", "route": "rest",
  "applied": "休息#1", "how": "rest-button", "optionId": "HEAL",
  "animated": true, "waitedMs": 1180,
  "visual": "rest-applied",       // 走了按钮自己的入口 = "rest-applied"；只走同步器 = "rest-model-delay"
  "settled": true,
  "items": [ { "key": "休息#1", "optionId": "HEAL", "how": "rest-button",
               "visual": "rest-applied", "settled": true, "ok": true } ]
}
```

**连锁（锻造 → 选牌）**

`SmithRestSiteOption.OnSelect` → `CardSelectCmd.FromDeckForUpgrade(player, prefs{UpgradeSelectionPrompt,
MaxSelect=SmithCount, Cancelable=true, RequireManualConfirmation=true})`，**选完才**
`CardCmd.Upgrade` + `Hook.AfterRestSiteSmith`。所以 `/respond` 里**不能**把这个 Task 等到底
（否则永不返回、agent 也看不到选牌请求）：

- 单项最多等 `RestSettleMs`；超时且 selector 通道已经有待答请求（`HasPending`）→ `settled:false`、
  `next:"card-choice"`，note 写明「要等选完才真的结算」；
- 下一次 `/request` 给出的选牌请求走**已有**的卡牌通道：selector 开着时是 `kind:"upgrade"`
  （`DetectKind` 从 `FromDeckForUpgrade` 认出来）、`caller:"FromDeckForUpgrade"`，会走已有的牌库动画；
- 选完之后 `ChooseLocalOption` 才完成 → 按钮的 `AfterSelectingOption` 播 PostSelectVfx + 亮出「前进」，
  于是 `/request` 因为「已经选过」而回 `pending:null`。

**清场**：离开营火（`NRestSiteRoom.Instance` 变 null）后 `/request` 直接清掉 pending；
被更高优先级的请求（选牌 / 奖励 / 商店）顶掉时也会清。选过之后不再发营火请求，不留僵尸请求。

**仍未实测的点**

- `NRestSiteButton.SelectOption(option)` 的反射调用在生产环境里的行为（IL 已确认它是 `OnRelease`
  里调的那个 1 参 private 方法）。
- 单人模式下「一次营火只能选一项」是靠 `GetChosenOptionIndex` 判定的（IL 里 `lastChosenOptionIndex`
  在 `ChooseOption` 里被置位，但**成功与否都置**）；如果玩家取消选牌导致 `OnSelect` 返回 false，
  游戏会把选项重新启用，而我们会因为「已经选过」不再发请求 —— 这条边界没在实机上跑过。
- `RestSiteOption.IsEnabled == false` 时按钮是否真的可点（IL 只看到它改 shader；`_isUnclickable`
  只在 `OnRelease` 里读，写在场景/编辑器属性上）。

### 7.4 事件通道（C1/C2 `NEventRoom` + `EventModel.CurrentOptions`，2026-10-23 落地）

`RequestApi.cs` 里的「事件通道」，和营火通道同一套结构。事件 = 选一个选项推进剧情，
`min = max = 1`、`cancelable = false`；「继续/离开」如果作为选项存在（`IsProceed`），也在候选里。

**类型核对结论（依据：`meta` 只读工具对 `sts2.dll` 的 metadata + IL）**

| 项 | 结论 | 依据 |
|---|---|---|
| 房间节点 | `MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom : Godot.Control`；`NEventRoom.Instance` = `NRun.Instance.EventRoom` = 房间容器当前场景 | `NRun::get_EventRoom` IL |
| 事件模型 | `NEventRoom` 的 private 字段 `_event : EventModel`（`SetOptions`/`RefreshEventState` 用的就是它）；读不到再退到 `RunManager.Instance.EventSynchronizer.GetLocalEvent()`，最后退到 `EventRoom.LocalMutableEvent` | `MetaPriv NEventRoom` + `members.txt` |
| 候选 | `EventModel.CurrentOptions : IReadOnlyList<EventOption>`（**类型在 `MegaCrit.Sts2.Core.Events`，不是 `Core.Models`**） | `members.txt` / `MetaPriv Core.Events.EventOption` |
| 每项字段 | `TextKey : string`、`Title`/`Description : LocString`、`IsLocked`、`IsProceed`、`WasChosen`、`WillKillPlayer : Func<Player,bool>`、`Relic`、`Chosen() : Task`、`DisableOnChosen` | `MetaPriv` |
| **执行入口** | `NEventRoom.OptionButtonClicked(EventOption option, int index)`（**public，返回 void**）：`IsLocked` → 直接 return；`IsProceed` → `option.Chosen()`（= `NEventRoom.Proceed()`：`NMapScreen.SetTravelEnabled(true)` + `Open()`）；否则非共享事件先 `Layout.ClearOptions()`，再 `RunManager.Instance.EventSynchronizer.ChooseLocalOption(index)` | `NEventRoom::OptionButtonClicked` IL |
| `index` 的含义 | **就是 `EventModel.CurrentOptions` 的下标**：`ChooseOptionForEvent` 里 `option = CurrentOptions[index]`，越界抛 `InvalidOperationException`；`NEventOptionButton.Index` 由 `AddOptions` 按 `_optionsContainer.GetChildCount()` 生成，与 `CurrentOptions` 顺序一致 | `EventSynchronizer::ChooseOptionForEvent` / `NEventLayout::AddOptions` IL |
| 选项怎么跑完 | `ChooseOptionForEvent` 把 `TaskHelper.RunSafely(option.Chosen())` 挂进 `_pendingOptionTasks`，所以要等就 `RunManager.Instance.EventSynchronizer.AwaitPendingOptionTasks()`（`OptionButtonClicked` 本身拿不到 Task） | 同上 + `EventSynchronizer::AwaitPendingOptionTasks` |
| `Chosen()` 内部 | `OnChosen==null` 直接返回；`DisableOnChosen && WasChosen` 直接返回（游戏自带幂等）；`WasChosen=true` → `BeforeChosen`（`NEventRoom.BeforeOptionChosen` → `DisableOptionButtons`）→ `await OnChosen()` | `EventOption::<Chosen>d__63::MoveNext` IL |
| 事件结束时的「继续」 | `CurrentOptions` 被清空后，`NEventRoom.SetOptions` 自己造一个 `new EventOption(event, Proceed, "PROCEED", DisableOnChosen:false, IsProceed:true)` 的按钮 —— **它不在模型列表里**，所以按「界面上真有那个按钮」把它也当候选发出去 | `NEventRoom::SetOptions` IL |
| 按钮节点 | `NEventLayout.OptionButtons : IEnumerable<NEventOptionButton>`（= `_optionsContainer` 的子节点），`NEventOptionButton.Option`（public）/`Index`（private）/`FlashConfirmation()`（public Task） | `MetaPriv` |

**`/request` 长这样**

```jsonc
{
  "source": "screen",
  "selector": { "state": "active", "...": "..." },
  "pending": {
    "id": 1732,
    "kind": "event",
    "prompt": "事件「鸟面神龛」：选择一个选项",
    "min": 1,
    "max": 1,
    "requireConfirm": false,
    "cancelable": false,
    "caller": "NEventRoom",
    "confidence": "high",
    "animated": true,
    "animation": "应答时调 NEventRoom.OptionButtonClicked(option, index)……",
    "route": "event",
    "candidates": [
      { "key": "献上金币#1", "title": "献上金币", "upgraded": false, "cost": -1,
        "enabled": true,  "isProceed": false, "willKillPlayer": false,
        "optionId": "OFFER_GOLD", "detail": "失去 50 金币，获得一件遗物。" },
      { "key": "离开#1", "title": "离开", "upgraded": false, "cost": -1,
        "enabled": true,  "isProceed": true,  "willKillPlayer": false,
        "optionId": "PROCEED", "detail": "" }
    ]
  }
}
```

- `candidates[].enabled` / `.isProceed` / `.willKillPlayer` / `.optionId`（`TextKey`）/ `.detail`
  是**只给事件候选新增**的字段；`enabled = !EventOption.IsLocked`。
- prompt 用「事件『标题』：选择一个选项」，标题读不到才用兜底句。
- **什么时候才发这条请求**：在事件房间且房间可见；`CurrentOptions` 非空（或事件已结束、界面上
  只剩那个合成 PROCEED 按钮）；选项按钮里至少有一个**可见**（`Layout.OptionButtons` 全不可见 =
  内嵌战斗/换页动画中，这时不发）；地图没打开（点过「继续」之后 `NEventRoom` 还在树上，
  但地图已经开了）；并且至少有一项不是 `IsLocked`（全锁住时游戏对任何点击都 `return`，
  发了也答不对）。

**`/respond` 怎么应用**

1. `Validate`（复用）：id 对、数量 `1..1`、key 逐字命中候选。
2. 先读现场 `EventView`：`CurrentOptions` 的 `TextKey`+状态位+事件描述文本、`IsFinished`、
   是否在战斗、`Layout.OptionButtons` 数量/可见数。
   - 这个选项已经 `WasChosen` → `ok:false`，`how:"already-chosen"`。
3. `IsProceed` 的候选 → 直接 `option.Chosen()`（拿到 Task 才能等；`OptionButtonClicked` 对它是同一句），
   `how:"event-proceed"`；
   普通候选 → `NEventRoom.OptionButtonClicked(option, index)`（`index` 用**当下**的
   `CurrentOptions` 重算，不信候选里那份旧下标），`how:"event-option-button"`，然后等
   `EventSynchronizer.AwaitPendingOptionTasks()`。
4. 顺手让那个按钮播自己的确认反馈 `NEventOptionButton.FlashConfirmation()`（best effort，
   按钮可能马上被换页销毁，失败只写日志）。
5. 最多等 `EventSettleMs = 1200ms`，然后核对（任一条命中才算推进，否则 `ok:false` 并写出前后事实）：
   选项集合/描述文本变了、`IsFinished` 变 true、事件房间没了、进了内嵌战斗
   （`CombatManager.IsInProgress` false→true）；`IsProceed` 的候选额外接受「地图真的打开了」
   （`NMapScreen.IsOpen`）。

**`/respond` 返回**

```jsonc
{
  "ok": true, "source": "screen", "route": "event",
  "applied": "献上金币#1", "how": "event-option-button", "optionId": "OFFER_GOLD",
  "animated": true, "waitedMs": 1310,
  "visual": "event-applied",
  "settled": true,
  "items": [ { "key": "献上金币#1", "optionId": "OFFER_GOLD", "isProceed": false,
               "how": "event-option-button", "visual": "event-applied", "settled": true, "ok": true } ],
  "next": "card-choice"          // 只有「选项先弹选牌」时才有
}
```

**连锁（事件选项 → 选牌 / 奖励）**

事件选项经常先 `CardSelectCmd.FromDeckForUpgrade/FromDeckForRemoval/...` 或发衍生奖励，
那一步是**下一个** request：单项最多等 `EventSettleMs`，超时且 selector 通道已经有待答请求时返回
`settled:false` + `next:"card-choice"`；后续走已有通道（selector 的 `kind=upgrade/remove/...`，
或界面扫描的 `route=grid`），事件在那一步完成之后才继续推进。

**清场**：事件房间从场景树消失后 `/request` 清掉 pending；被更高优先级请求顶掉也清。
事件结束时如果 `CurrentOptions` 空了但界面上还有那个合成 PROCEED 按钮，会把它作为
`isProceed:true` 的唯一候选发出去 —— 答它等于点界面上的「继续」（打开地图离开事件），
**不依赖** `RunManager.Proceed()`。

**仍未实测的点**

- `NEventRoom.OptionButtonClicked` 是 void：异常只会在 `ChooseLocalOption` → `ChooseOptionForEvent`
  里同步抛出（已 try/catch），而 `option.Chosen()` 的异常被 `TaskHelper.RunSafely` 吃掉，
  所以我们**拿不到**「事件选项执行失败」的直接信号，只能靠「现场有没有变化」判断。
- 共享事件（多人）走的是「投票」分支（`EventSynchronizer.IsShared`），单人剧情事件走
  `ChooseOptionForEvent`；投票那条路没有实机验证。
- 事件选项进入内嵌战斗时，`NEventRoom` 仍在树上、选项按钮被隐藏 —— 用「按钮可见数」当判据，
  但某些自定义事件布局（水晶球等）可能不用 `AddOptions`，那时 `ButtonCount == 0`，
  代码会退回「只看 `CurrentOptions`」的行为，没实机验证过。

### 7.5 宝箱通道（C5 `NTreasureRoom` + `TreasureRoomRelicSynchronizer`，**两阶段**，2026-10-23 落地）

`RequestApi.cs` 里的「宝箱通道」，和商店/营火通道同一套结构：一个 pending 请求 + 复用
`NextKey` / `Fail` / 包裹层+核心体 / 失败可重答，**不走** `CardSelectCmd`，也不点坐标。

宝箱房是**两个决定、两个阶段**，一次 `/respond` 只做一件事：

| 阶段 | `pending.stage` | 什么时候发 | `min`/`max` | `picks:[]` 的含义 |
|---|---|---|---|---|
| 1 | `chest-closed` | 在宝箱房、箱子**还没开**（`_hasChestBeenOpened == false`） | 1 / 1 | **不打开**（箱子保持关闭） |
| 2 | `relics` | 箱子已开 **且** 遗物格子已摆出来（`_hasChestBeenOpened && _isRelicCollectionOpen`） | 0 / 遗物数（单人恒 1） | **都不拿**，走游戏的「跳过」后 `/proceed` 离开 |

阶段 1 的候选是一个**动作**（`itemType: "Action"`，key 固定 `打开宝箱#1`），**不读 `CurrentRelics`**
—— 所以 agent 在决定开不开之前，看不到箱里是什么。阶段 2 才给出真正的遗物。

两个阶段是两个 `id`（`stage` 参与签名）：阶段 1 答完 → 箱子开、遗物摆出来 → **下一次** `/request`
才会拿到阶段 2。开箱动画期间（`_isRelicCollectionOpen` 还是 false）**不发**阶段 2 请求（候选还没摆出来）。

**类型核对结论（依据：`meta` 只读工具对 `sts2.dll` 的 metadata + IL）**

| 项 | 结论 | 依据 |
|---|---|---|
| 房间节点 | `MegaCrit.Sts2.Core.Nodes.Rooms.NTreasureRoom : Godot.Control`；**`NRun.Instance.TreasureRoom`** = 房间容器 `_roomContainer.CurrentScene` `isinst` 的结果（不在宝箱房时为 null，不会留僵尸节点） | `NRun::get_TreasureRoom`：`ldfld _roomContainer` → `get_CurrentScene` → `isinst NTreasureRoom`（与 `get_RestSiteRoom` 逐字节同构） |
| **「箱子开没开」读哪个字段** | `NTreasureRoom` 的 private bool **`_hasChestBeenOpened`**（`/request` 的 `pending.chestOpened` 就是它）；「遗物格子摆出来了」= private bool **`_isRelicCollectionOpen`**。这两个就是 `NTreasureRoom` 上唯一的两个 bool，没有同名属性 | `MetaPriv <sts2.dll> NTreasureRoom`：`field private bool _isRelicCollectionOpen` / `field private bool _hasChestBeenOpened`；`NTreasureRoom+PropertyName` 里也只是这两个名字（Godot 导出名） |
| 遗物数据源 | `RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics : IReadOnlyList<RelicModel>`（public prop，读 private 字段 `_currentRelics`） | `MetaPriv` 里 `TreasureRoomRelicSynchronizer` 的 `prop CurrentRelics` / `field private List<RelicModel> _currentRelics` |
| 遗物哪来的 | **进房间那一刻** `TreasureRoom.EnterInternal` 末尾调 `RunManager.TreasureRoomRelicSynchronizer.BeginRelicPicking()`：对每个玩家 `Hook.ShouldGenerateTreasure` → `RelicFactory.RollRarity` → `TryGetRelicForTutorial` → `RelicGrabBag.PullFromFront` → 拉空才 `RelicFactory.FallbackRelic` → `_currentRelics.Add(...)` | `Rooms.TreasureRoom+<EnterInternal>d__6::MoveNext`（末尾 `BeginRelicPicking`）、`TreasureRoomRelicSynchronizer::BeginRelicPicking` IL |
| 「已经拿过 / 空箱」怎么判 | `AwardRelics()` 之后的 `EndRelicVoting()` 把 `_currentRelics` 置 **null**；空箱走 `CompleteWithNoRelics()` 也置 null ⇒ `CurrentRelics == null`（或 `Count == 0`）= 没有要拿的，**不发请求**（两个阶段都不发，包括「要不要开箱」） | `EndRelicVoting`：`ldnull; stfld _currentRelics`；`CompleteWithNoRelics` 同样 |
| 遗物模型字段 | `RelicModel.Title : LocString`、`Id : ModelId`；描述是 private getter，沿用商店通道的容错读取 `DynamicText(model, "DynamicDescription", "Description")`（`ReadMember` 沿继承链取字段/属性） | `members.txt` / `MetaPriv Models.RelicModel` + 商店通道实测过的同一段代码 |
| 界面容器 | `NTreasureRoom` 的 private 字段 `_relicCollection : NTreasureRoomRelicCollection`；遗物格子 `NTreasureRoomRelicCollection._holdersInUse : List<NTreasureRoomRelicHolder>`，单人只摆 `SingleplayerRelicHolder` 一个（`Index = 0`） | `MetaPriv Nodes.Rooms.NTreasureRoom` / `Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicCollection` |
| **开箱入口** | `NTreasureRoom.OpenChest()`（private，返回 `Task`）—— `OnChestButtonReleased` 就是 `TaskHelper.RunSafely(OpenChest())` + `_chestButton.Disable()` | `NTreasureRoom::OnChestButtonReleased` IL：`call OpenChest` → `TaskHelper.RunSafely` → `_chestButton.Disable()` |
| `OpenChest` 内部（= 阶段 1 干的活） | banner 滑入 → 关「前进」→ `UpdateChestSkin(false)` + `Act.ChestOpenSfx` → `await _room.DoNormalRewards()`（返回金币数 → 金光粒子）→ `await DoExtraRewardsIfNeeded()` → `_relicCollection.InitializeRelics()`（摆格子）+ `AnimIn(_chestNode)` → **`_isRelicCollectionOpen = true`** →（单人）`_proceedButton.UpdateText(SkipLoc)` + `EnableSkipAfterDelay(2.5f)` → **`_hasChestBeenOpened = true`** → **`await RelicPickingBegan()`（卡在这里等玩家拿/跳过）** → 之后才 `RelicPickingFinished()` → `_isRelicCollectionOpen = false` + `UpdateText(ProceedLoc)` + 开「前进」 | `NTreasureRoom+<OpenChest>d__30::MoveNext` IL（两个 flag 的写入顺序、`EnableSkipAfterDelay(2.5f)`、两个 await 都在里面） |
| **领取入口** | `NTreasureRoomRelicCollection.PickRelic(holder)`（private）：`if (Time.GetTicksMsec() - _openedTicks <= 200) return;` → `RunManager.Instance.TreasureRoomRelicSynchronizer.PickRelicLocally(holder.Index)` | `PickRelic` IL（4 行，就是这两句）；`<InitializeRelics>b__21_0`：`PickRelic(get_SingleplayerRelicHolder())` —— 格子 `Released` 信号接的就是它 |
| 领取真正干活的那一句 | `PickRelicLocally(int? index)`：`_currentRelics == null` 抛 `InvalidOperationException("Attempted to pick relic while relic picking is not active!")` → `_predictedVote = {index, voteReceived=true}` → `ActionQueueSynchronizer.RequestEnqueue(new PickRelicAction(localPlayer, index))` → `VotesChanged` | `PickRelicLocally` IL |
| 票到齐之后 | `OnPicked`：所有票 `voteReceived` → `AwardRelics()`（构造 `RelicPickingResult`，发 `RelicsAwarded` 信号）→ `EndRelicVoting()` | `OnPicked` / `AwardRelics` / `EndRelicVoting` IL |
| 遗物进背包的那一步 | `RelicsAwarded` → `NTreasureRoomRelicCollection.OnRelicsAwarded` → `TaskHelper.RunSafely(AnimateRelicAwards)` → **`RelicCmd.Obtain(relic.ToMutable(), player)`** + `NRelicInventory.AnimateRelic(...)`（遗物飞进遗物栏），中间夹着 `Cmd.Wait`；这个协程开头 `SetResult` 掉了 `_relicPickingBeganTaskCompletionSource`、结尾 `SetResult` 掉 `_relicPickingCompleteTaskCompletionSource`（= 解开 `OpenChest` 的两个 await） | `NTreasureRoomRelicCollection+<AnimateRelicAwards>d__30::MoveNext` IL |
| **「跳过」到底做了什么** | `SkipRelicLocally()` = `PickRelicLocally(null)`（**不是**只有一句 `_singleplayerSkipped = true`）。`PickRelicLocally(null)` 照样 `RequestEnqueue(new PickRelicAction(localPlayer, null))`；排队执行到的 `OnPicked(player, null)` 里：**单人 + index 没值** → `_singleplayerSkipped = true` 然后 **return**（不发 `RelicsAwarded`、不 `EndRelicVoting`，`_currentRelics` 仍非 null）。联机则进投票/`AwardRelics` 那条路 | `SkipRelicLocally` IL（两行：`ldloca.s 0; initobj` → `call PickRelicLocally`）、`OnPicked` IL（`HasValue == false && Players.Count == 1 → _singleplayerSkipped = true; ret`） |
| **跳过的真正收尾** | `TreasureRoom.Exit()` → `RunManager.Instance.TreasureRoomRelicSynchronizer.OnRoomExited()` → 读到 `_singleplayerSkipped` 才 `EndRelicVoting()`（`_currentRelics = null`、`_singleplayerSkipped = false`）。`Exit` 是在**地图上走到下一个节点**时才调用的 | `Rooms.TreasureRoom::Exit` IL（只有 `get_TreasureRoomRelicSynchronizer → OnRoomExited → Task.CompletedTask`）、`OnRoomExited` IL |
| **玩家点「跳过」按钮走的路** | `NProceedButton.ForceClick()` = `OnRelease()` + `EmitSignal(Released)`；`NTreasureRoom._Ready` 把 `_proceedButton.Released` 接到 **`OnProceedButtonPressed`**：`if (_proceedButton.IsSkip) { SkipRelicLocally(); if (Players.Count == 1) { NMapScreen.SetTravelEnabled(true); ProceedFromTerminalRewardsScreen(); } return; }` 否则直接 `ProceedFromTerminalRewardsScreen()`。`IsSkip` 由 `UpdateText(LocString)` 设置（`LocEntryKey == SkipLoc.LocEntryKey`） | `NProceedButton::ForceClick` / `UpdateText` / `NTreasureRoom::_Ready`（`Connect(Released, OnProceedButtonPressed)`）/ `OnProceedButtonPressed` IL |
| `ProceedFromTerminalRewardsScreen` 是什么 | **不是退房间**：`CurrentRoomCount > 1` 时（事件内嵌战斗则 `ResumePreviousRoom`，否则）`NMapScreen.SetTravelEnabled(true)` + `NMapScreen.Open(false)`；`CurrentRoomCount <= 1` 时只 `NMapScreen.Open(false)`。房间真正的退出发生在地图旅行那一步（见上一条 `TreasureRoom.Exit`） | `RunManager+<ProceedFromTerminalRewardsScreen>d__199::MoveNext` IL |
| 开箱前「前进」按钮是关着的 | `OnActiveScreenChanged`（订阅 `ActiveScreenContext.Updated`）：`_hasChestBeenOpened ? _proceedButton.Enable() : _proceedButton.Disable()` | `NTreasureRoom::OnActiveScreenChanged` / `_EnterTree`（`add_Updated(OnActiveScreenChanged)`）IL |

**`/request` 阶段 1 长这样（箱子还没开，看不到内容）**

```jsonc
{
  "source": "screen",
  "pending": {
    "id": 2001,
    "kind": "treasure",
    "stage": "chest-closed",                 // ★ 新增字段
    "prompt": "宝箱：要打开吗",
    "min": 1, "max": 1,
    "requireConfirm": false,
    "cancelable": true,                      // picks:[] = 不打开
    "caller": "NTreasureRoom",
    "confidence": "high",
    "animated": true,
    "animation": "阶段 1：应答「打开宝箱」时用游戏自己的开箱入口 NTreasureRoom.OpenChest()……",
    "route": "treasure",
    "chestOpened": false,
    "candidates": [
      { "key": "打开宝箱#1", "title": "打开宝箱", "upgraded": false, "cost": -1,
        "itemType": "Action", "enabled": true, "detail": "打开后会看到里面的东西" }
    ]
  }
}
```

**`/request` 阶段 2 长这样（遗物已经摆出来）**

```jsonc
{
  "source": "screen",
  "pending": {
    "id": 2002,
    "kind": "treasure",
    "stage": "relics",                       // ★ 新增字段
    "prompt": "宝箱：要拿走哪件（picks:[] = 都不拿，直接离开）",
    "min": 0,                                // 不拿永远合法
    "max": 1,                                // = 宝箱里的遗物数（单人恒为 1）
    "requireConfirm": false,
    "cancelable": true,
    "caller": "NTreasureRoom",
    "confidence": "high",
    "animated": true,
    "animation": "阶段 2：应答时调遗物格子自己的领取入口 NTreasureRoomRelicCollection.PickRelic(holder)……",
    "route": "treasure",
    "chestOpened": true,
    "candidates": [
      { "key": "燃烧之血#1", "title": "燃烧之血", "upgraded": false, "cost": -1,
        "itemType": "Relic", "enabled": true, "treasureIndex": 0,
        "detail": "每场战斗结束时回复 6 点生命。" }
    ]
  }
}
```

- `candidates[].itemType` / `.enabled` / `.detail` 两个阶段都有；`treasureIndex` 只有阶段 2 的遗物有
  （= 在 `CurrentRelics` 里的下标 = 遗物格子的 `Index`，应答时用它重找格子）。
- key = `标题#同标题序号`，与卡牌 key 同一套 `NextKey`；阶段 1 是固定的 `打开宝箱#1`。
- **什么时候才发**：`NRun.Instance.TreasureRoom` 非 null（在宝箱房）、地图没打开、
  `CurrentRelics` 非空、**且没有投过「跳过」**（`_singleplayerSkipped == false`）。
  箱子开没开决定阶段；开了但 `_isRelicCollectionOpen` 还是 false（开箱动画没播完）时**这一轮不发**。

**`/respond` 怎么应用**

阶段 1（`stage: "chest-closed"`）：

1. 校验（只认一个动作，不走 `Validate` 的 0..max）：`picks` 长度 > 1 → 错；长度 1 时必须逐字命中
   `打开宝箱#1`；**`picks: []` 合法**（= 不打开，箱子保持关闭）。
2. `picks: []` → 什么都不动，返回 `{"ok":true,"opened":false,"visual":"chest-skipped","taken":0}`。
   下一次 `/request` 会再发一次阶段 1（箱子还是关的）。
3. `picks: ["打开宝箱#1"]` → 反射调 `NTreasureRoom.OpenChest()`，**不 await 它**
   （它内部 `await RelicPickingBegan()` 要等玩家拿走/跳过才结束，await 到底会让 `/respond` 永不返回），
   然后轮询 `_isRelicCollectionOpen == true`（最多 `TreasureOpenMs = 3000ms`），再多留
   `TreasureOpenGapMs = 340ms` 让开盖/金光/格子淡入播开。箱子按钮 `_chestButton._isEnabled == false`
   （玩家自己已经在开箱）时**不调第二次** `OpenChest`，只等格子摆好。
   **这一步不拿任何遗物**；返回
   `{"ok":true,"opened":true,"visual":"chest-opened","waitedMs":…,"chestOpened":true,"taken":0}`。

阶段 2（`stage: "relics"`）：

1. `Validate`（复用）：id 对、数量 `0..max`、key 逐字命中候选、不重复。
2. `picks` 有值 → ① 按 `candidate.treasureIndex` 在 `_holdersInUse` 里找 `Index` 相同的
   `NTreasureRoomRelicHolder`，反射调 `NTreasureRoomRelicCollection.PickRelic(holder)`，
   `how: "treasure-holder"`；找不到格子时退化成
   `TreasureRoomRelicSynchronizer.PickRelicLocally(index)`，`how: "treasure-model"`。
   ② 每件之间 `Task.Delay(TreasureGapMs = 420ms)`。
   ③ **核对**（最多等 `TreasureSettleMs = 2600ms`，轮询 150ms）：本地玩家 `Player.Relics` 里
   **出现了这件遗物**（`ModelId` 相同）或**遗物数 +1**，命中一条才算拿到；否则 `ok:false`
   并把前后事实写进 error。幂等保护：应答前这件遗物已经不在 `CurrentRelics` 里 →
   `how:"already-taken"`、`ok:false`、不重复领。
3. `picks: []` → **都不拿**，走游戏自己的「跳过」：
   ① `TreasureRoomRelicSynchronizer.SkipRelicLocally()`（= 玩家点「跳过」时
   `OnProceedButtonPressed` 的 IsSkip 分支第一句），**不硬改任何状态**；
   ② 轮询 `_singleplayerSkipped == true`（或 `CurrentRelics` 已经清掉）最多
   `TreasureSkipSettleMs = 1500ms` —— `PickRelicAction` 是排队执行的，不能当同步调用；
   ③ 轮询「前进」按钮可用（`_proceedButton._isEnabled`）最多 `TreasureProceedReadyMs = 3500ms`
   —— 单人开箱时 `EnableSkipAfterDelay(2.5f)` 到点才 `Enable()`。超时**不算失败**，只是照实报
   `proceedReady:false`。
   ④ 返回 `{"ok":true,"stage":"relics","taken":0,"visual":"treasure-skipped","skipped":true,
   "proceedReady":true,"proceedIsSkip":true,"next":"…用 /proceed…"}`。
   **之后 `/request` 不再发宝箱请求**（`_singleplayerSkipped` 期间直接不发），
   `/proceed`（按同一个 `NProceedButton`）→ 它自己再调一次 `SkipRelicLocally`（无害）→
   `ProceedFromTerminalRewardsScreen`（开地图 + 放开移动）→ 在地图上走到下一个节点时
   `TreasureRoom.Exit` → `OnRoomExited` → `EndRelicVoting` 收尾。
   ⇒ **`picks:[]` 之后「前进」按钮是能用的**（除非 2.5s 还没到，那就等一会儿）。

**`/respond` 返回（阶段 2 拿遗物）**

```jsonc
{
  "ok": true, "source": "screen", "route": "treasure", "stage": "relics",
  "applied": "燃烧之血#1", "how": "treasure-holder",
  "animated": true, "waitedMs": 2140,
  "visual": "treasure-taken",       // 走了格子自己的入口 = treasure-taken；只走同步器 = treasure-model-delay
  "settled": true,
  "chestOpened": true,
  "taken": 1, "relics": 12, "gold": 148,
  "items": [ { "key": "燃烧之血#1", "itemType": "Relic", "how": "treasure-holder",
               "visual": "treasure-taken", "settled": true, "ok": true,
               "note": "遗物已经进了遗物栏（11 → 12）" } ]
}
```

**清场**：`NRun.Instance.TreasureRoom` 变 null（离开宝箱房）、地图打开
（`NMapScreen.IsOpen`）、`CurrentRelics` 空（空箱 / 已经拿走）、或已经投过「跳过」时，
`/request` 直接清掉 pending；被更高优先级的请求（选牌 / 手牌 / 奖励列表）顶掉时也会清。
不留僵尸请求。

**仍未实测的点**

- `NTreasureRoom.OpenChest()` / `PickRelic(holder)` 的反射调用在「两阶段」之后没有实机复测
  （IL 已确认入口；`OpenChest` 返回 `Task`，本实现只推起来不 await）。
- `PickRelic(holder)` 里那句 `if (Time.GetTicksMsec() - _openedTicks <= 200) return;` ——
  `_openedTicks` 在 metadata 里没找到写入点（`_Ready`/`AnimIn` 都没有），值恒为 0 时判据恒过；
  本实现额外在「格子摆好」之后停 340ms，不依赖这个判据。
- `SkipRelicLocally()` → `_singleplayerSkipped` 的落地时间按「一个 action queue 回合」估的
  （轮询上限 1.5s），实机没量过；`EnableSkipAfterDelay` 的 2.5s 也没实机量过（按 IL 常量写的上限）。
- 多人（2~4 人）宝箱是「投票 + `RelicPickingFight`」流程，本通道按单人的「一件遗物一个格子」
  实现：候选会按 `CurrentRelics` 全列出来，但多人的 `RelicPickingResult`（`GenerateRelicFight`）
  没有实机验证；`_singleplayerSkipped` 只在单人分支置位，多人下 `picks:[]` 走的是投票清空
  `CurrentRelics` 那条路，未验证。
- 空箱（`CurrentRelics.Count == 0`）时两个阶段都**不发请求**（沿用旧行为），
  所以「空箱还要不要打开看金币」这件事没有协议覆盖。

### 7.6 药水目标通道（`PotionModel.TargetType` + `PotionUsage`，2026-10-23 落地）

这条通道解决两件事：**「需要目标的药水在非战斗场景里怎么选目标」** 和
**「再也别出现 `ok:true` + `target:null` 的静默成功」**。

**先纠正一个旧实现的前提（IL 依据）**

| 旧假设 | 真相 | 依据 |
|---|---|---|
| 商人是一个 `Creature`，扫场景树找类名含 `"MerchantCharacter"` 的节点、反射它的 `Creature` 属性就能拿到 | `NMerchantCharacter : Godot.Node2D` 上**根本没有** `Creature` 成员（它只是店里站着的角色视觉），所以那段反射永远返回 `null` | `members.txt` 里 `NMerchantCharacter` 只有 `_Ready/PlayAnimation/.ctor` |
| `TargetType.AnyEnemy` 是「需要目标」的全部 | 游戏自己的判定是 `TargetType ∈ {AnyEnemy, TargetedNoCreature} \|\| CanThrowAtAlly()`（`NPotionHolder.UsePotion`）；而 `TargetedNoCreature`（污浊药水在非战斗里的 `TargetType`）的目标是**商人按钮**，交给模型的 `target` 本来就是 `null` | `NPotionHolder::<UsePotion>d__40::MoveNext` IL、`FoulPotion::get_TargetType`（战斗=3 `AllEnemies`，非战斗=8 `TargetedNoCreature`） |
| 「商人在场」= 面板开着 | 反了：`FoulPotion.GetFoulPotionMerchantTarget(room)` 只在 `MerchantInventory.IsOpen == false` 时返回商人按钮；面板开着时游戏自己就把这瓶药禁用了（`PassesCustomUsabilityCheck`） | `FoulPotion::GetFoulPotionMerchantTarget` IL（`callvirt NMerchantInventory::get_IsOpen` + `brtrue` 到返回 null）、`FoulPotion::get_PassesCustomUsabilityCheck` IL、`NPotionPopup::_Ready`（`if (!PassesCustomUsabilityCheck) _useButton.Disable()`） |

**`/potion/use` 现在分三步（`Potions.Use`）**

1. **问游戏「这瓶现在能不能用」**（主线程 `Potions.Probe`，不改任何状态）：
   - 解析药水（key / uid / index，老规则不变）；解析不出来 → 明确失败 + `availableKeys`；
   - **战斗中**：沿用老的 `target` 下标法（`CombatState.HittableEnemies[target]`）。只补一条：需要目标
     （`AnyEnemy` / `TargetedNoCreature` / `CanThrowAtAlly`）却没给下标，或下标越界 → 明确失败并列出
     `0=名字、1=名字…`，不再拿 `null` 往下走；
   - **非战斗**：`CanUseNow`，规则照抄 `NPotionPopup._Ready` / `RefreshButtons`：
     `PotionUsage`（`AnyTime` 能用 / `CombatOnly` 只在战斗里 / `Automatic` 不能手动用 / `None` 不该存在）
     + `PotionModel.PassesCustomUsabilityCheck` + 角色活着 + `Player.CanRemovePotions` + `!IsQueued`。
2. 不能用 → **直接失败、不消耗、不入队、不建请求**：

   ```jsonc
   { "ok": false, "usable": false,
     "error": "这瓶药水在当前场景用不了（游戏不允许）：只有在战斗里才能用（当前不是战斗，没有可作用的目标）" }
   ```
3. 能用且**需要玩家选目标**（只在非战斗里走到这一步）→ **不立刻喝**，建一条 `kind:"potion"` 的 pending：

   ```jsonc
   // POST /potion/use {"potion":"污浊药水#1"}（不带 target）
   { "ok": false, "needTarget": true, "used": null, "confirmed": false,
     "request": {
       "id": 1902, "kind": "potion", "stage": "target",
       "prompt": "污浊药水：选一个目标",
       "min": 1, "max": 1, "requireConfirm": false, "cancelable": true,
       "caller": "PotionModel", "confidence": "high", "route": "potion",
       "animated": true, "animation": "……EnqueueManualUse……轮询药水槽……",
       "potion": { "key": "污浊药水#1", "title": "污浊药水", "description": "……" },
       "candidates": [
         { "key": "商人#1", "title": "商人", "upgraded": false, "cost": -1,
           "itemType": "Target", "enemy": true, "enabled": true },
         { "key": "取消#1", "title": "取消", "upgraded": false, "cost": -1,
           "itemType": "Cancel", "cancel": true, "enabled": true }
       ],
       "next": "picks:[\"商人#1\"] = 对这瓶的目标用掉；picks:[] 或 picks:[\"取消#1\"] = 不喝（药水留在槽里）"
     } }
   ```

   同一个 pending 也会出现在 `GET /request`（`source:"screen"`），优先级在宝箱之后、商店之前。

   没有任何合法目标时**不建请求**，直接失败（这就是任务里要求的显式失败）：

   ```jsonc
   { "ok": false, "usable": false,
     "error": "这瓶药水需要目标，但当前场景没有可选的商人/敌人目标（当前房间不是商店，也不是假商人事件）" }
   ```

4. `POST /respond {"id":1902,"picks":["商人#1"]}` → `Potions.UseResolved` 复查「药水还在槽里 + 商人还在」
   → `PotionModel.EnqueueManualUse(null)`（= 界面「扔出去」按钮最后那一步；`TargetedNoCreature` 的 target
   本来就是 null，游戏自己在 `FoulPotion.OnUse` 里认商人按钮：`ShowPotionVfx(MerchantButton)` +
   `NMerchantRoom.FoulPotionThrown`（音效/台词/震屏）+ `PlayerCmd.GainGold`）→ 轮询药水槽，那一瓶真的消失才 ok：

   ```jsonc
   { "ok": true, "source": "screen", "route": "potion", "stage": "target",
     "applied": "污浊药水 → 商人", "how": "potion-target",
     "animated": true, "waitedMs": 1620,
     "visual": "potion-used", "used": "污浊药水", "target": "商人",
     "confirmed": true, "canceled": false }
   ```

5. `picks:[]` 或 `picks:["取消#1"]` → 不喝、药水留在槽里：
   `{"ok":true,"route":"potion","applied":"（取消，没喝）","how":"potion-cancel","canceled":true,"visual":"potion-canceled"}`
6. 任何 `ok:false` 都把请求挂回 `_potionPending`（`RestorePotionAfterReject`），能改一道答案再答同一个 id。

**签名/清场**：药水请求没有签名（id 在 `BeginPotionTargetRequest` 里一次分配）；药水不在槽里了、
商人目标不在了、这一局结束了 → `/request` 直接清掉 pending（并写日志说明原因）。被更高优先级请求
（选牌 / 手牌 / 奖励 / 宝箱）顶掉时也清。

**商店里只有污浊药水能对商人用（模型层依据）**

`TargetType == TargetedNoCreature` 的药水只有 `POTION.FOUL_POTION` 一种；它的 `Usage` 是
`PotionUsage.AnyTime`（= 2），恰好是「非战斗也能用」的那一类。其余药水（力量 / 火焰 / 迅捷 …）的
`Usage` 都是 `CombatOnly`（= 1），在商店里 `NPotionPopup.RefreshButtons` 会把使用按钮禁用，
`/potion/use` 现在会明确拒绝（`usable:false`）。另外 `BloodPotion` / `EntropicBrew` / `FruitJuice`
也是 `AnyTime`（回血 / 加最大生命），商店里也能喝——但它们**不**以商人为目标
（`TargetType` 不是 `TargetedNoCreature`），所以 `canTargetMerchant:false`。

> 全量清单（按 IL 扫 `get_Usage`）：`AnyTime` = `BloodPotion` / `EntropicBrew` / `FoulPotion` /
> `FruitJuice`；`Automatic` = `FairyInABottle`；其余 61 种全是 `CombatOnly`。

**仍未实测的点**

- `Potions.UseResolved` 走的 `EnqueueManualUse(null)` 在商店里到底把金币加了多少、商人台词播没播，
  没实机核对（用户当时正在玩、不允许调接口）。只按 IL 确认了 `FoulPotion.OnUse` 的两个分支
  （`NRun.Instance.MerchantRoom != null` → VFX + 台词；`EventRoom is FakeMerchant` → VFX + 全体
  `FakeMerchant.FoulPotionThrown`）都会走到 `PlayerCmd.GainGold`。
- 「商店面板打开时污浊药水不可用」是按 `PassesCustomUsabilityCheck` 推的：面板开着 →
  `GetFoulPotionMerchantTarget` 返回 `(null, null)` → 检查为 false → 使用按钮被禁。实机没试过
  「面板开着直接喝」到底会不会真的被拦（我们的 `/potion/use` 会先拒绝，走不到那一步）。
- `Player.CanRemovePotions == false` 的三个来源（`RanwidTheElder` / `StoneOfAllTime` /
  `TheFutureOfPotions` 三个事件的 `BeforeEventStarted`）只按 IL 认了，没在事件里试过。

### 7.7 菜单通道（开局：`NMainMenu` / `NSingleplayerSubmenu` / `NCharacterSelectScreen`，2026-10-23 落地）

以前开局只能：① `/screen` 枚举控件 + `/tap` 一个个点（主菜单 → 单人 → 标准 → 角色格 → 开始），
或者 ② 干脆绕过菜单、`POST /start` 从模型层硬造一局。前者是模拟点击（有 uid 过期/点空的坑），
后者跳过了菜单和大厅（`StartRunLobby`）。现在补上**语义通道**：`kind:"menu"`，四段 stage，
一次 `/respond` 只做一件事，按「页面变了没有」核对。

| stage | 什么时候 | 应答要交什么 | 依据（都在官方节点上） |
|---|---|---|---|
| `main` | 主菜单（没有子菜单打开） | 这一页的一个按钮 | `NMainMenu.MainMenuButtons : NButton[]`（8 个：继续 / 放弃本局 / 单人 / 多人 / 时间线 / 设置 / 图鉴 / 退出） |
| `mode` | 单人子菜单 | 标准模式 / 每日挑战 / 自定义 | `NSingleplayerSubmenu` 的 private `_standardButton` / `_dailyButton` / `_customButton` |
| `character` | 角色选择页 | 一个角色 key | `NCharacterSelectScreen._charButtonContainer` 里的 `NCharacterSelectButton`（`IsRandom` / `Character` / `IsLocked` / `IsSelected`） |
| `submenu` | 其它子菜单页（多人/图鉴/时间线/设置/每日/自定义…） | 这一页的某个按钮或「返回」 | 子菜单里的 `N*SubmenuButton` + `NBackButton` |
| `confirm` | **确认弹窗**（目前只有「放弃本局」的 `NAbandonRunConfirmPopup`） | 「好的」或「取消」 | `NAbandonRunConfirmPopup._verticalPopup`（`NVerticalPopup`）的 `YesButton` / `NoButton` |

**类型核对结论（依据：`meta` 只读工具对 `sts2.dll` 的 metadata + IL）**

| 项 | 结论 | 依据 |
|---|---|---|
| 主菜单节点 | `MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu : Godot.Control`。**必须按具体类型认**（`node is NMainMenu`）：`NMainMenuSubmenuStack` 的类名里也含 `NMainMenu`，按类名模糊匹配会认错节点 | `types.txt` |
| 主菜单按钮 | `prop NButton[] MainMenuButtons`（`get_MainMenuButtons` IL 里 `newarr 8` + 依次取 `_continueButton` / `_abandonRunButton` / `_singleplayerButton` / `_multiplayerButton` / `_timelineButton` / `_settingsButton` / `_compendiumButton` / `_quitButton`） | `NMainMenu::get_MainMenuButtons` IL |
| 按钮标题从哪来 | 每个 `NMainMenuTextButton` 在 `_Ready` 里拿到场景节点后 `SetLocalization("CONTINUE"/"SINGLE_PLAYER"/…)`，文字进 `public MegaLabel label`；`NSubmenuButton` 是 `_title`（MegaLabel）+ `_description`（MegaRichTextLabel） | `NMainMenu::_Ready` IL、`NSubmenuButton::RefreshLabels` IL（`main_menu_ui.<前缀>.title`） |
| 单人子菜单 | `NSingleplayerSubmenu : NSubmenu`，三个 `NSubmenuButton` 字段；`_Ready` 里 `Released` 分别连到 `OpenCharacterSelect` / `OpenDailyScreen` / `OpenCustomScreen` | `NSingleplayerSubmenu::_Ready` IL、MetaPriv 字段表 |
| 打开角色选择 | `OpenCharacterSelect` = `stack.GetSubmenuType<NCharacterSelectScreen>()` → **`InitializeSingleplayer()`**（里面 `new NetSingleplayerGameService()` + `new StartRunLobby()` 塞进 `_lobby`）→ `Push(screen)` | `NSingleplayerSubmenu::OpenCharacterSelect` / `NCharacterSelectScreen::InitializeSingleplayer` IL |
| 角色卡的三个属性 | `prop bool IsRandom` / `prop CharacterModel Character`（**`CharacterModel.Title` 是 `LocString`，不是 string**）/ `prop bool IsLocked` / `prop bool IsSelected` | `members.txt` + `MetaPriv CharacterModel` |
| 角色卡从哪来 | `InitCharacterButtons` 遍历 `ModelDb.AllCharacters` 各 `Instantiate` 一个 `NCharacterSelectButton`（`AddChildSafely(_charButtonContainer)`，节点名 `<Id.Entry>_button`），最后再造一张 `IsRandom` 的「随机」卡 | `NCharacterSelectScreen::InitCharacterButtons` IL |
| 选中角色 | `NCharacterSelectButton.Select()`：`_isSelected = true` → `delegate.SelectCharacter(this, _character)` → `NCharacterSelectScreen.SelectCharacter`（记住 `_selectedButton`，**`_embarkButton.Enable()`**，刷新信息面板/背景） | `NCharacterSelectButton::Select` / `NCharacterSelectScreen::SelectCharacter` IL |
| 「开始」按钮 | `NCharacterSelectScreen` 的 private `_embarkButton : NConfirmButton`，`Released` 连到 private `OnEmbarkPressed()`：`_lobby.SetReady(true)` → 关掉所有角色卡/返回 → 等大厅开局 | `NCharacterSelectScreen::_Ready` IL（`Connect(Released, OnEmbarkPressed)`）、`OnEmbarkPressed` IL |
| 按钮的「点一下」是什么 | `NClickableControl.ForceClick()` = `OnRelease()` + `EmitSignal(Released)`。`Released` 就是各界面 `_Ready` 里 `Connect` 的那个信号 ⇒ 调它等于玩家真点一下，**不是**坐标点击 | `NClickableControl::ForceClick` IL |
| 「放弃本局」的确认框 | 点 `NMainMenu._abandonRunButton` → `OnAbandonRunButtonPressed` = `NModalContainer.Instance.Add(NAbandonRunConfirmPopup.Create(this))`。**点完菜单页本身不变** | `NMainMenu::OnAbandonRunButtonPressed` IL |
| 确认框的结构 | `private _verticalPopup : NVerticalPopup`；`_Ready` 里 `SetText(ABANDON_RUN_CONFIRMATION.header/body)`、`InitYesButton(GENERIC_POPUP.confirm, OnYesButtonPressed)`、`InitNoButton(GENERIC_POPUP.cancel, OnNoButtonPressed)` | `NAbandonRunConfirmPopup::_Ready` IL |
| 确认框的按钮 | `NVerticalPopup.YesButton` / `.NoButton : NPopupYesNoButton : NButton`，文字在各自的 `_label`（`MegaLabel`） | MetaPriv `NVerticalPopup` / `NPopupYesNoButton` |
| 点「好的」之后 | `OnYesButtonPressed` = `_mainMenuNode.AbandonRun()`（为 null 时 `RunManager.Instance.Abandon()`）+ `QueueFreeSafely()`。`AbandonRun` 里 `SaveManager.DeleteCurrentRun()` + `RefreshButtons()` | `NAbandonRunConfirmPopup::OnYesButtonPressed` / `NMainMenu::AbandonRun` IL |
| 子菜单栈 | `NMainMenu.SubmenuStack.SubmenusOpen` + `Peek()`；`NSubmenuStack.Push/Pop/Peek` | `members.txt` |
| 子菜单返回键 | `MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton : NButton` | `types.txt` |

**`/request` 长这样**（`stage:"main"`）

```jsonc
{
  "source": "screen",
  "selector": { "state": "active", "...": "..." },
  "pending": {
    "id": 2001, "kind": "menu", "stage": "main",
    "prompt": "主菜单：选择要做什么",
    "min": 1, "max": 1, "requireConfirm": false, "cancelable": false,
    "caller": "NMainMenu", "confidence": "high",
    "animated": false,                  // mod 不补动画：播的是游戏自己的页面切换
    "route": "menu",
    "candidates": [
      { "key": "继续#1",     "title": "继续",     "enabled": false, "itemType": "Menu", "buttonType": "NMainMenuContinueButton" },
      { "key": "放弃本局#1", "title": "放弃本局", "enabled": false, "itemType": "Menu", "buttonType": "NMainMenuTextButton" },
      { "key": "单人模式#1", "title": "单人模式", "enabled": true,  "itemType": "Menu", "buttonType": "NMainMenuTextButton" },
      { "key": "多人模式#1", "title": "多人模式", "enabled": true,  "itemType": "Menu", "buttonType": "NMainMenuTextButton" },
      { "key": "设置#1",     "title": "设置",     "enabled": true,  "itemType": "Menu", "buttonType": "NMainMenuTextButton" }
    ]
  }
}
```

- key = `标题#同标题序号`（和其他通道同一套 `NextKey`）；标题取自按钮**自己身上的 Label**，
  不是按类名猜的。`enabled` 是 `NClickableControl.IsEnabled`（没存档时「继续」就是 false）。
- **「退出」不在候选里**：按 `NMainMenu._quitButton` 的**引用**剔除 —— 协议不提供关游戏这条路。
- `candidates[].itemType` / `.buttonType` 只给菜单候选新增。

**`stage:"mode"`**

```jsonc
{ "id": 2002, "kind": "menu", "stage": "mode",
  "prompt": "单人模式：选择要玩哪种模式", "min": 1, "max": 1, "cancelable": false,
  "caller": "NSingleplayerSubmenu", "route": "menu",
  "candidates": [
    { "key": "标准模式#1", "title": "标准模式", "enabled": true,  "itemType": "Menu", "buttonType": "NSubmenuButton" },
    { "key": "每日挑战#1", "title": "每日挑战", "enabled": false, "itemType": "Menu", "buttonType": "NSubmenuButton" },
    { "key": "自定义#1",   "title": "自定义",   "enabled": true,  "itemType": "Menu", "buttonType": "NSubmenuButton" }
  ] }
```

**`stage:"character"`**

```jsonc
{ "id": 2003, "kind": "menu", "stage": "character",
  "prompt": "选择角色", "min": 1, "max": 1, "cancelable": false,
  "caller": "NCharacterSelectScreen", "route": "menu",
  "selected": "silent#1",            // ★ 已经选中的那张（没有就不出现）
  "candidates": [
    { "key": "ironclad#1",   "title": "铁甲战士", "enabled": true,  "itemType": "Character", "characterId": "ironclad",   "random": false, "locked": false },
    { "key": "silent#1",     "title": "静默猎手", "enabled": true,  "itemType": "Character", "characterId": "silent",     "random": false, "locked": false },
    { "key": "defect#1",     "title": "故障机器人", "enabled": false, "itemType": "Character", "characterId": "defect",
      "random": false, "locked": true,  "detail": "未解锁（NCharacterSelectButton.IsLocked=true），游戏不接受选中" },
    { "key": "random#1",     "title": "随机",     "enabled": true,  "itemType": "Character", "characterId": "random",    "random": true,  "locked": false }
  ] }
```

- 身份**只**读 `NCharacterSelectButton.IsRandom` / `Character` / `IsLocked`：
  `key` = 角色 `Id` 的末尾名字（`CHARACTER.SILENT` → `silent`），`random` 那张固定 `random`；
  `title` = `CharacterModel.Title`（**LocString**，用 `LocText` 取文本）。
- 锁着的角色 `"enabled": false` + `"locked": true` + `detail` 说明；答它会被拒。

**`/respond` 怎么应用**

1. `Validate`（复用，单位「项」）：id 对、正好给 1 个、key 逐字命中候选。
2. **现场重读**一遍当前菜单（按钮节点会随界面重建换新实例），再按 key 在**新**候选里找回那一项；
   找不到 → `ok:false`（菜单内容已经变了）。
3. `main` / `mode` / `submenu`：调那个按钮自己的 `NClickableControl.ForceClick()`
   （= `OnRelease()` + `EmitSignal(Released)`）。`enabled == false` 的按钮**不按**
   （游戏 UI 本来会拦，`ForceClick` 不拦，所以必须由我们拒）。然后轮询
   `MenuSettleMs = 2400ms`，等「stage 变了 / 候选变了 / 菜单整个消失」任一发生才算生效。
4. `character`：先 `NCharacterSelectButton.Select()`（内部才 `_embarkButton.Enable()`），
   再按角色选择页自己的「开始」按钮（private `_embarkButton.ForceClick()`；拿不到按钮才反射调
   private `OnEmbarkPressed()`），然后轮询 `RunManager.Instance.IsInProgress` 变 true
   （最多 `MenuSettleMs`）。
5. 任何 `ok:false` 都把请求挂回 `_menuPending`（`RestoreMenuAfterReject`），能改一道答案再答。

**「放弃本局」的二次确认：选自动确认（失败才退回 `stage:"confirm"`）**

老实现按 `ForceClick` 之后只等「菜单页变了」，而「放弃本局」按下去既不换页也不换 stage，
只会弹一个模态确认框 —— 于是被判成「点击入口调过，但界面没变」的 `ok:false`，agent 必须再答一次。

现在的做法（`ApplyMenuChoice` 里 `IsAbandonRunButton` + `HandleAbandonConfirm`）：

1. 只有按下去的那个候选**按引用**等于 `NMainMenu._abandonRunButton` 时，才去等弹窗
   （别的按钮不多等这 1.4 秒）。
2. 等 `NAbandonRunConfirmPopup` 冒出来（最多 `AbandonConfirmWaitMs = 1400ms`），
   拿它的 `_verticalPopup.YesButton`，调它自己的 `ForceClick()`；再等弹窗真的从树上消失。
3. 返回体带 `"confirmed": true` + `"confirmButton": "<按钮上的文字>"`，
   `how` 变成 `menu-button+confirm`、`visual` 变成 `menu-applied-confirmed`。
   ⇒ agent **只说一次「放弃本局」**，游戏里真的放弃掉了。
4. 安全边界：只认 `NAbandonRunConfirmPopup` 这一种弹窗类型，其它弹窗（退出游戏 / 重置设置）一律不碰；
   确认键按不到 / 弹窗没关掉时**不撒谎**，`confirmed:false` + `confirmNote` 写清现场事实，
   同时下一次 `/request` 会用 `stage:"confirm"` 把「好的 / 取消」两个 key 交给 agent 自己决定
   （可重答，不会卡死）。

选自动确认而不是「分两步」的理由：agent 选「放弃本局」时意图已经表达完，那个框只有「好的 / 取消」、
不含任何新信息，再答一次只会把一次操作变成两轮往返，还会在第二轮之前被别的通道顶掉。
代价是「误点放弃」少了一道由 agent 兜底的机会 —— 但按钮本身在候选里就叫「放弃本局」，
且确认键是弹窗自己的 `YesButton`（不是任何坐标/任意确认框），风险可控。

**`/respond` 返回**

```jsonc
// 阶段 1/2/4：按了按钮自己的入口
{ "ok": true, "source": "screen", "route": "menu", "stage": "main",
  "applied": "单人模式#1", "how": "menu-button", "animated": false, "waitedMs": 180,
  "visual": "menu-applied", "button": "单人模式", "settled": true, "nextStage": "mode" }

// 阶段 3：选中角色 + 按开始 + 真的开局了
{ "ok": true, "source": "screen", "route": "menu", "stage": "character",
  "applied": "silent#1", "how": "menu-character", "animated": false, "waitedMs": 1320,
  "visual": "menu-character", "characterId": "silent", "random": false,
  "embark": "menu-embark", "selected": true, "started": true, "settled": true }
```

- `nextStage` 是**观测到**的下一阶段（不是猜的）；菜单整个消失时是 `null`。
- 开局没生效（典型：第一次进游戏弹 `NAcceptTutorialsFtue` 教程框）时返回 `ok:false`，
  `error` 只陈述事实（角色已选中 / 开始已按 / `IsInProgress` 还是 false / 界面上有教程确认框），
  请求挂回去可重答。

**清场**：`RunManager.Instance.IsInProgress` 为真（进局了）、或者任何菜单页都不在场景树里、
或者这一页没有可选按钮 → `/request` 直接清掉 pending。被更高优先级请求顶掉时也清。

**仍未实测的点**

- 「按下去之后页面变了」是按 `MenuSettleMs` 轮询判定的；**读档 / 开局加载超过 2.4s** 时会被判成
  `ok:false`（请求会挂回去，重新 `GET /request` 再答即可），没在实机上量过这段耗时。
- `NSingleplayerSubmenu` 三个按钮的私有字段名（`_standardButton` / `_dailyButton` / `_customButton`）
  是 MetaPriv 核对过的，但字段读不到时会退回「按 `N*SubmenuButton` 扫」，那条兜底路没实测。
- `NSubmenuButton` 的标题是本地化文本（`main_menu_ui.<前缀>.title`），所以 key 随语言变；
  中文客户端下应该是 `标准模式#1` 这类，但**没有实机截图确认过具体字面**。
- `stage:"submenu"` 只是「协议没覆盖的菜单页」的通用出口：候选按 `N*SubmenuButton` + `NBackButton`
  枚举，多人/设置/每日挑战这些页里的真实交互（勾选框、下拉、排行榜）都不在协议里，需要时仍靠
  调试接口 `/screen` + `/tap`。
- 角色选择页的「开始」按下后，`OnEmbarkPressed` 里还有一条 FTUE 分支（`SeenFtue("accept_tutorials_ftue")`
  为 false 时弹 `NAcceptTutorialsFtue` 并 `return`）。代码会把这种「没进局」如实报成 `ok:false`，
  但**没有**把那个教程弹窗做成一条 request（弹窗本身要先按确认）。

### 7.8 游戏结束通道（`NGameOverScreen`，2026-10-23 落地）

死亡 / 胜利之后那块收尾界面既不是「选牌」，也不归任何房间通道管：它是模态浮层
（`NGameOverScreen.Create(runState, serializableRun)` → 压进 `NOverlayStack`）。
以前 agent 只能靠记忆坐标点三下：

```
GAME OVER → 点「继续」→【一个看不见的空按钮，约 200x110】→ 才亮出「主菜单」→ 点「主菜单」
```

中间那个空按钮没有任何文字，所以「按文字找按钮」永远找不到它；`/screen` 也读不出它的文案。

**数据源（全部在主线程读；私有字段用 MetaPriv 在 `sts2.dll` 上核对过）**

| 用途 | 出处 |
|---|---|
| 界面 | `MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen`（本身是 `NClickableControl`） |
| 「继续」 | private `_continueButton : NGameOverContinueButton`（`Released` → `OpenSummaryScreen`） |
| 「主菜单」/「时间线」 | private `_mainMenuButton : NReturnToMainMenuButton`（`Released` → `OnMainMenuButtonPressed`：有解锁则 `OpenTimeline`，否则 `ReturnToMainMenu`） |
| 「查看本局」 | private `_viewRunButton : NViewRunButton`（`Released` → `OpenRunHistoryScreen`） |
| 「排行榜」 | private `_leaderboardButton : NGameOverContinueButton`（只在每日局里 `Visible`） |
| 结算容器 | private `_summaryContainer : NRunSummary`（`IsVisibleInTree()` = 结算视图已经打开） |
| 徽章容器 | private `_badgeContainer : Control`（里面的可点节点是装饰，不当候选） |
| 本局胜负 | private `_history : RunHistory` → `Win` |

**关键时序（`AnimateRunSummary` 的 IL 实证）**

点「继续」= `OpenSummaryScreen`：先 `Disable` 继续、显示结算容器，然后
`AnimateScoreLines` → `AnimateBadges` → `AnimateScoreBar` → `AnimateDiscoveries` 一路 `await`；
**最后**才 `_mainMenuButton.Visible = true; _mainMenuButton.Enable();`（每日局换成排行榜按钮）。
⇒ **「主菜单」在整段动画播完之前既不可见也不可用**，这就是「必须点那个空按钮」的成因。
游戏自带的 AutoSlay（`AutoSlay.Handlers.Screens.GameOverScreenHandler`）走的也是这条路：

```
点 continueButton → WaitHelper.Until(() => mainMenuButton.Visible && IsEnabled, 90s) → 点 mainMenuButton
```

**`RunState.IsGameOver` 的真实语义（核实过 IL，和名字不一样）**

```
get_IsGameOver = Players.Count > 0 && Players.All(p => p.Creature.IsDead)     // = 全员阵亡
```

**胜利**时这块界面同样会出现，而 `IsGameOver` 是 `false`。
所以判定只能用「界面在树上且可见」，`IsGameOver` 只作为辅助字段 `pending.died` 发出去
（`null` = 读不到，不猜）。另外 `pending.won` 来自 `RunHistory.Win`。

**`/request` 长这样**

```jsonc
{
  "source": "screen",
  "selector": { "state": "active", "...": "..." },
  "pending": {
    "id": 3001,
    "kind": "gameover",
    "stage": "page",            // page（收尾主页）/ summary（结算动画没播完，「主菜单」还没亮）
    "prompt": "游戏结束：选择要做什么（「继续」看本局结算；结算动画播完才会亮出「主菜单」/「时间线」）",
    "min": 1, "max": 1, "requireConfirm": false, "cancelable": false,
    "caller": "NGameOverScreen", "confidence": "high",
    "animated": false,          // mod 不补动画：播的是游戏自己的收尾动效
    "route": "gameover",
    "summaryVisible": false, "continueEnabled": true, "mainMenuReady": false,
    "died": true, "won": false, // 辅助信号；读不到就不出现
    "candidates": [
      { "key": "继续#1", "title": "继续", "enabled": true, "itemType": "Action",
        "buttonType": "NGameOverContinueButton", "detail": "打开本局结算（结算动画播完才会亮出「主菜单」）" },
      { "key": "ContinueButton#1", "title": "ContinueButton", "enabled": true,
        "itemType": "EmptyButton", "buttonType": "NButton", "emptyButton": true,
        "detail": "没有文字的按钮（NButton，屏幕 76% x 85%，大小 200x110）：游戏自己没给它挂文案，点它等于玩家点一下" }
    ]
  }
}
```

- key = `标题#同标题序号`（同一套 `NextKey`）；标题取自按钮**自己身上的 Label**。
- **只发「现在真能按」的按钮**：不可见 / 被游戏 `Disable` 的一律不发（这个通道里它们不是决策点）。
  所以结算动画期间可能只剩那个空按钮，或者一个都没有（那时 `/request` 回 `pending:null`，
  再 GET 一次就好）。
- 那个空按钮的识别方式（**不含硬编码坐标**）：`NClickableControl` 派生 + 可见 + `IsEnabled`
  + 自身子树里找不到任何 Label/Button 文字（`MenuLabel == ""`）+ 不在 `_badgeContainer` 里
  + 不是官方四个按钮。`detail` 里写的是**视口相对位置百分比**，方便核对「是不是右下角那个」。
- `candidates[].itemType` / `.buttonType` / `.detail` / `.emptyButton` 只给这两条 scene 通道新增。

**`/respond` 怎么应用**

1. `Validate`（复用，单位「项」）：id 对、正好给 1 个、key 逐字命中候选。
2. **现场重读**一遍收尾界面（按钮实例会换），再按 key 在新的候选里找回那一项；找不到 → `ok:false`。
3. `NClickableControl.ForceClick()`（= `OnRelease()` + `EmitSignal(Released)`）—— 和玩家真点一下
   走的是同一个信号，**不点坐标**。按钮不可见 / `IsEnabled == false` 时由我们拒（`ForceClick` 不拦）。
4. 等界面推进：预算 = 点的是「继续」或那个空按钮时 `GameOverSummaryMs = 8000ms`，
   其它按钮 `GameOverSettleMs = 3200ms`。判定顺序：
   ① 收尾界面整个消失（回主菜单了）②「主菜单」按钮真的 `Visible && IsEnabled`
   ③ 阶段 / 候选变了。都没发生 → `ok:false`（请求挂回去可重答）。
5. 一路按下去就是：`继续` →（结算动画）→ `主菜单` → 主菜单页（有新解锁时这一下进的是**时间线**，
   时间线是主菜单的子菜单页，下一次 `/request` 会用 `stage:"submenu"` 给出它的按钮 + 「返回」）。

**`/respond` 返回**

```jsonc
{ "ok": true, "source": "screen", "route": "gameover", "stage": "page",
  "applied": "继续#1", "how": "force-click", "animated": false, "waitedMs": 640,
  "visual": "gameover-applied", "button": "继续", "settled": true,
  "nextStage": "summary",        // 观测到的下一阶段；界面关了就是 null
  "mainMenuReady": false }

// 点的是那个空按钮
{ "ok": true, "...": "...", "how": "force-click-empty", "emptyButton": true,
  "settled": true, "nextStage": "page", "mainMenuReady": true }
```

**清场**：收尾界面不在场景树里 / 一个能按的候选都没有 → `/request` 直接清掉 pending；
被 selector 之外的更高优先级请求顶掉时也清（selector 优先）。

**仍未实测的点**

- 那个「空按钮」我**没能在实机里确认是哪一种节点**（不能启动游戏）。现在的识别是
  「`NClickableControl` 派生 + 无文字 + 可见 + 可用 + 不在徽章容器里」，
  只要它在 `NGameOverScreen` 的子树里就会被发出来；如果它其实挂在别的画布层上，这条兜底会落空
  （那时 `/request` 会回 `pending:null`，需要实机跑一遍把节点类型补进 `AddTextlessAdvanceButtons`）。
- `GameOverSummaryMs = 8000ms` 是按「结算动画几秒」估的；实机如果需要更久，
  `/respond` 会返回 `settled:false` 但把请求交回给下一次 `/request`（不卡死）。
- `won` 读的是 `RunHistory.Win`（`_history` 在 `_Ready` 里赋值）；如果拿到的是 null 就不发这个字段。

### 7.9 推进通道（`kind:"proceed"`，2026-10-23 落地）

现象：击杀 Boss 之后 `/request` 是 `pending:null`，必须点那个 (1940,1226) 的空按钮才会弹出奖励列表。

**判定条件（全部是官方 public 成员，MetaPriv 核对过）**

| 条件 | 依据 |
|---|---|
| 战斗已经打完 | `MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsOverOrEnding` |
| 还停在战斗房 | `RunManager.Instance.DebugOnlyGetState().CurrentRoom.RoomType ∈ {Monster, Elite, Boss}` |
| 没判负 | `RunState.IsGameOver == false`（死了走 §7.8） |
| 没有更高优先级界面 | 不是 `NGameOverScreen`；奖励 / 宝箱 / 事件 / 营火 / 商店 那几条通道在前面，它们在场时根本走不到这里 |
| 状态已经稳定 `ProceedArmMs = 1200ms` | 正常战斗的奖励界面 ~1s 内会自己弹出来，这段窗口期不该打扰 agent；只有真卡住（Boss）才发 |

**候选怎么来（不写死坐标）**

1. 优先 `MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton` —— 游戏自己的「前进」控件，
   右下角那个（`ShowPos = viewport * _showPosRatio`），`ScreenApi.Proceed` 用的是同一个类型；
   要求 `IsVisibleInTree() && IsEnabled`。标题取它自己的 `_label`，读不到就叫「继续」。
2. 找不到 `NProceedButton` 才退化：树里「可见 + 可用 + 没有文字 + 中心落在视口**右下半区**
   + 尺寸不像图标 + 祖先里没有别的界面（`RewardsScreen` / `TreasureRoom` / `EventRoom` /
   `RestSiteRoom` / `MerchantInventory` / `GameOverScreen` / `MainMenu` / `CrystalSphere` /
   `SelectionScreen`）」的 `NClickableControl`。纯相对位置判断，**不含任何像素常量**。

**为什么发请求而不是自动点**

同一个 `NProceedButton` 在**奖励界面已经出现**时是奖励屏自己的「跳过 / 离开」键
（`NRewardsScreen._proceedButton`）。自动点它就会把奖励全跳过。
所以这条通道**只发请求、不自动点**：位置由场景状态保证（有奖励屏就不发），
agent 看到它时点一下就是把奖励列表顶出来。

**`/request` 长这样**

```jsonc
{ "id": 3101, "kind": "proceed", "stage": "combat-end",
  "prompt": "战斗结束：按「继续 / 前进」推进（点了之后奖励列表才会出现；没有奖励时就是离开这个房间）",
  "min": 1, "max": 1, "requireConfirm": false, "cancelable": false,
  "caller": "NProceedButton", "confidence": "high", "animated": false, "route": "proceed",
  "candidates": [
    { "key": "继续#1", "title": "继续", "enabled": true, "itemType": "Action",
      "buttonType": "NProceedButton", "detail": "NProceedButton（游戏的「前进」控件），屏幕 76% x 85%，大小 200x110" }
  ] }
```

**`/respond` 返回**

```jsonc
{ "ok": true, "source": "screen", "route": "proceed", "stage": "combat-end",
  "applied": "继续#1", "how": "force-click", "animated": false, "waitedMs": 1230,
  "visual": "proceed-applied", "button": "继续", "settled": true,
  "rewardsUp": true,      // 奖励列表真的弹出来了（Boss 那一幕）
  "leftRoom": false }     // 没奖励，直接进了下一个房间 / 离开战斗房
```

**仍未实测的点**

- 没能在实机里确认「Boss 那个空按钮就是 `NProceedButton`」。判定优先用类型；
  如果不是，退化的「右下半区无文字控件」能兜住，但那条路只是几何启发式，**没实机验证过**。
- `ProceedArmMs` 是估的：如果某些战斗的奖励界面弹得比 1.2s 慢，可能会先看到一次 `proceed`
  请求（答了也就是点一下前进，奖励照样出）。

### 7.10 选牌包通道（`kind:"bundle"`，`NChooseABundleSelectionScreen`，2026-10-24 落地）

现场：Act1 开局某个事件要求「选择一组牌」，agent 点没反应 / 卡住，`/request` 一直是 `pending:null`。

**为什么 selector 接不住它（IL 实证）**

`CardSelectCmd.FromChooseABundleScreen(player, bundles)` 的实现是：

```
if (CombatManager.Instance.IsEnding) return empty;
choiceId = PlayerChoiceSynchronizer.ReserveChoiceId(player);
if (TestMode.IsOn)                          chosen = bundles[0];              // 只有测试模式直接取第一组
else if (CardSelectCmd.ShouldSelectLocalCard(player)) {
    screen = NChooseABundleSelectionScreen.ShowScreen(bundles);  // 弹界面
    chosen = (await screen.CardsSelected()).FirstOrDefault();    // 等玩家选
    PlayerChoiceSynchronizer.SyncLocalChoice(choiceId, FromIndex(bundles.IndexOf(chosen)));
} else /* 远端玩家 */                         chosen = bundles[await WaitForRemoteChoice(choiceId)];
```

**全程没有问过 `CardSelectCmd.Selector`** —— 所以挂 `ICardSelector` 对这条选择完全无效。

**界面自己的结构（MetaPriv + IL 核对）**

| 用途 | 出处 |
|---|---|
| 界面节点 | `MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseABundleSelectionScreen`（`Godot.Control`，无派生类，按具体类型找） |
| 一组一组 | private 字段 `_bundles : IReadOnlyList<IReadOnlyList<CardModel>>`；`_Ready` 里 `NCardBundle.Create(bundle)` 逐个 AddChild 进 `_bundleRow` |
| 一捆的牌 | `NCardBundle.Bundle : IReadOnlyList<CardModel>` |
| 一捆的可点区域 | `NCardBundle.Hitbox : NClickableControl`；`NCardBundle._Ready` 把 `Hitbox.Released` → `NCardBundle.OnClicked` → `EmitSignal(Clicked)` |
| 点中一捆 | 界面的 private `OnBundleClicked(NCardBundle)`：`_selectedBundle = 该捆`、`_bundleRow.Visible=false`、`_bundlePreviewContainer.Visible=true` 并把牌飞进预览、**这时才 `Enable()` `_previewCancelButton` 和 `_previewConfirmButton`** |
| 确认键 | private `_previewConfirmButton : NConfirmButton`（`: NButton : NClickableControl`）；`_Ready` 把它的 `Released` → 界面的 `ConfirmSelection` |
| 确认 | private `ConfirmSelection(NButton)`：逐张播飞进牌组的 vfx，然后 `_completionSource.SetResult(_selectedBundle.Bundle)`；**参数在 IL 里完全没用到** |
| 关闭 | `CardsSelected()`：`await _completionSource.Task` → `NOverlayStack.Instance.Remove(this)` |
| 取消 | private `CancelSelection(NButton)`：只回到牌包行，**不会**让 `CardsSelected()` 返回 |

**判定「选中了一组」和「真的交出去了」（两个判据都要）**

1. **选中**：`ReferenceEquals(界面自己的 _selectedBundle, 这一捆)` **且** `_previewConfirmButton` 已经
   `IsVisibleInTree() && IsEnabled`（选中之前它是 `Disable()` 状态）。
2. **成功**：`_completionSource.Task.IsCompleted == true` **并且** 界面 `IsVisibleInTree() == false`。
   只看「点过」不算成功 —— 点过但界面还在就返回 `ok:false`，请求挂回去可以重答。
   （游戏自带的 `AutoSlay.Handlers.Screens.ChooseABundleScreenHandler` 也是等
   `!screen.IsVisibleInTree()` 才算完成。）

**应答顺序（不点坐标）**

1. `NCardBundle.Hitbox.ForceClick()`（= `OnRelease()` + `EmitSignal(Released)` → `OnClicked` →
   `EmitSignal(Clicked)` → 界面的 `OnBundleClicked`）；界面没记下这一捆时再退一步反射调
   `OnBundleClicked(NCardBundle)`（同一段代码，只是绕开信号）。
2. 按界面自己的确认键 `_previewConfirmButton.ForceClick()`（`Released` → `ConfirmSelection`）；
   控件拿不到才反射调 `ConfirmSelection(NButton)`（传 `null` 就行，参数没用到）。
3. 等上面那两条成功判据。

**两阶段（都能重答）**

| stage | 什么时候 | 候选 | 应答做什么 |
|---|---|---|---|
| `bundles` | 还没选中一捆（√ 还没亮） | 每一捆一组；标题 = 组内卡名（最多 3 个，多了加 `…`），`detail` = `共 N 张：…`，另带 `cards[]` 与 `count` | 选中这一捆 → 按 √ |
| `confirm` | 已经选中一捆、√ 亮了（上一轮 √ 没按成，或被人点进了预览） | 只有一个「确认这组#1」，`detail` = `已选：共 N 张：…` | 只按 √ |

`requireConfirm` 恒为 `true`（这个界面选中之后**必须**按 √ 才会交答案）；
`cancelable` 恒为 `false`（`CancelSelection` 不会让 `CardsSelected()` 返回，放弃就等于把游戏卡死）。

**`/request` 长这样**

```jsonc
{ "id": 4201, "kind": "bundle", "stage": "bundles",
  "prompt": "选择一组牌：候选就是一个个牌包（detail 是组里的牌），选中之后要按 √ 确认",
  "min": 1, "max": 1, "requireConfirm": true, "cancelable": false,
  "caller": "NChooseABundleSelectionScreen", "confidence": "high",
  "animated": true, "animation": "应答时：点这一捆自己的 Hitbox（…）然后按界面自己的确认键 …（见 §7.10）",
  "route": "bundle",
  "candidates": [
    { "key": "打击、防御、猛扑#1", "title": "打击、防御、猛扑", "upgraded": false, "cost": -1,
      "enabled": true, "itemType": "Bundle", "buttonType": "NClickableControl",
      "detail": "共 3 张：打击、防御、猛扑", "cards": ["打击", "防御", "猛扑"], "count": 3 },
    { "key": "铁斩波、铁斩波#1", "title": "铁斩波、铁斩波", "upgraded": false, "cost": -1,
      "enabled": true, "itemType": "Bundle",
      "detail": "共 2 张：铁斩波、铁斩波", "cards": ["铁斩波", "铁斩波"], "count": 2 }
  ] }
```

**`/respond` 返回**

```jsonc
{ "ok": true, "source": "screen", "route": "bundle", "stage": "bundles",
  "applied": "打击、防御、猛扑#1",
  "how": "bundle-hitbox-force-click+confirm-button-force-click",
  "animated": true, "waitedMs": 1640, "visual": "bundle-confirmed",
  "bundle": ["打击", "防御", "猛扑"], "count": 3,
  "selected": true,      // 界面自己的 _selectedBundle 就是这一捆
  "confirmed": true,     // 按的是界面自己的 NConfirmButton
  "screenClosed": true,  // 界面真的从浮层栈上摘掉了，不是「点过」
  "settled": true }
```

**没实测到的点**

- 没在实机里撞上这个界面，「选中 → √ → 界面关闭」这一串是按 IL 推的，没跑过；
  但两个失败判据（`_selectedBundle` 引用不符 / √ 没亮 / 界面没关）都会如实返回 `ok:false`，
  不会谎报成功。

### 7.11 进阶难度阶段（`kind:"menu"` + `stage:"ascension"`，2026-10-24 落地）

现场：每个角色有各自的 Ascension 解锁等级，agent 以前一答 `stage:"character"` 就直接开打，
根本选不了进阶，也不记录进度。

**协议变化（只新增，不改既有字段语义）**

- `stage:"character"` 的应答**不再**按「开始」：只选中角色卡，返回
  `{ok:true, stage:"character", selected:true, started:false, nextStage:"ascension",
    ascensionMax:<上限>, ascensionSource?, ascensionNote?, characterId, random}`。
- 紧接着的 `/request` 给 `stage:"ascension"`，候选是 `Ascension 0` … `Ascension {max}`；
  答这一阶段才「设等级 + 按「开始」+ 等真的进对局」。

**数据链（MetaPriv + IL 实证）**

| 用途 | 出处 |
|---|---|
| 面板 | `NCharacterSelectScreen._ascensionPanel : NAscensionPanel` |
| 上限 | `StartRunLobby.SetSingleplayerAscensionAfterCharacterChanged(角色)` 算好之后经 `NCharacterSelectScreen.MaxAscensionChanged()` → `NAscensionPanel.SetMaxAscension(int)` 写进 `_maxAscension` |
| 上限里的游戏规则 | ① 该角色的 `CharacterStats.MaxAscension`（存档进度）；② `IsAscensionEpochRevealed(角色)` —— 进阶 epoch 没揭开就把上限压成 0（两条都在 `SetSingleplayerAscensionAfterCharacterChanged` 的 IL 里） |
| 当前等级 | `NAscensionPanel.Ascension`（int） |
| 设等级 | 公开 `NAscensionPanel.SetAscensionLevel(int)`：`set_Ascension` + `EmitSignal(AscensionLevelChanged)` + 刷新文本 |
| 同步进 lobby | 信号 → `NCharacterSelectScreen.OnAscensionPanelLevelChanged` → `StartRunLobby.SyncAscensionChange(panel.Ascension)`（开打时用的是 lobby 里这个值） |
| 开打 | `_embarkButton.ForceClick()`（`NConfirmButton`，`Released` → `OnEmbarkPressed`） |
| 复核 | `RunState.AscensionLevel` |

**候选怎么生成**

1. `max` 依次尝试：`NAscensionPanel._maxAscension` → `StartRunLobby.MaxAscension` →
   `SaveManager.Instance.Progress.CharacterStats[角色].MaxAscension`（**只读字典**，不调
   `GetOrCreateCharacterStats`，免得往存档里写多余条目），最后用
   `AscensionManager.maxAscensionAllowed` 封顶。
2. 三个来源都读不到 → **只给 `Ascension 0`**，并在 `prompt` / `pending.ascensionNote` /
   返回体 `ascensionNote` 里明说「无法读取解锁等级」。**绝不编一个上限。**
3. 标题 `Ascension N`，key `Ascension N#1`；等级同时写进候选的 `ascension` 字段（应答按候选对象取值，
   不从 key 里解析字符串）；`detail` 用 `AscensionHelper.GetTitle/GetDescription(N)`，读不到就是「进阶 N」。
4. `selected` = 面板当前等级对应的 key。

**应答（`ApplyMenuAscension`）**

1. 现场复核上限（`level > max` → `ok:false`，可重答）。
2. `NAscensionPanel.SetAscensionLevel(level)` → 复核 `panel.Ascension == level`；
   若 `StartRunLobby.Ascension` 还没跟上（信号没连上的极端情况），补一次
   `StartRunLobby.SyncAscensionChange(level)`。
3. `_embarkButton.ForceClick()` → 等 `RunManager.IsInProgress`。
4. 用 `RunState.AscensionLevel` 复核这一局真的是这个进阶（`ascensionVerified`）。

```jsonc
// /request（stage=ascension）
{ "id": 4210, "kind": "menu", "stage": "ascension",
  "prompt": "选择进阶难度（已解锁 Ascension 0~6）",
  "min": 1, "max": 1, "requireConfirm": false, "cancelable": false,
  "caller": "NAscensionPanel", "confidence": "high", "animated": false, "route": "menu",
  "selected": "Ascension 3#1",
  "ascensionMax": 6, "ascensionCurrent": 3,
  "ascensionSource": "NAscensionPanel._maxAscension（SetMaxAscension 由 StartRunLobby 算出）",
  "characterId": "silent",
  "candidates": [
    { "key": "Ascension 0#1", "title": "Ascension 0", "enabled": true, "itemType": "Menu",
      "buttonType": "NAscensionPanel", "detail": "无进阶（基础难度）" },
    { "key": "Ascension 1#1", "title": "Ascension 1", "enabled": true, "itemType": "Menu",
      "buttonType": "NAscensionPanel", "detail": "…（AscensionHelper.GetTitle/GetDescription）" }
  ] }

// /respond
{ "ok": true, "source": "screen", "route": "menu", "stage": "ascension",
  "applied": "Ascension 3#1",
  "how": "ascension-panel+menu-embark",
  "ascension": 3, "ascensionMax": 6, "ascensionVerified": true, "runAscension": 3,
  "ascensionSource": "…", "characterId": "silent", "embark": "menu-embark",
  "selected": true, "started": true, "settled": true, "waitedMs": 3120 }
```

`/state` 里的 `ascension`（= `RunState.AscensionLevel`）本来就有，这一版确认没改语义。

**没实测到的点**

- `CharacterStats.MaxAscension` 的读写没在实机里跑过（这台机器上没有解锁进度可验证）；
  上界走的是**界面已经算好的** `_maxAscension`，所以才敢说「按游戏自己的规则」。
- 失败回退（`ascensionNote`）那条分支没实机触发过。

### 7.12 存档并退出 / 重新读档（`POST /save-quit`、`POST /reload`，2026-10-24 落地）

现场：决策做错之后只能手动退出游戏。两个接口都走游戏自己的入口，不强杀进程。

**`/save-quit` 的真实入口（IL 实证）**

- 暂停菜单那个按钮：`NPauseMenu.OnSaveAndQuitButtonPressed(NButton)` —— IL 只有
  `TaskHelper.RunSafely(CloseToMenu())`。
- `NPauseMenu.CloseToMenu()`：把所有暂停菜单按钮 `Disable()` → `NRunMusicController.StopMusic()`
  → `NDebugAudioManager.StopAll()` → **`NGame.Instance.ReturnToMainMenu()`**。
- 也就是说游戏自己的「保存并退出」**并不额外存盘**：进度是房间推进时
  `SaveManager.Instance.SaveRun(room, saveProgress)` 自动写的（`RunManager.ShouldSave` 为真时才写）。
- 所以本实现两步都用游戏自己的入口：
  1. `SaveManager.Instance.SaveRun(preFinishedRoom: null, saveProgress: true)`
     —— 就是自动存档那一个（内部：等上一次存盘 → `BeginSaveBatch` → `SaveProgressFile` →
     `RunManager.ToSave(preFinishedRoom)` → `RunSaveManager.SaveRun` → `EndSaveBatch`）；
     `preFinishedRoom: null` = 按当前房间存，和游戏走图时存的形状一致。
  2. `NGame.Instance.ReturnToMainMenu()`
     —— 它的 IL：`FadeOut` → `LoadCommonAndMainMenuAssets` → **`RunManager.Instance.CleanUp(true)`**
     （注意 `CleanUp` 会把 `ShouldSave` 置 false，所以**必须先存再退**）→ `LoadMainMenu`。
- **没存成功就不退**：存盘任务 fault/超时就返回 `ok:false` 并说明原因（退了就再也存不了了）。

**`/reload` 的真实入口**

- 主菜单「继续游戏」= `NMainMenu._continueButton`（`NMainMenuTextButton`，`Released` →
  `OnContinueButtonPressed` → `OnContinueButtonPressedAsync`）。
  `OnContinueButtonPressedAsync` 的 IL 依次是：
  `_readRunSaveResult.SaveData` → `RunState.FromSerializable(save)` →
  `RunManager.Instance.SetUpSavedSingleplayer(runState, save)` → 淡入 →
  `NGame.Instance.LoadRun(runState, save.PreFinishedRoom)` → 淡出。
- 所以 `/reload` **优先直接按那个按钮**（`ForceClick()`）；按钮拿不到时才按上面这段自己走一遍
  （`SaveManager.LoadRunSave()` → `RunState.FromSerializable` → `SetUpSavedSingleplayer` → `LoadRun`）。
- 落点就是游戏自己的存档点（存档里记的是**当前房间**），**不是**「回到战斗开始」。

**返回结构**

```jsonc
// POST /save-quit
{ "ok": true, "saved": true, "room": "MainMenu", "waitedMs": 5210,
  "hasRunSave": true, "wasRoom": "Monster", "wasAct": "ACT.1", "wasFloor": 7, "wasAscension": 3,
  "saveEntry": "SaveManager.Instance.SaveRun(preFinishedRoom:null, saveProgress:true)",
  "quitEntry": "NGame.Instance.ReturnToMainMenu()（= NPauseMenu.CloseToMenu 的最后一步）",
  "note": "读档落点就是游戏自己的存档点…；/reload = 主菜单「继续游戏」" }

// POST /reload
{ "ok": true, "inRun": true, "hasRunSave": true, "how": "continue-button",
  "waitedMs": 7320, "room": "Monster", "act": "ACT.1", "actFloor": 3, "totalFloor": 7,
  "ascension": 3, "currentCoord": { "col": 2, "row": 3 },
  "reloadEntry": "NMainMenu._continueButton（ForceClick = 主菜单「继续游戏」）",
  "note": "落点是游戏自己的存档点（存的是当前房间），不是「回到战斗开始」" }
```

失败分支（都是 `ok:false` + `error` 说清现场）：不在对局里、`ShouldSave=false`、存盘超时/异常、
回主菜单没落地、已经在局里（`/reload`）、没有存档（`HasRunSave=false`）、读档发起后 `inRun` 仍为 false。

**两个操作都在主线程、等状态稳定**

- 所有 Godot / `RunManager` / `SaveManager` 的读写都经 `BridgePump.OnMain` 回主线程；
  两个 `async Task`（`SaveRun` / `ReturnToMainMenu` / `LoadRun`）都**在主线程上发起、在 HTTP 线程上等**，
  不在主线程上 await（主线程上等主线程任务会卡住泵）。
- 成功判据不是「调过」，而是 `RunManager.IsInProgress == false` + `NMainMenu` 已在树上（save-quit）、
  `RunManager.IsInProgress == true`（reload）。

**没实测到的点**

- 没在实机里跑过 `/save-quit` → `/reload` 这一对（这台机器上不启动游戏、不调 HTTP 接口）。
- 读档落在**战斗中**的存档时游戏会怎么恢复（`RunManager.ToSave(preFinishedRoom:null)` 对战斗房
  的序列化）没有实测；这里只是照抄游戏自己的存盘/读档入口，没有额外加工。

### 7.13 手牌可用性（`/state` 的 `hand[].playable`，2026-10-24 落地）

现场：有些牌除了费用还有别的条件（「本回合已经打出过技能牌」之类），agent 看不见，于是反复尝试同一张。

**用的就是游戏自己的统一入口**

`CardModel.CanPlay(out UnplayableReason reason, out AbstractModel source) -> bool`（IL 逐条核对）：

```
reason = None;
if (CombatState == null || Owner.PlayerCombatState == null) { source = null; return false; }   // reason 仍是 None
if (keywords.Contains(Unplayable))                    reason |= HasUnplayableKeyword;         // bit 2
if (!PlayerCombatState.HasEnoughResourcesFor(card, out r)) reason |= r;                        // EnergyCostTooHigh / StarCostTooHigh
if (TargetType == 6 && 活着的队友数 <= 1)              reason |= NoLivingAllies;               // bit 64
if (!Hook.ShouldPlay(combat, card, source, false))     reason |= BlockedByHook;                // bit 4
if (!card.IsPlayable)                                  reason |= BlockedByCardLogic;           // bit 8（子类 override，就是「本回合已经打出过技能牌」这类）
return reason == None;
```

`CardModel.IsPlayable` 在基类里恒为 `true`，由具体卡 override —— 所以「费用以外的条件」全都落在
`BlockedByCardLogic` / `BlockedByHook` 这两位上；`source` 是游戏指出的「谁拦的」（卡 / 遗物 / 能力，可能是 null）。

**`/state` 新增字段（只加不改）**

```jsonc
{ "title": "猛扑", "cost": 2,
  "playable": false,
  "unplayableReason": "条件不满足",            // 给人看的一句话
  "unplayableReasonCode": "BlockedByCardLogic", // 游戏 UnplayableReason 枚举自己的名字（复合原因是 "A, B"）
  "unplayableSource": "…" }                    // 有 source 时才有
```

映射规则（**只写游戏真的给出的那一个原因，不编具体文案**）：

| 游戏原因位 | `unplayableReason` |
|---|---|
| `EnergyCostTooHigh` | `能量不足` |
| `StarCostTooHigh` | `星费不足` |
| `NoLivingAllies` | `没有存活的队友` |
| `HasUnplayableKeyword` | `这张牌不能打出` |
| `BlockedByHook` / `BlockedByCardLogic` / 其它 / `None` | `条件不满足` |

`playable: true` 时只发 `playable`，不发那两个原因字段。`CanPlay` 抛异常时**不拦**（按可用处理），
免得把一张其实能打的牌误判掉。

**`/acts` 撞上打不出去的牌**

- 批量执行前先问一次 `GameApi.PlayabilityOfHandCard(uid)`：`playable:false` 就**不入队**，
  直接停在这张牌上，返回 `reason:"unplayable"` + 顶层的 `playable` / `unplayableReason` /
  `unplayableReasonCode`（+ 有则 `unplayableSource`），`hint` 区分「能量不够」和「条件不满足」。
- `/act` 的 `play_card` 也做同一道门：失败体是
  `{ok:false, error:"这张牌现在打不出去：<原因>（<牌名>）", playable:false, unplayableReason,
    unplayableReasonCode, card, unplayableSource?}`。

**没实测到的点**

- `CanPlay` 的位值是 IL 里读出来的（2 / 4 / 8 / 64 + `HasEnoughResourcesFor` 返回的那一位），
  但没有实机跑过「打出过的技能牌 → 猛扑变成不可打」这一类具体场景。
- `unplayableSource` 能不能拿到非空值没验证过（`source` 是 `Hook.ShouldPlay` 的 out 参数）。

### 7.14 地图查询（`GET /map` 增强 / `GET /map/next`，2026-10-24 落地）

agent 走图一直在「试」坐标：它只有一堆 `points` 坐标，看不到**连边**，也不知道从当前节点到 Boss
有几条路、各自经过什么房间类型。现在 `/map` 只**新增**字段（原有字段语义不变），并多一个明确接口
`/map/next`。**路线规划不在 mod 里做**：`/map` 只给全图（含连边），由 agent 自己按
`children`/`parents` 定路线（skill 的「每幕开局」一节写了判断依据）。

`GET /map` 新增字段与来源（`MetaPriv` / `MetaIl` 核过 `sts2.dll`）：

| 字段 | 从哪读的 |
| --- | --- |
| `act` | `RunState.Act.Id`（如 `ACT.OVERGROWTH`） |
| `rows` / `cols` | `ActMap.GetRowCount()` = IL 里 `Grid.GetLength(1)`；`ActMap.GetColumnCount()` = `Grid.GetLength(0)`。`Grid` 是 `MapPoint[列, 行]`（`StandardActMap..ctor`: `new MapPoint[7, _mapLength]`） |
| `boss` | `ActMap.BossMapPoint`；`StandardActMap..ctor` 里 `new MapPoint(GetColumnCount()/2, GetRowCount())`，所以 **`boss.row == rows`**，最后一行 Grid 是 `rows-1` |
| `points[].children` | `MapPoint.Children`（public property，`HashSet<MapPoint>`） |
| `points[].parents` | `MapPoint.parents`（**public 字段**）；`AddChildPoint(child)` 的 IL 同时 `Children.Add(child)` + `child.parents.Add(this)`，两边互为镜像，这里两边都读、按坐标去重，并丢掉两端不在本图里的僵尸边（`MapPathPruning` 裁掉的点） |
| `points[].visited` | `RunState.VisitedMapCoords` |
| `points[].special` | `Start` / `Boss` / `SecondBoss` 三个不在 Grid 里的点（`GetAllMapPoints()` 不含它们，`ActMap.GetPoint` 里单独比对坐标） |
| `available[].edge` | 相对当前节点：`start`（还没进过节点）/ `up`（正上方，或 Boss / 第二 Boss —— 整行共用的出口）/ `left` / `right`（斜上一层） |
| `legend` | `MapPointType` 全枚举 → 中文（另有 `Start` / `SecondBoss`；枚举里**没有** `Event`，`Event` 这个 key 只是沿用旧叫法） |

`GET /map/next`：

- `canMove` 与 `/map` 的 `available` **同源**：`GameApi.TravelablePoints`（照抄
  `NMapScreen.RecalculateTravelability`，主体是 `MapTravel.GetTravelablePointsFrom`）。
- `cannot` = 「看着像候选但进不去」的节点 + 原因（`已走过` / `不相邻（和当前节点没有连边）` /
  `同一层不能横走` / `起点只能作为第一步` / `你现在就在这个节点上` / 刚切幕时的
  `还没进入这一幕的地图：先走起点节点`）；一条候选都没有就是 `[]`。
- 人在房间里（事件/营火/商店/宝箱/奖励）或战斗没打完 → `{ok:true, inRoom:true, room, canMove:[],
  note}`。判据与 `BeginMove` 拦 `move` 的是**同一个方法**（`GameApi.RoomBlocksTravel`），
  不会出现「next 说能走、move 被拒」。

**没实测到的点**

- 只做了离线核对（`sts2agent\tmp\MapProbe`）：`MapPoint.AddChildPoint` 确实双向写
  `Children` / `parents`、`MapCoord` 的 `(col,row)` 顺序、`EdgeName` 四个方向、`legend` 覆盖全枚举。
- 真实一局里的 `/map` / `/map/next` JSON **没有**在游戏里跑过（直播中，不允许启游戏），
  所以 `rows` / `boss.row` 的实际数字、以及 `visited` 的时序都还没实测。


