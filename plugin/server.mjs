#!/usr/bin/env node
/**
 * MCP stdio server bridging the DSH agent to the in-game StS2 Agent Bridge mod.
 * The mod exposes plain HTTP on 127.0.0.1:47821; this process translates MCP
 * tool calls into those HTTP calls.
 *
 * Two jobs beyond plain forwarding:
 *  - compute what the agent would otherwise have to do in its head (damage per
 *    card vs each enemy, block pool, route scoring, plan reconciliation);
 *  - keep the model-facing output small: reasoning aids first, raw JSON behind
 *    an explicit flag.
 */
import process from 'node:process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SELF = fileURLToPath(import.meta.url);
const IS_MAIN = Boolean(process.argv[1]) && path.resolve(process.argv[1]) === SELF;

const BRIDGE = process.env.STS2_BRIDGE ?? 'http://127.0.0.1:47821';

const STORE_DIR = process.env.STS2_AGENT_STORE
  ? path.resolve(process.env.STS2_AGENT_STORE)
  : path.join(os.homedir(), '.dsh', 'storages', 'sts2-agent');
const PLAN_PATH = path.join(STORE_DIR, 'route-plan.json');

const COMBAT_ROOMS = new Set(['Monster', 'Elite', 'Boss', 'CombatRoom']);

// ---------------------------------------------------------------- http

async function call(pathname, init) {
  const response = await fetch(BRIDGE + pathname, init);
  const text = await response.text();
  let json = null;
  try {
    json = JSON.parse(text);
  } catch {
    json = null;
  }
  return { status: response.status, text, json };
}

/** Legacy shape: "HTTP <status>\n<body>" — several tools still return this. */
async function http(pathname, init) {
  const r = await call(pathname, init);
  return `HTTP ${r.status}\n${r.text}`;
}

async function getJson(pathname) {
  try {
    const r = await call(pathname);
    if (r.json && typeof r.json === 'object') return r.json;
    return { ok: false, error: `HTTP ${r.status}: ${r.text.slice(0, 300)}` };
  } catch (e) {
    return { ok: false, error: `bridge unreachable: ${e?.message ?? e}` };
  }
}

function postJson(pathname, body) {
  return getJson2(pathname, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body ?? {}),
  });
}

async function getJson2(pathname, init) {
  try {
    const r = await call(pathname, init);
    if (r.json && typeof r.json === 'object') return r.json;
    return { ok: false, error: `HTTP ${r.status}: ${r.text.slice(0, 300)}` };
  } catch (e) {
    return { ok: false, error: `bridge unreachable: ${e?.message ?? e}` };
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------------------------------------------------------------- small helpers

const int = (v, d = 0) => {
  const n = typeof v === 'number' ? v : parseFloat(v);
  return Number.isFinite(n) ? Math.trunc(n) : d;
};

const txt = (v, d = '') => (v === null || v === undefined ? d : String(v));

/** Build a query string from whichever of `names` the caller supplied (URI-encoded). */
function queryOf(args, names) {
  const parts = [];
  for (const name of names) {
    const value = args?.[name];
    if (value === undefined || value === null || value === '') continue;
    parts.push(`${name}=${encodeURIComponent(String(value))}`);
  }
  return parts.join('&');
}

/** Card `vars` is {Name: int}; normalise keys to lower case for lookups. */
function vars(card) {
  const out = {};
  const raw = card?.vars;
  if (raw && typeof raw === 'object') {
    for (const [k, v] of Object.entries(raw)) out[String(k).toLowerCase()] = int(v);
  }
  return out;
}

const varOf = (card, ...names) => {
  const v = vars(card);
  for (const n of names) {
    const hit = v[String(n).toLowerCase()];
    if (hit !== undefined && hit !== 0) return hit;
  }
  return 0;
};

/** Powers arrive as [{id, amount}] with ids owned by the game; match by substring. */
function powerAmount(powers, patterns) {
  let total = 0;
  for (const p of powers ?? []) {
    const id = String(p?.id ?? '').toLowerCase();
    if (!id) continue;
    if (patterns.some((pat) => id.includes(pat))) total += int(p?.amount, 0);
  }
  return total;
}

const POWER_VULN = ['vulnerab'];
const POWER_WEAK = ['weak'];
const POWER_FRAIL = ['frail'];
const POWER_STRENGTH = ['strength'];
const POWER_DEXTERITY = ['dexterity'];
const POWER_THORNS = ['thorns', 'thorn'];
const POWER_TIMID = ['timid'];

function powersLine(powers) {
  const bits = [];
  const push = (label, pats) => {
    const amt = powerAmount(powers, pats);
    if (amt) bits.push(`${label}${amt}`);
  };
  push('力', POWER_STRENGTH);
  push('敏', POWER_DEXTERITY);
  push('虚弱', POWER_WEAK);
  push('易伤', POWER_VULN);
  push('脆弱', POWER_FRAIL);
  push('荆棘', POWER_THORNS);
  push('胆小', POWER_TIMID);
  const known = new Set([...POWER_STRENGTH, ...POWER_DEXTERITY, ...POWER_WEAK, ...POWER_VULN, ...POWER_FRAIL, ...POWER_THORNS, ...POWER_TIMID]);
  for (const p of powers ?? []) {
    const raw = String(p?.id ?? '');
    const low = raw.toLowerCase();
    if (!raw || [...known].some((k) => low.includes(k))) continue;
    // 优先用游戏给的中文名（「神器」比 POWER.ARTIFACT_POWER 好判断），拿不到再退回短 id。
    const label = txt(p?.title, '') || raw.replace(/^.*[.\/]/, '');
    bits.push(`${label}:${int(p?.amount, 0)}`);
  }
  return bits.join(' ');
}

function isAttackIntent(it) {
  return /attack|death/i.test(String(it?.type ?? ''));
}

/** Total incoming for one intent: the mod already gives the modified total. */
function intentTotal(it) {
  const total = int(it?.damage, 0);
  if (total > 0) return total;
  const single = int(it?.single, 0);
  const repeats = Math.max(1, int(it?.repeats, 1));
  return single * repeats;
}

function incomingOf(state) {
  const enemies = state?.combat?.hittable ?? [];
  let sum = 0;
  for (const e of enemies) {
    if (int(e?.hp, 0) <= 0) continue;
    for (const it of e?.intents ?? []) if (isAttackIntent(it)) sum += intentTotal(it);
  }
  return sum;
}

function handOf(state) {
  const hand = state?.players?.[0]?.hand;
  return Array.isArray(hand) ? hand : [];
}

function player(state) {
  return state?.players?.[0] ?? {};
}

function potionIds(state) {
  const list = player(state)?.potions;
  return Array.isArray(list) ? list.filter(Boolean) : [];
}

// ---------------------------------------------------------------- card addressing

/**
 * Stable hand key: 「标题#序号」/「id#序号」（序号按同名牌在手牌里的顺序，从 1 起）,
 * plus bare 标题 / id when unique. Mirrors the potion key idea so a batch survives
 * a re-read (uids change whenever cards are drawn or shuffled).
 */
function handIndex(state) {
  const hand = handOf(state);
  const counters = new Map();
  const entries = [];
  for (let i = 0; i < hand.length; i += 1) {
    const card = hand[i] ?? {};
    const title = txt(card.title, '');
    const id = txt(card.id, '');
    const base = title || id || `card${i}`;
    const n = (counters.get(base) ?? 0) + 1;
    counters.set(base, n);
    entries.push({ index: i, card, keys: [`${base}#${n}`, id ? `${id}#${n}` : '', base, id].filter(Boolean) });
  }
  return entries;
}

function resolveCard(state, query) {
  const q = String(query ?? '').trim();
  if (!q) return { error: 'card key is empty' };
  const entries = handIndex(state);
  const low = q.toLowerCase();

  const exact = entries.filter((e) => e.keys.some((k) => k.toLowerCase() === low));
  if (exact.length === 1) return exact[0];
  if (exact.length > 1) return { error: `${q} 匹配到 ${exact.length} 张（用「标题#序号」区分）` };

  const partial = entries.filter((e) => e.keys.some((k) => k.toLowerCase().includes(low)));
  if (partial.length === 1) return partial[0];
  if (partial.length > 1) {
    return { error: `${q} 有歧义，候选：${partial.map((e) => e.keys[0]).join(' / ')}` };
  }
  const available = entries.map((e) => e.keys[0]);
  return { error: `手里没有「${q}」。当前手牌 key：${available.join(' / ') || '（空）'}` };
}

// ---------------------------------------------------------------- deck stats

const DEFENSE_IDS = new Set([
  'DEFEND_IRONCLAD', 'TRUE_GRIT', 'BLOOD_WALL', 'SHRUG_IT_OFF', 'TAUNT',
  'IMPERVIOUS', 'FLAME_BARRIER', 'EVIL_EYE', 'METALLICIZE', 'ARMAMENTS',
]);

const CURSE_IDS = new Set([
  'DOUBT', 'GUILT', 'REGRET', 'SHAME', 'PAIN', 'PARASITE', 'CLUMSY', 'DECAY',
  'INJURY', 'NORMALITY', 'WRITHE', 'ASCENDERS_BANE',
]);
const STATUS_IDS = new Set(['SLIMED', 'WOUND', 'BURN', 'DAZED', 'VOID']);

const shortId = (id) => String(id ?? '').replace(/^.*[.\/]/, '');

function badKind(card) {
  const id = String(card?.id ?? '').toUpperCase();
  const bare = shortId(id).toUpperCase();
  const type = String(card?.type ?? '').toLowerCase();
  if (bare.includes('ETERNAL') || bare.includes('ASCENDERS_BANE')) return '';
  if (type.includes('curse') || CURSE_IDS.has(bare)) return 'curse';
  if (type.includes('status') || STATUS_IDS.has(bare)) return 'status';
  return '';
}

function isAoe(card) {
  return /all/i.test(String(card?.target ?? ''));
}

function deckStats(cards) {
  const stats = { size: cards.length, attack: 0, skill: 0, power: 0, defense: 0, aoe: 0, upgraded: 0, curses: 0, status: 0, defenseNames: [], aoeNames: [], curseNames: [] };
  for (const card of cards) {
    const type = String(card?.type ?? '').toLowerCase();
    const bare = shortId(card?.id).toUpperCase();
    if (card?.upgraded) stats.upgraded += 1;
    if (type.includes('attack')) stats.attack += 1;
    else if (type.includes('skill')) stats.skill += 1;
    else if (type.includes('power')) stats.power += 1;

    const block = varOf(card, 'block');
    if (block > 0 || DEFENSE_IDS.has(bare)) {
      stats.defense += 1;
      stats.defenseNames.push(txt(card?.title, bare));
    }
    if (isAoe(card)) {
      stats.aoe += 1;
      stats.aoeNames.push(txt(card?.title, bare));
    }
    const kind = badKind(card);
    if (kind === 'curse') {
      stats.curses += 1;
      stats.curseNames.push(txt(card?.title, bare));
    } else if (kind === 'status') stats.status += 1;
  }
  return stats;
}

let deckCache = { at: 0, stats: null, cards: null };

async function readDeck(force) {
  const now = Date.now();
  if (!force && deckCache.stats && now - deckCache.at < 8000) return deckCache;
  const r = await getJson('/deck');
  if (!r?.ok || !Array.isArray(r.cards)) {
    const error = r?.error ?? r?.note ?? 'no /deck route';
    // "not in run" is not a deployment problem: /deck exists and answered, the run just hasn't
    // started. Only a missing route means the deployed DLL predates this tool.
    const errorKind = /not in run|不在对局/i.test(error)
      ? 'not-in-run'
      : /no route|404|HTTP 404/i.test(error) ? 'no-route' : 'unknown';
    deckCache = { at: now, stats: null, cards: null, error, errorKind };
    return deckCache;
  }
  deckCache = { at: now, stats: deckStats(r.cards), cards: r.cards, error: null, errorKind: null };
  return deckCache;
}

// ---------------------------------------------------------------- damage model

/**
 * Per-hit damage vs one enemy. `per` already includes the player's own strength/weak
 * (the mod reports the live value), so only vulnerability is applied here.
 */
function simulateHits(per, hits, block, timely) {
  let pool = Math.max(0, block);
  let total = 0;
  for (let i = 0; i < Math.max(1, hits); i += 1) {
    if (i === 0) pool += Math.max(0, timely);
    let d = per;
    if (pool > 0) {
      const absorbed = Math.min(pool, d);
      pool -= absorbed;
      d -= absorbed;
    }
    total += d;
  }
  return total;
}

function attackProfile(card) {
  const per = varOf(card, 'damage', 'calculateddamage');
  if (!per) return null;
  const repeat = Math.max(1, varOf(card, 'repeat', 'hits') || 1);
  return { per, hits: repeat };
}

function effectRows(state, hand) {
  const enemies = (state?.combat?.hittable ?? []).filter((e) => int(e?.hp, 0) > 0);
  if (!hand.length || !enemies.length) return [];
  const rows = [];
  for (let idx = 0; idx < hand.length && rows.length < 6; idx += 1) {
    const card = hand[idx];
    const profile = attackProfile(card);
    if (!profile) continue;
    const { per, hits } = profile;
    const parts = [];
    const lethal = [];
    let thornsWorst = 0;
    for (let ei = 0; ei < enemies.length; ei += 1) {
      const e = enemies[ei];
      const vuln = powerAmount(e?.powers, POWER_VULN) > 0;
      const timid = powerAmount(e?.powers, POWER_TIMID);
      const thorns = powerAmount(e?.powers, POWER_THORNS);
      const eff = vuln ? Math.trunc(per * 1.5) : per;
      const dmg = simulateHits(eff, hits, int(e?.block, 0), timid);
      let tag = `e${ei}:${dmg}`;
      if (vuln) tag += '(易伤)';
      if (timid) tag += `(胆小${timid})`;
      if (int(e?.hp, 0) > 0 && dmg >= int(e?.hp, 0)) {
        tag += '★斩';
        lethal.push(`e${ei}`);
      }
      if (thorns) thornsWorst = Math.max(thornsWorst, thorns);
      parts.push(tag);
    }
    const hitTxt = hits > 1 ? `${per}x${hits}` : String(per);
    const chips = [];
    if (thornsWorst) chips.push(`反伤-${thornsWorst * hits}`);
    rows.push({
      idx,
      card,
      line: `EFF [${idx}] ${shortId(card?.id)}${card?.upgraded ? '+' : ''} ${hitTxt}伤 → ${parts.join(' ')}`
        + (lethal.length ? ` | ★可斩杀 ${lethal.join('/')}` : '')
        + (chips.length ? ` | ${chips.join(' ')}` : ''),
    });
  }
  return rows;
}

function orderHint(hand) {
  const applierIdx = hand.findIndex((c) => varOf(c, 'vulnerablepower', 'vulnerable') > 0);
  if (applierIdx < 0) return '';
  const applier = hand[applierIdx];
  const gains = [];
  for (let i = 0; i < hand.length; i += 1) {
    if (i === applierIdx) continue;
    const profile = attackProfile(hand[i]);
    if (!profile) continue;
    const before = profile.per * profile.hits;
    const after = Math.trunc(profile.per * 1.5) * profile.hits;
    if (after > before) {
      gains.push(`${shortId(hand[i]?.id)}${hand[i]?.upgraded ? '+' : ''} ${before}→${after}`);
    }
  }
  if (!gains.length) return '';
  return `ORDER 先打 [${applierIdx}] ${shortId(applier?.id)} 挂易伤 → ${gains.slice(0, 4).join('，')} ← 先挂易伤再抡刀`;
}

function copiesHint(hand) {
  const groups = new Map();
  hand.forEach((card, i) => {
    const id = shortId(card?.id) || `card${i}`;
    if (!groups.has(id)) groups.set(id, []);
    groups.get(id).push({ i, up: Boolean(card?.upgraded) });
  });
  const parts = [];
  for (const [id, items] of groups) {
    if (items.length < 2) continue;
    const up = items.filter((x) => x.up).map((x) => `[${x.i}]`);
    const down = items.filter((x) => !x.up).map((x) => `[${x.i}]`);
    if (up.length && down.length) parts.push(`${id}: 升级版 ${up.join('/')} vs 未升级 ${down.join('/')} → 打升级版`);
  }
  return parts.length ? `COPIES ${parts.slice(0, 3).join('；')} ← 同名不同级，别把未升级那张打出去` : '';
}

function blockPool(hand) {
  let total = 0;
  const parts = [];
  for (const card of hand) {
    const block = varOf(card, 'block');
    if (block <= 0) continue;
    total += block;
    parts.push(`${txt(card?.title, shortId(card?.id))}${block}`);
  }
  return { total, parts };
}

// ---------------------------------------------------------------- view

let seenCards = new Map();
let lastHud = '';

function cardLine(idx, card, full, key) {
  const id = shortId(card?.id) || '?';
  const title = txt(card?.title, id);
  const seenKey = `${id}|${card?.upgraded ? 1 : 0}`;
  const cost = int(card?.cost, 0);
  const target = String(card?.target ?? '');
  const tgt = /AnyEnemy/i.test(target) ? '敌' : /All/i.test(target) ? '全体' : /Self/i.test(target) ? '己' : /Random/i.test(target) ? '随机' : '-';
  const unplayable = card?.playable === false ? ` UNPLAYABLE(${txt(card?.unplayableReasonCode ?? card?.unplayableReason, '条件不满足')})` : '';
  const keyTxt = key ? ` key=${key}` : '';
  if (!full && seenCards.has(seenKey)) {
    return `[${idx}] ${title}${card?.upgraded ? '+' : ''} c${cost} ${tgt}${unplayable}${keyTxt}`;
  }
  const v = vars(card);
  const vtxt = Object.entries(v).filter(([, n]) => n).map(([k, n]) => `${k}=${n}`).join(',');
  return `[${idx}] ${title}${card?.upgraded ? '+' : ''} c${cost} ${tgt}${vtxt ? ` | ${vtxt}` : ''}${unplayable}${keyTxt}`;
}

function markSeen(hand) {
  for (const card of hand) seenCards.set(`${shortId(card?.id)}|${card?.upgraded ? 1 : 0}`, 1);
}

function hudLine(state, stats) {
  const p = player(state);
  const potions = potionIds(state);
  const relics = Array.isArray(p?.relics) ? p.relics.length : 0;
  const deck = stats
    ? `${stats.size}(防${stats.defense}/AOE${stats.aoe}/诅咒${stats.curses}/升级${stats.upgraded})`
    : `${int(p?.deckSize, 0)}(统计不可用)`;
  return `HUD HP ${int(p?.hp)}/${int(p?.maxHp)} 金币 ${int(p?.gold)} 药水 ${potions.length} 遗物 ${relics} 牌组 ${deck}`;
}

async function viewReport(state, opts = {}) {
  const full = Boolean(opts.full);
  const room = txt(state?.room, '');
  const p = player(state);
  const act = txt(state?.act, '?');
  const lines = [];

  lines.push(`SCREEN ${room || '?'} | ${act} 第${int(state?.actFloor)}层/共${int(state?.totalFloor)}层 | combatState=${txt(state?.combatState, '?')}`);

  if (!state?.inRun) {
    lines.push('NOT_IN_RUN 不在对局里：开新局走 sts2_request 的 kind:"menu"（main → mode → character → ascension）');
    return lines.join('\n');
  }

  const deck = await readDeck(full);
  const stats = deck.stats;

  const hud = hudLine(state, stats);
  lines.push(hud);
  if (hud !== lastHud) {
    const potions = potionIds(state);
    if (potions.length) lines.push(`  POTIONS ${potions.join(' / ')}`);
    if (stats?.curseNames?.length) lines.push(`  DECK_BAD 诅咒 ${stats.curseNames.join(',')} ← 下一个移除机会先删诅咒`);
    if (!stats && deck.error) lines.push(`  DECK_STATS 不可用（${deck.error}）：需要部署带 /deck 的新 DLL，get：launch-game.ps1 -Deploy`);
  }
  lastHud = hud;

  if (!COMBAT_ROOMS.has(room)) {
    const pending = await getJson('/request');
    if (pending?.pending) {
      lines.push(`PENDING ${txt(pending.pending.kind)}：${txt(pending.pending.prompt)}（用 sts2_request / sts2_respond 处理）`);
    }
    markSeen(handOf(state));
    return lines.join('\n');
  }

  const hand = handOf(state);
  const enemies = (state?.combat?.hittable ?? []).filter((e) => int(e?.hp, 0) > 0);
  const incoming = incomingOf(state);
  const energy = int(p?.energy, 0);

  const self = [`hp=${int(p?.hp)}`];
  if (int(p?.block, 0)) self.push(`blk=${int(p?.block)}`);
  const pl = powersLine(p?.powers);
  if (pl) self.push(pl);
  if (int(p?.stars, 0)) self.push(`星${int(p?.stars)}`);

  const anchor = [`ANCHOR enemies=${enemies.length} incoming=${incoming} energy=${energy} hand=${hand.length}`];
  anchor.push(`self: ${self.join(' ')}`);
  const multi = [];
  for (const e of enemies) {
    for (const it of e?.intents ?? []) {
      if (!isAttackIntent(it)) continue;
      const single = int(it?.single, 0);
      const repeats = Math.max(1, int(it?.repeats, 1));
      const total = intentTotal(it);
      const label = `${shortId(e?.name) || txt(e?.name, '?')}`;
      if (repeats > 1 && single > 0) multi.push(`${label}[dmg=${single}x${repeats}=${total} 多段${repeats}次]`);
      else if (total > 0) multi.push(`${label}[dmg=${total}]`);
    }
  }
  if (multi.length) anchor.push(multi.slice(0, 4).join(' '));
  lines.push(anchor.join(' | '));

  lines.push(`ENEMIES ${enemies.length}`);
  enemies.forEach((e, i) => {
    const intents = (e?.intents ?? [])
      .filter((it) => it && it.type && String(it.type) !== 'Unknown')
      .map((it) => {
        const repeats = Math.max(1, int(it?.repeats, 1));
        const single = int(it?.single, 0);
        const total = intentTotal(it);
        if (repeats > 1 && single > 0) return `${txt(it.type)} ${single}x${repeats}=${total}`;
        return total > 0 ? `${txt(it.type)} ${total}` : txt(it.type);
      })
      .join(';');
    const pl2 = powersLine(e?.powers);
    lines.push(` [${i}] ${shortId(e?.name) || txt(e?.name, '?')} hp=${int(e?.hp)}/${int(e?.maxHp)} block=${int(e?.block)}`
      + (intents ? ` intent=${intents}` : '')
      + (pl2 ? ` powers=(${pl2})` : ''));
  });

  const summons = (state?.combat?.summons ?? []).filter((s) => int(s?.hp, 0) > 0);
  if (summons.length) {
    lines.push(`SUMMONS ${summons.map((s) => `${txt(s?.name, '?')} ${int(s?.hp)}/${int(s?.maxHp)}`).join(' / ')}`);
  }

  const rows = effectRows(state, hand);
  if (rows.length) lines.push(...rows.map((r) => r.line));

  const pool = blockPool(hand);
  if (incoming > 0) {
    lines.push(pool.parts.length
      ? `BLOCK_POOL 手牌可挡 ${pool.total}（${pool.parts.join(' + ')}）｜入伤 ${incoming} → ${pool.total >= incoming ? '够挡满（净掉 0）' : `不够，净掉 ${incoming - pool.total}`}`
      : `BLOCK_POOL 手牌无格挡牌｜入伤 ${incoming} → 净掉 ${incoming}`);
  } else {
    lines.push('NO_INCOMING 这回合没有攻击意图：先输出/运转，纯防御只有铺垫（Body Slam/Barricade/留甲）才打');
  }

  const order = orderHint(hand);
  if (order) lines.push(order);
  const copies = copiesHint(hand);
  if (copies) lines.push(copies);

  const keys = handIndex(state);
  lines.push(`HAND ${hand.length}`);
  for (const entry of keys) lines.push(` ${cardLine(entry.index, entry.card, full, entry.keys[0])}`);
  markSeen(hand);

  return lines.join('\n');
}

// ---------------------------------------------------------------- route planning

const ROUTE_SYM = {
  Monster: 'M', Elite: 'E', RestSite: 'R', Shop: '$', Event: '?',
  Treasure: 'T', Boss: 'B', Ancient: 'A', Unknown: '?', Start: 'S', SecondBoss: 'B2',
};

function snapshotOf(state, stats) {
  const p = player(state);
  return {
    floor: int(state?.totalFloor),
    act: txt(state?.act, ''),
    hp: int(p?.hp),
    maxHp: int(p?.maxHp),
    gold: int(p?.gold),
    potions: potionIds(state).length,
    deck: stats ? { size: stats.size, defense: stats.defense, aoe: stats.aoe, upgraded: stats.upgraded, curses: stats.curses } : null,
    ts: new Date().toISOString(),
  };
}

function fmtSnapshot(s) {
  const pct = s?.maxHp ? s.hp / s.maxHp : 0;
  const d = s?.deck;
  return `第${s?.floor}层 HP ${s?.hp}/${s?.maxHp}（${Math.round(pct * 100)}%） 金币${s?.gold} 药水${s?.potions}`
    + (d ? ` 牌组${d.size}（防${d.defense}/AOE${d.aoe}/升级${d.upgraded}/诅咒${d.curses}）` : ' 牌组统计不可用');
}

function replanReasons(cur, plan) {
  const reasons = [];
  const base = plan?.snapshot;
  if (!base) return reasons;
  if (base.act && cur.act && base.act !== cur.act) reasons.push(`换幕（${base.act} → ${cur.act}）：上一幕的计划作废，重新 route`);
  const curPct = cur.maxHp ? cur.hp / cur.maxHp : 0;
  const basePct = base.maxHp ? base.hp / base.maxHp : 0;
  if (basePct >= 0.7 && curPct < 0.7) reasons.push(`血线跌破 70%（${Math.round(basePct * 100)}%→${Math.round(curPct * 100)}%）：精英/普通怪序列要降级`);
  const drop = base.hp - cur.hp;
  if (drop >= Math.max(10, Math.trunc(0.2 * Math.max(1, cur.maxHp)))) reasons.push(`这一段掉了 ${drop} 血：后面的营火从「锻造」改成「睡觉」`);
  if (curPct < 0.5) reasons.push(`HP ${Math.round(curPct * 100)}% <50%：下一格必须是营火，绕开任何精英/多怪`);
  if (cur.deck && base.deck) {
    if (cur.deck.size - base.deck.size >= 3) reasons.push(`牌组 ${base.deck.size}→${cur.deck.size}：抽牌被稀释，防御牌占比要看一眼`);
    if (base.deck.defense < 6 && cur.deck.defense >= 6) reasons.push(`防御牌 ${base.deck.defense}→${cur.deck.defense}：达到 6 张，精英门槛可以放宽`);
    if (cur.deck.aoe > base.deck.aoe) reasons.push(`拿到 AOE（${base.deck.aoe}→${cur.deck.aoe}）：多怪精英可以考虑打了`);
    if (cur.deck.curses > base.deck.curses) reasons.push('牌组多了诅咒：下一个移除机会优先删它');
  }
  if (cur.potions === 0 && base.potions > 0) reasons.push('药水用光：精英四检里的「救命药」失效，别硬闯');
  if (cur.gold >= 100 && base.gold < 100) reasons.push(`金币到 ${cur.gold}：开始规划商店（删牌/买防御）`);
  return reasons;
}

async function routeReport(state, opts = {}) {
  const map = await getJson('/map');
  if (!map?.ok) return `NO_MAP ${map?.error ?? '地图读不到'}（先走到地图，或 /map 还没生成）`;

  const pts = Array.isArray(map.points) ? map.points : [];
  const rows = int(map.rows, 16);
  const key = (c, r) => `${int(c)},${int(r)}`;
  const byKey = new Map();
  for (const pt of pts) byKey.set(key(pt.col, pt.row), pt);

  const childrenOf = (pt) => {
    const out = [];
    for (const ch of pt?.children ?? []) {
      const node = byKey.get(key(ch?.col, ch?.row));
      if (node) out.push(node);
    }
    return out;
  };

  const isTerminal = (pt) => /boss|ancient/i.test(String(pt?.type ?? '')) || int(pt?.row, -1) >= rows;

  const walk = (start) => {
    const paths = [];
    const dfs = (node, trail) => {
      if (paths.length > 2000 || trail.length > 40) return;
      const next = [...trail, node];
      if (isTerminal(node)) {
        paths.push(next);
        return;
      }
      for (const child of childrenOf(node)) {
        if (next.includes(child)) continue;
        dfs(child, next);
      }
    };
    dfs(start, []);
    return paths;
  };

  const deck = await readDeck(false);
  const stats = deck.stats;
  const p = player(state);
  const hp = int(p?.hp);
  const maxHp = int(p?.maxHp);
  const pct = maxHp ? hp / maxHp : 1;
  const potions = potionIds(state).length;
  const gold = int(p?.gold);

  const gate = [
    `防御≥6? ${stats ? (stats.defense >= 6 ? '是' : `否（${stats.defense}）`) : '未知（无 /deck）'}`,
    `AOE≥1? ${stats ? (stats.aoe >= 1 ? '是' : '否') : '未知'}`,
    `药水≥1? ${potions >= 1 ? '是' : '否'}`,
  ];

  const score = (trail) => {
    const count = {};
    for (const n of trail) {
      const t = String(n?.type ?? '?');
      count[t] = (count[t] ?? 0) + 1;
    }
    let sc = 4.0 * (count.RestSite ?? 0) + 2.0 * (count.Shop ?? 0) + 2.0 * (count.Treasure ?? 0)
      + 1.0 * (count.Event ?? 0) + 0.6 * (count.Unknown ?? 0);
    if ((count.RestSite ?? 0) >= 3) sc += 1.5;

    let elitePen = pct < 0.55 ? 5.0 : pct < 0.7 ? 3.5 : pct < 0.85 ? 2.0 : 1.0;
    if (stats?.defense >= 6) elitePen *= 0.78;
    if (stats?.aoe >= 1) elitePen *= 0.85;
    if (potions >= 1) elitePen *= 0.85;
    if (stats && stats.defense < 5) elitePen *= 1.25;

    sc -= (elitePen + 2.0) * (count.Elite ?? 0);
    sc -= (pct < 0.45 ? 1.0 : 0.3) * (count.Monster ?? 0);
    if (stats && stats.size > 16) sc += 1.0 * (count.Shop ?? 0);

    const tail = trail.slice(-3);
    if (tail.some((n) => String(n?.type) === 'RestSite')) sc += 2.0;
    const preBossShop = trail.slice(-5).some((n) => String(n?.type) === 'Shop');
    if (preBossShop) sc += 2.5;
    if ((count.Treasure ?? 0) >= 2) sc += 1.0;
    return { sc, count, preBossShop };
  };

  const available = Array.isArray(map.available) ? map.available : [];
  const starts = available.map((a) => byKey.get(key(a.col, a.row))).filter(Boolean);
  if (!starts.length) {
    return `NO_AVAILABLE 现在没有可走节点（人在房间里就先 /request + /respond 处理完，再用 sts2_proceed 离开）\n`
      + `current=${JSON.stringify(map.current)} room=${txt(state?.room, '?')}`;
  }

  const ranked = [];
  for (let i = 0; i < starts.length; i += 1) {
    const paths = walk(starts[i]);
    if (!paths.length) continue;
    let best = null;
    for (const trail of paths) {
      const s = score(trail);
      if (!best || s.sc > best.sc) best = { ...s, trail };
    }
    if (best) ranked.push({ i, start: starts[i], ...best });
  }
  ranked.sort((a, b) => b.sc - a.sc);

  const lines = [];
  lines.push(`ROUTE PLAN | act ${txt(map.act, '?')} 第${int(map.actFloor)}层 | HP ${hp}/${maxHp}（${Math.round(pct * 100)}%） gold ${gold}`);
  lines.push(`SELF ${fmtSnapshot(snapshotOf(state, stats))}`);
  lines.push(`ELITE_GATE ${gate.join(' / ')} ← 三项全「是」也只当备选：非必要不精英`);

  for (const r of ranked.slice(0, 3)) {
    const seq = r.trail.map((n) => ROUTE_SYM[String(n?.type)] ?? '?').join(' ');
    const notes = [];
    for (const [t, label] of [['Elite', '精英'], ['RestSite', '营火'], ['Shop', '商店'], ['Treasure', '宝箱'], ['Monster', '战斗']]) {
      if (r.count[t]) notes.push(`${label}${r.count[t]}`);
    }
    if (r.trail.slice(-3).some((n) => String(n?.type) === 'RestSite')) notes.push('Boss前有营火');
    if (r.preBossShop) notes.push('Boss前有商店');
    if (pct < 0.7 && r.count.Elite) notes.push('HP<70%：精英高风险');
    lines.push(`opt${r.i} ${txt(r.start?.type, '?')} (${int(r.start?.col)},${int(r.start?.row)}) score=${r.sc.toFixed(1)} | ${notes.join(' / ') || '常规'}`);
    lines.push(`  ${seq}`);
  }

  if (pct < 0.7) lines.push('WARNING HP<70%：火堆权重提高，优先睡觉；非必要不打精英。');
  if (pct < 0.5) lines.push('WARNING HP<50%：下一格必须营火；没有营火就改道/用药，不接多怪或精英。');
  if (gold >= 100 && !ranked[0]?.preBossShop) lines.push('WARNING 金币≥100 且首选路线 Boss 前无商店：考虑改道/预留删牌。');

  const plan = readPlan();
  if (plan) {
    lines.push(`PLAN_BASE 上次规划：第${plan.snapshot?.floor}层 opt${plan.option} ${txt(plan.seq)} | ${fmtSnapshot(plan.snapshot ?? {})}`);
    const reasons = replanReasons(snapshotOf(state, stats), plan);
    if (reasons.length) {
      lines.push('REPLAN 建议重规划：');
      for (const r of reasons) lines.push(`  - ${r}`);
    } else {
      lines.push('PLAN_OK 状态与上次规划一致，按原计划走（每步仍看 sts2_map_next）');
    }
  }

  if (opts.commit !== undefined && opts.commit !== null) {
    const pick = ranked.find((r) => r.i === int(opts.commit));
    if (!pick) {
      lines.push(`COMMIT_FAIL opt${opts.commit} 不在候选里（候选：${ranked.map((r) => `opt${r.i}`).join(' ')}）`);
    } else {
      const seq = pick.trail.map((n) => ROUTE_SYM[String(n?.type)] ?? '?').join(' ');
      const data = {
        snapshot: snapshotOf(state, stats),
        option: pick.i,
        start: { col: int(pick.start?.col), row: int(pick.start?.row) },
        seq,
        note: txt(opts.note, ''),
      };
      writePlan(data);
      lines.push(`PLAN_COMMIT 已记录 opt${pick.i} (${data.start.col},${data.start.row}) ${seq}`);
    }
  }

  return lines.join('\n');
}

function readPlan() {
  try {
    return JSON.parse(fs.readFileSync(PLAN_PATH, 'utf8'));
  } catch {
    return null;
  }
}

function writePlan(data) {
  try {
    fs.mkdirSync(STORE_DIR, { recursive: true });
    fs.writeFileSync(PLAN_PATH, JSON.stringify(data, null, 2), 'utf8');
  } catch (e) {
    throw new Error(`写入计划失败：${e?.message ?? e}`);
  }
}

async function planReport(state, opts = {}) {
  if (opts.clear) {
    try {
      fs.rmSync(PLAN_PATH, { force: true });
    } catch {
      /* ignore */
    }
    return 'PLAN_CLEARED 计划已清空（换幕/改线/弃局后重新 route --commit）';
  }
  const plan = readPlan();
  if (!plan) return 'NO_PLAN 还没有记录计划：先 sts2_route 看候选，再 sts2_route {commit:<opt>} 记下来';
  const deck = await readDeck(false);
  const cur = snapshotOf(state, deck.stats);
  const lines = [
    `PLAN opt${plan.option} ${txt(plan.seq)}${plan.note ? `（${plan.note}）` : ''}`,
    `BASE ${fmtSnapshot(plan.snapshot ?? {})}`,
    `NOW  ${fmtSnapshot(cur)}`,
  ];
  const reasons = replanReasons(cur, plan);
  if (reasons.length) {
    lines.push('REPLAN 需要重规划：');
    for (const r of reasons) lines.push(`  - ${r}`);
    lines.push('做法：sts2_route 重新排序 → sts2_route {commit:<新 opt>} 覆盖 → 在鲸鱼气泡里说清改线原因');
  } else {
    lines.push('PLAN_OK 状态没变，按原计划走');
  }
  return lines.join('\n');
}

// ---------------------------------------------------------------- tools

const TOOLS = [
  {
    name: 'sts2_whale_say',
    description:
      'Make the in-game whale companion say a short line describing what the agent is about to do, in simplified language. Call this before starting a new step so the player can follow along without reading raw state.',
    inputSchema: {
      type: 'object',
      properties: {
        text: { type: 'string', description: 'One short sentence in the player language, e.g. "我先看看手牌，再决定打哪张。"' },
        ms: { type: 'integer', minimum: 800, maximum: 120000, description: 'how long the bubble stays visible, default 8000' },
      },
      required: ['text'],
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_whale_hide',
    description: 'Hide the in-game whale speech bubble.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_health',
    description:
      'Check whether the StS2 bridge is alive. Returns attached=true only when the game is running with the mod loaded.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_state',
    description:
      'Read the live game state: run (act, floor, gold, relics, deck size), the local player, hand cards, energy, enemies with HP/block, and the current room. Raw JSON; for combat decisions use sts2_view, which computes incoming damage and per-card damage.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_view',
    description:
      'Read the turn as one compact, computed block: run HUD, ANCHOR (incoming damage, energy, hand size, your powers), per-enemy intent and powers, EFF (each attack card damage vs each enemy after vulnerability/block/thorns/multi-hit), BLOCK_POOL (whether your hand can block incoming), ORDER and COPIES hints, then the hand with stable keys. Use it instead of sts2_state for every combat decision and every map/room check. Non-combat rooms return the HUD plus any pending decision.',
    inputSchema: {
      type: 'object',
      properties: {
        full: {
          type: 'boolean',
          description: 'Re-print full card lines (vars text) and refresh deck stats instead of the short form for cards already seen this session. Default false.',
        },
      },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_deck',
    description:
      'Read your whole deck: size, attack/skill/power split, defense cards, AOE cards, upgraded count, curses/status, and the card list (grouped by name). Use it before a shop removal, a card reward, or a route commitment.',
    inputSchema: {
      type: 'object',
      properties: {
        full: { type: 'boolean', description: 'Print one line per card with its current vars instead of grouped counts. Default false.' },
      },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_play_card',
    description:
      'Play one card from the local player hand. Prefer sts2_acts for a whole turn (verified batch); use this only when you need a single unbatched play.',
    inputSchema: {
      type: 'object',
      properties: {
        card: { type: 'integer', minimum: 0, description: '0-based hand index (see sts2_state)' },
        target: { type: 'integer', minimum: 0, description: '0-based target index (hittable enemies)' },
        player: { type: 'integer', minimum: 0, description: 'player index, default 0' },
      },
      required: ['card'],
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_acts',
    description:
      'Play a whole turn in ONE request: a list of actions executed in order, each verified (the card must leave the hand and the board must change). Returns the resulting turn summary. Stops early with `stopped` when an action did not take effect (e.g. energy ran out or a choice screen opened).',
    inputSchema: {
      type: 'object',
      properties: {
        actions: {
          type: 'array',
          description:
            'Ordered actions, e.g. [{"kind":"play_card","card":"打击#2","target":0},{"kind":"end_turn"}]. Supported kinds: play_card (card key or uid, target?), end_turn (confirm?), use_potion (potion key preferred, or uid, target?), discard_potion (potion key preferred, or uid), move (col,row). end_turn is REJECTED with reason="energy_left" when you still have energy and playable cards; pass confirm:true to skip the turn anyway.',
          items: {
            type: 'object',
            properties: {
              kind: { type: 'string', description: 'play_card | end_turn | use_potion | discard_potion | move' },
              card: { type: 'string', description: 'hand key from sts2_view: 标题#序号 (e.g. "打击#2") or card id. Resolved against a fresh read, so it survives redraws.' },
              uid: { type: 'integer', description: 'card uid, or legacy potion uid' },
              potion: { type: 'string', description: 'stable potion key, e.g. "力量药水#1" (use_potion / discard_potion)' },
              target: { type: 'integer', description: '0-based hittable enemy index' },
              confirm: { type: 'boolean', description: 'end_turn only: confirm skipping a turn while energy and playable cards remain' },
              col: { type: 'integer', description: 'map column for move' },
              row: { type: 'integer', description: 'map row for move' },
            },
            required: ['kind'],
            additionalProperties: false,
          },
        },
      },
      required: ['actions'],
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_potions',
    description:
      'List your potion slots: [{ index, key, uid, id, title, description }]. Address a potion by `key` (stable: 标题#同标题序号, e.g. "力量药水#1") — keys are re-read live on every call, so they survive a reload while uids do not. Call it before using or discarding anything.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_use_potion',
    description:
      'Drink a potion: { potion: "力量药水#1" } (preferred) or { uid } or { index }, plus { target } when the potion needs an enemy. The mod re-reads the belt on every call, then verifies the slot actually emptied (up to 2s) — ok:true means the potion is really gone; ok:false comes with the reason and the current availableKeys.',
    inputSchema: {
      type: 'object',
      properties: {
        potion: { type: 'string', description: 'stable key from sts2_potions, e.g. "力量药水#1" (preferred)' },
        uid: { type: 'integer', description: 'legacy, compatibility only — prefer potion' },
        index: { type: 'integer', minimum: 0, description: 'legacy slot index — prefer potion' },
        target: { type: 'integer', minimum: 0, description: '0-based enemy index for targeted potions' },
      },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_discard_potion',
    description:
      'Throw a potion away without using it: { potion: "力量药水#1" } (preferred) or { uid } or { index }. Use it when slots are full and the potion is worthless. Verifies the slot really emptied (up to 2s): ok:false means it is still there, with the reason and current availableKeys.',
    inputSchema: {
      type: 'object',
      properties: {
        potion: { type: 'string', description: 'stable key from sts2_potions, e.g. "力量药水#1" (preferred)' },
        uid: { type: 'integer', description: 'legacy, compatibility only — prefer potion' },
        index: { type: 'integer', minimum: 0, description: 'legacy slot index — prefer potion' },
      },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_relic',
    description:
      'Look up what a relic does: { id } (e.g. "RELIC.MAW_BANK") or { title } (中文名) → { title, description }. Use it before taking or buying a relic.',
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'model id, e.g. RELIC.MAW_BANK' },
        title: { type: 'string', description: '中文名，从 request 候选的 title 直接抄；和 id 二选一' },
      },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_potion',
    description:
      'Look up what a potion does: { id } (e.g. "POTION.FRUIT_JUICE") or { title } (中文名) → { title, description }. Use it before drinking or buying one.',
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'model id, e.g. POTION.FRUIT_JUICE' },
        title: { type: 'string', description: '中文名，从 request 候选的 title 直接抄；和 id 二选一' },
      },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_power',
    description:
      'Look up what a buff/debuff (power) does: { id } (e.g. "POWER.ARTIFACT_POWER") or { title } (中文名) → { title, description }. Use it for every power in sts2_view/ENEMIES you do not recognize; guessing a power costs HP.',
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'model id, e.g. POWER.SLIPPERY_POWER' },
        title: { type: 'string', description: '中文名，和 id 二选一' },
      },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_relics',
    description:
      'List every relic you own: { count, relics:[{index,id,title,description}] } — same shape as sts2_potions. Use sts2_relic {id} only for relics not yet acquired (shop/treasure).',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_card_info',
    description:
      'Look up a card: { id } (e.g. "CARD.BASH"), { uid } (实体 uid, 8 位整数), or { title } (中文名) → { title, cost, type, text, rarity }. Request candidates carry modelId/id/detail, so prefer that id; use title when only the Chinese name is known. A title shared by several cards returns { ambiguous:true, matches:[…] }.',
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'model id, e.g. CARD.BASH' },
        uid: { type: 'integer', description: '实体 uid（/state 的 hand[].uid 或 /deck 的 cards[].uid）；不是屏幕控件 uid' },
        title: { type: 'string', description: '中文名，例如「主宰」；同名多张时返回候选列表' },
      },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_map',
    description:
      'Read the WHOLE act map as a graph: { act, actFloor, totalFloor, current:{col,row}, rows, cols, boss:{col,row,type}, available:[{col,row,type,edge}], points:[{col,row,type,special?,visited,reachable,children:[{col,row}],parents:[{col,row}]}], legend }。children = 往下能到哪，parents = 从哪来。走图前先用它看清全图，再自己定一条从当前节点到 boss 的路线；actFloor=0 + room="Map" 是每幕开头的正常状态。',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_route',
    description:
      'Score every path from the available nodes to the boss: node mix, HP gates, elite penalty adjusted by your deck (defense/AOE) and potions, rest/shop placement, ELITE_GATE. Returns the top 3 with a symbol sequence. Pass commit to record the chosen option as the current plan. Use it at the start of each act and after any REPLAN.',
    inputSchema: {
      type: 'object',
      properties: {
        commit: { type: 'integer', minimum: 0, description: 'Record opt<i> as the plan (after reading the ranked list). Omit for a read-only ranking.' },
        note: { type: 'string', description: 'Short reason stored with the plan, e.g. "走商店线，Boss 前留营火".' },
      },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_plan',
    description:
      'Check the recorded plan against the live run and report PLAN_OK or REPLAN with concrete reasons (HP threshold, HP drop, deck growth, first AOE, potions spent, gold ≥100, new curse, act change). Use it on every map node before moving.',
    inputSchema: {
      type: 'object',
      properties: {
        clear: { type: 'boolean', description: 'Forget the recorded plan (new act, changed route, abandoned run). Default false.' },
      },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_map_next',
    description:
      'Read which map nodes can be entered RIGHT NOW: { ok, current, room, canMove:[{col,row,type,edge,reason}], cannot:[{col,row,type,reason}] }。canMove 与 /map 的 available 同源（游戏自己的 MapTravel），cannot 是「看着像候选但进不去」的节点与原因（不相邻 / 已走过 / 同一层不能横走）。人在房间里时返回 { inRoom:true, canMove:[], note }。move 的 col/row 只从 canMove 里取，永远不要猜。',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_close_overlay',
    description:
      'Close the topmost open overlay (map / rewards screen / shop panel / deck view / card-select grid) through the game\'s own control, and return the remaining layerStack. Use it when an overlay blocks the control you need; sts2_screen now reports layerStack.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_start',
    description:
      'Start a run directly from the models, bypassing the menus. Disabled unless STS2_ALLOW_START=1: it can leave an inRun=true run whose map never generated, which also blocks the menu protocol and save-quit/reload until the game restarts. The normal way to start a run is sts2_request kind:"menu".',
    inputSchema: {
      type: 'object',
      properties: {
        character: { type: 'string', description: 'e.g. silent, ironclad, defect' },
        seed: { type: 'string', description: 'optional seed string' },
      },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_end_turn',
    description: 'End the current player turn. Rejected with reason energy_left when you still have energy and playable cards; call sts2_acts with {"kind":"end_turn","confirm":true} to skip the turn anyway.',
    inputSchema: {
      type: 'object',
      properties: { player: { type: 'integer', minimum: 0, description: 'player index, default 0' } },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_move',
    description:
      'Move to an ADJACENT map node by column/row. Only nodes listed in the map\'s `available` array can be entered; the game rejects anything further away, so never guess a coordinate further down the path.',
    inputSchema: {
      type: 'object',
      properties: {
        col: { type: 'integer', minimum: 0 },
        row: { type: 'integer', minimum: 0 },
        player: { type: 'integer', minimum: 0, description: 'player index, default 0' },
      },
      required: ['col', 'row'],
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_proceed',
    description:
      'Leave the current room (the game\'s own proceed button; if the button does not move the room, the top-bar map button is used next). Refused while rewards are still settling (boss rewards appear 2–3s late) — wait for sts2_request to show the reward list instead of skipping it. Returns the room it lands in / whether the map page opened.',
    inputSchema: {
      type: 'object',
      properties: {
        force: {
          type: 'boolean',
          description: 'Bypass the pending-rewards guard. Only when the rewards really cannot be taken (e.g. stuck); normal path is picks:[] on the reward request.',
        },
      },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_save_quit',
    description:
      'Save the run with the game\'s own save and return to the main menu (the pause menu "save and quit"). Use it before a deliberate reload. In a run only; returns ok/saved/room/inRun.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_reload',
    description:
      'Continue the saved run from the main menu (the "continue" button), i.e. load back right where you saved. Use it after sts2_save_quit to restart a fight; card order may be reshuffled. Returns ok/room/inRun.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_autoslay',
    description:
      'Start or stop the game built-in AutoSlay harness. Starting it may begin a run on its own; it quits the game on failure, so prefer manual control.',
    inputSchema: {
      type: 'object',
      properties: { on: { type: 'boolean', description: 'true starts AutoSlay, false stops it' } },
      required: ['on'],
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_screen',
    description:
      '【调试用，正常流程用 sts2_request / sts2_respond】List every visible clickable control on the current screen with a stable uid, text and rect. Only for diagnosing screens the request protocol does not cover; every decision (cards, rewards, shop, rest, events, treasure, potions, menus) has a semantic channel in sts2_request.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_request',
    description:
      '读取当前游戏等待玩家做的决定。pending 为 null 表示现在没有待答请求；非空时包含 id / kind / prompt / min / max / candidates（候选里每项有 key，回答时用 key）。候选带 modelId / id / detail（卡牌还有牌面文字），可直接喂 sts2_card_info / sts2_relic / sts2_potion。kind 有 card/reward/shop/rest/event/treasure/potion/menu/bundle/proceed：menu 是开局那条（stage: main / mode / character / submenu / ascension）。',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_respond',
    description:
      '回答 sts2_request 返回的请求。picks 里填候选的 key（如 "打击#1"、"silent#1"）。校验失败会返回 ok:false 和 error，按 error 修正后重试。',
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'integer' },
        picks: { type: 'array', items: { type: 'string' } },
      },
      required: ['id', 'picks'],
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_tap',
    description:
      '【调试用，正常流程用 sts2_request / sts2_respond】Click one control returned by sts2_screen, by its uid. Only a fallback for menu pages the request protocol does not cover yet.',
    inputSchema: {
      type: 'object',
      properties: { uid: { type: 'integer', minimum: 0, description: 'uid from sts2_screen' } },
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_log',
    description: 'Tail the bridge mod log (last 100 lines) plus its file path, for diagnosing a failed call.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
];

async function runTool(name, args) {
  switch (name) {
    case 'sts2_health':
      return await http('/health');
    case 'sts2_state': {
      // Cards are dealt one at a time by an animation: a read taken right after the turn starts
      // can show a 1-card hand and make the agent mis-count energy. Re-read once after a beat.
      const first = await getJson('/state');
      const room = txt(first?.room, '');
      const hand = handOf(first);
      if (COMBAT_ROOMS.has(room) && hand.length <= 1) {
        await sleep(400);
        const second = await getJson('/state');
        if (handOf(second).length > hand.length) return `HTTP 200\n${JSON.stringify(second)}`;
      }
      return `HTTP 200\n${JSON.stringify(first)}`;
    }
    case 'sts2_view': {
      const state = await getJson('/state');
      return await viewReport(state, args ?? {});
    }
    case 'sts2_deck': {
      const deck = await readDeck(true);
      if (!deck.stats) {
        if (deck.errorKind === 'not-in-run') {
          return `DECK 不在对局里（/deck: ${deck.error}）：先用菜单协议开一局（sts2_request 的 kind:"menu"），进局后再查牌组。`;
        }
        return `DECK 不可用：${deck.error ?? '未知原因'}\n（需要部署带 /deck 的新 DLL：powershell -NoProfile -ExecutionPolicy Bypass -File sts2agent\\scripts\\launch-game.ps1 -Deploy）`;
      }
      const s = deck.stats;
      const lines = [
        `DECK ${s.size} 张 | 攻${s.attack} 技${s.skill} 能${s.power} | 防御${s.defense} AOE${s.aoe} 升级${s.upgraded} 诅咒${s.curses} 状态${s.status}`,
      ];
      if (s.defenseNames.length) lines.push(`DEFENSE ${s.defenseNames.join(',')}`);
      if (s.aoeNames.length) lines.push(`AOE ${s.aoeNames.join(',')}`);
      if (s.curseNames.length) lines.push(`CURSES ${s.curseNames.join(',')} ← 下一个移除机会优先删这些`);
      if (args?.full) {
        lines.push('CARDS');
        deck.cards.forEach((c, i) => lines.push(` ${cardLine(i, c, true, null)}`));
      } else {
        const counts = new Map();
        for (const c of deck.cards) {
          const label = `${txt(c?.title, shortId(c?.id))}${c?.upgraded ? '+' : ''}`;
          counts.set(label, (counts.get(label) ?? 0) + 1);
        }
        const groups = [...counts.entries()].sort((a, b) => b[1] - a[1]).map(([k, n]) => (n > 1 ? `${k}x${n}` : k));
        lines.push(`CARDS ${groups.join(' ')}`);
        lines.push('（要看每张牌的具体数值用 sts2_deck {full:true}）');
      }
      return lines.join('\n');
    }
    case 'sts2_log':
      return await http('/log');
    case 'sts2_map':
      return await http('/map');
    case 'sts2_map_next':
      return await http('/map/next');
    case 'sts2_route': {
      const state = await getJson('/state');
      return await routeReport(state, args ?? {});
    }
    case 'sts2_plan': {
      const state = await getJson('/state');
      return await planReport(state, args ?? {});
    }
    case 'sts2_acts': {
      let actions = Array.isArray(args?.actions) ? args.actions : [];
      const needsResolve = actions.some((a) => a && a.kind === 'play_card' && a.card !== undefined && a.uid === undefined);
      if (needsResolve) {
        const state = await getJson('/state');
        const resolved = [];
        for (let i = 0; i < actions.length; i += 1) {
          const a = { ...actions[i] };
          if (a.kind === 'play_card' && a.card !== undefined && a.uid === undefined) {
            const hit = resolveCard(state, a.card);
            if (hit.error) {
              return `STOPPED before action ${i}: ${hit.error}\n（没有提交任何动作；重读 sts2_view 拿当前手牌 key）`;
            }
            a.uid = int(hit.card?.uid, 0);
            a.card_title = txt(hit.card?.title, '');
            delete a.card;
          }
          resolved.push(a);
        }
        actions = resolved;
      }
      const r = await getJson2('/acts', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ actions }),
      });
      return JSON.stringify(r);
    }
    case 'sts2_play_card':
    case 'sts2_end_turn':
    case 'sts2_move':
    case 'sts2_autoslay': {
      const kind = name.slice('sts2_'.length);
      const body = JSON.stringify({ kind, ...args });
      return await http('/act', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body,
      });
    }
    case 'sts2_proceed':
      return await http('/proceed', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ force: args?.force === true }),
      });
    case 'sts2_save_quit':
      return await http('/save-quit', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' });
    case 'sts2_reload':
      return await http('/reload', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' });
    case 'sts2_potions':
      return await http('/potions');
    case 'sts2_use_potion':
      return await http('/potion/use', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ potion: args.potion, uid: args.uid, index: args.index, target: args.target }),
      });
    case 'sts2_discard_potion':
      return await http('/potion/discard', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ potion: args.potion, uid: args.uid, index: args.index }),
      });
    case 'sts2_relic':
      return await http(`/relic?${queryOf(args, ['id', 'title'])}`);
    case 'sts2_potion':
      return await http(`/potion?${queryOf(args, ['id', 'title'])}`);
    case 'sts2_power':
      return await http(`/power?${queryOf(args, ['id', 'title'])}`);
    case 'sts2_relics':
      return await http('/relics');
    case 'sts2_close_overlay':
      return await http('/close-overlay', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' });
    case 'sts2_card_info':
      return await http(`/card?${queryOf(args, ['id', 'uid', 'title'])}`);
    case 'sts2_start': {
      // 默认关闭：它会造出 inRun=true 但地图没生成的幽灵局，还会把菜单协议、save-quit、
      // reload 一起封死（只能重启游戏）。开新局的正常入口是 /request 的 kind:"menu"。
      if (process.env.STS2_ALLOW_START !== '1') {
        return 'REFUSED sts2_start 默认禁用：它会造出「inRun 为真但地图没生成」的幽灵局，并把菜单协议、save-quit、reload 一起封死。\n'
          + '开新局请走 sts2_request（kind:"menu"：main → mode → character → ascension）。\n'
          + '确实要绕过菜单时，在启动 DSH 前设 STS2_ALLOW_START=1。';
      }
      return await http('/start', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ character: args.character, seed: args.seed }),
      });
    }
    case 'sts2_whale_say':
      return await http('/whale/say', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ text: args.text, ms: args.ms ?? 8000 }),
      });
    case 'sts2_whale_hide':
      return await http('/whale/hide');
    case 'sts2_screen':
      return await http('/screen');
    case 'sts2_tap':
      return await http('/tap', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ uid: args.uid }),
      });
    case 'sts2_request':
      return await http('/request');
    case 'sts2_respond':
      return await http('/respond', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ id: args.id, picks: args.picks }),
      });
    default:
      throw new Error(`unknown tool: ${name}`);
  }
}

// ---------------------------------------------------------------- mcp plumbing

function send(message) {
  process.stdout.write(JSON.stringify(message) + '\n');
}

function result(id, payload) {
  send({ jsonrpc: '2.0', id, result: payload });
}

function error(id, code, message) {
  send({ jsonrpc: '2.0', id, error: { code, message } });
}

async function handle(raw) {
  let msg;
  try {
    msg = JSON.parse(raw);
  } catch {
    return;
  }

  const { id, method, params } = msg;
  const isNotification = id === undefined || id === null;

  try {
    switch (method) {
      case 'initialize':
        result(id, {
          protocolVersion: params?.protocolVersion ?? '2024-11-05',
          capabilities: { tools: { listChanged: false } },
          serverInfo: { name: 'sts2-agent', version: '0.2.0' },
        });
        break;

      case 'notifications/initialized':
      case 'notifications/cancelled':
        break;

      case 'ping':
        result(id, {});
        break;

      case 'tools/list':
        result(id, { tools: TOOLS });
        break;

      case 'tools/call': {
        const name = params?.name;
        const args = params?.arguments ?? {};
        try {
          const text = await runTool(name, args);
          result(id, { content: [{ type: 'text', text }], isError: false });
        } catch (e) {
          result(id, {
            content: [{ type: 'text', text: `tool ${name} failed: ${e?.message ?? e}` }],
            isError: true,
          });
        }
        break;
      }

      case 'resources/list':
        result(id, { resources: [] });
        break;

      case 'prompts/list':
        result(id, { prompts: [] });
        break;

      default:
        if (!isNotification) error(id, -32601, `method not found: ${method}`);
    }
  } catch (e) {
    if (!isNotification) error(id, -32603, String(e?.message ?? e));
  }
}

let buffer = '';
if (IS_MAIN) {
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', (chunk) => {
    buffer += chunk;
    let index;
    while ((index = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, index).trim();
      buffer = buffer.slice(index + 1);
      if (line) void handle(line);
    }
  });
  process.stdin.on('end', () => process.exit(0));
}

export { TOOLS, runTool, handle, resolveCard, handIndex, viewReport, routeReport, planReport, deckStats };
