---
type: Reference
title: Agent environment guardrails
description: Tool output shown to agents is sanitized by PII and secret-key redaction guardrails, so placeholder tokens replace sensitive spans while files on disk stay unchanged.
tags: [guardrails, privacy, security, redaction, workflow]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T09:12:50Z }
sources:
  - id: operator-briefing
    resource: OpenCode agent session — operator briefing and observed tool output
    title: Operator briefing — PII and secret-key redaction guardrails
    author: human:ehud
---

# Definition

The OpenCode agent runtime that operates in this repository applies **PII and secret-key
guardrails** to tool output: before a tool result reaches the model, any span the runtime
classifies as personally identifiable information (PII) or secret/key material is replaced
by a bracketed placeholder token. In this session the placeholder was observed as
`[ADDRESS]`.[^operator-briefing]

Redaction is applied to what the **model sees**, not to the repository. The bytes on disk
are untouched, so a file read through a tool can show a placeholder while the file itself
still contains the original text. When this concept was written, a literal search
(`grep -c -F '[ADDRESS]'`) over the pre-existing concepts returned `0` — the placeholders
were display artifacts, not stored text. This concept is the deliberate exception: it
quotes the token in order to document it.

# Behaviour

| Aspect | Behaviour |
|--------|-----------|
| Applies to | Tool results in general — file reads, `grep`, shell output, and other tool calls |
| Categories | PII (names, addresses, contact details) and secret keys / credentials |
| Effect | The matched span is replaced with a bracketed placeholder token (observed: `[ADDRESS]`) |
| Scope | Display/transport only; the underlying files are never modified |
| Stability | Not deterministic — the same span may or may not be redacted between calls, so placeholders are not stable identifiers |

# Agent rules

* **Never copy a placeholder token into a file.** `[ADDRESS]` (or any similar bracket token)
  is a redaction marker, not content; writing it into code, config, or this bundle corrupts
  the result.
* **Do not treat a redacted span as the true value.** Re-derive the fact from an
  authoritative source (the source file, [`PROMPT.md`](../../PROMPT.md), or the live system)
  before relying on it; if the value is genuinely sensitive, do not record it.
* **Keep the bundle free of PII and secrets.** OKF concepts must not contain personal data,
  credentials, tokens, or keys — the guardrails would hide them from a reader and the
  knowledge would be lost or misleading.
* **Confirm with counts, not previews.** To check whether a placeholder is literal file
  content, use a transformation that yields a non-textual answer (for example
  `grep -c -F '[ADDRESS]' <file>`) instead of eyeballing a preview that redaction may alter.

# Related

* [Project overview](/project/overview.md) — the system this environment builds.
* [`/AGENTS.md`](../../AGENTS.md) — the mandatory consult-and-update workflow that these
  rules extend.
* [Stack constraints](/project/stack-constraints.md) — dependency, storage, and
  secret-handling constraints for the product itself.

# Notes

Reported by the repository operator (`human:ehud`) on 2026-11-13 and confirmed by observing
redacted spans in tool output during the same session. The exact placeholder label and the
classification rules are properties of the harness, not of this repository, and may change;
re-verify against observed tool output if the behaviour looks different.

No independent `verified` entry is recorded, so this concept is in the **unverified** trust
tier (per OKF v0.2).

[^operator-briefing]: Operator briefing — PII and secret-key redaction guardrails
