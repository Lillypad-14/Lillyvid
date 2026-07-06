// From Showdown's CC0-licensed BattleMoveAnims, extract per-move { pattern, sprite } data:
// - pattern: from the BattleOtherAnims.<x> shared anim it uses, else from move metadata.
// - sprite: the fx art name it passes to showEffect(), else a type default.
// This reads only asset-reference facts, not the animation code itself.
const fs = require('fs');
const path = require('path');

const anims = fs.readFileSync(path.join(__dirname, 'anim', 'anims.js'), 'utf8');
const { moves } = require('./gen1.json');

// Split the big object into per-move blocks.
const blocks = {};
const re = /\n([a-z0-9]+):\{/g;
let m, prev = null, prevStart = 0;
while ((m = re.exec(anims)) !== null) {
  if (prev) blocks[prev] = anims.slice(prevStart, m.index);
  prev = m[1];
  prevStart = m.index;
}
if (prev) blocks[prev] = anims.slice(prevStart);

const SHARED_PATTERN = {
  contactattack: 'Contact', xattack: 'Contact', punchattack: 'Contact', fastattack: 'Contact',
  spinattack: 'Contact', slashattack: 'Contact', clawattack: 'Contact', shake: 'Contact',
  bite: 'Contact', flight: 'Contact', kick: 'Contact', dance: 'SelfBuff', selfstatus: 'SelfBuff',
  chargestatus: 'SelfBuff', sound: 'Cloud', hydroshot: 'Projectile',
};

// Aux sprites that aren't the "signature" projectile; deprioritise when choosing.
const AUX = new Set(['shine', 'impact', 'wisp', 'angry', 'pointer', 'foot', 'mail', 'item',
  'blackwisp', 'heart', 'pokeball', 'mudwisp']);

const TYPE_SPRITE = {
  Fire: 'fireball', Water: 'waterwisp', Grass: 'leaf1', Electric: 'electroball', Ice: 'iceball',
  Ground: 'mudwisp', Rock: 'rock1', Poison: 'poisonwisp', Psychic: 'mistball', Ghost: 'shadowball',
  Bug: 'web', Flying: 'feather', Dragon: 'bluefireball', Dark: 'blackwisp', Steel: 'gear',
  Fairy: 'moon', Fighting: 'fist', Normal: 'wisp',
};

const BEAM = /beam|cannon|pulse|hyper|blast|thrower|hydro pump|solar|overheat|discharge|thunder\b|psychic|aurora|spectral|flash|tri attack|swift|barrage|spike cannon/i;
const CONTACT = /punch|kick|slash|claw|bite|tackle|pound|chop|headbutt|slam|peck|horn|wing|megahorn|take down|double-edge|quick attack|scratch|\bcut\b|strike|dig|stomp|body|fury|rage|thrash|crabhammer|guillotine|submission|seismic/i;
const CLOUD = /powder|spore|wave|sand|whirlwind|gust|smog|smokescreen|string shot|glare|leer|growl|screech|roar|sing|supersonic|hypnosis|confuse|toxic|acid armor|mist|haze|reflect|barrier|screen|disable|mimic/i;

function chooseSprite(block, type) {
  if (block) {
    const names = [...block.matchAll(/showEffect\('([a-z0-9_]+)'/g)].map((x) => x[1]);
    const signature = names.find((n) => !AUX.has(n));
    if (signature) return signature;
    if (names.length) return names[0];
  }
  return TYPE_SPRITE[type] || 'wisp';
}

function choosePattern(block, mv) {
  // A status move is never a contact attack visually; decide from its own effect.
  if (mv.category === 'Status') {
    const self = mv.effect.startsWith('Raise') || mv.effect === 'HealUser'
      || /dance|growth|sharpen|agility|rest|barrier|reflect|light screen|acid armor|amnesia|withdraw|harden|meditate|focus energy|charge|coil|calm mind|bulk up|nasty plot|cosmic|shell smash|conversion|recover|roost|synthesis|morning sun|moonlight|slack off|wish|defense curl|double team|minimize/i.test(mv.name);
    return self ? 'SelfBuff' : 'Cloud';
  }
  if (block) {
    const shared = block.match(/BattleOtherAnims\.([a-zA-Z]+)/);
    if (shared && SHARED_PATTERN[shared[1]]) return SHARED_PATTERN[shared[1]];
  }
  if (BEAM.test(mv.name)) return 'Beam';
  if (CONTACT.test(mv.name)) return 'Contact';
  if (CLOUD.test(mv.name)) return 'Cloud';
  return 'Projectile';
}

const visuals = {};
const spritesUsed = new Set();
let fromAnim = 0;
for (const mv of moves) {
  const block = blocks[mv.id];
  if (block) fromAnim++;
  const sprite = chooseSprite(block, mv.type);
  const pattern = choosePattern(block, mv);
  spritesUsed.add(sprite);
  visuals[mv.name] = { pattern, sprite };
}

fs.writeFileSync(path.join(__dirname, 'movevisuals.json'),
  JSON.stringify({ visuals, sprites: [...spritesUsed].sort() }, null, 1));

const patCount = {};
for (const v of Object.values(visuals)) patCount[v.pattern] = (patCount[v.pattern] || 0) + 1;
console.log('moves:', moves.length, 'matched anim block:', fromAnim);
console.log('patterns:', patCount);
console.log('sprites used (%d):', spritesUsed.size, [...spritesUsed].sort().join(' '));
console.log('samples:', ['Thunderbolt', 'Razor Leaf', 'Ember', 'Tackle', 'Swords Dance', 'Sleep Powder', 'Hydro Pump', 'Surf']
  .map((n) => `${n}=${visuals[n].pattern}/${visuals[n].sprite}`).join('  '));
