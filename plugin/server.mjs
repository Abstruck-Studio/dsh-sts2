#!/usr/bin/env node
/**
 * MCP stdio server bridging the DSH agent to the in-game StS2 Agent Bridge mod.
 * The mod exposes plain HTTP on 127.0.0.1:47821; this process translates MCP
 * tool calls into those HTTP calls.
 */
import process from 'node:process';

const BRIDGE = process.env.STS2_BRIDGE ?? 'http://127.0.0.1:47821';

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
      'Read the live game state: run (act, floor, gold, relics, deck size), the local player, hand cards, energy, enemies with HP/block, and the current room.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_play_card',
    description:
      'Play a card from the local player hand. card is the 0-based index in hand (see sts2_state); target is the 0-based index into the hittable enemies list, required for targeted cards only.',
    inputSchema: {
      type: 'object',
      properties: {
        card: { type: 'integer', minimum: 0, description: '0-based hand index' },
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
      'Play a whole turn in ONE request: a list of actions executed in order, each verified (the card must leave the hand and the board must change). Use uids from the pushed turn snapshot. Returns the resulting turn summary. Stops early with `stopped` when an action did not take effect (e.g. energy ran out or a choice screen opened).',
    inputSchema: {
      type: 'object',
      properties: {
        actions: {
          type: 'array',
          description:
            'Ordered actions, e.g. [{"kind":"play_card","uid":123,"target":0},{"kind":"end_turn"}]. Supported kinds: play_card (uid, target?), end_turn (confirm?), use_potion (potion key preferred, or uid, target?), discard_potion (potion key preferred, or uid), move (col,row). end_turn is REJECTED with reason="energy_left" when you still have energy and playable cards; pass confirm:true to skip the turn anyway.',
          items: {
            type: 'object',
            properties: {
              kind: { type: 'string', description: 'play_card | end_turn | use_potion | discard_potion | move' },
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
      'List your potion slots: [{ index, key, uid, id, title, description }]. Address a potion by `key` (stable: 标题#同标题序号, e.g. "力量药水#1" / "力量药水#2") — keys are re-read live on every call, so they survive a reload while uids do not. `uid` is still returned for compatibility only. Call it before using or discarding anything.',
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
      'Look up what a relic does: { id } from sts2_state relics (e.g. "RELIC.MAW_BANK") → { title, description }. Use it before taking or buying a relic.',
    inputSchema: {
      type: 'object',
      properties: { id: { type: 'string' } },
      required: ['id'],
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_potion',
    description:
      'Look up what a potion does: { id } from sts2_state potions → { title, description }. Use it before drinking or buying one.',
    inputSchema: {
      type: 'object',
      properties: { id: { type: 'string' } },
      required: ['id'],
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_card_info',
    description:
      'Look up a card by id or uid: { id } or { uid } → { title, cost, type, description, vars }. Use it to check what a reward/shop card actually does before picking it.',
    inputSchema: {
      type: 'object',
      properties: { id: { type: 'string' }, uid: { type: 'integer' } },
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
    name: 'sts2_map_next',
    description:
      'Read which map nodes can be entered RIGHT NOW: { ok, current, room, canMove:[{col,row,type,edge,reason}], cannot:[{col,row,type,reason}] }。canMove 与 /map 的 available 同源（游戏自己的 MapTravel），cannot 是「看着像候选但进不去」的节点与原因（不相邻 / 已走过 / 同一层不能横走）。人在房间里时返回 { inRoom:true, canMove:[], note }。move 的 col/row 只从 canMove 里取，永远不要猜。',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
  {
    name: 'sts2_start',
    description:
      'Start a fresh single-player run straight from the models, bypassing the menus entirely (no NMainMenu / NCharacterSelectScreen involvement). Prefer sts2_request kind:"menu" when the menus are on screen: it walks main → mode → character with the game\'s own buttons. This one builds the run state directly via RunManager.SetUpNewSingleplayer + Launch; 角色 id 用末尾名字即可：ironclad / silent / regent / necrobinder / defect。 Requires that no run is in progress.',
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
      '读取当前游戏等待玩家做的决定。pending 为 null 表示现在没有待答请求；非空时包含 id / kind / prompt / min / max / candidates（候选里每项有 key，回答时用 key）。kind 有 card/reward/shop/rest/event/treasure/potion/menu：menu 是开局那条（stage: main / mode / character / submenu）。',
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
      required: ['uid'],
      additionalProperties: false,
    },
  },
  {
    name: 'sts2_log',
    description: 'Tail the bridge mod log (last 100 lines) plus its file path, for diagnosing a failed call.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
  },
];

async function http(path, init) {
  const response = await fetch(BRIDGE + path, init);
  const text = await response.text();
  return `HTTP ${response.status}\n${text}`;
}

async function runTool(name, args) {
  switch (name) {
    case 'sts2_health':
      return await http('/health');
    case 'sts2_state': {
      // Cards are dealt one at a time by an animation: a read taken right after the turn starts
      // can show a 1-card hand and make the agent mis-count energy. Re-read once after a beat.
      const first = await http('/state');
      const combat = ['Monster', 'Elite', 'Boss'].includes(first?.room);
      const hand = first?.players?.[0]?.hand;
      if (combat && Array.isArray(hand) && hand.length <= 1) {
        await new Promise((r) => setTimeout(r, 400));
        const second = await http('/state');
        const hand2 = second?.players?.[0]?.hand;
        if (Array.isArray(hand2) && hand2.length > hand.length) return second;
        return first;
      }
      return first;
    }
    case 'sts2_log':
      return await http('/log');
    case 'sts2_map':
      return await http('/map');
    case 'sts2_map_next':
      return await http('/map/next');
    case 'sts2_acts':
      return await http('/acts', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ actions: args.actions }),
      });
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
      return await http(`/relic?id=${encodeURIComponent(args.id)}`);
    case 'sts2_potion':
      return await http(`/potion?id=${encodeURIComponent(args.id)}`);
    case 'sts2_card_info':
      return await http(`/card?id=${encodeURIComponent(args.id ?? '')}${args.uid != null ? `&uid=${args.uid}` : ''}`);
    case 'sts2_start':
      return await http('/start', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ character: args.character, seed: args.seed }),
      });
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
          serverInfo: { name: 'sts2-agent', version: '0.1.0' },
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
