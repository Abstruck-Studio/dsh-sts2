using MegaCrit.Sts2.Core.Modding;

namespace Sts2AgentBridge;

/// <summary>
/// Mod entry point. The game calls <see cref="Init"/> after the assembly is loaded
/// (see MegaCrit.Sts2.Core.Modding.ModManager.CallModInitializer).
/// </summary>
[ModInitializer("Init")]
public static class ModEntry
{
    public static void Init()
    {
        try
        {
            BridgeBootstrap.Start();
        }
        catch (Exception e)
        {
            BridgeLog.Error("bootstrap failed", e);
        }
    }
}
