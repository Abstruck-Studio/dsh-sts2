#!/usr/bin/env node
/**
 * Offline self-test for plugin/server.mjs.
 *
 * Starts a fake bridge on a random port, imports the real server module and
 * asserts the computed output. No game and no child process needed — this is
 * the regression net for the tools that carry logic (view / acts / route / plan).
 *
 *   node sts2agent/scripts/test-plugin.mjs
 */
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));

let failures = 0;
function check(name, ok, extra) {
  if (ok) {
    console.log(`  ok   ${name}`);
  } else {
    failures += 1;
    console.log(`  FAIL ${name}${extra ? `\n       ${String(extra).slice(0, 700)}` : ''}`);
  }
}

// ---------------------------------------------------------------- fake bridge

const mock = {
  mode: 'normal',
  stateCalls: 0,
  actsBodies: [],
  hits: [],
  lookups: [],
  proceedBodies: [],
  planDir: fs.mkdtempSync(path.join(os.tmpdir(), 'sts2-test-')),
};

const HAND = [
  { id: 'CARD.BASH', uid: 101, title: '重击', upgraded: false, cost: 2, target: 'AnyEnemy', type: 'Attack', vars: { Damage: 8, VulnerablePower: 2 }, playable: true },
  { id: 'CARD.STRIKE_IRONCLAD', uid: 102, title: '打击', upgraded: false, cost: 1, target: 'AnyEnemy', type: 'Attack', vars: { Damage: 6 }, playable: true },
  { id: 'CARD.STRIKE_IRONCLAD', uid: 103, title: '打击', upgraded: true, cost: 1, target: 'AnyEnemy', type: 'Attack', vars: { Damage: 9 }, playable: true },
  { id: 'CARD.DEFEND_IRONCLAD', uid: 104, title: '防御', upgraded: false, cost: 1, target: 'Self', type: 'Skill', vars: { Block: 5 }, playable: true },
  { id: 'CARD.CLEAVE', uid: 105, title: '顺劈斩', upgraded: false, cost: 1, target: 'AllEnemies', type: 'Attack', vars: { Damage: 8 }, playable: false, unplayableReasonCode: 'EnergyCostTooHigh' },
];

const DECK = [
  ...Array.from({ length: 5 }, () => ({ id: 'CARD.STRIKE_IRONCLAD', title: '打击', upgraded: false, cost: 1, type: 'Attack', target: 'AnyEnemy', vars: { Damage: 6 } })),
  ...Array.from({ length: 3 }, () => ({ id: 'CARD.DEFEND_IRONCLAD', title: '防御', upgraded: false, cost: 1, type: 'Skill', target: 'Self', vars: { Block: 5 } })),
  { id: 'CARD.SHRUG_IT_OFF', title: '耸肩无视', upgraded: false, cost: 1, type: 'Skill', target: 'Self', vars: { Block: 8 } },
  { id: 'CARD.CLEAVE', title: '顺劈斩', upgraded: false, cost: 1, type: 'Attack', target: 'AllEnemies', vars: { Damage: 8 } },
  { id: 'CARD.DOUBT', title: '疑惑', upgraded: false, cost: -1, type: 'Curse', target: 'None', vars: {} },
];

function stateBody() {
  // The deal animation only shows the truncated hand on the very first read of the turn.
  const dealAnimation = mock.mode === 'deal' && mock.stateCalls === 1;
  return {
    inRun: true,
    room: 'Monster',
    act: 'ACT.OVERGROWTH',
    actFloor: 3,
    totalFloor: 5,
    ascension: 10,
    combatState: 'PlayPhase',
    players: [{
      index: 0, hp: 60, maxHp: 85, block: 0, gold: 99, energy: 3, turn: 3, deckSize: DECK.length,
      powers: [{ id: 'POWERS.STRENGTH_POWER', amount: 2 }],
      potions: ['POTION.STRENGTH_POTION'],
      relics: ['RELIC.BURNING_BLOOD', 'RELIC.ANCHOR'],
      hand: dealAnimation ? HAND.slice(0, 1) : HAND,
      handComplete: !dealAnimation,
    }],
    combat: {
      round: 3, side: 'Player',
      hittable: [
        { name: 'GARDENER', hp: 31, maxHp: 31, block: 0, intents: [{ type: 'Attack', damage: 21, single: 7, repeats: 3 }], powers: [{ id: 'POWERS.TIMID_POWER', amount: 7 }] },
        { name: 'GARDENER', hp: 27, maxHp: 27, block: 6, intents: [{ type: 'Attack', damage: 10, single: 10, repeats: 1 }], powers: [{ id: 'POWERS.VULNERABLE_POWER', amount: 2 }, { id: 'POWERS.STRENGTH_POWER', amount: 3 }] },
      ],
      summons: [],
    },
  };
}

const MAP = {
  ok: true, act: 'ACT.OVERGROWTH', actFloor: 3, totalFloor: 5,
  current: { col: 3, row: 1 }, rows: 4, cols: 7,
  boss: { col: 3, row: 4, type: 'Boss' },
  available: [
    { col: 3, row: 2, type: 'Monster', edge: 'up' },
    { col: 2, row: 2, type: 'Shop', edge: 'left' },
  ],
  points: [
    { col: 3, row: 0, type: 'Start', special: true, visited: true, children: [{ col: 3, row: 1 }] },
    { col: 3, row: 1, type: 'Monster', visited: true, children: [{ col: 3, row: 2 }, { col: 2, row: 2 }] },
    { col: 3, row: 2, type: 'Monster', visited: false, children: [{ col: 3, row: 3 }] },
    { col: 2, row: 2, type: 'Shop', visited: false, children: [{ col: 2, row: 3 }] },
    { col: 3, row: 3, type: 'RestSite', visited: false, children: [{ col: 3, row: 4 }] },
    { col: 2, row: 3, type: 'Treasure', visited: false, children: [{ col: 3, row: 4 }] },
    { col: 3, row: 4, type: 'Boss', special: true, visited: false, children: [] },
  ],
};

const bridge = http.createServer((req, res) => {
  const route = new URL(req.url, 'http://127.0.0.1').pathname;
  let body = '';
  req.on('data', (c) => { body += c; });
  req.on('end', () => {
    mock.hits.push(route);
    const json = (obj) => {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify(obj));
    };
    if (route === '/health') return json({ ok: true, attached: true, mod: 'Sts2AgentBridge', version: '0.1.0' });
    if (route === '/state') {
      mock.stateCalls += 1;
      return json(stateBody());
    }
    if (route === '/deck') {
      if (mock.deckMode === 'notInRun') return json({ ok: false, error: 'not in run' });
      return json({ ok: true, player: 0, deckSize: DECK.length, handSize: 5, cards: DECK });
    }
    if (route === '/map') return json(MAP);
    if (route === '/request') return json({ ok: true, pending: null });
    if (route === '/acts') {
      try { mock.actsBodies.push(JSON.parse(body)); } catch { mock.actsBodies.push({ raw: body }); }
      return json({ ok: true, applied: 1, summary: 'T3 你60/85 能量1 | 手牌: 打击', stopped: null });
    }
    if (['/proceed', '/save-quit', '/reload', '/close-overlay'].includes(route)) {
      if (route === '/proceed') {
        try { mock.proceedBodies.push(JSON.parse(body || '{}')); } catch { mock.proceedBodies.push({ raw: body }); }
      }
      return json({ ok: true, route, room: 'Map', inRun: true });
    }
    if (route === '/log') return json({ ok: true, path: 'x', lines: [] });
    if (['/card', '/relic', '/potion', '/power', '/relics'].includes(route)) {
      const query = Object.fromEntries(new URL(req.url, 'http://127.0.0.1').searchParams);
      mock.lookups.push({ route, query });
      return json({ ok: true, route, query });
    }
    return json({ ok: false, error: `no route ${route}` });
  });
});

// ---------------------------------------------------------------- run

await new Promise((resolve) => bridge.listen(0, '127.0.0.1', resolve));
const PORT = bridge.address().port;
process.env.STS2_BRIDGE = `http://127.0.0.1:${PORT}`;
process.env.STS2_AGENT_STORE = mock.planDir;

const agent = await import('../plugin/server.mjs');
const callTool = (name, args) => agent.runTool(name, args ?? {});

try {
  const names = new Set(agent.TOOLS.map((t) => t.name));
  for (const name of ['sts2_view', 'sts2_deck', 'sts2_route', 'sts2_plan', 'sts2_proceed', 'sts2_save_quit', 'sts2_reload', 'sts2_acts', 'sts2_state']) {
    check(`tool list has ${name}`, names.has(name));
  }

  // ---- view
  const view = await callTool('sts2_view');
  check('view: ANCHOR incoming=31', view.includes('incoming=31'), view);
  check('view: timid shield math', view.includes('e0:1(胆小7)'), view);
  check('view: vuln + block math', view.includes('e1:6(易伤)'), view);
  check('view: multi-hit intent expanded', view.includes('7x3=21'), view);
  check('view: BLOCK_POOL shortfall', view.includes('不够，净掉 26'), view);
  check('view: COPIES hint', view.includes('COPIES') && view.includes('打击'), view);
  check('view: ORDER hint', view.includes('ORDER 先打 [0]'), view);
  check('view: stable hand key', view.includes('key=重击#1') && view.includes('key=打击#2'), view);
  check('view: unplayable annotated', view.includes('UNPLAYABLE(EnergyCostTooHigh)'), view);
  check('view: HUD line', view.includes('HUD HP 60/85'), view);

  const view2 = await callTool('sts2_view');
  check('view: second read shorter (cards already seen)', view2.length < view.length, `${view2.length} vs ${view.length}`);

  // ---- deck
  const deck = await callTool('sts2_deck');
  check('deck: stats line', deck.includes('DECK 11 张') && deck.includes('防御4'), deck);
  check('deck: AOE listed', deck.includes('AOE1') && deck.includes('AOE 顺劈斩'), deck);
  check('deck: curses flagged', deck.includes('CURSES') && deck.includes('疑惑'), deck);
  check('deck: grouped counts', deck.includes('打击x5'), deck);

  // ---- acts by stable key
  mock.actsBodies.length = 0;
  const acts = await callTool('sts2_acts', { actions: [{ kind: 'play_card', card: '打击#2', target: 1 }, { kind: 'end_turn', confirm: true }] });
  check('acts: accepted', acts.includes('"ok":true'), acts);
  const sent = mock.actsBodies[0]?.actions ?? [];
  check('acts: 打击#2 resolves to the upgraded copy (uid 103)', sent[0]?.uid === 103, JSON.stringify(sent));
  check('acts: card key removed before posting', sent[0]?.card === undefined, JSON.stringify(sent[0]));

  mock.actsBodies.length = 0;
  const bad = await callTool('sts2_acts', { actions: [{ kind: 'play_card', card: '不存在' }] });
  check('acts: unknown key stops before posting', bad.startsWith('STOPPED') && mock.actsBodies.length === 0, bad);

  // ---- route + plan
  const route = await callTool('sts2_route');
  check('route: gate line', route.includes('ELITE_GATE'), route);
  check('route: ranked options', (route.match(/^opt\d/gm) ?? []).length >= 2, route);
  check('route: symbol sequence', /\n {2}[A-Z$? ]+/.test(route), route);

  const commit = await callTool('sts2_route', { commit: 1, note: '测试商店线' });
  check('route: commit recorded', commit.includes('PLAN_COMMIT'), commit);
  check('route: plan file written', fs.existsSync(path.join(mock.planDir, 'route-plan.json')));

  const plan = await callTool('sts2_plan');
  check('plan: PLAN_OK on unchanged run', plan.includes('PLAN_OK'), plan);

  const cleared = await callTool('sts2_plan', { clear: true });
  check('plan: clear', cleared.includes('PLAN_CLEARED'), cleared);

  // ---- state re-read during the deal animation
  mock.mode = 'deal';
  mock.stateCalls = 0;
  const deal = await callTool('sts2_state');
  const dealJson = JSON.parse(deal.split('\n').slice(1).join('\n'));
  check('state: re-reads once while the hand is still dealt', (dealJson.players?.[0]?.hand ?? []).length === 5, deal.slice(0, 400));
  mock.mode = 'normal';

  // ---- tools that were missing before
  for (const [name, route] of [['sts2_proceed', '/proceed'], ['sts2_save_quit', '/save-quit'], ['sts2_reload', '/reload']]) {
    const before = mock.hits.filter((h) => h === route).length;
    const text = await callTool(name);
    const after = mock.hits.filter((h) => h === route).length;
    check(`${name}: posts ${route}`, after === before + 1 && text.includes('"ok":true'), text);
  }

  // ---- sts2_start stays refused by default (ghost-run hazard)
  const startBefore = mock.hits.filter((h) => h === '/start').length;
  const started = await callTool('sts2_start', { character: 'ironclad' });
  const startAfter = mock.hits.filter((h) => h === '/start').length;
  check('sts2_start: refused without STS2_ALLOW_START=1', started.startsWith('REFUSED') && startAfter === startBefore, started);

  // ---- /deck answering "not in run" must not be reported as "deploy a new DLL"
  mock.deckMode = 'notInRun';
  const noRun = await callTool('sts2_deck');
  check('deck: not-in-run is not misreported as a deployment problem', noRun.includes('不在对局') && !noRun.includes('需要部署'), noRun);
  mock.deckMode = 'normal';

  // ---- card / relic / potion lookups: model id, 60-bit uid and 中文 title all survive the proxy
  mock.lookups.length = 0;
  const byTitle = await callTool('sts2_card_info', { title: '主宰' });
  check('card_info: title forwarded', mock.lookups[0]?.query.title === '主宰' && byTitle.includes('"ok":true'), byTitle);
  const byUid = await callTool('sts2_card_info', { uid: 98582925170 });
  check('card_info: 60-bit control uid not truncated', mock.lookups[1]?.query.uid === '98582925170', JSON.stringify(mock.lookups[1]));
  const byId = await callTool('sts2_card_info', { id: 'CARD.MASTERY' });
  check('card_info: model id forwarded', mock.lookups[2]?.query.id === 'CARD.MASTERY', JSON.stringify(mock.lookups[2]));
  const relicTitle = await callTool('sts2_relic', { title: '石像' });
  check('relic: title forwarded', mock.lookups[3]?.query.title === '石像' && relicTitle.includes('"ok":true'), relicTitle);
  const potionTitle = await callTool('sts2_potion', { title: '力量药水' });
  check('potion: title forwarded', mock.lookups[4]?.query.title === '力量药水' && potionTitle.includes('"ok":true'), potionTitle);
  const powerById = await callTool('sts2_power', { id: 'POWER.SLIPPERY_POWER' });
  check('power: id forwarded', mock.lookups[5]?.query.id === 'POWER.SLIPPERY_POWER' && powerById.includes('"ok":true'), powerById);
  const relics = await callTool('sts2_relics');
  check('relics: list endpoint hit', mock.lookups[6]?.route === '/relics' && relics.includes('"ok":true'), relics);
  const closeOverlay = await callTool('sts2_close_overlay');
  check('close_overlay: posts /close-overlay', closeOverlay.includes('"ok":true') && mock.hits.includes('/close-overlay'), closeOverlay);

  // ---- proceed passes force through (boss-reward guard bypass)
  mock.proceedBodies.length = 0;
  await callTool('sts2_proceed');
  await callTool('sts2_proceed', { force: true });
  check('proceed: default body has force=false', mock.proceedBodies[0]?.force === false, JSON.stringify(mock.proceedBodies[0]));
  check('proceed: force=true is forwarded', mock.proceedBodies[1]?.force === true, JSON.stringify(mock.proceedBodies[1]));
} catch (e) {
  failures += 1;
  console.log(`  FAIL harness error: ${e?.stack ?? e}`);
} finally {
  bridge.close();
  try { fs.rmSync(mock.planDir, { recursive: true, force: true }); } catch { /* ignore */ }
  console.log(failures === 0 ? '\nALL PASS' : `\n${failures} FAILURE(S)`);
  process.exit(failures === 0 ? 0 : 1);
}
