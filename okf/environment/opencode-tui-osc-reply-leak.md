---
type: Reference
title: OpenCode TUI leaks the OSC colour-probe reply into the prompt
description: On this SSH host the OpenCode TUI types the tail of the terminal's OSC 10 colour reply (":eeee/eeee/eeee") into the prompt at startup; the native stdin parser or a terminal multiplexer avoids it.
tags: [opencode, tui, terminal, ssh, tooling, workaround]
status: draft
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T09:15:35Z }
stale_after: 2027-02-13T00:00:00Z
sources:
  - id: opencode-57345
    resource: https://github.com/anomalyco/opencode/issues/57345
    title: 'tui: delayed OSC 10/11 replies leak into prompt as ":ffff/ffff/ffff" (anomalyco/opencode#57345)'
  - id: opentui-core-081
    resource: https://www.npmjs.com/package/@opentui/core/v/0.8.1
    title: "@opentui/core 0.8.1 — StdinParser / NativeStdinParser, the parser bundled by OpenCode 2.2.7"
  - id: bashrc
    resource: ../../.bashrc
    title: "~/.bashrc on this host (outside the bundle)"
    author: human:ehud
---

# Symptom

Launching the OpenCode TUI on this host (`ehud@…`, over SSH) pre-fills the prompt with
`:eeee/eeee/eeee`. The full sequence the TUI received is
`ESC ]10;rgb:eeee/eeee/eeee BEL`; the leading `ESC ]10;rgb` is easy to miss because the
prompt renders it at the left edge. `eeee` is `0xEE` = 238, i.e. the terminal's default
foreground colour `#eeeeee`.[^opencode-57345]

The text is cosmetic: it is a draft in the prompt input, never a submitted message (nothing
appears in `~/.local/state/opencode/prompt-history.jsonl`). It is cleared with `Ctrl+U`.

# Cause

1. At startup the TUI probes the terminal for its colours (OSC 10/11, `ESC ]10;?` and
   `ESC ]11;?`) to follow the terminal's light/dark mode and to sync the 256-colour palette.
   The palette half of that query runs only when the terminal is detected as
   `ansi256 && !rgb`, which is the case for `TERM=xterm-256color` over SSH.
2. Over SSH the reply is delivered fragmented: the lone `ESC` arrives first, the rest more
   than the parser's escape timeout later.
3. The default parser — `StdinParser` in `@opentui/core`, bundled into the OpenCode binary —
   flushes a pending lone `ESC` after `timeoutMs = 20` ms as the Escape key and sets an
   internal `justFlushedEsc` flag. In the ground state that flag only re-synchronises on
   `[` (0x5B, CSI):

   ```js
   if (this.justFlushedEsc) {
     if (byte === 91 /* '[' */) { this.state = { tag: "esc_recovery" }; continue }
     this.justFlushedEsc = false
   }
   ```

   The late tail begins with `]` (0x5D, OSC), so it falls through and every byte is emitted
   as a key event, which the TUI inserts into the prompt.[^opentui-core-081]

# Evidence

Feeding the bundled parser exactly the fragmented reply — `ESC`, a forced timeout flush,
then `]10;rgb:eeee/eeee/eeee` — emits one key event per byte:

| Step | Events emitted |
|------|----------------|
| `ESC` then 20 ms timeout | `key: "escape"` |
| `]10;rgb:eeee/eeee/eeee` | `key: "]"`, `key: "1"`, `key: "0"`, `key: ";"`, `key: "r"`, `key: "g"`, `key: "b"`, `key: ":"`, `key: "e"`, … |

Upgrading does not fix it: the legacy parser is byte-identical in `@opentui/core` 0.8.1
(bundled by OpenCode 2.2.7) and 0.8.4 (bundled by OpenCode 2.3.0), so the `esc_recovery`
branch still ignores `]`.[^opentui-core-081]

# Fix

| Option | Mechanism | Status |
|--------|-----------|--------|
| `export OTUI_NATIVE_INPUT_PARSER=1` | selects OpenTUI's `NativeStdinParser`; the renderer then sets `replySentinel = true` and holds split replies until the outstanding query batch's DA1 reply arrives, instead of flushing the lone `ESC` | Applied in `~/.bashrc` on this host; the parser is experimental and the fix has **not** been verified end-to-end |
| Run opencode inside tmux/screen | the multiplexer answers the colour probe itself and delivers the reply atomically, so it is never fragmented | Reliable workaround, suggested in the upstream issue |
| `Ctrl+U` | clears the leaked draft before typing | Immediate, per-occurrence |

The switch is `env2.OTUI_NATIVE_INPUT_PARSER` (registered `type: "boolean"`, default
`false`; `1`, `true`, `on`, `yes` are truthy). It has to be set in the environment the TUI
inherits — on this host, `~/.bashrc`, which `~/.profile` sources for login shells.[^bashrc]

# Trust

The cause is reproduced against the exact parser code and is **machine-confirmed**. The fix
is **not** verified: no end-to-end run has shown the prompt staying clean with
`OTUI_NATIVE_INPUT_PARSER=1`, so this concept is `status: draft` and carries no `verified`
entry. Re-check it after the next OpenCode/OpenTUI release, or if input handling changes.

[^opencode-57345]: 'tui: delayed OSC 10/11 replies leak into prompt as ":ffff/ffff/ffff" (anomalyco/opencode#57345)'
[^opentui-core-081]: "@opentui/core 0.8.1 — StdinParser / NativeStdinParser, the parser bundled by OpenCode 2.2.7"
[^bashrc]: "~/.bashrc on this host (outside the bundle)"
