// File: src/eh.DockManager.Client/src/App.tsx
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import { api } from './api';
import type {
  AuditLogEntry,
  CveFinding,
  DockerContainerSummary,
  DockerHostInfo,
  KubeClusterInfo,
  LandingResponse,
  McpKeyInfo,
  PasskeyInfo,
  PortalApp,
  RegistryInfo,
  SessionUser,
  StackSummary,
  SystemInfo,
  TarEntryInfo,
  UserInfo,
  ViewKey,
  WebhookInfo
} from './types';

// ────────────────────────────────────────────────────────────────────────────
// Small utilities & shared UI atoms
// ────────────────────────────────────────────────────────────────────────────
const NAV: { key: ViewKey; label: string; icon: string }[] = [
  { key: 'portal', label: 'Portal', icon: '🏠' },
  { key: 'dashboard', label: 'Dashboard', icon: '📊' },
  { key: 'stacks', label: 'Stacks', icon: '🧩' },
  { key: 'containers', label: 'Containers', icon: '🐳' },
  { key: 'kubernetes', label: 'Kubernetes', icon: '☸️' },
  { key: 'images', label: 'Images', icon: '💾' },
  { key: 'hosts', label: 'Hosts', icon: '🖧' },
  { key: 'security', label: 'Security', icon: '🔐' }
];

function classNames(...parts: (string | false | null | undefined)[]): string {
  return parts.filter(Boolean).join(' ');
}

function StatusBadge({ status }: { status: string }) {
  const tone =
    /running|online|healthy|active/i.test(status)
      ? 'bg-emerald-950/60 text-emerald-300 border border-emerald-800/60'
      : /paused|starting|pending|degraded/i.test(status)
        ? 'bg-amber-950/60 text-amber-300 border border-amber-800/60'
        : 'bg-red-950/60 text-red-300 border border-red-900/60';
  return <span className={classNames('badge', tone)}>{status}</span>;
}

function Sparkline({ data, stroke = '#38bdf8', height = 30 }: { data: number[]; stroke?: string; height?: number }) {
  if (data.length < 2) return <div style={{ height }} className="text-[10px] text-slate-600">no data</div>;
  const max = Math.max(...data, 1);
  const min = Math.min(...data, 0);
  const range = max - min || 1;
  const width = 120;
  const points = data
    .map((value, index) => {
      const x = (index / (data.length - 1)) * width;
      const y = height - ((value - min) / range) * (height - 3) - 1.5;
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(' ');
  return (
    <svg viewBox={`0 0 ${width} ${height}`} className="w-full" style={{ height }} preserveAspectRatio="none">
      <polyline points={points} fill="none" stroke={stroke} strokeWidth="1.6" vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

function diffLines(original: string, modified: string): { type: 'same' | 'add' | 'del'; text: string }[] {
  const a = original.split('\n');
  const b = modified.split('\n');
  const max = Math.max(a.length, b.length);
  const out: { type: 'same' | 'add' | 'del'; text: string }[] = [];
  for (let i = 0; i < max; i += 1) {
    if (a[i] === b[i]) out.push({ type: 'same', text: a[i] ?? '' });
    else {
      if (a[i] !== undefined) out.push({ type: 'del', text: a[i] });
      if (b[i] !== undefined) out.push({ type: 'add', text: b[i] });
    }
  }
  return out;
}

function useEventSource<T>(url: string | null, onMessage: (data: T) => void) {
  const handlerRef = useRef(onMessage);
  handlerRef.current = onMessage;
  useEffect(() => {
    if (!url) return undefined;
    const source = new EventSource(url, { withCredentials: true });
    source.onmessage = (event) => {
      try {
        handlerRef.current(JSON.parse(event.data) as T);
      } catch {
        /* ignore malformed frames */
      }
    };
    return () => source.close();
  }, [url]);
}

// ────────────────────────────────────────────────────────────────────────────
// Root application
// ────────────────────────────────────────────────────────────────────────────
export default function App() {
  const [view, setView] = useState<ViewKey>(() => {
    const hash = location.hash.replace('#', '') as ViewKey;
    return NAV.some((n) => n.key === hash) ? hash : 'portal';
  });
  const [session, setSession] = useState<SessionUser | null>(null);
  const [loading, setLoading] = useState(true);
  const [commandOpen, setCommandOpen] = useState(false);
  const [hosts, setHosts] = useState<DockerHostInfo[]>([]);
  const [activeHost, setActiveHost] = useState<number | undefined>(undefined);
  const [terminals, setTerminals] = useState<TerminalTab[]>([]);
  const [terminalOpen, setTerminalOpen] = useState(false);
  const [inspector, setInspector] = useState<string | null>(null);
  const [toast, setToast] = useState<string | null>(null);

  const notify = useCallback((message: string) => {
    setToast(message);
    window.setTimeout(() => setToast(null), 3200);
  }, []);

  const refreshSession = useCallback(async () => {
    try {
      const me = await api.me();
      setSession(me);
    } catch {
      setSession({ authenticated: false });
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refreshSession();
  }, [refreshSession]);

  useEffect(() => {
    location.hash = view;
  }, [view]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        setCommandOpen((open) => !open);
      }
      if (event.key === 'Escape') setCommandOpen(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  useEffect(() => {
    if (!session?.authenticated) return;
    api.hosts().then(setHosts).catch(() => setHosts([]));
  }, [session?.authenticated]);

  const openTerminal = useCallback((title: string, url: string, onSend?: (data: string) => void) => {
    const id = `${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
    setTerminals((tabs) => [...tabs, { id, title, url, onSend }]);
    setTerminalOpen(true);
  }, []);

  if (loading) {
    return <Splash message="Starting eh.DockManager…" />;
  }
  if (!session?.authenticated) {
    return <LoginView onAuthenticated={refreshSession} notify={notify} />;
  }

  return (
    <div className="flex h-full min-h-screen bg-ink-950 text-slate-100">
      <Sidebar view={view} setView={setView} />
      <div className="flex min-w-0 flex-1 flex-col pb-16 md:pb-0">
        <TopBar
          session={session}
          hosts={hosts}
          activeHost={activeHost}
          setActiveHost={setActiveHost}
          onCommand={() => setCommandOpen(true)}
          onLogout={async () => {
            await api.logout();
            await refreshSession();
          }}
        />
        <main className="min-h-0 flex-1 overflow-y-auto p-3 sm:p-5">
          {view === 'portal' && <PortalLandingView notify={notify} openTerminal={openTerminal} />}
          {view === 'dashboard' && <DashboardView activeHost={activeHost} notify={notify} />}
          {view === 'stacks' && <StacksView notify={notify} />}
          {view === 'containers' && (
            <ContainersView
              activeHost={activeHost}
              notify={notify}
              openTerminal={openTerminal}
              inspect={setInspector}
            />
          )}
          {view === 'kubernetes' && <KubernetesView notify={notify} openTerminal={openTerminal} />}
          {view === 'images' && <ImagesAndRegistriesView activeHost={activeHost} notify={notify} />}
          {view === 'hosts' && <HostsAndWebhooksView notify={notify} refreshHosts={() => api.hosts().then(setHosts)} />}
          {view === 'security' && <SecurityAndSsoView notify={notify} />}
        </main>
      </div>

      <BottomNav view={view} setView={setView} />

      {commandOpen && (
        <CommandPalette
          onClose={() => setCommandOpen(false)}
          onNavigate={(key) => {
            setView(key);
            setCommandOpen(false);
          }}
          actions={[
            { label: 'Open Portal landing page', run: () => (location.href = '/') },
            { label: 'Refresh portal discovery', run: async () => { await api.refreshPortal(); notify('Portal refreshed'); } },
            { label: 'Toggle terminal drawer', run: () => setTerminalOpen((o) => !o) }
          ]}
        />
      )}

      {inspector && (
        <ContainerInspector id={inspector} onClose={() => setInspector(null)} notify={notify} openTerminal={openTerminal} />
      )}

      <TerminalDrawer
        tabs={terminals}
        open={terminalOpen}
        onOpenChange={setTerminalOpen}
        onClose={(id) => setTerminals((tabs) => tabs.filter((t) => t.id !== id))}
      />

      {toast && (
        <div className="fixed bottom-20 left-1/2 z-50 -translate-x-1/2 rounded-lg border border-ink-700 bg-ink-800 px-4 py-2 text-sm shadow-lg md:bottom-6">
          {toast}
        </div>
      )}
    </div>
  );
}

interface TerminalTab {
  id: string;
  title: string;
  url: string;
  onSend?: (data: string) => void;
}

function Splash({ message }: { message: string }) {
  return (
    <div className="flex h-screen items-center justify-center bg-ink-950 text-slate-400">
      <div className="animate-pulseSoft text-sm">{message}</div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// Shell: sidebar, top bar, bottom nav, command palette
// ────────────────────────────────────────────────────────────────────────────
function Sidebar({ view, setView }: { view: ViewKey; setView: (v: ViewKey) => void }) {
  return (
    <aside className="hidden w-56 shrink-0 flex-col border-r border-ink-800 bg-ink-900/60 p-3 md:flex">
      <div className="mb-4 flex items-center gap-2 px-2 py-1">
        <span className="text-xl">🐳</span>
        <div>
          <div className="text-sm font-semibold">eh.DockManager</div>
          <div className="text-[10px] uppercase tracking-wider text-slate-500">Control Plane</div>
        </div>
      </div>
      <nav className="flex flex-col gap-1">
        {NAV.map((item) => (
          <button
            key={item.key}
            onClick={() => setView(item.key)}
            className={classNames(
              'flex items-center gap-3 rounded-lg px-3 py-2 text-sm transition-colors',
              view === item.key ? 'bg-accent-muted/50 text-white' : 'text-slate-400 hover:bg-ink-800 hover:text-slate-200'
            )}
          >
            <span>{item.icon}</span>
            {item.label}
          </button>
        ))}
      </nav>
      <div className="mt-auto px-2 text-[10px] text-slate-600">
        Ctrl+K for the command palette
      </div>
    </aside>
  );
}

function TopBar({
  session,
  hosts,
  activeHost,
  setActiveHost,
  onCommand,
  onLogout
}: {
  session: SessionUser;
  hosts: DockerHostInfo[];
  activeHost: number | undefined;
  setActiveHost: (id: number | undefined) => void;
  onCommand: () => void;
  onLogout: () => void;
}) {
  return (
    <header className="flex items-center gap-3 border-b border-ink-800 bg-ink-900/70 px-3 py-2 backdrop-blur sm:px-5">
      <div className="md:hidden text-lg">🐳</div>
      <div className="hidden items-center gap-2 sm:flex">
        <span className="label mb-0">Host</span>
        <select
          className="input w-48 py-1.5"
          value={activeHost ?? ''}
          onChange={(event) => setActiveHost(event.target.value ? Number(event.target.value) : undefined)}
        >
          <option value="">default</option>
          {hosts.map((host) => (
            <option key={host.id} value={host.id}>
              {host.name} ({host.kind})
            </option>
          ))}
        </select>
      </div>
      <button className="btn-ghost ml-auto hidden sm:inline-flex" onClick={onCommand}>
        ⌘K Command
      </button>
      <div className="ml-auto flex items-center gap-2 sm:ml-0">
        <span className="hidden text-xs text-slate-400 sm:inline">
          {session.username} · <span className="text-accent">{session.role}</span>
        </span>
        <button className="btn-ghost px-2 py-1 text-xs" onClick={onLogout}>
          Sign out
        </button>
      </div>
    </header>
  );
}

function BottomNav({ view, setView }: { view: ViewKey; setView: (v: ViewKey) => void }) {
  return (
    <nav className="fixed inset-x-0 bottom-0 z-30 flex border-t border-ink-800 bg-ink-900/95 backdrop-blur md:hidden">
      {NAV.map((item) => (
        <button
          key={item.key}
          onClick={() => setView(item.key)}
          className={classNames(
            'flex flex-1 flex-col items-center gap-0.5 py-2 text-[10px]',
            view === item.key ? 'text-accent' : 'text-slate-500'
          )}
        >
          <span className="text-base">{item.icon}</span>
          {item.label}
        </button>
      ))}
    </nav>
  );
}

function CommandPalette({
  onClose,
  onNavigate,
  actions
}: {
  onClose: () => void;
  onNavigate: (key: ViewKey) => void;
  actions: { label: string; run: () => void | Promise<void> }[];
}) {
  const [query, setQuery] = useState('');
  const items = useMemo(
    () => [
      ...NAV.map((n) => ({ label: `Go to ${n.label}`, run: () => onNavigate(n.key) })),
      ...actions
    ],
    [actions, onNavigate]
  );
  const filtered = items.filter((i) => i.label.toLowerCase().includes(query.toLowerCase()));
  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center bg-black/60 p-4 pt-24" onClick={onClose}>
      <div className="card w-full max-w-lg overflow-hidden" onClick={(e) => e.stopPropagation()}>
        <input
          autoFocus
          className="w-full border-b border-ink-700 bg-transparent px-4 py-3 text-sm outline-none"
          placeholder="Type a command or view…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <div className="max-h-72 overflow-y-auto">
          {filtered.map((item) => (
            <button
              key={item.label}
              className="block w-full px-4 py-2 text-left text-sm text-slate-300 hover:bg-ink-800"
              onClick={() => {
                void item.run();
                onClose();
              }}
            >
              {item.label}
            </button>
          ))}
          {filtered.length === 0 && <div className="px-4 py-3 text-sm text-slate-500">No matches</div>}
        </div>
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// Login
// ────────────────────────────────────────────────────────────────────────────
function LoginView({ onAuthenticated, notify }: { onAuthenticated: () => void; notify: (m: string) => void }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [totp, setTotp] = useState('');
  const [needsTotp, setNeedsTotp] = useState(false);
  const [busy, setBusy] = useState(false);
  const [providers, setProviders] = useState({ google: false, microsoft: false });

  useEffect(() => {
    api.oidcProviders().then(setProviders).catch(() => setProviders({ google: false, microsoft: false }));
  }, []);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setBusy(true);
    try {
      const result = await api.login(username, password, needsTotp ? totp : undefined);
      if (result.requiresTotp) {
        setNeedsTotp(true);
        notify('Enter your authenticator code');
      } else {
        await onAuthenticated();
      }
    } catch (error) {
      notify((error as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const passkeyLogin = async () => {
    try {
      const begin = await api.passkeyLoginBegin(username || undefined);
      const credential = await navigator.credentials.get({
        publicKey: decodePublicKeyRequestOptions(begin.options)
      });
      if (!credential) return;
      await api.passkeyLoginComplete(begin.flowId, encodeCredential(credential as PublicKeyCredential));
      await onAuthenticated();
    } catch (error) {
      notify((error as Error).message);
    }
  };

  return (
    <div className="flex min-h-screen items-center justify-center bg-ink-950 p-4">
      <form onSubmit={submit} className="card w-full max-w-sm space-y-4 p-6">
        <div className="flex items-center gap-2">
          <span className="text-2xl">🐳</span>
          <div>
            <h1 className="text-lg font-semibold">eh.DockManager</h1>
            <p className="text-xs text-slate-500">Sign in to the control plane</p>
          </div>
        </div>
        <div>
          <label className="label">Username</label>
          <input className="input" value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" />
        </div>
        <div>
          <label className="label">Password</label>
          <input
            className="input"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
          />
        </div>
        {needsTotp && (
          <div>
            <label className="label">Authenticator code</label>
            <input className="input tracking-[0.4em]" value={totp} onChange={(e) => setTotp(e.target.value)} inputMode="numeric" />
          </div>
        )}
        <button className="btn-primary w-full" disabled={busy} type="submit">
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
        <button className="btn-ghost w-full" type="button" onClick={passkeyLogin}>
          🪪 Sign in with a passkey
        </button>
        {(providers.google || providers.microsoft) && (
          <div className="grid grid-cols-2 gap-2">
            {providers.google && (
              <a className="btn-ghost" href="/api/auth/oidc/google/challenge">
                Google
              </a>
            )}
            {providers.microsoft && (
              <a className="btn-ghost" href="/api/auth/oidc/microsoft/challenge">
                Microsoft
              </a>
            )}
          </div>
        )}
        <a className="block text-center text-xs text-slate-500 hover:text-accent" href={api.ssoLoginUrl}>
          Enterprise SSO (SAML 2.0)
        </a>
      </form>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 1 — Portal landing
// ────────────────────────────────────────────────────────────────────────────
function PortalLandingView({
  notify,
  openTerminal
}: {
  notify: (m: string) => void;
  openTerminal: (title: string, url: string) => void;
}) {
  const [landing, setLanding] = useState<LandingResponse | null>(null);
  const [query, setQuery] = useState('');
  const [liveMetrics, setLiveMetrics] = useState(true);
  const [editing, setEditing] = useState<PortalApp | null>(null);

  const load = useCallback(() => {
    api.landing().then(setLanding).catch((e) => notify((e as Error).message));
  }, [notify]);

  useEffect(load, [load]);

  const categories = useMemo(() => {
    const apps = (landing?.apps ?? []).filter((a) =>
      `${a.name} ${a.subdomain} ${a.category}`.toLowerCase().includes(query.toLowerCase())
    );
    const map = new Map<string, PortalApp[]>();
    for (const app of apps) {
      const list = map.get(app.category) ?? [];
      list.push(app);
      map.set(app.category, list);
    }
    return [...map.entries()];
  }, [landing, query]);

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center gap-3">
        <div>
          <h1 className="text-xl font-semibold">App Portal</h1>
          <p className="text-xs text-slate-500">
            {landing ? `https://${landing.domain}/ · ${landing.apps.length} applications` : 'Loading applications…'}
          </p>
        </div>
        <input
          className="input ml-auto w-full max-w-xs"
          placeholder="Search apps…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <label className="flex items-center gap-2 text-xs text-slate-400">
          <input type="checkbox" checked={liveMetrics} onChange={(e) => setLiveMetrics(e.target.checked)} />
          live metrics
        </label>
        <button
          className="btn-ghost"
          onClick={async () => {
            await api.refreshPortal();
            load();
            notify('Discovery refreshed');
          }}
        >
          Refresh
        </button>
      </div>

      {categories.map(([category, apps]) => (
        <section key={category} className="space-y-2">
          <h2 className="text-xs font-semibold uppercase tracking-wider text-slate-500">{category}</h2>
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
            {apps.map((app) => (
              <PortalCard
                key={app.id}
                app={app}
                live={liveMetrics}
                onEdit={() => setEditing(app)}
                onOpen={() => window.open(app.url ?? '#', '_blank')}
                onTerminal={() =>
                  app.containerName && openTerminal(app.name, api.execUrl(app.containerName, '/bin/sh'))
                }
              />
            ))}
          </div>
        </section>
      ))}

      {categories.length === 0 && <div className="card p-6 text-center text-sm text-slate-500">No applications discovered.</div>}

      {editing && (
        <PortalAppEditor
          app={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            load();
          }}
          notify={notify}
        />
      )}
    </div>
  );
}

function PortalCard({
  app,
  live,
  onEdit,
  onOpen,
  onTerminal
}: {
  app: PortalApp;
  live: boolean;
  onEdit: () => void;
  onOpen: () => void;
  onTerminal: () => void;
}) {
  const [cpu, setCpu] = useState<number[]>([]);
  const [ram, setRam] = useState<number[]>([]);

  useEffect(() => {
    if (!live || !app.containerName) return undefined;
    const source = new EventSource(api.statsUrl(app.containerName), { withCredentials: true });
    source.onmessage = (event) => {
      try {
        const sample = JSON.parse(event.data) as { cpuPercent?: number; memoryMb?: number; error?: string };
        if (sample.error) return;
        setCpu((prev) => [...prev, sample.cpuPercent ?? 0].slice(-30));
        setRam((prev) => [...prev, sample.memoryMb ?? 0].slice(-30));
      } catch {
        /* ignore */
      }
    };
    return () => source.close();
  }, [live, app.containerName]);

  return (
    <div className="card group relative flex flex-col gap-3 p-4">
      <div className="flex items-start gap-3">
        <div className="flex h-10 w-10 items-center justify-center rounded-lg bg-ink-800 text-lg">
          {app.icon ? <img src={app.icon} alt="" className="h-6 w-6" /> : app.name.slice(0, 1).toUpperCase()}
        </div>
        <div className="min-w-0 flex-1">
          <button className="truncate text-left text-sm font-semibold hover:text-accent" onClick={onOpen}>
            {app.name}
          </button>
          <div className="truncate text-[11px] text-slate-500">{app.subdomain}</div>
        </div>
        {app.autoscale !== 'None' && (
          <span className="badge bg-cyan-950/60 text-cyan-300 border border-cyan-800/60">scale-0</span>
        )}
      </div>
      {live && app.containerName && (
        <div className="grid grid-cols-2 gap-2 text-[10px] text-slate-400">
          <div>
            CPU {cpu.length ? `${(cpu[cpu.length - 1] ?? 0).toFixed(1)}%` : '—'}
            <Sparkline data={cpu} stroke="#38bdf8" />
          </div>
          <div>
            RAM {ram.length ? `${(ram[ram.length - 1] ?? 0).toFixed(0)} MB` : '—'}
            <Sparkline data={ram} stroke="#34d399" />
          </div>
        </div>
      )}
      <div className="flex items-center gap-2">
        <StatusBadge status={app.auth === 'Admin' ? 'admin' : app.auth} />
        <div className="ml-auto flex gap-1 opacity-0 transition-opacity group-hover:opacity-100">
          {app.containerName && (
            <button className="btn-ghost px-2 py-1 text-xs" onClick={onTerminal}>
              Term
            </button>
          )}
          <button className="btn-ghost px-2 py-1 text-xs" onClick={onEdit}>
            Edit
          </button>
        </div>
      </div>
    </div>
  );
}

function PortalAppEditor({
  app,
  onClose,
  onSaved,
  notify
}: {
  app: PortalApp;
  onClose: () => void;
  onSaved: () => void;
  notify: (m: string) => void;
}) {
  const [draft, setDraft] = useState<PortalApp>(app);
  const save = async () => {
    try {
      await api.savePortalApp(draft);
      notify('Application saved');
      onSaved();
    } catch (error) {
      notify((error as Error).message);
    }
  };
  return (
    <Modal title={`Edit ${app.name}`} onClose={onClose}>
      <div className="space-y-3">
        <div>
          <label className="label">Name</label>
          <input className="input" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="label">Category</label>
            <input className="input" value={draft.category} onChange={(e) => setDraft({ ...draft, category: e.target.value })} />
          </div>
          <div>
            <label className="label">Icon URL</label>
            <input className="input" value={draft.icon ?? ''} onChange={(e) => setDraft({ ...draft, icon: e.target.value })} />
          </div>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="label">Access</label>
            <select
              className="input"
              value={draft.auth}
              onChange={(e) => setDraft({ ...draft, auth: e.target.value as PortalApp['auth'] })}
            >
              <option value="Public">Public</option>
              <option value="Sso">SSO</option>
              <option value="Admin">Admin</option>
            </select>
          </div>
          <div>
            <label className="label">Autoscale</label>
            <select
              className="input"
              value={draft.autoscale}
              onChange={(e) => setDraft({ ...draft, autoscale: e.target.value as PortalApp['autoscale'] })}
            >
              <option value="None">None</option>
              <option value="Idle15m">Idle 15m</option>
              <option value="Idle1h">Idle 1h</option>
            </select>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="label">Target port</label>
            <input
              className="input"
              type="number"
              value={draft.targetPort ?? ''}
              onChange={(e) => setDraft({ ...draft, targetPort: e.target.value ? Number(e.target.value) : null })}
            />
          </div>
          <div>
            <label className="label">External URL</label>
            <input
              className="input"
              value={draft.externalUrl ?? ''}
              onChange={(e) => setDraft({ ...draft, externalUrl: e.target.value })}
            />
          </div>
        </div>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={draft.hidden} onChange={(e) => setDraft({ ...draft, hidden: e.target.checked })} />
          Hide from portal
        </label>
        <div className="flex justify-end gap-2 pt-2">
          <button className="btn-ghost" onClick={onClose}>
            Cancel
          </button>
          <button className="btn-primary" onClick={save}>
            Save
          </button>
        </div>
      </div>
    </Modal>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 2 — Dashboard
// ────────────────────────────────────────────────────────────────────────────
function DashboardView({ activeHost, notify }: { activeHost?: number; notify: (m: string) => void }) {
  const [info, setInfo] = useState<SystemInfo | null>(null);
  const [containers, setContainers] = useState<DockerContainerSummary[]>([]);
  const [images, setImages] = useState<{ Id: string; RepoTags?: string[]; Size: number; Containers: number }[]>([]);
  const [events, setEvents] = useState<AuditLogEntry[]>([]);
  const [cves, setCves] = useState<CveFinding[]>([]);

  useEffect(() => {
    api.systemInfo().then(setInfo).catch(() => undefined);
    api.containers(activeHost).then(setContainers).catch(() => undefined);
    api.images(activeHost).then(setImages).catch(() => undefined);
    api.audit(50).then(setEvents).catch(() => undefined);
  }, [activeHost]);

  useEventSource<AuditLogEntry>('/api/events/stream', (entry) => setEvents((prev) => [entry, ...prev].slice(0, 100)));

  const running = containers.filter((c) => c.State === 'running').length;
  const diskGb = images.reduce((sum, image) => sum + image.Size, 0) / 1024 / 1024 / 1024;

  return (
    <div className="space-y-5">
      <h1 className="text-xl font-semibold">Dashboard</h1>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <StatCard label="Containers" value={`${running}/${containers.length}`} hint="running / total" />
        <StatCard label="Images" value={String(images.length)} hint={`${diskGb.toFixed(1)} GB on disk`} />
        <StatCard label="Proxy routes" value={String(info?.routes ?? 0)} hint={info?.portalDomain ?? ''} />
        <StatCard label="Uptime" value={`${Math.floor((info?.uptimeSeconds ?? 0) / 3600)}h`} hint={`v${info?.version ?? '?'}`} />
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <div className="card p-4">
          <div className="mb-3 flex items-center justify-between">
            <h2 className="text-sm font-semibold">Provenance event feed</h2>
            <span className="text-[11px] text-slate-500">WebUI · MCP · Updater · CLI</span>
          </div>
          <div className="max-h-80 space-y-1 overflow-y-auto font-mono text-xs">
            {events.map((event) => (
              <div key={event.id} className="flex items-center gap-2 border-b border-ink-800/60 py-1">
                <ActorBadge actor={event.actor} />
                <span className="text-slate-300">{event.action}</span>
                <span className="truncate text-slate-500">{event.target}</span>
                <span className="ml-auto shrink-0 text-[10px] text-slate-600">
                  {new Date(event.timestamp).toLocaleTimeString()}
                </span>
              </div>
            ))}
            {events.length === 0 && <div className="py-4 text-center text-slate-600">No events yet.</div>}
          </div>
        </div>

        <div className="card p-4">
          <div className="mb-3 flex items-center justify-between">
            <h2 className="text-sm font-semibold">Vulnerability summary</h2>
            <button
              className="btn-ghost px-2 py-1 text-xs"
              onClick={async () => {
                const target = containers.find((c) => c.State === 'running');
                if (!target) {
                  notify('No running container to scan.');
                  return;
                }
                try {
                  const findings = await api.cveScan(target.Id);
                  setCves(findings);
                  notify(`${findings.length} findings`);
                } catch (error) {
                  notify((error as Error).message);
                }
              }}
            >
              Scan a container
            </button>
          </div>
          <div className="max-h-80 space-y-1 overflow-y-auto text-xs">
            {cves.map((cve) => (
              <div key={cve.id + cve.package} className="border-b border-ink-800/60 py-1">
                <span className="text-red-300">{cve.id}</span> · <span className="text-slate-300">{cve.package}</span>{' '}
                <span className="text-slate-500">{cve.version}</span>
              </div>
            ))}
            {cves.length === 0 && <div className="py-4 text-center text-slate-600">No scan run yet (OSV.dev).</div>}
          </div>
        </div>
      </div>
    </div>
  );
}

function ActorBadge({ actor }: { actor: string }) {
  const tone: Record<string, string> = {
    WebUi: 'bg-sky-950/60 text-sky-300',
    Mcp: 'bg-violet-950/60 text-violet-300',
    Updater: 'bg-emerald-950/60 text-emerald-300',
    Cli: 'bg-amber-950/60 text-amber-300',
    System: 'bg-slate-800 text-slate-300'
  };
  return <span className={classNames('badge', tone[actor] ?? tone.System)}>{actor}</span>;
}

function StatCard({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div className="card p-4">
      <div className="text-xs uppercase tracking-wide text-slate-500">{label}</div>
      <div className="mt-1 text-2xl font-semibold">{value}</div>
      {hint && <div className="text-[11px] text-slate-500">{hint}</div>}
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 3 — Stacks
// ────────────────────────────────────────────────────────────────────────────
function StacksView({ notify }: { notify: (m: string) => void }) {
  const [stacks, setStacks] = useState<StackSummary[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [yaml, setYaml] = useState('');
  const [original, setOriginal] = useState('');
  const [showDiff, setShowDiff] = useState(false);
  const [converterInput, setConverterInput] = useState('docker run -d --name web -p 8080:80 -e TZ=UTC nginx:latest');
  const [converterOutput, setConverterOutput] = useState('');
  const [k8sOutput, setK8sOutput] = useState('');

  const load = useCallback(() => {
    api.stacks().then(setStacks).catch((e) => notify((e as Error).message));
  }, [notify]);

  useEffect(load, [load]);

  const open = useCallback(
    async (name: string) => {
      try {
        const result = await api.stack(name);
        setSelected(name);
        setYaml(result.yaml);
        setOriginal(result.yaml);
        setShowDiff(false);
      } catch (error) {
        notify((error as Error).message);
      }
    },
    [notify]
  );

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <h1 className="text-xl font-semibold">Compose stacks</h1>
        <button
          className="btn-ghost ml-auto"
          onClick={async () => {
            const name = window.prompt('New stack name');
            if (!name) return;
            await api.saveStack(name, { yaml: `name: ${name}\nservices:\n  app:\n    image: nginx:latest\n    ports:\n      - "8080:80"\n` });
            load();
            void open(name);
          }}
        >
          + New stack
        </button>
      </div>

      <div className="grid gap-4 lg:grid-cols-[280px_1fr]">
        <div className="card divide-y divide-ink-800/70 p-0">
          {stacks.map((stack) => (
            <button
              key={stack.name}
              onClick={() => open(stack.name)}
              className={classNames(
                'flex w-full items-center gap-2 px-3 py-2 text-left text-sm',
                selected === stack.name ? 'bg-accent-muted/40' : 'hover:bg-ink-800/60'
              )}
            >
              <span className="truncate">{stack.name}</span>
              <StatusBadge status={stack.state} />
              {stack.autoUpdateEnabled && <span className="badge bg-cyan-950/60 text-cyan-300">auto</span>}
            </button>
          ))}
          {stacks.length === 0 && <div className="p-4 text-sm text-slate-500">No stacks yet.</div>}
        </div>

        <div className="space-y-4">
          {selected ? (
            <div className="card p-4">
              <div className="mb-3 flex flex-wrap items-center gap-2">
                <h2 className="text-sm font-semibold">{selected}</h2>
                <button className="btn-primary px-2 py-1 text-xs" onClick={() => api.deployStack(selected).then(load)}>
                  Deploy
                </button>
                {['start', 'stop', 'restart', 'down', 'update'].map((action) => (
                  <button
                    key={action}
                    className="btn-ghost px-2 py-1 text-xs"
                    onClick={() => api.stackAction(selected, action).then(load)}
                  >
                    {action}
                  </button>
                ))}
                <button className="btn-ghost ml-auto px-2 py-1 text-xs" onClick={() => setShowDiff((v) => !v)}>
                  {showDiff ? 'Edit YAML' : 'Live diff'}
                </button>
              </div>

              {showDiff ? (
                <pre className="max-h-96 overflow-auto rounded-lg bg-ink-950 p-3 font-mono text-xs">
                  {diffLines(original, yaml).map((line, index) => (
                    <div
                      key={index}
                      className={classNames(
                        line.type === 'add' && 'bg-emerald-950/40 text-emerald-300',
                        line.type === 'del' && 'bg-red-950/40 text-red-300',
                        line.type === 'same' && 'text-slate-400'
                      )}
                    >
                      {line.type === 'add' ? '+ ' : line.type === 'del' ? '- ' : '  '}
                      {line.text}
                    </div>
                  ))}
                </pre>
              ) : (
                <textarea
                  className="input h-96 font-mono text-xs"
                  value={yaml}
                  onChange={(e) => setYaml(e.target.value)}
                  spellCheck={false}
                />
              )}

              <div className="mt-3 flex flex-wrap items-center gap-2">
                <button
                  className="btn-primary px-3 py-1.5 text-xs"
                  onClick={async () => {
                    await api.saveStack(selected, { yaml });
                    setOriginal(yaml);
                    load();
                    notify('Stack saved');
                  }}
                >
                  Save
                </button>
                <button
                  className="btn-danger px-3 py-1.5 text-xs"
                  onClick={async () => {
                    if (!window.confirm(`Delete ${selected}?`)) return;
                    await api.deleteStack(selected);
                    setSelected(null);
                    load();
                  }}
                >
                  Delete
                </button>
                <button
                  className="btn-ghost px-3 py-1.5 text-xs"
                  onClick={async () => {
                    const result = await api.stack(selected);
                    setYaml(result.yaml);
                  }}
                >
                  Reload from disk
                </button>
              </div>

              <AutoUpdateConfig
                stack={stacks.find((s) => s.name === selected)}
                onSave={async (payload) => {
                  await api.setAutoUpdate(selected, payload);
                  load();
                  notify('Auto-update saved');
                }}
              />
            </div>
          ) : (
            <div className="card p-6 text-sm text-slate-500">Select a stack to edit, deploy and monitor.</div>
          )}

          <div className="card p-4">
            <h2 className="mb-3 text-sm font-semibold">3-way converter</h2>
            <label className="label">docker run → docker-compose</label>
            <textarea
              className="input h-20 font-mono text-xs"
              value={converterInput}
              onChange={(e) => setConverterInput(e.target.value)}
            />
            <div className="mt-2 flex gap-2">
              <button
                className="btn-ghost px-3 py-1.5 text-xs"
                onClick={async () => setConverterOutput((await api.convertDockerRun(converterInput)).yaml)}
              >
                → compose
              </button>
              <button
                className="btn-ghost px-3 py-1.5 text-xs"
                onClick={async () => setConverterOutput(await api.convertToK8s({ source: converterOutput }))}
              >
                compose → K8s
              </button>
            </div>
            <pre className="mt-2 max-h-60 overflow-auto rounded-lg bg-ink-950 p-3 font-mono text-xs text-slate-300">
              {converterOutput || '// output appears here'}
            </pre>
            <div className="mt-2 flex gap-2">
              <button
                className="btn-ghost px-3 py-1.5 text-xs"
                onClick={async () => setK8sOutput(await api.convertToK8s({ name: selected ?? '' }))}
              >
                Export selected stack to Kubernetes
              </button>
              <button
                className="btn-ghost px-3 py-1.5 text-xs"
                onClick={() => {
                  const url = window.prompt('Compose URL to import');
                  const name = window.prompt('Stack name');
                  if (url && name) api.importStack(name, url).then(load);
                }}
              >
                Import from URL
              </button>
            </div>
            {k8sOutput && (
              <pre className="mt-2 max-h-60 overflow-auto rounded-lg bg-ink-950 p-3 font-mono text-xs text-slate-300">
                {k8sOutput}
              </pre>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

function AutoUpdateConfig({
  stack,
  onSave
}: {
  stack?: StackSummary;
  onSave: (payload: Record<string, unknown>) => void;
}) {
  const [enabled, setEnabled] = useState(stack?.autoUpdateEnabled ?? false);
  const [interval, setInterval] = useState(stack?.autoUpdateIntervalMinutes ?? 1440);
  const [prune, setPrune] = useState(stack?.pruneOldImages ?? true);
  const [rollback, setRollback] = useState(stack?.rollbackOnFailure ?? true);

  useEffect(() => {
    setEnabled(stack?.autoUpdateEnabled ?? false);
    setInterval(stack?.autoUpdateIntervalMinutes ?? 1440);
    setPrune(stack?.pruneOldImages ?? true);
    setRollback(stack?.rollbackOnFailure ?? true);
  }, [stack]);

  return (
    <div className="mt-4 rounded-lg border border-ink-700 bg-ink-900/50 p-3">
      <div className="mb-2 text-sm font-semibold">Auto-updater (25s health guard)</div>
      <div className="flex flex-wrap items-center gap-4 text-sm">
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Enabled
        </label>
        <label className="flex items-center gap-2">
          Interval
          <input
            className="input w-24 py-1"
            type="number"
            value={interval}
            onChange={(e) => setInterval(Number(e.target.value))}
          />
          min
        </label>
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={prune} onChange={(e) => setPrune(e.target.checked)} /> Prune old images
        </label>
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={rollback} onChange={(e) => setRollback(e.target.checked)} /> Rollback on failure
        </label>
        <button
          className="btn-primary px-3 py-1.5 text-xs"
          onClick={() =>
            onSave({ enabled, intervalMinutes: interval, pruneOldImages: prune, rollbackOnFailure: rollback })
          }
        >
          Save
        </button>
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 4 — Containers
// ────────────────────────────────────────────────────────────────────────────
function ContainersView({
  activeHost,
  notify,
  openTerminal,
  inspect
}: {
  activeHost?: number;
  notify: (m: string) => void;
  openTerminal: (title: string, url: string) => void;
  inspect: (id: string) => void;
}) {
  const [containers, setContainers] = useState<DockerContainerSummary[]>([]);
  const [query, setQuery] = useState('');

  const load = useCallback(() => {
    api.containers(activeHost).then(setContainers).catch((e) => notify((e as Error).message));
  }, [activeHost, notify]);

  useEffect(() => {
    load();
    const timer = window.setInterval(load, 5000);
    return () => window.clearInterval(timer);
  }, [load]);

  const filtered = containers.filter((c) =>
    `${c.Names?.[0] ?? ''} ${c.Image}`.toLowerCase().includes(query.toLowerCase())
  );

  const act = async (id: string, action: string) => {
    try {
      await api.containerAction(id, action, activeHost);
      load();
    } catch (error) {
      notify((error as Error).message);
    }
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <h1 className="text-xl font-semibold">Containers</h1>
        <input
          className="input ml-auto w-full max-w-xs"
          placeholder="Filter containers…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <button className="btn-ghost" onClick={load}>
          Refresh
        </button>
      </div>

      <div className="card overflow-x-auto p-0">
        <table className="w-full min-w-[720px] text-sm">
          <thead className="text-left text-xs uppercase tracking-wide text-slate-500">
            <tr>
              <th className="px-3 py-2">Name</th>
              <th className="px-3 py-2">Image</th>
              <th className="px-3 py-2">State</th>
              <th className="px-3 py-2">Status</th>
              <th className="px-3 py-2 text-right">Actions</th>
            </tr>
          </thead>
          <tbody>
            {filtered.map((container) => (
              <tr key={container.Id} className="table-row">
                <td className="px-3 py-2 font-medium">{(container.Names?.[0] ?? container.Id.slice(0, 12)).replace('/', '')}</td>
                <td className="px-3 py-2 text-slate-400">{container.Image}</td>
                <td className="px-3 py-2">
                  <StatusBadge status={container.State} />
                </td>
                <td className="px-3 py-2 text-xs text-slate-500">{container.Status}</td>
                <td className="px-3 py-2">
                  <div className="flex justify-end gap-1">
                    <button className="btn-ghost px-2 py-1 text-xs" onClick={() => inspect(container.Id)}>
                      Inspect
                    </button>
                    <button
                      className="btn-ghost px-2 py-1 text-xs"
                      onClick={() =>
                        openTerminal(
                          (container.Names?.[0] ?? container.Id.slice(0, 12)).replace('/', ''),
                          api.execUrl(container.Id, '/bin/sh')
                        )
                      }
                    >
                      Exec
                    </button>
                    <button className="btn-ghost px-2 py-1 text-xs" onClick={() => act(container.Id, 'restart')}>
                      Restart
                    </button>
                    <button
                      className="btn-ghost px-2 py-1 text-xs"
                      onClick={() => act(container.Id, container.State === 'running' ? 'stop' : 'start')}
                    >
                      {container.State === 'running' ? 'Stop' : 'Start'}
                    </button>
                  </div>
                </td>
              </tr>
            ))}
            {filtered.length === 0 && (
              <tr>
                <td colSpan={5} className="px-3 py-6 text-center text-slate-500">
                  No containers.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function ContainerInspector({
  id,
  onClose,
  notify,
  openTerminal
}: {
  id: string;
  onClose: () => void;
  notify: (m: string) => void;
  openTerminal: (title: string, url: string) => void;
}) {
  const [tab, setTab] = useState<'stats' | 'logs' | 'files'>('stats');
  const [cpu, setCpu] = useState<number[]>([]);
  const [ram, setRam] = useState<number[]>([]);
  const [logs, setLogs] = useState<{ stream: string; line: string }[]>([]);
  const [filter, setFilter] = useState('');
  const [path, setPath] = useState('/');
  const [entries, setEntries] = useState<TarEntryInfo[]>([]);
  const [cves, setCves] = useState<CveFinding[]>([]);

  useEffect(() => {
    if (tab !== 'stats') return undefined;
    const source = new EventSource(api.statsUrl(id), { withCredentials: true });
    source.onmessage = (event) => {
      try {
        const sample = JSON.parse(event.data) as { cpuPercent?: number; memoryMb?: number; error?: string };
        if (sample.error) return;
        setCpu((prev) => [...prev, sample.cpuPercent ?? 0].slice(-60));
        setRam((prev) => [...prev, sample.memoryMb ?? 0].slice(-60));
      } catch {
        /* ignore */
      }
    };
    return () => source.close();
  }, [id, tab]);

  useEffect(() => {
    if (tab !== 'logs') return undefined;
    const source = new EventSource(api.logsUrl(id, 300), { withCredentials: true });
    source.onmessage = (event) => {
      try {
        setLogs((prev) => [...prev, JSON.parse(event.data) as { stream: string; line: string }].slice(-500));
      } catch {
        /* ignore */
      }
    };
    return () => source.close();
  }, [id, tab]);

  const loadFiles = useCallback(
    (target: string) => {
      api
        .archiveList(id, target)
        .then((list) => {
          setEntries(list);
          setPath(target);
        })
        .catch((e) => notify((e as Error).message));
    },
    [id, notify]
  );

  const visibleLogs = logs.filter((entry) => {
    if (!filter) return true;
    try {
      return new RegExp(filter, 'i').test(entry.line);
    } catch {
      return entry.line.toLowerCase().includes(filter.toLowerCase());
    }
  });

  return (
    <div className="fixed inset-0 z-40 flex justify-end bg-black/50" onClick={onClose}>
      <div className="flex h-full w-full max-w-2xl flex-col border-l border-ink-700 bg-ink-900" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center gap-2 border-b border-ink-800 px-4 py-3">
          <h2 className="text-sm font-semibold">Container {id.slice(0, 12)}</h2>
          <div className="ml-auto flex gap-1">
            {(['stats', 'logs', 'files'] as const).map((key) => (
              <button
                key={key}
                className={classNames('btn-ghost px-2 py-1 text-xs', tab === key && 'bg-accent-muted/50')}
                onClick={() => setTab(key)}
              >
                {key}
              </button>
            ))}
            <button className="btn-ghost px-2 py-1 text-xs" onClick={() => openTerminal(id.slice(0, 12), api.execUrl(id, '/bin/sh'))}>
              Exec
            </button>
            <button className="btn-ghost px-2 py-1 text-xs" onClick={onClose}>
              Close
            </button>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto p-4">
          {tab === 'stats' && (
            <div className="space-y-4">
              <div className="card p-3">
                <div className="flex items-baseline justify-between text-xs text-slate-400">
                  <span>CPU</span>
                  <span>{(cpu[cpu.length - 1] ?? 0).toFixed(1)}%</span>
                </div>
                <Sparkline data={cpu} stroke="#38bdf8" height={70} />
              </div>
              <div className="card p-3">
                <div className="flex items-baseline justify-between text-xs text-slate-400">
                  <span>Memory</span>
                  <span>{(ram[ram.length - 1] ?? 0).toFixed(0)} MB</span>
                </div>
                <Sparkline data={ram} stroke="#34d399" height={70} />
              </div>
              <button
                className="btn-ghost w-full"
                onClick={async () => setCves(await api.cveScan(id))}
              >
                Run agentless CVE scan (OSV.dev)
              </button>
              {cves.map((cve) => (
                <div key={cve.id + cve.package} className="rounded border border-red-900/50 bg-red-950/30 p-2 text-xs">
                  <span className="text-red-300">{cve.id}</span> {cve.package} {cve.version}
                </div>
              ))}
            </div>
          )}

          {tab === 'logs' && (
            <div className="space-y-2">
              <input className="input" placeholder="Regex/ANSI filter…" value={filter} onChange={(e) => setFilter(e.target.value)} />
              <pre className="max-h-[60vh] overflow-auto rounded-lg bg-ink-950 p-3 font-mono text-xs">
                {visibleLogs.map((entry, index) => (
                  <div key={index} className={entry.stream === 'stderr' ? 'text-amber-300' : 'text-slate-300'}>
                    {entry.line}
                  </div>
                ))}
                {visibleLogs.length === 0 && <span className="text-slate-600">Waiting for log output…</span>}
              </pre>
            </div>
          )}

          {tab === 'files' && (
            <div className="space-y-2">
              <div className="flex gap-2">
                <input className="input font-mono text-xs" value={path} onChange={(e) => setPath(e.target.value)} />
                <button className="btn-ghost" onClick={() => loadFiles(path)}>
                  Browse
                </button>
              </div>
              <button className="btn-ghost px-2 py-1 text-xs" onClick={() => loadFiles(path === '/' ? '/' : path.split('/').slice(0, -1).join('/') || '/')}>
                ↑ Up
              </button>
              <div className="divide-y divide-ink-800 rounded-lg border border-ink-800">
                {entries.map((entry) => (
                  <div key={entry.name} className="flex items-center gap-2 px-3 py-1.5 text-sm">
                    <span>{entry.isDirectory ? '📁' : '📄'}</span>
                    <button
                      className="truncate text-left hover:text-accent"
                      onClick={() =>
                        entry.isDirectory
                          ? loadFiles(entry.name)
                          : window.open(api.archiveFileUrl(id, entry.name), '_blank')
                      }
                    >
                      {entry.name}
                    </button>
                    <span className="ml-auto text-xs text-slate-500">{entry.isDirectory ? '' : `${entry.size} B`}</span>
                  </div>
                ))}
                {entries.length === 0 && <div className="px-3 py-3 text-sm text-slate-500">Browse a path to list files.</div>}
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 5 — Kubernetes
// ────────────────────────────────────────────────────────────────────────────
function KubernetesView({
  notify,
  openTerminal
}: {
  notify: (m: string) => void;
  openTerminal: (title: string, url: string) => void;
}) {
  const [clusters, setClusters] = useState<KubeClusterInfo[]>([]);
  const [active, setActive] = useState<number | null>(null);
  const [pods, setPods] = useState<Record<string, unknown>[]>([]);
  const [deployments, setDeployments] = useState<Record<string, unknown>[]>([]);
  const [namespace, setNamespace] = useState('default');
  const [manifest, setManifest] = useState('apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: demo\n');
  const [kubeconfig, setKubeconfig] = useState('');

  const loadClusters = useCallback(() => {
    api.clusters().then((list) => {
      setClusters(list);
      if (list.length > 0 && active === null) setActive(list[0].id);
    });
  }, [active]);

  useEffect(loadClusters, [loadClusters]);

  const loadWorkloads = useCallback(async () => {
    if (active === null) return;
    try {
      const [podResult, deploymentResult] = await Promise.all([
        api.pods(active, namespace),
        api.deployments(active, namespace)
      ]);
      setPods((podResult.items as Record<string, unknown>[]) ?? []);
      setDeployments((deploymentResult.items as Record<string, unknown>[]) ?? []);
    } catch (error) {
      notify((error as Error).message);
    }
  }, [active, namespace, notify]);

  useEffect(() => {
    void loadWorkloads();
  }, [loadWorkloads]);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-xl font-semibold">Kubernetes</h1>
        <select className="input w-48" value={active ?? ''} onChange={(e) => setActive(e.target.value ? Number(e.target.value) : null)}>
          <option value="">select cluster…</option>
          {clusters.map((cluster) => (
            <option key={cluster.id} value={cluster.id}>
              {cluster.name}
            </option>
          ))}
        </select>
        <input className="input w-40" value={namespace} onChange={(e) => setNamespace(e.target.value)} placeholder="namespace" />
        <button className="btn-ghost" onClick={loadWorkloads}>
          Refresh
        </button>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <div className="card p-4">
          <h2 className="mb-2 text-sm font-semibold">Clusters</h2>
          <div className="space-y-1">
            {clusters.map((cluster) => (
              <div key={cluster.id} className="flex items-center gap-2 text-sm">
                <span>{cluster.name}</span>
                <span className="text-xs text-slate-500">{cluster.serverUrl}</span>
                <button
                  className="btn-ghost ml-auto px-2 py-1 text-xs"
                  onClick={async () => notify((await api.testCluster(cluster.id)).online ? 'Online' : 'Unreachable')}
                >
                  Test
                </button>
                <button className="btn-danger px-2 py-1 text-xs" onClick={() => api.deleteCluster(cluster.id).then(loadClusters)}>
                  Delete
                </button>
              </div>
            ))}
          </div>
          <div className="mt-3 space-y-2">
            <label className="label">Import kubeconfig</label>
            <textarea className="input h-28 font-mono text-xs" value={kubeconfig} onChange={(e) => setKubeconfig(e.target.value)} />
            <div className="flex gap-2">
              <input className="input w-40" placeholder="cluster name" id="kube-name" />
              <button
                className="btn-primary"
                onClick={async () => {
                  const name = (document.getElementById('kube-name') as HTMLInputElement)?.value || 'cluster';
                  await api.saveCluster({ name, enabled: true, kubeConfigYaml: kubeconfig, serverUrl: '' });
                  setKubeconfig('');
                  loadClusters();
                }}
              >
                Import
              </button>
            </div>
          </div>
        </div>

        <div className="card p-4">
          <h2 className="mb-2 text-sm font-semibold">Pods ({pods.length})</h2>
          <div className="max-h-64 overflow-y-auto text-xs">
            {pods.map((pod, index) => {
              const meta = pod.metadata as Record<string, unknown> | undefined;
              const status = pod.status as Record<string, unknown> | undefined;
              const name = (meta?.name as string) ?? `pod-${index}`;
              const phase = (status?.phase as string) ?? 'Unknown';
              return (
                <div key={name} className="flex items-center gap-2 border-b border-ink-800/60 py-1">
                  <span className="truncate">{name}</span>
                  <StatusBadge status={phase} />
                  <button
                    className="btn-ghost ml-auto px-2 py-0.5 text-[11px]"
                    onClick={() =>
                      openTerminal(
                        name,
                        `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/k8s-exec/${active}/${namespace}/${name}`
                      )
                    }
                  >
                    Exec
                  </button>
                </div>
              );
            })}
            {pods.length === 0 && <div className="py-3 text-slate-500">No pods.</div>}
          </div>
        </div>
      </div>

      <div className="card p-4">
        <h2 className="mb-2 text-sm font-semibold">Deployments ({deployments.length})</h2>
        <div className="space-y-1 text-xs">
          {deployments.map((deployment, index) => {
            const meta = deployment.metadata as Record<string, unknown> | undefined;
            const spec = deployment.spec as Record<string, unknown> | undefined;
            const name = (meta?.name as string) ?? `deployment-${index}`;
            return (
              <div key={name} className="flex items-center gap-2 border-b border-ink-800/60 py-1">
                <span className="truncate">{name}</span>
                <span className="text-slate-500">replicas {String(spec?.replicas ?? '?')}</span>
                <div className="ml-auto flex gap-1">
                  <button
                    className="btn-ghost px-2 py-0.5 text-[11px]"
                    onClick={() => active && api.restartDeployment(active, namespace, name).then(loadWorkloads)}
                  >
                    Rollout restart
                  </button>
                  <button
                    className="btn-ghost px-2 py-0.5 text-[11px]"
                    onClick={() => {
                      const value = Number(window.prompt('Replicas', '1') ?? '1');
                      if (active) void api.scaleDeployment(active, namespace, name, value).then(loadWorkloads);
                    }}
                  >
                    Scale
                  </button>
                </div>
              </div>
            );
          })}
          {deployments.length === 0 && <div className="py-3 text-slate-500">No deployments.</div>}
        </div>

        <div className="mt-4">
          <label className="label">Deploy manifest (Compose → K8s compatible)</label>
          <textarea className="input h-40 font-mono text-xs" value={manifest} onChange={(e) => setManifest(e.target.value)} />
          <button
            className="btn-primary mt-2"
            onClick={async () => {
              if (active === null) return;
              const result = await api.applyManifest(active, manifest, namespace);
              notify(`Applied: ${result.applied.join(', ') || 'nothing'}`);
              loadWorkloads();
            }}
          >
            Apply
          </button>
        </div>
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 6 — Images & registries
// ────────────────────────────────────────────────────────────────────────────
function ImagesAndRegistriesView({ activeHost, notify }: { activeHost?: number; notify: (m: string) => void }) {
  const [images, setImages] = useState<{ Id: string; RepoTags?: string[]; Size: number; Containers: number }[]>([]);
  const [registries, setRegistries] = useState<RegistryInfo[]>([]);
  const [pullImage, setPullImage] = useState('nginx:latest');
  const [pullProgress, setPullProgress] = useState<string[]>([]);

  const load = useCallback(() => {
    api.images(activeHost).then(setImages).catch(() => undefined);
    api.registries().then(setRegistries).catch(() => undefined);
  }, [activeHost]);

  useEffect(load, [load]);

  const startPull = () => {
    const [name, tag] = pullImage.includes(':') ? pullImage.split(':') : [pullImage, 'latest'];
    const source = new EventSource(api.pullUrl(name, tag), { withCredentials: true });
    setPullProgress([]);
    source.onmessage = (event) => {
      setPullProgress((prev) => [...prev, event.data].slice(-40));
      if (event.data.includes('"done":true')) {
        source.close();
        load();
      }
    };
  };

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold">Images & registries</h1>

      <div className="card flex flex-wrap items-center gap-2 p-3">
        <input className="input flex-1" value={pullImage} onChange={(e) => setPullImage(e.target.value)} placeholder="image:tag" />
        <button className="btn-primary" onClick={startPull}>
          Pull image
        </button>
        <button
          className="btn-ghost"
          onClick={async () => {
            const result = await api.pruneImages(false);
            notify(`Pruned; reclaimed ${((result.SpaceReclaimed ?? 0) / 1024 / 1024).toFixed(1)} MB`);
            load();
          }}
        >
          Prune unused images
        </button>
      </div>

      {pullProgress.length > 0 && (
        <pre className="card max-h-40 overflow-auto p-3 font-mono text-[11px] text-slate-400">{pullProgress.join('\n')}</pre>
      )}

      <div className="card overflow-x-auto p-0">
        <table className="w-full min-w-[560px] text-sm">
          <thead className="text-left text-xs uppercase tracking-wide text-slate-500">
            <tr>
              <th className="px-3 py-2">Repository</th>
              <th className="px-3 py-2">Size</th>
              <th className="px-3 py-2">Containers</th>
              <th className="px-3 py-2 text-right">Actions</th>
            </tr>
          </thead>
          <tbody>
            {images.map((image) => (
              <tr key={image.Id} className="table-row">
                <td className="px-3 py-2 font-mono text-xs">{(image.RepoTags ?? ['<none>']).join(', ')}</td>
                <td className="px-3 py-2 text-slate-400">{(image.Size / 1024 / 1024).toFixed(1)} MB</td>
                <td className="px-3 py-2">
                  {image.Containers === 0 ? (
                    <span className="badge bg-amber-950/60 text-amber-300">unused</span>
                  ) : (
                    <span className="text-slate-400">{image.Containers}</span>
                  )}
                </td>
                <td className="px-3 py-2 text-right">
                  <button
                    className="btn-ghost px-2 py-1 text-xs"
                    onClick={() => api.removeImage(image.Id).then(load)}
                  >
                    Remove
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <RegistryManager registries={registries} reload={load} notify={notify} />
    </div>
  );
}

function RegistryManager({
  registries,
  reload,
  notify
}: {
  registries: RegistryInfo[];
  reload: () => void;
  notify: (m: string) => void;
}) {
  const [draft, setDraft] = useState({ name: '', url: 'https://index.docker.io/v1/', username: '', password: '', ignoreSslErrors: false, isDefault: false });

  return (
    <div className="card p-4">
      <h2 className="mb-3 text-sm font-semibold">Private registries</h2>
      <div className="space-y-2">
        {registries.map((registry) => (
          <div key={registry.id} className="flex flex-wrap items-center gap-2 text-sm">
            <span className="font-medium">{registry.name}</span>
            <span className="text-xs text-slate-500">{registry.url}</span>
            {registry.ignoreSslErrors && <span className="badge bg-amber-950/60 text-amber-300">ignore SSL</span>}
            {registry.isDefault && <span className="badge bg-sky-950/60 text-sky-300">default</span>}
            <button className="btn-danger ml-auto px-2 py-1 text-xs" onClick={() => api.deleteRegistry(registry.id).then(reload)}>
              Delete
            </button>
          </div>
        ))}
      </div>
      <div className="mt-3 grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
        <input className="input" placeholder="name" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
        <input className="input" placeholder="url" value={draft.url} onChange={(e) => setDraft({ ...draft, url: e.target.value })} />
        <input className="input" placeholder="username" value={draft.username} onChange={(e) => setDraft({ ...draft, username: e.target.value })} />
        <input className="input" type="password" placeholder="password/token" value={draft.password} onChange={(e) => setDraft({ ...draft, password: e.target.value })} />
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={draft.ignoreSslErrors} onChange={(e) => setDraft({ ...draft, ignoreSslErrors: e.target.checked })} />
          Ignore SSL errors
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={draft.isDefault} onChange={(e) => setDraft({ ...draft, isDefault: e.target.checked })} />
          Default
        </label>
      </div>
      <button
        className="btn-primary mt-3"
        onClick={async () => {
          try {
            await api.saveRegistry(draft);
            notify('Registry saved');
            reload();
          } catch (error) {
            notify((error as Error).message);
          }
        }}
      >
        Add registry
      </button>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 7 — Hosts & webhooks
// ────────────────────────────────────────────────────────────────────────────
function HostsAndWebhooksView({ notify, refreshHosts }: { notify: (m: string) => void; refreshHosts: () => void }) {
  const [hosts, setHosts] = useState<DockerHostInfo[]>([]);
  const [webhooks, setWebhooks] = useState<WebhookInfo[]>([]);
  const [enrollment, setEnrollment] = useState<{ compose: string; endpoint: string } | null>(null);

  const load = useCallback(() => {
    api.hosts().then(setHosts).catch(() => undefined);
    api.webhooks().then(setWebhooks).catch(() => undefined);
  }, []);

  useEffect(load, [load]);

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold">Hosts & webhooks</h1>

      <div className="card p-4">
        <div className="mb-3 flex items-center gap-2">
          <h2 className="text-sm font-semibold">Docker hosts</h2>
          <button
            className="btn-ghost ml-auto px-2 py-1 text-xs"
            onClick={async () => {
              const result = await api.enrollAgent('remote-agent', window.prompt('Remote host address') ?? 'agent-host', 2376);
              setEnrollment({ compose: result.compose, endpoint: result.endpoint });
              notify('Agent enrollment generated');
            }}
          >
            Generate mTLS agent enrollment
          </button>
        </div>
        <div className="space-y-2">
          {hosts.map((host) => (
            <div key={host.id} className="flex flex-wrap items-center gap-2 text-sm">
              <span className="font-medium">{host.name}</span>
              <span className="text-xs text-slate-500">{host.endpoint}</span>
              <div className="ml-auto flex gap-1">
                <button className="btn-ghost px-2 py-1 text-xs" onClick={async () => notify((await api.testHost(host.id)).online ? 'Online' : 'Offline')}>
                  Test
                </button>
                <button className="btn-ghost px-2 py-1 text-xs" onClick={async () => notify((await api.wakeHost(host.id)).sent ? 'Magic packet sent' : 'Failed')}>
                  Wake-on-LAN
                </button>
                <button
                  className="btn-danger px-2 py-1 text-xs"
                  onClick={() => api.deleteHost(host.id).then(() => {
                    load();
                    refreshHosts();
                  })}
                >
                  Delete
                </button>
              </div>
            </div>
          ))}
        </div>
        {enrollment && (
          <div className="mt-3">
            <div className="text-xs text-slate-400">Endpoint: {enrollment.endpoint}</div>
            <pre className="mt-1 max-h-72 overflow-auto rounded-lg bg-ink-950 p-3 font-mono text-[11px] text-slate-300">
              {enrollment.compose}
            </pre>
          </div>
        )}
      </div>

      <WebhookManager webhooks={webhooks} reload={load} notify={notify} />
    </div>
  );
}

function WebhookManager({
  webhooks,
  reload,
  notify
}: {
  webhooks: WebhookInfo[];
  reload: () => void;
  notify: (m: string) => void;
}) {
  const [draft, setDraft] = useState({
    name: '',
    kind: 'Discord',
    url: '',
    chatId: '',
    secret: '',
    events: 'stack.deploy,stack.update,stack.rollback,cve.alert,user.pending'
  });

  return (
    <div className="card p-4">
      <h2 className="mb-3 text-sm font-semibold">Webhooks</h2>
      <div className="space-y-2">
        {webhooks.map((hook) => (
          <div key={hook.id} className="flex flex-wrap items-center gap-2 text-sm">
            <span className="font-medium">{hook.name}</span>
            <span className="badge bg-ink-800 text-slate-300">{hook.kind}</span>
            <span className="truncate text-xs text-slate-500">{hook.events}</span>
            {hook.lastStatus && <span className="text-xs text-slate-500">{hook.lastStatus}</span>}
            <div className="ml-auto flex gap-1">
              <button className="btn-ghost px-2 py-1 text-xs" onClick={async () => notify((await api.testWebhook(hook.id)).lastStatus)}>
                Test
              </button>
              <button className="btn-danger px-2 py-1 text-xs" onClick={() => api.deleteWebhook(hook.id).then(reload)}>
                Delete
              </button>
            </div>
          </div>
        ))}
      </div>
      <div className="mt-3 grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
        <input className="input" placeholder="name" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
        <select className="input" value={draft.kind} onChange={(e) => setDraft({ ...draft, kind: e.target.value })}>
          {['Discord', 'Telegram', 'Slack', 'Ntfy', 'Gotify', 'GenericHmac'].map((kind) => (
            <option key={kind}>{kind}</option>
          ))}
        </select>
        <input className="input" placeholder="webhook url" value={draft.url} onChange={(e) => setDraft({ ...draft, url: e.target.value })} />
        <input className="input" placeholder="telegram chat id (optional)" value={draft.chatId} onChange={(e) => setDraft({ ...draft, chatId: e.target.value })} />
        <input className="input" placeholder="secret / token (optional)" value={draft.secret} onChange={(e) => setDraft({ ...draft, secret: e.target.value })} />
        <input className="input" placeholder="events" value={draft.events} onChange={(e) => setDraft({ ...draft, events: e.target.value })} />
      </div>
      <button
        className="btn-primary mt-3"
        onClick={async () => {
          try {
            await api.saveWebhook(draft);
            notify('Webhook saved');
            reload();
          } catch (error) {
            notify((error as Error).message);
          }
        }}
      >
        Add webhook
      </button>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 8 — Security & SSO
// ────────────────────────────────────────────────────────────────────────────
function SecurityAndSsoView({ notify }: { notify: (m: string) => void }) {
  const [passkeys, setPasskeys] = useState<PasskeyInfo[]>([]);
  const [pending, setPending] = useState<UserInfo[]>([]);
  const [users, setUsers] = useState<UserInfo[]>([]);
  const [keys, setKeys] = useState<McpKeyInfo[]>([]);
  const [totpSetup, setTotpSetup] = useState<{ secret: string; qrSvg: string; otpauthUri: string } | null>(null);
  const [totpCode, setTotpCode] = useState('');
  const [samlSp, setSamlSp] = useState<Record<string, unknown>>({});
  const [oidcGoogle, setOidcGoogle] = useState<Record<string, unknown>>({});
  const [dnsNames, setDnsNames] = useState('dock.lan,*.dock.lan');
  const [ips, setIps] = useState('127.0.0.1');

  const load = useCallback(() => {
    api.passkeys().then(setPasskeys).catch(() => undefined);
    api.users().then(setUsers).catch(() => undefined);
    api.pendingUsers().then(setPending).catch(() => undefined);
    api.mcpKeys().then(setKeys).catch(() => undefined);
    api.samlSp().then(setSamlSp).catch(() => undefined);
    api.oidcProvider('google').then(setOidcGoogle).catch(() => undefined);
  }, []);

  useEffect(load, [load]);

  const registerPasskey = async () => {
    try {
      const begin = await api.passkeyRegisterBegin();
      const credential = await navigator.credentials.create({
        publicKey: decodeCredentialCreationOptions(begin.options)
      });
      if (!credential) return;
      await api.passkeyRegisterComplete(begin.flowId, encodeCredential(credential as PublicKeyCredential), 'Passkey');
      notify('Passkey registered');
      load();
    } catch (error) {
      notify((error as Error).message);
    }
  };

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold">Security & SSO</h1>

      <div className="grid gap-4 lg:grid-cols-2">
        <div className="card p-4">
          <h2 className="mb-3 text-sm font-semibold">Passkeys (WebAuthn)</h2>
          <div className="space-y-1">
            {passkeys.map((passkey) => (
              <div key={passkey.id} className="flex items-center gap-2 text-sm">
                <span>🪪 {passkey.name}</span>
                <span className="text-xs text-slate-500">{new Date(passkey.createdAt).toLocaleDateString()}</span>
                <button className="btn-danger ml-auto px-2 py-1 text-xs" onClick={() => api.deletePasskey(passkey.id).then(load)}>
                  Remove
                </button>
              </div>
            ))}
            {passkeys.length === 0 && <div className="text-sm text-slate-500">No passkeys registered.</div>}
          </div>
          <button className="btn-primary mt-3" onClick={registerPasskey}>
            Register passkey
          </button>
        </div>

        <div className="card p-4">
          <h2 className="mb-3 text-sm font-semibold">TOTP authenticator</h2>
          {totpSetup ? (
            <div className="space-y-2">
              <div className="rounded-lg bg-white p-3" dangerouslySetInnerHTML={{ __html: totpSetup.qrSvg }} />
              <div className="font-mono text-xs text-slate-400">{totpSetup.secret}</div>
              <div className="flex gap-2">
                <input className="input" placeholder="6-digit code" value={totpCode} onChange={(e) => setTotpCode(e.target.value)} />
                <button
                  className="btn-primary"
                  onClick={async () => {
                    await api.totpEnable(totpCode);
                    notify('TOTP enabled');
                    setTotpSetup(null);
                  }}
                >
                  Enable
                </button>
              </div>
            </div>
          ) : (
            <div className="flex gap-2">
              <button className="btn-primary" onClick={async () => setTotpSetup(await api.totpSetup())}>
                Set up TOTP
              </button>
              <button className="btn-ghost" onClick={() => api.totpDisable().then(() => notify('TOTP disabled'))}>
                Disable
              </button>
            </div>
          )}
        </div>

        <div className="card p-4">
          <h2 className="mb-3 text-sm font-semibold">Google / Microsoft OIDC</h2>
          <label className="label">Google Client ID</label>
          <input
            className="input"
            value={(oidcGoogle.clientId as string) ?? ''}
            onChange={(e) => setOidcGoogle({ ...oidcGoogle, clientId: e.target.value })}
          />
          <label className="label mt-2">Google Client Secret</label>
          <input
            className="input"
            type="password"
            placeholder={oidcGoogle.hasSecret ? '(unchanged)' : ''}
            onChange={(e) => setOidcGoogle({ ...oidcGoogle, clientSecret: e.target.value })}
          />
          <label className="mt-2 flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={Boolean(oidcGoogle.enabled)}
              onChange={(e) => setOidcGoogle({ ...oidcGoogle, enabled: e.target.checked })}
            />
            Enabled
          </label>
          <button
            className="btn-primary mt-3"
            onClick={async () => {
              await api.saveOidcProvider('google', oidcGoogle);
              notify('OIDC saved (restart to apply)');
            }}
          >
            Save Google OIDC
          </button>
        </div>

        <div className="card p-4">
          <h2 className="mb-3 text-sm font-semibold">SAML 2.0 Service Provider</h2>
          <label className="label">SP Entity ID</label>
          <input
            className="input"
            value={(samlSp.entityId as string) ?? ''}
            onChange={(e) => setSamlSp({ ...samlSp, entityId: e.target.value })}
          />
          <label className="label mt-2">IdP Metadata URL</label>
          <input
            className="input"
            value={(samlSp.idpMetadataUrl as string) ?? ''}
            onChange={(e) => setSamlSp({ ...samlSp, idpMetadataUrl: e.target.value })}
          />
          <div className="mt-2 flex items-center gap-2">
            <a className="btn-ghost" href={api.samlMetadataUrl} target="_blank" rel="noreferrer">
              SP metadata
            </a>
            <a className="btn-ghost" href={api.samlIdpMetadataUrl} target="_blank" rel="noreferrer">
              IdP metadata
            </a>
          </div>
          <button
            className="btn-primary mt-3"
            onClick={async () => {
              await api.saveSamlSp(samlSp);
              notify('SAML SP saved');
            }}
          >
            Save SAML SP
          </button>
        </div>

        <div className="card p-4">
          <h2 className="mb-3 text-sm font-semibold">Pending user queue</h2>
          <div className="space-y-1">
            {pending.map((user) => (
              <div key={user.id} className="flex items-center gap-2 text-sm">
                <span>{user.email || user.username}</span>
                <span className="text-xs text-slate-500">{user.provider}</span>
                <button
                  className="btn-ghost ml-auto px-2 py-1 text-xs"
                  onClick={() => api.approveUser(user.id, 'Viewer').then(load)}
                >
                  Approve as Viewer
                </button>
                <button className="btn-primary px-2 py-1 text-xs" onClick={() => api.approveUser(user.id, 'Admin').then(load)}>
                  Approve as Admin
                </button>
              </div>
            ))}
            {pending.length === 0 && <div className="text-sm text-slate-500">No pending registrations.</div>}
          </div>
          <div className="mt-3 text-xs uppercase tracking-wide text-slate-500">All users ({users.length})</div>
        </div>

        <div className="card p-4">
          <h2 className="mb-3 text-sm font-semibold">MCP API keys</h2>
          <div className="space-y-1">
            {keys.map((key) => (
              <div key={key.id} className="flex items-center gap-2 text-sm">
                <span>{key.name}</span>
                <code className="text-xs text-slate-500">{key.prefix}…</code>
                <span className="badge bg-ink-800 text-slate-300">{key.scopes}</span>
                <button className="btn-danger ml-auto px-2 py-1 text-xs" onClick={() => api.deleteMcpKey(key.id).then(load)}>
                  Revoke
                </button>
              </div>
            ))}
          </div>
          <button
            className="btn-primary mt-3"
            onClick={async () => {
              const name = window.prompt('Key name') ?? 'mcp';
              const result = await api.createMcpKey(name, 'read,write');
              notify(`Copy now: ${result.token}`);
              load();
            }}
          >
            Create MCP key
          </button>
        </div>

        <div className="card p-4 lg:col-span-2">
          <h2 className="mb-3 text-sm font-semibold">SSL certificate SAN regenerator</h2>
          <div className="grid gap-2 sm:grid-cols-2">
            <div>
              <label className="label">DNS names (comma separated)</label>
              <input className="input" value={dnsNames} onChange={(e) => setDnsNames(e.target.value)} />
            </div>
            <div>
              <label className="label">IP addresses (comma separated)</label>
              <input className="input" value={ips} onChange={(e) => setIps(e.target.value)} />
            </div>
          </div>
          <div className="mt-3 flex gap-2">
            <button
              className="btn-primary"
              onClick={async () => {
                const result = await api.sslRegenerate(
                  dnsNames.split(',').map((s) => s.trim()).filter(Boolean),
                  ips.split(',').map((s) => s.trim()).filter(Boolean)
                );
                notify(`New certificate ${result.thumbprint.slice(0, 12)}… (hot-swapped)`);
              }}
            >
              Regenerate & hot-swap
            </button>
            <a className="btn-ghost" href={api.sslDownloadUrl}>
              Download ca.crt
            </a>
          </div>
        </div>
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// Terminal drawer + mobile touch bar
// ────────────────────────────────────────────────────────────────────────────
function TerminalDrawer({
  tabs,
  open,
  onOpenChange,
  onClose
}: {
  tabs: TerminalTab[];
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onClose: (id: string) => void;
}) {
  const [activeId, setActiveId] = useState<string | null>(null);

  useEffect(() => {
    if (tabs.length > 0 && !tabs.some((t) => t.id === activeId)) setActiveId(tabs[tabs.length - 1].id);
    if (tabs.length === 0) onOpenChange(false);
  }, [tabs, activeId, onOpenChange]);

  if (!open || tabs.length === 0) return null;
  const active = tabs.find((t) => t.id === activeId) ?? tabs[0];

  return (
    <div className="fixed inset-x-0 bottom-0 z-40 h-[55vh] border-t border-ink-700 bg-ink-950 md:left-56">
      <div className="flex items-center gap-1 border-b border-ink-800 px-2 py-1">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            className={classNames(
              'rounded px-2 py-1 text-xs',
              tab.id === active.id ? 'bg-ink-800 text-white' : 'text-slate-400 hover:bg-ink-800/60'
            )}
            onClick={() => setActiveId(tab.id)}
          >
            {tab.title}
            <span
              className="ml-2 text-slate-500 hover:text-red-300"
              onClick={(e) => {
                e.stopPropagation();
                onClose(tab.id);
              }}
            >
              ✕
            </span>
          </button>
        ))}
        <button className="btn-ghost ml-auto px-2 py-1 text-xs" onClick={() => onOpenChange(false)}>
          Hide
        </button>
      </div>
      <TerminalPane key={active.id} tab={active} />
      <MobileTouchBar />
    </div>
  );
}

function TerminalPane({ tab }: { tab: TerminalTab }) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const terminalRef = useRef<Terminal | null>(null);

  useEffect(() => {
    if (!containerRef.current) return undefined;
    const terminal = new Terminal({
      convertEol: true,
      fontFamily: 'ui-monospace, Menlo, Consolas, monospace',
      fontSize: 13,
      theme: { background: '#00000000', foreground: '#e5e7eb', cursor: '#38bdf8' },
      scrollback: 5000
    });
    const fit = new FitAddon();
    terminal.loadAddon(fit);
    terminal.open(containerRef.current);
    fit.fit();
    terminalRef.current = terminal;

    const socket = new WebSocket(tab.url);
    socket.binaryType = 'arraybuffer';
    const encode = (data: string) => new TextEncoder().encode(data);
    const write = (data: string) => socket.readyState === WebSocket.OPEN && socket.send(encode(data));

    socket.onmessage = (event) => {
      if (typeof event.data === 'string') terminal.write(event.data);
      else terminal.write(new Uint8Array(event.data as ArrayBuffer));
    };
    socket.onopen = () => {
      terminal.onData((data) => write(data));
      fit.fit();
    };
    socket.onclose = () => terminal.write('\r\n\x1b[31m[disconnected]\x1b[0m\r\n');

    const onResize = () => fit.fit();
    window.addEventListener('resize', onResize);
    return () => {
      window.removeEventListener('resize', onResize);
      socket.close();
      terminal.dispose();
    };
  }, [tab]);

  return <div ref={containerRef} className="h-[calc(55vh-5.5rem)] w-full p-1" />;
}

function MobileTouchBar() {
  const keys: { label: string; send: string }[] = [
    { label: 'ESC', send: '\x1b' },
    { label: 'TAB', send: '\t' },
    { label: '^C', send: '\x03' },
    { label: '^D', send: '\x04' },
    { label: '^L', send: '\x0c' },
    { label: '↑', send: '\x1b[A' },
    { label: '↓', send: '\x1b[B' },
    { label: '←', send: '\x1b[D' },
    { label: '→', send: '\x1b[C' },
    { label: '/', send: '/' },
    { label: '-', send: '-' },
    { label: '|', send: '|' }
  ];
  const sendKey = (data: string) => {
    // xterm focuses the hidden textarea; dispatch a synthetic input is not reliable, so we type into the active element.
    const active = document.activeElement as HTMLTextAreaElement | null;
    if (active && active.classList.contains('xterm-helper-textarea')) {
      active.value += data;
      active.dispatchEvent(new InputEvent('input', { data }));
      active.value = '';
    }
  };
  return (
    <div className="flex gap-1 overflow-x-auto border-t border-ink-800 bg-ink-900 px-2 py-1 md:hidden">
      {keys.map((key) => (
        <button
          key={key.label}
          className="btn-ghost shrink-0 px-2 py-1 text-xs"
          onMouseDown={(e) => {
            e.preventDefault();
            sendKey(key.send);
          }}
        >
          {key.label}
        </button>
      ))}
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// Modal helper + WebAuthn encoding helpers
// ────────────────────────────────────────────────────────────────────────────
function Modal({ title, children, onClose }: { title: string; children: React.ReactNode; onClose: () => void }) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4" onClick={onClose}>
      <div className="card w-full max-w-lg p-5" onClick={(e) => e.stopPropagation()}>
        <h2 className="mb-4 text-sm font-semibold">{title}</h2>
        {children}
      </div>
    </div>
  );
}

function base64UrlToBuffer(value: string): ArrayBuffer {
  const padding = '='.repeat((4 - (value.length % 4)) % 4);
  const base64 = (value + padding).replace(/-/g, '+').replace(/_/g, '/');
  const raw = atob(base64);
  const buffer = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i += 1) buffer[i] = raw.charCodeAt(i);
  return buffer.buffer;
}

function bufferToBase64Url(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (let i = 0; i < bytes.length; i += 1) binary += String.fromCharCode(bytes[i]);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function decodeCredentialCreationOptions(options: unknown): PublicKeyCredentialCreationOptions {
  const o = options as Record<string, any>;
  return {
    ...o,
    challenge: base64UrlToBuffer(o.challenge),
    user: { ...o.user, id: base64UrlToBuffer(o.user.id) },
    excludeCredentials: (o.excludeCredentials ?? []).map((c: any) => ({ ...c, id: base64UrlToBuffer(c.id) }))
  } as PublicKeyCredentialCreationOptions;
}

function decodePublicKeyRequestOptions(options: unknown): PublicKeyCredentialRequestOptions {
  const o = options as Record<string, any>;
  return {
    ...o,
    challenge: base64UrlToBuffer(o.challenge),
    allowCredentials: (o.allowCredentials ?? []).map((c: any) => ({ ...c, id: base64UrlToBuffer(c.id) }))
  } as PublicKeyCredentialRequestOptions;
}

function encodeCredential(credential: PublicKeyCredential): string {
  const response = credential.response as unknown as {
    attestationObject?: ArrayBuffer;
    clientDataJSON: ArrayBuffer;
    authenticatorData?: ArrayBuffer;
    signature?: ArrayBuffer;
    userHandle?: ArrayBuffer | null;
  };
  const json: Record<string, unknown> = {
    id: credential.id,
    rawId: bufferToBase64Url(credential.rawId),
    type: credential.type
  };
  if (response.attestationObject) {
    json.response = {
      attestationObject: bufferToBase64Url(response.attestationObject),
      clientDataJSON: bufferToBase64Url(response.clientDataJSON)
    };
  } else {
    json.response = {
      authenticatorData: response.authenticatorData ? bufferToBase64Url(response.authenticatorData) : '',
      clientDataJSON: bufferToBase64Url(response.clientDataJSON),
      signature: response.signature ? bufferToBase64Url(response.signature) : '',
      userHandle: response.userHandle ? bufferToBase64Url(response.userHandle) : null
    };
  }
  return JSON.stringify(json);
}
