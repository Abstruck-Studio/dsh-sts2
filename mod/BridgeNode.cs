using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Godot;

namespace Sts2AgentBridge;

/// <summary>
/// Runs work on the Godot main thread. A code-created Node subclass does not get a script
/// instance (no Godot source generator in a plain mod assembly), so its _Process is never
/// called; the SceneTree's ProcessFrame signal is used instead.
/// </summary>
public static class BridgePump
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static SceneTree _tree;
    private static Action _handler;

    public static bool IsAttached => _tree != null;

    public static void Install()
    {
        _tree = Engine.GetMainLoop() as SceneTree;
        if (_tree == null)
        {
            BridgeLog.Error("no SceneTree; cannot install main-thread pump");
            return;
        }

        _handler = Drain;
        _tree.ProcessFrame += _handler;
        BridgeLog.Info("main-thread pump installed on SceneTree.ProcessFrame");
    }

    private static void Drain()
    {
        AgentUi.Tick();
        EventFeed.Tick();
        StatePush.Tick();
        RequestApi.Tick();
        GameApi.TickHandWatch();
        for (var i = 0; i < 64 && Queue.TryDequeue(out var work); i++)
        {
            try { work(); }
            catch (Exception e) { BridgeLog.Error("main-thread work threw", e); }
        }
    }

    public static Task<T> OnMain<T>(Func<T> fn, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(() =>
        {
            try { tcs.TrySetResult(fn()); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    public static Task OnMain(Action action, int timeoutMs = 5000) =>
        OnMain<object>(() => { action(); return null; }, timeoutMs);
}


