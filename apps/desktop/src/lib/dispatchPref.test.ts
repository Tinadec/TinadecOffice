import { Window } from 'happy-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ENTER_PREF_KEY, getDispatchPref, setDispatchPref } from './dispatchPref';

const testWindow = new Window({ url: 'http://127.0.0.1:5173' });

describe('dispatchPref', () => {
  beforeEach(() => {
    vi.stubGlobal('localStorage', testWindow.localStorage);
    testWindow.localStorage.clear();
  });

  it('defaults to queued', () => {
    expect(getDispatchPref()).toBe('queued');
  });

  it('returns the value set', () => {
    setDispatchPref('parallel');
    expect(getDispatchPref()).toBe('parallel');
    setDispatchPref('ask');
    expect(getDispatchPref()).toBe('ask');
    setDispatchPref('queued');
    expect(getDispatchPref()).toBe('queued');
  });

  it('falls back to queued for invalid stored values', () => {
    localStorage.setItem(ENTER_PREF_KEY, 'bogus');
    expect(getDispatchPref()).toBe('queued');
  });
});

