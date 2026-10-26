// Property tests for the masonry layout in layout.js (design §12). Run with: node --test tests/MasonryGallery.UI.Tests/js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { columnCount, computeLayout, visibleItems } from '../../../src/MasonryGallery.UI/wwwroot/js/layout.js';

// Positions are Float32: at y ~ 100,000 px their resolution is ~0.01 px. Real overlaps would be a whole gap.
const STRIDE = 7, GAP = 10, EPS = 0.05;

// Small seeded PRNG (mulberry32) so a failure can be replayed.
function rng(seed) {
    return () => {
        seed = (seed + 0x6D2B79F5) | 0;
        let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

// [id, w, h, kind, ready, rgb, fav] * n, with every kind of shape: portraits, panoramas, 1x1 placeholders, zeros.
function items(n, random) {
    const flat = [];
    for (let i = 0; i < n; i++) {
        const pick = random();
        const [w, h] = pick < 0.05 ? [0, 0] : pick < 0.15 ? [1, 1] : [1 + Math.floor(random() * 8000), 1 + Math.floor(random() * 8000)];
        flat.push(i + 1, w, h, 0, 1, -1, 0);
    }
    return flat;
}

function* cases() {
    for (let seed = 1; seed <= 200; seed++) {
        const random = rng(seed);
        const width = 200 + Math.floor(random() * 3000);
        yield { seed, width, flat: items(Math.floor(random() * 400), random) };
    }
}

test('every item is placed exactly once', () => {
    for (const { seed, width, flat } of cases()) {
        const g = computeLayout(flat, width);
        const placed = g.columns.flat().sort((a, b) => a - b);
        assert.deepEqual(placed, [...Array(flat.length / STRIDE).keys()], `seed ${seed}`);
    }
});

test('tiles never overlap and keep the gap', () => {
    for (const { seed, width, flat } of cases()) {
        const g = computeLayout(flat, width);
        g.columns.forEach((list, k) => {
            for (const i of list) assert.ok(Math.abs(g.xs[i] - k * (g.colW + GAP)) < EPS, `seed ${seed}: item ${i} off its column`);
            for (let p = 1; p < list.length; p++) {
                const above = list[p - 1], below = list[p];
                assert.ok(g.ys[below] >= g.ys[above] + g.hs[above] + GAP - EPS, `seed ${seed}: items ${above} and ${below} overlap`);
            }
        });
        assert.ok(g.colW > 0 && g.colW + GAP > GAP, `seed ${seed}: columns have width`);
    }
});

test('tiles fit the container width', () => {
    for (const { seed, width, flat } of cases()) {
        const g = computeLayout(flat, width);
        for (let i = 0; i < flat.length / STRIDE; i++) assert.ok(g.xs[i] + g.colW <= width + EPS, `seed ${seed}: item ${i} sticks out`);
    }
});

test('columns stay balanced: none ends more than one tile below another', () => {
    for (const { seed, width, flat } of cases()) {
        const g = computeLayout(flat, width);
        const bottoms = g.columns.map(list => list.length ? g.ys[list.at(-1)] + g.hs[list.at(-1)] : 0);
        const tallestTile = g.colW * 2; // ratios are clamped to 2:1
        assert.ok(Math.max(...bottoms) - Math.min(...bottoms) <= tallestTile + GAP + EPS, `seed ${seed}: unbalanced`);
        // Positions are Float32 (plenty for pixels); the running heights are Float64, so allow a pixel.
        assert.ok(Math.abs(g.height - (Math.max(0, ...bottoms) + (flat.length ? GAP : 0))) < 1, `seed ${seed}: height`);
    }
});

test('aspect ratios are clamped to 1:2 .. 2:1', () => {
    const flat = [1, 4000, 100, 0, 1, -1, 0, 2, 100, 4000, 0, 1, -1, 0, 3, 0, 0, 0, 1, -1, 0];
    const g = computeLayout(flat, 1000);
    assert.ok(Math.abs(g.hs[0] - g.colW * 0.5) < EPS);
    assert.ok(Math.abs(g.hs[1] - g.colW * 2) < EPS);
    assert.ok(Math.abs(g.hs[2] - g.colW) < EPS, 'unknown size shows as a square');
});

test('the visible window matches a brute-force scan', () => {
    for (const { seed, width, flat } of cases()) {
        const g = computeLayout(flat, width);
        const random = rng(seed * 7919);
        for (let q = 0; q < 20; q++) {
            const top = random() * (g.height + 500) - 250;
            const bottom = top + random() * 2000;
            const expected = [];
            for (let i = 0; i < flat.length / STRIDE; i++) if (g.ys[i] + g.hs[i] >= top && g.ys[i] <= bottom) expected.push(i);
            assert.deepEqual([...visibleItems(g, top, bottom)].sort((a, b) => a - b), expected, `seed ${seed}, window ${top}..${bottom}`);
        }
    }
});

test('column count gives tiles a usable width', () => {
    assert.equal(columnCount(100), 1);
    for (let w = 300; w <= 4000; w += 37) {
        const c = columnCount(w);
        const tile = (w - GAP * (c - 1)) / c;
        assert.ok(tile >= 150 && tile <= 520, `width ${w}: ${c} columns of ${tile.toFixed(0)} px`);
    }
});
