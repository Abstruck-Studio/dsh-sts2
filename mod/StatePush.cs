using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2AgentBridge;

/// <summary>
/// Pushes a compact combat snapshot the moment the player can act again — new turn, or after
/// the action queue settles. The live director forwards it into the session, so the agent
/// never has to poll /state and can never act on a stale board.
/// </summary>
public static class StatePush
{
    private const ulong ThrottleMs = 250;
    private const ulong SettleMs = 600;

    private static ulong _lastTickMs;
    private static string _lastSignature = "";
    private static string _pendingSignature = "";
    private static ulong _pendingSinceMs;
    private static bool _errorLogged;

    /// <summary>Called from BridgePump.Drain on the Godot main thread.</summary>
    public static void Tick()
    {
        var now = Time.GetTicksMsec();
        if (_lastTickMs != 0 && now - _lastTickMs < ThrottleMs) return;
        _lastTickMs = now;

        try
        {
            if (!PlayerCanAct()) return;

            // Cheap signature first: building the full state dict on every frame starves the
            // action executor and makes batched card plays silently fail.
            var signature = CheapSignature();

            // The draw animation briefly reports an empty hand with zero energy; that is not a
            // decision point, so never push it.
            if (HandIsEmpty()) return;

            if (signature != _pendingSignature)
            {
                _pendingSignature = signature;
                _pendingSinceMs = now;
                return;
            }

            if (signature == _lastSignature) return;
            if (now - _pendingSinceMs < SettleMs) return;

            _lastSignature = signature;

            var state = GameApi.State();
            if (state == null) return;

            var data = Compact(state);
            EventFeed.Publish(screen: "Combat", kind: "turn", summary: Summarize(state), items: new List<object>(), data: data);
        }
        catch (Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            BridgeLog.Warn("state push failed: " + e.Message);
        }
    }

    /// <summary>Resets the signature so the next combat pushes immediately.</summary>
    public static void Reset()
    {
        _lastSignature = "";
        _pendingSignature = "";
    }

    private static bool PlayerCanAct()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm == null || !rm.IsInProgress) return false;
            if (rm.ActionExecutor.IsRunning) return false;

            var state = rm.DebugOnlyGetState();
            if (state?.CurrentRoom is not CombatRoom room || room.CombatState == null) return false;
            if (state.Players is not { Count: > 0 }) return false;

            // Between the enemy's actions the queue is briefly idle too; a player turn always
            // has energy or cards in hand, so this keeps idle enemy frames from pushing.
            return LocalEnergyOrCards();
        }
        catch
        {
            return false;
        }
    }

    private static bool LocalEnergyOrCards()
    {
        try
        {
            return GameApi.LocalEnergy() > 0 || GameApi.LocalHandUids().Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Cheap fingerprint: no full state build, so it never starves the action queue.</summary>
    private static string CheapSignature()
    {
        var turn = GameApi.LocalTurn();
        var energy = GameApi.LocalEnergy();
        var hand = string.Join(",", GameApi.LocalHandUids());
        var enemies = GameApi.EnemyHpTotal();
        return $"{turn}|{energy}|{hand}|{enemies}";
    }

    private static Dictionary<string, object> Compact(Dictionary<string, object> state)
    {
        var player = FirstPlayer(state);
        var hand = new List<object>();
        if (player != null && player.TryGetValue("hand", out var handObj) && handObj is List<object> rawHand)
        {
            foreach (var card in rawHand.OfType<Dictionary<string, object>>())
            {
                hand.Add(new Dictionary<string, object>
                {
                    ["uid"] = card.TryGetValue("uid", out var uid) ? uid : 0,
                    ["title"] = card.TryGetValue("title", out var title) ? title : "",
                    ["cost"] = card.TryGetValue("cost", out var cost) ? cost : 0,
                    ["vars"] = card.TryGetValue("vars", out var vars) ? vars : new Dictionary<string, object>(),
                    ["target"] = card.TryGetValue("target", out var target) ? target : "",
                });
            }
        }

        var enemies = new List<object>();
        if (state.TryGetValue("combat", out var combatObj) && combatObj is Dictionary<string, object> combat
            && combat.TryGetValue("hittable", out var enemiesObj) && enemiesObj is List<object> rawEnemies)
        {
            foreach (var enemy in rawEnemies.OfType<Dictionary<string, object>>())
            {
                enemies.Add(new Dictionary<string, object>
                {
                    ["name"] = enemy.TryGetValue("name", out var name) ? name : "",
                    ["hp"] = enemy.TryGetValue("hp", out var hp) ? hp : 0,
                    ["block"] = enemy.TryGetValue("block", out var block) ? block : 0,
                    ["intents"] = enemy.TryGetValue("intents", out var intents) ? intents : new List<object>(),
                    ["powers"] = enemy.TryGetValue("powers", out var powers) ? powers : new List<object>(),
                });
            }
        }

        return new Dictionary<string, object>
        {
            ["hp"] = player != null && player.TryGetValue("hp", out var php) ? php : 0,
            ["maxHp"] = player != null && player.TryGetValue("maxHp", out var mhp) ? mhp : 0,
            ["block"] = player != null && player.TryGetValue("block", out var pblock) ? pblock : 0,
            ["energy"] = player != null && player.TryGetValue("energy", out var energy) ? energy : 0,
            ["turn"] = player != null && player.TryGetValue("turn", out var turn) ? turn : 0,
            ["hand"] = hand,
            ["enemies"] = enemies,
        };
    }

    private static string Summarize(Dictionary<string, object> state)
    {
        var player = FirstPlayer(state);
        var sb = new StringBuilder();

        if (player != null)
        {
            var turn = player.TryGetValue("turn", out var t) ? t : 0;
            var hp = player.TryGetValue("hp", out var h) ? h : 0;
            var maxHp = player.TryGetValue("maxHp", out var m) ? m : 0;
            var energy = player.TryGetValue("energy", out var e) ? e : 0;
            var block = player.TryGetValue("block", out var bl) ? bl : 0;
            sb.Append("T").Append(turn).Append(" 你").Append(hp).Append('/').Append(maxHp);
            if (block is int bv && bv > 0) sb.Append(" 格挡").Append(bv);
            sb.Append(" 能量").Append(energy).Append(" | ");
        }

        sb.Append("手牌: ");
        if (player != null && player.TryGetValue("hand", out var handObj) && handObj is List<object> hand)
        {
            foreach (var card in hand.OfType<Dictionary<string, object>>())
            {
                var title = card.TryGetValue("title", out var ti) ? ti : "?";
                var cost = card.TryGetValue("cost", out var co) ? co : "?";
                var v = VarsOf(card);
                sb.Append(title).Append("(c").Append(cost);
                if (v.Length > 0) sb.Append(',').Append(v);
                sb.Append(") ");
            }
        }

        sb.Append("| 敌: ");
        if (state.TryGetValue("combat", out var combatObj) && combatObj is Dictionary<string, object> combat
            && combat.TryGetValue("hittable", out var enemiesObj) && enemiesObj is List<object> enemies)
        {
            var index = 0;
            foreach (var enemy in enemies.OfType<Dictionary<string, object>>())
            {
                var name = enemy.TryGetValue("name", out var n) ? n : "?";
                var hp = enemy.TryGetValue("hp", out var h) ? h : 0;
                sb.Append('[').Append(index++).Append(']').Append(name).Append(' ').Append(hp);
                var intent = IntentsOf(enemy);
                if (intent.Length > 0) sb.Append('(').Append(intent).Append(')');
                sb.Append("  ");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string VarsOf(Dictionary<string, object> card)
    {
        if (!card.TryGetValue("vars", out var varsObj) || varsObj == null) return "";

        var parts = new List<string>();
        if (varsObj is System.Collections.IDictionary map)
        {
            foreach (System.Collections.DictionaryEntry entry in map)
            {
                var value = entry.Value?.ToString();
                if (string.IsNullOrEmpty(value) || value == "0") continue;
                parts.Add($"{entry.Key}={value}");
            }
        }
        else if (varsObj is IEnumerable<KeyValuePair<string, object>> pairs)
        {
            foreach (var pair in pairs)
            {
                var value = pair.Value?.ToString();
                if (string.IsNullOrEmpty(value) || value == "0") continue;
                parts.Add($"{pair.Key}={value}");
            }
        }
        return string.Join(",", parts);
    }

    private static string IntentsOf(Dictionary<string, object> enemy)
    {
        if (!enemy.TryGetValue("intents", out var intentsObj) || intentsObj is not List<object> intents) return "";
        var parts = new List<string>();
        foreach (var intent in intents.OfType<Dictionary<string, object>>())
        {
            var type = intent.TryGetValue("type", out var t) ? t?.ToString() : "";
            var damage = intent.TryGetValue("damage", out var d) ? d : null;
            var repeats = intent.TryGetValue("repeats", out var r) ? r : null;
            if (string.IsNullOrEmpty(type) || type == "Unknown") continue;

            var text = type;
            if (damage != null && Convert.ToInt32(damage) > 0)
            {
                text += damage;
                if (repeats != null && Convert.ToInt32(repeats) > 1) text += "x" + repeats;
            }
            parts.Add(text);
        }
        return string.Join(",", parts);
    }

    private static bool HandIsEmpty()
    {
        try
        {
            return GameApi.LocalEnergy() <= 0 && GameApi.LocalHandUids().Count == 0;
        }
        catch
        {
            return true;
        }
    }

    private static Dictionary<string, object> FirstPlayer(Dictionary<string, object> state)
    {
        if (!state.TryGetValue("players", out var playersObj) || playersObj is not List<object> players) return null;
        return players.OfType<Dictionary<string, object>>().FirstOrDefault();
    }
}
