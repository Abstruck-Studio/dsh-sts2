# 接手提示词（HANDOFF）

> 给「下一次开新会话 / 换一个 agent 接着打」用的。复制下面整段作为第一条消息即可。
> 和 `docs/live-architecture-v2.md`（架构）不同，这份只讲**怎么接手一局**。

---

你现在接手一个《杀戮尖塔 2》的 agent 项目，工作目录是本仓库（`sts2agent/`）。
你的任务：通过游戏内 mod 桥接把这一局打好，每局结束把可复用经验写回记忆。

## 开工前按顺序读

1. `sts2agent/README.md` —— 工具有哪些、哪些是调试用的、部署方式
2. `sts2agent/skill/UPDATE-2026-10-26.md` —— 压缩视图 / 牌组 / 路线 / 对账怎么用
3. 技能 `slay-the-spire-2`（`~/.dsh/skills/slay-the-spire-2/SKILL.md`；需要重装/更新就跑
   `powershell -NoProfile -ExecutionPolicy Bypass -File sts2agent\scripts\apply-skill-updates.ps1`）
4. `%APPDATA%\SlayTheSpire2\guide\memory.md` + `guide\<角色>\memory.md`
   —— 跨角色经验 / 角色专属经验 + 文件头「当前进阶」

## 先确认环境和状态

```
sts2_health                 # attached:false = 游戏没开或 mod 没加载
sts2_view                   # 一眼看房间/血量/牌组；不在局里会说 NOT_IN_RUN
sts2_request                # pending 非空 = 游戏在等你决定，先答完再干别的
```

没开局就从 `kind:"menu"` 走：main → mode → character → ascension（**不要用 `sts2_start`**，
它现在默认 `REFUSED`：会造出地图没生成的幽灵局）。

## 硬规则（实测踩出来的，别自己发明）

1. **战斗决策只信 `sts2_view`**：ANCHOR 的 `incoming` 是这回合的账；EFF 已经算好易伤/格挡/段数；
   `BLOCK_POOL` 告诉你够不够挡满。别拿 `/state` 自己乘。
2. **出牌用稳定 key**：`sts2_acts({actions:[{kind:"play_card",card:"打击#2",target:0}]})`。
   返回 `STOPPED before action N` = 一个动作都没提交，重读 `sts2_view` 换 key，别改下标盲试。
3. **`combatState` 不是 `PlayPhase` 就别发动作**；`end_turn` 被判 `energy_left` 说明还有能量/能打的牌。
4. **每幕开头**：`sts2_route` → 选一条 → `sts2_route {commit:N, note:"…"}` → 之后每个节点先
   `sts2_plan`（`PLAN_OK` 就走，`REPLAN` 就重新 route + commit，并在鲸鱼气泡里说清改线原因）。
5. **走图只从 `sts2_map_next` 的 `canMove` 取坐标**；`cannot[].reason` 明确告诉你为什么不能走，
   同一个坐标不要试第二次。
6. **一切「让你选」的界面**走 `sts2_request` + `sts2_respond`（key 用返回的 `candidates`），
   `sts2_screen` / `sts2_tap` 只在协议没覆盖时兜底。
7. **回滚用 `sts2_save_quit` → `sts2_reload`**，落点是当前房间的存档点；不要杀进程。
8. **只有回到主菜单（`inRun:false`）才算一局结束**；死亡结算动画期间不要开新局。

## 每局结束（收尾清单）

1. 走完 `kind:"gameover"` 的两页（继续 → 主菜单），`sts2_state` 确认 `inRun:false`。
2. 把「好的决策 / 坏的决策 / 通用经验」按角色写进 `guide/<角色>/memory.md`
   （一条一个判断，带具体数字）；只有**通关**才把文件头「当前进阶」+1。
3. 复述这一局最重要的一个教训（一句话），供下一局开工时先读。

## 报告口径

- 鲸鱼气泡（`sts2_whale_say`）一句话：威胁 / 计划 / 原因，不贴 uid、不贴状态转储。
- 对用户汇报时先说**结论和现在的局面**，再说你做了什么；失败要说清是哪一步、为什么。
