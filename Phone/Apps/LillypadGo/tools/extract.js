// Extracts Gen-1 (#1-151) species, level-up learnsets, and referenced moves from
// Showdown's data files, plus catch rate/habitat/legendary from PokeAPI CSV.
// Emits a single intermediate JSON the C# emitter consumes.
const fs = require('fs');
const path = require('path');
const ts = require('typescript');

const DIR = path.join(__dirname, 'data');

function evalTable(file, name) {
  const raw = fs.readFileSync(path.join(DIR, file), 'utf8');
  // Transpile away TS type syntax (moves.ts has annotations inside function bodies),
  // then eval the resulting JS. The file exports one big `const <Name> = {...}`.
  const js = ts.transpileModule(raw, {
    compilerOptions: { target: ts.ScriptTarget.ESNext, module: ts.ModuleKind.CommonJS },
  }).outputText;
  const sandbox = { exports: {}, require: () => ({}) };
  const fn = new Function('exports', 'require', js + '\nreturn exports;');
  const ex = fn(sandbox.exports, sandbox.require);
  return ex[name];
}

const Pokedex = evalTable('pokedex.ts', 'Pokedex');
const Moves = evalTable('moves.ts', 'Moves');
const Learnsets = evalTable('learnsets.ts', 'Learnsets');

// --- PokeAPI CSV: num -> {capture, habitat, legendary} ---
const csv = fs.readFileSync(path.join(DIR, 'species.csv'), 'utf8').trim().split(/\r?\n/);
const header = csv[0].split(',');
const col = (n) => header.indexOf(n);
const cId = col('id'), cCap = col('capture_rate'), cHab = col('habitat_id'),
  cLeg = col('is_legendary'), cMyth = col('is_mythical');
const meta = {};
for (let i = 1; i < csv.length; i++) {
  const f = csv[i].split(',');
  const id = +f[cId];
  if (id > 151) continue;
  meta[id] = {
    capture: +f[cCap] || 45,
    habitat: +f[cHab] || 0,
    legendary: f[cLeg] === '1' || f[cMyth] === '1',
  };
}

// --- Select the 151 base species ---
const species = [];
for (const key of Object.keys(Pokedex)) {
  const p = Pokedex[key];
  if (!p.num || p.num < 1 || p.num > 151) continue;
  if (p.forme || p.baseSpecies) continue; // skip alternate formes
  species.push({ id: key, ...p });
}
species.sort((a, b) => a.num - b.num);
if (species.length !== 151) {
  console.error('WARNING expected 151 base species, got', species.length);
}

// --- Level-up learnset: newest generation's L-codes ---
function levelupMoves(id) {
  const entry = Learnsets[id];
  const out = [];
  if (!entry || !entry.learnset) return out;
  for (const [moveid, codes] of Object.entries(entry.learnset)) {
    let bestGen = -1, bestLevel = null;
    for (const code of codes) {
      const m = /^(\d+)L(\d+)$/.exec(code);
      if (!m) continue;
      const gen = +m[1];
      const lvl = Math.max(1, +m[2]);
      if (gen > bestGen || (gen === bestGen && lvl < bestLevel)) {
        bestGen = gen; bestLevel = lvl;
      }
    }
    if (bestLevel !== null) out.push({ moveid, level: bestLevel });
  }
  out.sort((a, b) => a.level - b.level || a.moveid.localeCompare(b.moveid));
  return out;
}

// --- Move effect mapping -> our MoveEffect enum ---
const STATUS = { brn: 'Burn', frz: 'Freeze', par: 'Paralyze', psn: 'Poison', tox: 'Poison' };
const BOOST_LOWER = { atk: 'LowerTargetAtk', def: 'LowerTargetDef', spa: 'LowerTargetSpAtk', spd: 'LowerTargetSpDef', spe: 'LowerTargetSpd', accuracy: 'LowerTargetAccuracy' };
const BOOST_RAISE = { atk: 'RaiseAtk', def: 'RaiseDef', spe: 'RaiseSpd', evasion: 'RaiseEvasion' };

function firstBoost(boosts, table) {
  for (const [k, v] of Object.entries(boosts)) {
    if (table[k] !== undefined) return { effect: table[k], stage: Math.abs(v) };
  }
  return null;
}

function mapEffect(mv) {
  // returns {effect, chance, stage}
  const none = { effect: 'None', chance: 0, stage: 1 };
  // 1) primary status (status-only moves like Thunder Wave, Toxic, Will-O-Wisp)
  if (mv.status && STATUS[mv.status]) return { effect: STATUS[mv.status], chance: 100, stage: 1 };
  // 1b) primary volatile status (Supersonic/Confuse Ray = confusion, etc.)
  if (mv.volatileStatus === 'confusion') return { effect: 'Confuse', chance: 100, stage: 1 };
  // 2) secondaries
  const secs = mv.secondaries || (mv.secondary ? [mv.secondary] : []);
  for (const s of secs) {
    if (!s) continue;
    const chance = s.chance || 100;
    if (s.status && STATUS[s.status]) return { effect: STATUS[s.status], chance, stage: 1 };
    if (s.volatileStatus === 'flinch') return { effect: 'Flinch', chance, stage: 1 };
    if (s.volatileStatus === 'confusion') return { effect: 'Confuse', chance, stage: 1 };
    if (s.boosts) { const b = firstBoost(s.boosts, BOOST_LOWER); if (b) return { effect: b.effect, chance, stage: b.stage }; }
  }
  // 3) top-level boosts (self-buff or foe-debuff status moves)
  if (mv.boosts) {
    const self = mv.target === 'self';
    const table = self ? BOOST_RAISE : BOOST_LOWER;
    const b = firstBoost(mv.boosts, table);
    if (b) return { effect: b.effect, chance: 100, stage: b.stage };
  }
  // 4) healing
  if (mv.heal || mv.flags?.heal) return { effect: 'HealUser', chance: 0, stage: 1 };
  // 5) recoil
  if (mv.recoil) return { effect: 'RecoilQuarterMax', chance: 0, stage: 1 };
  return none;
}

const VALID_TYPES = new Set(['Normal', 'Fire', 'Water', 'Electric', 'Grass', 'Ice', 'Fighting', 'Poison',
  'Ground', 'Flying', 'Psychic', 'Bug', 'Rock', 'Ghost', 'Dragon', 'Dark', 'Steel', 'Fairy']);
const elem = (t) => VALID_TYPES.has(t) ? t : 'Normal';

// --- Collect referenced moves ---
const usedMoves = new Set(['tackle', 'struggle']);
const learnsetById = {};
for (const s of species) {
  const lm = levelupMoves(s.id);
  learnsetById[s.id] = lm;
  for (const e of lm) usedMoves.add(e.moveid);
}

const moves = [];
for (const moveid of usedMoves) {
  const mv = Moves[moveid];
  if (!mv) { console.error('missing move', moveid); continue; }
  const category = mv.category; // Physical/Special/Status
  let power = mv.basePower || 0;
  if (power === 0 && category !== 'Status') power = 50; // fixed/variable-power fallbacks
  const acc = mv.accuracy === true ? 0 : mv.accuracy; // 0 = never-miss sentinel in engine
  const eff = mapEffect(mv);
  moves.push({
    id: moveid,
    name: mv.name,
    type: elem(mv.type),
    power,
    accuracy: acc,
    pp: mv.pp,
    effect: eff.effect,
    chance: eff.chance,
    category,
    priority: mv.priority || 0,
    stage: eff.stage,
  });
}
moves.sort((a, b) => a.name.localeCompare(b.name));

// --- Evolution: resolve the first Gen-1 evolution target + how it evolves ---
const toID = (s) => ('' + s).toLowerCase().replace(/[^a-z0-9]/g, '');
function evoInfo(s) {
  if (!s.evos || !s.evos.length) return null;
  for (const name of s.evos) {
    const toId = toID(name);
    const e = Pokedex[toId];
    if (!e || !e.num || e.num > 151) continue; // keep evolutions inside the Kanto roster
    const level = e.evoLevel || 0;
    let method = null;
    if (!level) {
      if (e.evoType === 'useItem') method = e.evoItem || 'a special stone';
      else if (e.evoType === 'trade') method = 'Trade';
      else if (e.evoType === 'levelFriendship') method = 'high friendship';
      else if (e.evoType) method = e.evoCondition || e.evoType;
      else method = e.evoCondition || 'a special condition';
    }
    return { toId, level, method };
  }
  return null;
}

// --- Emit intermediate JSON ---
const outSpecies = species.map((s) => ({
  id: s.id,
  name: s.name,
  num: s.num,
  types: s.types.map(elem),
  stats: s.baseStats,
  color: s.color,
  prevo: s.prevo || null,
  evos: s.evos || null,
  evo: evoInfo(s),
  bst: Object.values(s.baseStats).reduce((a, b) => a + b, 0),
  capture: meta[s.num]?.capture ?? 45,
  habitat: meta[s.num]?.habitat ?? 0,
  legendary: meta[s.num]?.legendary ?? false,
  learnset: learnsetById[s.id],
}));

fs.writeFileSync(path.join(__dirname, 'gen1.json'),
  JSON.stringify({ species: outSpecies, moves }, null, 1));
console.log('species:', outSpecies.length, 'moves:', moves.length);
console.log('sample:', outSpecies[0].name, outSpecies[0].types, outSpecies[0].learnset.slice(0, 4));
