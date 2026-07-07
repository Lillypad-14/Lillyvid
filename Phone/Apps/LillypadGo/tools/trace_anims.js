// Traces Pokémon Showdown's move animations (battle-animations-moves.js, CC0-1.0; engine
// battle-animations.js, MIT) by EXECUTING each move's anim() against an instrumented mock of
// the BattleScene/Sprite API and recording the resulting keyframe timelines. This captures the
// full choreography — loops, helper anims (BattleOtherAnims), chained sprite keyframes, waits —
// as plain data, unlike static parsing.
//
// Coordinates are recorded in Showdown scene units with the canonical singles layout:
//   attacker (near/back): x=0 y=0 z=0     defender (far/front): x=0 y=0 z=200
// The C# runtime re-projects these through Showdown's pos() math onto the battle screen.
//
// Usage: node trace_anims.js  -> moveanims.json
'use strict';

const fs = require('fs');
const path = require('path');

const engineSrc = fs.readFileSync(path.join(__dirname, 'anim', 'battle-animations.js'), 'utf8');
const movesSrc = fs.readFileSync(path.join(__dirname, 'anim', 'battle-animations-moves.js'), 'utf8');

// ---- Extract the data objects we need from the engine file (balanced-brace slice) ----
function sliceObject(src, marker) {
    const at = src.indexOf(marker);
    if (at < 0) throw new Error('marker not found: ' + marker);
    let i = src.indexOf('{', at);
    let depth = 0;
    let inStr = null;
    for (; i < src.length; i++) {
        const ch = src[i];
        if (inStr) {
            if (ch === '\\') i++;
            else if (ch === inStr) inStr = null;
        } else if (ch === '"' || ch === "'") {
            inStr = ch;
        } else if (ch === '{') {
            depth++;
        } else if (ch === '}') {
            depth--;
            if (depth === 0) return src.slice(at, i + 1) + ';';
        }
    }
    throw new Error('unbalanced braces for ' + marker);
}

const effectsSrc = sliceObject(engineSrc, 'var BattleEffects={');
const otherSrc = sliceObject(engineSrc, 'var BattleOtherAnims={');
const statusSrc = sliceObject(engineSrc, 'var BattleStatusAnims={');

// battle-animations-moves.js is `"use strict";` + header comment + `var BattleMoveAnims={...}`.
const load = new Function(
    "var Config={routes:{client:'play.pokemonshowdown.com'}};\n" +
    effectsSrc + '\n' + otherSrc + '\n' + statusSrc + '\n' + movesSrc + '\n' +
    'return {BattleEffects:BattleEffects,BattleOtherAnims:BattleOtherAnims,' +
    'BattleStatusAnims:BattleStatusAnims,BattleMoveAnims:BattleMoveAnims};');
const { BattleEffects, BattleOtherAnims, BattleStatusAnims, BattleMoveAnims } = load();

// Give each effect its id so recorded segments can name it.
for (const id in BattleEffects) BattleEffects[id].id = id;

// ---- Deterministic Math.random so traces are stable across regenerations ----
let rngState = 1;
const realRandom = Math.random;
Math.random = () => {
    rngState = (rngState * 1103515245 + 12345) & 0x7fffffff;
    return rngState / 0x7fffffff;
};
function seedRng(str) {
    rngState = 0;
    for (let i = 0; i < str.length; i++) rngState = (rngState * 31 + str.charCodeAt(i)) & 0x7fffffff;
    rngState = rngState || 1;
}

// ---- Mock scene + sprites -----------------------------------------------------------
// Mirrors BattleScene.animateEffect's normalization exactly (time defaults, timeOffset,
// end-inherits-start) but records scene-space keyframes instead of animating DOM nodes.

function num(v, dflt) { return typeof v === 'number' && isFinite(v) ? v : dflt; }

// Resolve a loc the way pos() does: fill defaults, xscale/yscale fall back to scale.
function resolveLoc(loc) {
    const scale = num(loc.scale, 1);
    return {
        x: num(loc.x, 0),
        y: num(loc.y, 0),
        z: num(loc.z, 0),
        xscale: num(loc.xscale, scale),
        yscale: num(loc.yscale, scale),
        opacity: num(loc.opacity, 1),
        time: num(loc.time, 0),
    };
}

// Chainable stand-in for the scene's jQuery-wrapped bg element. Moves like Earthquake shake
// the field by tweening $bg's top (base -90); we record that as a screen-shake track.
class MockBg {
    constructor(scene) {
        this.scene = scene;
        this.queueTime = 0;
        this.cur = 0;
        this.segs = [];
    }

    delay(time) {
        this.queueTime += time;
        return this;
    }

    animate(props, dur) {
        const y = num(props.top, -90) + 90;
        this.segs.push({ t0: this.queueTime, t1: this.queueTime + dur, y0: this.cur, y1: y });
        this.queueTime += dur;
        this.cur = y;
        this.scene.maxT = Math.max(this.scene.maxT, this.queueTime);
        return this;
    }

    css() { return this; }
}

class MockScene {
    constructor() {
        this.$bg = new MockBg(this);
        this.timeOffset = 0;
        this.pokemonTimeOffset = 0;
        this.gen = 6;
        this.mod = '';
        this.activeCount = 1;
        this.acceleration = 1;
        this.animating = true;
        this.battle = { gameType: 'singles', dex: null };
        this.fx = [];
        this.bg = [];
        this.maxT = 0;
    }

    wait(time) { this.timeOffset += time; }

    showEffect(effect, start, end, transition, after, additionalCss) {
        if (typeof effect === 'string') effect = BattleEffects[effect];
        if (!effect) return;
        if (additionalCss) cssMoves.add(currentMove);
        start = Object.assign({}, start);
        end = Object.assign({}, end);
        if (!start.time) start.time = 0;
        if (!end.time && end.time !== 0) end.time = start.time + 500;
        start.time += this.timeOffset;
        end.time += this.timeOffset;
        if (!end.scale && end.scale !== 0 && start.scale) end.scale = start.scale;
        if (!end.xscale && end.xscale !== 0 && start.xscale) end.xscale = start.xscale;
        if (!end.yscale && end.yscale !== 0 && start.yscale) end.yscale = start.yscale;
        end = Object.assign({}, start, end);
        const rec = {
            sprite: effect.id || '$mon',
            a: resolveLoc(start),
            b: resolveLoc(end),
            tr: transition || 'linear',
            after: after || '',
        };
        this.fx.push(rec);
        this.maxT = Math.max(this.maxT,
            rec.b.time + (after === 'fade' ? 100 : after === 'explode' ? 200 : 0));
        return { css: () => {}, animate: () => {}, delay: () => {} };
    }

    backgroundEffect(bg, duration, opacity, delay) {
        if (opacity === undefined) opacity = 1;
        if (delay === undefined) delay = 0;
        this.bg.push({ bg: String(bg), duration, opacity, delay });
        this.maxT = Math.max(this.maxT, delay + duration + 250);
    }
}

class MockSprite {
    constructor(scene, isFront) {
        this.scene = scene;
        this.isFrontSprite = isFront;
        this.isMissedPokemon = false;
        this.isSubActive = false;
        this.x = 0;
        this.y = 0;
        this.z = isFront ? 200 : 0;
        this.sp = { id: isFront ? '$defender' : '$attacker', url: '', w: 96, h: 96 };
        this.queueTime = 0;
        this.cur = { x: this.x, y: this.y, z: this.z, xscale: 1, yscale: 1, opacity: 1 };
        this.segs = [];
    }

    behindx(o) { return this.x + (this.isFrontSprite ? 1 : -1) * o; }
    behindy(o) { return this.y + (this.isFrontSprite ? -1 : 1) * o; }
    leftof(o) { return this.x + (this.isFrontSprite ? 1 : -1) * o; }
    behind(o) { return this.z + (this.isFrontSprite ? 1 : -1) * o; }

    delay(time) {
        this.queueTime += time;
        return this;
    }

    anim(end, transition) {
        end = Object.assign({ x: this.x, y: this.y, z: this.z, scale: 1, opacity: 1, time: 500 }, end);
        const to = resolveLoc(end);
        if (end.time === 0) {
            // Instant jump (css set), no tween.
            this.cur = { x: to.x, y: to.y, z: to.z, xscale: to.xscale, yscale: to.yscale, opacity: to.opacity };
            this.segs.push({ t0: this.queueTime, t1: this.queueTime, from: this.cur, to: this.cur, tr: 'linear' });
            return this;
        }
        this.segs.push({
            t0: this.queueTime,
            t1: this.queueTime + end.time,
            from: Object.assign({}, this.cur),
            to,
            tr: transition || 'linear',
        });
        this.queueTime += end.time;
        this.cur = { x: to.x, y: to.y, z: to.z, xscale: to.xscale, yscale: to.yscale, opacity: to.opacity };
        this.scene.maxT = Math.max(this.scene.maxT, this.queueTime);
        return this;
    }
}

// ---- Runtime JSON encoding ------------------------------------------------------------
// Compact arrays keyed for the C# runtime. Transition codes:
//   0 linear, 1 ballistic, 2 ballisticUnder, 3 ballistic2, 4 ballistic2Back,
//   5 ballistic2Under, 6 swing, 7 accel, 8 decel
// After codes: 0 none, 1 fade (100ms), 2 explode (200ms).
const TR = {
    linear: 0, ballistic: 1, ballisticUnder: 2, ballistic2: 3, ballistic2Back: 4,
    ballistic2Under: 5, swing: 6, accel: 7, decel: 8,
};
const AF = { '': 0, fade: 1, explode: 2 };

const r3 = v => Math.round(v * 1000) / 1000;
const locArr = l => [r3(l.x), r3(l.y), r3(l.z), r3(l.xscale), r3(l.yscale), r3(l.opacity), r3(l.time)];
const monLocArr = l => [r3(l.x), r3(l.y), r3(l.z), r3(l.xscale), r3(l.yscale), r3(l.opacity)];

function parseBg(bg) {
    const urlMatch = bg.match(/url\('([^']+)'\)/);
    if (urlMatch) return { img: urlMatch[1].split('/').pop(), c0: '000000', c1: '000000' };
    const hexes = [...bg.matchAll(/#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})/g)].map(m => {
        let h = m[1];
        if (h.length === 3) h = h.split('').map(c => c + c).join('');
        return h.toUpperCase();
    });
    if (hexes.length === 0) return { img: null, c0: '000000', c1: '000000' };
    return { img: null, c0: hexes[0], c1: hexes[hexes.length - 1] };
}

function encodeVariant(scene, attacker, defender) {
    const dur = Math.max(scene.maxT, attacker.queueTime, defender.queueTime);
    return {
        d: r3(dur),
        fx: scene.fx.map(f => ({ s: f.sprite, tr: TR[f.tr] ?? 0, af: AF[f.after] ?? 0, a: locArr(f.a), b: locArr(f.b) })),
        am: attacker.segs.map(s => [r3(s.t0), r3(s.t1), ...monLocArr(s.from), ...monLocArr(s.to), TR[s.tr] ?? 0]),
        dm: defender.segs.map(s => [r3(s.t0), r3(s.t1), ...monLocArr(s.from), ...monLocArr(s.to), TR[s.tr] ?? 0]),
        bg: scene.bg.map(b => Object.assign(parseBg(b.bg), { delay: r3(b.delay), dur: r3(b.duration), o: r3(b.opacity) })),
        sh: scene.$bg.segs.map(s => [r3(s.t0), r3(s.t1), r3(s.y0), r3(s.y1)]),
    };
}

// ---- Run every move used by the app, in both orientations ----------------------------
// near: the attacker is the player's mon (back sprite, z=0); far: the attacker is the wild
// mon (front sprite, z=200). Showdown's anim code branches on this via behind()/leftof(),
// so both variants are traced from the real code rather than mirrored.
const movesDataCs = fs.readFileSync(path.join(__dirname, '..', 'MovesData.g.cs'), 'utf8');
const moveIds = [...new Set([...movesDataCs.matchAll(/d\["([a-z0-9]+)"\]/g)].map(m => m[1]))];

let currentMove = '';
const cssMoves = new Set();
const out = { sprites: {}, moves: {} };
const failures = [];
let bespoke = 0;
let fallback = 0;

function traceVariant(id, entry, attackerFar) {
    seedRng(id);
    const scene = new MockScene();
    const attacker = new MockSprite(scene, attackerFar);
    const defender = new MockSprite(scene, !attackerFar);
    entry.anim(scene, [attacker, defender]);
    for (const fx of scene.fx) {
        if (fx.sprite[0] !== '$') {
            const eff = BattleEffects[fx.sprite];
            out.sprites[fx.sprite] = { w: eff.w, h: eff.h, y: eff.y || 0 };
        }
    }
    return encodeVariant(scene, attacker, defender);
}

for (const id of moveIds) {
    const entry = BattleMoveAnims[id];
    if (!entry || !entry.anim) {
        out.moves[id] = { alias: 'tackle' };
        fallback++;
        continue;
    }
    currentMove = id;
    try {
        out.moves[id] = { n: traceVariant(id, entry, false), f: traceVariant(id, entry, true) };
        bespoke++;
    } catch (err) {
        failures.push(id + ': ' + err.message);
        out.moves[id] = { alias: 'tackle' };
    }
}

// The tackle fallback must exist even if 'tackle' is somehow absent from the id list.
if (!out.moves.tackle || out.moves.tackle.alias) {
    throw new Error('tackle must trace successfully (it is the universal fallback)');
}

Math.random = realRandom;

const outPath = path.join(__dirname, '..', '..', '..', '..', 'Assets', 'pokemon', 'moveanims.json');
fs.writeFileSync(outPath, JSON.stringify(out));
console.log('wrote ' + outPath + ' (' + fs.statSync(outPath).size + ' bytes)');
console.log('traced ' + bespoke + ' bespoke move anims, ' + fallback + ' tackle fallbacks');
console.log('fx sprites used: ' + Object.keys(out.sprites).length);
if (failures.length) console.log('FAILURES:\n  ' + failures.join('\n  '));
if (cssMoves.size) console.log('moves using additionalCss (ignored): ' + [...cssMoves].join(', '));
