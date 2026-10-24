using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2AgentBridge;

/// <summary>
/// Loads the run scene through NGame's own (private) entry points so the run is a real,
/// fully wired screen instead of a bare RunState. Also lets an in-progress run be attached.
/// </summary>
public static class RunAttach
{
    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static Dictionary<string, object> Inspect()
    {
        var game = NGame.Instance;
        if (game == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "NGame not ready" };

        var found = new List<string>();
        foreach (var m in game.GetType().GetMethods(Flags))
        {
            if (m.Name.Contains('<')) continue;
            if (!m.Name.Contains("Run") && !m.Name.Contains("Load") && !m.Name.Contains("MainMenu")) continue;
            found.Add($"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))}) -> {m.ReturnType.Name}");
        }

        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["inRun"] = RunManager.Instance?.IsInProgress ?? false,
            ["methods"] = found.Distinct().OrderBy(x => x).ToList(),
        };
    }

    /// <summary>Load whatever run RunManager already holds (fresh /start or a saved run).</summary>
    public static Dictionary<string, object> AttachCurrentRun()
    {
        var game = NGame.Instance;
        var rm = RunManager.Instance;
        if (game == null) return Fail("NGame not ready");
        if (rm == null || !rm.IsInProgress) return Fail("no run in progress to attach");

        var state = rm.DebugOnlyGetState();
        if (state == null) return Fail("run has no state");

        foreach (var name in new[] { "StartRun", "LoadRun" })
        {
            var method = game.GetType().GetMethods(Flags)
                .FirstOrDefault(m => m.Name == name && !m.Name.Contains('<'));
            if (method == null) continue;

            try
            {
                var args = BuildArgs(method, state);
                var result = method.Invoke(game, args);
                if (result is Task task) _ = Continue(task);
                BridgeLog.Info($"attached run via NGame.{name}({args.Length} args)");
                return Ok(new Dictionary<string, object> { ["attached"] = name, ["args"] = args.Length });
            }
            catch (Exception e)
            {
                BridgeLog.Error($"NGame.{name} failed", e);
            }
        }

        return Fail("no usable run-loading method found; call /attach/inspect");
    }

    private static async Task Continue(Task task)
    {
        try
        {
            await task;
            BridgeLog.Info("run scene loaded");
        }
        catch (Exception e)
        {
            BridgeLog.Error("run scene load failed", e);
        }
    }

    private static object[] BuildArgs(MethodInfo method, RunState state)
    {
        var parameters = method.GetParameters();
        var args = new object[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            if (type == typeof(RunState)) args[i] = state;
            else if (type.IsValueType && Nullable.GetUnderlyingType(type) == null) args[i] = Activator.CreateInstance(type);
            else args[i] = null;
        }
        return args;
    }

    private static Dictionary<string, object> Ok(Dictionary<string, object> payload)
    {
        var d = new Dictionary<string, object> { ["ok"] = true };
        foreach (var kv in payload) d[kv.Key] = kv.Value;
        return d;
    }

    private static Dictionary<string, object> Fail(string message) =>
        new() { ["ok"] = false, ["error"] = message };
}
