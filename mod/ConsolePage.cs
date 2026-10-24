using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Sts2AgentBridge;

/// <summary>
/// Live, view-only feed of what the agent sends to the bridge. Purely in memory: nothing is
/// written to disk, and a restart starts from zero.
/// </summary>
public static class ConsolePage
{
    private const int MaxEntries = 200;
    private static readonly object Gate = new();
    private static readonly List<Dictionary<string, object>> Items = new();
    private static long _seq;

    public static void Record(string method, string path, string body, int status, string response, long elapsedMs)
    {
        lock (Gate)
        {
            _seq++;
            Items.Add(new Dictionary<string, object>
            {
                ["seq"] = _seq,
                ["t"] = DateTime.Now.ToString("HH:mm:ss.fff"),
                ["method"] = method,
                ["path"] = path,
                ["body"] = body ?? "",
                ["status"] = status,
                ["ms"] = elapsedMs,
                ["res"] = response ?? "",
            });
            if (Items.Count > MaxEntries) Items.RemoveAt(0);
        }
    }

    public static string Since(long since)
    {
        lock (Gate)
        {
            var fresh = Items.Where(i => (long)i["seq"] > since).ToList();
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["now"] = _seq,
                ["events"] = fresh,
            });
        }
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value.Substring(0, max) + "…";
    }

    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>STS2 Agent Bridge · 消息控制台</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin: 0; font: 13px/1.45 ui-monospace, Consolas, monospace; background: #14161a; color: #d6dae0; }
  header { position: sticky; top: 0; display: flex; gap: 16px; align-items: center;
           padding: 10px 14px; background: #1b1e24; border-bottom: 1px solid #2b3038; z-index: 2; }
  h1 { font-size: 13px; margin: 0; font-weight: 600; color: #eaeef4; }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: #37c26a; box-shadow: 0 0 8px #37c26a; }
  .dot.off { background: #c24040; box-shadow: 0 0 8px #c24040; }
  .meta { color: #7d8794; }
  .spacer { flex: 1; }
  label { color: #7d8794; cursor: pointer; user-select: none; }
  #feed { padding: 8px 0 40px; }
  .row { padding: 7px 14px; border-bottom: 1px solid #1f232a; }
  .row:hover { background: #191d23; }
  .line { display: flex; gap: 10px; align-items: baseline; flex-wrap: wrap; }
  .t { color: #5f6a77; }
  .m { color: #7cc4ff; min-width: 42px; }
  .p { color: #f0c674; font-weight: 600; }
  .s-ok { color: #57c777; }
  .s-bad { color: #e06c6c; }
  .ms { color: #6f7a88; }
  .body, .res { margin: 4px 0 0 52px; color: #98a3b1; white-space: pre-wrap; word-break: break-all; }
  .res { color: #7d8794; }
  .empty { padding: 40px 14px; color: #5f6a77; }
</style>
</head>
<body>
<header>
  <span class="dot" id="dot"></span>
  <h1>STS2 Agent Bridge · 消息控制台</h1>
  <span class="meta" id="stat">连接中…</span>
  <span class="spacer"></span>
  <label><input type="checkbox" id="auto" checked> 自动滚动</label>
  <label><input type="checkbox" id="pause"> 暂停</label>
</header>
<div id="feed"><div class="empty">等待消息…（仅在内存中显示最近 200 条，不写入磁盘）</div></div>
<script>
let since = 0;
const feed = document.getElementById('feed');
const stat = document.getElementById('stat');
const dot = document.getElementById('dot');
const autoEl = document.getElementById('auto');
const pauseEl = document.getElementById('pause');
let count = 0, clearNext = true;

function esc(s) {
  return String(s).replace(/[&<>]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));
}

function pretty(s) {
  if (!s) return '';
  try { return JSON.stringify(JSON.parse(s)); } catch { return s; }
}

function addRow(e) {
  if (clearNext) { feed.innerHTML = ''; clearNext = false; }
  const row = document.createElement('div');
  row.className = 'row';
  const ok = e.status >= 200 && e.status < 300;
  row.innerHTML =
    '<div class="line">' +
      '<span class="t">' + esc(e.t) + '</span>' +
      '<span class="m">' + esc(e.method) + '</span>' +
      '<span class="p">' + esc(e.path) + '</span>' +
      '<span class="' + (ok ? 's-ok' : 's-bad') + '">' + e.status + '</span>' +
      '<span class="ms">' + e.ms + 'ms</span>' +
    '</div>' +
    (e.body ? '<div class="body">→ ' + esc(pretty(e.body)) + '</div>' : '') +
    (e.res ? '<div class="res">← ' + esc(pretty(e.res)) + '</div>' : '');
  feed.appendChild(row);
  count++;
}

async function tick() {
  if (pauseEl.checked) return;
  try {
    const r = await fetch('/console/log?since=' + since, { cache: 'no-store' });
    const data = await r.json();
    for (const e of data.events) { addRow(e); since = e.seq; }
    stat.textContent = '已接收 ' + count + ' 条';
    dot.classList.remove('off');
  } catch (err) {
    stat.textContent = '连接断开';
    dot.classList.add('off');
  }
  if (autoEl.checked) window.scrollTo(0, document.body.scrollHeight);
}

setInterval(tick, 400);
tick();
</script>
</body>
</html>
""";
}
