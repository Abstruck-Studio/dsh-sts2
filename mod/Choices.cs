using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;

namespace Sts2AgentBridge;

/// <summary>
/// 卡牌界面的**只读诊断**工具（供 /dump、/probe 和 <see cref="ScreenApi"/> 用）。
///
/// 历史：这里原来还有 <c>List()</c>（枚举可点卡牌）和 <c>Choose(int)</c>（按索引模拟点击），
/// 由旧的 <c>/choices</c> / <c>/choose</c> 路由驱动。那两条路由已经被删除 —— 选牌统一走
/// <c>GET /request</c> + <c>POST /respond</c>（selector 通道或各房间通道）。
/// 现在只留下「把节点/模型读成数据」的只读部分，**不含任何点击逻辑**。
/// </summary>
public static class Choices
{
    /// <summary>
    /// 沿节点和它的祖先找第一个 CardModel 成员。类型无关：NCardHolder / NGridCardHolder
    /// 以及以后出现的 holder 都能用。
    /// </summary>
    internal static MegaCrit.Sts2.Core.Models.CardModel CardOf(object holder)
    {
        if (holder is not Node node) return null;

        for (var current = node; current != null; current = current.GetParent())
        {
            var type = current.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var prop in type.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                if (!typeof(MegaCrit.Sts2.Core.Models.CardModel).IsAssignableFrom(prop.PropertyType)) continue;
                try { if (prop.GetValue(current) is MegaCrit.Sts2.Core.Models.CardModel card) return card; }
                catch { }
            }

            foreach (var field in type.GetFields(flags))
            {
                if (!typeof(MegaCrit.Sts2.Core.Models.CardModel).IsAssignableFrom(field.FieldType)) continue;
                try { if (field.GetValue(current) is MegaCrit.Sts2.Core.Models.CardModel card) return card; }
                catch { }
            }
        }
        return null;
    }

    /// <summary>诊断：当前可见控件的类型与数量，用来按事实给界面建模。</summary>
    public static List<object> Dump()
    {
        var counts = new Dictionary<string, int>();
        if (Engine.GetMainLoop() is not SceneTree tree) return new List<object>();

        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 30000)
        {
            var node = stack.Pop();
            if (node is Control control && control.IsVisibleInTree() && control.Size.X > 20f)
            {
                var name = node.GetType().Name;
                counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
            }
            foreach (var child in node.GetChildren()) stack.Push(child);
        }

        return counts.OrderByDescending(kv => kv.Value)
            .Select(kv => (object)new Dictionary<string, object> { ["type"] = kv.Key, ["count"] = kv.Value })
            .ToList();
    }

    /// <summary>诊断：卡牌奖励的 hitbox 到底暴露了哪些成员。</summary>
    public static List<object> Probe()
    {
        var result = new List<object>();
        if (Engine.GetMainLoop() is not SceneTree tree) return result;

        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 30000)
        {
            var node = stack.Pop();
            if (node.GetType().Name == "NCardHolderHitbox")
            {
                var type = node.GetType();
                var members = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(p => p.Name + ":" + p.PropertyType.Name)
                    .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Select(f => f.Name + ":" + f.FieldType.Name))
                    .Where(n => n.IndexOf("card", StringComparison.OrdinalIgnoreCase) >= 0
                             || n.IndexOf("model", StringComparison.OrdinalIgnoreCase) >= 0
                             || n.IndexOf("holder", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Distinct()
                    .ToList();

                result.Add(new Dictionary<string, object>
                {
                    ["node"] = node.Name,
                    ["type"] = type.Name,
                    ["parent"] = node.GetParent()?.GetType().Name,
                    ["members"] = members,
                });
                if (result.Count >= 3) break;
            }
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return result;
    }
}
