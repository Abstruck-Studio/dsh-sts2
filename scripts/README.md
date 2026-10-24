# sts2agent · scripts

## launch-game.ps1

用 Steam 启动《杀戮尖塔 2》并等桥接就绪。**必须走 Steam**（`steam.exe -applaunch 2868840`），直接启动游戏 exe 会崩 Steamworks。

```powershell
# 正常启动；游戏已经在跑就只等桥接，不会重复开
.\launch-game.ps1

# 先把编译好的 mod DLL 拷进 mods 目录再启动（游戏在跑时跳过拷贝，避免文件被占用）
.\launch-game.ps1 -Deploy

# 冷启动慢的时候放宽等待
.\launch-game.ps1 -TimeoutSeconds 180
```

参数：

| 参数 | 默认 | 说明 |
|---|---|---|
| `-Deploy` | 关 | 启动前把 `..\mod\bin\Release\Sts2AgentBridge.dll` 拷到 `<游戏目录>\mods\Sts2AgentBridge\`；拷之前先确认游戏没在运行 |
| `-GameDir` | `D:\.steam\steamapps\common\Slay the Spire 2` | 游戏目录 |
| `-SteamExe` | `D:\.steam\steam.exe` | Steam 路径 |
| `-AppId` | `2868840` | 游戏 appid |
| `-BridgeUrl` | `http://127.0.0.1:47821` | 桥接地址 |
| `-TimeoutSeconds` | `120` | 等桥接的上限（秒） |
| `-IntervalSeconds` | `3` | health 轮询间隔（秒） |

行为：进程 `SlayTheSpire2` 已在运行就不重复启动，直接轮询 `/health`；否则用 Steam 启动后再轮询。就绪打印 `OK bridge=... attached=true ...`（退出码 0），超时打印 `FAIL` + 进程在不在 + 最后一次 health 返回（退出码 1）。脚本可重复执行，跑第二遍只会等到 `OK`。
