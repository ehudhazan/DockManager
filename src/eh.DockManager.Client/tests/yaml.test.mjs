// File: src/eh.DockManager.Client/tests/yaml.test.mjs
//
// Dependency-free tests for the compose editor engine (`src/yaml.ts`) and for the exact key
// handling `YamlEditor` applies on top of it. Run with `npm test` (esbuild bundles the engine
// first, then this file runs on Node).
//
// The `apply`/`enter`/`tab` helpers below mirror `YamlEditor`'s handlers in `ui.tsx`; if the
// component's splicing changes, these must change with it.

import {
  suggest,
  enterInsertion,
  indentSelection,
  lintEnums,
  mergeIssues
} from '../.test-build/yaml.mjs';

let passed = 0;
const failures = [];

function ok(condition, label) {
  if (condition) {
    passed += 1;
  } else {
    failures.push(label);
  }
}

function eq(actual, expected, label) {
  ok(actual === expected, `${label}\n    expected: ${JSON.stringify(expected)}\n    actual:   ${JSON.stringify(actual)}`);
}

// ── Mirrors of YamlEditor handlers ─────────────────────────────────────────────

function apply(value, caret, completion) {
  const suggestion = suggest(value, caret);
  const start = suggestion.replaceFrom;
  const end = Math.max(start, caret);
  return value.slice(0, start) + completion.insertText + value.slice(end);
}

function applyFirst(value, caret) {
  const suggestion = suggest(value, caret);
  return { text: apply(value, caret, suggestion.items[0]), suggestion };
}

function enter(value, start, end = start) {
  const base = start === end ? value : value.slice(0, start) + value.slice(end);
  const { insert, caret } = enterInsertion(base, start);
  return { text: base.slice(0, start) + insert + base.slice(start), caret: start + caret };
}

const enumValue = (state, label) => state.items.find((item) => item.label === label);

// ── suggest(): offsets are absolute ────────────────────────────────────────────

{
  const doc = [
    'name: my-stack',
    'services:',
    '  web:',
    '    image: nginx:alpine',
    '    ports:',
    '      - "8080:80"',
    '    restart: al',
    ''
  ].join('\n');
  const caret = doc.indexOf('restart: al') + 'restart: al'.length;
  const state = suggest(doc, caret);
  eq(state.kind, 'value', 'restart value: kind');
  eq(state.context, 'services → web → restart', 'restart value: context');
  eq(state.prefix, 'al', 'restart value: prefix');
  eq(state.replaceFrom, caret - 2, 'restart value: absolute replaceFrom');

  const next = apply(doc, caret, enumValue(state, 'always'));
  eq(next, doc.replace('restart: al', 'restart: always'), 'restart value: applying never deletes earlier text');
  ok(next.startsWith('name: my-stack\nservices:'), 'restart value: earlier lines intact');
  ok(next.includes('    restart: always'), 'restart value: line rewritten');
}

{
  // A key completion far down the document must splice at the caret, not at the line start.
  const doc = 'name: x\nservices:\n  web:\n    image: nginx\n    \n';
  const caret = doc.length;
  const { text, suggestion } = applyFirst(doc, caret);
  eq(suggestion.kind, 'key', 'key completion: kind');
  eq(suggestion.replaceFrom, caret, 'key completion: replaceFrom at caret');
  eq(text, doc + 'name:', 'key completion: appended, nothing deleted');
  ok(text.includes('image: nginx'), 'key completion: earlier text intact');
}

// ── Empty value: children go on the next line, scalars get a space ─────────────

{
  const doc = 'services:\n  web:\n    ports:';
  const caret = doc.length;
  const state = suggest(doc, caret);
  const next = apply(doc, caret, state.items[0]);
  eq(next, 'services:\n  web:\n    ports:\n      - "8080:80"', 'empty sequence: list item on a new line');
}

{
  const doc = 'services:\n  web:\n    healthcheck:';
  const caret = doc.length;
  const state = suggest(doc, caret);
  const next = apply(doc, caret, state.items[0]);
  eq(next, 'services:\n  web:\n    healthcheck:\n      test: ', 'empty mapping: child key on a new line');
}

{
  const doc = 'services:\n  web:\n    restart:';
  const caret = doc.length;
  const state = suggest(doc, caret);
  const next = apply(doc, caret, enumValue(state, 'always'));
  eq(next, 'services:\n  web:\n    restart: always', 'empty scalar: separating space added');
}

{
  const doc = 'services:\n  web:\n    restart: ';
  const caret = doc.length;
  const state = suggest(doc, caret);
  const next = apply(doc, caret, enumValue(state, 'always'));
  eq(next, 'services:\n  web:\n    restart: always', 'empty scalar with space: no double space');
}

// ── Bare list item completes the item schema, not the sequence template ────────

{
  const doc = 'services:\n  web:\n    ports:\n      - ';
  const caret = doc.length;
  const state = suggest(doc, caret);
  eq(state.kind, 'item', 'bare item: kind');
  eq(state.replaceFrom, caret, 'bare item: replaceFrom at caret');
  const next = apply(doc, caret, state.items[0]);
  eq(next, 'services:\n  web:\n    ports:\n      - "8080:80"', 'bare item: no doubled dash');
}

// ── auto-open only where it helps ─────────────────────────────────────────────

{
  const doc = 'services:\n  web:\n    s';
  const caret = doc.length;
  ok(suggest(doc, caret).auto === true, 'auto: key position opens automatically');
  const value = 'services:\n  web:\n    restart: a';
  ok(suggest(value, value.length).auto === true, 'auto: closed enum opens automatically');
  const free = 'services:\n  web:\n    image: n';
  ok(suggest(free, free.length).auto === false, 'auto: freeform scalar stays quiet');
}

// ── enterInsertion ─────────────────────────────────────────────────────────────

{
  const doc = 'services:\n  web:\n    image: nginx';
  const r = enter(doc, doc.length);
  eq(r.text, 'services:\n  web:\n    image: nginx\n    ', 'enter after a value: sibling indent');
}

{
  const doc = 'services:\n  web:\n    ports:';
  const r = enter(doc, doc.length);
  eq(r.text, 'services:\n  web:\n    ports:\n      - ', 'enter after a sequence key: new item');
}

{
  const doc = 'services:\n  web:\n    healthcheck:';
  const r = enter(doc, doc.length);
  eq(r.text, 'services:\n  web:\n    healthcheck:\n      ', 'enter after a mapping key: child indent');
}

{
  const doc = 'services:\n  web:\n    ports:\n      - "8080:80"';
  const r = enter(doc, doc.length);
  eq(r.text, 'services:\n  web:\n    ports:\n      - "8080:80"\n      - ', 'enter on a list item: another item');
}

{
  const doc = 'services:\n  web:\n    volumes:\n      - type: bind';
  const r = enter(doc, doc.length);
  eq(r.text, 'services:\n  web:\n    volumes:\n      - type: bind\n        ', 'enter inside a list map: continue properties');
}

{
  // Enter with a selection must replace it, never duplicate the tail.
  const doc = 'services:\n  web:\n    image: nginx\n    ports: 8080';
  const start = doc.indexOf('8080');
  const end = doc.length;
  const r = enter(doc, start, end);
  eq(r.text, 'services:\n  web:\n    image: nginx\n    ports: \n      - ', 'enter with a selection collapses it');
  ok(!r.text.includes('8080'), 'enter with a selection drops the selected text');
}

// ── indentSelection ────────────────────────────────────────────────────────────

{
  const doc = 'services:\n  web:\n    image: nginx\n';
  const start = doc.indexOf('  web:');
  const end = doc.indexOf('nginx') + 'nginx'.length;
  const r = indentSelection(doc, start, end, false);
  eq(r.text, 'services:\n    web:\n      image: nginx\n', 'tab indents every selected line');
}

{
  const doc = 'services:\n  web:\n';
  const caret = doc.indexOf('  web:') + 2;
  const r = indentSelection(doc, caret, caret, false);
  eq(r.text, 'services:\n    web:\n', 'tab at a caret inserts two spaces');
}

{
  const doc = 'services:\n    web:\n';
  const caret = 0;
  const r = indentSelection(doc, caret, caret, true);
  eq(r.text, doc, 'shift+tab at a caret is a no-op');
}

// ── lintEnums / mergeIssues ────────────────────────────────────────────────────

{
  const bad = 'services:\n  web:\n    restart: sometimes\n';
  const issues = lintEnums(bad);
  eq(issues.length, 1, 'lint: one issue for a bad enum');
  eq(issues[0].line, 3, 'lint: reports the offending line');
  eq(issues[0].severity, 'error', 'lint: bad enum is an error');

  const good = 'services:\n  web:\n    restart: unless-stopped\n';
  eq(lintEnums(good).length, 0, 'lint: valid enum passes');

  const labels = 'services:\n  web:\n    labels:\n      - "traefik.enable=true"\n';
  eq(lintEnums(labels).length, 0, 'lint: labels do not false-positive');
}

{
  const merged = mergeIssues(
    [{ line: 2, column: 1, message: 'a', severity: 'warning' }],
    [
      { line: 2, column: 1, message: 'a', severity: 'warning' },
      { line: 1, column: 3, message: 'b', severity: 'error' }
    ]
  );
  eq(merged.length, 2, 'merge: de-duplicates by line+severity+message');
  eq(merged[0].line, 1, 'merge: sorted by line');
}

// ── Report ─────────────────────────────────────────────────────────────────────

if (failures.length > 0) {
  console.error(`\n${failures.length} failing check(s):`);
  for (const failure of failures) console.error('  ✗ ' + failure);
  console.error(`\n${passed} passed, ${failures.length} failed.`);
  process.exit(1);
}
console.log(`All ${passed} checks passed.`);
