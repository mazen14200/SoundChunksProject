/*
 * Audio studio: waveform player + CUT workflow.
 *
 * - The browser plays the local file (object URL), so seeking is instant.
 * - The same file is uploaded to the server, which owns the project state and does the cutting.
 * - The waveform comes from GET /api/audio/waveform/{projectName} (computed once by FFmpeg and cached),
 *   so even multi-hour recordings are drawn without decoding the whole file in the browser.
 * - CUT sends only { projectName, currentPosition }. The position is read from audio.currentTime
 *   at the very first line of the click handler.
 */
(function () {
    'use strict';

    // ---------- settings you may want to change ----------
    const SKIP_SECONDS = 10;                 // the -10 s / +10 s buttons
    const STOP_RETURNS_TO_LAST_CUT = false;  // false: Stop rewinds to 0 (standard player). true: Stop jumps to the previous cut.
    const DEFAULT_PPS = 40;                  // initial zoom: pixels per second
    const MIN_PPS = 0.02;
    const MAX_PPS = 400;
    const SILENCE = 0.07;                    // below this (sqrt-scaled, roughly -46 dBFS) a bar becomes a flat line
    const MIN_REF = 0.3;                     // never amplify a very quiet recording beyond this reference level

    // ---------- drawing constants ----------
    const BAR_W = 2;
    const STEP = 3;                          // bar width + gap, in CSS pixels
    const RULER_H = 22;
    const EPS = 0.001;
    const RULER_STEPS = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];

    const $ = (id) => document.getElementById(id);
    const el = {
        app: $('app'), fileInput: $('fileInput'), fileName: $('fileName'), fileSub: $('fileSub'),
        progress: $('uploadProgress'), curTime: $('curTime'), totalTime: $('totalTime'),
        statChunk: $('statChunk'), statPrevCut: $('statPrevCut'), statPending: $('statPending'),
        viewport: $('viewport'), inner: $('inner'), canvas: $('wfCanvas'), overlay: $('wfOverlay'),
        seek: $('seek'), audio: $('audio'), toast: $('toast'),
        btnBack: $('btnBack'), btnFwd: $('btnFwd'), btnPlay: $('btnPlay'), btnPause: $('btnPause'),
        btnResume: $('btnResume'), btnStop: $('btnStop'), btnCut: $('btnCut'),
        zoomIn: $('zoomIn'), zoomOut: $('zoomOut'), zoomFit: $('zoomFit')
    };
    if (!el.app || !el.canvas) return;

    const audio = el.audio;
    const ctx = el.canvas.getContext('2d');

    const S = {
        token: 0,              // increments per chosen file; stale async results are ignored
        xhr: null,
        objectUrl: null,
        project: null,         // { projectName, sourceFileName, lastCutPosition, duration, nextChunkNumber }
        ready: false,          // audio metadata loaded and playable
        uploading: false,
        cutting: false,
        transport: 'stopped',  // 'stopped' | 'playing' | 'paused'
        pps: DEFAULT_PPS,
        peaks: null,           // Uint8Array, sqrt-scaled 0..255
        peaksPerSecond: 25,
        ref: 1,
        dragging: false,
        resumed: false,
        raf: 0,
        drawQueued: false,
        freeScrollUntil: 0,
        lastProgScroll: 0,
        toastTimer: 0
    };

    let colors = null;

    // =====================================================================
    // helpers
    // =====================================================================

    function clamp(v, lo, hi) { return Math.min(hi, Math.max(lo, v)); }

    function fmt(t, withCentiseconds) {
        if (withCentiseconds === undefined) withCentiseconds = true;
        if (!isFinite(t) || t < 0) t = 0;
        const total = Math.floor(t * 100 + 1e-6);
        const cs = total % 100;
        const secs = Math.floor(total / 100);
        const s = secs % 60;
        const m = Math.floor(secs / 60) % 60;
        const h = Math.floor(secs / 3600);
        const pad = (n) => String(n).padStart(2, '0');
        const base = h > 0 ? h + ':' + pad(m) + ':' + pad(s) : pad(m) + ':' + pad(s);
        return withCentiseconds ? base + '.' + pad(cs) : base;
    }

    function getDuration() {
        if (isFinite(audio.duration) && audio.duration > 0) return audio.duration;
        return S.project && S.project.duration > 0 ? S.project.duration : 0;
    }

    function toast(message, isError) {
        clearTimeout(S.toastTimer);
        el.toast.textContent = message;
        el.toast.classList.toggle('is-error', !!isError);
        el.toast.classList.add('is-visible');
        S.toastTimer = setTimeout(function () { el.toast.classList.remove('is-visible'); }, isError ? 5200 : 3200);
    }

    function setOverlay(message) {
        if (message) {
            el.overlay.textContent = message;
            el.overlay.classList.remove('is-hidden');
        } else {
            el.overlay.classList.add('is-hidden');
        }
    }

    function setSub(message, isError) {
        el.fileSub.textContent = message;
        el.fileSub.classList.toggle('is-error', !!isError);
    }

    // =====================================================================
    // controls state
    // =====================================================================

    function updateControls() {
        const ready = S.ready;
        el.btnBack.disabled = !ready;
        el.btnFwd.disabled = !ready;
        el.seek.disabled = !ready;
        el.zoomIn.disabled = !ready;
        el.zoomOut.disabled = !ready;
        el.zoomFit.disabled = !ready;
        el.btnPlay.disabled = !(ready && S.transport === 'stopped');
        el.btnPause.disabled = !(ready && S.transport === 'playing');
        el.btnResume.disabled = !(ready && S.transport === 'paused');
        el.btnStop.disabled = !(ready && S.transport !== 'stopped');
        el.btnCut.disabled = !(ready && S.project && !S.cutting && !S.uploading);
        el.btnCut.classList.toggle('is-busy', S.cutting);
    }

    function updateStats() {
        const p = S.project;
        el.statChunk.textContent = p ? String(p.nextChunkNumber) : '\u2013';
        el.statPrevCut.textContent = p ? fmt(p.lastCutPosition) : '\u2013';
        const pending = p ? audio.currentTime - p.lastCutPosition : 0;
        el.statPending.textContent = p && pending > EPS ? fmt(pending) : '\u2013';
    }

    function updateDurationUi() {
        const dur = getDuration();
        el.totalTime.textContent = fmt(dur);
        el.seek.max = String(dur > 0 ? dur : 1);
        layout();
    }

    // =====================================================================
    // time / seeking
    // =====================================================================

    function onTime() {
        const t = audio.currentTime;
        const dur = getDuration();
        el.curTime.textContent = fmt(t);
        if (dur > 0) {
            el.seek.value = String(t);
            el.seek.style.setProperty('--p', (clamp(t / dur, 0, 1) * 100) + '%');
        }
        updateStats();
        followPlayhead(false);
        requestDraw();
    }

    function seekTo(t, keepVisible) {
        const dur = getDuration();
        audio.currentTime = clamp(t, 0, dur > 0 ? dur : Math.max(t, 0));
        onTime();
        if (keepVisible) followPlayhead(true);
    }

    function skip(delta) {
        if (!S.ready) return;
        seekTo(audio.currentTime + delta, true);
    }

    function setScroll(x) {
        el.viewport.scrollLeft = Math.max(0, x);
        S.lastProgScroll = el.viewport.scrollLeft;
    }

    function followPlayhead(force) {
        if (S.dragging) return;
        if (!force && (S.transport !== 'playing' || performance.now() < S.freeScrollUntil)) return;
        const vw = el.viewport.clientWidth;
        const x = audio.currentTime * S.pps - el.viewport.scrollLeft;
        if (x < 0 || x > vw * 0.9) {
            setScroll(audio.currentTime * S.pps - vw * 0.25);
        }
    }

    function startLoop() {
        if (S.raf) return;
        const tick = function () {
            S.raf = 0;
            if (S.transport === 'playing') {
                onTime();
                S.raf = requestAnimationFrame(tick);
            }
        };
        S.raf = requestAnimationFrame(tick);
    }

    // =====================================================================
    // layout + zoom
    // =====================================================================

    function layout() {
        const dur = getDuration();
        const vw = el.viewport.clientWidth;
        // the small subtraction stops float noise (e.g. 1000.0000000000001) from adding a useless 1px scrollbar
        el.inner.style.width = Math.max(vw, Math.ceil(dur * S.pps - 0.01)) + 'px';
        requestDraw();
    }

    function setZoom(pps, anchorTime, anchorX) {
        const dur = getDuration();
        if (!dur) return;
        const vw = el.viewport.clientWidth;
        if (anchorTime === undefined) {
            const cx = audio.currentTime * S.pps - el.viewport.scrollLeft;
            anchorTime = cx >= 0 && cx <= vw ? audio.currentTime : (el.viewport.scrollLeft + vw / 2) / S.pps;
        }
        if (anchorX === undefined) anchorX = anchorTime * S.pps - el.viewport.scrollLeft;
        S.pps = clamp(pps, MIN_PPS, MAX_PPS);
        layout();
        setScroll(anchorTime * S.pps - anchorX);
        requestDraw();
    }

    // =====================================================================
    // waveform data
    // =====================================================================

    function computeReference(arr) {
        const hist = new Uint32Array(256);
        const minByte = Math.ceil(SILENCE * 255);
        let n = 0;
        for (let i = 0; i < arr.length; i++) {
            const v = arr[i];
            if (v >= minByte) { hist[v]++; n++; }
        }
        if (n < 10) return 1;
        const target = n * 0.98;   // ignore the loudest 2% so a single clap does not flatten everything else
        let acc = 0;
        for (let v = minByte; v < 256; v++) {
            acc += hist[v];
            if (acc >= target) return Math.max(MIN_REF, v / 255);
        }
        return 1;
    }

    function applyPeaks(data) {
        const bin = atob(data.peaks || '');
        const arr = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
        S.peaks = arr;
        S.peaksPerSecond = data.peaksPerSecond > 0 ? data.peaksPerSecond : 25;
        S.ref = computeReference(arr);
    }

    // max peak between two times; -1 when there is no data at all
    function peakRange(ta, tb) {
        const peaks = S.peaks;
        if (!peaks) return -1;
        const pp = S.peaksPerSecond;
        let i0 = Math.max(0, Math.floor(ta * pp));
        let i1 = Math.max(i0 + 1, Math.ceil(tb * pp));
        if (i0 >= peaks.length) return 0;
        if (i1 > peaks.length) i1 = peaks.length;
        let m = 0;
        for (let i = i0; i < i1; i++) if (peaks[i] > m) m = peaks[i];
        return m;
    }

    async function loadWaveform(projectName, token) {
        setOverlay('Building the waveform\u2026 (long recordings take a moment the first time)');
        try {
            const res = await fetch('/api/audio/waveform/' + encodeURIComponent(projectName));
            const data = await res.json().catch(function () { return {}; });
            if (token !== S.token) return;
            if (!res.ok) throw new Error(data.error || 'Waveform unavailable');
            applyPeaks(data);
            setOverlay(null);
        } catch (err) {
            if (token !== S.token) return;
            S.peaks = null;
            setOverlay('The waveform could not be built (' + (err.message || 'unknown error') + '). Playback and cutting still work.');
        }
        requestDraw();
    }

    // =====================================================================
    // canvas rendering
    // =====================================================================

    function readColors() {
        const cs = getComputedStyle(el.app);
        const g = function (name) { return cs.getPropertyValue(name).trim(); };
        colors = {
            played: g('--ac-played') || '#0d6e6a',
            rest: g('--ac-rest') || '#a3b0b8',
            silence: g('--ac-silence') || '#c3ccd1',
            playhead: g('--ac-playhead') || '#16212b',
            cut: g('--ac-cut') || '#d98e04',
            cutInk: g('--ac-cut-ink') || '#1b1400',
            shade: g('--ac-shade') || 'rgba(13,110,106,0.13)',
            ruler: g('--ac-ruler') || '#8c99a3',
            rulerText: g('--ac-ruler-text') || '#5d6a74'
        };
    }

    function requestDraw() {
        if (S.drawQueued) return;
        S.drawQueued = true;
        requestAnimationFrame(draw);
    }

    function drawRuler(vw, scroll, pps) {
        ctx.font = '11px "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif';
        ctx.textBaseline = 'top';
        let step = RULER_STEPS[RULER_STEPS.length - 1];
        for (let i = 0; i < RULER_STEPS.length; i++) {
            if (RULER_STEPS[i] * pps >= 90) { step = RULER_STEPS[i]; break; }
        }
        const first = Math.floor(scroll / pps / step);
        const last = Math.ceil((scroll + vw) / pps / step);
        ctx.fillStyle = colors.ruler;
        ctx.globalAlpha = 0.5;
        ctx.fillRect(0, RULER_H - 1, vw, 1);
        ctx.globalAlpha = 1;
        for (let k = first; k <= last; k++) {
            const t = k * step;
            if (t < 0) continue;
            const x = Math.round(t * pps - scroll) + 0.5;
            ctx.fillStyle = colors.ruler;
            ctx.fillRect(x, RULER_H - 7, 1, 7);
            ctx.fillStyle = colors.rulerText;
            ctx.fillText(fmt(t, step < 1), x + 4, 4);
        }
    }

    function draw() {
        S.drawQueued = false;
        const vw = el.viewport.clientWidth;
        const H = el.viewport.clientHeight;
        if (!vw || !H) return;
        if (!colors) readColors();

        const dpr = window.devicePixelRatio || 1;
        const cw = Math.round(vw * dpr);
        const ch = Math.round(H * dpr);
        if (el.canvas.width !== cw || el.canvas.height !== ch) {
            el.canvas.width = cw;
            el.canvas.height = ch;
            el.canvas.style.width = vw + 'px';
            el.canvas.style.height = H + 'px';
        }
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, vw, H);

        const dur = getDuration();
        const pps = S.pps;
        const scroll = Math.floor(el.viewport.scrollLeft);
        const waveH = H - RULER_H;
        const mid = RULER_H + waveH / 2;
        const curX = audio.currentTime * pps - scroll;
        const lastCut = S.project ? S.project.lastCutPosition : 0;
        const cutX = lastCut * pps - scroll;
        const endX = dur * pps - scroll;

        // the part of the audio that the next CUT would save
        if (S.ready && S.project && curX > cutX) {
            const x0 = Math.max(cutX, 0);
            const x1 = Math.min(curX, vw);
            if (x1 > x0) {
                ctx.fillStyle = colors.shade;
                ctx.fillRect(x0, RULER_H, x1 - x0, waveH);
            }
        }

        drawRuler(vw, scroll, pps);

        // bars are aligned to an absolute pixel grid so they do not shimmer while scrolling
        const firstX = -(scroll % STEP);
        for (let x = firstX; x < vw; x += STEP) {
            const absX = scroll + x;
            const ta = absX / pps;
            if (dur > 0 && ta >= dur) break;
            const v = peakRange(ta, (absX + STEP) / pps);
            const played = x + 1 < curX;
            const level = v < 0 ? 0 : v / 255;

            if (v < 0 || level < SILENCE) {
                // silence (or no data yet): a thin flat line
                ctx.globalAlpha = played ? 0.6 : 1;
                ctx.fillStyle = played ? colors.played : colors.silence;
                ctx.fillRect(x, mid - 0.75, STEP, 1.5);
                ctx.globalAlpha = 1;
            } else {
                const h = Math.max(3, Math.min(1, level / S.ref) * (waveH - 10));
                ctx.fillStyle = played ? colors.played : colors.rest;
                ctx.fillRect(x, mid - h / 2, BAR_W, h);
            }
        }

        // end of the audio
        if (dur > 0 && endX >= 0 && endX <= vw) {
            ctx.fillStyle = colors.ruler;
            ctx.fillRect(Math.round(endX) - 1, RULER_H, 2, waveH);
        }

        // previous cut marker
        if (S.project && lastCut > 0 && cutX >= -40 && cutX <= vw + 2) {
            ctx.fillStyle = colors.cut;
            ctx.fillRect(cutX - 1, RULER_H, 2, waveH);
            ctx.fillRect(cutX + 1, RULER_H, 30, 15);
            ctx.fillStyle = colors.cutInk;
            ctx.font = '600 10px "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif';
            ctx.textBaseline = 'middle';
            ctx.fillText('cut', cutX + 7, RULER_H + 8);
        }

        // playhead
        if (S.ready && curX >= -2 && curX <= vw + 2) {
            ctx.fillStyle = colors.playhead;
            ctx.fillRect(curX - 1, 0, 2, H);
            ctx.beginPath();
            ctx.moveTo(curX - 6, 0);
            ctx.lineTo(curX + 6, 0);
            ctx.lineTo(curX, 8);
            ctx.closePath();
            ctx.fill();
        }
    }

    // =====================================================================
    // pointer interaction on the waveform
    // =====================================================================

    function seekFromPointer(e) {
        const rect = el.canvas.getBoundingClientRect();
        const x = clamp(e.clientX - rect.left, 0, rect.width);
        seekTo((el.viewport.scrollLeft + x) / S.pps, false);

        // nudge the view when dragging close to an edge
        const edge = 28;
        if (x < edge) setScroll(el.viewport.scrollLeft - (edge - x) * 1.5);
        else if (x > rect.width - edge) setScroll(el.viewport.scrollLeft + (x - (rect.width - edge)) * 1.5);
    }

    el.canvas.addEventListener('pointerdown', function (e) {
        if (!S.ready || e.button > 0) return;
        S.dragging = true;
        try { el.canvas.setPointerCapture(e.pointerId); } catch (_) { /* not critical */ }
        seekFromPointer(e);
        e.preventDefault();
    });
    el.canvas.addEventListener('pointermove', function (e) {
        if (S.dragging) seekFromPointer(e);
    });
    function endDrag() { S.dragging = false; }
    el.canvas.addEventListener('pointerup', endDrag);
    el.canvas.addEventListener('pointercancel', endDrag);
    el.canvas.addEventListener('lostpointercapture', endDrag);

    el.viewport.addEventListener('pointerdown', function (e) {
        // a press on the scrollbar itself: do not fight the user by auto-following
        if (e.target === el.viewport) S.freeScrollUntil = performance.now() + 5000;
    });

    el.viewport.addEventListener('scroll', function () {
        if (Math.abs(el.viewport.scrollLeft - S.lastProgScroll) > 1) {
            S.freeScrollUntil = performance.now() + 2500;
            S.lastProgScroll = el.viewport.scrollLeft;
        }
        requestDraw();
    });

    el.viewport.addEventListener('wheel', function (e) {
        if (!S.ready) return;
        S.freeScrollUntil = performance.now() + 3000;
        if (e.ctrlKey || e.metaKey) {
            e.preventDefault();
            const rect = el.canvas.getBoundingClientRect();
            const x = e.clientX - rect.left;
            setZoom(S.pps * (e.deltaY < 0 ? 1.25 : 0.8), (el.viewport.scrollLeft + x) / S.pps, x);
        } else if (Math.abs(e.deltaY) > Math.abs(e.deltaX)) {
            // plain mouse wheel moves the view sideways
            e.preventDefault();
            setScroll(el.viewport.scrollLeft + e.deltaY);
        }
    }, { passive: false });

    // =====================================================================
    // transport buttons
    // =====================================================================

    function play() {
        const p = audio.play();
        if (p && typeof p.catch === 'function') {
            p.catch(function (err) { toast('Could not start playback: ' + (err && err.message ? err.message : err), true); });
        }
    }

    function stop() {
        audio.pause();
        S.transport = 'stopped';
        const target = STOP_RETURNS_TO_LAST_CUT && S.project ? S.project.lastCutPosition : 0;
        seekTo(target, true);   // Stop never touches the saved previous cut
        updateControls();
    }

    el.btnPlay.addEventListener('click', play);
    el.btnResume.addEventListener('click', play);
    el.btnPause.addEventListener('click', function () { audio.pause(); });
    el.btnStop.addEventListener('click', stop);
    el.btnBack.addEventListener('click', function () { skip(-SKIP_SECONDS); });
    el.btnFwd.addEventListener('click', function () { skip(SKIP_SECONDS); });
    el.zoomIn.addEventListener('click', function () { setZoom(S.pps * 2); });
    el.zoomOut.addEventListener('click', function () { setZoom(S.pps / 2); });
    el.zoomFit.addEventListener('click', function () {
        const dur = getDuration();
        if (dur > 0) setZoom(el.viewport.clientWidth / dur, 0, 0);
    });
    el.seek.addEventListener('input', function () {
        if (S.ready) seekTo(parseFloat(el.seek.value), true);
    });

    // =====================================================================
    // audio element events
    // =====================================================================

    function maybeResumePosition() {
        if (S.resumed || !S.ready || !S.project) return;
        S.resumed = true;
        if (S.project.lastCutPosition > 0) seekTo(S.project.lastCutPosition, true);
    }

    audio.addEventListener('loadedmetadata', function () {
        S.ready = true;
        updateDurationUi();
        onTime();
        updateControls();
        maybeResumePosition();
    });
    audio.addEventListener('durationchange', function () { if (S.objectUrl) updateDurationUi(); });
    audio.addEventListener('timeupdate', onTime);
    audio.addEventListener('seeked', onTime);
    audio.addEventListener('play', function () {
        S.transport = 'playing';
        startLoop();
        updateControls();
    });
    audio.addEventListener('pause', function () {
        if (audio.ended) return;                       // the 'ended' handler takes care of it
        if (S.transport === 'playing') S.transport = 'paused';
        updateControls();
        onTime();
    });
    audio.addEventListener('ended', function () {
        S.transport = 'stopped';
        updateControls();
        onTime();
    });
    audio.addEventListener('error', function () {
        if (!S.objectUrl) return;
        S.ready = false;
        S.transport = 'stopped';
        setOverlay('This browser cannot play this audio format, so the position cannot be previewed. Try an MP3, M4A or WAV copy.');
        setSub('Playback is not supported for this file in this browser.', true);
        updateControls();
    });

    // =====================================================================
    // choosing a file + upload
    // =====================================================================

    function uploadFile(file, token) {
        return new Promise(function (resolve, reject) {
            const xhr = new XMLHttpRequest();
            S.xhr = xhr;
            xhr.open('POST', '/api/audio/upload');
            xhr.upload.onprogress = function (e) {
                if (e.lengthComputable && token === S.token) el.progress.value = e.loaded / e.total;
            };
            xhr.onload = function () {
                let data = {};
                try { data = JSON.parse(xhr.responseText); } catch (_) { /* keep empty */ }
                if (xhr.status >= 200 && xhr.status < 300) resolve(data);
                else reject(new Error(data.error || 'Upload failed (' + xhr.status + ')'));
            };
            xhr.onerror = function () { reject(new Error('Network error while uploading the file')); };
            xhr.onabort = function () { reject(new Error('aborted')); };
            const form = new FormData();
            form.append('file', file);
            xhr.send(form);
        });
    }

    async function onFileChosen(file) {
        if (!file) return;
        const token = ++S.token;

        if (S.xhr) { try { S.xhr.abort(); } catch (_) { /* ignore */ } S.xhr = null; }
        audio.pause();
        if (S.objectUrl) URL.revokeObjectURL(S.objectUrl);

        S.project = null;
        S.peaks = null;
        S.ref = 1;
        S.ready = false;
        S.resumed = false;
        S.transport = 'stopped';
        S.pps = DEFAULT_PPS;
        S.uploading = true;
        el.viewport.scrollLeft = 0;
        S.lastProgScroll = 0;

        S.objectUrl = URL.createObjectURL(file);
        audio.src = S.objectUrl;
        audio.load();

        el.fileName.textContent = file.name;
        setSub('Uploading\u2026', false);
        el.progress.value = 0;
        el.progress.hidden = false;
        setOverlay('Uploading the file\u2026');
        el.curTime.textContent = fmt(0);
        el.totalTime.textContent = fmt(0);
        updateStats();
        updateControls();
        requestDraw();

        try {
            const data = await uploadFile(file, token);
            if (token !== S.token) return;

            const p = data.project || {};
            S.project = {
                projectName: p.projectName,
                sourceFileName: p.sourceFileName,
                lastCutPosition: Number(p.lastCutPosition) || 0,
                duration: Number(p.duration) || 0,
                nextChunkNumber: Number(p.nextChunkNumber) || 1
            };
            S.uploading = false;
            el.progress.hidden = true;
            setSub(S.project.lastCutPosition > 0
                ? 'Project "' + S.project.projectName + '" resumes after ' + fmt(S.project.lastCutPosition) + '.'
                : 'New project "' + S.project.projectName + '".', false);

            updateDurationUi();
            updateStats();
            updateControls();
            maybeResumePosition();
            await loadWaveform(S.project.projectName, token);
        } catch (err) {
            if (token !== S.token || (err && err.message === 'aborted')) return;
            S.uploading = false;
            el.progress.hidden = true;
            setSub(err.message || 'Upload failed.', true);
            setOverlay('The file could not be uploaded, so cutting is unavailable. You can still listen to it here.');
            updateControls();
            toast(err.message || 'Upload failed.', true);
        }
    }

    el.fileInput.addEventListener('change', function () {
        const file = el.fileInput.files && el.fileInput.files[0];
        if (file) onFileChosen(file);
        el.fileInput.value = '';   // choosing the same file again must fire 'change' again
    });

    // =====================================================================
    // CUT
    // =====================================================================

    async function onCut() {
        const position = audio.currentTime;   // exact playback position at the moment of the click
        if (S.cutting || !S.project || !S.ready || S.uploading) return;

        const project = S.project;
        const previous = project.lastCutPosition;
        if (!(position > previous + EPS)) {
            toast('Move the position past the previous cut (' + fmt(previous) + ') and try again.', true);
            return;
        }

        const token = S.token;
        S.cutting = true;       // locks the button immediately; there is no confirmation dialog
        updateControls();

        try {
            const res = await fetch('/api/audio/cut', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ projectName: project.projectName, currentPosition: position })
            });
            const data = await res.json().catch(function () { return {}; });
            if (!res.ok) throw new Error(data.error || 'The cut failed (' + res.status + ').');

            project.lastCutPosition = Number(data.lastCutPosition);
            project.nextChunkNumber = Number(data.nextChunkNumber);
            if (token === S.token) {
                updateStats();
                requestDraw();
            }
            toast('Chunk ' + data.chunkNumber + ' saved: ' + fmt(previous) + ' to ' + fmt(project.lastCutPosition) + '.', false);
        } catch (err) {
            toast(err.message || 'The cut failed.', true);
        } finally {
            S.cutting = false;
            updateControls();
        }
    }

    el.btnCut.addEventListener('click', onCut);

    // =====================================================================
    // boot
    // =====================================================================

    if (window.matchMedia) {
        const mq = window.matchMedia('(prefers-color-scheme: dark)');
        const onScheme = function () { readColors(); requestDraw(); };
        if (mq.addEventListener) mq.addEventListener('change', onScheme);
    }

    if (typeof ResizeObserver !== 'undefined') {
        new ResizeObserver(function () { layout(); }).observe(el.viewport);
    } else {
        window.addEventListener('resize', layout);
    }

    updateControls();
    layout();
})();
