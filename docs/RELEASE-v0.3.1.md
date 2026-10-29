# v0.3.1 · 杀戮尖塔2 Agent 套件

从 0.1.0 到 0.3.1 的主要变化：工具从 ~20 个扩到 **32 个**，并把打牌过程中踩到的协议坑全部补上。

## 新增工具

| 工具 | 作用 |
|---|---|
| `sts2_view` | 一屏读局面：`ANCHOR`（入伤/能量/能力）、`EFF`（每张攻击牌对每个敌人的真实伤害，含易伤/格挡池/胆小/多段/荆棘，标 `★斩`）、`BLOCK_POOL`、`ORDER`、`COPIES`、手牌稳定 key |
| `sts2_deck` | 整副牌健康度：攻/技/能、防御、AOE、升级、诅咒/状态 + 同名合并清单 |
| `sts2_route` / `sts2_plan` | 路径打分（营火/商店/宝箱/精英 + 血线闸 + 牌组）+ `ELITE_GATE`；计划快照对账（`PLAN_OK` / `REPLAN` + 理由） |
| `sts2_power` | buff/debuff 全文案（按 id 或中文名） |
| `sts2_relics` | 一次列全自己身上的遗物（带 title/description） |
| `sts2_close_overlay` | 关栈顶覆层（地图/奖励屏/商店面板/牌库/选牌网格） |
| `sts2_proceed` | 离开房间；奖励未结算时会被拒（`force:true` 才是逃生口） |
| `sts2_save_quit` / `sts2_reload` | 存盘回主菜单 / 读回存档（回滚错误决策） |

## 协议与行为修复

- **选牌候选带 `modelId` / `id` / `detail` / `upgradePreview`**：中文标题不再是死路；
  `sts2_card_info` 支持 `title`、实体 uid 与「升级后效果」预览。
- **`sts2_acts` 支持手牌稳定 key**：`{"card":"打击#2"}`，抽牌/洗牌后仍有效；解析失败一个动作都不提交。
- **商店**：`picks:[]` = 离开并关面板（`stage:"left"`），不再原地循环；面板关着时前进键可用。
- **奖励屏**：`picks:[]` = 跳过剩余奖励并**真的关屏**（会点界面自己的跳过键），新增「离开奖励#1」；
  `/request` 判活改成「节点不在或不可见」，点掉后不再有幽灵 pending。
- **Boss 奖励**：奖励屏比战斗结束晚 2~3 秒时，`/request` 给 `stage:"incoming"`，
  `proceed` 被拒绝（`rewardsPending:true`）——修掉「Boss 奖励被整批跳过」。
- **空宝箱（白银熔炉）**：箱子没开也会发 `stage:"chest-closed"`；开完没遗物转 `stage:"empty"` +「继续#1」。
- **地图**：`boss` 带 `encounterId` / `title` / `tags` / `monsters`（第二 Boss 另给 `secondBoss`）。
- **`sts2_screen`**：新增 `layerStack` / `canvas` / 控件 `offscreen`；画布外点击不再假装成功。
- **`sts2_start`**：默认 `REFUSED`（会造幽灵局），开新局一律走菜单协议。

## 安装

1. 游戏侧：解压本 release 的 zip 到 `<游戏目录>/mods/Sts2AgentBridge/`，或自己 `dotnet build -c Release`
   后用 `mod/deploy.ps1`。
2. DSH 插件：安装 `plugin/`（见 README）。
3. 预设：安装 `preset/sts2-player-preset/`。
4. 技能：跑 `scripts/apply-skill-updates.ps1` 或把 `skill/slay-the-spire-2/` 拷进 `~/.dsh/skills/`。

> 校验：`GET http://127.0.0.1:47821/health` → `{"ok":true,"attached":true,"version":"0.3.1"}`。

## 验证

- `dotnet build -c Release`：0 warning / 0 error（本 release 的 DLL 由 tag 源码编译）。
- `scripts/test-plugin.mjs`：离线自测 **ALL PASS**（49 checks）。
- 真机：0.3.1 已在本机运行验证 —— `/health` 0.3.1、`/card` 升级预览、`/power`、`/relics`、
  `/map` Boss 身份、`/screen` layerStack、商店离开、Boss 奖励守卫（Map 上不再误报）。

完整变更见 `docs/CHANGELOG-2026-10-26.md`。
