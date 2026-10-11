/*
 * Quraan ayah player (everything runs in the browser; the server only lists and serves the mp3 files).
 *
 * Runs on /Quraan/Surah/{surahId}?sheikhId=... pages (loaded from _QuraanLayout).
 *  - On open it downloads the surah's ayah files from the chosen sheikh's folder, ONCE, into the browser's own
 *    storage (IndexedDB). Next visits use the saved copy (the server's file version tells if it is still valid).
 *  - Each file is decoded here to get its exact length and its loudness profile (the waveform).
 *  - "Joining" is virtual: a track is a list of ayah files played back to back with one timeline, one seek bar and
 *    one waveform. Any range (an ayah, from..to, the whole surah) is built instantly, no server work.
 *  - Splits the text inside #app into ayat. An ayah is the text that ends with its <label>(N)</label> marker.
 *  - Top player (sticky): the clicked ayah, or a range ("from / to" fields, Shift+click). Has a waveform.
 *  - Bottom player: the whole surah with a waveform. Clicking an ayah never moves it.
 *  - Both players: play, pause, resume, stop, -10/+10 seconds, speed 25%-400%. No cutting.
 */
(function () {
    'use strict';

    const pathMatch = /\/Quraan\/Surah\/([^\/?#]+)/i.exec(location.pathname);
    const app = document.getElementById('app');
    if (!pathMatch || !app) return;

    // ---------- settings ----------
    const SKIP_SECONDS = 10;
    const SPEED_MIN = 25;
    const SPEED_MAX = 400;
    const SPEED_DEFAULT = 100;
    const SPEED_KEY = 'qp.speed';
    const COLLAPSE_KEY = 'qp.topCollapsed';
    const SPEED_RES = 1000;
    const SPEED_PRESETS = [25, 50, 75, 100, 125, 150, 200, 300, 400];
    const SPEED_LADDER = (function () {
        const a = [];
        for (let v = 25; v <= 200; v += 5) a.push(v);
        for (let v = 210; v <= 400; v += 10) a.push(v);
        return a;
    })();

    const MIN_PPS = 0.02;
    const MAX_PPS = 400;
    const BAR_W = 2;
    const STEP = 3;
    const RULER_H = 20;
    const SILENCE = 0.07;        // sqrt-scaled amplitude below which a bar becomes a flat line
    const MIN_REF = 0.3;
    const ANALYSIS_RATE = 8000;        // sample rate used to measure each file and draw its waveform
    const PEAKS_PER_SECOND = 25;
    const DOWNLOAD_CONCURRENCY = 3;
    const DB_NAME = 'qp-audio';
    const AUDIO_TYPE = 'audio/mpeg';
    const RULER_STEPS = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];

    const Q = {
        sheikhId: new URLSearchParams(location.search).get('sheikhId') || '',
        surahId: decodeURIComponent(pathMatch[1]),
        ready: false,            // files endpoint answered and there is audio
        disabledMessage: 'جارٍ تجهيز الصوت، حاول بعد لحظة.',
        has: new Set(),          // ayah numbers that have an mp3
        min: 1,
        max: 1,
        selection: null,         // { from, to }
        anchor: null,
        repeat: false,
        token: 0
    };

    // =====================================================================
    // small helpers
    // =====================================================================

    function clamp(v, lo, hi) { return Math.min(hi, Math.max(lo, v)); }

    function el(tag, cls, attrs) {
        const e = document.createElement(tag);
        if (cls) e.className = cls;
        if (attrs) for (const k in attrs) e.setAttribute(k, attrs[k]);
        return e;
    }

    function span(text, cls) {
        const s = el('span', cls);
        s.textContent = text;
        return s;
    }

    // text lives in a <span>: the site themes recolour buttons but skip spans
    function button(label, cls, title) {
        const b = el('button', 'qp-btn ' + (cls || ''), { type: 'button' });
        b.appendChild(span(label));
        if (title) b.title = title;
        return b;
    }

    function setLabel(b, text) { b.firstChild.textContent = text; }

    function fmt(t, centis) {
        if (centis === undefined) centis = true;
        if (!isFinite(t) || t < 0) t = 0;
        const total = Math.floor(t * 100 + 1e-6);
        const cs = total % 100;
        const secs = Math.floor(total / 100);
        const s = secs % 60;
        const m = Math.floor(secs / 60) % 60;
        const h = Math.floor(secs / 3600);
        const p = function (n) { return String(n).padStart(2, '0'); };
        const base = h > 0 ? h + ':' + p(m) + ':' + p(s) : p(m) + ':' + p(s);
        return centis ? base + '.' + p(cs) : base;
    }

    // ---------- toast ----------
    const toastEl = el('div', 'qp-toast', { role: 'status', 'aria-live': 'polite' });
    const toastText = span('');
    toastEl.appendChild(toastText);
    document.body.appendChild(toastEl);
    let toastTimer = 0;

    function toast(message, isError) {
        clearTimeout(toastTimer);
        toastText.textContent = message;
        toastEl.classList.toggle('is-error', !!isError);
        toastEl.classList.add('is-visible');
        toastTimer = setTimeout(function () { toastEl.classList.remove('is-visible'); }, isError ? 5200 : 3200);
    }

    // =====================================================================
    // playback speed (shared by both players)
    // =====================================================================

    const Speed = {
        pct: SPEED_DEFAULT,

        snap: function (pct) {
            if (!isFinite(pct)) return SPEED_DEFAULT;
            pct = clamp(pct, SPEED_MIN, SPEED_MAX);
            let best = SPEED_LADDER[0];
            let dist = Infinity;
            for (let i = 0; i < SPEED_LADDER.length; i++) {
                const d = Math.abs(SPEED_LADDER[i] - pct);
                if (d < dist) { dist = d; best = SPEED_LADDER[i]; }
            }
            return best;
        },

        // logarithmic slider: 100% sits in the middle
        toPos: function (pct) {
            return Math.round(SPEED_RES * Math.log(pct / SPEED_MIN) / Math.log(SPEED_MAX / SPEED_MIN));
        },

        fromPos: function (pos) {
            return SPEED_MIN * Math.pow(SPEED_MAX / SPEED_MIN, pos / SPEED_RES);
        },

        set: function (pct, persist) {
            this.pct = this.snap(pct);
            QPlayer.all.forEach(function (p) { p.syncSpeed(); });
            if (persist !== false) {
                try { localStorage.setItem(SPEED_KEY, String(this.pct)); } catch (_) { /* storage may be blocked */ }
            }
        },

        step: function (direction) {
            let i = SPEED_LADDER.indexOf(this.pct);
            if (i < 0) i = SPEED_LADDER.indexOf(this.snap(this.pct));
            this.set(SPEED_LADDER[clamp(i + direction, 0, SPEED_LADDER.length - 1)]);
        }
    };

    // =====================================================================
    // saved copy of the audio inside the browser (IndexedDB)
    // =====================================================================

    const Store = {
        dbPromise: null,

        open: function () {
            if (this.dbPromise) return this.dbPromise;
            this.dbPromise = new Promise(function (resolve) {
                try {
                    const rq = indexedDB.open(DB_NAME, 1);
                    rq.onupgradeneeded = function () { rq.result.createObjectStore('files', { keyPath: 'key' }); };
                    rq.onsuccess = function () { resolve(rq.result); };
                    rq.onerror = function () { resolve(null); };
                    rq.onblocked = function () { resolve(null); };
                } catch (_) {
                    resolve(null);   // storage unavailable (private mode ...): the page still works, it just downloads again next time
                }
            });
            return this.dbPromise;
        },

        get: async function (key) {
            const db = await this.open();
            if (!db) return null;
            return new Promise(function (resolve) {
                try {
                    const rq = db.transaction('files', 'readonly').objectStore('files').get(key);
                    rq.onsuccess = function () { resolve(rq.result || null); };
                    rq.onerror = function () { resolve(null); };
                } catch (_) { resolve(null); }
            });
        },

        put: async function (record) {
            const db = await this.open();
            if (!db) return;
            return new Promise(function (resolve) {
                try {
                    const tx = db.transaction('files', 'readwrite');
                    tx.objectStore('files').put(record);
                    tx.oncomplete = function () { resolve(); };
                    tx.onerror = function () { resolve(); };     // quota exceeded etc.: not fatal
                    tx.onabort = function () { resolve(); };
                } catch (_) { resolve(); }
            });
        }
    };

    // =====================================================================
    // measuring a file: exact length + loudness profile
    // =====================================================================

    function decodeAudio(ctx, buffer) {
        return new Promise(function (resolve, reject) {
            const r = ctx.decodeAudioData(buffer, resolve, reject);   // old Safari only has the callback form
            if (r && typeof r.then === 'function') r.then(resolve, reject);
        });
    }

    // returns { duration (seconds), peaks (Uint8Array, PEAKS_PER_SECOND values per second, sqrt-scaled 0..255) }
    async function analyze(blob) {
        const Ctx = window.OfflineAudioContext || window.webkitOfflineAudioContext;
        if (!Ctx) throw new Error('هذا المتصفح لا يدعم تحليل الصوت.');
        const ctx = new Ctx(1, 1, ANALYSIS_RATE);                  // decoding at 8 kHz keeps memory small
        const decoded = await decodeAudio(ctx, await blob.arrayBuffer());

        const length = decoded.length;
        const channels = [];
        for (let c = 0; c < decoded.numberOfChannels; c++) channels.push(decoded.getChannelData(c));

        const bucket = Math.round(decoded.sampleRate / PEAKS_PER_SECOND);
        const count = Math.ceil(length / bucket);
        const peaks = new Uint8Array(count);
        for (let b = 0; b < count; b++) {
            const from = b * bucket;
            const to = Math.min(length, from + bucket);
            let max = 0;
            for (let c = 0; c < channels.length; c++) {
                const data = channels[c];
                for (let i = from; i < to; i++) {
                    const v = data[i] < 0 ? -data[i] : data[i];
                    if (v > max) max = v;
                }
            }
            peaks[b] = Math.min(255, Math.round(255 * Math.sqrt(Math.min(1, max))));
        }
        return { duration: length / decoded.sampleRate, peaks: peaks };
    }

    // =====================================================================
    // getting the files: server -> browser storage, once
    // =====================================================================

    const Loader = {
        entries: new Map(),     // n -> { n, url, size, version } from the server
        recs: new Map(),        // n -> { n, blob, duration, peaks } ready to play
        inflight: new Map(),
        failed: new Set(),
        onProgress: null,

        setEntries: function (list) {
            const self = this;
            list.forEach(function (e) { self.entries.set(e.n, e); });
        },

        keyOf: function (n) { return Q.sheikhId + '/' + Q.surahId + '/' + n; },

        progress: function () {
            return { done: this.recs.size + this.failed.size, total: this.entries.size };
        },

        ensure: function (n) {
            const self = this;
            if (this.recs.has(n)) return Promise.resolve(this.recs.get(n));
            if (this.failed.has(n)) return Promise.reject(new Error('تعذّر تجهيز الآية ' + n + '.'));
            if (this.inflight.has(n)) return this.inflight.get(n);

            const p = this.load(n).then(function (rec) {
                self.recs.set(n, rec);
                self.inflight.delete(n);
                if (self.onProgress) self.onProgress();
                return rec;
            }, function (err) {
                self.failed.add(n);
                self.inflight.delete(n);
                if (self.onProgress) self.onProgress();
                throw err;
            });
            this.inflight.set(n, p);
            return p;
        },

        load: async function (n) {
            const e = this.entries.get(n);
            if (!e) throw new Error('لا يوجد ملف صوت لهذه الآية.');
            const key = this.keyOf(n);

            const saved = await Store.get(key);
            if (saved && saved.version === e.version && saved.blob && saved.peaks && saved.duration > 0) {
                return { n: n, blob: saved.blob, duration: saved.duration, peaks: new Uint8Array(saved.peaks) };
            }

            const res = await fetch(e.url);
            if (!res.ok) throw new Error('تعذّر تنزيل الآية ' + n + '.');
            let blob = await res.blob();
            if (!blob.type) blob = blob.slice(0, blob.size, AUDIO_TYPE);

            let info;
            try {
                info = await analyze(blob);
            } catch (err) {
                throw new Error('تعذّر قراءة ملف الآية ' + n + '.');
            }
            if (!(info.duration > 0)) throw new Error('ملف الآية ' + n + ' فارغ.');

            Store.put({ key: key, version: e.version, blob: blob, duration: info.duration, peaks: info.peaks });
            return { n: n, blob: blob, duration: info.duration, peaks: info.peaks };
        },

        // downloads the given ayat a few at a time; resolves when every one has finished or failed
        startBackground: function (ns) {
            const self = this;
            const queue = ns.slice();
            const worker = async function () {
                while (queue.length) {
                    const n = queue.shift();
                    try { await self.ensure(n); } catch (_) { /* recorded in failed */ }
                }
            };
            const workers = [];
            for (let i = 0; i < DOWNLOAD_CONCURRENCY; i++) workers.push(worker());
            return Promise.all(workers);
        }
    };

    // Joins ayat into one track: items back to back with exact lengths, plus one combined loudness profile
    function buildTrack(ns) {
        const items = [];
        const segments = [];
        let cursor = 0;
        ns.forEach(function (n) {
            const rec = Loader.recs.get(n);
            items.push({ n: n, blob: rec.blob, start: cursor, end: cursor + rec.duration });
            segments.push({ n: n, start: cursor, end: cursor + rec.duration });
            cursor += rec.duration;
        });

        const peaks = new Uint8Array(Math.ceil(cursor * PEAKS_PER_SECOND) + 2);
        items.forEach(function (it) {
            const rec = Loader.recs.get(it.n);
            const base = Math.round(it.start * PEAKS_PER_SECOND);
            for (let j = 0; j < rec.peaks.length; j++) {
                const g = base + j;
                if (g < peaks.length && rec.peaks[j] > peaks[g]) peaks[g] = rec.peaks[j];
            }
        });

        return { items: items, segments: segments, duration: cursor, peaks: peaks, peaksPerSecond: PEAKS_PER_SECOND };
    }

    // =====================================================================
    // one audio "element" made of several files, with ONE timeline
    // Implements the small part of HTMLMediaElement that the player uses.
    // Two real <audio> elements take turns: while one plays, the other has the next file ready.
    // =====================================================================

    class VirtualAudio extends EventTarget {
        constructor() {
            super();
            this.slots = [new Audio(), new Audio()];
            this.active = 0;
            this.items = [];            // { n, start, end }
            this.urls = [];
            this.idx = -1;              // item playing in the active slot
            this.preIdx = -1;           // item preloaded in the other slot
            this.loop = false;
            this._paused = true;
            this._ended = false;
            this._switching = false;
            this._suppressPlay = false;
            this._advIdx = -1;
            this._timer = 0;
            const self = this;
            this.slots.forEach(function (slot, i) { slot.preload = 'auto'; self._wire(slot, i); });
        }

        get slot() { return this.slots[this.active]; }
        get paused() { return this._paused; }
        get ended() { return this._ended; }
        get duration() { return this.items.length ? this.items[this.items.length - 1].end : NaN; }
        get playbackRate() { return this.slot.playbackRate; }

        get currentTime() {
            if (!this.items.length || this.idx < 0) return 0;
            if (this._ended) return this.duration;
            const it = this.items[this.idx];
            return Math.min(it.end, it.start + Math.max(0, this.slot.currentTime));
        }

        set currentTime(t) {
            if (!this.items.length) return;
            t = clamp(t, 0, this.duration);
            this._ended = false;
            const j = this._indexAt(t);
            this._goTo(j, Math.max(0, t - this.items[j].start));
            if (t >= this.duration - 0.001 && this._paused) this._ended = true;   // like a real element parked at its end
        }

        _emit(type) { this.dispatchEvent(new Event(type)); }

        _wire(slot, i) {
            const self = this;
            const mine = function () { return i === self.active && !self._switching; };
            slot.addEventListener('timeupdate', function () { if (mine()) self._emit('timeupdate'); });
            slot.addEventListener('seeked', function () { if (mine()) self._emit('seeked'); });
            slot.addEventListener('ratechange', function () { if (mine()) self._emit('ratechange'); });
            slot.addEventListener('play', function () {
                if (!mine()) return;
                if (self._suppressPlay) { self._suppressPlay = false; return; }   // internal hand-over between files
                self._emit('play');
            });
            slot.addEventListener('pause', function () {
                if (!mine() || slot.ended) return;
                self._paused = true;
                self._clearTimer();
                self._emit('pause');
            });
            slot.addEventListener('ended', function () {
                if (!mine() || self._paused) return;
                self._advance();
            });
            slot.addEventListener('error', function () {
                if (i === self.active && slot.getAttribute('src')) self._emit('error');
            });
        }

        _indexAt(t) {
            const items = this.items;
            let lo = 0;
            let hi = items.length - 1;
            while (lo < hi) {
                const mid = (lo + hi + 1) >> 1;
                if (items[mid].start <= t) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        _setSrc(slot, j) {
            slot.src = this.urls[j];
        }

        _preload(j) {
            const other = this.slots[1 - this.active];
            if (j >= 0 && j < this.items.length) {
                if (this.preIdx !== j) {
                    other.pause();
                    this._setSrc(other, j);
                    other.currentTime = 0;
                }
                this.preIdx = j;
            } else {
                this.preIdx = -1;
            }
        }

        _goTo(j, offset) {
            const wasPlaying = !this._paused;
            this._switching = true;
            try {
                this._advIdx = -1;
                if (j === this.idx) {
                    this.slot.currentTime = offset;
                } else if (j === this.preIdx) {
                    const old = this.slot;
                    this.active = 1 - this.active;
                    this.idx = j;
                    old.pause();
                    this.slot.currentTime = offset;
                    this._preload(j + 1);
                } else {
                    this.idx = j;
                    this._setSrc(this.slot, j);
                    this.slot.currentTime = offset;
                    this._preload(j + 1);
                }
            } finally {
                this._switching = false;
            }
            if (wasPlaying) this._playSlot();
        }

        // the current file is over: hand over to the next one (or finish / loop)
        _advance() {
            if (this._advIdx === this.idx) return;
            const last = this.items.length - 1;

            if (this.idx >= last) {
                if (this.loop) {
                    this._goTo(0, 0);
                    return;
                }
                this._paused = true;
                this._ended = true;
                this._clearTimer();
                this._emit('timeupdate');
                this._emit('ended');
                return;
            }

            this._advIdx = this.idx;
            if (this.preIdx !== this.idx + 1) {
                this._goTo(this.idx + 1, 0);
                return;
            }

            const old = this.slot;
            this.active = 1 - this.active;
            this.idx += 1;
            const self = this;
            this._suppressPlay = true;
            setTimeout(function () { self._suppressPlay = false; }, 300);
            this._playSlot();
            old.pause();
            this._preload(this.idx + 1);
            this._emit('timeupdate');
        }

        _check() {
            if (this._paused || this._switching || this.idx < 0 || this.idx >= this.items.length - 1) return;
            const it = this.items[this.idx];
            const rate = this.slot.playbackRate || 1;
            // start the next file a hair early so there is no audible gap
            if (this.slot.currentTime >= (it.end - it.start) - 0.03 * rate) this._advance();
        }

        _startTimer() {
            if (this._timer) return;
            const self = this;
            this._timer = setInterval(function () { self._check(); }, 20);
        }

        _clearTimer() {
            clearInterval(this._timer);
            this._timer = 0;
        }

        _playSlot() {
            this._startTimer();
            const p = this.slot.play();
            if (p && typeof p.catch === 'function') p.catch(function () { /* the caller of play() sees its own promise */ });
            return p || Promise.resolve();
        }

        // items: [{ n, blob, start, end }]
        setTrack(items) {
            this.clear();
            this.urls = items.map(function (it) { return URL.createObjectURL(it.blob); });
            this.items = items.map(function (it) { return { n: it.n, start: it.start, end: it.end }; });
            this.idx = 0;
            this.active = 0;
            this.preIdx = -1;
            this._switching = true;
            this._setSrc(this.slot, 0);
            this.slot.currentTime = 0;
            this._switching = false;
            this._preload(1);
            const self = this;
            setTimeout(function () { if (self.items.length) self._emit('loadedmetadata'); }, 0);
        }

        clear() {
            this._switching = true;
            this._clearTimer();
            this.slots.forEach(function (slot) {
                slot.pause();
                slot.removeAttribute('src');
                slot.load();
            });
            this.urls.forEach(function (u) { URL.revokeObjectURL(u); });
            this.urls = [];
            this.items = [];
            this.idx = -1;
            this.preIdx = -1;
            this.active = 0;
            this._paused = true;
            this._ended = false;
            this._advIdx = -1;
            this._switching = false;
        }

        play() {
            if (!this.items.length) return Promise.reject(new Error('no audio'));
            if (this._ended || this.currentTime >= this.duration - 0.001) {
                this._ended = false;
                this._paused = true;
                this.currentTime = 0;                 // start again from the beginning
                this._ended = false;
            }
            this._paused = false;
            return this._playSlot();
        }

        pause() {
            this._paused = true;
            this._clearTimer();
            this.slot.pause();
        }

        setRate(rate) {
            this.slots.forEach(function (slot) {
                slot.defaultPlaybackRate = rate;      // survives choosing another source
                slot.playbackRate = rate;
                slot.preservesPitch = true;           // natural voice at any speed
                slot.mozPreservesPitch = true;
                slot.webkitPreservesPitch = true;
            });
        }
    }

    // =====================================================================
    // the player (used twice: top and bottom)
    // =====================================================================

    class QPlayer {
        constructor(mount, opts) {
            this.opts = Object.assign({ zoom: false, onTime: null, onSegment: null, onPlay: null, onIdle: null }, opts);
            this.audio = new VirtualAudio();

            this.loaded = false;
            this.autoplay = false;
            this.disabled = true;
            this.ready = false;
            this.transport = 'stopped';     // 'stopped' | 'playing' | 'paused'

            this.duration = 0;
            this.segments = [];
            this.segIdx = -1;
            this.peaks = null;
            this.peaksPerSecond = 25;
            this.ref = 1;

            this.pps = 10;
            this.fitMode = true;
            this.dragging = false;
            this.freeScrollUntil = 0;
            this.lastProgScroll = 0;
            this.raf = 0;
            this.drawQueued = false;
            this.colors = null;

            this.build(mount);
            this.bind();
            QPlayer.all.push(this);
            this.syncSpeed();
            this.updateControls();
        }

        // ---------------- DOM ----------------

        build(mount) {
            const wave = el('div', 'qp-wave');
            const frame = el('div', 'qp-wave-frame');
            this.viewport = el('div', 'qp-viewport', { tabindex: '-1' });
            this.inner = el('div', 'qp-inner');
            this.canvas = el('canvas', 'qp-canvas');
            this.overlay = el('div', 'qp-overlay');
            this.overlayText = span('');
            this.overlay.appendChild(this.overlayText);
            this.inner.appendChild(this.canvas);
            this.viewport.appendChild(this.inner);
            frame.appendChild(this.viewport);
            frame.appendChild(this.overlay);
            wave.appendChild(frame);
            mount.appendChild(wave);
            this.ctx = this.canvas.getContext('2d');

            if (this.opts.zoom) {
                const seekRow = el('div', 'qp-row');
                this.seek = el('input', 'qp-seek', {
                    type: 'range', min: '0', max: '1', step: '0.01', value: '0', 'aria-label': 'الموضع في الصوت'
                });
                seekRow.appendChild(this.seek);
                mount.appendChild(seekRow);
            }

            const transport = el('div', 'qp-row qp-transport');
            const group = el('div', 'qp-group');
            this.btnBack = button('\u221210 ث', '', 'رجوع 10 ثوانٍ');
            this.btnPlay = button('تشغيل', 'qp-btn-primary');
            this.btnPause = button('إيقاف مؤقت');
            this.btnResume = button('متابعة');
            this.btnStop = button('إيقاف');
            this.btnFwd = button('+10 ث', '', 'تقديم 10 ثوانٍ');
            [this.btnBack, this.btnPlay, this.btnPause, this.btnResume, this.btnStop, this.btnFwd]
                .forEach(function (b) { group.appendChild(b); });
            transport.appendChild(group);

            if (this.opts.zoom) {
                const zoom = el('div', 'qp-group');
                this.zoomOut = button('\u2212', 'qp-btn-quiet', 'تصغير');
                this.zoomFit = button('ملاءمة', 'qp-btn-quiet', 'عرض الصوت كاملًا');
                this.zoomIn = button('+', 'qp-btn-quiet', 'تكبير');
                [this.zoomOut, this.zoomFit, this.zoomIn].forEach(function (b) { zoom.appendChild(b); });
                transport.appendChild(zoom);
            }

            const time = el('div', 'qp-time');
            this.curText = span('00:00.00');
            this.totalText = span(' / 00:00.00', 'qp-time-total');
            time.appendChild(this.curText);
            time.appendChild(this.totalText);
            transport.appendChild(time);
            mount.appendChild(transport);

            const speedRow = el('div', 'qp-row qp-speedrow');
            speedRow.appendChild(span('السرعة', 'qp-speed-title'));
            this.speedDown = button('\u2212', 'qp-btn-quiet', 'أبطأ');
            this.speedSlider = el('input', 'qp-seek qp-speed-range', {
                type: 'range', min: '0', max: String(SPEED_RES), step: '1', value: String(Speed.toPos(SPEED_DEFAULT)),
                'aria-label': 'سرعة التشغيل'
            });
            this.speedUp = button('+', 'qp-btn-quiet', 'أسرع');
            this.speedValue = button('100%', 'qp-speed-value', 'اضغط للعودة إلى 100%');
            speedRow.appendChild(this.speedDown);
            speedRow.appendChild(this.speedSlider);
            speedRow.appendChild(this.speedUp);
            speedRow.appendChild(this.speedValue);

            this.chips = [];
            const chipBox = el('div', 'qp-chips');
            SPEED_PRESETS.forEach(function (v) {
                const c = el('button', 'qp-chip', { type: 'button', 'aria-pressed': 'false' });
                c.appendChild(span(v + '%'));
                c.dataset.speed = String(v);
                this.chips.push(c);
                chipBox.appendChild(c);
            }, this);
            speedRow.appendChild(chipBox);
            mount.appendChild(speedRow);
        }

        // ---------------- events ----------------

        bind() {
            const self = this;
            const a = this.audio;

            this.btnPlay.addEventListener('click', function () { self.play(); });
            this.btnResume.addEventListener('click', function () { self.play(); });
            this.btnPause.addEventListener('click', function () { a.pause(); });
            this.btnStop.addEventListener('click', function () { self.stop(); });
            this.btnBack.addEventListener('click', function () { self.skip(-SKIP_SECONDS); });
            this.btnFwd.addEventListener('click', function () { self.skip(SKIP_SECONDS); });

            if (this.opts.zoom) {
                this.zoomIn.addEventListener('click', function () { self.setZoom(self.pps * 2); });
                this.zoomOut.addEventListener('click', function () { self.setZoom(self.pps / 2); });
                this.zoomFit.addEventListener('click', function () { self.fit(); });
                this.seek.addEventListener('input', function () {
                    if (self.ready) self.seekTo(parseFloat(self.seek.value), true);
                });
                this.viewport.addEventListener('wheel', function (e) {
                    if (!self.ready) return;
                    self.freeScrollUntil = performance.now() + 3000;
                    if (e.ctrlKey || e.metaKey) {
                        e.preventDefault();
                        const rect = self.canvas.getBoundingClientRect();
                        const x = e.clientX - rect.left;
                        self.setZoom(self.pps * (e.deltaY < 0 ? 1.25 : 0.8), (self.viewport.scrollLeft + x) / self.pps, x);
                    } else if (Math.abs(e.deltaY) > Math.abs(e.deltaX)) {
                        e.preventDefault();
                        self.setScroll(self.viewport.scrollLeft + e.deltaY);
                    }
                }, { passive: false });
            }

            // speed controls
            this.speedSlider.addEventListener('input', function () {
                Speed.set(Speed.fromPos(parseFloat(self.speedSlider.value)));
            });
            // arrow keys on a logarithmic slider move less than one ladder step, so handle them directly
            this.speedSlider.addEventListener('keydown', function (e) {
                let handled = true;
                switch (e.key) {
                    case 'ArrowLeft': case 'ArrowDown': Speed.step(-1); break;
                    case 'ArrowRight': case 'ArrowUp': Speed.step(1); break;
                    case 'PageDown': Speed.step(-5); break;
                    case 'PageUp': Speed.step(5); break;
                    case 'Home': Speed.set(SPEED_MIN); break;
                    case 'End': Speed.set(SPEED_MAX); break;
                    default: handled = false;
                }
                if (handled) e.preventDefault();
            });
            this.speedDown.addEventListener('click', function () { Speed.step(-1); });
            this.speedUp.addEventListener('click', function () { Speed.step(1); });
            this.speedValue.addEventListener('click', function () { Speed.set(SPEED_DEFAULT); });
            this.chips.forEach(function (c) {
                c.addEventListener('click', function () { Speed.set(Number(c.dataset.speed)); });
            });

            // drag on the waveform = seek
            this.canvas.addEventListener('pointerdown', function (e) {
                if (!self.ready || e.button > 0) return;
                self.dragging = true;
                try { self.canvas.setPointerCapture(e.pointerId); } catch (_) { /* not critical */ }
                self.seekFromPointer(e);
                e.preventDefault();
            });
            this.canvas.addEventListener('pointermove', function (e) { if (self.dragging) self.seekFromPointer(e); });
            const endDrag = function () { self.dragging = false; };
            this.canvas.addEventListener('pointerup', endDrag);
            this.canvas.addEventListener('pointercancel', endDrag);
            this.canvas.addEventListener('lostpointercapture', endDrag);

            this.viewport.addEventListener('pointerdown', function (e) {
                if (e.target === self.viewport) self.freeScrollUntil = performance.now() + 5000; // scrollbar drag
            });
            this.viewport.addEventListener('scroll', function () {
                if (Math.abs(self.viewport.scrollLeft - self.lastProgScroll) > 1) {
                    self.freeScrollUntil = performance.now() + 2500;
                    self.lastProgScroll = self.viewport.scrollLeft;
                }
                self.requestDraw();
            });

            if (typeof ResizeObserver !== 'undefined') {
                new ResizeObserver(function () { self.onResize(); }).observe(this.viewport);
            } else {
                window.addEventListener('resize', function () { self.onResize(); });
            }

            // audio element
            a.addEventListener('loadedmetadata', function () {
                if (!self.loaded) return;
                self.ready = true;
                if (isFinite(a.duration) && a.duration > 0) self.duration = a.duration;
                self.syncSpeed();
                self.totalText.textContent = ' / ' + fmt(self.duration);
                if (self.seek) self.seek.max = String(self.duration || 1);
                self.fit();
                self.setOverlay(null);
                self.updateControls();
                self.tick();
                if (self.autoplay) {
                    self.autoplay = false;
                    self.play();
                }
            });
            a.addEventListener('timeupdate', function () { self.tick(); });
            a.addEventListener('seeked', function () { self.tick(); });
            a.addEventListener('play', function () {
                self.transport = 'playing';
                if (self.opts.onPlay) self.opts.onPlay(self);
                self.startLoop();
                self.updateControls();
            });
            a.addEventListener('pause', function () {
                if (a.ended) return;
                if (self.transport === 'playing') self.transport = 'paused';
                self.updateControls();
                self.tick();
            });
            a.addEventListener('ended', function () {
                self.transport = 'stopped';
                self.updateControls();
                self.tick();
                if (self.opts.onIdle) self.opts.onIdle(self);
            });
            a.addEventListener('ratechange', function () {
                const pct = Math.round(a.playbackRate * 100);
                if (pct !== Speed.pct) Speed.set(pct);
            });
            a.addEventListener('error', function () {
                if (!self.loaded) return;
                self.ready = false;
                self.transport = 'stopped';
                self.setOverlay('تعذّر تشغيل هذا الملف الصوتي في المتصفح.');
                self.updateControls();
            });
        }

        // ---------------- state ----------------

        setOverlay(message) {
            if (message) {
                this.overlayText.textContent = message;
                this.overlay.classList.remove('is-hidden');
            } else {
                this.overlay.classList.add('is-hidden');
            }
        }

        setDisabled(message) {
            this.unload();
            this.disabled = true;
            this.setOverlay(message);
            this.updateControls();
        }

        getDuration() {
            if (isFinite(this.audio.duration) && this.audio.duration > 0) return this.audio.duration;
            return this.duration;
        }

        unload() {
            this.audio.clear();
            this.loaded = false;
            this.autoplay = false;
            this.ready = false;
            this.transport = 'stopped';
            this.duration = 0;
            this.segments = [];
            this.segIdx = -1;
            this.peaks = null;
            this.viewport.scrollLeft = 0;
            this.curText.textContent = '00:00.00';
            this.totalText.textContent = ' / 00:00.00';
            if (this.seek) { this.seek.value = '0'; this.seek.style.setProperty('--p', '0%'); }
            this.requestDraw();
            this.updateControls();
            if (this.opts.onIdle) this.opts.onIdle(this);
        }

        // data: { items, segments, duration, peaks, peaksPerSecond }  (see buildTrack)
        load(data, options) {
            this.unload();
            this.disabled = false;
            this.loaded = true;
            this.duration = data.duration || 0;
            this.segments = data.segments || [];
            this.peaks = data.peaks;
            this.peaksPerSecond = data.peaksPerSecond > 0 ? data.peaksPerSecond : PEAKS_PER_SECOND;
            this.ref = computeReference(data.peaks);
            this.autoplay = !!(options && options.autoplay);
            this.setOverlay(null);
            this.audio.setTrack(data.items);
            this.requestDraw();
            this.updateControls();
        }

        updateControls() {
            const on = this.ready && !this.disabled;
            this.btnBack.disabled = !on;
            this.btnFwd.disabled = !on;
            this.btnPlay.disabled = !(on && this.transport === 'stopped');
            this.btnPause.disabled = !(on && this.transport === 'playing');
            this.btnResume.disabled = !(on && this.transport === 'paused');
            this.btnStop.disabled = !(on && this.transport !== 'stopped');
            if (this.seek) this.seek.disabled = !on;
            if (this.opts.zoom) {
                this.zoomIn.disabled = !on;
                this.zoomOut.disabled = !on;
                this.zoomFit.disabled = !on;
            }
        }

        // ---------------- transport ----------------

        play() {
            const p = this.audio.play();
            if (p && typeof p.catch === 'function') {
                p.catch(function (err) {
                    if (err && err.name === 'AbortError') return; // a new source replaced this one
                    toast('تعذّر بدء التشغيل. اضغط زر التشغيل.', true);
                });
            }
        }

        pause() { this.audio.pause(); }

        stop() {
            this.audio.pause();
            this.transport = 'stopped';
            this.seekTo(0, true);
            this.updateControls();
            if (this.opts.onIdle) this.opts.onIdle(this);
        }

        toggle() {
            if (!this.ready) return;
            if (this.transport === 'playing') {
                this.audio.pause();
            } else {
                if (this.transport === 'stopped') this.audio.currentTime = 0;
                this.play();
            }
        }

        skip(delta) {
            if (!this.ready) return;
            this.seekTo(this.audio.currentTime + delta, true);
        }

        seekTo(t, keepVisible) {
            const dur = this.getDuration();
            this.audio.currentTime = clamp(t, 0, dur > 0 ? dur : Math.max(t, 0));
            this.tick();
            if (keepVisible) this.follow(true);
        }

        seekFromPointer(e) {
            const rect = this.canvas.getBoundingClientRect();
            const x = clamp(e.clientX - rect.left, 0, rect.width);
            this.seekTo((this.viewport.scrollLeft + x) / this.pps, false);
            const edge = 28;
            if (x < edge) this.setScroll(this.viewport.scrollLeft - (edge - x) * 1.5);
            else if (x > rect.width - edge) this.setScroll(this.viewport.scrollLeft + (x - (rect.width - edge)) * 1.5);
        }

        syncSpeed() {
            const rate = Speed.pct / 100;
            this.audio.setRate(rate);

            const pos = Speed.toPos(Speed.pct);
            this.speedSlider.value = String(pos);
            this.speedSlider.style.setProperty('--p', (pos / SPEED_RES * 100) + '%');
            this.speedSlider.setAttribute('aria-valuetext', Speed.pct + '%');
            setLabel(this.speedValue, Speed.pct + '%');
            this.speedDown.disabled = Speed.pct <= SPEED_MIN;
            this.speedUp.disabled = Speed.pct >= SPEED_MAX;
            this.chips.forEach(function (c) {
                c.setAttribute('aria-pressed', String(Number(c.dataset.speed) === Speed.pct));
            });
        }

        // ---------------- time ----------------

        segmentIndexAt(t) {
            const segs = this.segments;
            if (!segs.length) return -1;
            let i = this.segIdx >= 0 ? Math.min(this.segIdx, segs.length - 1) : 0;
            while (i > 0 && t < segs[i].start) i--;
            while (i < segs.length - 1 && t >= segs[i].end) i++;
            return i;
        }

        tick() {
            const t = this.audio.currentTime;
            const dur = this.getDuration();
            this.curText.textContent = fmt(t);
            if (this.seek && dur > 0) {
                this.seek.value = String(t);
                this.seek.style.setProperty('--p', (clamp(t / dur, 0, 1) * 100) + '%');
            }

            if (this.ready) {
                const idx = this.segmentIndexAt(t);
                if (idx !== this.segIdx) {
                    this.segIdx = idx;
                    if (idx >= 0 && this.opts.onSegment) this.opts.onSegment(this.segments[idx].n, this);
                }
            }

            if (this.opts.onTime) this.opts.onTime(t, this);
            this.follow(false);
            this.requestDraw();
        }

        startLoop() {
            if (this.raf) return;
            const self = this;
            const frame = function () {
                self.raf = 0;
                if (self.transport === 'playing') {
                    self.tick();
                    self.raf = requestAnimationFrame(frame);
                }
            };
            this.raf = requestAnimationFrame(frame);
        }

        // ---------------- layout / zoom ----------------

        onResize() {
            if (!this.ready) { this.requestDraw(); return; }
            if (this.fitMode) this.fit(); else this.layout();
        }

        layout() {
            const dur = this.getDuration();
            const vw = this.viewport.clientWidth;
            this.inner.style.width = Math.max(vw, Math.ceil(dur * this.pps - 0.01)) + 'px';
            this.requestDraw();
        }

        fit() {
            const vw = this.viewport.clientWidth;
            const dur = this.getDuration();
            if (!vw || !dur) return;
            this.pps = Math.max(MIN_PPS, vw / dur);
            this.fitMode = true;
            this.layout();
            this.setScroll(0);
        }

        setScroll(x) {
            this.viewport.scrollLeft = Math.max(0, x);
            this.lastProgScroll = this.viewport.scrollLeft;
        }

        setZoom(pps, anchorTime, anchorX) {
            const dur = this.getDuration();
            if (!dur) return;
            const vw = this.viewport.clientWidth;
            if (anchorTime === undefined) {
                const cx = this.audio.currentTime * this.pps - this.viewport.scrollLeft;
                anchorTime = cx >= 0 && cx <= vw ? this.audio.currentTime : (this.viewport.scrollLeft + vw / 2) / this.pps;
            }
            if (anchorX === undefined) anchorX = anchorTime * this.pps - this.viewport.scrollLeft;
            this.pps = clamp(pps, MIN_PPS, MAX_PPS);
            this.fitMode = false;
            this.layout();
            this.setScroll(anchorTime * this.pps - anchorX);
            this.requestDraw();
        }

        follow(force) {
            if (this.dragging) return;
            if (!force && (this.transport !== 'playing' || performance.now() < this.freeScrollUntil)) return;
            const vw = this.viewport.clientWidth;
            const x = this.audio.currentTime * this.pps - this.viewport.scrollLeft;
            if (x < 0 || x > vw * 0.9) {
                this.setScroll(this.audio.currentTime * this.pps - vw * 0.25);
            }
        }

        // ---------------- drawing ----------------

        readColors() {
            const cs = getComputedStyle(this.viewport);
            const g = function (name, fallback) { return cs.getPropertyValue(name).trim() || fallback; };
            this.colors = {
                played: g('--qp-played', '#0d6e6a'),
                rest: g('--qp-rest', '#9fb0ae'),
                silence: g('--qp-silence', '#c1cccb'),
                playhead: g('--qp-playhead', '#1d2a2a'),
                ruler: g('--qp-ruler', '#8a9a98'),
                rulerText: g('--qp-ruler-text', '#51615f'),
                seg: g('--qp-seg', 'rgba(13,110,106,0.38)')
            };
        }

        requestDraw() {
            if (this.drawQueued) return;
            this.drawQueued = true;
            const self = this;
            requestAnimationFrame(function () { self.draw(); });
        }

        peakRange(ta, tb) {
            const peaks = this.peaks;
            if (!peaks) return -1;
            const pp = this.peaksPerSecond;
            const i0 = Math.max(0, Math.floor(ta * pp));
            let i1 = Math.max(i0 + 1, Math.ceil(tb * pp));
            if (i0 >= peaks.length) return 0;
            if (i1 > peaks.length) i1 = peaks.length;
            let m = 0;
            for (let i = i0; i < i1; i++) if (peaks[i] > m) m = peaks[i];
            return m;
        }

        drawRuler(vw, scroll, pps) {
            const ctx = this.ctx;
            const c = this.colors;
            ctx.font = '11px "Segoe UI", Tahoma, system-ui, sans-serif';
            ctx.textBaseline = 'top';
            ctx.textAlign = 'left';
            let step = RULER_STEPS[RULER_STEPS.length - 1];
            for (let i = 0; i < RULER_STEPS.length; i++) {
                if (RULER_STEPS[i] * pps >= 80) { step = RULER_STEPS[i]; break; }
            }
            ctx.fillStyle = c.ruler;
            ctx.globalAlpha = 0.5;
            ctx.fillRect(0, RULER_H - 1, vw, 1);
            ctx.globalAlpha = 1;
            const first = Math.floor(scroll / pps / step);
            const last = Math.ceil((scroll + vw) / pps / step);
            for (let k = first; k <= last; k++) {
                const t = k * step;
                if (t < 0) continue;
                const x = Math.round(t * pps - scroll) + 0.5;
                ctx.fillStyle = c.ruler;
                ctx.fillRect(x, RULER_H - 6, 1, 6);
                ctx.fillStyle = c.rulerText;
                ctx.fillText(fmt(t, step < 1), x + 4, 3);
            }
        }

        draw() {
            this.drawQueued = false;
            const vw = this.viewport.clientWidth;
            const H = this.viewport.clientHeight;
            if (!vw || !H) return;
            if (!this.colors) this.readColors();

            const ctx = this.ctx;
            const c = this.colors;
            const dpr = window.devicePixelRatio || 1;
            const cw = Math.round(vw * dpr);
            const ch = Math.round(H * dpr);
            if (this.canvas.width !== cw || this.canvas.height !== ch) {
                this.canvas.width = cw;
                this.canvas.height = ch;
                this.canvas.style.width = vw + 'px';
                this.canvas.style.height = H + 'px';
            }
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            ctx.clearRect(0, 0, vw, H);

            const dur = this.getDuration();
            if (!dur) return;

            const pps = this.pps;
            const scroll = Math.floor(this.viewport.scrollLeft);
            const waveH = H - RULER_H;
            const mid = RULER_H + waveH / 2;
            const curX = this.audio.currentTime * pps - scroll;
            const endX = dur * pps - scroll;
            const segs = this.segments;

            // the ayah that is playing right now
            if (this.ready && segs.length > 1 && this.segIdx >= 0 && segs[this.segIdx]) {
                const s = segs[this.segIdx];
                const x0 = Math.max(0, s.start * pps - scroll);
                const x1 = Math.min(vw, s.end * pps - scroll);
                if (x1 > x0) {
                    ctx.globalAlpha = 0.14;
                    ctx.fillStyle = c.played;
                    ctx.fillRect(x0, RULER_H, x1 - x0, waveH);
                    ctx.globalAlpha = 1;
                }
            }

            this.drawRuler(vw, scroll, pps);

            // bars on an absolute pixel grid so they do not shimmer while scrolling
            const firstX = -(scroll % STEP);
            for (let x = firstX; x < vw; x += STEP) {
                const absX = scroll + x;
                const ta = absX / pps;
                if (ta >= dur) break;
                const v = this.peakRange(ta, (absX + STEP) / pps);
                const played = x + 1 < curX;
                const level = v < 0 ? 0 : v / 255;

                if (v < 0 || level < SILENCE) {
                    ctx.globalAlpha = played ? 0.6 : 1;
                    ctx.fillStyle = played ? c.played : c.silence;
                    ctx.fillRect(x, mid - 0.75, STEP, 1.5);
                    ctx.globalAlpha = 1;
                } else {
                    const h = Math.max(3, Math.min(1, level / this.ref) * (waveH - 10));
                    ctx.fillStyle = played ? c.played : c.rest;
                    ctx.fillRect(x, mid - h / 2, BAR_W, h);
                }
            }

            // ayah boundaries and numbers
            if (segs.length > 1) {
                ctx.font = '600 10px "Segoe UI", Tahoma, system-ui, sans-serif';
                ctx.textBaseline = 'top';
                ctx.textAlign = 'left';
                for (let i = 0; i < segs.length; i++) {
                    const s = segs[i];
                    const x = Math.round(s.start * pps - scroll);
                    const w = (s.end - s.start) * pps;
                    if (x + w < 0) continue;
                    if (x > vw) break;
                    if (w >= 5) {
                        ctx.fillStyle = c.seg;
                        ctx.fillRect(x, RULER_H, 1, waveH);
                    }
                    if (w >= 26) {
                        ctx.fillStyle = c.rulerText;
                        ctx.fillText(String(s.n), x + 3, RULER_H + 3);
                    }
                }
            }

            if (endX >= 0 && endX <= vw) {
                ctx.fillStyle = c.ruler;
                ctx.fillRect(Math.round(endX) - 1, RULER_H, 2, waveH);
            }

            // playhead
            if (this.ready && curX >= -2 && curX <= vw + 2) {
                ctx.fillStyle = c.playhead;
                ctx.fillRect(curX - 1, 0, 2, H);
                ctx.beginPath();
                ctx.moveTo(curX - 6, 0);
                ctx.lineTo(curX + 6, 0);
                ctx.lineTo(curX, 8);
                ctx.closePath();
                ctx.fill();
            }
        }
    }
    QPlayer.all = [];

    function computeReference(arr) {
        const hist = new Uint32Array(256);
        const minByte = Math.ceil(SILENCE * 255);
        let n = 0;
        for (let i = 0; i < arr.length; i++) {
            const v = arr[i];
            if (v >= minByte) { hist[v]++; n++; }
        }
        if (n < 10) return 1;
        const target = n * 0.98;   // ignore the loudest 2%
        let acc = 0;
        for (let v = minByte; v < 256; v++) {
            acc += hist[v];
            if (acc >= target) return Math.max(MIN_REF, v / 255);
        }
        return 1;
    }

    // =====================================================================
    // splitting the page text into ayat
    // =====================================================================

    const PREAMBLE_TAGS = /^(A|H[1-6]|P|IMG|BR|HR|DIV|SECTION|FIGURE|SCRIPT|STYLE|LINK|META)$/;

    function isBlankText(node) {
        return node.nodeType === 3 && !/\S/.test(node.nodeValue);
    }

    function isPreamble(node) {
        if (node.nodeType === 8) return true;                       // comment
        if (isBlankText(node)) return true;
        return node.nodeType === 1 && PREAMBLE_TAGS.test(node.tagName);
    }

    // returns the ayah <span>s in page order, or null when this page has no (N) markers
    function splitAyat() {
        const labels = Array.prototype.filter.call(app.querySelectorAll('label'), function (l) {
            return /^\(\s*\d+\s*\)$/.test(l.textContent.trim());
        });
        if (!labels.length) return null;

        const host = labels[0].parentNode;
        const labelSet = new Set(labels.filter(function (l) { return l.parentNode === host; }));
        const nodes = Array.prototype.slice.call(host.childNodes);

        let i = 0;
        while (i < nodes.length && isPreamble(nodes[i])) i++;   // heading + basmalah image

        const spans = [];
        let group = [];
        for (; i < nodes.length; i++) {
            const node = nodes[i];
            group.push(node);
            if (!labelSet.has(node)) continue;

            const n = parseInt(node.textContent.replace(/\D/g, ''), 10);
            while (group.length && isBlankText(group[0])) group.shift();
            if (group.length && n > 0) {
                const s = el('span', 'qp-ayah', { role: 'button', tabindex: '0' });
                s.dataset.n = String(n);
                host.insertBefore(s, group[0]);
                group.forEach(function (g) { s.appendChild(g); });
                spans.push(s);
            }
            group = [];
        }
        // anything after the last marker (closing du'a, line breaks) stays outside the ayat
        return spans;
    }

    // =====================================================================
    // building the panels
    // =====================================================================

    const ayahSpans = splitAyat();
    if (!ayahSpans || !ayahSpans.length) return;

    // top panel
    const topPanel = el('section', 'qp-panel qp-top', { 'aria-label': 'مشغل الآيات' });
    const bar = el('div', 'qp-top-bar');
    const fromInput = el('input', 'qp-num', { type: 'number', min: '1', step: '1', inputmode: 'numeric', 'aria-label': 'من آية', placeholder: '1' });
    const toInput = el('input', 'qp-num', { type: 'number', min: '1', step: '1', inputmode: 'numeric', 'aria-label': 'إلى آية', placeholder: '1' });
    const runBtn = button('تنفيذ', 'qp-btn-primary', 'تشغيل الآيات من الرقم الأول إلى الثاني');
    const repeatBtn = button('\u27F3 تكرار', '', 'إعادة تشغيل نفس المدى باستمرار');
    repeatBtn.setAttribute('aria-pressed', 'false');
    const collapseBtn = button('طيّ', 'qp-btn-quiet', 'إخفاء الموجة والسرعة');
    const statusText = el('span', 'qp-status');
    bar.appendChild(span('من آية', 'qp-field-label'));
    bar.appendChild(fromInput);
    bar.appendChild(span('إلى آية', 'qp-field-label'));
    bar.appendChild(toInput);
    bar.appendChild(runBtn);
    bar.appendChild(repeatBtn);
    bar.appendChild(statusText);
    bar.appendChild(collapseBtn);
    topPanel.appendChild(bar);
    const topMount = el('div');
    topPanel.appendChild(topMount);

    // bottom panel
    const bottomPanel = el('section', 'qp-panel qp-bottom', { 'aria-label': 'مشغل السورة كاملة' });
    bottomPanel.appendChild(span('السورة كاملة', 'qp-title'));
    const bottomMount = el('div');
    bottomPanel.appendChild(bottomMount);

    app.insertBefore(topPanel, app.firstChild);
    app.appendChild(bottomPanel);

    function pauseOthers(player) {
        QPlayer.all.forEach(function (p) { if (p !== player) p.pause(); });
    }

    const top = new QPlayer(topMount, {
        zoom: false,
        onPlay: pauseOthers,
        onSegment: function (n) { setNow(n, true); },
        onIdle: function () { setNow(null); }
    });
    const bottom = new QPlayer(bottomMount, { zoom: true, onPlay: pauseOthers });

    try {
        if (localStorage.getItem(COLLAPSE_KEY) === '1') topPanel.classList.add('is-collapsed');
    } catch (_) { /* ignore */ }
    collapseBtn.addEventListener('click', function () {
        const collapsed = topPanel.classList.toggle('is-collapsed');
        setLabel(collapseBtn, collapsed ? 'فتح' : 'طيّ');
        try { localStorage.setItem(COLLAPSE_KEY, collapsed ? '1' : '0'); } catch (_) { /* ignore */ }
    });
    if (topPanel.classList.contains('is-collapsed')) setLabel(collapseBtn, 'فتح');

    // =====================================================================
    // the only server call: which files exist (everything else happens in the browser)
    // =====================================================================

    async function api(name, params) {
        const qs = new URLSearchParams(Object.assign({ sheikhId: Q.sheikhId, surahId: Q.surahId }, params || {}));
        const res = await fetch('/api/quranaudio/' + name + '?' + qs.toString());
        let data = {};
        try { data = await res.json(); } catch (_) { /* no body */ }
        if (!res.ok) throw new Error(data.error || ('حدث خطأ (' + res.status + ')'));
        return data;
    }

    // =====================================================================
    // selection, highlighting, fields
    // =====================================================================

    function setStatus(message, isError) {
        statusText.textContent = message;
        statusText.classList.toggle('is-error', !!isError);
    }

    function rangeLabel(from, to) {
        return from === to ? 'الآية ' + from : 'الآيات ' + from + ' \u2013 ' + to;
    }

    function paintSelection() {
        const sel = Q.selection;
        ayahSpans.forEach(function (s) {
            const n = Number(s.dataset.n);
            s.classList.toggle('qp-sel', !!sel && n >= sel.from && n <= sel.to);
        });
    }

    let nowN = null;
    function setNow(n, scroll) {
        if (n === nowN) return;
        nowN = n;
        let target = null;
        ayahSpans.forEach(function (s) {
            const is = n !== null && Number(s.dataset.n) === n;
            s.classList.toggle('qp-now', is);
            if (is) target = s;
        });
        if (scroll && target && top.transport === 'playing') {
            const r = target.getBoundingClientRect();
            const headroom = topPanel.offsetHeight + 16;
            if (r.top < headroom || r.bottom > window.innerHeight - 24) {
                target.scrollIntoView({ block: 'center', behavior: 'smooth' });
            }
        }
    }

    function updateFields(from, to) {
        fromInput.value = String(from);
        toInput.value = String(to);
    }

    async function selectRange(from, to, autoplay) {
        Q.selection = { from: from, to: to };
        paintSelection();
        updateFields(from, to);
        setNow(null);

        const token = ++Q.token;
        top.unload();

        const wanted = [];
        const skipped = [];
        for (let n = from; n <= to; n++) {
            if (Q.has.has(n) && !Loader.failed.has(n)) wanted.push(n); else skipped.push(n);
        }
        if (!wanted.length) {
            const message = 'لا توجد ملفات صوت في هذا المدى.';
            top.setOverlay(message);
            setStatus(message, true);
            toast(message, true);
            return;
        }

        if (wanted.some(function (n) { return !Loader.recs.has(n); })) {
            top.setOverlay('جارٍ نقل الصوت إلى جهازك\u2026');
            setStatus(rangeLabel(from, to) + ' \u2013 جارٍ التنزيل\u2026');
        }

        // anything not on this device yet is fetched now, ahead of the background download
        await Promise.all(wanted.map(function (n) { return Loader.ensure(n).catch(function () { return null; }); }));
        if (token !== Q.token) return;

        const ready = wanted.filter(function (n) { return Loader.recs.has(n); });
        wanted.forEach(function (n) { if (!Loader.recs.has(n)) skipped.push(n); });
        if (!ready.length) {
            const message = 'تعذّر تجهيز الصوت لهذا المدى.';
            top.setOverlay(message);
            setStatus(message, true);
            toast(message, true);
            return;
        }

        top.audio.loop = Q.repeat;
        top.load(buildTrack(ready), { autoplay: autoplay });

        let message = rangeLabel(from, to);
        if (skipped.length) {
            skipped.sort(function (a, b) { return a - b; });
            message += ' \u2013 لا توجد ملفات للآيات: ' + skipped.join('، ');
            toast('لا توجد ملفات صوت للآيات: ' + skipped.join('، '));
        }
        setStatus(message);
    }

    function runFields() {
        if (!Q.ready) { toast(Q.disabledMessage, true); return; }
        let from = parseInt(fromInput.value, 10);
        let to = parseInt(toInput.value, 10);
        if (isNaN(from) && isNaN(to)) { setStatus('اكتب رقم الآية الأولى والأخيرة.', true); return; }
        if (isNaN(from)) from = to;
        if (isNaN(to)) to = from;
        if (from > to) { const t = from; from = to; to = t; }
        if (from < Q.min || to > Q.max) {
            setStatus('الآيات المتاحة من ' + Q.min + ' إلى ' + Q.max + '.', true);
            return;
        }
        Q.anchor = from;
        selectRange(from, to, true);
    }

    function handleAyahClick(n, shift) {
        if (!Q.ready) { toast(Q.disabledMessage, true); return; }
        if (!Q.has.has(n)) { toast('لا يوجد ملف صوت لهذه الآية.', true); return; }

        if (shift) {
            const anchor = Q.anchor === null ? n : Q.anchor;
            selectRange(Math.min(anchor, n), Math.max(anchor, n), true);
            return;
        }

        Q.anchor = n;
        const sel = Q.selection;
        if (sel && sel.from === n && sel.to === n && top.ready) {
            top.toggle();            // same ayah again: pause / resume
            return;
        }
        selectRange(n, n, true);
    }

    function hasTextSelection() {
        const sel = window.getSelection ? window.getSelection() : null;
        return !!sel && !sel.isCollapsed && sel.toString().trim().length > 0;
    }

    app.addEventListener('mousedown', function (e) {
        // Shift+click would otherwise extend the browser's text selection
        if (e.shiftKey && e.target.closest && e.target.closest('.qp-ayah')) e.preventDefault();
    });

    app.addEventListener('click', function (e) {
        const s = e.target.closest ? e.target.closest('.qp-ayah') : null;
        if (!s) return;
        if (e.detail > 1) return;          // double click = selecting a word
        if (hasTextSelection()) return;    // the reader is selecting text to copy
        handleAyahClick(Number(s.dataset.n), e.shiftKey);
    });

    app.addEventListener('keydown', function (e) {
        if (e.key !== 'Enter' && e.key !== ' ') return;
        const s = e.target.classList && e.target.classList.contains('qp-ayah') ? e.target : null;
        if (!s) return;
        e.preventDefault();
        handleAyahClick(Number(s.dataset.n), e.shiftKey);
    });

    runBtn.addEventListener('click', runFields);
    [fromInput, toInput].forEach(function (input) {
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); runFields(); }
        });
    });

    repeatBtn.addEventListener('click', function () {
        Q.repeat = !Q.repeat;
        repeatBtn.setAttribute('aria-pressed', String(Q.repeat));
        top.audio.loop = Q.repeat;
        // pressing it after the reading has finished starts it again
        if (Q.repeat && top.ready && top.transport === 'stopped') {
            top.audio.currentTime = 0;
            top.play();
        }
    });

    // =====================================================================
    // start-up
    // =====================================================================

    function disableAll(message) {
        Q.ready = false;
        Q.disabledMessage = message;
        fromInput.disabled = true;
        toInput.disabled = true;
        runBtn.disabled = true;
        repeatBtn.disabled = true;
        top.setDisabled(message);
        bottom.setDisabled(message);
        setStatus(message, true);
    }

    // progress of the background download, shown while the whole-surah player is being prepared
    let bottomDone = false;
    function showProgress() {
        const p = Loader.progress();
        if (!bottomDone) bottom.setOverlay('جارٍ نقل صوت السورة إلى جهازك\u2026 ' + p.done + ' / ' + p.total);
        if (!Q.selection && !bottomDone) {
            setStatus('الآيات المتاحة من ' + Q.min + ' إلى ' + Q.max + ' \u2013 جارٍ تنزيل الصوت ' + p.done + ' / ' + p.total);
        }
    }

    async function loadBottom() {
        showProgress();
        await Loader.startBackground(Array.from(Q.has).sort(function (a, b) { return a - b; }));
        bottomDone = true;

        const ns = Array.from(Q.has).sort(function (a, b) { return a - b; }).filter(function (n) { return Loader.recs.has(n); });
        if (!ns.length) {
            bottom.setDisabled('تعذّر تجهيز صوت السورة.');
            return;
        }
        bottom.load(buildTrack(ns), { autoplay: false });

        if (Loader.failed.size) toast('تعذّر تجهيز بعض الآيات: ' + Array.from(Loader.failed).sort(function (a, b) { return a - b; }).join('، '), true);
        if (!Q.selection) setStatus('الآيات المتاحة من ' + Q.min + ' إلى ' + Q.max + '.');
    }

    async function start() {
        try {
            const stored = parseFloat(localStorage.getItem(SPEED_KEY));
            if (isFinite(stored)) Speed.set(stored, false);
        } catch (_) { /* ignore */ }

        top.setOverlay('\u2026');
        bottom.setOverlay('\u2026');

        if (!Q.sheikhId) {
            disableAll('اختر قارئًا لتفعيل الصوت.');
            return;
        }

        let files;
        try {
            files = await api('files');
        } catch (err) {
            disableAll(err.message || 'تعذّر الاتصال بالخادم.');
            return;
        }

        if (!files.found || !files.ayat || !files.ayat.length) {
            disableAll(files.message || 'لا توجد ملفات صوت لهذه السورة عند هذا القارئ.');
            return;
        }

        Loader.setEntries(files.ayat);
        Loader.onProgress = showProgress;

        const numbers = files.ayat.map(function (a) { return a.n; });
        Q.has = new Set(numbers);
        Q.min = numbers[0];
        Q.max = numbers[numbers.length - 1];
        Q.ready = true;
        fromInput.min = toInput.min = String(Q.min);
        fromInput.max = toInput.max = String(Q.max);
        fromInput.placeholder = String(Q.min);
        toInput.placeholder = String(Q.max);

        ayahSpans.forEach(function (s) {
            if (!Q.has.has(Number(s.dataset.n))) s.classList.add('qp-noaudio');
        });

        top.setOverlay('اضغط على آية، أو اكتب مدى الآيات ثم "تنفيذ".');
        setStatus('الآيات المتاحة من ' + Q.min + ' إلى ' + Q.max + '.');

        // as soon as the page is open the whole surah is moved to this device (or read from the saved copy)
        loadBottom();
    }

    start();

    if (window.__QP_TEST__) {
        window.__QP_TEST__.api = { Q: Q, top: top, bottom: bottom, Speed: Speed, ayahSpans: ayahSpans, Loader: Loader, Store: Store };
    }
})();
