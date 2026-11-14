// Zoom and pan for the lightbox image. Transform is translate(x, y) scale(s) around the image centre.
const MIN = 1, MAX = 8, STEP = 1.25;

// Touch gestures. Mouse and pen keep the handlers above them; only pointerType 'touch' takes these paths.
const SLOP = 10;            // px a finger may wander before a touch counts as a move
const HOLD_MS = 400;        // a still finger this long on the side of a video is a hold
const TAP_MS = 300;         // longer touches aren't taps
const DOUBLE_MS = 300;      // second tap within this is a double tap
const STREAK_MS = 700;      // further taps on the same side within this add another seek
const SEEK_S = 5;           // matches Lightbox.razor's SeekSeconds (J / L)
const CONTROLS_PX = 56;     // the native video playback bar at the bottom: no gestures there
const REWIND_TICK_MS = 125; // rewinding steps back 2 x this every tick, i.e. 2x speed in reverse

/** What a finished swipe does, or null when it was too short and slow. `dx` or `dy` is 0 for the axis not locked. */
export function swipeAction(dx, dy, ms, width, height) {
    const dist = Math.max(Math.abs(dx), Math.abs(dy));
    const flick = dist > 30 && dist / Math.max(ms, 1) > 0.5; // a quick flick counts even when short
    if (Math.abs(dx) >= Math.abs(dy))
        return Math.abs(dx) > width * 0.2 || flick ? (dx < 0 ? 'next' : 'prev') : null;
    return Math.abs(dy) > height * 0.15 || flick ? (dy > 0 ? 'close' : 'details') : null;
}

/** 'left' or 'right' third of a video, or null for the middle. */
export function videoSide(x, rect) {
    const t = (x - rect.left) / rect.width;
    return t < 1 / 3 ? 'left' : t > 2 / 3 ? 'right' : null;
}

/** The part of an object-fit: contain box its picture covers; the rest is empty bars (presentation fills the screen). */
export function pictureRect(rect, naturalWidth, naturalHeight) {
    if (!naturalWidth || !naturalHeight) return rect; // not loaded yet: treat the whole box as picture
    const s = Math.min(rect.width / naturalWidth, rect.height / naturalHeight);
    const width = naturalWidth * s, height = naturalHeight * s;
    return { left: rect.left + (rect.width - width) / 2, top: rect.top + (rect.height - height) / 2, width, height };
}

const within = (e, r) => e.clientX >= r.left && e.clientX <= r.left + r.width && e.clientY >= r.top && e.clientY <= r.top + r.height;
const pictureOf = el => pictureRect(el.getBoundingClientRect(), el.naturalWidth ?? el.videoWidth, el.naturalHeight ?? el.videoHeight);

export function create(stage, dotnet) {
    let scale = 1, x = 0, y = 0;
    let drag = null;
    const image = () => stage.querySelector('img.zoomable');
    const video = () => stage.querySelector('video');

    function apply() {
        const img = image();
        if (!img) return;
        img.style.transform = scale === 1 ? '' : `translate(${x}px, ${y}px) scale(${scale})`;
        stage.classList.toggle('zoomed', scale > 1);
    }

    // Zoom keeping the image point under (cx, cy) fixed on screen.
    // A point p (from the untransformed centre) shows at centre + t + s*p, so t' = t - (screen offset) * (s'/s - 1).
    function zoomAt(factor, cx, cy) {
        const img = image();
        if (!img) return;
        const next = Math.min(MAX, Math.max(MIN, scale * factor));
        if (next === MIN) {
            reset();
            return;
        }
        const r = img.getBoundingClientRect();
        const k = next / scale;
        x -= (cx - (r.left + r.width / 2)) * (k - 1);
        y -= (cy - (r.top + r.height / 2)) * (k - 1);
        scale = next;
        apply();
    }

    function reset() {
        scale = 1; x = 0; y = 0;
        apply();
    }

    function centre() {
        const r = stage.getBoundingClientRect();
        return [r.left + r.width / 2, r.top + r.height / 2];
    }

    // Jump within the current video; clamped to its length.
    function seek(seconds) {
        const v = video();
        if (!v || !Number.isFinite(v.duration)) return;
        v.currentTime = Math.min(v.duration, Math.max(0, v.currentTime + seconds));
    }

    // A gesture must not end as a click: beside the media it changes item (onBlankClick), on a video it toggles play.
    // A mouse click follows pointerup at once; a touch click can lag, so touch keeps the guard a little longer.
    function swallowClick(ms = 0) {
        const swallow = ev => { ev.stopPropagation(); ev.preventDefault(); };
        stage.addEventListener('click', swallow, { capture: true, once: true });
        setTimeout(() => stage.removeEventListener('click', swallow, { capture: true }), ms);
    }

    // ── Mouse: wheel zoom, double-click zoom, drag to pan (drag also pans a zoomed image by touch) ──

    const onWheel = e => {
        if (!image()) return;
        e.preventDefault();
        // WebKitGTK also turns a quick finger drag into scroll events (still coasting after the finger lifts):
        // those must not zoom under a swipe. Sideways scrolling doesn't zoom either.
        if (touches.size > 0 || e.timeStamp - lastTouchEnd < 1000 || e.deltaY === 0) return;
        zoomAt(e.deltaY < 0 ? STEP : 1 / STEP, e.clientX, e.clientY);
    };
    const onDoubleClick = e => {
        const img = e.target.closest('img.zoomable');
        if (!img || !within(e, pictureOf(img))) return; // a double click on the bars is two clicks to move on
        if (e.timeStamp - lastTouchEnd < 800) return; // a double tap already zoomed (tap below)
        if (scale > 1) reset(); else zoomAt(2.5, e.clientX, e.clientY);
    };
    const onDown = e => {
        if (scale === 1 || !e.target.closest('img.zoomable')) return;
        drag = { id: e.pointerId, lastX: e.clientX, lastY: e.clientY, moved: false };
        stage.setPointerCapture(e.pointerId);
    };
    const onMove = e => {
        if (!drag || e.pointerId !== drag.id) return;
        x += e.clientX - drag.lastX;
        y += e.clientY - drag.lastY;
        drag.moved ||= Math.abs(e.clientX - drag.lastX) + Math.abs(e.clientY - drag.lastY) > 2;
        drag.lastX = e.clientX;
        drag.lastY = e.clientY;
        apply();
    };
    const onUp = e => {
        if (!drag || e.pointerId !== drag.id) return;
        if (drag.moved) swallowClick(e.pointerType === 'touch' ? 400 : 0);
        drag = null;
    };

    // ── Touch: swipe, pinch, double tap, and on videos double tap / hold on the sides ──

    const touches = new Map(); // pointerId -> { x, y } of every finger down
    let g = null;              // the one-finger gesture: mode 'pending' -> 'swipe' | 'hold' | 'done'
    let pinch = null;
    let lastTap = null, streak = null, lastTouchEnd = -Infinity, badgeTimer;

    function flash(text, side, sticky = false) {
        const b = stage.querySelector('.lb-gesture');
        if (!b) return;
        b.textContent = text;
        b.dataset.side = side ?? '';
        b.classList.add('show');
        clearTimeout(badgeTimer);
        if (!sticky) badgeTimer = setTimeout(() => b.classList.remove('show'), 600);
    }
    const hideBadge = () => stage.querySelector('.lb-gesture')?.classList.remove('show');

    function settle(el) {
        if (!el) return;
        el.style.transition = el.style.transform = el.style.opacity = '';
    }

    function startHold() {
        const v = g?.video;
        if (!v || g.mode !== 'pending') return;
        g.mode = 'hold';
        g.wasPaused = v.paused;
        if (g.side === 'right') {
            v.playbackRate = 2;
            v.play().catch(() => { });
            flash('2× »', 'right', true);
        } else {
            // Browsers can't play backwards: pause and step back. Skipping a tick while a seek is still running
            // keeps the pace at 2x; how smooth it looks depends on the file's keyframes.
            v.pause();
            let target = v.currentTime;
            g.rewind = setInterval(() => {
                target = Math.max(0, target - 2 * REWIND_TICK_MS / 1000);
                if (!v.seeking) v.currentTime = target;
            }, REWIND_TICK_MS);
            flash('« 2×', 'left', true);
        }
    }

    function endHold(gesture) {
        const v = gesture.video;
        clearInterval(gesture.rewind);
        v.playbackRate = 1;
        if (gesture.wasPaused) v.pause(); else v.play().catch(() => { });
        hideBadge();
    }

    // Drop the one-finger gesture, undoing what it did on screen (a second finger landed, or the viewer closed).
    function abandon() {
        if (!g) return;
        clearTimeout(g.holdTimer);
        if (g.mode === 'hold') endHold(g);
        if (g.mode === 'swipe') settle(g.el);
        g = null;
    }

    function tap(e, gesture) {
        const now = e.timeStamp;
        const double = lastTap !== null && now - lastTap.t < DOUBLE_MS
            && Math.hypot(e.clientX - lastTap.x, e.clientY - lastTap.y) < 40;
        lastTap = double ? null : { t: now, x: e.clientX, y: e.clientY };

        if (gesture.side) {
            swallowClick(400); // a tap on the side of a video must not toggle play
            const inStreak = streak?.side === gesture.side && now - streak.t < STREAK_MS;
            if (!inStreak && !double) return;
            if (!inStreak) streak = { side: gesture.side, total: 0 };
            streak.t = now;
            streak.total += SEEK_S;
            const sign = gesture.side === 'right' ? 1 : -1;
            seek(sign * SEEK_S);
            flash(sign > 0 ? `${streak.total} s »` : `« ${streak.total} s`, gesture.side);
        } else if (double && e.target.closest('img.zoomable') && within(e, pictureOf(image()))) {
            if (scale > 1) reset(); else zoomAt(2.5, e.clientX, e.clientY);
        }
    }

    function startPinch() {
        abandon();
        drag = null; // the second finger turns a pan into a pinch
        const [a, b] = touches.values();
        pinch = { d0: Math.hypot(a.x - b.x, a.y - b.y) || 1, s0: scale, mx: (a.x + b.x) / 2, my: (a.y + b.y) / 2 };
        const img = image();
        if (img) img.style.transition = 'none'; // follow the fingers without lag
    }

    function movePinch() {
        if (touches.size < 2 || !image()) return;
        const [a, b] = touches.values();
        const mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
        zoomAt(pinch.s0 * Math.hypot(a.x - b.x, a.y - b.y) / pinch.d0 / scale, mx, my);
        if (scale > 1) { // both fingers moving together pan
            x += mx - pinch.mx;
            y += my - pinch.my;
            apply();
        }
        pinch.mx = mx;
        pinch.my = my;
    }

    const onTouchDown = e => {
        if (e.pointerType !== 'touch') return;
        touches.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (touches.size === 2) startPinch();
        if (touches.size > 1 || e.target.closest('button, input, a, .present-bar, .lb-fallback')) return;
        const v = e.target.closest('video');
        if (v && e.clientY > v.getBoundingClientRect().bottom - CONTROLS_PX) return; // leave the native playback bar alone
        // Seek and hold zones are thirds of the picture; a tap on the bars beside it moves on (onBlankClick).
        const pic = v && pictureOf(v);
        g = { id: e.pointerId, x0: e.clientX, y0: e.clientY, t0: e.timeStamp, mode: 'pending', video: v,
              side: pic && within(e, pic) ? videoSide(e.clientX, pic) : null };
        if (g.side) g.holdTimer = setTimeout(startHold, HOLD_MS);
    };

    const onTouchMove = e => {
        if (e.pointerType !== 'touch' || !touches.has(e.pointerId)) return;
        touches.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (pinch) { movePinch(); return; }
        if (!g || e.pointerId !== g.id || g.mode === 'hold' || g.mode === 'done') return;
        const dx = e.clientX - g.x0, dy = e.clientY - g.y0;
        if (g.mode === 'pending') {
            if (Math.hypot(dx, dy) < SLOP) return;
            clearTimeout(g.holdTimer);
            if (scale > 1) { g.mode = 'done'; return; } // zoomed in, the finger pans (onDown) instead
            g.mode = 'swipe';
            g.axis = Math.abs(dx) > Math.abs(dy) ? 'x' : 'y';
            g.el = image() ?? video();
            if (g.el) g.el.style.transition = 'none';
        }
        if (!g.el) return;
        g.el.style.transform = g.axis === 'x' ? `translateX(${dx}px)` : `translateY(${dy}px)`;
        if (g.axis === 'y' && dy > 0) g.el.style.opacity = String(Math.max(0.3, 1 - dy / stage.clientHeight));
    };

    const onTouchUp = e => {
        if (e.pointerType !== 'touch' || !touches.delete(e.pointerId)) return;
        lastTouchEnd = e.timeStamp;
        if (pinch) {
            if (touches.size > 0) return;
            pinch = null;
            settle(image()); // drops the inline 'none' so later zoom steps animate again
            apply();
            swallowClick(400);
            return;
        }
        if (!g || e.pointerId !== g.id) return;
        const gesture = g;
        g = null;
        clearTimeout(gesture.holdTimer);
        const done = e.type === 'pointerup';

        if (gesture.mode === 'hold') {
            endHold(gesture);
            swallowClick(400);
        } else if (gesture.mode === 'swipe') {
            swallowClick(400);
            const dx = gesture.axis === 'x' ? e.clientX - gesture.x0 : 0;
            const dy = gesture.axis === 'y' ? e.clientY - gesture.y0 : 0;
            const action = done && swipeAction(dx, dy, e.timeStamp - gesture.t0, stage.clientWidth, stage.clientHeight);
            if (!action) { settle(gesture.el); return; }
            // On success the element leaves with the old item; otherwise (first or last item, details) it snaps back.
            dotnet.invokeMethodAsync('OnSwipe', action)
                .then(moved => { if (!moved) settle(gesture.el); }, () => settle(gesture.el));
        } else if (gesture.mode === 'pending' && done && e.timeStamp - gesture.t0 < TAP_MS) {
            tap(e, gesture);
        }
    };

    // A click (or tap) on the blank area beside the picture works like the arrows: left half back, right half on.
    // Blank is the stage itself or the bars inside the media's own box (in presentation it fills the screen).
    // defaultPrevented: swallowClick's guard; it runs on this same element, so it can't stop this listener.
    const onBlankClick = e => {
        if (e.defaultPrevented) return;
        const media = e.target.closest('img.zoomable, video');
        if (media ? within(e, pictureOf(media)) : e.target !== stage) return;
        if (media?.tagName === 'VIDEO') {
            if (e.clientY > media.getBoundingClientRect().bottom - CONTROLS_PX) return; // its playback bar
            e.preventDefault(); // a click beside the video must not also toggle play
        }
        const r = stage.getBoundingClientRect();
        dotnet.invokeMethodAsync('OnSwipe', e.clientX < r.left + r.width / 2 ? 'prev' : 'next');
    };

    // Windows turns a touch press-and-hold into a right click.
    const onContextMenu = e => {
        if (touches.size > 0 || e.timeStamp - lastTouchEnd < 800) e.preventDefault();
    };

    // Touch listeners go after the mouse ones: on a second finger, onDown starts a pan that startPinch cancels.
    const listeners = [
        ['wheel', onWheel, { passive: false }],
        ['dblclick', onDoubleClick],
        ['pointerdown', onDown],
        ['pointermove', onMove],
        ['pointerup', onUp],
        ['pointercancel', onUp],
        ['pointerdown', onTouchDown],
        ['pointermove', onTouchMove],
        ['pointerup', onTouchUp],
        ['pointercancel', onTouchUp],
        ['contextmenu', onContextMenu],
        ['click', onBlankClick],
    ];
    for (const [type, fn, options] of listeners) stage.addEventListener(type, fn, options);

    function togglePlay() {
        const v = video();
        if (!v) return;
        if (v.paused) v.play(); else v.pause();
    }
    // Space plays or pauses, unless a control has focus and handles Space itself (a focused video toggles natively).
    const root = stage.closest('.lb') ?? stage;
    const onKey = e => {
        if (e.key !== ' ' || e.target.closest('button, video, input, textarea') || !video()) return;
        e.preventDefault();
        togglePlay();
    };
    root.addEventListener('keydown', onKey);

    return {
        reset,
        togglePlay,
        seek,
        zoomBy(factor) { zoomAt(factor, ...centre()); },
        zoomIn() { zoomAt(STEP, ...centre()); },
        zoomOut() { zoomAt(1 / STEP, ...centre()); },
        dispose() {
            abandon();
            clearTimeout(badgeTimer);
            root.removeEventListener('keydown', onKey);
            for (const [type, fn] of listeners) stage.removeEventListener(type, fn);
        }
    };
}

// Presentation mode: controls and the pointer fade out after a moment without mouse movement.
// A touch wakes them too (pointerdown), so a tap shows the controls.
const IDLE_MS = 2500;

export function present(root) {
    let timer;
    const wake = () => {
        root.classList.remove('idle');
        clearTimeout(timer);
        timer = setTimeout(() => root.classList.add('idle'), IDLE_MS);
    };
    root.addEventListener('pointermove', wake);
    root.addEventListener('pointerdown', wake);
    wake();
    return {
        dispose() {
            clearTimeout(timer);
            root.removeEventListener('pointermove', wake);
            root.removeEventListener('pointerdown', wake);
            root.classList.remove('idle');
        }
    };
}
