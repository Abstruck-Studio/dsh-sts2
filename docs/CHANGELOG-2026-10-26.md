# 变更记录 · 2026-10-26

主题：把「别人项目里值得抄的策略」并进来，**不动原有主体**（协议、验证、打包、直播链路都没改语义）。
方向只有一个：让模型少心算、少走图、少踩接口不一致的坑。

## 2026-10-26 深夜：Boss 奖励被 proceed 跳过（0.3.1）

现象：Boss 打完 → `/request` 返回 `pending:null` → agent 调 `sts2_proceed` → 奖励整批丢失、直接进下一幕。
日志：21:16:51 进 Boss → 21:19:42 `proceed via NProceedButton.ForceClick` → 没有任何 `rewards:` 记录。

根因：Boss 奖励屏比战斗结束晚 2~3 秒弹出，这段空档由 `kind:"proceed"` 通道（`跳过#1`）占着，
一点就把「还没出现的奖励」跳掉了。

修法：
- `RequestApi.UnclaimedRewardsPending()`（`GetNextRewardIds()` + `IsRewardsSetCompleted(player,id)`），
  **只在战斗房间（Monster/Elite/Boss）里成立**：实测 `GetNextRewardIds()` 含「将来」的奖励 id，
  人回到 Map 后仍然非空 —— 不限房间会在 Map 上一直报 `stage:"incoming"`（已真机复现并修掉）。
- 空档期 `/request` 给 `kind:"reward"` + `stage:"incoming"`（空候选、提示等待）；`AnswerRewards` 对该 stage
  只重新探、绝不 skip。
- `DescribeProceedPending` 在有未结算奖励时不再挂 `跳过#1`。
- `POST /proceed` 同条件下拒绝：`{ok:false, rewardsPending:true, error, hint}`；
  新增 `sts2_proceed {force:true}` 逃生口（确实拿不了奖励时）。
- 插件 `sts2_proceed` 增加 `force` 参数；测试补两条（默认 false / force 透传）。
- 版本：0.3.0 → **0.3.1**（`plugin/package.json`、`mod/Sts2AgentBridge.json`、`ModVersion` 同步）。

## 2026-10-26 晚：按 7 条 bug 报告修完（版本 0.3.0）

报告：`sts2-treasure-empty-room-protocol-gap.md`（7 条全部处理；问题 1 按「箱子根本没打开」的现场重修）。

- **问题 1 空宝箱**（白银熔炉）：真正的根因是 `CurrentTreasureRelics(sync).Count == 0` 就提前 return ——
  空箱一开始就是空的，于是连「打开宝箱#1」都发不出去。现在先看开箱状态：
  `!ChestOpened` 一律发 `stage:"chest-closed"`；`ChestOpened && !CollectionOpen` 超过
  `TreasureEmptyGraceMs=1200ms` 发 `stage:"empty"` +「继续#1」；`AnswerTreasureEmpty` 点前进并轮询房间变化。
- **问题 2 power**：`DataApi.Power(id|title)`、`GET /power`、工具 `sts2_power`；`/state` 的 `powers[]`
  带 `title`，`sts2_view` 优先打中文名。
- **问题 3 升级预览**：`CardModel.ToMutable()/UpgradeInternal()/FinalizeUpgradeInternal()`
  克隆后升级（**不动游戏本体**）→ `sts2_card_info` 返回 `upgraded:{title,text,vars}`；
  `CandidateJson` 给每个卡牌候选加 `upgradePreview`（只列变了的数值）。
- **问题 4 奖励屏死锁**：`离开奖励#1` + `picks:[]` 关屏；跳过之后真去点界面自己的跳过/继续键；
  `/request` 判活改成 `IsShown`（不在树或不可见都算消失），幽灵 pending 自愈；结果带 `closed`/`hint`。
- **问题 5 界面层**：`/screen` 加 `layerStack` + `canvas` + 控件 `offscreen`；
  新增 `POST /close-overlay` + 工具 `sts2_close_overlay`（商店返回键 / 奖励跳过键 / 地图 toggle /
  牌库 capstone.Close / 选牌 ESC）；`sts2_tap` 画布外返回 `offscreen:true` + `warning`。
- **问题 6 relics**：`DataApi.Relics()` + `GET /relics` + `sts2_relics`。
- **问题 7 Boss 身份**：`/map` 的 `boss` 补 `encounterId`/`title`/`tags`/`monsters`
  （`ActModel.BossEncounter`，游戏自己选中的那个）+ `secondBoss`。
- 版本：`plugin/package.json`、`mod/Sts2AgentBridge.json`、`HttpBridge.ModVersion` 均为 **0.3.0**。

**仍未做**：`sts2_route` 按 Boss 类型调权重（召唤型 → AOE 权重上调）。

## 新增能力（插件侧，纯本地，无需改游戏）

| 新增 | 作用 | 为什么值得加 |
|---|---|---|
| `sts2_view` | 一屏给出 HUD / ANCHOR（入伤）/ 敌人意图 / **EFF**（每张攻击牌对每个敌人的真实伤害，含易伤×1.5、格挡池、胆小首盾、多段、荆棘反伤、`★斩`）/ BLOCK_POOL / ORDER / COPIES / 手牌稳定 key | 原来模型拿原始 JSON 自己乘，最容易错的就是「半挡半打」「差 1–2 点没斩杀」 |
| `sts2_deck` | 整副牌统计（攻/技/能、防御、AOE、升级、诅咒/状态）+ 同名合并清单 | `/state` 只有 `deckSize`，模型此前**看不到牌组内容**，删牌和拿奖励全靠猜 |
| `sts2_route` | 每个可走起点到 Boss 的全路径打分 + `ELITE_GATE` + 血线/金币 WARNING，`commit` 记计划 | 原来每幕都要模型自己遍历 `children/parents` 再心算权重 |
| `sts2_plan` | 拿「规划当时的快照」对账，`PLAN_OK` / `REPLAN` + 具体理由（换幕、血线、掉血、牌组、AOE、药水、金币、诅咒） | 计划会过期，原来只能靠模型记得住 |
| `sts2_proceed` / `sts2_save_quit` / `sts2_reload` | 补齐 mod 早就有的 `/proceed`、`/save-quit`、`/reload` | 之前文档写了路由但**工具没暴露**，agent 只能拿 pwsh 打 HTTP |
| `sts2_acts` 的 `card` 参数 | 手牌用 `标题#序号` 寻址（插件先读现场手牌解析成 uid） | uid/下标抽牌就变；药水早有稳定 key，手牌现在一致 |

## 修复

- `sts2_state` 的**发牌动画补读是死代码**：`http()` 返回的是字符串，`first?.room` / `Array.isArray(hand)`
  永远拿不到值。改成解析 JSON 后判断，真能补读一次。
- `sts2_start` 与预设矛盾（预设说已禁用，工具却还暴露）：现在**默认 `REFUSED`**，只在
  `STS2_ALLOW_START=1` 时放行，并把「会造幽灵局」的原因写进拒绝文案。
- `mod/deploy.ps1` 在 Windows PowerShell 5.1 下会崩：无 BOM 的 UTF-8 脚本被按 ANSI 解码，
  中文注释末尾的字节吃掉换行，把下一行 `$root = ...` 并进注释 → `$root` 为 null。
  已改成**纯 ASCII 脚本**，并加「游戏在跑就别部署（DLL 被占用）」的显式报错。
- **部署出重复 id 的模组**（我引入的回归）：`deploy.ps1` 会把仓库里的 `mod.json` 一起拷进
  `mods\Sts2AgentBridge\`，而加载器把该目录下**每个 `*.json` 都当清单**，于是
  `Sts2AgentBridge.json` 与 `mod.json` 两份同 id 清单 → 报「存在多个 id 为 Sts2AgentBridge 的模组」
  （godot 日志里能同时看到 `Found mod manifest file …Sts2AgentBridge.json` 与 `…\mod.json`）。
  修法：① 删掉游戏目录里的 `mod.json`；② `deploy.ps1` 只拷 `<id>.json`，并在部署前自愈式删除
  遗留的 `mod.json`；③ 仓库里的旧草稿改名 `mod.json.legacy-camelcase`（它是 camelCase schema、
  且 `affectsGameplay: true`，与正式清单的 `affects_gameplay: false` 语义相反，不能混用）。
  正式清单版本号同时升到 0.2.0。
- **选牌候选查不到卡面（「主宰」事件）**：`card-choice` 候选只有 `title`，`sts2_card_info` 只认 `id`，
  且 `/card?uid=` 用 `int` 解析（屏幕控件 uid 是 60 位 → 静默变 null → 回误导的 `missing id or uid`）。
  修法（四层）：
  ① `RequestApi.CandidateJson` 给候选补 `modelId`（卡/遗物/药水，来自游戏模型）+ 卡牌候选的 `detail`
  （复用 `DataApi.CardText`，动态数值已展开）；覆盖选牌族、奖励列表、宝箱、商店。
  ② `DataApi.Card` 改收 `long? uid`：60 位 uid 明确报「这是界面控件 id」，不再是 `missing id or uid`；
  新增 `title` 反查（手牌 → 牌库 → `ModelDb.AllCards`，同名返回 `ambiguous + matches`）。
  ③ `/relic`、`/potion` 同样支持 `title`（`ModelDb.AllRelics/AllPotions`）。
  ④ `respond` 的 key 校验除 `Key` 外也认 `Stable` 与 `modelId`（用 `HashSet<Candidate>` 去重），
  避免「字段读得出、答不回去」；插件侧 `sts2_card_info/relic/potion` 三个工具都加了 `title` 参数。
- `/health` 的 `version` 一直是硬编码 `0.1.0`（manifest 已到 0.2.0）：改成常量
  `HttpBridge.ModVersion = "0.2.0"`，并在注释里写明必须和 `mod/Sts2AgentBridge.json` 同步改。
  已重编（`bin\Release` 521728 bytes），**部署留到下次重启游戏时一起做**。
- **商店面板关不掉（阻塞）**：`stage:"items"` 下 `picks:[]` 只回「什么都没买」，面板不关；
  而面板开着时前进键 `NProceedButton.IsEnabled=false`、前进按钮又在画布外（x≈1987）点不到、
  `move` 被房间护栏拒绝 → 完全死锁（实测 pending 原地换成新 id）。
  修法：① 新增「离开商店#1」动作候选（`id:"leave"`，`itemType:"Action"`），`picks:[]` 也走同一条路；
  ② `AnswerShopLeave` → `TryCloseShopPanel` 依次尝试 **面板自己的 `NBackButton`** → 名字像
  返回/关闭的可点控件 → 已可用的前进键，每步轮询 `IsOpen` 变 false，全失败就 `ok:false` 并把请求挂回；
  ③ 关掉后记住节点 id（`_shopDismissedFor`），不再发 `stage:"closed"` 把 agent 拉回去；
  ④ 全售罄时也发一个「离开商店」出口；
  ⑤ **`move` 保持只在地图页有效**（用户口径）：关面板不等于在地图页，所以不放宽 `move`；
  改成 `/proceed` 两段式 —— 先点前进键、轮询「房间变了或地图页开了」，没变化就点顶栏
  `NTopBarMapButton` 打开地图页，返回 `{ok, method, mapOpen, room, proceed, hint}`；
  `ScreenApi.Proceed` 在前进键被禁用且商店面板开着时，错误信息直接告诉你怎么关。
  真机验证：`picks:[]` → `{"ok":true,"stage":"left","closed":true,"how":"shop-leave-back-button","waitedMs":91}`，
  之后 `/request` pending=null、`/proceed` 从「不可用」变成可用（两段式与地图回退尚待部署后验）。

## 游戏侧（C#，只加只读路由）

- `GET /deck`：`GameApi.DeckView(playerIndex)` + `HttpBridge` 路由，复用 `CardDto` 返回整副牌。
  只读：不排队动作、不改状态。`dotnet build -c Release` 通过。

## 文档 / 工具

- `README.md`：工具表补 7 个工具；新增「压缩视图 / 牌组 / 路线 / 对账」整节（算法口径、稳定 key、
  计划文件位置、自测命令、部署要求）；组件表补 `skill/`、`scripts/`。
- `docs/HANDOFF.md`：接手提示词（读什么、硬规则、收尾清单）。
- `skill/APPEND.md` + `skill/PATCHES-2026-10-26.json` + `skill/UPDATE-2026-10-26.md` +
  `scripts/apply-skill-updates.ps1`：技能增量 = 1 个参考块（`sts2_view` 怎么读、稳定 key、
  route/plan 循环、三个补齐工具）+ 7 处定点补丁（第 2 步、出牌 fast path、路线 fast path、
  存读档两行、push 与 view 的分工、`sts2_start` 口径）。脚本**幂等**且 **CRLF 无关**：
  标记块整块替换，每条补丁自带 marker，命中过就跳过；动手前先备份原文件。
  `scripts/install-skill.ps1` 现在只是它的兼容壳。
- `scripts/test-plugin.mjs`：离线自测，起假桥 + 直接 import `server.mjs`，覆盖 EFF 数学、
  key 解析、route 打分、plan 对账、`/deck` 统计、state 补读、三个补齐工具、`sts2_start` 拒绝。
- `plugin/package.json` 0.1.0 → 0.2.0；`serverInfo.version` → 0.2.0。

## 验证结果

```
node sts2agent\scripts\test-plugin.mjs        -> ALL PASS (39 checks)
dotnet build -c Release (sts2agent\mod)       -> 0 warning, 0 error
node --check sts2agent\plugin\server.mjs      -> OK
apply-skill-updates.ps1 本地副本连跑两次        -> 7 补丁全命中；第二次 skip 全部；35010 bytes 稳定
apply-skill-updates.ps1 真实技能文件            -> 7 补丁 applied / 0 missing；磁盘 35010 bytes
deploy.ps1（游戏未运行）                       -> 已部署，hash 与 bin\Release 一致（517120 bytes）
```

## 已代做（原本要你手动）

1. **新 DLL 已部署**到
   `D:\.steam\steamapps\common\Slay the Spire 2\mods\Sts2AgentBridge\Sts2AgentBridge.dll`
   （`GET /deck` 生效；SHA256 与 `bin\Release` 一致）。
2. **技能已更新**：`~/.dsh/skills/slay-the-spire-2/SKILL.md` 已打上参考块 + 7 处补丁，
   原文件备份为同目录 `SKILL.md.bak-<时间戳>`。
3. **攻略库按角色归位**（`%APPDATA%\SlayTheSpire2\guide\`）：外部铁甲 A10 手册里
   「与角色无关的算术」进 `memory.md` 通用经验（12 条）；铁甲专用进 `ironclad/memory.md`（6 条，含 1 条冲突）、
   `ironclad/cards.md`（AOE 清算表 + 升级优先级）、`ironclad/matchups.md`（A1 Boss 时序 + 精英解法 + Act2 总账 + 2 条冲突）。
   全部标「导入 / 待验证」，`guide/README.md` 补了来源与可信度声明。
   未导入：梗库、caster 台词、96KB 逐局复盘（个案，不适合整包搬）。
4. **玩家预设词已同步**（`sts2-player-preset/cordis.patch.yml`）：规则 1–8 改为指向新工具
   （`sts2_view` 读局面、`sts2_route`+`sts2_plan` 定路线、`sts2_acts` 用稳定 key、`sts2_deck` 看缺口、
   `sts2_save_quit`/`sts2_reload`/`sts2_proceed` 管存读档），`sts2_start` 口径改为默认 `REFUSED`；
   边界新增「标着导入/待验证的条目先按本机实测走」。插件清单与 persona 之外的结构未动。

## 还需要你做的

1. **重启 DSH / 开新会话**才能看到新工具（MCP 工具表在会话启动时注册）。
2. 想在真机上验证：`sts2_view` 的 EFF 对**条件多段**（「目标易伤时攻击两次」）会低估，
   因为卡面文字不在 `/state` 里；拿不准时 `sts2_card_info` 兜底。
