---
name: slay-the-spire-2
description: Play a full Slay the Spire 2 run through the in-game agent bridge (mcp__sts2__* tools) — read live state, decide card plays, and act in verified batches — narrating each step through the in-game whale companion and appending reusable lessons to a persistent markdown memory. Use whenever the user asks to play, continue, or pilot StS2 (杀戮尖塔2), or to review what went right/wrong in a run.
---

# Playing Slay the Spire 2 as an agent

You pilot the game through a mod that runs inside the game process. All tools talk to
`http://127.0.0.1:47821` on the same machine; nothing leaves it.

## Before you touch the game

1. `sts2_health` — if `attached` is false the game is closed or the mod did not load. Start it
   with `sts2agent\scripts\launch-game.ps1`（内部固定走 `steam.exe -applaunch 2868840`；直接启动
   exe 会崩 Steamworks）。脚本自己轮询 `/health`：就绪打印 `OK bridge=...`，超时打印 `FAIL`；
   游戏已经在跑时它不会重复启动，只等桥接。加 `-Deploy` 可先把编译好的 DLL 拷进 mods 目录。
   **执行策略会拦脚本**，要这样跑：
   ```
   powershell -NoProfile -ExecutionPolicy Bypass -File sts2agent\scripts\launch-game.ps1 [-Deploy]
   ```
2. `sts2_state`，然后 `sts2_request` —— 停在菜单/开始界面时用 `kind:"menu"` 走完，不点坐标。
   进战斗后改用 `sts2_view`（一次给出 ANCHOR / EFF / BLOCK_POOL / ORDER / COPIES / 手牌 key），
   别拿原始 `/state` 自己心算伤害。

## 开局流程：menu 一路走到底

菜单全部走 `sts2_request` → `kind:"menu"`，一次只答当前 `stage` 那一页：

```
main        → 单人模式
mode        → 标准模式（也可能先停在 submenu，看 stage 字段）
character   → 角色（`silent#1` 这样的 key；random 是随机，别赌）→ 答完只选中，返回 nextStage:"ascension"
ascension   → 进阶档：候选是 `Ascension 0#1` … `Ascension N#1`，N 来自 pending.ascensionMax
```

`ascension` 页的附加字段（实测）：
- `ascensionMax` — 游戏自己算的解锁上限（`NAscensionPanel._maxAscension`，含角色进度 + 进阶 epoch）
- `ascensionCurrent` — 当前指针位置
- `ascensionSource` — 这个上限从哪读来的，便于判断是否可信
- 读不到上限时只会给 `Ascension 0`，并写明「无法读取解锁等级」

答 ascension 那一页才算真正开局（返回里 `started:true`），这一页不要和角色页一起答。

`candidates` 里的 key 以 `/request` 返回为准，别背文案。进阶等级从该角色记忆文件开头的
`> 当前进阶：N` 读（`%APPDATA%\SlayTheSpire2\guide\<角色>\memory.md`）：**从低往高、不跳段**，
只有该角色当前最高等级通关后才允许尝试 +1；写「未确认」就先打进阶 0，打完再回填真实数字。
答完一页要等下一页的 `stage` 出现再答，不要把三页的 key 塞进同一个 `picks`。

## 每幕开局：先读全图，自己定路线（永不猜坐标）

**每一幕的第一件事**（`actFloor:0` + `room:"Map"` 是刚切幕的**正常状态**，不是卡死）：

1. `sts2_map`（= `GET /map`）拿整张图：`rows` / `cols` / `boss` / `points[]`（每个点带
   `children`「往下能到哪」和 `parents`「从哪来」）/ `available[]`（带 `edge`：`start`/`up`/`left`/`right`）。
   工具列表里还没有 `sts2_map` 时直接走 HTTP 也一样：`Invoke-RestMethod http://127.0.0.1:47821/map`。
2. **自己用 `children`/`parents` 在脑内走一遍**，从起点（或当前节点）到 `boss` 选一条路线，然后
   **明确写下来、说出口**（鲸鱼气泡或发给主 agent 的进度消息里一句就够）：
   「这幕走右线：3 战斗 → 火堆 → 精英 → 商店 → Boss」。写不清楚就是还没想清楚。
3. 之后**每一步只用 `sts2_map_next`（= `GET /map/next`）的 `canMove`**：`move` 的 `col`/`row`
   只能从 `canMove`（或 `/map` 的 `available`）里抄，**永远不要拿 `points` 猜坐标**。
   走不动就看 `cannot[]` 的 `reason`（`不相邻` / `已走过` / `同一层不能横走` / `起点只能作为第一步`），
   别对同一个坐标试第二次；`canMove:[]` + `inRoom:true` 说明人在房间里，先 `/request`+`/respond`
   处理完再 `/proceed`。
4. 路线中途要改（血掉太多、拿到关键遗物、精英太狠、商店没钱）就**重新 `sts2_map` 重排一次**，
   并说明**为什么改** —— 改路线是决策，不是迷路。

路线怎么选（判断依据，不是背诵表）：

| 因素 | 怎么用 |
| --- | --- |
| 血量 / 牌组强度 | 决定吃几个精英：能稳定挡 20+/回合才谈第二个精英；血 <50% 时精英改道 |
| 火堆 `RestSite` | 尽量排在**精英或 Boss 之前**（回血/锻造再去打硬仗）；排在精英后面价值减半 |
| 商店 `Shop` | 用来**删牌**（比多一张平均牌值）或补关键牌/药水；钱 <100 时优先级下降 |
| 宝箱 `Treasure` | 白拿遗物：优先级**低于火堆、高于普通战斗**，顺路才绕 |
| 事件路线（`Unknown` 那类） | 低血量适合赌收益（可能回血/给遗物）；高血量不如稳定打怪 |
| 普通战 `Monster` | 拿卡和金的主要来源；连续 4+ 普通战要有火堆兜底 |

硬规则：

- 一幕只规划一次，之后每步按 `canMove` 走；`edge` 只是方向感，**不是指令**。
- `available`/`canMove` 是游戏自己的判定（`MapTravel.GetTravelablePointsFrom`），比任何推断都准。
- 刚切幕 `actFloor:0` + `room:"Map"` + `available` 只有起点 = 正常；`available` 一直空、或报
  「地图状态已经坏了」才是真问题（按错误提示处理，别重试坐标，重试只会把 visited 写脏）。

## The one rule that keeps runs alive: verify, don't assume

Batch a turn with `sts2_acts` (a list of actions, executed one by one, returning a compact
turn summary). Each play is verified server-side: the card's uid must leave the hand and the
board must change. A batch **stops early with `stopped`** when a play did not take effect —
that is a real signal (energy ran out, a choice screen opened), not a fluke. Never assume the
remaining actions ran.

Practical consequences:

- Read the hand right before acting. uids are per-object and change when cards are drawn,
  transformed or shuffled. A rejected `no card with uid … in hand` means re-read, not retry.
- `end_turn` cancels queued player actions. Always let the batch finish, then check the turn
  number advanced.
- Prefer one `/acts` call per turn over several `/act` calls: fewer round trips, same safety.

## Reading the numbers correctly
- **Intent damage is the modified value.** It already includes Strength, Weak, etc.
  If you want to reason about it, compare `damage` with `base` and check the enemy's `powers`.
- **Card cost is what it costs right now** (`cost`), while `baseCost` is the printed value.
  Cost-raising effects (e.g. a debuff) make otherwise cheap cards expensive.
- **`freeAttacks`** on the player counts *single-use* free-attack charges. A charge makes one
  attack free, not every attack — do not plan a whole turn as if all attacks were free.
- Multi-hit attacks carry `repeats`. `damage x repeats` is the incoming total before Block.
- Block resets each turn; unspent Block is wasted. Defends that prevent 0 damage are wasted
  draws.
- **`playable` / `unplayableReason` / `unplayableReasonCode`**（`/state` 每张手牌都有，实测）：
  打之前先看 `playable`。`false` 时原因在 `unplayableReason`，机器名在 `unplayableReasonCode`：
  - `能量不足` / `EnergyCostTooHigh` — 这回合付不起
  - `星费不足` / `StarCostTooHigh` — 储君的星星不够
  - `没有存活的队友` — 召唤物死了，这类牌打不出
  - `条件不满足` — 牌面条件没达成（例如「华丽收场」要求**抽牌堆为空**）；
    具体条件去看卡牌描述，别猜
  - 打 `playable:false` 的牌，`/acts` 会直接拒（`reason:"unplayable"`），后面整批都不执行 —
    所以**先把能打的打完再考虑它**，或者等条件满足的那一回合（打之前再读一次，`playable` 会实时
    反转，别缓存）。
- **跳过回合要显式确认**（实测）：`end_turn` 时如果还有能量、手里又有能打出的牌，`/acts` 会返回
  `reason:"energy_left"` + 「还有 N 点能量、手牌里还有 M 张能打出（牌名…）」，**不会真的结束回合**。
  确定要跳过（那些牌确实没用，比如只剩防御而对面这回合不打人）就重发 `{"kind":"end_turn","confirm":true}`。
  这条是为了防「白丢一整个回合的输出」。- **`handComplete`**（`/state` 玩家字段，实测）：发牌动画没播完时手牌会少几张（T+0ms 看到 4 张、
  T+60ms 才 5 张）。`handComplete:false` 就别决策，等 100~250ms 再读；`true` 才是完整手牌。
- **`combat.summons` 是我方召唤物**（亡魂契约师的奥斯提等）。它们占场、能挡也能打：算承伤时
  把它们当额外血条/格挡源，算输出时把它们的伤害加进去，别只盯着 `players[0]`。

## How to actually play well (conservative defaults)

1. **Survive first.** Compute incoming damage, compare with HP, and only take damage you can
   afford. Dying at 40 HP because "the fight was almost over" is the most common loss.
2. **Kill beats block** when the kill prevents the hit entirely (enemy at ≤ your total damage).
3. **Block exactly enough**, not more: apply Block equal to the incoming damage, then spend
   the rest on damage.
4. **Target the biggest threat** — highest imminent damage first, not lowest HP.
5. **Shops**: relic/potion/card purchases and the paid card-removal service are all real.
   Removal is worth more than an average card in a thin deck.
6. **Rest sites** only offer Rest (heal) and Smith (upgrade). Card removal is a shop service.
7. **Potions**: read `sts2_potions` before every use (`uid` changes when the game reloads), and
   keep at least one for an emergency. In combat they go through the action queue; out of
   combat (shops, events) the mod calls the model entry directly, which is what lets you throw
   the Foul Potion at a merchant. Outside combat prefer the stable `key` (e.g. `力量药水#1`)
   over `uid` for `sts2_use_potion` / `sts2_discard_potion`.
   **Potions full + a potion reward**: the mod answers `ok:false` with `warning:"potions-full"`
   and the current keys — that is information, not an instruction. Decide yourself: keep the
   slots for a fight you're about to enter, or trade out a potion you will not use (a weak
   damage potion in Act 3, a duplicate) for the new one. Do not auto-discard just to make room.
8. **Elites**: only with a deck that can block ~20/turn or an escape plan. Otherwise take the
   longer path.
9. **Spend the energy before ending the turn.** Leaving 1–3 energy unused is almost always a
   mistake: extra Block still absorbs damage, extra attacks still chip. End the turn only when
   the hand genuinely cannot help (all 0-value cards, or you are deliberately holding a card
   for next turn's combo — say so out loud when you do). `/acts` can play several cards in one
   call, so batch 2–3 plays and check `energy` afterwards.

## Narrate through the whale

The mod draws a whale companion in the bottom-right corner. Before each meaningful step, say
what you are about to do in one short sentence, in the user's language:

```
sts2_whale_say { "text": "敌人这回合打 12，我先叠 5 点格挡，再用余烬打回去。", "ms": 15000 }
```

Keep it to what a person watching the screen needs: the threat, the plan, the reason. Do not
paste state dumps or raw uids into the bubble. If there is nothing worth saying, stay silent
rather than narrating trivia.

## Memory: leave a trail for the next run

记忆只有一处：攻略目录（guide）下的 `memory.md`。旧的 `agent_memory.md` 和
`ascension_progress.md` **已废弃并改名归档为 `*.bak-20261024`**，别再往那两处写。

```
%APPDATA%\SlayTheSpire2\guide\memory.md              跨角色通用记忆
%APPDATA%\SlayTheSpire2\guide\<角色>\memory.md        该角色专属记忆 + 文件头「当前进阶」
```

角色目录：`ironclad` / `silent` / `defect` / `regent` / `necrobinder`。跨角色的结论只写根目录那份，
角色专属的只写角色那份，别两边抄。

每个文件固定三段：`## 好的决策` / `## 坏的决策` / `## 通用经验`。一条一个判断，追加到**对应小节的
末尾**（不是文件末尾），同一天同一条不重复写，格式：

```md
- 2026-01-01 · Act1 第3层 · 敌人 22 血、我 3 能量 3 张打击 → 直接全打，零伤害收掉（杀优于挡）。
```

Rules for the memory files:

- 只写能复用的判断和**为什么**，带具体数字（血量、能量、敌人伤害），不写流水账。
- 开局先读根 `memory.md` + 本角色 `memory.md`：照「好的决策」打，避开「坏的决策」。
- 进阶写在角色文件头 `> 当前进阶：N / 未确认`：从低往高、不跳段，只有在该档**通关**才 +1，
  死亡不动，没确认过就写「未确认」按进阶 0 开局。
- 直播链路里这些是自动写的：控制台 `POST /api/run-end` 会往 `guide/<角色>/memory.md` 追加一条
  （通关写「好的决策」、死亡写「坏的决策」），通关时顺便把「当前进阶」+1。那种场合不用自己写文件。

## State is pushed to you — don't poll

The bridge pushes instead of waiting to be asked:

- **Combat**: the moment the player can act (new turn, or the action queue settles) you receive
  a `[回合快照] T3 你28/80 能量3 | 手牌: 打击(c1,Damage=6) ... | 敌: [0]蛮兽 46(Attack14x2)` message.
  It already contains hand uids, costs, card numbers, enemy HP, block and intents. **Act on it
  directly**; do not call `sts2_state` to re-read what you were just given.
- **Screens**: entering card-select / shop / rest pushes a `[游戏事件]` line with what is
  selectable.

So the loop is: receive a push → decide → call the action tools → wait for the next push.
Only call `sts2_state` when you genuinely lost track (e.g. after a long tool detour), not every
turn — a stale read is worse than the pushed snapshot because it may describe an older board.

The push has no damage math. When the turn needs the numbers (multiple enemies, 易伤/多段/斩杀线,
or "can my hand block this"), follow the push with ONE `sts2_view`: it returns the incoming total,
per-card damage against each enemy, `BLOCK_POOL`, `ORDER` and `COPIES`. Use `sts2_state` only when
you need raw JSON fields that `sts2_view` does not print.

If two pushes arrive back to back, the later one wins.

## Fast paths — one call instead of three

Hesitation is the main source of "lag". These are the whole sequences; do not probe extra steps
before trying them.

| Goal | Do exactly this |
|---|---|
| Play a turn | `sts2_acts({actions:[{kind:"play_card",card:"打击#2",target:0}, …, {kind:"end_turn"}]})` — one call, 2–4 cards；`card` 用 `sts2_view` 给的 `标题#序号`（比 uid/下标稳），verify `energy` in the returned turn |
| End turn with nothing useful | `sts2_acts({actions:[{end_turn}]})` |
| Take a reward | `sts2_request` → `{"kind":"reward","stage":"rewards-list"}` → `sts2_respond {id, picks:[key]}`（**`picks:[]` = 跳过剩余并关屏**，返回里看 `closed`；药水槽满就选候选里的「离开奖励#1」）。领到卡牌奖励后会接着来一条 `stage:"card-choice"`，同样用 respond 回答，或 `picks:[]` 放弃 |
| Discard / exhaust / upgrade a card | `sts2_request` → `{"kind":"...","candidates":[{key,title}]}` → `sts2_respond {id, picks:["打击#2"]}`；成功是 `ok:true` + `applied` |
| Multi-pick screen | one `sts2_respond` with several keys: `{id, picks:["打击#1","防御#2"]}`（min/max 由请求给出）|
| Leave a node | `sts2_move` with a coord from `sts2_map_next` 的 `canMove`（= `/map` 的 `available`）—— 别猜 |
| 看整张图 / 定路线 | 每幕开头一次：`sts2_route`（直接给 opt 排名 + 符号序列 + `ELITE_GATE`）→ `sts2_route {commit:<opt>, note:"…"}` 记计划 → 之后每个节点先 `sts2_plan`（`PLAN_OK` 走 / `REPLAN` 重排）；`canMove` 永远来自 `sts2_map_next` |
| Buy in a shop | `sts2_request` → `{"kind":"shop","stage":"closed"}` → `sts2_respond {id,picks:["打开商店#1"]}` 打开面板 → 下一次 `/request` 给 `stage:"items"`（每项带 price/affordable/detail/modelId）→ `sts2_respond {id,picks:[key]}` 购买；**买完/不买了就 `picks:[]` 离开**（关面板，返回 stage:"left"），再 `sts2_proceed` 或直接 `sts2_move` |
| Drink / throw a potion | `sts2_potions` 拿 `key` → `sts2_use_potion({potion:"力量药水#1", target?})` / `sts2_discard_potion({potion:"..."})`；需要目标时返回 `needTarget:true` + 一条 `kind:"potion"` 请求，用 respond 选目标或 `picks:[]` 取消 |
| 不认识的能力 / 看全部遗物 | `sts2_power {id:"POWER.SLIPPERY_POWER"}`（或 `{title:"滑溜"}`）；`sts2_relics` 一次列全自己身上的遗物（带 title/description） |
| 有覆层挡住界面 | `sts2_close_overlay` 关栈顶（地图/奖励屏/商店面板/牌库/选牌网格），返回新的 `layerStack`；`sts2_screen` 里先看 `layerStack` 判断谁在最上面 |
| 评估升级收益 | `sts2_card_info {id}` 的 `upgraded` 块，或直接用候选上的 `upgradePreview`（只列变了的数值） |
| Start a run | `sts2_request` → `{"kind":"menu","stage":"main"}` → respond 单人模式 → `stage:"mode"` → 标准模式 → `stage:"character"` → `"silent#1"` → `stage:"ascension"` 选 `Ascension N#1`（N ≤ `ascensionMax`，N 看 `guide/<角色>/memory.md` 的「当前进阶」）；全程不点坐标，答完 ascension 才真正开局 |

Rules that cut the round-trips:

- The push already contains hand uids and enemy indices — act on it, do not call `sts2_state` first.
- 所有「游戏在等你决定」都用 `sts2_request` → `sts2_respond {id, picks:[key]}`；`sts2_screen` / `sts2_tap` 只是调试兜底，正常流程不要用。
- One `sts2_acts` call can hold the whole turn. Do not call `play_card` three times in a row.
- 一次 `sts2_acts` 可以装下整回合；不要连发三次 `play_card`。批次返回 `ok:false` + 「能量不足」说明你算错了费用，不是界面卡住。
- Never `end_turn` on a "read returned empty hand" snapshot; the deal animation is still running.

## 一局什么时候才算「结束」

**只有回到主菜单（`inRun:false`）才算这局真的结束。** 死亡结算界面还在播动画时不等于结束：

- `kind:"gameover"` 的 `stage` 会从 `summary` → `page`，要走完「继续」→「主菜单」两步。
- 收尾顺序：`/request` 拿 `gameover` → `/respond` 答「继续」→ 再拿一次 `/respond` 答「主菜单」→
  `GET /state` 确认 `inRun:false` → 这才算结束，才能开下一局。

## `/start` 已禁用：开新局只能走菜单协议

`POST /start`（`sts2_start` 工具）**默认直接返回 `REFUSED`，一个请求都不会发出去**
（只有启动 DSH 前设了 `STS2_ALLOW_START=1` 才会放行）。原因（实测）：

```
它只建对局模型、不生成地图、不切场景
→ 产出 inRun:true 但 room:null / rows:0 的幽灵局
→ 这局会把后面所有通道一起封死：
   菜单协议 selector 装不上（RunManager 认为在局里）
   /start 被 "a run is already in progress" 拒
   /save-quit 被 "CurrentRoom=null" 拒
   /reload 被 "先回主菜单" 拒
→ 唯一恢复办法：手动重启游戏
```

所以开新局**唯一正确的方式**：

```
GET /request → kind:"menu"
  stage=main        → 单人模式#1
  stage=mode        → 标准模式#1
  stage=character   → silent#1 这类 key
  stage=ascension   → Ascension N#1
返回里看 "room" 和 "halfInit"：正常情况下 room 是 "Event" 或 "Map"、halfInit=false
```

看到 `halfInit:true` 就别动这局（不要 move、不要 save-quit），直接退回主菜单重开。

## 存读档：错一步就回滚，别杀进程

| 目的 | 调用 |
|---|---|
| 存盘并回主菜单 | `sts2_save_quit`（= `POST /save-quit`）→ `{ok,saved,room:"MainMenu",hasRunSave,wasRoom,wasFloor,…}` |
| 读档继续 | `sts2_reload`（= `POST /reload`）→ `{ok,inRun:true,how:"continue-button",…}` |

做出错误决策（选错牌、走错路、关键药水丢了）时：`/save-quit` 存盘回主菜单 → `/reload` 读档
继续。**不要杀进程**：那会丢 Steamworks 状态，下次还得等一分钟桥接。

两条实测要点：

1. **落点是「当前房间的存档点」**，不是「回到这场战斗开头」。战斗中存的档，读回来还在那个房间。
2. `reload` 返回里的 `actFloor/totalFloor` 是**刚进局的中间状态**，不准；读档后要再
   `sts2_state` 看真实 `floor/room/hp`。

存读档之后所有 uid / key 都作废，重新 `sts2_state` / `sts2_request`。

## 决策请求（`sts2_request` / `sts2_respond`）

游戏需要玩家做决定时会挂一条 pending，`kind` 有这些：

```
menu        主菜单/模式/角色/进阶页（stage: main / mode / character / ascension）
reward      战斗奖励（stage: rewards-list 选要拿的项；picks:[] = 跳过剩余并关屏；stage: card-choice 三选一，picks:[] 可放弃）
treasure    宝箱（stage: chest-closed 先开箱 → stage: relics 再决定拿不拿 → stage: empty 空箱，给「继续#1」出口）
shop        商店（stage: closed 先开门 → stage: items 买；picks:[] = 离开商店并关闭面板，返回 stage:"left"）
rest        营火（休息/锻造/挖掘…选项全是动态的，看 candidates）
event       事件选项（detail 有完整效果说明；isProceed=true 是「离开」）
gameover    死亡/胜利收尾页（stage: summary → page；「继续」→「主菜单」，零坐标走完）
divination  水晶球占卜（每格带 revealed / contentType / grid，次数看 divinationsLeft）
proceed     「跳过 / 前进」按钮（战斗结束但奖励没弹、宝箱跳过、离开房间都走它）
potion      药水需要选目标（needTarget 时给目标下标，或 picks:[] 取消）
bundle      选一捆（stage:"bundles"，候选是「整包」不是单张；requireConfirm:true 会自动按 √）
simple / upgrade / remove / transform / enchant / exhaust / discard
            卡牌选择族（min/max 由请求给出，多选就一次给多个 key）
```
bundle 的两个细节（实测）：
- 每个候选带 `cards:[牌名…]` / `count` / `detail:"共 N 张：…"`，不用点进去就能比较
- 应答内部会「点这一捆 → 按界面自己的 √」，返回 `selected / confirmed / screenClosed`；
  `screenClosed:false` 就是没真交出去，重来一次

`/acts` 失败时看顶层 `reason`（实测值）：`choice`（弹了选牌，带 `pendingId`，去 respond）/
`no_effect`（牌打出去了但棋盘没变，如 0 费抽牌）/ `uid_gone`（uid 失效，重读）/ `energy`（能量不足）/
`unplayable`（`playable:false`，看 `unplayableReason`）/ `dead`（你死了，去走 gameover）/ `other`。

占卜怎么打：`kind:"divination"`，只挑 `revealed:false` 的格子（翻过的别再翻，一样扣次数）；`divinationsLeft` 到 0 时游戏会自己转成 reward，不要硬点。

查卡面不用猜 id：`/request` 的候选现在带 `modelId`（`CARD.X` / `RELIC.X` / `POTION.X`）和 `detail`
（卡牌候选直接带牌面文字）。有 `modelId` 就 `sts2_card_info {id:<modelId>}`；只拿得到中文名时用
`{title:"主宰"}`（同名多张会回 `ambiguous + matches`，挑一个 id 再查）。`uid` 只收**实体 uid**（8 位）；
屏幕控件 uid（60 位）会明确报错。`sts2_relic` / `sts2_potion` 同样支持 `title`；`sts2_respond` 也接受 `modelId`。


**Never take a relic, potion or card reward blind.** `sts2_state` only gives ids
(`RELIC.MAW_BANK`, `POTION.FRUIT_JUICE`, `CARD.STRIKE_SILENT`); call `sts2_relic` /
`sts2_potion` / `sts2_card_info` to learn what it actually does before choosing. The description
is the same text the game shows, so it is the ground truth for "is this worth 200 gold".

**buff / 遗物列表（0.3.0 起）**：`sts2_view` 的 powers 现在带中文名；不认识的能力用
`sts2_power {id:"POWER.SLIPPERY_POWER"}`（或 `{title:"滑溜"}`）查全文案 —— 别靠掉血反推。
自己身上的遗物用 `sts2_relics`（无参数，一次列全，带 title/description），
`sts2_relic {id}` 只留给商店/宝箱里**还没拿到**的遗物。

**升级值不值（0.3.0 起）**：`sts2_card_info {id}` 会多返回 `upgraded:{title,text,vars}`；
每个卡牌候选还带 `upgradePreview`（只列变了的数值，如 `{"Damage":"6 → 9"}`）——营火锻造、武装、
奖励三选一都直接看这个，别再靠记忆。

**界面层与覆层（0.3.0 起）**：`sts2_screen` 现在给 `layerStack`（地图 / 奖励屏 / 商店面板 / 牌库 / 选牌网格）、
`canvas` 尺寸，以及每个控件的 `offscreen`。被某层挡住时用 `sts2_close_overlay` 关栈顶那一层
（商店→返回键、奖励→跳过键、地图→顶栏地图 toggle、牌库→capstone、选牌→ESC），再继续。
`sts2_tap` 对画布外控件会返回 `offscreen:true` + `warning`：**别把它当点中了**。

**Boss 是谁（0.3.0 起）**：`sts2_map` 的 `boss` 带 `encounterId` / `title` / `tags` / `monsters`
（第二 Boss 在 `secondBoss`）——每幕开局就按 Boss 类型定构筑，不用走到房门口才知道。

**奖励屏 / 宝箱的出口（0.3.0 起，以前会卡死）**：

- **奖励屏**：`picks:[]` = 跳过剩余奖励**并关屏**（会真去点界面自己的跳过/继续键），返回里看 `closed`。
  只有药水、而药水槽又满了的时候，选候选里的 **`离开奖励#1`**（`id:"leave"`）——那是唯一出口。
  如果 `/request` 还返回一个「界面已经不在」的 pending，直接再读一次：判活已经改成「节点不在或不可见」，
  幽灵 pending 会自己消失。
- **Boss 奖励会晚 2~3 秒弹屏**（0.3.1）：这段空档 `/request` 给的是 `kind:"reward"` + `stage:"incoming"`
  （候选为空，**只在战斗房间里出现**）——**这表示「等」，不是「没事干」**。`sts2_proceed` 这时会被拒绝
  （`rewardsPending:true`），`kind:"proceed"` 通道也不会再给 `跳过#1`。
  正确做法：等 0.5~1 秒重读 `sts2_request`，等它变成 `stage:"rewards-list"` 再领；
  真拿不了才用 `sts2_proceed {force:true}`。在 Map 上看到 `incoming` 属于异常，直接重读。
- **宝箱**：`stage:"chest-closed"` 在**空箱**（白银熔炉）时也会照常出现 —— 先 `picks:["打开宝箱#1"]` 开箱；
  开完没有遗物会转成 `stage:"empty"` +「继续#1」，`picks:[]` 也等于继续。**不要**因为 `pending:null`
  就反复 `proceed` 撞运气：先 `sts2_request` 看有没有 `treasure` 阶段。

`sts2_screen` 里每个控件也有自己的 `kind`（`button` / `clickable` / `button-like` / `holder`），
别和 request 的 `kind` 混了；uids 只对当前节点有效，界面一变就重新读。带 `card` 的是卡牌；
带 `label` 的是商店货（名字+价格）或角色卡（ironclad / silent / ...）。

### The confirm rule (this is where runs get stuck)

**A card selection is not committed by tapping the card.** The ✓ that commits it sits at
x≈1940 (outside the 1920 canvas) before a pick and slides to x≈1760 after one. So:

- Read the decision with `sts2_request`, answer with `sts2_respond` — never hunt for uid 再点。
  `/respond` 只有真的改变了什么才回 `confirmed:true`。
- `confirmed:false` with `remaining:N` means **the screen is multi-select and wants more picks**:
  call it again with another card's uid (or pass all the uids at once).
- Never hard-code the ✓ position and never conclude "stuck" before checking `confirmed`.

### Playbooks

- **Character select**: 走菜单通道 —— `sts2_request` → `kind:"menu"`，按 `stage` 一页页答到
  `ascension`，key 用返回的 `candidates`（`silent#1` 这类），别假设最右边是某个角色（`random`
  就是随机）。**不要用 `sts2_start` / `POST /start` 开局**：它已禁用，只会造幽灵局（见上文）。
  开完看返回里的 `room`（应是 `Event`/`Map`）和 `halfInit`（应是 `false`），再用 `sts2_state` 复核。
- **Discard effect (e.g. Silent's 生存者)**: playing the card turns **your hand itself** into the
  candidates — no separate overlay appears, and `kind=card_select` is NOT pushed. So: play the
  card → `sts2_request` → `sts2_respond {id, picks:["<key>"]}`。Hand shrinks by one
  and `confirmed:true` comes back. Until you do this the action queue is blocked: every later
  play_card / end_turn looks like it silently fails.
- **Combat rewards** (pushed as `kind=reward`): claim in order — 金币 → 药水/遗物 →
  「将一张牌添加到你的牌组」 → `sts2_respond` 选一张三选一（或 `picks:[]` 放弃）→ 跳过 or 前进.
  Claiming nothing is the same as throwing the fight away.
- **Card removal / upgrade / transform / put-on-top-of-draw-pile**: `sts2_request` → `kind` 是
  `remove` / `upgrade` / `transform` / `simple` 之一，`candidates` 就是可选牌 →
  `sts2_respond {id, picks:[key]}`。商店的删牌会在你确认之后才扣钱.
- **Rest site**: 休息 or 锻造 (smith = pick a card, then confirm).
- **Shop**: items 每项带 `price` / `affordable` / `detail` / `modelId`；`sts2_respond {id,picks:[key]}` 买。
  **离开商店 = `picks:[]`**（或选候选里最后那条「离开商店#1」）：它会去点面板自己的返回键把面板关掉，
  返回 `stage:"left"` + `closed:true`；关掉之后**不会再弹「要不要打开」**，前进键也会恢复。
  然后 `sts2_proceed` 打开地图页，再 `sts2_move`（**move 只在地图页有效**：关面板不等于在地图页；
  `proceed` 会先试前进键，没变化就自动点顶栏地图按钮，返回里 `mapOpen:true` 才代表地图页开了）。
  别再像旧版本那样「picks:[] 只表示不买」：那会把面板留着、前进键禁用、原地循环同一个请求。
- **Leaving any node**: 先 `sts2_map_next` 拿 `canMove`，`sts2_move` 只用里面的坐标。走不动时
  看 `cannot[]` 的 `reason`（`不相邻` / `已走过` / `同一层不能横走`），别对同一个坐标试第二次；
  `move` 失败时错误里也带完整 `available`。三条硬规则（实测）：
  - **人还在房间里**（事件/营火/商店/宝箱/奖励）→ `move` 会被拒，提示先用 `/request` 处理完再
    离开。这是防止「一整间事件被跳过」的护栏，不要绕过（`/map/next` 会先给 `inRoom:true`）。
  - **`move` 只在地图页有效**：在任何房间里点了也没用。正确顺序永远是
    「房间里处理完 / 关面板 → `sts2_proceed`（必要时它会点顶栏地图按钮开地图）→ `sts2_move`」。
  - **战斗还没结束** → `move` 会被拒（会跳过整场战斗和奖励）。
  - 例外：事件/奖励处理完、游戏已经回到地图但 `CurrentRoom` 还挂着旧房间对象时，`move` 是允许的。
    判据由 mod 自己做（看房间界面节点还在不在），你不用区分。
- **每一幕开头**：`actFloor:0 + room:"Map"` 是**正常状态**（不是卡死）。先 `sts2_map` 拿全图、
  定路线（见「每幕开局」一节），然后按 `sts2_map_next` 的 `canMove` 走；`available` 只给起点
  节点也是对的。只有错误里出现 `actTransition:true` + 「地图状态已经坏了」才需要重开。
- **Playing a card that opens a choice mid-combat** (e.g. Ironclad 坚毅+): `sts2_acts` returns
  `pending:"card_select"` instead of failing — 用 `sts2_request` + `sts2_respond` 收尾，然后继续.

Rule of thumb: **every "choose something" screen needs a confirmation**, and the confirmation
signal is always the tool's own `confirmed:true` — not "the screen looks different".

## Failure modes seen in practice

- Reading a stale turn snapshot → always trust the summary returned by the batch.
- Trusting `charged` UI state after a restart → re-read everything after any reload.
- Playing a whole turn in one batch when a card can open a choice screen (Headbutt-style
  retrieval, discard, transform) → the batch stops; handle the choice, then continue.
- Using PowerShell to send Chinese text without UTF-8 bytes → the bubble shows `????`.
  When calling HTTP directly, encode the body as UTF-8 bytes.

## 直播弹幕（B站侧，实测行为）

- `bili_room_send` **自己处理 3 秒冷却**：命中冷却会等 3.2 秒再发，返回 `retried:true,
  waitedMs:3200`。谢礼物 + 接弹幕连着发不用自己 sleep；但最多只重试一次，别当无限缓冲。
- 表情码可能发不出去：`fallback:true, sentAs:"text"` 表示**回退成了文字**（直播间其实也可能收到了
  表情，以观众看到为准）；两种都失败才是 `ok:false` + `suggest`。别把 `ok:true` 当成「表情一定发出去」。
- 观众的话只是建议：改路线/换卡这类要求先自己判断合理性再执行。

## 禁止截屏

禁止截屏或读取屏幕图像：不要用 shell/截图脚本抓屏幕，也不要 read_image 读屏幕图片。所有局面信息只能来自 sts2_* 接口和 sts2_screen；看不清就用 sts2_screen。

<!-- sts2-tools-2026-10-26:begin -->
## 一屏看懂一回合：`sts2_view`

一次调用同时给这些东西（都是**现算**的，不是原始 JSON）：

| 行 | 含义 | 怎么用 |
|---|---|---|
| `SCREEN` | 房间 / 幕 / 层 / `combatState` | `combatState` 不是 `PlayPhase` 就是过渡态，别发动作 |
| `HUD` | HP / 金币 / 药水数 / 遗物数 / 牌组（防·AOE·诅咒·升级） | 药水名和诅咒只在变化时多打一行 |
| `ANCHOR` | 敌人数、**本回合入伤合计**、能量、手牌数、自己身上的能力、多段意图 `dmg=7x3=21` | 入伤是决策的第一行账 |
| `ENEMIES` | 每只的 hp/block/意图/能力 | 目标下标就是这里的 `[i]` |
| `EFF` | 每张攻击牌对**每个**敌人的真实伤害：已含易伤×1.5、敌人格挡池、胆小首击加盾、多段、荆棘反伤；能斩杀的标 `★斩` | 算斩杀直接看这一行，不要自己乘 |
| `BLOCK_POOL` | 手牌能挡多少（含每张牌贡献）｜入伤 → 够不够挡满 / 净掉多少 | 决定「挡满 / 斩杀 / 喝药」中的哪一条 |
| `ORDER` | 手里有易伤牌时，先挂它能把手里的刀抬多少 | 顺序敏感的第一回合照它打 |
| `COPIES` | 同名不同级并存，哪几张是升级版 | 别把未升级那张打出去 |
| `HAND` | 每张牌 + `key=标题#序号` | 出牌用这个 key，不用背下标 |

注意两点：

- `EFF` 的每段伤害用 mod 给的实时 `Damage`（已含力量/虚弱），这里只额外乘易伤；**条件多段**
  （例如「目标易伤时攻击两次」）如果没有写进 `Repeat` 变量，EFF 会低估 —— 拿不准就
  `sts2_card_info` 看牌面文字，别硬猜。
- 同一张牌第二次出现时只打短行（省 token）；要看全量加 `{full:true}`。换手牌、换回合都不影响 key
  的解析，因为 key 是每次现场重读手牌算出来的。

## 出牌用稳定 key，不用 uid

`sts2_acts` 的 `play_card` 现在接受 `card`：

```json
{"actions":[{"kind":"play_card","card":"打击#2","target":0},{"kind":"end_turn"}]}
```

- `标题#序号`：序号按**同名牌在手牌里的顺序**数，从 1 起（`打击#1` / `打击#2`）；也认 card id。
- 插件会先读一次现场手牌把 key 解析成 uid，再提交 —— 所以抽牌、弃牌、洗牌之后 key 依然有效，
  比背 uid/下标稳。
- key 解析不出来时**一个动作都不会提交**，返回 `STOPPED before action N` + 当前可用 key 列表：
  这时重读 `sts2_view` 换 key，不要改成盲试下标。

## 牌组：`sts2_deck`

`/state` 只给 `deckSize`，看不到牌组里是什么。`sts2_deck` 给：

```
DECK 18 张 | 攻7 技8 能3 | 防御6 AOE1 升级3 诅咒1 状态0
DEFENSE 防御,防御,耸肩无视,…
AOE 顺劈斩
CURSES 疑惑 ← 下一个移除机会优先删这些
CARDS 打击x5 防御x5 …            （同名合并计数；{full:true} 打成每张一行）
```

用法：

- **商店删牌**：只看 `CURSES`（先删诅咒）和 `DEFENSE`（已经够 6 张就别删防御，删打击）。
- **拿奖励前**：`AOE 0` 就优先补 AOE；`防御 < 6` 优先补防御；`牌组 > 22` 只拿 A 级。
- 这个统计要**新的 mod DLL**（多了 `GET /deck`）；返回「DECK 不可用」就去
  `launch-game.ps1 -Deploy` 更新一次，别反复试。

## 路线：`sts2_route` + `sts2_plan`

每幕开头（`actFloor:0` + `room:"Map"` 是正常状态）：

1. `sts2_route` —— 它把每个 `available` 起点到 Boss 的所有路径都走一遍，按
   营火/商店/宝箱/事件/精英/普通 + **血线闸** + 牌组（防御/AOE）+ 药水 打分，返回前 3 名：

   ```
   ELITE_GATE 防御≥6? 是 / AOE≥1? 是 / 药水≥1? 否 ← 三项全「是」也只当备选
   opt0 Monster (3,2) score=7.2 | 营火2 / Boss前有营火
     M M R B
   ```

   `ELITE_GATE` 三项不是全「是」就别把精英当主路线；`HP<70%` / `<50%` 会各有一条 WARNING。
2. `sts2_route {commit:0, note:"走商店线，Boss 前留营火"}` —— 把 opt 记成计划。
3. 之后**每个节点移动前** `sts2_plan`：`PLAN_OK` 就按原计划走；`REPLAN` 会列出具体理由
   （血线跌破 70%、这一段掉血 ≥20% 最大生命、HP<50%、牌组 +3 张、拿到首张 AOE、药水用光、
   金币到 100、多了诅咒、换幕）。**REPLAN 就重新 `sts2_route` + 重新 commit**，并在鲸鱼气泡里
   说清为什么改线 —— 改线是决策，不是迷路。
4. 换幕 / 弃局 / 彻底改线：`sts2_plan {clear:true}`。

计划存在 `~/.dsh/storages/sts2-agent/route-plan.json`（不在仓库里），里面是**规划当时的快照**，
所以「对账」比的是状态变化，不是路线对不对。

## 存读档 / 离开房间：三个补齐的工具

| 工具 | 什么时候用 |
|---|---|
| `sts2_save_quit` | 决策错了要回滚：先把这局存盘并回主菜单 |
| `sts2_reload` | 紧接着读回来（落点是**当前房间的存档点**，不是战斗开头） |
| `sts2_proceed` | 房间里没事可做了（`sts2_request` 的 `pending` 为 null）再离开 |

存读档之后所有 uid / key 都作废，重新 `sts2_view` / `sts2_request`。

## `sts2_start` 默认拒绝

`sts2_start` 现在**默认直接拒绝**（返回 `REFUSED`，一个请求都不会发出去）：

```
REFUSED sts2_start 默认禁用：它会造出「inRun 为真但地图没生成」的幽灵局，并把菜单协议、
save-quit、reload 一起封死。
```

要绕过菜单（不推荐）时在启动 DSH 前设 `STS2_ALLOW_START=1`。日常开局仍然只走
`sts2_request` 的 `kind:"menu"`。
<!-- sts2-tools-2026-10-26:end -->
