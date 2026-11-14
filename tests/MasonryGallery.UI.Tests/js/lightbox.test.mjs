// Touch gesture rules in lightbox.js. Run with: node --test tests/MasonryGallery.UI.Tests/js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { swipeAction, videoSide } from '../../../src/MasonryGallery.UI/wwwroot/js/lightbox.js';

const W = 1000, H = 800, SLOW = 2000;

test('long slow swipes pick the direction of their axis', () => {
    assert.equal(swipeAction(-300, 0, SLOW, W, H), 'next');
    assert.equal(swipeAction(300, 0, SLOW, W, H), 'prev');
    assert.equal(swipeAction(0, 200, SLOW, W, H), 'close');
    assert.equal(swipeAction(0, -200, SLOW, W, H), 'details');
});

test('short slow swipes do nothing, so the media snaps back', () => {
    assert.equal(swipeAction(-150, 0, SLOW, W, H), null);
    assert.equal(swipeAction(0, 100, SLOW, W, H), null);
});

test('a quick flick counts even when short, but a twitch does not', () => {
    assert.equal(swipeAction(-80, 0, 100, W, H), 'next');
    assert.equal(swipeAction(0, 60, 80, W, H), 'close');
    assert.equal(swipeAction(-20, 0, 10, W, H), null);
});

test('video sides are the outer thirds', () => {
    const rect = { left: 100, width: 300 };
    assert.equal(videoSide(150, rect), 'left');
    assert.equal(videoSide(250, rect), null);
    assert.equal(videoSide(350, rect), 'right');
});
