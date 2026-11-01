const test = require('node:test');
const assert = require('node:assert/strict');
const { AGENT_BROWSER_STRINGS, resolveLocale, stringsForLocale } = require('./agentBrowserStrings.cjs');

test('resolves every supported locale family', () => {
  assert.equal(resolveLocale('zh-CN'), 'zh-CN');
  assert.equal(resolveLocale('zh-Hans-CN'), 'zh-CN');
  assert.equal(resolveLocale('zh-TW'), 'zh-TW');
  assert.equal(resolveLocale('zh-Hant-HK'), 'zh-TW');
  assert.equal(resolveLocale('en-US'), 'en');
  assert.equal(resolveLocale('ja-JP'), 'ja');
  assert.equal(resolveLocale('ru-RU'), 'ru');
  assert.equal(resolveLocale('fr-FR'), 'zh-CN');
  assert.equal(resolveLocale(''), 'zh-CN');
  assert.equal(resolveLocale(undefined), 'zh-CN');
});

test('every language carries the same keys and placeholders', () => {
  const reference = AGENT_BROWSER_STRINGS['zh-CN'];
  const referenceKeys = Object.keys(reference).sort();
  for (const [locale, strings] of Object.entries(AGENT_BROWSER_STRINGS)) {
    assert.deepEqual(Object.keys(strings).sort(), referenceKeys, `${locale} key parity`);
    for (const key of referenceKeys) {
      assert.equal(typeof strings[key], 'string', `${locale}.${key} is a string`);
      const expected = (reference[key].match(/\{\w+\}/g) ?? []).sort();
      const actual = (strings[key].match(/\{\w+\}/g) ?? []).sort();
      assert.deepEqual(actual, expected, `${locale}.${key} placeholders`);
    }
  }
});

test('stringsForLocale returns the resolved bundle', () => {
  assert.equal(stringsForLocale('ja-JP'), AGENT_BROWSER_STRINGS.ja);
  assert.equal(stringsForLocale('nope'), AGENT_BROWSER_STRINGS['zh-CN']);
});
