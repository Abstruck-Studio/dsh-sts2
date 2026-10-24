using System;
using Godot;

namespace Sts2AgentBridge;

public static class BridgeBootstrap
{
    public static void Start()
    {
        BridgeLog.Info("mod initializer called");
        BridgePump.Install();

        // 语义请求/应答：默认接管 CardSelectCmd（selector 通道，数据层最可靠），
        // /respond 时由 mod 自己播一段本地动画（亮牌 + 鲸鱼气泡 + 延迟），见 RequestApi。
        // 只有启动前显式设了 STS2_AGENT_SELECTOR=0 才退回「扫界面 + 模拟点击」的保留通道。
        RequestApi.InstallSelector();

        HttpBridge.Start();
        AgentUi.Load();
        WhaleUi.Install();
    }
}


