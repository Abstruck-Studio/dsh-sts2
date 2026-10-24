using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2AgentBridge;

/// <summary>地图上的一个节点：Grid 里的普通点，或 Start / Boss / SecondBoss 三个不在 Grid 里的特殊点。</summary>
internal sealed class MapNodeInfo
{
    public MapPoint Point;
    public int Col;
    public int Row;
    /// <summary>MapPointType 自己的名字；特殊点用 "Start" / "Boss" / "SecondBoss"。</summary>
    public string Type;
    /// <summary>不在 Grid 里（起点 / Boss / 第二 Boss）。</summary>
    public bool Special;
    public bool Visited;
    /// <summary>往下能到哪（游戏自己的 <c>MapPoint.Children</c>）。</summary>
    public readonly List<MapNodeInfo> Children = new();
    /// <summary>从哪来（游戏自己的 <c>MapPoint.parents</c> 公有字段）。</summary>
    public readonly List<MapNodeInfo> Parents = new();
}

/// <summary>
/// 一幕地图的只读图论快照：节点 + 父子连边 + visited。
/// <b>只读游戏数据，不改任何游戏状态</b>（没有 move、没有点击、没有写 visited）。
/// </summary>
internal sealed class MapGraph
{
    public ActMap Map;
    public readonly List<MapNodeInfo> All = new();
    public readonly Dictionary<(int col, int row), MapNodeInfo> ByCoord = new();
    public MapNodeInfo Start;
    public MapNodeInfo Boss;
    public MapNodeInfo SecondBoss;
    /// <summary>ActMap.GetRowCount()（Grid 的行数）。</summary>
    public int Rows;
    /// <summary>ActMap.GetColumnCount()（Grid 的列数）。</summary>
    public int Cols;
    public readonly HashSet<MapCoord> Visited = new();

    public MapNodeInfo At(MapCoord coord) =>
        ByCoord.TryGetValue((coord.col, coord.row), out var node) ? node : null;
}

/// <summary>
/// 整张图 / 可达节点查询的实现。数据来源（MetaPriv + MetaIl 核实过 sts2.dll）：
///
///   * 节点集合：<c>ActMap.GetAllMapPoints()</c>（遍历 Grid），加上
///     <c>ActMap.StartingMapPoint</c> / <c>BossMapPoint</c> / <c>SecondBossMapPoint</c> 三个不在 Grid 里的点。
///   * 连边：<c>MapPoint.Children</c>（public property，<c>HashSet&lt;MapPoint&gt;</c> 的 backing field）
///     与 <c>MapPoint.parents</c>（public field，<c>HashSet&lt;MapPoint&gt;</c>）。
///     <c>MapPoint.AddChildPoint(child)</c> 的 IL 同时做两件事：<c>Children.Add(child)</c> +
///     <c>child.parents.Add(this)</c>，所以两边互为镜像；这里两边都读、按坐标去重，
///     任一边被裁剪掉也还能拿到完整边。
///   * 行列数：<c>ActMap.GetColumnCount()</c> = IL 里 <c>Grid.GetLength(0)</c>（列），
///     <c>ActMap.GetRowCount()</c> = <c>Grid.GetLength(1)</c>（行）；<c>Grid</c> 是
///     <c>MapPoint[列, 行]</c> 二维数组（<c>StandardActMap..ctor</c> 里 <c>new MapPoint[7, _mapLength]</c>）。
///   * Boss 坐标：<c>StandardActMap..ctor</c> 里
///     <c>BossMapPoint = new MapPoint(GetColumnCount()/2, GetRowCount())</c>（即最后一行的下一行），
///     起点 <c>new MapPoint(GetColumnCount()/2, 0)</c>、第二 Boss <c>new MapPoint(GetColumnCount()/2, GetRowCount()+1)</c>。
///     Grid 的第 0 行是空的：<c>GenerateMap</c> 里 <c>ForEachInRow(Grid, 1, p =&gt; StartingMapPoint.AddChildPoint(p))</c>
///     用的是行号 1。
///   * visited：<c>RunState.VisitedMapCoords</c>（<c>IReadOnlyList&lt;MapCoord&gt;</c>）。
/// </summary>
internal static class MapApi
{
    // ---------------------------------------------------------------- 建图

    /// <summary>把当前这一幕读成一张图。没有地图返回 null（绝不抛）。</summary>
    internal static MapGraph Build(RunState state)
    {
        if (state?.Map is not { } map) return null;

        var graph = new MapGraph { Map = map };
        try { graph.Rows = map.GetRowCount(); } catch { graph.Rows = -1; }
        try { graph.Cols = map.GetColumnCount(); } catch { graph.Cols = -1; }

        try
        {
            foreach (var point in map.GetAllMapPoints())
            {
                if (point == null) continue;
                AddNode(graph, point, null);
            }
        }
        catch (Exception e) { BridgeLog.Warn("map: 读 Grid 节点失败（" + e.GetType().Name + "）：" + e.Message); }

        graph.Start = AddNode(graph, SafePoint(() => map.StartingMapPoint), "Start");
        graph.Boss = AddNode(graph, SafePoint(() => map.BossMapPoint), "Boss");
        graph.SecondBoss = AddNode(graph, SafePoint(() => map.SecondBossMapPoint), "SecondBoss");

        BuildEdges(graph);

        try
        {
            var visited = state.VisitedMapCoords;
            if (visited != null)
                foreach (var coord in visited) graph.Visited.Add(coord);
        }
        catch (Exception e) { BridgeLog.Warn("map: 读 visited 失败：" + e.Message); }
        foreach (var node in graph.All) node.Visited = graph.Visited.Contains(new MapCoord(node.Col, node.Row));

        // GetRowCount/GetColumnCount 读不到时按 Grid 里的点兜底（Boss/第二 Boss 不算进行列范围）。
        if (graph.Rows <= 0)
            graph.Rows = graph.All.Where(n => !n.Special).Select(n => n.Row).DefaultIfEmpty(-1).Max() + 1;
        if (graph.Cols <= 0)
            graph.Cols = graph.All.Where(n => !n.Special).Select(n => n.Col).DefaultIfEmpty(-1).Max() + 1;

        return graph;
    }

    private static MapPoint SafePoint(Func<MapPoint> get)
    {
        try { return get(); } catch { return null; }
    }

    private static MapNodeInfo AddNode(MapGraph graph, MapPoint point, string specialType)
    {
        if (point == null) return null;
        var key = (point.coord.col, point.coord.row);
        if (graph.ByCoord.TryGetValue(key, out var existing))
        {
            // Grid 里已经有这个坐标（理论上特殊点不会和 Grid 撞）：保留 Grid 的类型，只标 special。
            if (specialType != null && !existing.Special)
            {
                existing.Special = true;
                existing.Type = specialType;
            }
            return existing;
        }

        string type;
        try { type = point.PointType.ToString(); } catch { type = "Unknown"; }

        var node = new MapNodeInfo
        {
            Point = point,
            Col = point.coord.col,
            Row = point.coord.row,
            Type = specialType ?? type,
            Special = specialType != null,
        };
        graph.ByCoord[key] = node;
        graph.All.Add(node);
        return node;
    }

    private static MapNodeInfo NodeOf(MapGraph graph, MapPoint point)
    {
        if (point == null) return null;
        try
        {
            return graph.ByCoord.TryGetValue((point.coord.col, point.coord.row), out var node) ? node : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 连边。Children 是「往下走」的方向，parents 是它的镜像；两边都读。
    /// 只保留两端都在本图节点集合里的边 —— 这同时过滤掉了 MapPathPruning 裁掉的僵尸点。
    /// </summary>
    private static void BuildEdges(MapGraph graph)
    {
        var edges = new HashSet<(int fromCol, int fromRow, int toCol, int toRow)>();

        foreach (var node in graph.All.ToList())
        {
            try
            {
                var children = node.Point?.Children;
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        var target = NodeOf(graph, child);
                        if (target == null || ReferenceEquals(target, node)) continue;
                        edges.Add((node.Col, node.Row, target.Col, target.Row));
                    }
                }
            }
            catch (Exception e) { BridgeLog.Warn("map: 读 Children 失败：" + e.Message); }

            try
            {
                var parents = node.Point?.parents;
                if (parents != null)
                {
                    foreach (var parent in parents)
                    {
                        var source = NodeOf(graph, parent);
                        if (source == null || ReferenceEquals(source, node)) continue;
                        edges.Add((source.Col, source.Row, node.Col, node.Row));
                    }
                }
            }
            catch (Exception e) { BridgeLog.Warn("map: 读 parents 失败：" + e.Message); }
        }

        foreach (var edge in edges)
        {
            if (!graph.ByCoord.TryGetValue((edge.fromCol, edge.fromRow), out var from)) continue;
            if (!graph.ByCoord.TryGetValue((edge.toCol, edge.toRow), out var to)) continue;
            if (from.Children.All(n => !ReferenceEquals(n, to))) from.Children.Add(to);
            if (to.Parents.All(n => !ReferenceEquals(n, from))) to.Parents.Add(from);
        }

        // 输出顺序固定下来（按列再按行），agent 读起来稳定。
        foreach (var node in graph.All)
        {
            node.Children.Sort(CompareNode);
            node.Parents.Sort(CompareNode);
        }
    }

    private static int CompareNode(MapNodeInfo a, MapNodeInfo b)
    {
        var byCol = a.Col.CompareTo(b.Col);
        return byCol != 0 ? byCol : a.Row.CompareTo(b.Row);
    }

    // ---------------------------------------------------------------- 术语

    /// <summary>节点类型 → 中文。key 用 MapPointType 自己的名字（另有 Start / SecondBoss 两个特殊点名）。</summary>
    internal static readonly Dictionary<string, string> LegendMap = new()
    {
        ["Monster"] = "普通战",
        ["Elite"] = "精英",
        ["RestSite"] = "营火",
        ["Shop"] = "商店",
        ["Treasure"] = "宝箱",
        ["Event"] = "事件",
        ["Unknown"] = "未知",
        ["Boss"] = "Boss",
        ["Start"] = "起点",
        ["SecondBoss"] = "第二 Boss",
        ["Ancient"] = "古代",
        ["Unassigned"] = "未分配",
    };

    internal static Dictionary<string, object> Legend() =>
        LegendMap.ToDictionary(kv => kv.Key, kv => (object)kv.Value);

    /// <summary>
    /// 这一步的方向：<c>start</c>（还没进过节点 → 走起点）、<c>up</c>（正上方 / Boss）、
    /// <c>left</c> / <c>right</c>（斜上一层）。Boss / 第二 Boss 一律 up —— 它们是整行共用的出口，
    /// 不按列差算左右。
    /// </summary>
    internal static string EdgeName(MapCoord? from, MapCoord to, bool bossLike = false)
    {
        if (from == null) return "start";
        if (bossLike) return "up";
        var diff = to.col - from.Value.col;
        if (diff < 0) return "left";
        if (diff > 0) return "right";
        return "up";
    }

    internal static bool IsBossLike(MapGraph graph, MapNodeInfo node) =>
        node != null && (ReferenceEquals(node, graph.Boss) || ReferenceEquals(node, graph.SecondBoss) || node.Type == "Boss");

    // ------------------------------------------------------------ /map/next

    /// <summary>
    /// <c>GET /map/next</c>：现在真正能进的节点（和 <c>available</c> 同源，都是
    /// <see cref="GameApi.TravelablePoints"/>，也就是游戏自己的 MapTravel），外加「看着像候选但进不去」
    /// 的节点和原因。<b>要分清只读：这里不移动、不写 visited。</b>
    /// </summary>
    public static Dictionary<string, object> Next()
    {
        RunState state;
        try { state = RunManager.Instance?.DebugOnlyGetState(); }
        catch (Exception e) { return Fail("读不到 RunState：" + e.Message); }
        if (state == null) return Fail("no active run");
        if (state.Map == null) return Fail("这一局的地图还没生成（Map 为空）");

        var roomType = state.CurrentRoom?.RoomType.ToString();

        // ① 战斗没打完：move 会被 BeginMove 拒（会跳过整场战斗和奖励），先把话说明白。
        if (CombatInProgress())
            return InRoom(roomType ?? "Combat",
                "战斗还没结束：先把这场打完（出牌 / 结束回合），不要 move —— 会跳过整场战斗和它的奖励");

        // ② 人还卡在房间里：和 BeginMove 拦 move 的判据是同一个方法，不会出现
        //    「next 说能走、move 被拒」。地图界面已经开着时（事件/奖励处理完）不算卡住。
        if (GameApi.RoomBlocksTravel(state, out var blockedRoom))
            return InRoom(blockedRoom ?? roomType ?? "Room",
                "先处理完房间（/request 看有什么要决定的、/respond 答完）再用 /proceed 离开");

        var graph = Build(state);
        var current = state.CurrentMapPoint;
        var travelable = GameApi.TravelablePoints(state, current);
        var canMoveCoords = new HashSet<MapCoord>(travelable.Select(p => p.coord));
        var fromCoord = current?.coord == null ? (MapCoord?)null : current.coord;

        var canMove = new List<object>();
        foreach (var point in travelable)
        {
            var node = graph?.At(point.coord);
            var bossLike = node != null && IsBossLike(graph, node);
            string type;
            try { type = point.PointType.ToString(); } catch { type = "Unknown"; }
            canMove.Add(new Dictionary<string, object>
            {
                ["col"] = point.coord.col,
                ["row"] = point.coord.row,
                ["type"] = node?.Type ?? type,
                ["edge"] = EdgeName(fromCoord, point.coord, bossLike),
                ["reason"] = "",
            });
        }

        return Ok(new Dictionary<string, object>
        {
            ["current"] = fromCoord == null ? null : new { col = fromCoord.Value.col, row = fromCoord.Value.row },
            ["room"] = roomType,
            ["canMove"] = canMove,
            ["cannot"] = CannotList(graph, current, canMoveCoords),
        });
    }

    /// <summary>
    /// 「看着像候选但进不去」的节点 + 原因。取这几类：
    ///   1. 当前节点自己（agent 常重复 move 到自己脚下）；
    ///   2. 起点（还没进节点时它就是唯一出路，进过之后就回不去了）；
    ///   3. 下一层、列差 ≤1 的点：绝大概率是被猜过的坐标 → 已走过 / 没有连边；
    ///   4. 同一层左右相邻的点：本幕地图不支持横走。
    /// 一条候选都没有就是空数组（不从某个列表硬凑）。
    /// </summary>
    private static List<object> CannotList(MapGraph graph, MapPoint current, HashSet<MapCoord> canMove)
    {
        var result = new List<object>();
        if (graph == null) return result;

        var seen = new HashSet<(int col, int row)>();
        var currentCoord = current?.coord;
        var anchor = currentCoord ?? graph.Start?.Point?.coord ?? graph.Boss?.Point?.coord;

        void Add(MapNodeInfo node, string reason)
        {
            if (node == null) return;
            if (result.Count >= 12) return;
            if (canMove.Contains(new MapCoord(node.Col, node.Row))) return;
            if (!seen.Add((node.Col, node.Row))) return;
            result.Add(new Dictionary<string, object>
            {
                ["col"] = node.Col,
                ["row"] = node.Row,
                ["type"] = node.Type,
                ["reason"] = reason,
            });
        }

        if (current != null)
        {
            var here = graph.At(current.coord);
            if (here != null) Add(here, "你现在就在这个节点上，不用再走");
        }
        // 起点是 agent 最常猜的坐标：没进节点时它是唯一出路，进过之后就再也进不去了。
        if (graph.Start != null)
            Add(graph.Start, current == null
                ? "现在唯一能走的就是起点"
                : graph.Start.Visited ? "已走过" : "起点只能作为第一步");

        if (anchor is not { } a) return result;

        foreach (var node in graph.All)
        {
            if (node.Row != a.row + 1) continue;
            if (Math.Abs(node.Col - a.col) > 1) continue;
            // 还没走进这一幕（visited 为空）：下一层不是「不相邻」，而是必须先经过起点节点。
            Add(node, current == null
                ? "还没进入这一幕的地图：先走起点节点"
                : node.Visited ? "已走过" : "不相邻（和当前节点没有连边）");
        }

        foreach (var node in graph.All)
        {
            if (node.Row != a.row) continue;
            if (Math.Abs(node.Col - a.col) != 1) continue;
            Add(node, node.Visited ? "已走过" : "同一层不能横走（只能往上一层）");
        }

        return result;
    }

    private static Dictionary<string, object> InRoom(string room, string note) =>
        Ok(new Dictionary<string, object>
        {
            ["inRoom"] = true,
            ["room"] = room,
            ["canMove"] = new List<object>(),
            ["note"] = note,
        });

    private static bool CombatInProgress()
    {
        try { return MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.IsInProgress ?? false; }
        catch { return false; }
    }

    // ---------------------------------------------------------------- 杂项

    private static Dictionary<string, object> Ok(Dictionary<string, object> payload)
    {
        var result = new Dictionary<string, object> { ["ok"] = true };
        foreach (var kv in payload) result[kv.Key] = kv.Value;
        return result;
    }

    private static Dictionary<string, object> Fail(string message) =>
        new() { ["ok"] = false, ["error"] = message };
}
