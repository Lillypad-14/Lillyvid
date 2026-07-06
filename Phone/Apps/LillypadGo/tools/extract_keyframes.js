// Extracts move-animation KEYFRAMES (coordinates/timings) from Showdown's CC0 BattleMoveAnims
// (anims.js) + BattleOtherAnims (graphics.js) into structured data our C# runtime replays.
// We read the showEffect/backgroundEffect keyframe args as data; the animation code is not copied.
const fs = require('fs');
const path = require('path');
const A = path.join(__dirname, 'anim');
const anims = fs.readFileSync(path.join(A, 'anims.js'), 'utf8');
const graphics = fs.readFileSync(path.join(A, 'graphics.js'), 'utf8');
const { moves } = require('./gen1.json');

// --- Split a big `{ id:{...}, id2:{...} }` map into per-id block text. ---
function splitBlocks(src, startMarker) {
  const from = src.indexOf(startMarker);
  const body = src.slice(from);
  const blocks = {};
  const re = /\n([a-z0-9]+):\{/g;
  let m, prev = null, prevStart = 0;
  while ((m = re.exec(body)) !== null) {
    if (prev) blocks[prev] = body.slice(prevStart, m.index);
    prev = m[1];
    prevStart = m.index;
    if (body.slice(m.index).startsWith('\n};')) break;
  }
  if (prev) blocks[prev] = body.slice(prevStart, body.indexOf('\n};', prevStart));
  return blocks;
}

const moveBlocks = splitBlocks(anims, 'var BattleMoveAnims={');
const otherBlocks = splitBlocks(graphics, 'var BattleOtherAnims={');

// --- Read a balanced parenthetical starting at `open` (index of '(') ---
function readParens(s, open) {
  let depth = 0, i = open, q = null;
  for (; i < s.length; i++) {
    const c = s[i];
    if (q) { if (c === q && s[i - 1] !== '\\') q = null; continue; }
    if (c === '"' || c === "'") { q = c; continue; }
    if (c === '(') depth++;
    else if (c === ')') { depth--; if (depth === 0) return s.slice(open + 1, i); }
  }
  return s.slice(open + 1);
}

// --- Split top-level comma-separated args of a call body ---
function splitArgs(s) {
  const args = [];
  let depth = 0, q = null, start = 0;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (q) { if (c === q && s[i - 1] !== '\\') q = null; continue; }
    if (c === '"' || c === "'") { q = c; continue; }
    if (c === '(' || c === '{' || c === '[') depth++;
    else if (c === ')' || c === '}' || c === ']') depth--;
    else if (c === ',' && depth === 0) { args.push(s.slice(start, i)); start = i + 1; }
  }
  if (s.slice(start).trim()) args.push(s.slice(start));
  return args.map((a) => a.trim());
}

// --- Parse an object literal `{k:v,...}` -> { k: 'v-expr' } ---
function parseObj(str) {
  str = str.trim();
  if (!str.startsWith('{')) return null;
  const inner = str.slice(1, str.lastIndexOf('}'));
  const out = {};
  for (const part of splitArgs(inner)) {
    const c = part.indexOf(':');
    if (c < 0) continue;
    out[part.slice(0, c).trim()] = part.slice(c + 1).trim();
  }
  return out;
}

// --- Convert a coordinate expression to a structured ref { b, off } ---
// b: 'a' attacker, 'd' defender, 'm' midpoint, 'k' scene-constant.
function coord(expr, axis) {
  if (expr == null) return null;
  let e = expr.replace(/\s+/g, '');
  // attacker.behind(n)/leftof(n)/etc.
  let m = /^(attacker|defender)\.(behind|leftof|rightof|below|above)\((-?[\d.]+)\)([+-][\d.]+)?$/.exec(e);
  if (m) {
    const b = m[1] === 'attacker' ? 'a' : 'd';
    const n = parseFloat(m[3]);
    const extra = m[4] ? parseFloat(m[4]) : 0;
    const map = { behind: ['z', n], leftof: ['x', -n], rightof: ['x', n], below: ['y', -n], above: ['y', n] };
    const [ax, off] = map[m[2]];
    return { b, off: off + (ax === axis ? extra : extra) };
  }
  // attacker.x / defender.y +/- n
  m = /^(attacker|defender)\.(x|y|z)([+-][\d.]+)?$/.exec(e);
  if (m) return { b: m[1] === 'attacker' ? 'a' : 'd', off: m[3] ? parseFloat(m[3]) : 0 };
  // midpoint (attacker.x+defender.x)/2 [+/- n]
  if (/attacker\.\w/.test(e) && /defender\.\w/.test(e)) {
    const mm = /([+-][\d.]+)$/.exec(e);
    return { b: 'm', off: mm ? parseFloat(mm[1]) : 0 };
  }
  if (/attacker/.test(e)) return { b: 'a', off: 0 };
  if (/defender/.test(e)) return { b: 'd', off: 0 };
  // pure number -> scene constant
  const num = parseFloat(e);
  if (!Number.isNaN(num)) return { b: 'k', off: num };
  return null;
}

function num(expr, dflt) {
  if (expr == null) return dflt;
  const n = parseFloat(String(expr).replace(/[^0-9.eE+-].*$/, ''));
  return Number.isNaN(n) ? dflt : n;
}

// Build a normalized state (both start and end) with x/y/z coords + scale/opacity/time.
function state(obj, prev, fallbackBase) {
  obj = obj || {};
  const pick = (axis, key) => coord(obj[key], axis) || (prev ? prev[axis] : { b: fallbackBase, off: 0 });
  return {
    x: pick('x', 'x'), y: pick('y', 'y'), z: pick('z', 'z'),
    scale: obj.scale != null ? num(obj.scale, 1) : (prev ? prev.scale : 1),
    opacity: obj.opacity != null ? num(obj.opacity, 1) : (prev ? prev.opacity : 1),
    time: num(obj.time, prev ? prev.time : 0),
  };
}

const STR = /^'([^']*)'|^"([^"]*)"/;
function strlit(a) { const m = STR.exec(a.trim()); return m ? (m[1] ?? m[2]) : null; }

function extractCalls(block, fallbackBase) {
  const effects = [];
  const bg = [];
  if (!block) return { effects, bg };
  let idx = 0;
  while ((idx = block.indexOf('showEffect(', idx)) !== -1) {
    const args = splitArgs(readParens(block, block.indexOf('(', idx)));
    idx += 11;
    const sprite = strlit(args[0] || '');
    if (!sprite) continue;
    const startObj = parseObj(args[1] || '');
    const endObj = parseObj(args[2] || '');
    const transition = strlit(args[3] || '') || 'linear';
    const s = state(startObj, null, fallbackBase);
    const e = state(endObj, s, fallbackBase);
    effects.push({ sprite, s, e, t: transition });
  }
  idx = 0;
  while ((idx = block.indexOf('backgroundEffect(', idx)) !== -1) {
    const args = splitArgs(readParens(block, block.indexOf('(', idx)));
    idx += 16;
    bg.push({ color: strlit(args[0] || '') || '#000000', dur: num(args[1], 400), op: num(args[2], 0.4), time: num(args[3], 0) });
  }
  return { effects, bg };
}

// A move delegates if its block references BattleOtherAnims.<x>.anim
function sharedRef(block) {
  const m = /BattleOtherAnims\.([a-zA-Z]+)\.anim/.exec(block || '');
  return m ? m[1] : null;
}

const out = {};
let withData = 0, delegated = 0, sprites = new Set();
for (const mv of moves) {
  let block = moveBlocks[mv.id];
  let base = 'd';
  let { effects, bg } = extractCalls(block, base);
  if (effects.length === 0) {
    const ref = sharedRef(block);
    if (ref && otherBlocks[ref]) {
      ({ effects, bg } = extractCalls(otherBlocks[ref], base));
      if (effects.length) delegated++;
    }
  }
  if (effects.length === 0 && bg.length === 0) continue;
  const duration = Math.max(
    ...effects.map((f) => f.e.time), ...bg.map((b) => b.time + b.dur), 1);
  for (const f of effects) sprites.add(f.sprite);
  out[mv.name] = { dur: duration, fx: effects, bg };
  withData++;
}

fs.writeFileSync(path.join(__dirname, 'keyframes.json'),
  JSON.stringify({ moves: out, sprites: [...sprites].sort() }));
console.log('moves with keyframes:', withData, 'of', moves.length, '(delegated:', delegated + ')');
console.log('unique sprites:', sprites.size);
const sample = (n) => console.log(n, out[n] ? JSON.stringify(out[n]).slice(0, 240) : '(no keyframes -> pattern fallback)');
['Thunderbolt', 'Water Gun', 'Ember', 'Razor Leaf', 'Tackle', 'Surf', 'Vine Whip', 'Flamethrower', 'Bubble', 'Quick Attack'].forEach(sample);
const commonNoData = ['Tackle', 'Scratch', 'Ember', 'Water Gun', 'Vine Whip', 'Growl', 'Tail Whip', 'Bubble']
  .filter((n) => !out[n]);
console.log('common moves WITHOUT keyframes (use pattern fallback):', commonNoData.join(', ') || 'none');
