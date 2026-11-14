// Virtualized masonry. Layout is a pure function of (aspect ratios, width); the DOM only holds visible tiles.
const GAP = 10;
const BUFFER = 1200;
// id, w, h, kind, thumbReady, rgb (placeholder colour or -1), favorite; see GalleryService.LayoutStride
const STRIDE = 7;
const ID = 0, W = 1, H = 2, KIND = 3, READY = 4, RGB = 5, FAV = 6;
const HINT_DELAY = 200;  // ms of scroll quiet before telling the indexer what is on screen
const NAMES_DELAY = 150; // ms to batch file-name lookups for new tiles

/** Columns for a container width: tiles stay roughly 150-250 px wide. */
export function columnCount(width) {
    const target = width < 520 ? 150 : width < 900 ? 200 : 250;
    return Math.max(1, Math.floor((width + GAP) / (target + GAP)));
}

/**
 * The masonry layout as a pure function of the item sizes and the container width: each item goes into the
 * shortest column so far. Aspect ratios are clamped to 1:2..2:1 so panoramas and strips stay usable.
 * Returns positions per item, each column's items top to bottom, and the total height.
 */
export function computeLayout(flat, width) {
    const n = flat.length / STRIDE;
    const c = columnCount(width);
    const colW = (width - GAP * (c - 1)) / c;
    const heights = new Float64Array(c);
    const xs = new Float32Array(n), ys = new Float32Array(n), hs = new Float32Array(n);
    const columns = Array.from({ length: c }, () => []);

    for (let i = 0; i < n; i++) {
        const w = Math.max(1, flat[i * STRIDE + W]);
        const h = Math.max(1, flat[i * STRIDE + H]);
        const ratio = Math.min(2, Math.max(0.5, h / w));
        let k = 0;
        for (let j = 1; j < c; j++) if (heights[j] < heights[k]) k = j;
        xs[i] = k * (colW + GAP);
        ys[i] = heights[k];
        hs[i] = colW * ratio;
        heights[k] += hs[i] + GAP;
        columns[k].push(i);
    }
    return { colW, xs, ys, hs, columns, height: heights.reduce((a, b) => Math.max(a, b), 0) };
}

/**
 * Items that intersect [top, bottom]: per column, a binary search for the first one reaching below `top`
 * (a column's items are sorted by y), then a walk until one starts below `bottom`.
 */
export function visibleItems(geometry, top, bottom) {
    const { ys, hs, columns } = geometry;
    const found = new Set();
    for (const list of columns) {
        let lo = 0, hi = list.length;
        while (lo < hi) {
            const mid = (lo + hi) >> 1;
            if (ys[list[mid]] + hs[list[mid]] < top) lo = mid + 1; else hi = mid;
        }
        for (let p = lo; p < list.length && ys[list[p]] <= bottom; p++) found.add(list[p]);
    }
    return found;
}

// token: per-launch secret the media server requires on every request.
export function create(host, dotnet, mediaBase, token) {
    const spacer = document.createElement('div');
    spacer.className = 'masonry-spacer';
    host.appendChild(spacer);

    let flat = [];
    let index = new Map(); // id -> item position
    let xs, ys, hs;
    let columns = [];
    let colW = 0;
    let width = 0;
    const tiles = new Map(); // item position -> element
    const names = new Map(); // id -> file name, filled lazily for rendered tiles
    let frame = 0;
    let resizeTimer = 0;
    let hintTimer = 0;
    let namesTimer = 0;
    let lastHint = '';

    const count = () => flat.length / STRIDE;
    const at = (i, field) => flat[i * STRIDE + field];

    function layout() {
        width = host.clientWidth;
        if (!width) return;
        const geometry = computeLayout(flat, width);
        ({ colW, xs, ys, hs, columns } = geometry);
        spacer.style.height = geometry.height + 'px';
    }

    function addImage(el, id) {
        const img = new Image();
        img.alt = ''; // the tile button carries the label
        img.decoding = 'async';
        img.loading = 'lazy';
        img.onload = () => img.classList.add('in');
        img.onerror = () => img.remove();
        img.src = `${mediaBase}/thumb/${id}/small?t=${token}`;
        if (img.complete) img.classList.add('in');
        el.prepend(img);
    }

    // The thumbnail's average colour, so a tile is never a blank slot while its image loads.
    function paintPlaceholder(el, i) {
        const rgb = at(i, RGB);
        if (rgb >= 0) el.style.background = '#' + rgb.toString(16).padStart(6, '0');
    }

    function label(el, i) {
        const name = names.get(at(i, ID));
        const what = at(i, KIND) === 1 ? 'video' : 'photo';
        const fav = at(i, FAV) === 1 ? ', favorite' : '';
        el.setAttribute('aria-label', (name ? `Open ${what} ${name}` : `Open ${what}`) + fav);
    }

    // ::before (video badge) and ::after (hover shade) are taken, so the heart is a real element.
    function markFavorite(el, on) {
        const badge = el.querySelector('.fav');
        if (on && !badge) {
            const heart = document.createElement('span');
            heart.className = 'fav';
            heart.setAttribute('aria-hidden', 'true');
            el.append(heart);
        } else if (!on && badge) {
            badge.remove();
        }
    }

    // Rendered tiles (viewport + buffer) still without a thumbnail; sent only when the set changes.
    function reportViewport() {
        const ids = [];
        for (const i of tiles.keys()) if (at(i, READY) !== 1) ids.push(at(i, ID));
        const key = ids.join(',');
        if (key === lastHint) return;
        lastHint = key;
        dotnet.invokeMethodAsync('OnViewport', ids);
    }

    async function fetchNames() {
        const ids = [];
        for (const i of tiles.keys()) if (!names.has(at(i, ID))) ids.push(at(i, ID));
        if (!ids.length) return;
        const result = await dotnet.invokeMethodAsync('GetFileNames', ids);
        ids.forEach((id, k) => names.set(id, result[k]));
        for (const [i, el] of tiles) label(el, i);
    }

    function makeTile(i) {
        const id = at(i, ID);
        const el = document.createElement('button');
        el.type = 'button';
        el.className = at(i, KIND) === 1 ? 'tile video' : 'tile';
        el.dataset.id = id;
        label(el, i);
        el.style.setProperty('--h', (id * 47) % 360);
        el.style.width = colW + 'px';
        el.style.height = hs[i] + 'px';
        el.style.transform = `translate3d(${xs[i]}px, ${ys[i]}px, 0)`;
        paintPlaceholder(el, i);
        markFavorite(el, at(i, FAV) === 1);
        if (at(i, READY) === 1) addImage(el, id);
        tiles.set(i, el);
        host.appendChild(el);
    }

    function render() {
        frame = 0;
        if (!xs) return;
        const top = host.scrollTop - BUFFER;
        const bottom = host.scrollTop + host.clientHeight + BUFFER;
        const want = visibleItems({ ys, hs, columns }, top, bottom);
        for (const [i, el] of tiles) {
            if (!want.has(i)) { el.remove(); tiles.delete(i); }
        }
        for (const i of want) if (!tiles.has(i)) makeTile(i);
        clearTimeout(hintTimer);
        hintTimer = setTimeout(reportViewport, HINT_DELAY);
        clearTimeout(namesTimer);
        namesTimer = setTimeout(fetchNames, NAMES_DELAY);
    }

    const schedule = () => { if (!frame) frame = requestAnimationFrame(render); };

    function clear() {
        for (const el of tiles.values()) el.remove();
        tiles.clear();
    }

    function sameShape(next) {
        if (next.length !== flat.length) return false;
        for (let i = 0; i < next.length; i += STRIDE) {
            if (next[i] !== flat[i] || next[i + W] !== flat[i + W] || next[i + H] !== flat[i + H]) return false;
        }
        return true;
    }

    // The top-most tile on screen and its offset from the viewport top, by id, so it survives a new item list.
    function captureAnchor() {
        let anchor = -1;
        for (const i of tiles.keys()) {
            if (ys[i] + hs[i] > host.scrollTop && (anchor < 0 || ys[i] < ys[anchor])) anchor = i;
        }
        return anchor < 0 ? null : { id: at(anchor, ID), offset: ys[anchor] - host.scrollTop };
    }

    function rebuild(anchor) {
        index = new Map();
        for (let i = 0; i < count(); i++) index.set(at(i, ID), i);
        clear();
        layout();
        const i = anchor ? index.get(anchor.id) : undefined;
        if (i !== undefined) host.scrollTop = ys[i] - anchor.offset;
        render();
    }

    host.addEventListener('scroll', schedule, { passive: true });
    host.addEventListener('click', e => {
        const tile = e.target.closest('.tile');
        if (tile) dotnet.invokeMethodAsync('OpenMedia', Number(tile.dataset.id));
    });
    // Ctrl+N anywhere in the gallery opens another window (the viewer stops its keys from reaching here).
    const onGlobalKey = e => {
        if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && (e.key === 'n' || e.key === 'N')) {
            e.preventDefault();
            dotnet.invokeMethodAsync('NewWindow');
        }
    };
    document.addEventListener('keydown', onGlobalKey);

    // P on a focused tile starts a presentation from it.
    host.addEventListener('keydown', e => {
        const tile = e.target.closest('.tile');
        if (!tile || (e.key !== 'p' && e.key !== 'P') || e.ctrlKey || e.metaKey || e.altKey) return;
        e.preventDefault();
        dotnet.invokeMethodAsync('PresentMedia', Number(tile.dataset.id));
    });

    const observer = new ResizeObserver(() => {
        if (host.clientWidth === width) { schedule(); return; }
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(() => rebuild(captureAnchor()), 120);
    });
    observer.observe(host);

    return {
        // keepPosition: a refresh of the same view (scan progress) keeps the top tile in place;
        // a new filter or sort starts from the top.
        // `json`: the flat item array as JSON text (see App.razor RefreshAsync for why not an array).
        setItems(json, keepPosition) {
            const next = JSON.parse(json);
            if (sameShape(next)) {
                // Same items, same sizes: only readiness/colour/favorite can differ. Patch, don't relayout.
                flat = next;
                for (const [i, el] of tiles) {
                    markFavorite(el, at(i, FAV) === 1);
                    if (at(i, READY) === 1 && !el.querySelector('img')) {
                        paintPlaceholder(el, i);
                        addImage(el, at(i, ID));
                    }
                }
                reportViewport();
                return;
            }
            const anchor = keepPosition ? captureAnchor() : null;
            flat = next;
            if (!keepPosition) host.scrollTop = 0;
            rebuild(anchor);
        },
        // Flat [id, rgb, id, rgb, ...] of thumbnails that just finished; swaps them in without a relayout.
        markReady(pairs) {
            for (let k = 0; k < pairs.length; k += 2) {
                const i = index.get(pairs[k]);
                if (i === undefined) continue;
                flat[i * STRIDE + READY] = 1;
                flat[i * STRIDE + RGB] = pairs[k + 1];
                const el = tiles.get(i);
                if (el && !el.querySelector('img')) {
                    paintPlaceholder(el, i);
                    addImage(el, pairs[k]);
                }
            }
            reportViewport();
        },
        setFavorite(id, on) {
            const i = index.get(id);
            if (i === undefined) return;
            flat[i * STRIDE + FAV] = on ? 1 : 0;
            const el = tiles.get(i);
            if (el) {
                markFavorite(el, on);
                label(el, i);
            }
        },
        // Brings an item into view and focuses it (e.g. after closing the lightbox on a different item).
        reveal(id) {
            const i = index.get(id);
            if (i === undefined) return;
            if (ys[i] < host.scrollTop || ys[i] + hs[i] > host.scrollTop + host.clientHeight) {
                host.scrollTop = Math.max(0, ys[i] - (host.clientHeight - hs[i]) / 2);
                render();
            }
            tiles.get(i)?.focus({ preventScroll: true });
        },
        dispose() {
            document.removeEventListener('keydown', onGlobalKey);
            observer.disconnect();
            cancelAnimationFrame(frame);
            clearTimeout(resizeTimer);
            clearTimeout(hintTimer);
            clearTimeout(namesTimer);
        }
    };
}
