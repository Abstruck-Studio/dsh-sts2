# 杀戮尖塔2 Agent 套件

让 AI 自主游玩《杀戮尖塔2》的一套组件：游戏内 mod、MCP 工具层、DSH agent 预设、技能文档。

## 目录

```
sts2-export/
├── mod/                               游戏内 mod 的 C# 源码（Sts2AgentBridge）
│   ├── *.cs                           RequestApi 分 partial；GameApi/DataApi/MapApi/Batch/Potions…
│   ├── Sts2AgentBridge.csproj
│   ├── Sts2AgentBridge.json           正式清单（文件名必须 = mod id，且与 DLL 同名）
│   ├── deploy.ps1                     停游戏后把 DLL + 清单拷进 mods/
│   └── docs/request-protocol.md       语义请求协议说明
├── plugin/                            MCP server（把 mod 的 HTTP 桥接成 mcp__sts2__* 工具）
│   ├── server.mjs                     32 个工具：读局面 / 出牌 / 决策协议 / 路线 / 只读查询
│   ├── package.json
│   ├── locale/                        工具描述的中英文案
│   └── cordis.patch.yml               作为 dsh-mcp-client 插入的补丁
├── preset/
│   └── sts2-player-preset/            DSH agent 预设（只负责打牌，不含直播能力）
│       ├── package.json
│       └── cordis.patch.yml
├── skill/
│   ├── slay-the-spire-2/SKILL.md      agent 技能：怎么用工具、协议、坑（约 39KB）
│   ├── APPEND.md / PATCHES-*.json     技能的增量补丁（配 scripts/apply-skill-updates.ps1）
│   └── UPDATE-2026-10-26.md           那次更新的说明
├── docs/
│   ├── CHANGELOG-2026-10-26.md        0.1.0 → 0.3.1 的变更与验证
│   └── HANDOFF.md                     接手提示词（新会话怎么继续）
└── scripts/
    ├── launch-game.ps1                用 Steam 启动游戏并等桥接就绪（-Deploy 会先拷 DLL）
    ├── test-plugin.mjs                离线自测：假桥 + 直接 import server.mjs
    ├── apply-skill-updates.ps1        幂等安装/更新技能（参考块 + 定点补丁）
    ├── install-skill.ps1              兼容壳
    └── README.md
```

## 装回去的步骤

### 1. mod（游戏侧）

```powershell
cd mod
dotnet build -c Release
# 游戏没在跑时（会在跑的 DLL 被占用）：
.\deploy.ps1
# 或者用脚本一步到位（编译好之后）：
..\scripts\launch-game.ps1 -Deploy
```

装完 `GET http://127.0.0.1:47821/health` 应返回 `{"ok":true,"attached":true,"version":"0.3.1"}`。

> ⚠️ `mods/Sts2AgentBridge/` 里**只能有一份**声明同一个 id 的 json。加载器把该目录下每个 `*.json`
> 都当清单，多一份就会报「存在多个 id 为 Sts2AgentBridge 的模组」。`deploy.ps1` 会自动删掉遗留的
> `mod.json`（那是早期 camelCase 草稿，已从仓库移除）。

### 2. MCP 插件（让 agent 拿到工具）

把 `plugin/` 放到一个 DSH 能 `link:` 的路径，在 profile 的 `package.json` 里加依赖，然后用
`plugin_manager install_bundle <plugin 目录>` 安装；或者直接把 `plugin/cordis.patch.yml`
的内容并进 profile 的 `cordis.patch.yml`（它是一行 `dsh-mcp-client` 插入）。

装完**开新会话**工具才会出现（MCP 工具表在会话启动时注册）。

### 3. agent 预设

```
plugin_manager install_bundle <本目录>/preset/sts2-player-preset
```

装完在新建对话的预设列表里会多出「杀戮尖塔2 玩家」。

> 改完 `cordis.patch.yml` 必须 `remove_bundle` → `install_bundle`，否则加载的是旧配置。

### 4. skill

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\apply-skill-updates.ps1
```

它会把 `skill/slay-the-spire-2/SKILL.md` 拷到 `~/.dsh/skills/`（先备份），并把 `skill/APPEND.md`
+ `skill/PATCHES-*.json` 幂等地并进去。也可以手工整目录拷到 `~/.dsh/skills/`。

## 关键约定（踩过的坑，都在 skill 里）

- **开新局只能用菜单语义协议**：`GET /request` → `kind:"menu"`，依次答 main → mode → character → ascension。
  `POST /start`（`sts2_start`）默认被拒（`REFUSED`）：它只建对局模型、不生成地图，会留下
  `inRun:true / room:null` 的幽灵局，并把菜单协议、`/save-quit`、`/reload` 一起封死，只能重启游戏。
- 开局返回里看 `room`（应为 `Event`/`Map`）和 `halfInit`（应为 `false`）。
- 所有「让玩家做决定」的地方都走 `sts2_request` / `sts2_respond`，不点坐标、不截屏。
- 地图：`GET /map`（整图 + children/parents + **Boss 身份**）、`GET /map/next`（现在能走哪几个）。
- `end_turn` 若还有能量且手里有能打的牌会被拒绝（`reason:"energy_left"`），要么出完牌、要么显式
  `confirm:true`。
- 只有 `inRun:false`（回主菜单）才算一局结束。
- 局结束把「好的决策 / 坏的决策 / 通用经验」写进 `%APPDATA%\SlayTheSpire2\guide\<角色>\memory.md`。

## 0.3.1 更新摘要（2026-10-26）

工具从 ~20 个涨到 32 个，重点是「少心算、少踩协议坑」：

- **读局面**：`sts2_view`（ANCHOR / EFF 逐卡逐敌真伤 / BLOCK_POOL / ORDER / COPIES）、`sts2_deck`
  （牌组健康度）、`sts2_relics`（一次列全遗物）、`sts2_power`（buff 全文案）。
- **决策**：候选带 `modelId` / `id` / `detail` / `upgradePreview`；`sts2_card_info` 支持 `title` 与
  升级后预览；`sts2_acts` 支持手牌稳定 key（`标题#序号`）。
- **路线**：`sts2_route`（路径打分 + `ELITE_GATE`）、`sts2_plan`（计划对账）；`sts2_map` 的 `boss`
  带 `encounterId/title/tags/monsters`。
- **协议修复**：商店 `picks:[]` = 离开并关面板；奖励屏 `picks:[]` = 跳过并**真的关屏**（含「离开奖励#1」）；
  空宝箱（白银熔炉）`stage:"empty"` +「继续#1」；Boss 奖励晚弹屏时给 `stage:"incoming"` 并**拒绝 proceed**
  （`sts2_proceed {force:true}` 才是逃生口）；`sts2_screen` 带 `layerStack`/`canvas`/`offscreen`，
  新增 `sts2_close_overlay`。
- 详细变更与真机验证见 [`docs/CHANGELOG-2026-10-26.md`](docs/CHANGELOG-2026-10-26.md)。

## 许可

MIT，见 [`LICENSE`](LICENSE)。
