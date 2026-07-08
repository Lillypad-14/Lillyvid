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
const Abilities = evalTable('abilities.ts', 'Abilities');
// Ability descriptions live in the separate text/localisation file, not data/abilities.ts.
const AbilitiesText = evalTable('abilities_text.ts', 'AbilitiesText');

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

// --- Level-up learnset ---
// Prefer Scarlet/Violet (gen 9) level-up moves: a move is a real SV level-up move only if its
// newest code is "9L<level>". Falling back to the newest older-gen L-code (the previous behaviour)
// pulled TM-only SV moves at their old level-up levels, which read as wrong. Only mons with no SV
// level-up data at all (rare for Kanto) use the legacy newest-gen fallback.
function levelupMoves(id) {
  const entry = Learnsets[id];
  const out = [];
  if (!entry || !entry.learnset) return out;

  const sv = [];
  for (const [moveid, codes] of Object.entries(entry.learnset)) {
    for (const code of codes) {
      const m = /^9L(\d+)$/.exec(code);
      if (m) { sv.push({ moveid, level: Math.max(1, +m[1]) }); break; }
    }
  }
  if (sv.length) {
    sv.sort((a, b) => a.level - b.level || a.moveid.localeCompare(b.moveid));
    return sv;
  }

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
const STATUS = { brn: 'Burn', frz: 'Freeze', slp: 'Sleep', par: 'Paralyze', psn: 'Poison', tox: 'Poison' };
const BOOST_LOWER = { atk: 'LowerTargetAtk', def: 'LowerTargetDef', spa: 'LowerTargetSpAtk', spd: 'LowerTargetSpDef', spe: 'LowerTargetSpd', accuracy: 'LowerTargetAccuracy' };
const BOOST_RAISE = { atk: 'RaiseAtk', def: 'RaiseDef', spa: 'RaiseSpAtk', spd: 'RaiseSpDef', spe: 'RaiseSpd', accuracy: 'RaiseAccuracy', evasion: 'RaiseEvasion' };

const SPECIAL_EFFECTS = {
  acupressure: ['Acupressure', 0, 1],
  afteryou: ['RaiseSpd', 0, 1],
  allyswitch: ['RaiseEvasion', 0, 1],
  aquaring: ['AquaRing', 0, 1],
  aromatherapy: ['CureUserStatus', 0, 1],
  batonpass: ['RaiseSpd', 0, 1],
  bellydrum: ['BellyDrum', 0, 1],
  block: ['LowerTargetSpd', 0, 1],
  camouflage: ['RaiseDef', 0, 1],
  charge: ['RaiseSpDef', 0, 1],
  clearsmog: ['Haze', 0, 1],
  conversion: ['RaiseDef', 0, 1],
  conversion2: ['RaiseSpDef', 0, 1],
  copycat: ['RaiseAtk', 0, 1],
  curse: ['RaiseAtk', 0, 1],
  destinybond: ['NoOp', 0, 1],
  detect: ['ProtectUser', 0, 1],
  disable: ['LowerTargetAccuracy', 0, 1],
  electricterrain: ['SetElectricTerrain', 0, 1],
  encore: ['LowerTargetAtk', 0, 1],
  endure: ['EndureUser', 0, 1],
  focusenergy: ['RaiseAtk', 0, 1],
  followme: ['RaiseDef', 0, 1],
  foresight: ['LowerTargetEvasion', 0, 1],
  gastroacid: ['LowerTargetSpAtk', 0, 1],
  grassyterrain: ['SetGrassyTerrain', 0, 1],
  gravity: ['LowerTargetEvasion', 0, 1],
  guardswap: ['RaiseSpDef', 0, 1],
  haze: ['Haze', 0, 1],
  helpinghand: ['RaiseAtk', 0, 1],
  imprison: ['LowerTargetSpAtk', 0, 1],
  ingrain: ['Ingrain', 0, 1],
  leechseed: ['LeechSeed', 0, 1],
  lightscreen: ['LightScreenSide', 0, 1],
  lockon: ['RaiseAccuracy', 0, 2],
  magnetrise: ['RaiseEvasion', 0, 1],
  magneticflux: ['RaiseDef', 0, 1],
  mefirst: ['RaiseAtk', 0, 1],
  meanlook: ['LowerTargetSpd', 0, 1],
  metronome: ['NoOp', 0, 1],
  mimic: ['RaiseAccuracy', 0, 1],
  miracleeye: ['LowerTargetEvasion', 0, 1],
  mirrormove: ['RaiseAtk', 0, 1],
  mist: ['LightScreenSide', 0, 1],
  mistyterrain: ['SetMistyTerrain', 0, 1],
  mudsport: ['RaiseSpDef', 0, 1],
  perishsong: ['NoOp', 0, 1],
  powerswap: ['RaiseSpAtk', 0, 1],
  protect: ['ProtectUser', 0, 1],
  psychicterrain: ['SetPsychicTerrain', 0, 1],
  psychup: ['Acupressure', 0, 1],
  quickguard: ['ProtectUser', 0, 1],
  ragepowder: ['RaiseDef', 0, 1],
  raindance: ['SetRain', 0, 1],
  recycle: ['HealUser', 0, 1],
  reflect: ['ReflectSide', 0, 1],
  reflecttype: ['RaiseDef', 0, 1],
  refresh: ['CureUserStatus', 0, 1],
  roar: ['ForceSwitch', 0, 1],
  roleplay: ['RaiseSpAtk', 0, 1],
  safeguard: ['LightScreenSide', 0, 1],
  sandstorm: ['SetSandstorm', 0, 1],
  sleeptalk: ['NoOp', 0, 1],
  snowscape: ['SetSnow', 0, 1],
  soak: ['LowerTargetSpDef', 0, 1],
  spikes: ['LowerTargetSpd', 0, 1],
  spite: ['LowerTargetSpAtk', 0, 1],
  splash: ['NoOp', 0, 1],
  spotlight: ['RaiseDef', 0, 1],
  stealthrock: ['LowerTargetSpd', 0, 1],
  stockpile: ['RaiseDef', 0, 1],
  substitute: ['ProtectUser', 0, 1],
  sunnyday: ['SetSun', 0, 1],
  sweetscent: ['LowerTargetEvasion', 0, 1],
  switcheroo: ['LowerTargetAtk', 0, 1],
  tailwind: ['RaiseSpd', 0, 2],
  taunt: ['LowerTargetSpAtk', 0, 1],
  telekinesis: ['LowerTargetEvasion', 0, 1],
  teleport: ['ForceSwitch', 0, 1],
  toxicspikes: ['LowerTargetSpd', 0, 1],
  trick: ['LowerTargetAtk', 0, 1],
  watersport: ['RaiseSpDef', 0, 1],
  whirlwind: ['ForceSwitch', 0, 1],
  wideguard: ['ProtectUser', 0, 1],
  wonderroom: ['Haze', 0, 1],
  worryseed: ['LowerTargetSpAtk', 0, 1],
  yawn: ['Yawn', 0, 1],
};

function firstBoost(boosts, table) {
  for (const [k, v] of Object.entries(boosts)) {
    if (table[k] !== undefined) return { effect: table[k], stage: Math.abs(v) };
  }
  return null;
}

function mapEffect(mv, moveid) {
  // returns {effect, chance, stage}
  const none = { effect: 'None', chance: 0, stage: 1 };
  if (SPECIAL_EFFECTS[moveid]) {
    const [effect, chance, stage] = SPECIAL_EFFECTS[moveid];
    return { effect, chance, stage };
  }
  // 0) unique: Transform (Ditto)
  if (mv.name === 'Transform') return { effect: 'Transform', chance: 0, stage: 1 };
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
  const eff = mapEffect(mv, moveid);
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

// --- Abilities + gender ---
function abilitiesOf(s) {
  // Regular abilities first (slots 0/1), then the hidden ability, de-duplicated.
  const a = s.abilities || {};
  const list = [];
  for (const key of ['0', '1', 'H']) {
    if (a[key] && !list.includes(a[key])) list.push(a[key]);
  }
  return list.length ? list : ['Pressure'];
}
function maleRatioOf(s) {
  if (s.gender === 'N') return -1; // genderless
  if (s.gender === 'M') return 1;
  if (s.gender === 'F') return 0;
  return s.genderRatio ? s.genderRatio.M : 0.5;
}

// Collect used ability short descriptions.
const abilityDesc = {};
for (const s of species) {
  for (const name of abilitiesOf(s)) {
    if (abilityDesc[name] !== undefined) continue;
    const def = AbilitiesText[toID(name)];
    abilityDesc[name] = def && def.shortDesc ? def.shortDesc : '';
  }
}

// --- Emit intermediate JSON ---
const outSpecies = species.map((s) => ({
  id: s.id,
  name: s.name,
  num: s.num,
  types: s.types.map(elem),
  stats: s.baseStats,
  color: s.color,
  abilities: abilitiesOf(s),
  maleRatio: maleRatioOf(s),
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
  JSON.stringify({ species: outSpecies, moves, abilityDesc }, null, 1));
console.log('species:', outSpecies.length, 'moves:', moves.length);
console.log('sample:', outSpecies[0].name, outSpecies[0].types, outSpecies[0].learnset.slice(0, 4));
