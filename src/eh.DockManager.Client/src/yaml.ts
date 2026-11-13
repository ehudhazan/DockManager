// File: src/eh.DockManager.Client/src/yaml.ts
//
// A dependency-free, compose-aware YAML assistant used by the stack editor:
//   * path analysis (which keys enclose the caret),
//   * schema-driven key and value completions (with descriptions and known valid values),
//   * smart Enter indentation for mappings and sequence items,
//   * a fast client-side enum lint so mistakes show up while typing.
// Keep the schema in sync with the server-side ComposeSchemaValidator.

export interface SchemaNode {
  kind: 'map' | 'sequence' | 'scalar';
  description?: string;
  /** Known valid values for a scalar (shown as completions and checked by lintEnums). */
  values?: string[];
  /** Placeholder example for a freeform scalar. */
  example?: string;
  keys?: Record<string, SchemaNode>;
  /** Schema used for a child whose name is not in `keys` (e.g. a service name). */
  anyKey?: SchemaNode;
  item?: SchemaNode;
}

export interface Completion {
  label: string;
  detail?: string;
  insertText: string;
}

export interface SuggestionState {
  kind: 'key' | 'value' | 'item' | 'none';
  context: string;
  prefix: string;
  replaceFrom: number;
  items: Completion[];
}

export interface YamlIssue {
  line: number;
  column: number;
  message: string;
  severity: 'error' | 'warning';
  path?: string | null;
}

export const INDENT = 2;

const scalar = (description: string, values?: string[], example?: string): SchemaNode =>
  ({ kind: 'scalar', description, values, example });

const sequence = (description: string, item?: SchemaNode): SchemaNode =>
  ({ kind: 'sequence', description, item });

const map = (description: string, keys: Record<string, SchemaNode>, anyKey?: SchemaNode): SchemaNode =>
  ({ kind: 'map', description, keys, anyKey });

const envString = scalar('Environment variable (KEY=value).', undefined, 'TZ=UTC');

const SERVICE: SchemaNode = map(
  'A service (container) definition.',
  {
    image: scalar('Image to run, e.g. nginx:alpine.', undefined, 'nginx:alpine'),
    build: scalar('Build context path, or a build mapping.', undefined, './app'),
    container_name: scalar('Explicit container name.', undefined, 'my-app'),
    command: sequence('Override the image command.', scalar('Command argument', undefined, 'nginx -g "daemon off;"')),
    entrypoint: sequence('Override the image entrypoint.', scalar('Entrypoint argument')),
    environment: sequence('Environment variables (KEY=value list or a mapping).', envString),
    env_file: sequence('Files with environment variables.', scalar('Path to an env file', undefined, './.env')),
    expose: sequence('Ports exposed to linked services only.', scalar('Container port', undefined, '8080')),
    ports: sequence('Published ports: [host:]container[/tcp|udp|sctp].', scalar('Port mapping', undefined, '"8080:80"')),
    volumes: sequence('Bind mounts and named volumes: source:target[:mode].', scalar('Volume mount', undefined, './data:/data')),
    networks: sequence('Networks this service joins.', scalar('Network name')),
    depends_on: sequence('Services started before this one.', scalar('Service name')),
    restart: scalar('Restart policy.', ['no', 'always', 'unless-stopped', 'on-failure']),
    network_mode: scalar('Network mode.', ['none', 'host', 'bridge', 'service:<name>', 'container:<name>']),
    hostname: scalar('Container hostname.'),
    domainname: scalar('Container domain name.'),
    user: scalar('User (uid[:gid]) to run as.', undefined, '1000:1000'),
    working_dir: scalar('Working directory inside the container.', undefined, '/app'),
    labels: sequence('Metadata labels (KEY=value).', scalar('Label', undefined, 'traefik.enable=true')),
    cap_add: sequence('Linux capabilities to add.', scalar('Capability', undefined, 'NET_ADMIN')),
    cap_drop: sequence('Linux capabilities to drop.', scalar('Capability', undefined, 'ALL')),
    privileged: scalar('Run privileged.', ['true', 'false']),
    platform: scalar('Target platform.', undefined, 'linux/amd64'),
    healthcheck: map('Container health check.', {
      test: sequence('Health check command (CMD or CMD-SHELL).', scalar('Command', undefined, 'CMD-SHELL')),
      interval: scalar('Check interval, e.g. 30s.'),
      timeout: scalar('Timeout, e.g. 5s.'),
      retries: scalar('Retries before unhealthy.'),
      start_period: scalar('Grace period, e.g. 20s.')
    }),
    logging: map('Logging configuration.', {
      driver: scalar('Logging driver.', ['json-file', 'local', 'syslog', 'journald', 'gelf', 'fluentd', 'awslogs', 'splunk', 'etwlogs', 'gcplogs', 'none']),
      options: map('Driver options.', {}, scalar('Driver option'))
    }),
    sysctls: sequence('Kernel parameters.', scalar('sysctl', undefined, 'net.core.somaxconn=1024')),
    tmpfs: sequence('Tmpfs mounts.', scalar('Path', undefined, '/tmp')),
    devices: sequence('Device mappings.', scalar('Device', undefined, '/dev/dri:/dev/dri')),
    dns: sequence('DNS servers.', scalar('DNS server', undefined, '1.1.1.1')),
    extra_hosts: sequence('Extra /etc/hosts entries.', scalar('Host entry', undefined, 'host:10.0.0.1')),
    init: scalar('Run an init process.', ['true', 'false']),
    ipc: scalar('IPC namespace mode.', ['none', 'host', 'private', 'shareable', 'service:<name>']),
    pid: scalar('PID namespace mode.', ['host', 'service:<name>']),
    read_only: scalar('Mount the root filesystem read-only.', ['true', 'false']),
    security_opt: sequence('Security options.', scalar('Option', undefined, 'no-new-privileges:true')),
    shm_size: scalar('Size of /dev/shm, e.g. 64m.'),
    stop_grace_period: scalar('Seconds to wait before killing, e.g. 10s.'),
    stop_signal: scalar('Signal to stop with.', undefined, 'SIGTERM'),
    tty: scalar('Allocate a TTY.', ['true', 'false']),
    stdin_open: scalar('Keep stdin open.', ['true', 'false']),
    pull_policy: scalar('When to pull the image.', ['always', 'never', 'missing', 'build', 'if_not_present', 'daily', 'weekly']),
    profiles: sequence('Compose profiles that enable this service.', scalar('Profile name')),
    deploy: map('Deployment hints (Swarm).', {
      replicas: scalar('Replica count.'),
      mode: scalar('Deployment mode.', ['replicated', 'global', 'replicated-job', 'global-job']),
      restart_policy: map('Restart policy for the deployment.', {
        condition: scalar('Restart condition.', ['none', 'on-failure', 'any']),
        delay: scalar('Delay between restarts, e.g. 5s.'),
        max_attempts: scalar('Maximum attempts.')
      })
    })
  },
  scalar('Unknown service property')
);

export const COMPOSE_SCHEMA: SchemaNode = map('A docker-compose document.', {
  name: scalar('Project name.', undefined, 'my-stack'),
  version: scalar('Obsolete; the Compose Specification ignores it.'),
  services: map('The services (containers) of this stack.', {}, SERVICE),
  networks: map('Named networks.', {}, map('A network.', {
    driver: scalar('Network driver.', ['bridge', 'host', 'overlay', 'ipvlan', 'macvlan', 'none']),
    external: scalar('Use a network that already exists.', ['true', 'false']),
    attachable: scalar('Allow standalone containers to attach.', ['true', 'false']),
    internal: scalar('Restrict external access.', ['true', 'false']),
    enable_ipv6: scalar('Enable IPv6.', ['true', 'false']),
    labels: sequence('Network labels.', scalar('Label', undefined, 'com.example.role=frontend'))
  })),
  volumes: map('Named volumes.', {}, map('A volume.', {
    driver: scalar('Volume driver.', undefined, 'local'),
    external: scalar('Use a volume that already exists.', ['true', 'false']),
    labels: sequence('Volume labels.', scalar('Label'))
  })),
  configs: map('Configs mounted into services.', {}, scalar('Config definition')),
  secrets: map('Secrets mounted into services.', {}, scalar('Secret definition'))
});

// ────────────────────────────────────────────────────────────────────────────
// Line helpers
// ────────────────────────────────────────────────────────────────────────────

export function splitLines(text: string): string[] {
  return text.split('\n');
}

export function indentOf(line: string): number {
  const match = /^[ \t]*/.exec(line);
  if (!match) return 0;
  return match[0].replace(/\t/g, '  ').length;
}

/** A comment or blank line carries no structure. */
function isIgnorable(line: string): boolean {
  const trimmed = line.trim();
  return trimmed.length === 0 || trimmed.startsWith('#');
}

function isSequenceItem(line: string): boolean {
  return /^\s*-\s/.test(line) || /^\s*-$/.test(line);
}

/** The `key` on a mapping line, or on a `- key:` sequence item. */
function keyOf(line: string): string | null {
  const match = /^\s*(?:-\s*)?([A-Za-z0-9_.\-\/]+)\s*:/.exec(line);
  return match ? match[1] : null;
}

/** True when the line contains `key:` followed by only whitespace (a container for children). */
function isEmptyMapping(line: string): boolean {
  return /^\s*(?:-\s*)?[A-Za-z0-9_.\-\/]+\s*:\s*$/.test(line);
}

// ────────────────────────────────────────────────────────────────────────────
// Path analysis
// ────────────────────────────────────────────────────────────────────────────

/**
 * The chain of mapping keys that encloses the given line. Sequence items between two
 * mappings contribute their parent mapping key, so `services.app.ports` is the path for a
 * line under `ports:` regardless of list nesting.
 */
export function pathAt(lines: string[], index: number): string[] {
  const path: string[] = [];
  let indent = indentOf(lines[index] ?? '');
  for (let i = index - 1; i >= 0; i--) {
    const line = lines[i];
    if (isIgnorable(line)) continue;
    const lineIndent = indentOf(line);
    if (lineIndent >= indent) continue;
    const key = keyOf(line);
    if (key) {
      path.unshift(key);
      indent = lineIndent;
    } else {
      indent = lineIndent;
    }
  }
  return path;
}

export function resolveSchema(path: string[]): SchemaNode {
  let node: SchemaNode = COMPOSE_SCHEMA;
  for (const segment of path) {
    if (node.kind === 'sequence') node = node.item ?? scalar('');
    if (node.kind !== 'map') return scalar('');
    if (node.keys && node.keys[segment]) node = node.keys[segment];
    else if (node.anyKey) node = node.anyKey;
    else return scalar('');
  }
  return node;
}

// ────────────────────────────────────────────────────────────────────────────
// Completion engine
// ────────────────────────────────────────────────────────────────────────────

const describeKind = (node: SchemaNode): string =>
  node.kind === 'sequence' ? 'list' : node.kind === 'map' ? 'properties' : 'value';

function keyCompletions(node: SchemaNode, prefix: string): Completion[] {
  if (node.kind === 'sequence') node = node.item ?? scalar('');
  if (node.kind !== 'map' || !node.keys) return [];
  const lower = prefix.toLowerCase();
  return Object.entries(node.keys)
    .filter(([key]) => key.toLowerCase().startsWith(lower))
    .map(([key, child]) => ({
      label: key,
      detail: child.description ? `${describeKind(child)} · ${child.description}` : describeKind(child),
      insertText: `${key}:`
    }));
}

function valueCompletions(node: SchemaNode, prefix: string): Completion[] {
  if (node.kind === 'sequence') {
    const item = node.item ?? scalar('');
    return [
      {
        label: '- ',
        detail: `new list item${item.example ? ` · e.g. ${item.example}` : ''}`,
        insertText: `- ${item.example ?? ''}`
      }
    ];
  }
  if (node.kind === 'map') {
    return Object.entries(node.keys ?? {}).map(([key, child]) => ({
      label: `${key}:`,
      detail: child.description ?? describeKind(child),
      insertText: `${key}: `
    }));
  }
  if (node.values && node.values.length > 0) {
    const lower = prefix.toLowerCase();
    return node.values
      .filter((value) => value.toLowerCase().startsWith(lower))
      .map((value) => ({ label: value, detail: 'valid value', insertText: value }));
  }
  if (node.example) {
    return [{ label: node.example, detail: node.description ?? 'example value', insertText: node.example }];
  }
  return [];
}

/**
 * Works out what the caret is editing and which completions apply.
 * `index`/`column` are 0-based line and column inside that line.
 */
export function suggest(text: string, caret: number): SuggestionState {
  const lines = splitLines(text);
  let offset = 0;
  let index = 0;
  for (let i = 0; i < lines.length; i++) {
    if (caret <= offset + lines[i].length) {
      index = i;
      break;
    }
    offset += lines[i].length + 1;
    index = i + 1;
  }
  index = Math.min(index, lines.length - 1);
  const line = lines[index] ?? '';
  const column = Math.max(0, Math.min(caret - offset, line.length));
  const before = line.slice(0, column);
  const indent = indentOf(line);
  const body = line.slice(indent);
  const dash = /^-\s/.test(body) ? '- ' : '';
  const afterDash = dash ? body.slice(dash.length) : body;

  const path = pathAt(lines, index);
  const parent = resolveSchema(path);

  // Value position: a `: ` already exists on this line (or the line is a bare list item).
  const colonIndex = afterDash.indexOf(':');
  const inValue = colonIndex >= 0 && /:(\s|$)/.test(afterDash.slice(colonIndex));

  if (inValue) {
    const key = afterDash.slice(0, colonIndex).trim();
    const valueStart = indent + dash.length + colonIndex + 1;
    const rawRest = line.slice(valueStart);
    const leading = /^\s*/.exec(rawRest)?.[0] ?? '';
    const replaceFrom = valueStart + leading.length;
    const prefix = line.slice(replaceFrom, column);
    const node = resolveSchema([...path, key]);
    return {
      kind: 'value',
      context: [...path, key].join(' → '),
      prefix,
      replaceFrom,
      items: valueCompletions(node, prefix)
    };
  }

  // Bare list item (`- ` with no key yet): the item schema of the enclosing sequence.
  if (dash && afterDash.trim().length === 0) {
    const sequenceNode = parent.kind === 'sequence' ? parent : parent;
    const item = sequenceNode.kind === 'sequence' ? sequenceNode.item : undefined;
    const replaceFrom = indent + dash.length;
    return {
      kind: 'item',
      context: path.join(' → '),
      prefix: '',
      replaceFrom,
      items: valueCompletions(sequenceNode, '')
    };
  }

  // Key position (optionally a `- key:` item).
  const replaceFrom = indent + dash.length;
  const prefix = line.slice(replaceFrom, column);
  const node = parent.kind === 'sequence' ? parent.item ?? scalar('') : parent;
  return {
    kind: 'key',
    context: path.join(' → ') || 'document',
    prefix,
    replaceFrom,
    items: keyCompletions(node, prefix)
  };
}

// ────────────────────────────────────────────────────────────────────────────
// Smart Enter / Tab
// ────────────────────────────────────────────────────────────────────────────

const SEQUENCE_KEYS = new Set([
  'ports', 'expose', 'volumes', 'env_file', 'depends_on', 'dns', 'dns_search', 'extra_hosts',
  'cap_add', 'cap_drop', 'devices', 'security_opt', 'tmpfs', 'sysctls', 'labels', 'annotations',
  'secrets', 'configs', 'profiles', 'links', 'external_links', 'group_add', 'volumes_from',
  'device_cgroup_rules', 'command', 'entrypoint', 'test', 'networks'
]);

function childLinePrefix(key: string): string {
  return SEQUENCE_KEYS.has(key) ? '- ' : '';
}

/**
 * The text and caret position to apply when Enter is pressed at `caret`.
 * Continues lists with `- `, opens mappings at the next indentation level, and keeps
 * sibling properties aligned.
 */
export function enterInsertion(text: string, caret: number): { insert: string; caret: number } {
  const lines = splitLines(text);
  let offset = 0;
  let index = 0;
  for (let i = 0; i < lines.length; i++) {
    if (caret <= offset + lines[i].length) {
      index = i;
      break;
    }
    offset += lines[i].length + 1;
    index = i + 1;
  }
  index = Math.min(index, lines.length - 1);
  const line = lines[index] ?? '';
  const column = Math.max(0, Math.min(caret - offset, line.length));
  const indent = indentOf(line);
  const before = line.slice(0, column);
  const after = line.slice(column);
  const trimmed = before.trim();

  let nextIndent = indent;
  let continuation = '';

  if (trimmed.startsWith('-')) {
    const body = trimmed.replace(/^-\s*/, '');
    const key = keyOf(`- ${body}`);
    if (key && /:\s*$/.test(trimmed)) {
      // `- key:` → children of the item's mapping
      nextIndent = indent + INDENT;
      continuation = childLinePrefix(key);
    } else {
      // another list item at the same level
      nextIndent = indent;
      continuation = '- ';
    }
  } else {
    const key = keyOf(trimmed);
    if (key && /:\s*$/.test(trimmed)) {
      nextIndent = indent + INDENT;
      continuation = childLinePrefix(key);
    } else if (key) {
      nextIndent = indent;
    } else if (trimmed.length === 0) {
      // blank line: match the previous non-empty line's indentation
      for (let i = index - 1; i >= 0; i--) {
        if (!isIgnorable(lines[i])) {
          nextIndent = indentOf(lines[i]);
          break;
        }
      }
    }
  }

  const indentText = ' '.repeat(Math.max(0, nextIndent));
  // Keep any text that was after the caret on the new line.
  const insert = `\n${indentText}${continuation}${after.startsWith('\n') ? '' : after}`;
  const caretOffset = 1 + indentText.length + continuation.length;
  return { insert, caret: caretOffset };
}

/** Insert 2 spaces, or indent every line of a multi-line selection. */
export function indentSelection(text: string, start: number, end: number, outdent: boolean): { text: string; start: number; end: number } {
  const lines = splitLines(text);
  if (start === end || !text.slice(start, end).includes('\n')) {
    if (outdent) return { text, start, end };
    const pad = ' '.repeat(INDENT);
    return { text: text.slice(0, start) + pad + text.slice(end), start: start + INDENT, end: end + INDENT };
  }

  let offset = 0;
  let firstLine = 0;
  let lastLine = lines.length - 1;
  for (let i = 0; i < lines.length; i++) {
    const lineEnd = offset + lines[i].length;
    if (offset <= start && start <= lineEnd + 1) firstLine = i;
    if (offset <= end && end <= lineEnd + 1) {
      lastLine = i;
      break;
    }
    offset = lineEnd + 1;
  }

  const pad = ' '.repeat(INDENT);
  let delta = 0;
  const updated = lines.map((line, i) => {
    if (i < firstLine || i > lastLine) return line;
    if (outdent) {
      const removed = line.startsWith(pad) ? INDENT : line.startsWith(' ') ? 1 : 0;
      delta -= removed;
      return line.slice(removed);
    }
    delta += INDENT;
    return pad + line;
  });

  return { text: updated.join('\n'), start: Math.max(0, start + (outdent ? -INDENT : INDENT)), end: Math.max(0, end + delta) };
}

// ────────────────────────────────────────────────────────────────────────────
// Client-side enum lint (instant, before the server round-trip)
// ────────────────────────────────────────────────────────────────────────────

const ENUM_BY_KEY: Record<string, string[]> = {
  restart: ['no', 'always', 'unless-stopped', 'on-failure'],
  network_mode: ['none', 'host', 'bridge', 'service:', 'container:'],
  pull_policy: ['always', 'never', 'missing', 'build', 'if_not_present', 'daily', 'weekly'],
  condition: ['service_started', 'service_healthy', 'service_completed_successfully', 'none', 'on-failure', 'any'],
  driver: ['json-file', 'local', 'syslog', 'journald', 'gelf', 'fluentd', 'awslogs', 'splunk', 'etwlogs', 'gcplogs', 'none', 'bridge', 'overlay', 'ipvlan', 'macvlan']
};

const ENUM_BY_PATH: Record<string, string[]> = {
  'services.*.volumes.*.type': ['bind', 'volume', 'tmpfs', 'image', 'npipe', 'cluster'],
  'services.*.volumes.*.consistency': ['consistent', 'cached', 'delegated'],
  'services.*.healthcheck.test': ['CMD', 'CMD-SHELL', 'NONE']
};

/** Fast local checks that do not need the server; the server re-checks and adds more. */
export function lintEnums(text: string): YamlIssue[] {
  const issues: YamlIssue[] = [];
  const lines = splitLines(text);
  lines.forEach((line, index) => {
    const match = /^(\s*)(?:-\s*)?([A-Za-z0-9_.\-\/]+)\s*:\s*(.+?)\s*$/.exec(line);
    if (!match) return;
    const key = match[2];
    const value = match[3].replace(/^["']|["']$/g, '');
    if (value.length === 0 || value.startsWith('{') || value.startsWith('[')) return;

    const path = pathAt(lines, index);
    const pathKey = [...path, key].join('.');
    const wildcardPath = pathKey.replace(/\.(?!services|networks|volumes|healthcheck)[A-Za-z0-9_.\-]+\./g, '.*.');
    const allowed = ENUM_BY_PATH[wildcardPath] ?? ENUM_BY_KEY[key];
    if (!allowed) return;

    const ok = allowed.some((candidate) => candidate.endsWith(':')
      ? value.startsWith(candidate)
      : candidate.toLowerCase() === value.toLowerCase());
    if (!ok) {
      issues.push({
        line: index + 1,
        column: line.indexOf(value) + 1,
        message: `'${value}' is not a known value for '${key}'. Expected one of: ${allowed.join(', ')}.`,
        severity: 'error',
        path: pathKey
      });
    }
  });
  return issues;
}

/** Merges server issues with the local lint, de-duplicated by line + message. */
export function mergeIssues(server: YamlIssue[], local: YamlIssue[]): YamlIssue[] {
  const seen = new Set<string>();
  const all: YamlIssue[] = [];
  for (const issue of [...server, ...local]) {
    const key = `${issue.line}:${issue.severity}:${issue.message}`;
    if (seen.has(key)) continue;
    seen.add(key);
    all.push(issue);
  }
  return all.sort((a, b) => a.line - b.line || a.column - b.column);
}
