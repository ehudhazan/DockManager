# Environment

Knowledge about the agent working environment for this repository: properties of the
runtime that shape how agents read tool output and record knowledge, as distinct from the
`eh.DockManager` system itself. Add concepts here when the fact is about the agent, the
harness, or the workflow rather than the product.

* [Agent environment guardrails](guardrails.md) - Tool output shown to agents is sanitized by PII and secret-key redaction guardrails, so placeholder tokens replace sensitive spans while files on disk stay unchanged.
* [OpenCode TUI leaks the OSC colour-probe reply into the prompt](opencode-tui-osc-reply-leak.md) - On this SSH host the OpenCode TUI types the tail of the terminal's OSC 10 colour reply (":eeee/eeee/eeee") into the prompt at startup; the native stdin parser or a terminal multiplexer avoids it.
* [Chrome DevTools MCP over an SSH reverse tunnel](chrome-mcp-over-ssh.md) - How OpenCode on the SSH host drives a Chrome window on the operator's Windows laptop — a reverse SSH tunnel exposes the laptop's CDP port as remote 127.0.0.1:9222 and chrome-devtools-mcp attaches to it with --browser-url.
