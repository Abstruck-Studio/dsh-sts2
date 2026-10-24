# 杀戮尖塔2 Agent 套件

让 AI 自主游玩《杀戮尖塔2》的一套组件：游戏内 mod、MCP 工具层、DSH agent 预设、技能文档。

## 目录

```
sts2-export/
├── mod/                               游戏内 mod 的 C# 源码（Sts2AgentBridge）
│   ├── *.cs                           RequestApi 分 partial；GameApi/DataApi/MapApi/Batch/Potions…
│   ├── Sts2AgentBridge.csproj
│   ├── mod.json / Sts2AgentBridge.json
│   └── docs/request-protocol.md       语义请求协议说明
├── plugin/                            MCP server（把 mod 的 HTTP 桥接成 mcp__sts2__* 工具）
│   ├── server.mjs                     ~20 个工具
│   ├── package.json
│   └── cordis.patch.yml               作为 dsh-mcp-client 插入的补丁
├── preset/
│   └── sts2-player-preset/            DSH agent 预设（只负责打牌）
│       ├── package.json
│       └── cordis.patch.yml
├── skill/
│   └── slay-the-spire-2/SKILL.md      agent 技能：怎么用工具、协议、坑
└── scripts/
    ├── launch-game.ps1                用 Steam 启动游戏并等桥接就绪
    └── README.md
```

## 装回去的步骤

### 1. mod（游戏侧）

```
dotnet build -c Release        # 在 mod/ 目录
把 bin/Release/Sts2AgentBridge.dll
  拷到  <游戏目录>/mods/Sts2AgentBridge/
启动游戏（见 scripts/launch-game.ps1）
GET http://127.0.0.1:47821/health  → {"ok":true,"attached":true}
```

### 2. MCP 插件（让 agent 拿到工具）

把 `plugin/` 放到一个 DSH 能 `link:` 的路径，在 profile 的 `package.json` 里加依赖，然后用
`plugin_manager install_bundle <plugin 目录>` 安装；或者直接把 `plugin/cordis.patch.yml`
的内容并进 profile 的 `cordis.patch.yml`（它是一行 `dsh-mcp-client` 插入）。

### 3. agent 预设

```
plugin_manager install_bundle <本目录>/preset/sts2-player-preset
```

装完在新建对话的预设列表里会多出「杀戮尖塔2 玩家」。

> 改完 `cordis.patch.yml` 必须 `remove_bundle` → `install_bundle`，否则加载的是旧配置。

### 4. skill

把 `skill/slay-the-spire-2/` 整个目录拷到 `~/.dsh/skills/` 下即可。

## 关键约定（踩过的坑，都在 skill 里）

- **开新局只能用菜单语义协议**：`GET /request` → `kind:"menu"`，依次答 main → mode → character → ascension。
  `POST /start`（`sts2_start`）已禁用：它只建对局模型、不生成地图，会留下 `inRun:true / room:null`
  的幽灵局，并把菜单协议、`/save-quit`、`/reload` 一起封死，只能重启游戏。
- 开局返回里看 `room`（应为 `Event`/`Map`）和 `halfInit`（应为 `false`）。
- 所有「让玩家做决定」的地方都走 `sts2_request` / `sts2_respond`，不点坐标、不截屏。
- 地图：`GET /map`（整图 + children/parents）、`GET /map/next`（现在能走哪几个）。
- `end_turn` 若还有能量且手里有能打的牌会被拒绝（`reason:"energy_left"`），要么出完牌、要么显式
  `confirm:true`。
- 只有 `inRun:false`（回主菜单）才算一局结束。
- 局结束把「好的决策 / 坏的决策 / 通用经验」写进 `%APPDATA%\SlayTheSpire2\guide\<角色>\memory.md`。
