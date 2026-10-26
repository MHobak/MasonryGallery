// Zoom and pan for the lightbox image. Transform is translate(x, y) scale(s) around the image centre.
const MIN = 1, MAX = 8, STEP = 1.25;

export function create(stage) {
    let scale = 1, x = 0, y = 0;
    let drag = null;
    const image = () => stage.querySelector('img.zoomable');

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

    const onWheel = e => {
        if (!image()) return;
        e.preventDefault();
        zoomAt(e.deltaY < 0 ? STEP : 1 / STEP, e.clientX, e.clientY);
    };
    const onDoubleClick = e => {
        if (!e.target.closest('img.zoomable')) return;
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
        // A drag must not end as a click on the backdrop, which would close the lightbox.
        // The click (if any) is dispatched right after pointerup, so the guard only lives until the next tick.
        if (drag.moved) {
            const swallow = ev => ev.stopPropagation();
            stage.addEventListener('click', swallow, { capture: true, once: true });
            setTimeout(() => stage.removeEventListener('click', swallow, { capture: true }), 0);
        }
        drag = null;
    };

    stage.addEventListener('wheel', onWheel, { passive: false });
    stage.addEventListener('dblclick', onDoubleClick);
    stage.addEventListener('pointerdown', onDown);
    stage.addEventListener('pointermove', onMove);
    stage.addEventListener('pointerup', onUp);
    stage.addEventListener('pointercancel', onUp);

    const video = () => stage.querySelector('video');
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
        // Jump within the current video; clamped to its length.
        seek(seconds) {
            const v = video();
            if (!v || !Number.isFinite(v.duration)) return;
            v.currentTime = Math.min(v.duration, Math.max(0, v.currentTime + seconds));
        },
        zoomBy(factor) { zoomAt(factor, ...centre()); },
        zoomIn() { zoomAt(STEP, ...centre()); },
        zoomOut() { zoomAt(1 / STEP, ...centre()); },
        dispose() {
            root.removeEventListener('keydown', onKey);
            stage.removeEventListener('wheel', onWheel);
            stage.removeEventListener('dblclick', onDoubleClick);
            stage.removeEventListener('pointerdown', onDown);
            stage.removeEventListener('pointermove', onMove);
            stage.removeEventListener('pointerup', onUp);
            stage.removeEventListener('pointercancel', onUp);
        }
    };
}

// Presentation mode: controls and the pointer fade out after a moment without mouse movement.
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
