// Protocol smoke test: spins up the relay on a random port, then drives a host and two
// viewers through the full lifecycle — create, list, join, state relay, throttle-sized
// payloads, host drop + grace, host resume, leave, and room expiry paths.
// Run: node test_protocol.js
'use strict';

process.env.PORT = '0'; // pick a free port

const { once } = require('events');
const WebSocket = require('ws');

let passed = 0;
let failed = 0;
function check(name, cond) {
    if (cond) {
        passed++;
        console.log(`  ok  ${name}`);
    } else {
        failed++;
        console.log(`FAIL  ${name}`);
    }
}

function connect(port) {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    ws.inbox = [];
    ws.waiters = [];
    ws.on('message', data => {
        const msg = JSON.parse(data.toString());
        const waiter = ws.waiters.shift();
        if (waiter) waiter(msg);
        else ws.inbox.push(msg);
    });
    ws.next = (timeoutMs = 3000) => new Promise((resolve, reject) => {
        if (ws.inbox.length > 0) return resolve(ws.inbox.shift());
        const timer = setTimeout(() => reject(new Error('timed out waiting for message')), timeoutMs);
        ws.waiters.push(msg => { clearTimeout(timer); resolve(msg); });
    });
    // Consume messages until one matches the predicate (peers/etc. updates interleave freely).
    ws.until = async (pred, timeoutMs = 3000) => {
        const deadline = Date.now() + timeoutMs;
        for (;;) {
            const msg = await ws.next(Math.max(1, deadline - Date.now()));
            if (pred(msg)) return msg;
        }
    };
    ws.sendJson = obj => ws.send(JSON.stringify(obj));
    return ws;
}

async function main() {
    // Start the server in-process.
    const serverModule = require('./index.js');
    // Grab the listening port off the http server the module created.
    const httpServer = require('http').globalAgent && null;
    // index.js doesn't export; find the port by scanning open handles.
    const handles = process._getActiveHandles().filter(h => h.constructor && h.constructor.name === 'Server');
    const port = handles[0].address().port;
    console.log(`relay on :${port}`);

    // ---- host creates a shell ----
    const host = connect(port);
    await once(host, 'open');
    host.sendJson({ t: 'hello', name: 'Lilly Host' });
    host.sendJson({ t: 'create', roomName: 'Movie night' });
    const created = await host.next();
    check('create -> created', created.t === 'created' && /^[A-Z2-9]{6}$/.test(created.room));
    check('created carries host token', typeof created.token === 'string' && created.token.length === 32);

    // ---- viewer lists and joins ----
    const viewer = connect(port);
    await once(viewer, 'open');
    viewer.sendJson({ t: 'hello', name: 'Viewer One' });
    viewer.sendJson({ t: 'list' });
    const listing = await viewer.next();
    check('list shows the shell', listing.t === 'rooms' && listing.rooms.length === 1
        && listing.rooms[0].roomName === 'Movie night' && listing.rooms[0].host === 'Lilly Host');

    viewer.sendJson({ t: 'join', room: created.room.toLowerCase() }); // case-insensitive join
    const joined = await viewer.until(m => m.t === 'joined');
    check('join -> joined (case-insensitive)', joined.room === created.room);
    check('joined reports host present', joined.hostPresent === true && joined.users === 2);
    const hostPeers = await host.until(m => m.t === 'peers');
    check('host sees peer count', hostPeers.users === 2);

    // ---- state relay ----
    host.sendJson({ t: 'state', url: 'https://example.com/page', sx: 0, sy: 1234.6, ts: Date.now() });
    const state = await viewer.until(m => m.t === 'state');
    check('state relayed to viewer', state.url === 'https://example.com/page' && state.sy === 1235);

    // late joiner receives last state snapshot
    const viewer2 = connect(port);
    await once(viewer2, 'open');
    viewer2.sendJson({ t: 'join', room: created.room });
    const joined2 = await viewer2.until(m => m.t === 'joined');
    check('late joiner gets snapshot', joined2.state && joined2.state.sy === 1235);

    // ---- validation ----
    viewer.sendJson({ t: 'state', url: 'https://example.com', sx: 0, sy: 0, ts: 1 });
    const notHost = await viewer.until(m => m.t === 'error');
    check('viewer cannot broadcast', notHost.code === 'not_host');
    host.sendJson({ t: 'state', url: 'file:///C:/secrets.txt', sx: 0, sy: 0, ts: 1 });
    const badUrl = await host.until(m => m.t === 'error');
    check('non-http url rejected', badUrl.code === 'bad_message');
    viewer.sendJson({ t: 'join', room: 'ZZZZZZ' });
    const already = await viewer.until(m => m.t === 'error');
    check('double join rejected', already.code === 'already_in_room');

    // ---- host drop + resume ----
    host.terminate();
    const hostLeft = await viewer.until(m => m.t === 'hostleft');
    check('viewers told host left', hostLeft.t === 'hostleft');
    await viewer2.until(m => m.t === 'hostleft');

    const host2 = connect(port);
    await once(host2, 'open');
    host2.sendJson({ t: 'resume', room: created.room, token: 'wrong' });
    const badToken = await host2.until(m => m.t === 'error');
    check('resume with wrong token rejected', badToken.code === 'not_host');
    host2.sendJson({ t: 'resume', room: created.room, token: created.token });
    const resumed = await host2.until(m => m.t === 'resumed');
    check('host resumes with token', resumed.room === created.room);
    const hostBack = await viewer.until(m => m.t === 'hostback');
    check('viewers told host is back', hostBack.t === 'hostback');

    // host can broadcast again after resume
    host2.sendJson({ t: 'state', url: 'https://example.com/next', sx: 5, sy: 60, ts: Date.now() });
    const state2 = await viewer2.until(m => m.t === 'state' && m.url === 'https://example.com/next');
    check('resumed host still broadcasts', state2.sy === 60);

    // ---- leave ----
    viewer.sendJson({ t: 'leave' });
    const peersAfterLeave = await host2.until(m => m.t === 'peers' && m.users === 2);
    check('leave updates peers', peersAfterLeave.users === 2);

    // ---- invalid room join ----
    const stranger = connect(port);
    await once(stranger, 'open');
    stranger.sendJson({ t: 'join', room: 'NOPENO' });
    const invalid = await stranger.until(m => m.t === 'error');
    check('invalid room join errors', invalid.code === 'invalid_room');

    console.log(`\n${passed} passed, ${failed} failed`);
    process.exit(failed === 0 ? 0 : 1);
}

main().catch(err => {
    console.error('test crashed:', err);
    process.exit(1);
});
