// File: src/eh.DockManager.Client/src/App.tsx
import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import { api } from './api';
import { Icon, type IconName } from './icons';
import {
  Callout,
  ConfirmButton,
  EmptyState,
  Field,
  InfoTip,
  Modal,
  PageHeader,
  RoleBadge,
  Section,
  Skeleton,
  Sparkline,
  StatTile,
  StatusPill,
  Tabs,
  Toggle,
  YamlEditor,
  classNames,
  Expand,
  MetricRow,
  RefreshControl,
  TrafficChart,
  counterRate,
  formatBytes,
  formatDuration,
  formatNumber,
  formatPercent,
  formatRate,
  formatRelative,
  intervalLabel,
  roleBlurb,
  usePersistentState
} from './ui';
import type { Notify, OpenTerminal } from './ui';
import { lintEnums, mergeIssues, type YamlIssue } from './yaml';
import type {
  AcmeSettings,
  AuditLogEntry,
  CertificateInfo,
  ContainerNetworkHistory,
  CveFinding,
  DashboardContainerInfo,
  DashboardResponse,
  ExtendedContainerMetric,
  HealthInfo,
  NetworkSummary,
  StackStats,
  DockerContainerSummary,
  DockerHostInfo,
  DockerImageSummary,
  KubeClusterInfo,
  LandingResponse,
  McpKeyInfo,
  MetricSample,
  PasskeyInfo,
  PortalApp,
  PortalAppInput,
  PortalCandidate,
  RegistryInfo,
  SessionUser,
  StackSummary,
  SystemInfo,
  SystemSettings,
  TarEntryInfo,
  UserInfo,
  ViewKey,
  WebhookInfo
} from './types';

// ────────────────────────────────────────────────────────────────────────────
// Navigation model
// ────────────────────────────────────────────────────────────────────────────
type NavGroup = 'Overview' | 'Manage' | 'Configure';
const NAV_GROUPS: NavGroup[] = ['Overview', 'Manage', 'Configure'];

interface NavItem {
  key: ViewKey;
  label: string;
  icon: IconName;
  group: NavGroup;
  adminOnly: boolean;
  blurb: string;
}

const NAV: NavItem[] = [
  { key: 'portal', label: 'Portal', icon: 'layoutGrid', group: 'Overview', adminOnly: false,
    blurb: 'The published app launcher and the reverse-proxy front door on port 443.' },
  { key: 'dashboard', label: 'Dashboard', icon: 'gauge', group: 'Overview', adminOnly: false,
    blurb: 'A live health and activity summary. Every tile links to the area it describes.' },
  { key: 'stacks', label: 'Stacks', icon: 'layers', group: 'Manage', adminOnly: true,
    blurb: 'Compose stacks stored on disk: edit YAML, deploy, convert and auto-update.' },
  { key: 'containers', label: 'Containers', icon: 'box', group: 'Manage', adminOnly: true,
    blurb: 'Run, inspect, exec into, browse files and scan containers.' },
  { key: 'kubernetes', label: 'Kubernetes', icon: 'hexagon', group: 'Manage', adminOnly: true,
    blurb: 'Multi-cluster pods, deployments, scaling and manifest deploys.' },
  { key: 'images', label: 'Images', icon: 'disc', group: 'Manage', adminOnly: true,
    blurb: 'Local images, pulls with progress and private registry credentials.' },
  { key: 'hosts', label: 'Hosts', icon: 'server', group: 'Configure', adminOnly: true,
    blurb: 'Docker hosts, mTLS agents, Wake-on-LAN and outbound webhooks.' },
  { key: 'users', label: 'Users', icon: 'users', group: 'Configure', adminOnly: true,
    blurb: 'Accounts, roles, approvals and per-user access.' },
  { key: 'security', label: 'Security', icon: 'shield', group: 'Configure', adminOnly: true,
    blurb: 'Passkeys, TOTP, SSO providers and MCP API keys.' },
  { key: 'settings', label: 'Settings', icon: 'settings', group: 'Configure', adminOnly: true,
    blurb: 'Hostname, TLS certificate, storage paths and system information.' }
];

const PRIMARY_MOBILE: ViewKey[] = ['portal', 'dashboard', 'containers', 'stacks'];
const navByKey = (key: ViewKey) => NAV.find((item) => item.key === key);

interface TerminalTab {
  id: string;
  title: string;
  url: string;
}

interface ToastState {
  /** Monotonic id so a new toast remounts (and re-runs its entrance animation). */
  id: number;
  text: string;
  tone: 'info' | 'success' | 'error';
  /** Override the default on-screen time in ms; `0` keeps the toast until it is dismissed. */
  durationMs?: number;
}

/** Chrome/Edge fire this when the PWA can be installed; not in the TS DOM lib. */
interface BeforeInstallPromptEvent extends Event {
  prompt: () => Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
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
  const [toast, setToast] = useState<ToastState | null>(null);
  const toastId = useRef(0);

  const [installPrompt, setInstallPrompt] = useState<BeforeInstallPromptEvent | null>(null);

  // ── Server connection + version watch ───────────────────────────────────────
  // Polls the anonymous health endpoint: a failed poll raises the "disconnected" banner,
  // and a returning server whose app version changed (a redeploy) triggers a reload with a
  // service-worker refresh so the shell and the pre-built static resources never go stale.
  const [serverDown, setServerDown] = useState(false);
  const [serverVersion, setServerVersion] = useState<string | null>(null);
  const [appUpdate, setAppUpdate] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    let baseVersion: string | null = null;
    let reloadTimer: number | null = null;
    const check = async () => {
      try {
        const health = await api.health();
        if (cancelled) return;
        setServerDown(false);
        if (baseVersion === null) {
          baseVersion = health.appVersion;
          setServerVersion(health.version ?? health.appVersion);
        } else if (health.appVersion && health.appVersion !== baseVersion) {
          // A redeploy changed the served assets: show a note, refresh the worker, and reload
          // after a short grace period so the notice is actually readable.
          baseVersion = health.appVersion;
          setServerVersion(health.version ?? health.appVersion);
          setAppUpdate(health.version ?? health.appVersion);
          void refreshServiceWorker();
          if (reloadTimer === null) {
            reloadTimer = window.setTimeout(() => window.location.reload(), 5000);
          }
        }
      } catch {
        if (!cancelled) setServerDown(true);
      }
    };
    void check();
    const timer = window.setInterval(check, 15000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
      if (reloadTimer !== null) window.clearTimeout(reloadTimer);
    };
  }, []);

  // Returning to a view always starts at the top (a stale scroll offset made deep pages
  // such as Images look broken after navigation, e.g. from an image's detail link).
  useEffect(() => {
    window.scrollTo({ top: 0 });
    document.querySelector('main')?.scrollTo({ top: 0 });
  }, [view]);

  const notify = useCallback<Notify>((message, tone = 'info', durationMs) => {
    toastId.current += 1;
    setToast({ id: toastId.current, text: message, tone, durationMs });
  }, []);
  const closeToast = useCallback(() => setToast(null), []);

  useEffect(() => {
    const onPrompt = (event: Event) => {
      event.preventDefault();
      setInstallPrompt(event as BeforeInstallPromptEvent);
    };
    const onInstalled = () => setInstallPrompt(null);
    window.addEventListener('beforeinstallprompt', onPrompt);
    window.addEventListener('appinstalled', onInstalled);
    return () => {
      window.removeEventListener('beforeinstallprompt', onPrompt);
      window.removeEventListener('appinstalled', onInstalled);
    };
  }, []);

  const standalone = typeof window !== 'undefined' && window.matchMedia('(display-mode: standalone)').matches;

  const installApp = useCallback(async () => {
    if (installPrompt) {
      await installPrompt.prompt();
      await installPrompt.userChoice;
      setInstallPrompt(null);
      return;
    }
    const ua = navigator.userAgent;
    const isIos = /iPad|iPhone|iPod/.test(ua)
      || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
    notify(
      isIos
        ? 'On iPhone/iPad: tap Share → “Add to Home Screen”. The site must use a certificate the device trusts — install ca.crt from Settings → TLS certificate, or enable Let’s Encrypt.'
        : 'Install from the browser menu: Chrome/Edge → “Install app”, or the install icon in the address bar. If that option is missing, the site’s certificate is not trusted yet — install ca.crt from Settings → TLS certificate, or enable Let’s Encrypt.',
      'info',
      0
    );
  }, [installPrompt, notify]);

  const isAdmin = session?.role === 'Admin';
  const visibleNav = useMemo(() => NAV.filter((item) => !item.adminOnly || isAdmin), [isAdmin]);

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
    if (location.hash.replace('#', '') !== view) location.hash = view;
  }, [view]);

  useEffect(() => {
    const onHash = () => {
      const hash = location.hash.replace('#', '') as ViewKey;
      if (NAV.some((n) => n.key === hash)) setView(hash);
    };
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);

  useEffect(() => {
    if (session?.authenticated && !isAdmin && navByKey(view)?.adminOnly) setView('portal');
  }, [session?.authenticated, isAdmin, view]);

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
    if (!isAdmin) return;
    api.hosts().then(setHosts).catch(() => setHosts([]));
  }, [isAdmin]);

  const openTerminal = useCallback<OpenTerminal>((title, url) => {
    const id = `${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
    setTerminals((tabs) => [...tabs, { id, title, url }]);
    setTerminalOpen(true);
  }, []);

  const logout = useCallback(async () => {
    await api.logout();
    await refreshSession();
  }, [refreshSession]);

  if (loading) return <Splash message="Starting eh.DockManager…" />;
  if (!session?.authenticated) return <LoginView onAuthenticated={refreshSession} notify={notify} />;

  return (
    <div className="flex min-h-[100dvh] bg-ink-950 text-slate-100">
      <Sidebar view={view} setView={setView} nav={visibleNav} session={session} appVersion={serverVersion} />

      <div className="flex min-w-0 flex-1 flex-col">
        <TopBar
          view={view}
          session={session}
          hosts={hosts}
          activeHost={activeHost}
          setActiveHost={setActiveHost}
          isAdmin={isAdmin}
          onCommand={() => setCommandOpen(true)}
          onInstall={installApp}
          canInstall={!standalone}
          onNavigate={setView}
          onLogout={logout}
        />

        {serverDown && <ConnectionBanner version={serverVersion} />}
        {appUpdate && !serverDown && <UpdateBanner version={appUpdate} />}

        {/* `overflow-x-hidden`: nothing in a view is meant to be wider than the viewport
            (tables and tab strips carry their own scroll containers), so clip horizontally
            here as a backstop — a stray wide or off-screen element must never turn the
            scroll area into a horizontally swipeable one on a phone. */}
        <main className="min-h-0 flex-1 overflow-y-auto overflow-x-hidden px-3 pb-24 pt-4 sm:px-5 sm:pb-28 md:pb-8">
          {view === 'portal' && <PortalLandingView notify={notify} openTerminal={openTerminal} isAdmin={isAdmin} />}
          {view === 'dashboard' && (
            <DashboardView
              activeHost={activeHost}
              setActiveHost={setActiveHost}
              notify={notify}
              setView={setView}
              isAdmin={isAdmin}
              session={session}
            />
          )}
          {view === 'stacks' && <StacksView notify={notify} />}
          {view === 'containers' && (
            <ContainersView activeHost={activeHost} notify={notify} openTerminal={openTerminal} inspect={setInspector} />
          )}
          {view === 'kubernetes' && <KubernetesView notify={notify} openTerminal={openTerminal} />}
          {view === 'images' && <ImagesAndRegistriesView activeHost={activeHost} notify={notify} setView={setView} />}
          {view === 'hosts' && (
            <HostsView notify={notify} refreshHosts={() => api.hosts().then(setHosts).catch(() => undefined)} />
          )}
          {view === 'users' && <UsersView notify={notify} session={session} />}
          {view === 'security' && <SecurityAndSsoView notify={notify} />}
          {view === 'settings' && <SettingsView notify={notify} />}
        </main>
      </div>

      <BottomNav view={view} setView={setView} nav={visibleNav} session={session} onInstall={installApp} appVersion={serverVersion} />

      {commandOpen && (
        <CommandPalette
          nav={visibleNav}
          onClose={() => setCommandOpen(false)}
          onNavigate={(key) => {
            setView(key);
            setCommandOpen(false);
          }}
          actions={[
            { label: 'Open the public portal', icon: 'externalLink', run: () => { location.href = '/'; } },
            {
              label: 'Refresh portal discovery',
              icon: 'refreshCw',
              run: async () => {
                await api.refreshPortal();
                notify('Portal discovery refreshed', 'success');
              }
            },
            { label: 'Toggle terminal drawer', icon: 'terminal', run: () => setTerminalOpen((o) => !o) }
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

      {toast && <Toast key={toast.id} toast={toast} onClose={closeToast} />}
    </div>
  );
}

/** Non-blocking banner shown while the server is unreachable. */
function ConnectionBanner({ version }: { version: string | null }) {
  return (
    <div className="flex items-center gap-2 border-b border-amber-800/60 bg-amber-950/40 px-3 py-2 text-xs text-amber-200 sm:px-5">
      <Icon name="alertTriangle" className="h-4 w-4 shrink-0" />
      <span className="min-w-0 flex-1">
        <strong>Server disconnected.</strong> Retrying automatically — changes are paused until the connection returns.
        {version && <span className="ml-1 text-amber-300/80">(last known version {version})</span>}
      </span>
      <button type="button" className="btn-ghost px-2 py-1 text-xs" onClick={() => window.location.reload()}>
        Reload
      </button>
    </div>
  );
}

/** Non-blocking banner shown when a redeploy changed the served app version. */
function UpdateBanner({ version }: { version: string }) {
  return (
    <div className="flex items-center gap-2 border-b border-emerald-800/60 bg-emerald-950/40 px-3 py-2 text-xs text-emerald-200 sm:px-5">
      <Icon name="refreshCw" className="h-4 w-4 shrink-0" />
      <span className="min-w-0 flex-1">
        <strong>App upgraded to v{version}.</strong> Reloading automatically to load the new version…
      </span>
      <button type="button" className="btn-ghost px-2 py-1 text-xs" onClick={() => window.location.reload()}>
        Reload now
      </button>
    </div>
  );
}

/** Asks the browser to update the registered service worker before a reload. */
async function refreshServiceWorker(): Promise<void> {
  try {
    if ('serviceWorker' in navigator) {
      const registration = await navigator.serviceWorker.getRegistration();
      await registration?.update();
    }
  } catch {
    /* installability is best-effort */
  }
}

function Splash({ message }: { message: string }) {
  return (
    <div className="flex min-h-[100dvh] items-center justify-center bg-ink-950 text-slate-400">
      <div className="flex flex-col items-center gap-3">
        <span className="inline-flex h-12 w-12 items-center justify-center rounded-2xl border border-ink-700 bg-ink-900 text-accent">
          <Icon name="box" className="h-6 w-6" />
        </span>
        <div className="animate-pulseSoft text-sm">{message}</div>
      </div>
    </div>
  );
}

/**
 * Transient notification. On a phone it spans the width (minus margins); on a wider screen it
 * shrinks to its content and centres. It animates in, then out on dismiss or after `durationMs`
 * (default 3.6 s, 5.2 s for errors; `0` is sticky — it stays until dismissed) — the lifetime is
 * kept in step with the exit animation so the element is unmounted only once it has finished leaving.
 */
function Toast({ toast, onClose }: { toast: ToastState; onClose: () => void }) {
  const [leaving, setLeaving] = useState(false);
  const duration = toast.durationMs ?? (toast.tone === 'error' ? 5200 : 3600);

  useEffect(() => {
    if (duration <= 0) return undefined; // sticky: never auto-dismiss
    const timer = window.setTimeout(() => setLeaving(true), duration);
    return () => window.clearTimeout(timer);
  }, [duration]);

  useEffect(() => {
    if (!leaving) return undefined;
    const timer = window.setTimeout(onClose, 220);
    return () => window.clearTimeout(timer);
  }, [leaving, onClose]);

  const tones: Record<ToastState['tone'], string> = {
    info: 'border-ink-700',
    success: 'border-emerald-800/70',
    error: 'border-red-900/70'
  };
  const icons: Record<ToastState['tone'], IconName> = { info: 'info', success: 'circleCheck', error: 'circleAlert' };
  const tint = toast.tone === 'error' ? 'text-red-300' : toast.tone === 'success' ? 'text-emerald-300' : 'text-accent';

  return (
    <div
      role="status"
      className={classNames(
        // Phone: full width between 0.75rem margins. md+: shrink to content, centred.
        'fixed bottom-24 left-3 right-3 z-[70] flex items-start gap-2 rounded-xl border bg-ink-850 px-3.5 py-2.5 text-sm shadow-2xl',
        'md:bottom-6 md:left-1/2 md:right-auto md:max-w-md md:-translate-x-1/2',
        leaving ? 'animate-toastOut motion-reduce:animate-none' : 'animate-toastIn motion-reduce:animate-none',
        tones[toast.tone]
      )}
    >
      <Icon name={icons[toast.tone]} className={classNames('mt-0.5 h-4 w-4 shrink-0', tint)} />
      <span className="min-w-0 flex-1 break-words text-slate-200">{toast.text}</span>
      <button
        type="button"
        className="mt-0.5 shrink-0 text-slate-400 hover:text-slate-200"
        onClick={() => setLeaving(true)}
        aria-label="Dismiss"
      >
        <Icon name="x" className="h-4 w-4" />
      </button>
    </div>
  );
}

function Sidebar({ view, setView, nav, session, appVersion }: { view: ViewKey; setView: (v: ViewKey) => void; nav: NavItem[]; session: SessionUser; appVersion?: string | null }) {
  return (
    <aside className="sticky top-0 hidden h-[100dvh] w-60 shrink-0 flex-col border-r border-ink-800 bg-ink-900/50 p-3 md:flex">
      <div className="mb-5 flex items-center gap-2.5 px-1">
        <span className="inline-flex h-9 w-9 items-center justify-center rounded-xl border border-ink-700 bg-ink-850 text-accent">
          <Icon name="box" className="h-5 w-5" />
        </span>
        <div className="min-w-0">
          <div className="truncate text-sm font-semibold">eh.DockManager</div>
          <div className="text-[10px] uppercase tracking-wider text-slate-400">Control Plane</div>
        </div>
      </div>

      <nav className="flex flex-1 flex-col gap-4 overflow-y-auto">
        {NAV_GROUPS.map((group) => {
          const items = nav.filter((item) => item.group === group);
          if (items.length === 0) return null;
          return (
            <div key={group} className="flex flex-col gap-1">
              <div className="px-3 pb-1 text-[10px] font-semibold uppercase tracking-wider text-slate-400">{group}</div>
              {items.map((item) => (
                <button
                  key={item.key}
                  type="button"
                  onClick={() => setView(item.key)}
                  aria-current={view === item.key ? 'page' : undefined}
                  className={classNames(
                    'flex items-center gap-3 rounded-lg px-3 py-2 text-sm transition-colors',
                    view === item.key ? 'bg-accent-muted/50 font-medium text-white' : 'text-slate-400 hover:bg-ink-800 hover:text-slate-200'
                  )}
                >
                  <Icon name={item.icon} className="h-4 w-4" />
                  {item.label}
                </button>
              ))}
            </div>
          );
        })}
      </nav>

      <div className="mt-3 overflow-hidden rounded-xl border border-ink-800 bg-ink-900/60">
        <div className="flex items-center gap-2 p-2.5">
          <span className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-ink-700 bg-ink-850 text-slate-300">
            <Icon name="user" className="h-4 w-4" />
          </span>
          <div className="min-w-0 flex-1">
            <div className="flex min-w-0 items-center gap-2">
              <span className="min-w-0 truncate text-xs font-medium text-slate-200">{session.username}</span>
              <span className="shrink-0">
                <RoleBadge role={session.role} withLabel={false} />
              </span>
            </div>
          </div>
        </div>
        {appVersion && (
          <div
            className="border-t border-ink-800 px-2.5 py-1.5 text-center font-mono text-[10px] text-slate-400"
            title={`Application version (build): ${appVersion}`}
          >
            v{appVersion}
          </div>
        )}
      </div>
    </aside>
  );
}

function TopBar({
  view,
  session,
  hosts,
  activeHost,
  setActiveHost,
  isAdmin,
  onCommand,
  onInstall,
  canInstall,
  onNavigate,
  onLogout
}: {
  view: ViewKey;
  session: SessionUser;
  hosts: DockerHostInfo[];
  activeHost?: number;
  setActiveHost: (id?: number) => void;
  isAdmin: boolean;
  onCommand: () => void;
  onInstall: () => void;
  canInstall: boolean;
  onNavigate: (v: ViewKey) => void;
  onLogout: () => void;
}) {
  const current = navByKey(view);
  const [menuOpen, setMenuOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!menuOpen) return undefined;
    const onDown = (event: PointerEvent) => {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) setMenuOpen(false);
    };
    document.addEventListener('pointerdown', onDown);
    return () => document.removeEventListener('pointerdown', onDown);
  }, [menuOpen]);

  return (
    <header className="sticky top-0 z-30 flex items-center gap-2 border-b border-ink-800 bg-ink-900/80 px-3 py-2 backdrop-blur sm:px-5">
      <span className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-ink-700 bg-ink-850 text-accent md:hidden">
        <Icon name="box" className="h-4 w-4" />
      </span>
      <div className="min-w-0 md:hidden">
        <div className="truncate text-sm font-semibold">{current?.label ?? 'eh.DockManager'}</div>
      </div>

      {isAdmin && (
        <div className="hidden items-center gap-2 md:flex">
          <label className="label mb-0" htmlFor="host-switch">Host</label>
          <select
            id="host-switch"
            aria-label="Docker host"
            className="input w-56 py-1.5"
            value={activeHost ?? ''}
            onChange={(event) => setActiveHost(event.target.value ? Number(event.target.value) : undefined)}
          >
            <option value="">Local (default)</option>
            {hosts.map((host) => (
              <option key={host.id} value={host.id}>{host.name} · {host.kind}</option>
            ))}
          </select>
        </div>
      )}

      <div className="ml-auto flex items-center gap-2">
        {canInstall && (
          <button
            type="button"
            className="btn-ghost px-2 py-1.5 text-xs"
            onClick={onInstall}
            title="Install eh.DockManager as an app — the device must trust this site's certificate"
            aria-label="Install the app"
          >
            <Icon name="download" className="h-4 w-4" />
            <span className="hidden sm:inline">Install</span>
          </button>
        )}
        <button
          type="button"
          className="btn-ghost px-2 py-1.5 text-xs"
          onClick={onCommand}
          title="Command palette — jump to any view or run an action (Ctrl/⌘+K)"
          aria-label="Commands"
        >
          <Icon name="command" className="h-4 w-4" />
          <span className="hidden sm:inline">Commands</span>
        </button>

        <div className="relative" ref={menuRef}>
          <button
            type="button"
            className="flex items-center gap-2 rounded-lg border border-ink-700 bg-ink-800/60 px-2 py-1.5 text-xs hover:bg-ink-700"
            onClick={() => setMenuOpen((value) => !value)}
            aria-haspopup="menu"
            aria-expanded={menuOpen}
          >
            <span className="hidden max-w-[8rem] truncate text-slate-200 sm:inline">{session.username}</span>
            <RoleBadge role={session.role} withLabel={false} />
            <Icon name="chevronDown" className="h-3.5 w-3.5 text-slate-400" />
          </button>
          {menuOpen && (
            <div role="menu" className="absolute right-0 z-50 mt-2 w-64 overflow-hidden rounded-xl border border-ink-700 bg-ink-850 shadow-2xl">
              <div className="border-b border-ink-800 p-3">
                <div className="text-sm font-semibold text-slate-100">{session.username}</div>
                {session.email && <div className="truncate text-xs text-slate-400">{session.email}</div>}
                <div className="mt-1.5 flex items-center gap-2"><RoleBadge role={session.role} /></div>
                <p className="mt-1.5 text-[11px] leading-relaxed text-slate-400">{roleBlurb(session.role)}</p>
              </div>
              {isAdmin && (
                <div className="p-1">
                  <button type="button" role="menuitem" className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-sm text-slate-300 hover:bg-ink-800" onClick={() => { onNavigate('users'); setMenuOpen(false); }}>
                    <Icon name="users" className="h-4 w-4" /> Users &amp; roles
                  </button>
                  <button type="button" role="menuitem" className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-sm text-slate-300 hover:bg-ink-800" onClick={() => { onNavigate('settings'); setMenuOpen(false); }}>
                    <Icon name="settings" className="h-4 w-4" /> Settings
                  </button>
                </div>
              )}
              <div className="border-t border-ink-800 p-1">
                <button type="button" role="menuitem" className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-sm text-red-200 hover:bg-red-950/40" onClick={() => { setMenuOpen(false); onLogout(); }}>
                  <Icon name="logOut" className="h-4 w-4" /> Sign out
                </button>
              </div>
            </div>
          )}
        </div>
      </div>
    </header>
  );
}

function BottomNav({
  view,
  setView,
  nav,
  session,
  onInstall,
  appVersion
}: {
  view: ViewKey;
  setView: (v: ViewKey) => void;
  nav: NavItem[];
  session: SessionUser;
  onInstall: () => void;
  appVersion?: string | null;
}) {
  const [moreOpen, setMoreOpen] = useState(false);
  const primary = PRIMARY_MOBILE.map((key) => nav.find((item) => item.key === key)).filter(Boolean) as NavItem[];
  return (
    <>
      <nav className="fixed inset-x-0 bottom-0 z-40 flex border-t border-ink-800 bg-ink-900/95 pb-[env(safe-area-inset-bottom)] backdrop-blur md:hidden">
        {primary.map((item) => (
          <button
            key={item.key}
            type="button"
            onClick={() => setView(item.key)}
            className={classNames('flex min-h-[56px] flex-1 flex-col items-center justify-center gap-0.5 text-[10px]', view === item.key ? 'text-accent' : 'text-slate-400')}
          >
            <Icon name={item.icon} className="h-5 w-5" />
            {item.label}
          </button>
        ))}
        <button
          type="button"
          onClick={() => setMoreOpen(true)}
          className={classNames('flex min-h-[56px] flex-1 flex-col items-center justify-center gap-0.5 text-[10px]', primary.some((p) => p.key === view) ? 'text-slate-400' : 'text-accent')}
        >
          <Icon name="moreHorizontal" className="h-5 w-5" />
          More
        </button>
      </nav>
      {moreOpen && (
        <MoreSheet
          nav={nav}
          view={view}
          session={session}
          onClose={() => setMoreOpen(false)}
          onNavigate={(key) => { setView(key); setMoreOpen(false); }}
          onInstall={onInstall}
          appVersion={appVersion}
        />
      )}
    </>
  );
}

function MoreSheet({
  nav,
  view,
  session,
  onClose,
  onNavigate,
  onInstall,
  appVersion
}: {
  nav: NavItem[];
  view: ViewKey;
  session: SessionUser;
  onClose: () => void;
  onNavigate: (key: ViewKey) => void;
  onInstall: () => void;
  appVersion?: string | null;
}) {
  const standalone = typeof window !== 'undefined' && window.matchMedia('(display-mode: standalone)').matches;
  return (
    <div className="fixed inset-0 z-50 flex items-end bg-black/60 md:hidden" onClick={onClose}>
      <div className="w-full rounded-t-2xl border-t border-ink-700 bg-ink-900 p-4 pb-[calc(1rem+env(safe-area-inset-bottom))]" onClick={(event) => event.stopPropagation()}>
        <div className="mb-3 flex items-center gap-2">
          <div className="min-w-0 flex-1">
            <div className="text-sm font-semibold">All views</div>
            <div className="truncate text-[11px] text-slate-400">Signed in as {session.username} · {session.role}</div>
          </div>
          <button type="button" className="btn-ghost px-2 py-1" onClick={onClose} aria-label="Close menu">
            <Icon name="x" className="h-4 w-4" />
          </button>
        </div>
        <div className="grid grid-cols-3 gap-2">
          {nav.map((item) => (
            <button
              key={item.key}
              type="button"
              onClick={() => onNavigate(item.key)}
              className={classNames(
                'flex flex-col items-center gap-1.5 rounded-xl border p-3 text-[11px]',
                view === item.key ? 'border-accent-soft/50 bg-accent-muted/40 text-white' : 'border-ink-800 bg-ink-850/60 text-slate-300'
              )}
            >
              <Icon name={item.icon} className="h-5 w-5" />
              {item.label}
            </button>
          ))}
        </div>
        {!standalone && (
          <button type="button" className="btn-ghost mt-3 w-full" onClick={onInstall}>
            <Icon name="download" className="h-4 w-4" /> Install app (PWA)
          </button>
        )}
        <div className="mt-3 flex items-center justify-between border-t border-ink-800 pt-3 text-[11px] text-slate-400">
          <span className="font-semibold text-slate-300">eh.DockManager</span>
          {appVersion && (
            <span className="font-mono" title={`Application version (build): ${appVersion}`}>v{appVersion}</span>
          )}
        </div>
      </div>
    </div>
  );
}

function CommandPalette({
  nav,
  onClose,
  onNavigate,
  actions
}: {
  nav: NavItem[];
  onClose: () => void;
  onNavigate: (key: ViewKey) => void;
  actions: { label: string; icon: IconName; run: () => void | Promise<void> }[];
}) {
  const [query, setQuery] = useState('');
  const items = useMemo(
    () => [
      ...nav.map((n) => ({ label: `Go to ${n.label}`, icon: n.icon, hint: n.blurb, run: () => onNavigate(n.key) })),
      ...actions.map((a) => ({ label: a.label, icon: a.icon, hint: '', run: a.run }))
    ],
    [nav, actions, onNavigate]
  );
  const filtered = items.filter((item) => `${item.label} ${item.hint}`.toLowerCase().includes(query.toLowerCase()));
  return (
    <div className="fixed inset-0 z-[60] flex items-start justify-center bg-black/60 p-4 pt-20 sm:pt-24" onClick={onClose}>
      <div className="card w-full max-w-lg overflow-hidden" onClick={(event) => event.stopPropagation()}>
        <div className="flex items-center gap-2 border-b border-ink-700 px-4 py-3">
          <Icon name="search" className="h-4 w-4 text-slate-400" />
          <input
            autoFocus
            className="w-full bg-transparent text-sm outline-none"
            placeholder="Type a command…"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
          />
          <kbd className="rounded border border-ink-600 px-1 text-[10px] text-slate-400">Esc</kbd>
        </div>
        <p className="border-b border-ink-700 px-4 py-1.5 text-[11px] text-slate-400">
          The command palette is a quick keyboard way to move around and act — it is not help. Press{' '}
          <kbd className="mx-1 rounded border border-ink-600 px-1 text-[10px] text-slate-400">Ctrl/⌘ K</kbd>{' '}
          anywhere to open it.
        </p>
        <div className="max-h-80 overflow-y-auto p-1">
          {filtered.map((item) => (
            <button
              key={item.label}
              type="button"
              className="flex w-full items-center gap-3 rounded-lg px-3 py-2 text-left hover:bg-ink-800"
              onClick={() => {
                void item.run();
                onClose();
              }}
            >
              <Icon name={item.icon} className="h-4 w-4 text-slate-400" />
              <span className="min-w-0 flex-1">
                <span className="block truncate text-sm text-slate-200">{item.label}</span>
                {item.hint && <span className="block truncate text-[11px] text-slate-400">{item.hint}</span>}
              </span>
            </button>
          ))}
          {filtered.length === 0 && <div className="px-4 py-6 text-center text-sm text-slate-400">No matches</div>}
        </div>
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// Login
// ────────────────────────────────────────────────────────────────────────────
function LoginView({ onAuthenticated, notify }: { onAuthenticated: () => void; notify: Notify }) {
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
        notify('Enter the 6-digit code from your authenticator app');
      } else {
        await onAuthenticated();
      }
    } catch (error) {
      notify((error as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };

  const passkeyLogin = async () => {
    try {
      const begin = await api.passkeyLoginBegin(username || undefined);
      const credential = await navigator.credentials.get({ publicKey: decodePublicKeyRequestOptions(begin.options) });
      if (!credential) return;
      await api.passkeyLoginComplete(begin.flowId, encodeCredential(credential as PublicKeyCredential));
      await onAuthenticated();
    } catch (error) {
      notify((error as Error).message, 'error');
    }
  };

  return (
    <div className="flex min-h-[100dvh] items-center justify-center bg-ink-950 p-4">
      <div className="grid w-full max-w-4xl gap-6 lg:grid-cols-2">
        <form onSubmit={submit} className="card w-full space-y-4 p-6">
          <div className="flex items-center gap-2.5">
            <span className="inline-flex h-10 w-10 items-center justify-center rounded-xl border border-ink-700 bg-ink-850 text-accent">
              <Icon name="box" className="h-5 w-5" />
            </span>
            <div>
              <h1 className="text-lg font-semibold">eh.DockManager</h1>
              <p className="text-xs text-slate-400">Sign in to the control plane</p>
            </div>
          </div>

          <Field label="Username">
            <input
              className="input"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              autoComplete="username"
              autoCapitalize="none"
              autoCorrect="off"
              spellCheck={false}
              inputMode="text"
            />
          </Field>
          <Field label="Password">
            <input className="input" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" />
          </Field>
          {needsTotp && (
            <Field label="Authenticator code" hint="6 digits, refreshes every 30 seconds.">
              <input className="input tracking-[0.4em]" value={totp} onChange={(e) => setTotp(e.target.value)} inputMode="numeric" autoComplete="one-time-code" />
            </Field>
          )}

          <button className="btn-primary w-full" disabled={busy} type="submit">
            <Icon name="lock" className="h-4 w-4" />
            {busy ? 'Signing in…' : 'Sign in'}
          </button>
          <button className="btn-ghost w-full" type="button" onClick={passkeyLogin}>
            <Icon name="key" className="h-4 w-4" /> Sign in with a passkey
          </button>

          {(providers.google || providers.microsoft) && (
            <div className="grid grid-cols-2 gap-2">
              {providers.google && (
                <a className="btn-ghost" href="/api/auth/oidc/google/challenge"><Icon name="globe" className="h-4 w-4" /> Google</a>
              )}
              {providers.microsoft && (
                <a className="btn-ghost" href="/api/auth/oidc/microsoft/challenge"><Icon name="globe" className="h-4 w-4" /> Microsoft</a>
              )}
            </div>
          )}
          <a className="block text-center text-xs text-slate-400 hover:text-accent" href={api.ssoLoginUrl}>
            Enterprise SSO (SAML 2.0)
          </a>
        </form>

        <div className="hidden flex-col justify-center gap-4 lg:flex">
          <div className="flex items-center gap-2">
            <h2 className="text-xl font-semibold">One control plane for your whole fleet.</h2>
            <InfoTip label="About eh.DockManager">
              A hardened home-server and enterprise container control plane: reverse proxy on port 443, an
              auto-discovered app portal, Docker and Kubernetes management, auto-updates and an MCP server.
            </InfoTip>
          </div>
          <ul className="space-y-3 text-sm text-slate-400">
            {[
              ['layoutGrid', 'App portal', 'Every published app behind one HTTPS entry point.'],
              ['box', 'Containers & stacks', 'Deploy, inspect, exec and auto-update Compose stacks.'],
              ['shield', 'Zero-trust auth', 'Passkeys, TOTP, OIDC and SAML 2.0 with role-based access.']
            ].map(([icon, title, text]) => (
              <li key={title} className="flex gap-3">
                <span className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-ink-700 bg-ink-850 text-accent">
                  <Icon name={icon as IconName} className="h-4 w-4" />
                </span>
                <span>
                  <span className="block font-medium text-slate-200">{title}</span>
                  <span className="block text-xs text-slate-400">{text}</span>
                </span>
              </li>
            ))}
          </ul>
        </div>
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 1 — Portal landing
// ────────────────────────────────────────────────────────────────────────────
function emptyPortalApp(): PortalApp {
  return {
    id: 0,
    subdomain: '',
    name: '',
    icon: '',
    category: 'Apps',
    auth: 'Sso',
    autoscale: 'None',
    targetPort: null,
    containerName: null,
    externalUrl: '',
    sortOrder: 0,
    hidden: false,
    manual: true,
    autoDiscovered: false
  };
}

function PortalLandingView({ notify, openTerminal, isAdmin }: { notify: Notify; openTerminal: OpenTerminal; isAdmin: boolean }) {
  const [landing, setLanding] = useState<LandingResponse | null>(null);
  const [query, setQuery] = useState('');
  const [liveMetrics, setLiveMetrics] = useState(true);
  const [showHidden, setShowHidden] = useState(false);
  const [editing, setEditing] = useState<PortalApp | null>(null);
  const [addingAvailable, setAddingAvailable] = useState(false);

  const load = useCallback(() => {
    api.landing(showHidden && isAdmin).then(setLanding).catch((e) => notify((e as Error).message, 'error'));
  }, [notify, showHidden, isAdmin]);

  useEffect(load, [load]);

  const toggleHidden = async (app: PortalApp) => {
    try {
      await api.savePortalApp({ ...app, hidden: !app.hidden });
      notify(app.hidden ? 'App shown on the portal' : 'App hidden from the portal', 'success');
      load();
    } catch (error) {
      notify((error as Error).message, 'error');
    }
  };

  const deleteApp = async (app: PortalApp) => {
    try {
      await api.deletePortalApp(app.subdomain);
      notify('Application deleted', 'success');
      load();
    } catch (error) {
      notify((error as Error).message, 'error');
    }
  };

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
    // Categories are alphabetical; the order inside a category is the manual SortOrder.
    return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [landing, query]);

  const moveApp = async (app: PortalApp, direction: -1 | 1) => {
    if (!landing) return;
    const siblings = landing.apps.filter((item) => item.category === app.category);
    const index = siblings.findIndex((item) => item.subdomain === app.subdomain);
    const target = index + direction;
    if (index < 0 || target < 0 || target >= siblings.length) return;
    const reordered = [...siblings];
    [reordered[index], reordered[target]] = [reordered[target], reordered[index]];
    try {
      await api.reorderPortalApps(reordered.map((item) => item.subdomain));
      load();
    } catch (error) {
      notify((error as Error).message, 'error');
    }
  };

  return (
    <div className="space-y-5">
      <PageHeader
        title="App Portal"
        icon="layoutGrid"
        subtitle={landing ? `Published at https://${landing.domain} · ${landing.apps.length} application${landing.apps.length === 1 ? '' : 's'}` : 'Loading applications…'}
        info={
          <>
            This is the public landing page. Each card is a container or external link that DockManager
            reverse-proxies under <code>https://&lt;subdomain&gt;.{landing?.domain ?? 'dock.lan'}</code>. Edit a card
            to change its name, icon, category or access mode; refresh to re-run auto-discovery.
          </>
        }
        actions={
          <>
            <button className="btn-ghost" onClick={async () => { await api.refreshPortal(); load(); notify('Discovery refreshed', 'success'); }}>
              <Icon name="refreshCw" className="h-4 w-4" /> Refresh
            </button>
            {isAdmin && (
              <button className="btn-ghost" onClick={() => setAddingAvailable(true)}>
                <Icon name="layoutGrid" className="h-4 w-4" /> Add available
              </button>
            )}
            {isAdmin && (
              <button className="btn-primary" onClick={() => setEditing(emptyPortalApp())}>
                <Icon name="plus" className="h-4 w-4" /> Add app
              </button>
            )}
          </>
        }
      />

      <div className="flex flex-wrap items-center gap-3">
        <div className="relative min-w-[14rem] flex-1">
          <Icon name="search" className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <input className="input pl-9" placeholder="Search apps…" value={query} onChange={(e) => setQuery(e.target.value)} />
        </div>
        <label className="flex items-center gap-2 text-xs text-slate-400">
          <input type="checkbox" checked={liveMetrics} onChange={(e) => setLiveMetrics(e.target.checked)} />
          Live metrics
          <InfoTip label="Live metrics">
            Opens a streaming connection per app to chart CPU and memory. Turn it off on low-power devices
            or when you only need the links.
          </InfoTip>
        </label>
        {isAdmin && (
          <label className="flex items-center gap-2 text-xs text-slate-400">
            <input type="checkbox" checked={showHidden} onChange={(e) => setShowHidden(e.target.checked)} />
            Show hidden
            <InfoTip label="Show hidden">
              Hidden apps are removed from the portal list but stay reachable at their URL. Enable this to
              reveal them and unhide one.
            </InfoTip>
          </label>
        )}
      </div>

      {categories.map(([category, apps]) => (
        <section key={category} className="space-y-2">
          <h2 className="text-xs font-semibold uppercase tracking-wider text-slate-400">{category}</h2>
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
            {apps.map((app, index) => (
              <PortalCard
                key={app.id}
                app={app}
                live={liveMetrics}
                canEdit={isAdmin}
                onEdit={() => setEditing(app)}
                onOpen={() => window.open(app.url ?? '#', '_blank')}
                onTerminal={() => app.containerName && openTerminal(app.name, api.execUrl(app.containerName, '/bin/sh'))}
                onToggleHidden={() => toggleHidden(app)}
                onDelete={() => deleteApp(app)}
                canMoveUp={index > 0}
                canMoveDown={index < apps.length - 1}
                onMoveUp={() => moveApp(app, -1)}
                onMoveDown={() => moveApp(app, 1)}
              />
            ))}
          </div>
        </section>
      ))}

      {categories.length === 0 && (
        <EmptyState
          icon="layoutGrid"
          title={query ? 'No apps match your search' : 'No applications discovered yet'}
          hint={query ? 'Try a different name or clear the search.' : 'Publish a container with eh.portal.* labels or add a manual external link.'}
          action={isAdmin && !query ? <button className="btn-primary" onClick={() => setEditing(emptyPortalApp())}><Icon name="plus" className="h-4 w-4" /> Add app</button> : undefined}
        />
      )}

      {editing && (
        <PortalAppEditor
          app={editing}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); load(); }}
          notify={notify}
        />
      )}

      {addingAvailable && (
        <PortalAvailableDialog
          notify={notify}
          onClose={() => setAddingAvailable(false)}
          onAdded={() => { setAddingAvailable(false); load(); }}
        />
      )}
    </div>
  );
}

function PortalCard({
  app,
  live,
  canEdit,
  onEdit,
  onOpen,
  onTerminal,
  onToggleHidden,
  onDelete,
  canMoveUp,
  canMoveDown,
  onMoveUp,
  onMoveDown
}: {
  app: PortalApp;
  live: boolean;
  canEdit: boolean;
  onEdit: () => void;
  onOpen: () => void;
  onTerminal: () => void;
  onToggleHidden: () => void;
  onDelete: () => void;
  canMoveUp: boolean;
  canMoveDown: boolean;
  onMoveUp: () => void;
  onMoveDown: () => void;
}) {
  const [cpu, setCpu] = useState<number[]>([]);
  const [ram, setRam] = useState<number[]>([]);

  useEffect(() => {
    if (!live || !app.containerName) return undefined;
    const source = new EventSource(api.statsUrl(app.containerName), { withCredentials: true });
    source.onmessage = (event) => {
      try {
        const sample = JSON.parse(event.data) as MetricSample & { error?: string };
        if (sample.error) return;
        setCpu((p) => [...p, sample.cpuPercent ?? 0].slice(-30));
        setRam((p) => [...p, sample.memoryMb ?? 0].slice(-30));
      } catch {
        /* ignore malformed frames */
      }
    };
    return () => source.close();
  }, [live, app.containerName]);

  const accessLabel = app.auth === 'Public' ? 'anyone' : app.auth === 'Sso' ? 'signed-in users' : 'admins only';

  return (
    <div className={classNames('card group flex flex-col gap-3 p-4 outline outline-1 outline-transparent transition-colors hover:outline-accent-soft/40', app.hidden && 'opacity-60')}>
      <div className="flex items-start gap-3">
        <div className="flex h-10 w-10 shrink-0 items-center justify-center overflow-hidden rounded-lg border border-ink-700 bg-ink-800 text-lg">
          {app.icon ? <img src={app.icon} alt="" className="h-6 w-6 object-contain" /> : app.name.slice(0, 1).toUpperCase()}
        </div>
        <button type="button" className="min-w-0 flex-1 text-left" onClick={onOpen}>
          <div className="truncate text-sm font-semibold group-hover:text-accent">{app.name}</div>
          <div className="truncate text-[11px] text-slate-400">{app.subdomain}</div>
        </button>
        {app.autoscale !== 'None' && (
          <span className="badge border border-cyan-800/60 bg-cyan-950/60 text-cyan-300" title="Stops when idle and wakes on first request">
            <Icon name="power" className="h-3 w-3" /> scale-0
          </span>
        )}
        {app.hidden && (
          <span className="badge border border-ink-700 bg-ink-800 text-slate-400" title="Hidden from the portal list (still reachable by URL)">
            hidden
          </span>
        )}
      </div>

      {live && app.containerName && (
        <div className="grid grid-cols-2 gap-2 text-[10px] text-slate-400">
          <div>
            <div className="flex items-center justify-between"><span>CPU</span><span>{cpu.length ? `${(cpu[cpu.length - 1] ?? 0).toFixed(1)}%` : '—'}</span></div>
            <Sparkline data={cpu} stroke="#38bdf8" />
          </div>
          <div>
            <div className="flex items-center justify-between"><span>RAM</span><span>{ram.length ? `${(ram[ram.length - 1] ?? 0).toFixed(0)} MB` : '—'}</span></div>
            <Sparkline data={ram} stroke="#34d399" />
          </div>
        </div>
      )}

      <div className="mt-auto space-y-2">
        <Expand title="More info" hint={app.auth === 'Public' ? 'public' : app.auth === 'Sso' ? 'signed-in' : 'admins'}>
          <MetricRow label="Name" value={app.name} />
          <MetricRow label="Address" value={<span className="font-mono text-[11px]">{app.subdomain}</span>} />
          <MetricRow label="Category" value={app.category} />
          <MetricRow label="Access" value={app.auth === 'Public' ? 'Public (anyone)' : app.auth === 'Sso' ? 'Signed-in users' : 'Administrators only'} />
          <MetricRow label="Scale to zero" value={app.autoscale === 'None' ? 'Never' : app.autoscale === 'Idle15m' ? 'After 15 min idle' : 'After 1 hour idle'} />
          <MetricRow label="Target port" value={app.targetPort ?? 'default (80)'} />
          <MetricRow label="Container" value={app.containerName ?? (app.externalUrl ? 'external link' : 'not linked')} />
          {app.externalUrl && <MetricRow label="External URL" value={<span className="font-mono text-[11px]">{app.externalUrl}</span>} />}
          <MetricRow label="Discovered" value={app.autoDiscovered ? 'automatically' : 'manually added'} />
          {app.hidden && <MetricRow label="Visibility" value="hidden from the portal" />}
          {live && app.containerName && (
            <div className="mt-3">
              <ContainerStatsPanel id={app.containerName} />
            </div>
          )}
        </Expand>
        <div className="flex min-w-0 items-center gap-2">
          <StatusPill status={app.auth === 'Admin' ? 'admin' : app.auth} />
          <span className={classNames('truncate text-[10px]', app.auth === 'Admin' ? 'text-red-300' : app.auth === 'Sso' ? 'text-amber-300' : 'text-emerald-300')}>
            {accessLabel}
          </span>
          <span className="ml-auto shrink-0">
            <InfoTip label="Access">
              <strong>Public</strong> is open to anyone who can reach the host. <strong>Signed-in users</strong>{' '}
              requires any DockManager account. <strong>Administrators</strong> restricts it to admins.
            </InfoTip>
          </span>
        </div>
        {/* flex-wrap: with the card in a 3–4 column grid the action row is wider than the
            card, so the icon buttons wrap to a second line instead of drawing outside it. */}
        <div className="flex flex-wrap items-center gap-1.5">
          <button type="button" className="btn-primary flex-1 px-3 py-1.5 text-xs" onClick={onOpen}>
            <Icon name="externalLink" className="h-3.5 w-3.5" /> Open
          </button>
          {app.containerName && (
            <button type="button" className="btn-ghost px-2.5 py-1.5 text-xs" onClick={onTerminal} title="Open a shell" aria-label="Open a shell">
              <Icon name="terminal" className="h-3.5 w-3.5" />
            </button>
          )}
          {canEdit && (
            <>
              <button
                type="button"
                className="btn-ghost px-2 py-1.5 text-xs"
                onClick={onMoveUp}
                disabled={!canMoveUp}
                title="Move earlier"
                aria-label="Move this app earlier"
              >
                <Icon name="chevronDown" className="h-3.5 w-3.5 rotate-180" />
              </button>
              <button
                type="button"
                className="btn-ghost px-2 py-1.5 text-xs"
                onClick={onMoveDown}
                disabled={!canMoveDown}
                title="Move later"
                aria-label="Move this app later"
              >
                <Icon name="chevronDown" className="h-3.5 w-3.5" />
              </button>
            </>
          )}
          {canEdit && (
            <button
              type="button"
              className="btn-ghost px-2.5 py-1.5 text-xs"
              onClick={onToggleHidden}
              title={app.hidden ? 'Show on the portal' : 'Hide from the portal'}
              aria-label={app.hidden ? 'Show this app on the portal' : 'Hide this app from the portal'}
            >
              <Icon name={app.hidden ? 'eye' : 'eyeOff'} className="h-3.5 w-3.5" />
            </button>
          )}
          {canEdit && (
            <button type="button" className="btn-ghost px-2.5 py-1.5 text-xs" onClick={onEdit} title="Edit this app" aria-label="Edit this app">
              <Icon name="pencil" className="h-3.5 w-3.5" />
            </button>
          )}
          {canEdit && !app.self && (
            <ConfirmButton
              className="px-2.5 py-1.5"
              label="Delete"
              confirmLabel="Delete app"
              onConfirm={onDelete}
            />
          )}
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
  notify: Notify;
}) {
  const [draft, setDraft] = useState<PortalApp>(app);
  const [busy, setBusy] = useState(false);
  const isNew = app.id === 0;

  const save = async () => {
    if (!draft.name.trim() || !draft.subdomain.trim()) {
      notify('Name and subdomain are required', 'error');
      return;
    }
    setBusy(true);
    try {
      await api.savePortalApp(draft);
      notify(isNew ? 'Application added' : 'Application saved', 'success');
      onSaved();
    } catch (error) {
      notify((error as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={isNew ? 'Add application' : `Edit ${app.name}`} onClose={onClose}>
      <div className="space-y-3">
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Name">
            <input className="input" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} placeholder="Nextcloud" />
          </Field>
          <Field label="Subdomain" hint={`Reachable at https://${draft.subdomain || 'sub'}.${'dock.lan'}`}>
            <input className="input" value={draft.subdomain} onChange={(e) => setDraft({ ...draft, subdomain: e.target.value })} placeholder="nextcloud" />
          </Field>
        </div>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Category">
            <input className="input" value={draft.category} onChange={(e) => setDraft({ ...draft, category: e.target.value })} placeholder="Apps" />
          </Field>
          <Field label="Icon URL" hint="A favicon or PNG; leave blank for the initial letter.">
            <input className="input" value={draft.icon ?? ''} onChange={(e) => setDraft({ ...draft, icon: e.target.value })} />
          </Field>
        </div>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Access" info="Public needs no login; SSO requires any signed-in user; Admin is restricted to administrators.">
            <select className="input" value={draft.auth} onChange={(e) => setDraft({ ...draft, auth: e.target.value as PortalApp['auth'] })}>
              <option value="Public">Public</option>
              <option value="Sso">Signed-in users</option>
              <option value="Admin">Administrators</option>
            </select>
          </Field>
          <Field label="Scale to zero" info="Idle apps are stopped automatically and woken on the next request.">
            <select className="input" value={draft.autoscale} onChange={(e) => setDraft({ ...draft, autoscale: e.target.value as PortalApp['autoscale'] })}>
              <option value="None">Never</option>
              <option value="Idle15m">After 15 minutes idle</option>
              <option value="Idle1h">After 1 hour idle</option>
            </select>
          </Field>
        </div>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Target port" hint="Container port to proxy to (default 80).">
            <input className="input" type="number" value={draft.targetPort ?? ''} onChange={(e) => setDraft({ ...draft, targetPort: e.target.value ? Number(e.target.value) : null })} />
          </Field>
          <Field label="External URL" hint="Set to link to an external service instead of a container.">
            <input className="input" value={draft.externalUrl ?? ''} onChange={(e) => setDraft({ ...draft, externalUrl: e.target.value })} placeholder="https://10.10.1.1" />
          </Field>
        </div>
        <div className="flex items-start gap-2">
          <Toggle checked={draft.hidden} onChange={(value) => setDraft({ ...draft, hidden: value })} label="Hide from the portal" />
          <div className="min-w-0">
            <div className="text-sm">Hide from the portal</div>
            <p className="text-[11px] text-slate-400">
              The app is removed from the portal list but stays reachable at its URL. Turn on “Show hidden” on
              the Portal to reveal it and unhide it again.
            </p>
          </div>
        </div>
        <div className="flex items-center justify-end gap-2 pt-2">
          {!isNew && draft.self && (
            <p className="mr-auto text-[11px] text-slate-400">
              DockManager's own entry — it can't be deleted from here.
            </p>
          )}
          {!isNew && !draft.self && (
            <ConfirmButton
              className="mr-auto"
              label="Delete"
              confirmLabel="Delete app"
              onConfirm={async () => {
                try {
                  await api.deletePortalApp(draft.subdomain);
                  notify('Application deleted', 'success');
                  onSaved();
                } catch (error) {
                  notify((error as Error).message, 'error');
                }
              }}
            />
          )}
          <button className="btn-ghost" onClick={onClose}>Cancel</button>
          <button className="btn-primary" disabled={busy} onClick={save}>{busy ? 'Saving…' : 'Save'}</button>
        </div>
      </div>
    </Modal>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// Portal — bulk add from one host's available apps
// ────────────────────────────────────────────────────────────────────────────
function PortalAvailableDialog({
  notify,
  onClose,
  onAdded
}: {
  notify: Notify;
  onClose: () => void;
  onAdded: () => void;
}) {
  const [hosts, setHosts] = useState<DockerHostInfo[]>([]);
  const [clusters, setClusters] = useState<KubeClusterInfo[]>([]);
  const [selection, setSelection] = useState('');
  const [candidates, setCandidates] = useState<PortalCandidate[]>([]);
  const [loading, setLoading] = useState(false);
  const [query, setQuery] = useState('');
  const [chosen, setChosen] = useState<Record<string, PortalAppInput>>({});
  const [busy, setBusy] = useState(false);

  const enabledHosts = useMemo(() => hosts.filter((host) => host.enabled), [hosts]);
  const enabledClusters = useMemo(() => clusters.filter((cluster) => cluster.enabled), [clusters]);

  useEffect(() => {
    Promise.all([api.hosts(), api.clusters()])
      .then(([hostList, clusterList]) => {
        setHosts(hostList);
        setClusters(clusterList);
        const docker = hostList.find((host) => host.enabled);
        const kubernetes = clusterList.find((cluster) => cluster.enabled);
        if (docker) setSelection(`docker:${docker.id}`);
        else if (kubernetes) setSelection(`k8s:${kubernetes.id}`);
      })
      .catch((error) => notify((error as Error).message, 'error'));
  }, [notify]);

  useEffect(() => {
    if (!selection) return undefined;
    const [kind, id] = selection.split(':');
    setLoading(true);
    setChosen({});
    setCandidates([]);
    api
      .portalCandidates(kind === 'k8s' ? { clusterId: Number(id) } : { hostId: Number(id) })
      .then((result) => setCandidates(result.items))
      .catch((error) => notify((error as Error).message, 'error'))
      .finally(() => setLoading(false));
    return undefined;
  }, [selection, notify]);

  const draftFrom = (candidate: PortalCandidate): PortalAppInput => ({
    subdomain: candidate.subdomain,
    name: candidate.name,
    category: candidate.category,
    auth: 'Sso',
    autoscale: 'None',
    targetPort: candidate.targetPort ?? null,
    containerId: candidate.containerId ?? null,
    containerName: candidate.containerName ?? null,
    hostId: candidate.hostId ?? null,
    externalUrl: candidate.externalUrl ?? null
  });

  const toggle = (candidate: PortalCandidate) => {
    if (candidate.present) return;
    setChosen((current) => {
      const next = { ...current };
      if (next[candidate.key]) delete next[candidate.key];
      else next[candidate.key] = draftFrom(candidate);
      return next;
    });
  };

  const update = (key: string, patch: Partial<PortalAppInput>) =>
    setChosen((current) => (current[key] ? { ...current, [key]: { ...current[key], ...patch } } : current));

  const visible = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return candidates;
    return candidates.filter((candidate) =>
      `${candidate.name} ${candidate.subdomain} ${candidate.detail} ${candidate.source}`.toLowerCase().includes(q)
    );
  }, [candidates, query]);

  const selected = useMemo(() => Object.values(chosen), [chosen]);

  const selectAllVisible = () =>
    setChosen((current) => {
      const next = { ...current };
      for (const candidate of visible) {
        if (candidate.present || next[candidate.key]) continue;
        next[candidate.key] = draftFrom(candidate);
      }
      return next;
    });

  const add = async () => {
    if (selected.length === 0) return;
    setBusy(true);
    try {
      const result = await api.bulkAddPortalApps(selected);
      notify(`${result.added} app${result.added === 1 ? '' : 's'} added`, 'success');
      onAdded();
    } catch (error) {
      notify((error as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal wide title="Add available apps" onClose={onClose}>
      <div className="space-y-3">
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Host" hint="Pick a connected Docker host or Kubernetes cluster to load its apps.">
            <select className="input" value={selection} onChange={(event) => setSelection(event.target.value)}>
              <option value="">Select a host…</option>
              {enabledHosts.length > 0 && (
                <optgroup label="Docker hosts">
                  {enabledHosts.map((host) => (
                    <option key={`docker:${host.id}`} value={`docker:${host.id}`}>{host.name}</option>
                  ))}
                </optgroup>
              )}
              {enabledClusters.length > 0 && (
                <optgroup label="Kubernetes clusters">
                  {enabledClusters.map((cluster) => (
                    <option key={`k8s:${cluster.id}`} value={`k8s:${cluster.id}`}>{cluster.name}</option>
                  ))}
                </optgroup>
              )}
            </select>
          </Field>
          <div className="relative self-end">
            <Icon name="search" className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input className="input pl-9" placeholder="Search apps…" value={query} onChange={(event) => setQuery(event.target.value)} />
          </div>
        </div>

        <ul className="max-h-[45vh] space-y-2 overflow-y-auto pr-1">
          {visible.map((candidate) => {
            const draft = chosen[candidate.key];
            const controlId = `cand-${candidate.key.replace(/[^a-zA-Z0-9_-]/g, '-')}`;
            return (
              <li
                key={candidate.key}
                className={classNames(
                  'rounded-lg border p-3',
                  candidate.present ? 'border-ink-800 opacity-60' : draft ? 'border-accent-soft/60 bg-ink-900' : 'border-ink-800'
                )}
              >
                <div className="flex items-start gap-3">
                  <input
                    id={controlId}
                    type="checkbox"
                    className="mt-0.5"
                    checked={!!draft}
                    disabled={candidate.present}
                    onChange={() => toggle(candidate)}
                  />
                  <label htmlFor={controlId} className={classNames('min-w-0 flex-1', candidate.present ? 'cursor-default' : 'cursor-pointer')}>
                    <span className="flex flex-wrap items-center gap-2">
                      <span className="truncate text-sm font-medium">{candidate.name}</span>
                      <span className="rounded border border-ink-700 bg-ink-800 px-1.5 py-0.5 text-[10px] uppercase tracking-wide text-slate-400">
                        {candidate.source}
                      </span>
                      {candidate.present && (
                        <span className="rounded border border-emerald-800/60 bg-emerald-950/60 px-1.5 py-0.5 text-[10px] text-emerald-300">
                          already added
                        </span>
                      )}
                    </span>
                    <span className="mt-0.5 block truncate text-[11px] text-slate-400">{candidate.detail}</span>
                  </label>
                </div>

                {draft && (
                  <div className="mt-3 grid gap-2 border-t border-ink-800 pt-3 sm:grid-cols-2">
                    <Field label="Name">
                      <input className="input" value={draft.name} onChange={(event) => update(candidate.key, { name: event.target.value })} />
                    </Field>
                    <Field label="Subdomain" hint="The address under the portal domain; must be unique.">
                      <input className="input" value={draft.subdomain} onChange={(event) => update(candidate.key, { subdomain: event.target.value })} />
                    </Field>
                    <Field label="Category">
                      <input className="input" value={draft.category} onChange={(event) => update(candidate.key, { category: event.target.value })} />
                    </Field>
                    {candidate.source === 'container' ? (
                      <Field label="Target port" hint="Container port to proxy to.">
                        <input
                          className="input"
                          type="number"
                          value={draft.targetPort ?? ''}
                          onChange={(event) => update(candidate.key, { targetPort: event.target.value ? Number(event.target.value) : null })}
                        />
                      </Field>
                    ) : (
                      <Field label="External URL" info="Kubernetes Service address or Ingress/HTTPRoute host the portal proxies to.">
                        <input className="input" value={draft.externalUrl ?? ''} onChange={(event) => update(candidate.key, { externalUrl: event.target.value })} />
                      </Field>
                    )}
                    <Field label="Access" info="Public needs no login; SSO requires any signed-in user; Admin is restricted to administrators.">
                      <select className="input" value={draft.auth} onChange={(event) => update(candidate.key, { auth: event.target.value as PortalApp['auth'] })}>
                        <option value="Public">Public</option>
                        <option value="Sso">Signed-in users</option>
                        <option value="Admin">Administrators</option>
                      </select>
                    </Field>
                    <Field label="Scale to zero" info="Idle apps are stopped automatically and woken on the next request.">
                      <select className="input" value={draft.autoscale} onChange={(event) => update(candidate.key, { autoscale: event.target.value as PortalApp['autoscale'] })}>
                        <option value="None">Never</option>
                        <option value="Idle15m">After 15 minutes idle</option>
                        <option value="Idle1h">After 1 hour idle</option>
                      </select>
                    </Field>
                  </div>
                )}
              </li>
            );
          })}
        </ul>

        {!loading && visible.length === 0 && (
          <p className="py-6 text-center text-xs text-slate-400">
            {selection
              ? 'Nothing available here — every app on this host is already in the portal.'
              : 'Select a host to load its apps.'}
          </p>
        )}
        {loading && <p className="py-6 text-center text-xs text-slate-400">Loading apps…</p>}

        <div className="flex flex-wrap items-center justify-between gap-2 border-t border-ink-800 pt-3">
          <div className="text-xs text-slate-400">
            {visible.length} available · {selected.length} selected
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <button className="btn-ghost" onClick={selectAllVisible} disabled={loading || visible.length === 0}>Select all</button>
            <button className="btn-ghost" onClick={() => setChosen({})} disabled={selected.length === 0}>Clear</button>
            <button className="btn-ghost" onClick={onClose}>Cancel</button>
            <button className="btn-primary" onClick={add} disabled={busy || selected.length === 0}>
              {busy ? 'Adding…' : `Add selected${selected.length ? ` (${selected.length})` : ''}`}
            </button>
          </div>
        </div>
      </div>
    </Modal>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// Shared SSE hook
// ────────────────────────────────────────────────────────────────────────────
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
// View 2 — Dashboard
// ────────────────────────────────────────────────────────────────────────────
function DashboardView({
  activeHost,
  setActiveHost,
  notify,
  setView,
  isAdmin,
  session
}: {
  activeHost?: number;
  setActiveHost: (id?: number) => void;
  notify: Notify;
  setView: (v: ViewKey) => void;
  isAdmin: boolean;
  session: SessionUser;
}) {
  const [info, setInfo] = useState<SystemInfo | null>(null);
  const [data, setData] = useState<DashboardResponse | null>(null);
  const [events, setEvents] = useState<AuditLogEntry[]>([]);
  const [cves, setCves] = useState<CveFinding[]>([]);
  const [scanning, setScanning] = useState(false);
  const [refresh, setRefresh] = usePersistentState<number>('ehdm.refresh.dashboard', 10);
  const [sortKey, setSortKey] = useState<'cpu' | 'mem' | 'net' | 'size' | 'name'>('cpu');
  const [expandedContainer, setExpandedContainer] = useState<string | null>(null);

  const load = useCallback(
    (fresh = false) => {
      api.systemInfo().then(setInfo).catch(() => undefined);
      if (!isAdmin) return;
      api.dashboard(activeHost, fresh).then(setData).catch((e) => notify((e as Error).message, 'error'));
      api.audit(50).then(setEvents).catch(() => undefined);
    },
    [activeHost, isAdmin, notify]
  );

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    if (!isAdmin || refresh <= 0) return undefined;
    const timer = window.setInterval(() => load(), refresh * 1000);
    return () => window.clearInterval(timer);
  }, [isAdmin, refresh, load]);

  useEventSource<AuditLogEntry>(isAdmin ? '/api/events/stream' : null, (entry) =>
    setEvents((prev) => [entry, ...prev].slice(0, 100))
  );

  const overall = data?.overall;
  const uptimeHours = Math.floor((info?.uptimeSeconds ?? 0) / 3600);
  const uptimeLabel = uptimeHours >= 24 ? `${Math.floor(uptimeHours / 24)}d ${uptimeHours % 24}h` : `${uptimeHours}h`;
  const memPct = overall && overall.memoryTotalMb > 0 ? (overall.memoryUsedMb / overall.memoryTotalMb) * 100 : 0;

  const sortedContainers = useMemo(() => {
    const list = [...(data?.containers ?? [])];
    list.sort((a, b) => {
      switch (sortKey) {
        case 'mem':
          return b.memoryMb - a.memoryMb;
        case 'net':
          return b.networkRxBytes + b.networkTxBytes - (a.networkRxBytes + a.networkTxBytes);
        case 'size':
          return b.sizeRwBytes - a.sizeRwBytes;
        case 'name':
          return a.name.localeCompare(b.name);
        default:
          return b.cpuPercent - a.cpuPercent;
      }
    });
    return list;
  }, [data, sortKey]);

  const scan = async () => {
    const target = data?.containers.find((c) => c.state === 'running');
    if (!target) {
      notify('No running container to scan.', 'error');
      return;
    }
    setScanning(true);
    try {
      const findings = await api.cveScan(target.id);
      setCves(findings);
      notify(`${findings.length} findings`, findings.length ? 'error' : 'success');
    } catch (error) {
      notify((error as Error).message, 'error');
    } finally {
      setScanning(false);
    }
  };

  if (!isAdmin) {
    return (
      <div className="space-y-5">
        <PageHeader title="Dashboard" icon="gauge" subtitle={`Signed in as ${session.username}`} info="A summary of the control plane." />
        <Callout tone="info" title="You have viewer access">
          Your account can open published applications but cannot change the system. Ask an administrator for
          the Admin role if you need management access.
        </Callout>
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          <StatTile
            label="Portal apps"
            value={String(info?.routes ?? 0)}
            icon="layoutGrid"
            hint="published routes"
            onClick={() => setView('portal')}
            info="Applications published behind the port-443 portal. Open the Portal to launch one."
          />
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-5">
      <PageHeader
        title="Dashboard"
        icon="gauge"
        subtitle={`Health and activity on ${data?.hostName ?? 'the selected host'}.`}
        info="Overall totals, a live per-container breakdown and one panel per Docker host. The data refreshes every 12 seconds; every tile links to the area it describes."
        actions={
          <>
            <RefreshControl value={refresh} onChange={setRefresh} />
            <button className="btn-ghost" onClick={() => load(true)}>
              <Icon name="refreshCw" className="h-4 w-4" /> Refresh
            </button>
          </>
        }
      />

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6">
        <StatTile
          label="CPU"
          value={overall ? `${overall.cpuPercent.toFixed(1)}%` : '—'}
          icon="cpu"
          tone={overall && overall.cpuPercent > 85 ? 'warn' : 'accent'}
          hint={overall ? `${overall.cpuCores} cores` : 'loading…'}
          onClick={() => setView('containers')}
          info="Sum of the CPU usage of every running container on the selected host, as a percentage of all cores."
        />
        <StatTile
          label="Memory"
          value={overall ? formatBytes(overall.memoryUsedMb * 1024 * 1024) : '—'}
          icon="memoryStick"
          tone={memPct > 85 ? 'warn' : 'accent'}
          hint={overall ? `of ${formatBytes(overall.memoryTotalMb * 1024 * 1024)} · ${memPct.toFixed(0)}%` : 'loading…'}
          onClick={() => setView('containers')}
          info="Memory used by running containers against the host's total RAM."
        />
        <StatTile
          label="Disk"
          value={overall ? formatBytes(overall.diskTotalBytes) : '—'}
          icon="hardDrive"
          hint={overall ? `images ${formatBytes(overall.diskImagesBytes)} · layers ${formatBytes(overall.diskContainersBytes)}` : 'loading…'}
          onClick={() => setView('images')}
          info="Images, container layers, volumes and build cache reported by Docker (system df). Click to prune."
        />
        <StatTile
          label="Traffic"
          value={overall ? formatBytes(overall.networkRxBytes + overall.networkTxBytes) : '—'}
          icon="network"
          hint={overall ? `↓ ${formatBytes(overall.networkRxBytes)} · ↑ ${formatBytes(overall.networkTxBytes)}` : 'loading…'}
          info="Cumulative network received and sent by the running containers since they started."
        />
        <StatTile
          label="Ports"
          value={overall ? String(overall.publishedPorts) : '—'}
          icon="plug"
          hint="published to the host"
          onClick={() => setView('portal')}
          info="Host ports published by running containers. Portal apps are additionally reverse-proxied on port 443."
        />
        <StatTile
          label="Containers"
          value={overall ? `${overall.containersRunning}/${overall.containersTotal}` : '—'}
          icon="box"
          tone={overall && overall.containersRunning === overall.containersTotal ? 'ok' : 'warn'}
          hint={overall
            ? `${overall.containersRunning} running · ${overall.containersPaused} paused · ${overall.containersStopped} stopped`
            : 'loading…'}
          onClick={() => setView('containers')}
          info="Running containers versus the total on the selected host."
        />
      </div>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-5">
        <StatTile
          label="Images"
          value={String(overall?.images ?? 0)}
          icon="disc"
          hint={overall ? `${formatBytes(overall.diskImagesBytes)} on disk` : 'loading…'}
          onClick={() => setView('images')}
          info="Local images on the selected host and the disk space they occupy."
        />
        <StatTile
          label="Proxy routes"
          value={String(info?.routes ?? 0)}
          icon="network"
          hint={info?.portalDomain ?? ''}
          onClick={() => setView('portal')}
          info="Dynamic YARP routes published for portal apps on port 443."
        />
        <StatTile
          label="Docker hosts"
          value={String(info?.dockerHosts ?? 0)}
          icon="server"
          hint="local + remote"
          onClick={() => setView('hosts')}
          info="Docker endpoints DockManager can reach, including mTLS agents."
        />
        <StatTile
          label="K8s clusters"
          value={String(info?.kubeClusters ?? 0)}
          icon="hexagon"
          hint="connected"
          onClick={() => setView('kubernetes')}
          info="Kubernetes clusters imported for pod and deployment management."
        />
        <StatTile
          label="Uptime"
          value={uptimeLabel}
          icon="clock"
          hint={`v${info?.version ?? '?'}`}
          onClick={() => setView('settings')}
          info="How long this control plane has been running. Click for system information."
        />
      </div>

      <Section
        title={`Containers (${data?.containers.length ?? 0})`}
        description="Live CPU, memory, network, ports and writable-layer size."
        info="One Docker stats sample per running container, fetched with a bounded fan-out and cached briefly. Stopped containers show their disk size only."
        actions={
          <select className="input w-40 py-1 text-xs" value={sortKey} onChange={(e) => setSortKey(e.target.value as typeof sortKey)} aria-label="Sort containers">
            <option value="cpu">Sort by CPU</option>
            <option value="mem">Sort by memory</option>
            <option value="net">Sort by network</option>
            <option value="size">Sort by size</option>
            <option value="name">Sort by name</option>
          </select>
        }
      >
        <div className="card hidden overflow-x-auto p-0 md:block">
          <table className="w-full text-sm">
            <thead className="text-left text-xs uppercase tracking-wide text-slate-400">
              <tr className="border-b border-ink-800">
                <th className="px-3 py-2">Name</th>
                <th className="px-3 py-2">State</th>
                <th className="px-3 py-2 text-right">CPU</th>
                <th className="px-3 py-2 text-right">Memory</th>
                <th className="px-3 py-2 text-right">Network</th>
                <th className="px-3 py-2">Ports</th>
                <th className="px-3 py-2 text-right">Size</th>
              </tr>
            </thead>
            <tbody>
              {sortedContainers.map((container) => (
                <Fragment key={container.id}>
                  <tr className="table-row">
                    <td className="px-3 py-2">
                      <div className="flex items-start gap-1.5">
                        <button
                          type="button"
                          className="mt-0.5 shrink-0 text-slate-400 hover:text-accent"
                          aria-label={expandedContainer === container.id ? 'Hide container details' : 'Show container details'}
                          onClick={() => setExpandedContainer(expandedContainer === container.id ? null : container.id)}
                        >
                          <Icon name={expandedContainer === container.id ? 'chevronDown' : 'chevronRight'} className="h-3.5 w-3.5" />
                        </button>
                        <div className="min-w-0">
                          <div className="truncate font-medium">{container.name}</div>
                          <div className="truncate text-[11px] text-slate-400">{container.image}</div>
                        </div>
                      </div>
                    </td>
                    <td className="px-3 py-2"><StatusPill status={container.state} /></td>
                    <td className="px-3 py-2 text-right tabular-nums">{formatPercent(container.cpuPercent)}</td>
                    <td className="px-3 py-2 text-right tabular-nums">{formatBytes(container.memoryMb * 1024 * 1024)}</td>
                    <td className="px-3 py-2 text-right tabular-nums text-xs text-slate-400">
                      ↓ {formatBytes(container.networkRxBytes)} ↑ {formatBytes(container.networkTxBytes)}
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex flex-wrap gap-1">
                        {container.ports.length === 0 && <span className="text-xs text-slate-400">—</span>}
                        {container.ports.slice(0, 4).map((port) => (
                          <span key={port} className="badge border border-ink-700 bg-ink-800/60 font-mono text-[10px] normal-case text-slate-300">{port}</span>
                        ))}
                        {container.ports.length > 4 && <span className="text-[10px] text-slate-400">+{container.ports.length - 4}</span>}
                      </div>
                    </td>
                    <td className="px-3 py-2 text-right tabular-nums">{formatBytes(container.sizeRwBytes)}</td>
                  </tr>
                  {expandedContainer === container.id && (
                    <tr className="bg-ink-900/40">
                      <td colSpan={7} className="px-3 py-3">
                        <ContainerStatsPanel id={container.id} hostId={activeHost} ports={container.ports} created={container.created} sizeRwBytes={container.sizeRwBytes} />
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
              {sortedContainers.length === 0 && (
                <tr><td colSpan={7} className="px-3 py-8 text-center text-slate-400">No containers on this host.</td></tr>
              )}
            </tbody>
          </table>
        </div>

        <div className="space-y-2 md:hidden">
          {sortedContainers.map((container) => (
            <div key={container.id} className="rounded-lg border border-ink-800 bg-ink-900/50 p-3">
              <div className="flex items-center gap-2">
                <div className="min-w-0 flex-1">
                  <div className="truncate text-sm font-medium">{container.name}</div>
                  <div className="truncate text-[11px] text-slate-400">{container.image}</div>
                </div>
                <StatusPill status={container.state} />
              </div>
              <div className="mt-2 grid grid-cols-2 gap-1 text-[11px] text-slate-400">
                <span>CPU <span className="text-slate-200">{formatPercent(container.cpuPercent)}</span></span>
                <span>Memory <span className="text-slate-200">{formatBytes(container.memoryMb * 1024 * 1024)}</span></span>
                <span>In <span className="text-slate-200">{formatBytes(container.networkRxBytes)}</span></span>
                <span>Out <span className="text-slate-200">{formatBytes(container.networkTxBytes)}</span></span>
                <span>Size <span className="text-slate-200">{formatBytes(container.sizeRwBytes)}</span></span>
              </div>
              <div className="mt-2">
                <Expand title="More info" hint="CPU, memory, IO, network">
                  <ContainerStatsPanel id={container.id} hostId={activeHost} ports={container.ports} created={container.created} sizeRwBytes={container.sizeRwBytes} />
                </Expand>
              </div>
              {container.ports.length > 0 && (
                <div className="mt-2 flex flex-wrap gap-1">
                  {container.ports.map((port) => (
                    <span key={port} className="badge border border-ink-700 bg-ink-800/60 font-mono text-[10px] normal-case text-slate-300">{port}</span>
                  ))}
                </div>
              )}
            </div>
          ))}
          {sortedContainers.length === 0 && <EmptyState icon="box" title="No containers on this host" />}
        </div>
      </Section>

      <Section
        title="Hosts"
        description="Overall totals per Docker host. Click a panel to switch the active host."
        info="Each panel aggregates the host's own running containers, so you can compare machines at a glance. Offline hosts report their last error."
      >
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {(data?.hosts ?? []).map((host) => {
            const hostMemPct = host.memoryTotalMb > 0 ? (host.memoryUsedMb / host.memoryTotalMb) * 100 : 0;
            const active = (host.id ?? undefined) === activeHost;
            return (
              <button
                key={host.id ?? 'local'}
                type="button"
                onClick={() => setActiveHost(host.id ?? undefined)}
                className={classNames(
                  'card flex flex-col gap-2 p-3 text-left outline outline-1 transition-colors',
                  active ? 'outline-accent-soft/60' : 'outline-transparent hover:outline-accent-soft/40'
                )}
              >
                <div className="flex items-center gap-2">
                  <Icon name={host.kind === 'UnixSocket' ? 'plug' : 'server'} className="h-4 w-4 text-slate-400" />
                  <span className="truncate text-sm font-medium">{host.name}</span>
                  <span className="ml-auto"><StatusPill status={host.online ? 'online' : 'offline'} /></span>
                </div>
                {host.error ? (
                  <div className="text-[11px] text-red-300">{host.error}</div>
                ) : (
                  <>
                    <div className="grid grid-cols-2 gap-1 text-[11px] text-slate-400">
                      <span>CPU <span className="text-slate-200">{host.cpuPercent.toFixed(1)}%</span> <span className="text-slate-400">({host.cpuCores}c)</span></span>
                      <span>Mem <span className="text-slate-200">{formatBytes(host.memoryUsedMb * 1024 * 1024)}</span></span>
                      <span>Disk <span className="text-slate-200">{formatBytes(host.diskUsedBytes)}</span></span>
                      <span>Containers <span className="text-slate-200">{host.containersRunning}/{host.containersTotal}</span></span>
                    </div>
                    <div className="text-[10px] text-slate-400">Memory {hostMemPct.toFixed(0)}% of {formatBytes(host.memoryTotalMb * 1024 * 1024)} · {host.images} images</div>
                  </>
                )}
              </button>
            );
          })}
          {(data?.hosts.length ?? 0) === 0 && <div className="text-sm text-slate-400">No Docker hosts configured.</div>}
        </div>
      </Section>

      <NetworkSection network={data?.network} onConfigure={() => setView('settings')} />

      <div className="grid gap-4 lg:grid-cols-2">
        <Section
          className="min-w-0"
          title="Provenance event feed"
          description="WebUI · MCP · Updater · CLI"
          info="Every action is stamped with the actor that performed it, so you can tell Web UI, MCP, auto-updater and external CLI changes apart."
        >
          <div className="max-h-80 space-y-1 overflow-y-auto font-mono text-xs">
            {events.map((event) => (
              <div key={`${event.id}-${event.timestamp}`} className="flex items-center gap-2 border-b border-ink-800/60 py-1">
                <ActorBadge actor={event.actor} />
                {event.actorDetail && (
                  <span
                    className="max-w-[9rem] truncate rounded border border-ink-700 bg-ink-800/60 px-1.5 py-0.5 text-[10px] text-slate-400"
                    title={`${event.actor} · ${event.actorDetail}`}
                  >
                    {event.actorDetail}
                  </span>
                )}
                <span className="text-slate-300">{event.action}</span>
                <span className="truncate text-slate-400">{event.target}</span>
                <span className="ml-auto shrink-0 text-[10px] text-slate-400">{new Date(event.timestamp).toLocaleTimeString()}</span>
              </div>
            ))}
            {events.length === 0 && <div className="py-6 text-center text-slate-400">No events yet.</div>}
          </div>
        </Section>

        <Section
          className="min-w-0"
          title="Vulnerability summary"
          description="Agentless CVE scan via OSV.dev"
          info="Reads the package database inside a running container and queries OSV.dev. No agent is installed."
          actions={
            <button className="btn-ghost px-2 py-1 text-xs" disabled={scanning} onClick={scan}>
              <Icon name="shieldCheck" className="h-3.5 w-3.5" /> {scanning ? 'Scanning…' : 'Scan a container'}
            </button>
          }
        >
          <div className="max-h-80 space-y-1 overflow-y-auto text-xs">
            {cves.map((cve) => (
              <div key={cve.id + cve.package} className="flex items-center gap-2 border-b border-ink-800/60 py-1">
                <span className="text-red-300">{cve.id}</span>
                <span className="text-slate-300">{cve.package}</span>
                <span className="text-slate-400">{cve.version}</span>
              </div>
            ))}
            {cves.length === 0 && <div className="py-6 text-center text-slate-400">No scan run yet.</div>}
          </div>
        </Section>
      </div>
    </div>
  );
}

/** Network traffic panel: rates chart, 1h/24h totals and expandable counters/interfaces/TCP health.
 *  Shared by the Dashboard and the dedicated network areas so the labels stay identical. */
function NetworkSection({ network, onConfigure }: { network?: NetworkSummary; onConfigure?: () => void }) {
  if (!network) {
    return (
      <Section
        title="Network traffic"
        info="In and out traffic sampled every 30 seconds and kept for 24 hours, with connection resets, drops and errors where the host exposes them."
      >
        <div className="text-sm text-slate-400">
          Collecting samples… the traffic timeline appears after the first minute of runtime.
        </div>
      </Section>
    );
  }

  const inRates = network.history.map((point) => point.rxBytesPerSec);
  const outRates = network.history.map((point) => point.txBytesPerSec);
  const last = network.history[network.history.length - 1];
  const tcp = network.hostTcp;
  const tracking = network.trackingMinutes > 0 ? `tracking ${formatDuration(network.trackingMinutes * 60)}` : 'starting…';

  const stat = (label: string, value: string, sub: string, icon: IconName, tone: string) => (
    <div className="card flex items-center gap-3 p-3">
      <span className={classNames('inline-flex h-9 w-9 items-center justify-center rounded-lg border border-ink-700 bg-ink-900/70', tone)}>
        <Icon name={icon} className="h-4 w-4" />
      </span>
      <div className="min-w-0">
        <div className="truncate text-lg font-semibold tabular-nums">{value}</div>
        <div className="text-[11px] text-slate-400">{label} · {sub}</div>
      </div>
    </div>
  );

  return (
    <Section
      title="Network traffic"
      description="Received / sent by running containers, with 1-hour and 24-hour totals."
      info="Sampled every 30 seconds and retained for 24 hours. Connection resets, retransmits, drops and errors are shown where the host exposes them; expand for the details."
      actions={<span className="text-[11px] text-slate-400">{tracking}</span>}
    >
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        {stat('received', formatBytes(network.rxLast1hBytes), 'last hour', 'download', 'text-sky-300')}
        {stat('sent', formatBytes(network.txLast1hBytes), 'last hour', 'arrowUpRight', 'text-emerald-300')}
        {stat('received', formatBytes(network.rxLast24hBytes), 'last 24h', 'download', 'text-sky-300')}
        {stat('sent', formatBytes(network.txLast24hBytes), 'last 24h', 'arrowUpRight', 'text-emerald-300')}
      </div>

      <div className="mt-3 rounded-xl border border-ink-800 bg-ink-900/40 p-3">
        <div className="mb-1 flex items-center justify-between text-xs text-slate-400">
          <span>Throughput (per second)</span>
          <span>
            ↓ {last ? formatRate(last.rxBytesPerSec) : '—'} · ↑ {last ? formatRate(last.txBytesPerSec) : '—'}
          </span>
        </div>
        <TrafficChart inData={inRates} outData={outRates} />
      </div>

      {network.system && (
        <div className="mt-3 grid grid-cols-2 gap-2 text-[11px] text-slate-400 sm:grid-cols-4">
          <span>Load <span className="tabular-nums text-slate-200">{network.system.load1.toFixed(2)}</span></span>
          <span>Processes <span className="tabular-nums text-slate-200">{formatNumber(network.system.processesTotal)}</span></span>
          <span>Blocked <span className="tabular-nums text-slate-200">{formatNumber(network.system.processesBlocked)}</span></span>
          <span>Memory free <span className="tabular-nums text-slate-200">{formatBytes(network.system.memoryAvailableKb * 1024)}</span></span>
        </div>
      )}

      <div className="mt-3 grid gap-2 sm:grid-cols-2">
        <Expand title="Counters & issues" hint={`${network.rxDropped + network.txDropped} dropped`}>
          <MetricRow label="Packets received" value={formatNumber(network.rxPackets)} />
          <MetricRow label="Packets sent" value={formatNumber(network.txPackets)} />
          <MetricRow label="Receive errors" value={formatNumber(network.rxErrors)} />
          <MetricRow label="Transmit errors" value={formatNumber(network.txErrors)} />
          <MetricRow label="Receive drops" value={formatNumber(network.rxDropped)} />
          <MetricRow label="Transmit drops" value={formatNumber(network.txDropped)} />
        </Expand>

        <Expand title="Connection health (TCP)" hint={tcp ? `${formatNumber(tcp.estabResets)} resets` : 'not available'}>
          {tcp ? (
            <>
              <MetricRow label="Established connections" value={formatNumber(tcp.currEstab)} />
              <MetricRow label="Outgoing connection attempts" value={formatNumber(tcp.activeOpens)} />
              <MetricRow label="Incoming connection attempts" value={formatNumber(tcp.passiveOpens)} />
              <MetricRow label="Failed connection attempts" value={formatNumber(tcp.attemptFails)} />
              <MetricRow label="Connection resets (established)" value={formatNumber(tcp.estabResets)} />
              <MetricRow label="Segments retransmitted" value={formatNumber(tcp.retransSegs)} />
              <MetricRow label="Incoming errors" value={formatNumber(tcp.inErrs)} />
              <MetricRow label="Resets sent" value={formatNumber(tcp.outRsts)} />
            </>
          ) : (
            <div className="py-2 text-slate-400">
              TCP counters are not exposed inside this container. Run DockManager with access to the host
              <code className="mx-1 font-mono">/proc/net/snmp</code> to see connection resets and retransmits.
            </div>
          )}
        </Expand>

        {network.system && (
          <Expand
            title="Host load & processes"
            hint={`load ${network.system.load1.toFixed(2)} · ${formatNumber(network.system.processesTotal)} processes`}
          >
            <MetricRow label="Load average (1 / 5 / 15 min)" value={`${network.system.load1.toFixed(2)} / ${network.system.load5.toFixed(2)} / ${network.system.load15.toFixed(2)}`} />
            <MetricRow label="CPU cores" value={formatNumber(network.system.cpuCores)} />
            <MetricRow label="Processes running" value={formatNumber(network.system.processesRunning)} hint={`of ${formatNumber(network.system.processesTotal)}`} />
            <MetricRow label="Processes blocked" value={formatNumber(network.system.processesBlocked)} />
            <MetricRow label="Processes created (forks)" value={formatNumber(network.system.forks)} />
            <MetricRow label="Memory total" value={formatBytes(network.system.memoryTotalKb * 1024)} />
            <MetricRow label="Memory available" value={formatBytes(network.system.memoryAvailableKb * 1024)} />
            {network.system.swapTotalKb > 0 && (
              <MetricRow label="Swap used" value={`${formatBytes((network.system.swapTotalKb - network.system.swapFreeKb) * 1024)} of ${formatBytes(network.system.swapTotalKb * 1024)}`} />
            )}
          </Expand>
        )}

        <Expand title={`Interfaces (${network.hostInterfaces.length})`} hint="host-level">
          {network.hostInterfaces.length === 0 && (
            <div className="py-2 text-slate-400">No host interfaces reported.</div>
          )}
          {network.hostInterfaces.map((iface) => (
            <div key={iface.name} className="border-b border-ink-800/50 py-1 last:border-b-0">
              <div className="flex items-center justify-between text-xs">
                <span className="font-mono text-slate-300">{iface.name}</span>
                <span className="tabular-nums text-slate-400">
                  ↓ {formatBytes(iface.rxBytes)} · ↑ {formatBytes(iface.txBytes)}
                </span>
              </div>
              <div className="flex gap-3 text-[10px] text-slate-400">
                <span>pkts ↓{formatNumber(iface.rxPackets)} ↑{formatNumber(iface.txPackets)}</span>
                <span>err ↓{formatNumber(iface.rxErrors)} ↑{formatNumber(iface.txErrors)}</span>
                <span>drop ↓{formatNumber(iface.rxDropped)} ↑{formatNumber(iface.txDropped)}</span>
              </div>
            </div>
          ))}
        </Expand>

        <Expand title="Recent samples" hint={`${network.history.length} points`}>
          <div className="max-h-48 overflow-y-auto font-mono text-[11px] text-slate-400">
            {[...network.history].reverse().slice(0, 30).map((point, index) => (
              <div key={`${point.timestamp}-${index}`} className="flex justify-between border-b border-ink-800/40 py-0.5">
                <span>{new Date(point.timestamp).toLocaleTimeString()}</span>
                <span className="text-sky-300">↓ {formatRate(point.rxBytesPerSec)}</span>
                <span className="text-emerald-300">↑ {formatRate(point.txBytesPerSec)}</span>
              </div>
            ))}
          </div>
        </Expand>
      </div>

      {onConfigure && (
        <div className="mt-3 text-right">
          <button type="button" className="btn-ghost px-2 py-1 text-xs" onClick={onConfigure}>
            <Icon name="sliders" className="h-3.5 w-3.5" /> Display settings
          </button>
        </div>
      )}
    </Section>
  );
}

function ActorBadge({ actor }: { actor: string }) {
  const tone: Record<string, string> = {
    WebUi: 'bg-sky-950/60 text-sky-300',
    Mcp: 'bg-violet-950/60 text-violet-300',
    Updater: 'bg-emerald-950/60 text-emerald-300',
    Cli: 'bg-amber-950/60 text-amber-300',
    System: 'bg-ink-800 text-slate-300'
  };
  return <span className={classNames('badge border border-transparent', tone[actor] ?? tone.System)}>{actor}</span>;
}

// ────────────────────────────────────────────────────────────────────────────
// View 3 — Stacks
// ────────────────────────────────────────────────────────────────────────────
function StacksView({ notify }: { notify: Notify }) {
  const [stacks, setStacks] = useState<StackSummary[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [yaml, setYaml] = useState('');
  const [original, setOriginal] = useState('');
  const [showDiff, setShowDiff] = useState(false);
  const [newOpen, setNewOpen] = useState(false);
  const [importOpen, setImportOpen] = useState(false);
  const [newName, setNewName] = useState('');
  const [importName, setImportName] = useState('');
  const [importUrl, setImportUrl] = useState('');
  const [converterInput, setConverterInput] = useState('docker run -d --name web -p 8080:80 -e TZ=UTC nginx:latest');
  const [converterOutput, setConverterOutput] = useState('');
  const [k8sOutput, setK8sOutput] = useState('');
  const [refresh, setRefresh] = usePersistentState<number>('ehdm.refresh.stacks', 30);
  const [stats, setStats] = useState<StackStats | null>(null);
  const [serverIssues, setServerIssues] = useState<YamlIssue[]>([]);
  const [validation, setValidation] = useState<{ valid: boolean; name?: string | null; serviceCount: number } | null>(null);

  const validateYaml = useCallback((next: string) => {
    api.validateStack(next)
      .then((result) => {
        setServerIssues(result.issues);
        setValidation({ valid: result.valid, name: result.name, serviceCount: result.serviceCount });
      })
      .catch(() => undefined);
  }, []);

  const load = useCallback(() => {
    api.stacks().then(setStacks).catch((e) => notify((e as Error).message, 'error'));
  }, [notify]);

  useEffect(load, [load]);

  useEffect(() => {
    if (refresh <= 0) return undefined;
    const timer = window.setInterval(load, refresh * 1000);
    return () => window.clearInterval(timer);
  }, [load, refresh]);

  const open = useCallback(
    async (name: string) => {
      try {
        const result = await api.stack(name);
        setSelected(name);
        setYaml(result.yaml);
        setOriginal(result.yaml);
        setShowDiff(false);
        setServerIssues([]);
        api.stackStats(name).then(setStats).catch(() => setStats(null));
        validateYaml(result.yaml);
      } catch (error) {
        notify((error as Error).message, 'error');
      }
    },
    [notify]
  );

  return (
    <div className="space-y-4">
      <PageHeader
        title="Compose stacks"
        icon="layers"
        subtitle="Compose files stored on disk so manual docker compose stays compatible."
        info="Each stack is a docker-compose.yaml plus an optional encrypted .env. Deploy recreates only the services whose image changed; the auto-updater can pull, health-guard for 25s and roll back on failure."
        actions={
          <>
            <RefreshControl value={refresh} onChange={setRefresh} />
            <button className="btn-ghost" onClick={() => setImportOpen(true)}><Icon name="download" className="h-4 w-4" /> <span className="hidden sm:inline">Import URL</span></button>
            <button className="btn-primary" onClick={() => { setNewName(''); setNewOpen(true); }}><Icon name="plus" className="h-4 w-4" /> New stack</button>
          </>
        }
      />

      <div className="grid gap-4 lg:grid-cols-[280px_1fr]">
        <div className="card divide-y divide-ink-800/70 p-0">
          {stacks.map((stack) => (
            <button
              key={stack.name}
              type="button"
              onClick={() => open(stack.name)}
              className={classNames('flex w-full items-center gap-2 px-3 py-2.5 text-left text-sm', selected === stack.name ? 'bg-accent-muted/40' : 'hover:bg-ink-800/60')}
            >
              <Icon name="layers" className="h-4 w-4 text-slate-400" />
              <span className="truncate">{stack.name}</span>
              <StatusPill status={stack.state} />
              {stack.autoUpdateEnabled && <span className="badge border border-cyan-800/60 bg-cyan-950/60 text-cyan-300">auto</span>}
            </button>
          ))}
          {stacks.length === 0 && <div className="p-4 text-sm text-slate-400">No stacks yet. Create one or import a compose file.</div>}
        </div>

        <div className="space-y-4">
          {selected ? (
            <Section
              title={selected}
              info="Deploy applies the YAML. start/stop/restart/down/update map to docker compose subcommands. Live diff compares your edits against the file on disk."
              actions={
                <>
                  <button className="btn-primary px-2 py-1 text-xs" onClick={async () => { await api.deployStack(selected); load(); notify('Deploy requested', 'success'); }}>
                    <Icon name="rocket" className="h-3.5 w-3.5" /> Deploy
                  </button>
                  {(['start', 'stop', 'restart', 'down', 'update'] as const).map((action) => (
                    <button key={action} className="btn-ghost px-2 py-1 text-xs" onClick={async () => { await api.stackAction(selected, action); load(); }}>
                      {action}
                    </button>
                  ))}
                  <button className="btn-ghost px-2 py-1 text-xs" onClick={() => setShowDiff((value) => !value)}>
                    <Icon name={showDiff ? 'pencil' : 'panels'} className="h-3.5 w-3.5" /> {showDiff ? 'Edit YAML' : 'Live diff'}
                  </button>
                </>
              }
            >
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
                <YamlEditor
                  value={yaml}
                  onChange={setYaml}
                  serverIssues={serverIssues}
                  validate={validateYaml}
                  className="h-96"
                  ariaLabel="Compose YAML for this stack"
                  placeholder="name: my-stack&#10;services:&#10;  app:&#10;    image: nginx:alpine"
                />
              )}

              <div className="mt-3 flex flex-wrap items-center gap-2">
                <button className="btn-primary px-3 py-1.5 text-xs" onClick={async () => {
                  const issues = mergeIssues(serverIssues, lintEnums(yaml));
                  const errors = issues.filter((issue) => issue.severity === 'error');
                  if (errors.length > 0) {
                    notify(`Fix ${errors.length} error(s) before saving — line ${errors[0].line}: ${errors[0].message}`, 'error');
                    return;
                  }
                  try {
                    await api.saveStack(selected, { yaml });
                    setOriginal(yaml);
                    load();
                    if (selected) api.stackStats(selected).then(setStats).catch(() => undefined);
                    notify('Stack saved', 'success');
                  } catch (error) {
                    notify((error as Error).message, 'error');
                  }
                }}>
                  <Icon name="check" className="h-3.5 w-3.5" /> Save
                </button>
                <button className="btn-ghost px-3 py-1.5 text-xs" onClick={() => validateYaml(yaml)}>
                  <Icon name="shieldCheck" className="h-3.5 w-3.5" /> Validate
                </button>
                <button className="btn-ghost px-3 py-1.5 text-xs" onClick={async () => { const result = await api.stack(selected); setYaml(result.yaml); setOriginal(result.yaml); validateYaml(result.yaml); notify('Reloaded from disk'); }}>
                  <Icon name="refreshCw" className="h-3.5 w-3.5" /> Reload
                </button>
                {validation && (
                  <span className="text-[11px] text-slate-400">
                    schema {validation.valid ? 'valid' : 'has errors'} · {validation.serviceCount} service{validation.serviceCount === 1 ? '' : 's'}
                    {validation.name ? ` · project ${validation.name}` : ''}
                  </span>
                )}
                <ConfirmButton
                  className="ml-auto"
                  label="Delete stack"
                  confirmLabel="Delete permanently"
                  onConfirm={async () => { await api.deleteStack(selected); setSelected(null); load(); notify('Stack deleted', 'success'); }}
                />
              </div>

              <StackStatsPanel stats={stats} notify={notify} refresh={() => { if (selected) api.stackStats(selected).then(setStats).catch(() => undefined); }} />

              <AutoUpdateConfig
                stack={stacks.find((s) => s.name === selected)}
                onSave={async (payload) => { await api.setAutoUpdate(selected, payload); load(); notify('Auto-update saved', 'success'); }}
              />
            </Section>
          ) : (
            <EmptyState icon="layers" title="Select a stack" hint="Pick a stack on the left to edit its YAML, deploy it and configure auto-updates." />
          )}

          <Section
            title="3-way converter"
            info="Translate between a docker run command, a Compose file and Kubernetes manifests without leaving the UI."
          >
            <Field label="docker run → docker-compose">
              <textarea className="input h-20 font-mono text-xs" value={converterInput} onChange={(e) => setConverterInput(e.target.value)} />
            </Field>
            <div className="mt-2 flex flex-wrap gap-2">
              <button className="btn-ghost px-3 py-1.5 text-xs" onClick={async () => setConverterOutput((await api.convertDockerRun(converterInput)).yaml)}>→ Compose</button>
              <button className="btn-ghost px-3 py-1.5 text-xs" onClick={async () => setConverterOutput(await api.convertToK8s({ source: converterOutput }))}>Compose → K8s</button>
              <button className="btn-ghost px-3 py-1.5 text-xs" onClick={async () => setK8sOutput(await api.convertToK8s({ name: selected ?? '' }))}>Export selected stack</button>
            </div>
            <pre className="mt-2 max-h-60 overflow-auto rounded-lg bg-ink-950 p-3 font-mono text-xs text-slate-300">{converterOutput || '// output appears here'}</pre>
            {k8sOutput && <pre className="mt-2 max-h-60 overflow-auto rounded-lg bg-ink-950 p-3 font-mono text-xs text-slate-300">{k8sOutput}</pre>}
          </Section>
        </div>
      </div>

      {newOpen && (
        <Modal title="New stack" onClose={() => setNewOpen(false)}>
          <div className="space-y-3">
            <Field label="Stack name" hint="Lowercase letters, digits and dashes.">
              <input className="input" value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="my-stack" />
            </Field>
            <div className="flex justify-end gap-2">
              <button className="btn-ghost" onClick={() => setNewOpen(false)}>Cancel</button>
              <button
                className="btn-primary"
                disabled={!newName.trim()}
                onClick={async () => {
                  await api.saveStack(newName, { yaml: `name: ${newName}\nservices:\n  app:\n    image: nginx:latest\n    ports:\n      - "8080:80"\n` });
                  setNewOpen(false);
                  load();
                  void open(newName);
                }}
              >
                Create
              </button>
            </div>
          </div>
        </Modal>
      )}

      {importOpen && (
        <Modal title="Import compose from URL" onClose={() => setImportOpen(false)}>
          <div className="space-y-3">
            <Field label="Stack name"><input className="input" value={importName} onChange={(e) => setImportName(e.target.value)} placeholder="imported" /></Field>
            <Field label="Compose URL"><input className="input" value={importUrl} onChange={(e) => setImportUrl(e.target.value)} placeholder="https://example.com/docker-compose.yml" /></Field>
            <div className="flex justify-end gap-2">
              <button className="btn-ghost" onClick={() => setImportOpen(false)}>Cancel</button>
              <button
                className="btn-primary"
                disabled={!importName.trim() || !importUrl.trim()}
                onClick={async () => { await api.importStack(importName, importUrl); setImportOpen(false); load(); notify('Import requested', 'success'); }}
              >
                Import
              </button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

/** Auto-update interval presets covering minutes through months. */
const INTERVAL_PRESETS: { minutes: number; label: string }[] = [
  { minutes: 15, label: 'Every 15 minutes' },
  { minutes: 30, label: 'Every 30 minutes' },
  { minutes: 60, label: 'Every hour' },
  { minutes: 360, label: 'Every 6 hours' },
  { minutes: 720, label: 'Every 12 hours' },
  { minutes: 1440, label: 'Every day' },
  { minutes: 4320, label: 'Every 3 days' },
  { minutes: 10080, label: 'Every week' },
  { minutes: 20160, label: 'Every 2 weeks' },
  { minutes: 43200, label: 'Every month' }
];

function intervalHuman(minutes: number): string {
  const found = INTERVAL_PRESETS.find((preset) => preset.minutes === minutes);
  if (found) return found.label.toLowerCase();
  if (minutes < 60) return `every ${minutes} minutes`;
  if (minutes < 1440) return `every ${Math.round(minutes / 60)} hours`;
  return `every ${Math.round(minutes / 1440)} days`;
}

/** Per-stack operational stats: state, counters and a per-service container breakdown (expandable). */
function StackStatsPanel({ stats, notify, refresh }: { stats: StackStats | null; notify: Notify; refresh: () => void }) {
  if (!stats) return null;
  const t = stats.totals;
  const issues = stats.services.reduce((sum, svc) => sum + svc.networkRxErrors + svc.networkTxErrors + svc.networkRxDropped + svc.networkTxDropped, 0);

  return (
    <div className="mt-4 rounded-lg border border-ink-700 bg-ink-900/50 p-3">
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <h3 className="text-sm font-semibold">Stack stats</h3>
        <StatusPill status={stats.state} />
        <InfoTip label="Stack stats">
          Aggregated CPU, memory, network and disk across the stack's containers, plus per-service detail.
          Hidden until expanded so the editor stays focused on the YAML.
        </InfoTip>
        <span className="ml-auto flex items-center gap-2 text-[11px] text-slate-400">
          {t.servicesRunning}/{t.servicesTotal} services running
          <button type="button" className="btn-ghost px-2 py-1 text-xs" onClick={refresh}>
            <Icon name="refreshCw" className="h-3.5 w-3.5" /> Refresh
          </button>
        </span>
      </div>

      <div className="grid gap-2 sm:grid-cols-4">
        <div className="rounded-lg border border-ink-800 bg-ink-900/60 p-2">
          <div className="text-[10px] uppercase tracking-wide text-slate-400">CPU</div>
          <div className="text-lg font-semibold tabular-nums">{formatPercent(t.cpuPercent)}</div>
        </div>
        <div className="rounded-lg border border-ink-800 bg-ink-900/60 p-2">
          <div className="text-[10px] uppercase tracking-wide text-slate-400">Memory</div>
          <div className="text-lg font-semibold tabular-nums">{formatBytes(t.memoryMb * 1024 * 1024)}</div>
        </div>
        <div className="rounded-lg border border-ink-800 bg-ink-900/60 p-2">
          <div className="text-[10px] uppercase tracking-wide text-slate-400">Network in / out</div>
          <div className="text-sm font-semibold tabular-nums">↓ {formatBytes(t.networkRxBytes)} · ↑ {formatBytes(t.networkTxBytes)}</div>
        </div>
        <div className="rounded-lg border border-ink-800 bg-ink-900/60 p-2">
          <div className="text-[10px] uppercase tracking-wide text-slate-400">Restarts · issues</div>
          <div className="text-sm font-semibold tabular-nums">{formatNumber(t.restartCount)} · {formatNumber(issues)}</div>
        </div>
      </div>

      <div className="mt-3 space-y-2">
        {stats.services.map((svc) => (
          <Expand
            key={svc.name}
            title={<span className="flex items-center gap-2">{svc.name} <StatusPill status={svc.state} /></span>}
            hint={svc.image ?? undefined}
          >
            <div className="grid gap-3 sm:grid-cols-2">
              <div>
                <MetricRow label="Container" value={<span className="font-mono text-[11px]">{svc.container}</span>} />
                <MetricRow label="Image" value={<span className="font-mono text-[11px]">{svc.image ?? '—'}</span>} />
                <MetricRow label="Status" value={svc.status} />
                <MetricRow label="Health" value={svc.health} />
                <MetricRow label="Started" value={svc.startedAt ? formatRelative(svc.startedAt) : '—'} />
                <MetricRow label="Restarts" value={formatNumber(svc.restartCount)} />
              </div>
              <div>
                <MetricRow label="CPU" value={formatPercent(svc.cpuPercent)} hint={`of ${svc.cpuCores} cores`} />
                <MetricRow label="Memory" value={formatBytes(svc.memoryMb * 1024 * 1024)} hint={svc.memoryLimitMb > 0 ? `of ${formatBytes(svc.memoryLimitMb * 1024 * 1024)}` : undefined} />
                <MetricRow label="Memory peak" value={formatBytes(svc.memoryPeakMb * 1024 * 1024)} />
                <MetricRow label="Processes" value={formatNumber(svc.pidsCurrent)} />
                <MetricRow label="Writable layer" value={formatBytes(svc.sizeRwBytes)} />
              </div>
              <div>
                <MetricRow label="Network in" value={formatBytes(svc.networkRxBytes)} />
                <MetricRow label="Network out" value={formatBytes(svc.networkTxBytes)} />
                <MetricRow label="Errors in / out" value={`${formatNumber(svc.networkRxErrors)} / ${formatNumber(svc.networkTxErrors)}`} />
                <MetricRow label="Drops in / out" value={`${formatNumber(svc.networkRxDropped)} / ${formatNumber(svc.networkTxDropped)}`} />
              </div>
              <div>
                <MetricRow label="Published ports" value={svc.ports.length === 0 ? 'none' : ''} />
                {svc.ports.length > 0 && (
                  <div className="flex flex-wrap gap-1 pt-1">
                    {[...svc.ports].sort().map((port) => (
                      <span key={port} className="badge border border-ink-700 bg-ink-800/60 font-mono text-[10px] normal-case text-slate-300">{port}</span>
                    ))}
                  </div>
                )}
              </div>
            </div>
          </Expand>
        ))}
      </div>

      <div className="mt-3 text-[11px] text-slate-400">
        Auto-update {stats.autoUpdate.enabled ? 'enabled' : 'disabled'} · {intervalHuman(stats.autoUpdate.intervalMinutes)} ·
        {' '}{formatNumber(stats.autoUpdate.updateCount)} updates · {formatNumber(stats.autoUpdate.rollbackCount)} rollbacks
        {stats.autoUpdate.lastError && <span className="ml-1 text-amber-300">last error: {stats.autoUpdate.lastError}</span>}
      </div>
    </div>
  );
}

function AutoUpdateConfig({ stack, onSave }: { stack?: StackSummary; onSave: (payload: Record<string, unknown>) => void }) {
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
      <div className="mb-2 flex items-center gap-1.5">
        <h3 className="text-sm font-semibold">Auto-updater</h3>
        <InfoTip label="Auto-updater">
          Periodically pulls the latest images and recreates only changed services. A 25-second health guard
          watches the new container and rolls back to the previous image on crash or unhealthy state.
        </InfoTip>
      </div>
      <div className="flex flex-wrap items-center gap-4 text-sm">
        <label className="flex items-center gap-2">
          <Toggle checked={enabled} onChange={setEnabled} label="Auto-update enabled" /> Enabled
        </label>
        <label className="flex items-center gap-2">
          Interval
          <select
            className="input w-36 py-1"
            value={INTERVAL_PRESETS.some((p) => p.minutes === interval) ? String(interval) : 'custom'}
            aria-label="Auto-update interval"
            onChange={(e) => { if (e.target.value !== 'custom') setInterval(Number(e.target.value)); }}
          >
            {INTERVAL_PRESETS.map((preset) => (
              <option key={preset.minutes} value={preset.minutes}>{preset.label}</option>
            ))}
            <option value="custom">Custom…</option>
          </select>
          <input
            className="input w-20 py-1"
            type="number"
            min={1}
            value={interval}
            aria-label="Auto-update interval in minutes"
            onChange={(e) => setInterval(Number(e.target.value))}
          />
          <span className="text-slate-400">min</span>
        </label>
        <label className="flex items-center gap-2">
          <Toggle checked={prune} onChange={setPrune} label="Prune old images" /> Prune old images
        </label>
        <label className="flex items-center gap-2">
          <Toggle checked={rollback} onChange={setRollback} label="Rollback on failure" /> Rollback on failure
        </label>
        <button className="btn-primary px-3 py-1.5 text-xs" onClick={() => onSave({ enabled, intervalMinutes: interval, pruneOldImages: prune, rollbackOnFailure: rollback })}>
          Save
        </button>
      </div>
    </div>
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

// ────────────────────────────────────────────────────────────────────────────
// View 4 — Containers
// ────────────────────────────────────────────────────────────────────────────
function containerName(container: DockerContainerSummary): string {
  return (container.Names?.[0] ?? container.Id.slice(0, 12)).replace('/', '');
}

/** Renders Docker's port list as "8080→80/tcp" (published) or "80/tcp" (exposed only),
 *  ordered by container port so the column never reshuffles between refreshes. */
function containerPorts(container: DockerContainerSummary): string[] {
  return (container.Ports ?? [])
    .filter((port) => port.PrivatePort != null)
    .sort((a, b) => (a.PrivatePort ?? 0) - (b.PrivatePort ?? 0) || (a.PublicPort ?? 0) - (b.PublicPort ?? 0))
    .map((port) => {
      const protocol = port.Type ?? 'tcp';
      return port.PublicPort
        ? `${port.PublicPort}→${port.PrivatePort}/${protocol}`
        : `${port.PrivatePort}/${protocol}`;
    });
}

function ContainersView({
  activeHost,
  notify,
  openTerminal,
  inspect
}: {
  activeHost?: number;
  notify: Notify;
  openTerminal: OpenTerminal;
  inspect: (id: string) => void;
}) {
  const [containers, setContainers] = useState<DockerContainerSummary[]>([]);
  const [query, setQuery] = useState('');
  const [busyId, setBusyId] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [refresh, setRefresh] = usePersistentState<number>('ehdm.refresh.containers', 5);

  const load = useCallback(() => {
    api.containers(activeHost).then(setContainers).catch((e) => notify((e as Error).message, 'error'));
  }, [activeHost, notify]);

  useEffect(() => {
    load();
    if (refresh <= 0) return undefined;
    const timer = window.setInterval(load, refresh * 1000);
    return () => window.clearInterval(timer);
  }, [load, refresh]);

  const filtered = containers.filter((c) => `${containerName(c)} ${c.Image}`.toLowerCase().includes(query.toLowerCase()));

  const act = async (id: string, action: string) => {
    setBusyId(id);
    try {
      await api.containerAction(id, action, activeHost);
      load();
    } catch (error) {
      notify((error as Error).message, 'error');
    } finally {
      setBusyId(null);
    }
  };

  const actions = (container: DockerContainerSummary, compact: boolean) => (
    <div className={classNames('flex flex-wrap gap-1', compact ? '' : 'justify-end')}>
      <button className="btn-ghost px-2 py-1 text-xs" onClick={() => inspect(container.Id)}><Icon name="panels" className="h-3.5 w-3.5" /> Inspect</button>
      <button className="btn-ghost px-2 py-1 text-xs" onClick={() => openTerminal(containerName(container), api.execUrl(container.Id, '/bin/sh'))}><Icon name="terminal" className="h-3.5 w-3.5" /> Exec</button>
      <button className="btn-ghost px-2 py-1 text-xs" disabled={busyId === container.Id} onClick={() => act(container.Id, 'restart')}><Icon name="rotateCw" className="h-3.5 w-3.5" /> Restart</button>
      <button className="btn-ghost px-2 py-1 text-xs" disabled={busyId === container.Id} onClick={() => act(container.Id, container.State === 'running' ? 'stop' : 'start')}>
        <Icon name={container.State === 'running' ? 'stopCircle' : 'play'} className="h-3.5 w-3.5" />
        {container.State === 'running' ? 'Stop' : 'Start'}
      </button>
    </div>
  );

  return (
    <div className="space-y-4">
      <PageHeader
        title="Containers"
        icon="box"
        subtitle="Everything running on the selected host."
        info="Use Inspect for live CPU/RAM, network and a file browser, Exec for a shell, and the action buttons to restart, stop or start. Auto-refresh is configurable."
        actions={
          <>
            <RefreshControl value={refresh} onChange={setRefresh} />
            <button className="btn-ghost" onClick={load}><Icon name="refreshCw" className="h-4 w-4" /> Refresh</button>
          </>
        }
      />

      <div className="relative">
        <Icon name="search" className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
        <input className="input pl-9" placeholder="Filter by name or image…" value={query} onChange={(e) => setQuery(e.target.value)} />
      </div>

      <div className="card hidden overflow-x-auto p-0 md:block">
        <table className="w-full text-sm">
          <thead className="text-left text-xs uppercase tracking-wide text-slate-400">
            <tr className="border-b border-ink-800">
              <th className="px-3 py-2">Name</th>
              <th className="px-3 py-2">Image</th>
              <th className="px-3 py-2">State</th>
              <th className="px-3 py-2">Status</th>
              <th className="px-3 py-2">Ports</th>
              <th className="px-3 py-2">Created</th>
              <th className="px-3 py-2 text-right">Size</th>
              <th className="px-3 py-2 text-right">Actions</th>
            </tr>
          </thead>
          <tbody>
            {filtered.map((container) => (
              <Fragment key={container.Id}>
                <tr className="table-row">
                  <td className="px-3 py-2 font-medium">
                    <div className="flex items-start gap-1.5">
                      <button
                        type="button"
                        className="mt-0.5 shrink-0 text-slate-400 hover:text-accent"
                        aria-label={expanded === container.Id ? 'Hide container details' : 'Show container details'}
                        onClick={() => setExpanded(expanded === container.Id ? null : container.Id)}
                      >
                        <Icon name={expanded === container.Id ? 'chevronDown' : 'chevronRight'} className="h-3.5 w-3.5" />
                      </button>
                      <span className="min-w-0 truncate">{containerName(container)}</span>
                    </div>
                  </td>
                  <td className="px-3 py-2 text-slate-400">{container.Image}</td>
                  <td className="px-3 py-2"><StatusPill status={container.State} /></td>
                  <td className="px-3 py-2 text-xs text-slate-400">{container.Status}</td>
                  <td className="px-3 py-2">
                    <div className="flex flex-wrap gap-1">
                      {containerPorts(container).length === 0 && <span className="text-xs text-slate-400">—</span>}
                      {containerPorts(container).map((port) => (
                        <span key={port} className="badge border border-ink-700 bg-ink-800/60 font-mono text-[10px] normal-case text-slate-300">{port}</span>
                      ))}
                    </div>
                  </td>
                  <td className="px-3 py-2 text-xs text-slate-400">{container.Created ? formatRelative(container.Created) : '—'}</td>
                  <td className="px-3 py-2 text-right text-xs tabular-nums text-slate-400">{container.SizeRw != null ? formatBytes(container.SizeRw) : '—'}</td>
                  <td className="px-3 py-2">{actions(container, false)}</td>
                </tr>
                {expanded === container.Id && (
                  <tr className="bg-ink-900/40">
                    <td colSpan={8} className="px-3 py-3">
                      <ContainerStatsPanel id={container.Id} hostId={activeHost} ports={containerPorts(container)} created={container.Created} sizeRwBytes={container.SizeRw} />
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
            {filtered.length === 0 && (
              <tr><td colSpan={8} className="px-3 py-8 text-center text-slate-400">No containers match.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      <div className="space-y-3 md:hidden">
        {filtered.map((container) => (
          <div key={container.Id} className="card space-y-3 p-3">
            <div className="flex items-center gap-2">
              <div className="min-w-0 flex-1">
                <div className="truncate text-sm font-medium">{containerName(container)}</div>
                <div className="truncate text-xs text-slate-400">{container.Image}</div>
              </div>
              <StatusPill status={container.State} />
            </div>
            <div className="text-[11px] text-slate-400">{container.Status}</div>
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[11px] text-slate-400">
              <span className="flex items-center gap-1"><Icon name="clock" className="h-3 w-3" /> {container.Created ? formatRelative(container.Created) : 'unknown'}</span>
              <span className="flex items-center gap-1"><Icon name="hardDrive" className="h-3 w-3" /> {container.SizeRw != null ? formatBytes(container.SizeRw) : '—'}</span>
            </div>
            {containerPorts(container).length > 0 && (
              <div className="flex flex-wrap gap-1">
                {containerPorts(container).map((port) => (
                  <span key={port} className="badge border border-ink-700 bg-ink-800/60 font-mono text-[10px] normal-case text-slate-300">{port}</span>
                ))}
              </div>
            )}
            <Expand title="More info" hint="counters &amp; charts">
              <ContainerStatsPanel id={container.Id} hostId={activeHost} ports={containerPorts(container)} created={container.Created} sizeRwBytes={container.SizeRw} />
            </Expand>
            {actions(container, true)}
          </div>
        ))}
        {filtered.length === 0 && <EmptyState icon="box" title="No containers match" />}
      </div>
    </div>
  );
}

/** CPU/memory/network/disk counters for a container, from one extended `/metrics` sample. */
function ContainerCounterCards({
  metric,
  ports,
  created,
  sizeRwBytes,
  compact
}: {
  metric: ExtendedContainerMetric;
  ports?: string[];
  created?: string | number;
  sizeRwBytes?: number;
  compact?: boolean;
}) {
  const netIssues = metric.networkRxErrors + metric.networkTxErrors
    + metric.networkRxDropped + metric.networkTxDropped;
  if (compact) {
    return (
      <div className="space-y-1">
        <MetricRow label="CPU usage" value={formatPercent(metric.cpuPercent)} hint={`of ${metric.cpuCores} cores`} />
        <MetricRow label="CPU throttled" value={`${formatNumber(metric.cpuThrottledPeriods)} periods · ${formatNumber(metric.cpuThrottledMs, 1)} ms`} />
        <MetricRow label="Memory limit" value={metric.memoryLimitMb > 0 ? formatBytes(metric.memoryLimitMb * 1024 * 1024) : 'unlimited'} />
        <MetricRow label="Memory peak" value={formatBytes(metric.memoryPeakMb * 1024 * 1024)} />
        <MetricRow label="Allocation failures" value={formatNumber(metric.memoryFailCount)} />
        <MetricRow label="Processes / threads" value={formatNumber(metric.pidsCurrent)} hint={metric.pidsLimit > 0 ? `limit ${formatNumber(metric.pidsLimit)}` : undefined} />
        <MetricRow label="Block read / written" value={`${formatBytes(metric.blockReadBytes)} / ${formatBytes(metric.blockWriteBytes)}`} />
        <MetricRow label="Network in / out" value={`${formatBytes(metric.networkRxBytes)} / ${formatBytes(metric.networkTxBytes)}`} />
        <MetricRow label="Packets in / out" value={`${formatNumber(metric.networkRxPackets)} / ${formatNumber(metric.networkTxPackets)}`} />
        <MetricRow label="Network errors / drops" value={`${formatNumber(metric.networkRxErrors + metric.networkTxErrors)} / ${formatNumber(metric.networkRxDropped + metric.networkTxDropped)}`} />
      </div>
    );
  }
  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
      <div className="card p-3">
        <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-slate-400">CPU</div>
        <MetricRow label="Usage" value={formatPercent(metric.cpuPercent)} hint={`of ${metric.cpuCores} cores`} />
        <MetricRow label="Throttled periods" value={formatNumber(metric.cpuThrottledPeriods)} />
        <MetricRow label="Throttled time" value={`${formatNumber(metric.cpuThrottledMs, 1)} ms`} />
      </div>
      <div className="card p-3">
        <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-slate-400">Memory</div>
        <MetricRow label="Used" value={formatBytes(metric.memoryMb * 1024 * 1024)} />
        <MetricRow label="Limit" value={metric.memoryLimitMb > 0 ? formatBytes(metric.memoryLimitMb * 1024 * 1024) : 'unlimited'} />
        <MetricRow label="Peak" value={formatBytes(metric.memoryPeakMb * 1024 * 1024)} />
        <MetricRow label="Allocation failures" value={formatNumber(metric.memoryFailCount)} />
      </div>
      <div className="card p-3">
        <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-slate-400">Processes &amp; disk</div>
        <MetricRow label="Processes / threads (PIDs)" value={formatNumber(metric.pidsCurrent)} hint={metric.pidsLimit > 0 ? `limit ${formatNumber(metric.pidsLimit)}` : undefined} />
        <MetricRow label="Block read" value={formatBytes(metric.blockReadBytes)} />
        <MetricRow label="Block written" value={formatBytes(metric.blockWriteBytes)} />
        {sizeRwBytes != null && <MetricRow label="Writable layer" value={formatBytes(sizeRwBytes)} />}
      </div>
      <div className="card p-3">
        <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-slate-400">Network</div>
        <MetricRow label="Received" value={formatBytes(metric.networkRxBytes)} />
        <MetricRow label="Sent" value={formatBytes(metric.networkTxBytes)} />
        <MetricRow label="Packets in / out" value={`${formatNumber(metric.networkRxPackets)} / ${formatNumber(metric.networkTxPackets)}`} />
      </div>
      <div className="card p-3">
        <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-slate-400">Network issues</div>
        <MetricRow label="Receive errors" value={formatNumber(metric.networkRxErrors)} />
        <MetricRow label="Transmit errors" value={formatNumber(metric.networkTxErrors)} />
        <MetricRow label="Receive / transmit drops" value={`${formatNumber(metric.networkRxDropped)} / ${formatNumber(metric.networkTxDropped)}`} />
        <MetricRow label="Total issues" value={formatNumber(netIssues)} />
      </div>
      <div className="card p-3">
        <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-slate-400">Ports &amp; age</div>
        {created && <MetricRow label="Created" value={formatRelative(created)} />}
        <MetricRow label="Ports" value={ports && ports.length > 0 ? '' : 'none'} />
        {ports && ports.length > 0 && (
          <div className="flex flex-wrap gap-1 pt-1">
            {ports.map((port) => (
              <span key={port} className="badge border border-ink-700 bg-ink-800/60 font-mono text-[10px] normal-case text-slate-300">{port}</span>
            ))}
          </div>
        )}
      </div>
      {metric.interfaces.length > 0 && (
        <div className="sm:col-span-2 lg:col-span-3">
          <Expand title={`Interfaces (${metric.interfaces.length})`} hint="per Docker network">
            {metric.interfaces.map((iface) => (
              <div key={iface.name} className="border-b border-ink-800/50 py-1 text-xs last:border-b-0">
                <div className="flex items-center justify-between">
                  <span className="font-mono text-slate-300">{iface.name}</span>
                  <span className="tabular-nums text-slate-400">↓ {formatBytes(iface.rxBytes)} · ↑ {formatBytes(iface.txBytes)}</span>
                </div>
              </div>
            ))}
          </Expand>
        </div>
      )}
    </div>
  );
}

/** Polls one extended `/metrics` sample and renders the counter cards (mounts only when its disclosure is open). */
function ContainerExtendedCounters({
  id,
  hostId,
  ports,
  created,
  sizeRwBytes,
  compact
}: {
  id: string;
  hostId?: number;
  ports?: string[];
  created?: string | number;
  sizeRwBytes?: number;
  compact?: boolean;
}) {
  const [metric, setMetric] = useState<ExtendedContainerMetric | null>(null);
  useEffect(() => {
    let cancelled = false;
    const fetchMetrics = () => {
      api.containerMetrics(id, hostId)
        .then((result) => { if (!cancelled) setMetric(result.stats ?? null); })
        .catch(() => undefined);
    };
    fetchMetrics();
    const timer = window.setInterval(fetchMetrics, 5000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [id, hostId]);
  if (!metric) return <div className="text-xs text-slate-400">Loading counters…</div>;
  return <ContainerCounterCards metric={metric} ports={ports} created={created} sizeRwBytes={sizeRwBytes} compact={compact} />;
}

/**
 * The shared live container panel: a 2 s stats stream feeds CPU/memory/network/disk
 * charts, a 5 s `/metrics` poll feeds the counters. Used by the Containers view, the
 * Dashboard container row and the Container Inspector so every surface matches.
 */
function ContainerStatsPanel({
  id,
  hostId,
  ports,
  created,
  sizeRwBytes
}: {
  id: string;
  hostId?: number;
  ports?: string[];
  created?: string | number;
  sizeRwBytes?: number;
}) {
  const [cpu, setCpu] = useState<number[]>([]);
  const [ram, setRam] = useState<number[]>([]);
  const [netIn, setNetIn] = useState<number[]>([]);
  const [netOut, setNetOut] = useState<number[]>([]);
  const [ioRead, setIoRead] = useState<number[]>([]);
  const [ioWrite, setIoWrite] = useState<number[]>([]);
  const [netHistory, setNetHistory] = useState<ContainerNetworkHistory | null>(null);
  const previous = useRef<MetricSample | null>(null);

  useEffect(() => {
    let cancelled = false;
    const fetchHistory = () => {
      api.containerNetwork(id, hostId)
        .then((history) => { if (!cancelled) setNetHistory(history); })
        .catch(() => undefined);
    };
    fetchHistory();
    const timer = window.setInterval(fetchHistory, 5000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [id, hostId]);

  useEffect(() => {
    const source = new EventSource(api.statsUrl(id, hostId), { withCredentials: true });
    source.onmessage = (event) => {
      try {
        const sample = JSON.parse(event.data) as MetricSample & { error?: string };
        if (sample.error) return;
        const prev = previous.current;
        previous.current = sample;
        const seconds = prev
          ? Math.max(1, (new Date(sample.timestamp).getTime() - new Date(prev.timestamp).getTime()) / 1000)
          : 0;
        setCpu((p) => [...p, sample.cpuPercent ?? 0].slice(-60));
        setRam((p) => [...p, sample.memoryMb ?? 0].slice(-60));
        setNetIn((p) => [...p, counterRate(prev?.networkRxBytes, sample.networkRxBytes ?? 0, seconds)].slice(-60));
        setNetOut((p) => [...p, counterRate(prev?.networkTxBytes, sample.networkTxBytes ?? 0, seconds)].slice(-60));
        setIoRead((p) => [...p, counterRate(prev?.blockReadBytes, sample.blockReadBytes ?? 0, seconds)].slice(-60));
        setIoWrite((p) => [...p, counterRate(prev?.blockWriteBytes, sample.blockWriteBytes ?? 0, seconds)].slice(-60));
      } catch {
        /* ignore malformed frames */
      }
    };
    return () => source.close();
  }, [id, hostId]);

  const last = (series: number[]) => series[series.length - 1] ?? 0;

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="card p-3">
          <div className="flex items-baseline justify-between text-xs text-slate-400"><span>CPU</span><span>{formatPercent(cpu[cpu.length - 1] ?? 0)}</span></div>
          <Sparkline data={cpu} stroke="#38bdf8" height={70} />
        </div>
        <div className="card p-3">
          <div className="flex items-baseline justify-between text-xs text-slate-400"><span>Memory</span><span>{formatBytes((ram[ram.length - 1] ?? 0) * 1024 * 1024)}</span></div>
          <Sparkline data={ram} stroke="#34d399" height={70} />
        </div>
      </div>

      <div className="card p-3">
        <div className="mb-1 flex items-center justify-between text-xs text-slate-400">
          <span>Network throughput</span>
          <span>↓ {formatRate(last(netIn))} · ↑ {formatRate(last(netOut))}</span>
        </div>
        <TrafficChart inData={netIn} outData={netOut} />
        {netHistory && netHistory.history.length > 0 && (
          <div className="mt-2 grid grid-cols-2 gap-1 text-[11px] text-slate-400">
            <span>In 1h <span className="text-slate-200">{formatBytes(netHistory.rxLast1hBytes)}</span></span>
            <span>Out 1h <span className="text-slate-200">{formatBytes(netHistory.txLast1hBytes)}</span></span>
            <span>In 24h <span className="text-slate-200">{formatBytes(netHistory.rxLast24hBytes)}</span></span>
            <span>Out 24h <span className="text-slate-200">{formatBytes(netHistory.txLast24hBytes)}</span></span>
          </div>
        )}
      </div>

      <div className="card p-3">
        <div className="mb-1 flex items-center justify-between text-xs text-slate-400">
          <span>Disk throughput</span>
          <span>read {formatRate(last(ioRead))} · write {formatRate(last(ioWrite))}</span>
        </div>
        <TrafficChart inData={ioRead} outData={ioWrite} inLabel="Read" outLabel="Write" />
      </div>

      <ContainerExtendedCounters id={id} hostId={hostId} ports={ports} created={created} sizeRwBytes={sizeRwBytes} />
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
  notify: Notify;
  openTerminal: OpenTerminal;
}) {
  const [tab, setTab] = useState<'stats' | 'logs' | 'files'>('stats');
  const [shell, setShell] = useState('/bin/sh');
  const [logs, setLogs] = useState<{ stream: string; line: string }[]>([]);
  const [filter, setFilter] = useState('');
  const [path, setPath] = useState('/');
  const [entries, setEntries] = useState<TarEntryInfo[]>([]);
  const [cves, setCves] = useState<CveFinding[]>([]);
  const [scanning, setScanning] = useState(false);
  const [logTimestamps, setLogTimestamps] = usePersistentState<boolean>('ehdm.logs.timestamps', true);

  useEffect(() => {
    if (tab !== 'logs') return undefined;
    const source = new EventSource(api.logsUrl(id, 300, logTimestamps), { withCredentials: true });
    source.onmessage = (event) => {
      try {
        setLogs((prev) => [...prev, JSON.parse(event.data) as { stream: string; line: string }].slice(-500));
      } catch {
        /* ignore */
      }
    };
    return () => source.close();
  }, [id, tab, logTimestamps]);

  const loadFiles = useCallback(
    (target: string) => {
      api.archiveList(id, target)
        .then((list) => {
          setEntries(
            [...list].sort((a, b) =>
              Number(b.isDirectory) - Number(a.isDirectory) || a.name.localeCompare(b.name)
            )
          );
          setPath(target);
        })
        .catch((e) => notify((e as Error).message, 'error'));
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
        <div className="flex flex-wrap items-center gap-2 border-b border-ink-800 px-4 py-3">
          <h2 className="text-sm font-semibold">Container {id.slice(0, 12)}</h2>
          <div className="ml-auto flex items-center gap-1">
            <Tabs
              value={tab}
              onChange={setTab}
              tabs={[
                { key: 'stats', label: 'Stats', icon: 'activity' },
                { key: 'logs', label: 'Logs', icon: 'terminal' },
                { key: 'files', label: 'Files', icon: 'folder' }
              ]}
            />
            <select className="input w-28 py-1 text-xs" value={shell} onChange={(e) => setShell(e.target.value)} aria-label="Shell to run">
              <option value="/bin/sh">/bin/sh</option>
              <option value="/bin/bash">/bin/bash</option>
              <option value="/bin/ash">/bin/ash</option>
              <option value="/bin/busybox sh">busybox sh</option>
            </select>
            <button className="btn-ghost px-2 py-1 text-xs" onClick={() => openTerminal(id.slice(0, 12), api.execUrl(id, shell))}><Icon name="terminal" className="h-3.5 w-3.5" /> Exec</button>
            <button className="btn-ghost px-2 py-1 text-xs" onClick={onClose} aria-label="Close inspector"><Icon name="x" className="h-4 w-4" /></button>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto p-4">
          {tab === 'stats' && (
            <div className="space-y-4">
              <ContainerStatsPanel id={id} />
              <button className="btn-ghost w-full" disabled={scanning} onClick={async () => { setScanning(true); try { setCves(await api.cveScan(id)); } catch (error) { notify((error as Error).message, 'error'); } finally { setScanning(false); } }}>
                <Icon name="shieldCheck" className="h-4 w-4" /> {scanning ? 'Scanning…' : 'Run agentless CVE scan (OSV.dev)'}
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
              <div className="flex flex-wrap items-center gap-2">
                <input className="input min-w-[12rem] flex-1" placeholder="Filter with text or a regex…" value={filter} onChange={(e) => setFilter(e.target.value)} />
                <label className="flex items-center gap-1.5 text-xs text-slate-400">
                  <input type="checkbox" checked={logTimestamps} onChange={(e) => setLogTimestamps(e.target.checked)} />
                  Show timestamps
                  <InfoTip label="Timestamps">
                    Docker prefixes every line with its RFC 3339 timestamp. Turn this off for a cleaner log view.
                  </InfoTip>
                </label>
              </div>
              <pre className="max-h-[60vh] overflow-auto rounded-lg bg-ink-950 p-3 font-mono text-xs">
                {visibleLogs.map((entry, index) => (
                  <div key={index} className={entry.stream === 'stderr' ? 'text-amber-300' : 'text-slate-300'}>{entry.line}</div>
                ))}
                {visibleLogs.length === 0 && <span className="text-slate-400">Waiting for log output…</span>}
              </pre>
            </div>
          )}

          {tab === 'files' && (
            <div className="space-y-2">
              <div className="flex gap-2">
                <input className="input font-mono text-xs" value={path} onChange={(e) => setPath(e.target.value)} />
                <button className="btn-ghost" onClick={() => loadFiles(path)}>Browse</button>
              </div>
              <button className="btn-ghost px-2 py-1 text-xs" onClick={() => loadFiles(path === '/' ? '/' : path.split('/').slice(0, -1).join('/') || '/')}><Icon name="arrowLeft" className="h-3.5 w-3.5" /> Up</button>
              <div className="divide-y divide-ink-800 rounded-lg border border-ink-800">
                {entries.map((entry) => (
                  <div key={entry.name} className="flex items-center gap-2 px-3 py-2 text-sm">
                    <Icon name={entry.isDirectory ? 'folder' : 'file'} className="h-4 w-4 text-slate-400" />
                    <button className="truncate text-left hover:text-accent" onClick={() => (entry.isDirectory ? loadFiles(entry.name) : window.open(api.archiveFileUrl(id, entry.name), '_blank'))}>{entry.name}</button>
                    <span className="ml-auto shrink-0 text-xs text-slate-400">{entry.isDirectory ? '—' : formatBytes(entry.size)}</span>
                  </div>
                ))}
                {entries.length === 0 && <div className="px-3 py-4 text-sm text-slate-400">Browse a path to list files.</div>}
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
function KubernetesView({ notify, openTerminal }: { notify: Notify; openTerminal: OpenTerminal }) {
  const [clusters, setClusters] = useState<KubeClusterInfo[]>([]);
  const [active, setActive] = useState<number | null>(null);
  const [pods, setPods] = useState<Record<string, unknown>[]>([]);
  const [deployments, setDeployments] = useState<Record<string, unknown>[]>([]);
  const [namespaces, setNamespaces] = useState<string[]>([]);
  const [namespaceError, setNamespaceError] = useState<string | null>(null);
  const [namespace, setNamespace] = useState('default');
  const [manifest, setManifest] = useState('apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: demo\n');
  const [kubeconfig, setKubeconfig] = useState('');
  const [clusterName, setClusterName] = useState('');
  const [scaleTarget, setScaleTarget] = useState<string | null>(null);
  const [replicas, setReplicas] = useState(1);

  const loadClusters = useCallback(() => {
    api.clusters().then((list) => {
      setClusters(list);
      setActive((current) => (current === null && list.length > 0 ? list[0].id : current));
    }).catch(() => undefined);
  }, []);

  useEffect(loadClusters, [loadClusters]);

  // Load the namespace list from the cluster so the field is a picker, not a guess.
  useEffect(() => {
    if (active === null) {
      setNamespaces([]);
      setNamespaceError(null);
      return undefined;
    }
    let cancelled = false;
    api
      .namespaces(active)
      .then((result) => {
        if (cancelled) return;
        const items = (result.items ?? []) as { metadata?: { name?: string } }[];
        const names = items.map((item) => item.metadata?.name ?? '').filter(Boolean).sort();
        setNamespaces(names);
        setNamespaceError(null);
        setNamespace((current) => (current && current !== '*' && names.includes(current)) ? current : (names.includes('default') ? 'default' : names[0] ?? ''));
      })
      .catch((error) => {
        if (!cancelled) setNamespaceError((error as Error).message);
      });
    return () => {
      cancelled = true;
    };
  }, [active]);

  const loadWorkloads = useCallback(async () => {
    if (active === null) return;
    try {
      const [podResult, deploymentResult] = await Promise.all([api.pods(active, namespace), api.deployments(active, namespace)]);
      setPods((podResult.items as Record<string, unknown>[]) ?? []);
      setDeployments((deploymentResult.items as Record<string, unknown>[]) ?? []);
    } catch (error) {
      notify((error as Error).message, 'error');
    }
  }, [active, namespace, notify]);

  useEffect(() => {
    void loadWorkloads();
  }, [loadWorkloads]);

  // '*' is only a list filter; mutations need a concrete namespace.
  const workloadNamespace = namespace === '*' ? 'default' : namespace;

  return (
    <div className="space-y-4">
      <PageHeader
        title="Kubernetes"
        icon="hexagon"
        subtitle="Multi-cluster pods, deployments and manifests."
        info="Import a kubeconfig to manage a cluster. The pod list is scoped to the namespace field; deployments can be scaled or rolled out from here."
        actions={<button className="btn-ghost" onClick={loadWorkloads}><Icon name="refreshCw" className="h-4 w-4" /> Refresh</button>}
      />

      <div className="grid gap-3 sm:grid-cols-3">
        <Field label="Cluster">
          <select className="input" value={active ?? ''} onChange={(e) => setActive(e.target.value ? Number(e.target.value) : null)}>
            <option value="">Select cluster…</option>
            {clusters.map((cluster) => (<option key={cluster.id} value={cluster.id}>{cluster.name}</option>))}
          </select>
        </Field>
        <Field
          label="Namespace"
          hint={
            namespaceError
              ? 'Could not load namespaces from the cluster; type one manually.'
              : namespaces.length > 0
                ? `${namespaces.length} namespaces loaded from the cluster`
                : 'Select a cluster to load its namespaces'
          }
        >
          {namespaces.length > 0 ? (
            <select className="input" value={namespace} onChange={(e) => setNamespace(e.target.value)}>
              <option value="*">All namespaces</option>
              {namespaces.map((ns) => (<option key={ns} value={ns}>{ns}</option>))}
            </select>
          ) : (
            <input className="input font-mono" value={namespace} onChange={(e) => setNamespace(e.target.value)} placeholder="default" />
          )}
        </Field>
        <div className="flex items-end">
          <StatusPill status={active === null ? 'no cluster' : `${pods.length} pods`} />
        </div>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <Section title="Clusters" info="Each entry is a kubeconfig imported into the encrypted store. Test checks connectivity; delete removes it.">
          <div className="space-y-2">
            {clusters.map((cluster) => (
              <div key={cluster.id} className="flex flex-wrap items-center gap-2 rounded-lg border border-ink-800 bg-ink-900/50 p-2 text-sm">
                <Icon name="hexagon" className="h-4 w-4 text-slate-400" />
                <span className="font-medium">{cluster.name}</span>
                <span className="truncate text-xs text-slate-400">{cluster.serverUrl}</span>
                <div className="ml-auto flex gap-1">
                  <button className="btn-ghost px-2 py-1 text-xs" onClick={async () => { const online = (await api.testCluster(cluster.id)).online; notify(online ? 'Online' : 'Unreachable', online ? 'success' : 'error'); }}>Test</button>
                  <ConfirmButton label="Delete" confirmLabel="Delete cluster" onConfirm={() => api.deleteCluster(cluster.id).then(loadClusters)} />
                </div>
              </div>
            ))}
            {clusters.length === 0 && <div className="text-sm text-slate-400">No clusters imported.</div>}
          </div>
          <div className="mt-3 space-y-2">
            <Field label="Import kubeconfig"><textarea className="input h-28 font-mono text-xs" value={kubeconfig} onChange={(e) => setKubeconfig(e.target.value)} placeholder="apiVersion: v1&#10;clusters: …" /></Field>
            <div className="flex gap-2">
              <input className="input" placeholder="cluster name" value={clusterName} onChange={(e) => setClusterName(e.target.value)} />
              <button className="btn-primary" disabled={!kubeconfig.trim()} onClick={async () => { await api.saveCluster({ name: clusterName || 'cluster', enabled: true, kubeConfigYaml: kubeconfig, serverUrl: '' }); setKubeconfig(''); setClusterName(''); loadClusters(); notify('Cluster imported', 'success'); }}>Import</button>
            </div>
          </div>
        </Section>

        <Section title={`Pods (${pods.length})`} info="Pods in the selected namespace. Exec opens a shell inside the pod.">
          <div className="max-h-72 space-y-1 overflow-y-auto text-xs">
            {pods.map((pod, index) => {
              const meta = pod.metadata as Record<string, unknown> | undefined;
              const status = pod.status as Record<string, unknown> | undefined;
              const name = (meta?.name as string) ?? `pod-${index}`;
              const phase = (status?.phase as string) ?? 'Unknown';
              return (
                <div key={name} className="flex items-center gap-2 border-b border-ink-800/60 py-1.5">
                  <span className="truncate">{name}</span>
                  <StatusPill status={phase} />
                  <button className="btn-ghost ml-auto px-2 py-0.5 text-[11px]" onClick={() => openTerminal(name, `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/k8s-exec/${active}/${(meta?.namespace as string | undefined) ?? workloadNamespace}/${name}`)}>
                    <Icon name="terminal" className="h-3 w-3" /> Exec
                  </button>
                </div>
              );
            })}
            {pods.length === 0 && <div className="py-4 text-slate-400">No pods.</div>}
          </div>
        </Section>
      </div>

      <Section title={`Deployments (${deployments.length})`} info="Scale changes the replica count; rollout restart recreates the pods. The manifest box applies raw YAML.">
        <div className="space-y-1 text-xs">
          {deployments.map((deployment, index) => {
            const meta = deployment.metadata as Record<string, unknown> | undefined;
            const spec = deployment.spec as Record<string, unknown> | undefined;
            const name = (meta?.name as string) ?? `deployment-${index}`;
            return (
              <div key={name} className="flex items-center gap-2 border-b border-ink-800/60 py-1.5">
                <span className="truncate">{name}</span>
                <span className="text-slate-400">replicas {String(spec?.replicas ?? '?')}</span>
                <div className="ml-auto flex gap-1">
                  <button className="btn-ghost px-2 py-0.5 text-[11px]" onClick={() => active && api.restartDeployment(active, workloadNamespace, name).then(loadWorkloads)}><Icon name="rotateCw" className="h-3 w-3" /> Rollout</button>
                  <button className="btn-ghost px-2 py-0.5 text-[11px]" onClick={() => { setScaleTarget(name); setReplicas(Number(spec?.replicas ?? 1)); }}><Icon name="sliders" className="h-3 w-3" /> Scale</button>
                </div>
              </div>
            );
          })}
          {deployments.length === 0 && <div className="py-4 text-slate-400">No deployments.</div>}
        </div>

        <div className="mt-4">
          <Field label="Deploy manifest"><textarea className="input h-40 font-mono text-xs" value={manifest} onChange={(e) => setManifest(e.target.value)} /></Field>
          <button className="btn-primary mt-2" onClick={async () => { if (active === null) return; const result = await api.applyManifest(active, manifest, workloadNamespace); notify(`Applied: ${result.applied.join(', ') || 'nothing'}`, 'success'); loadWorkloads(); }}>Apply</button>
        </div>
      </Section>

      {scaleTarget && (
        <Modal title={`Scale ${scaleTarget}`} onClose={() => setScaleTarget(null)}>
          <Field label="Replicas"><input className="input" type="number" min={0} value={replicas} onChange={(e) => setReplicas(Number(e.target.value))} /></Field>
          <div className="mt-3 flex justify-end gap-2">
            <button className="btn-ghost" onClick={() => setScaleTarget(null)}>Cancel</button>
            <button className="btn-primary" onClick={() => { if (active !== null) void api.scaleDeployment(active, workloadNamespace, scaleTarget, replicas).then(() => { setScaleTarget(null); loadWorkloads(); }); }}>Scale</button>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 6 — Images & registries
// ────────────────────────────────────────────────────────────────────────────
function imageTag(image: DockerImageSummary): string | null {
  const tag = (image.RepoTags ?? []).find((value) => value && !value.startsWith('<none>'));
  return tag ?? null;
}

function imageShortId(image: DockerImageSummary): string {
  return image.Id.replace(/^sha256:/, '').slice(0, 12);
}

function ImagesAndRegistriesView({ activeHost, notify, setView }: { activeHost?: number; notify: Notify; setView: (view: ViewKey) => void }) {
  const [images, setImages] = useState<DockerImageSummary[]>([]);
  const [containers, setContainers] = useState<DockerContainerSummary[]>([]);
  const [registries, setRegistries] = useState<RegistryInfo[]>([]);
  const [pullImage, setPullImage] = useState('nginx:latest');
  const [pullProgress, setPullProgress] = useState<string[]>([]);
  const [pulling, setPulling] = useState(false);
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [runTarget, setRunTarget] = useState<DockerImageSummary | null>(null);
  const [refresh, setRefresh] = usePersistentState<number>('ehdm.refresh.images', 0);

  const load = useCallback(() => {
    api.images(activeHost).then(setImages).catch(() => undefined);
    api.containers(activeHost).then(setContainers).catch(() => undefined);
    api.registries().then(setRegistries).catch(() => undefined);
  }, [activeHost]);

  useEffect(load, [load]);

  useEffect(() => {
    if (refresh <= 0) return undefined;
    const timer = window.setInterval(load, refresh * 1000);
    return () => window.clearInterval(timer);
  }, [load, refresh]);

  // Docker reports -1 for image "Containers"; derive usage from the container list instead.
  const usage = useMemo(() => {
    const map = new Map<string, number>();
    for (const container of containers) map.set(container.ImageID, (map.get(container.ImageID) ?? 0) + 1);
    return map;
  }, [containers]);
  const usedBy = (image: DockerImageSummary) => usage.get(image.Id) ?? 0;

  const startPull = () => {
    const [name, tag] = pullImage.includes(':') ? pullImage.split(':') : [pullImage, 'latest'];
    const source = new EventSource(api.pullUrl(name, tag), { withCredentials: true });
    setPullProgress([]);
    setPulling(true);
    source.onmessage = (event) => {
      setPullProgress((prev) => [...prev, event.data].slice(-40));
      if (event.data.includes('"done":true')) {
        source.close();
        setPulling(false);
        load();
      }
    };
    source.onerror = () => { setPulling(false); source.close(); };
  };

  const filtered = images.filter((image) => {
    const labels = image.Labels ? Object.keys(image.Labels).join(' ') : '';
    return `${imageTag(image) ?? ''} ${imageShortId(image)} ${image.RepoTags?.join(' ') ?? ''} ${labels}`
      .toLowerCase()
      .includes(query.toLowerCase());
  });
  const totalBytes = images.reduce((sum, image) => sum + image.Size, 0);
  const unused = images.filter((image) => usedBy(image) === 0).length;
  const allFilteredSelected = filtered.length > 0 && filtered.every((image) => selected.has(image.Id));

  const toggleOne = (id: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  const clearSelection = () => setSelected(new Set());
  const selectAllUnused = () => setSelected(new Set(images.filter((image) => usedBy(image) === 0).map((image) => image.Id)));
  const toggleAllFiltered = (checked: boolean) =>
    setSelected((prev) => {
      const next = new Set(prev);
      for (const image of filtered) {
        if (checked) next.add(image.Id);
        else next.delete(image.Id);
      }
      return next;
    });
  const deleteSelected = async () => {
    const ids = [...selected];
    let removed = 0;
    const failures: string[] = [];
    for (const id of ids) {
      try {
        await api.removeImage(id);
        removed += 1;
      } catch (error) {
        failures.push((error as Error).message);
      }
    }
    clearSelection();
    load();
    if (failures.length === 0) notify(`Removed ${removed} image${removed === 1 ? '' : 's'}`, 'success');
    else notify(`Removed ${removed}; ${failures.length} could not be removed (in use).`, 'error');
  };

  return (
    <div className="space-y-4">
      <PageHeader
        title="Images & registries"
        icon="disc"
        subtitle="Local images, their tags, size and what is using them."
        info="An image is a read-only template; containers are created from it. 'Untagged' (dangling) images have no repository name — they are usually replaced builds and safe to prune. Pull by name:tag or add private registry credentials below."
        actions={
          <>
            <RefreshControl value={refresh} onChange={setRefresh} />
            <button className="btn-ghost" onClick={load}><Icon name="refreshCw" className="h-4 w-4" /> Refresh</button>
          </>
        }
      />

      <div className="grid gap-3 sm:grid-cols-3">
        <div className="card flex items-center gap-3 p-3">
          <span className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-ink-700 bg-ink-900/70 text-accent"><Icon name="disc" className="h-4 w-4" /></span>
          <div><div className="text-lg font-semibold tabular-nums">{images.length}</div><div className="text-[11px] text-slate-400">images</div></div>
        </div>
        <div className="card flex items-center gap-3 p-3">
          <span className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-ink-700 bg-ink-900/70 text-emerald-300"><Icon name="hardDrive" className="h-4 w-4" /></span>
          <div><div className="text-lg font-semibold tabular-nums">{formatBytes(totalBytes)}</div><div className="text-[11px] text-slate-400">on disk</div></div>
        </div>
        <div className="card flex items-center gap-3 p-3">
          <span className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-ink-700 bg-ink-900/70 text-amber-300"><Icon name="trash" className="h-4 w-4" /></span>
          <div><div className="text-lg font-semibold tabular-nums">{unused}</div><div className="text-[11px] text-slate-400">unused (prunable)</div></div>
        </div>
      </div>

      <Section title="Pull & prune" info="Pull accepts name:tag. Prune removes dangling layers that no container references.">
        <div className="flex flex-wrap items-center gap-2">
          <input className="input min-w-[12rem] flex-1" value={pullImage} onChange={(e) => setPullImage(e.target.value)} placeholder="image:tag" />
          <button className="btn-primary" disabled={pulling} onClick={startPull}><Icon name="download" className="h-4 w-4" /> {pulling ? 'Pulling…' : 'Pull image'}</button>
          <ConfirmButton
            className="btn-ghost"
            icon="trash"
            label="Prune unused"
            confirmLabel="Prune unused images"
            onConfirm={async () => {
              try {
                const result = await api.pruneImages(false);
                notify(`Pruned ${result.ImagesDeleted?.length ?? 0} images; reclaimed ${formatBytes(result.SpaceReclaimed ?? 0)}`, 'success');
                load();
              } catch (error) {
                notify((error as Error).message, 'error');
              }
            }}
          />
        </div>
        {pullProgress.length > 0 && <pre className="mt-3 max-h-40 overflow-auto rounded-lg bg-ink-950 p-3 font-mono text-[11px] text-slate-400">{pullProgress.join('\n')}</pre>}
      </Section>

      <div className="flex flex-wrap items-center gap-2">
        <div className="relative min-w-[14rem] flex-1">
          <Icon name="search" className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <input className="input pl-9" placeholder="Filter by repository, tag, ID or label…" value={query} onChange={(e) => setQuery(e.target.value)} />
        </div>
        <button className="btn-ghost" disabled={unused === 0} onClick={selectAllUnused}>
          <Icon name="check" className="h-4 w-4" /> Select unused ({unused})
        </button>
      </div>

      {selected.size > 0 && (
        <div className="card flex flex-wrap items-center gap-2 border-accent-soft/40 p-3">
          <span className="text-sm font-medium">{selected.size} selected</span>
          <button className="btn-ghost px-2 py-1 text-xs" onClick={clearSelection}>Clear</button>
          <ConfirmButton
            className="ml-auto"
            label={`Delete selected (${selected.size})`}
            confirmLabel={`Delete ${selected.size} image${selected.size === 1 ? '' : 's'}`}
            onConfirm={deleteSelected}
          />
        </div>
      )}

      <div className="card hidden overflow-x-auto p-0 md:block">
        <table className="w-full text-sm">
          <thead className="text-left text-xs uppercase tracking-wide text-slate-400">
            <tr className="border-b border-ink-800">
              <th className="w-8 px-3 py-2">
                <input
                  type="checkbox"
                  className="h-4 w-4 rounded border-ink-600 bg-ink-900 accent-sky-500"
                  checked={allFilteredSelected}
                  onChange={(e) => toggleAllFiltered(e.target.checked)}
                  aria-label="Select all images"
                />
              </th>
              <th className="px-3 py-2">Repository</th>
              <th className="px-3 py-2">Image ID</th>
              <th className="px-3 py-2">Created</th>
              <th className="px-3 py-2">Size</th>
              <th className="px-3 py-2">Used by</th>
              <th className="px-3 py-2 text-right">Actions</th>
            </tr>
          </thead>
          <tbody>
            {filtered.map((image) => {
              const tag = imageTag(image);
              const labels = image.Labels ? Object.entries(image.Labels) : [];
              return (
                <tr key={image.Id} className="table-row">
                  <td className="px-3 py-2">
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-ink-600 bg-ink-900 accent-sky-500"
                      checked={selected.has(image.Id)}
                      onChange={() => toggleOne(image.Id)}
                      aria-label={`Select ${tag ?? imageShortId(image)}`}
                    />
                  </td>
                  <td className="px-3 py-2">
                    <div className="flex items-center gap-2">
                      <Icon name="disc" className="h-4 w-4 shrink-0 text-slate-400" />
                      <div className="min-w-0">
                        {tag ? (
                          <div className="truncate font-mono text-xs text-slate-200">{tag}</div>
                        ) : (
                          <div className="flex items-center gap-1.5">
                            <span className="badge border border-amber-800/60 bg-amber-950/60 text-amber-300">untagged</span>
                            <span className="font-mono text-[11px] text-slate-400">no repository</span>
                          </div>
                        )}
                        {labels.length > 0 && (
                          <div className="mt-0.5 flex items-center gap-1 text-[10px] text-slate-400">
                            <span>{labels.length} label{labels.length === 1 ? '' : 's'}</span>
                            <InfoTip label="Image labels">
                              <div className="max-h-40 space-y-0.5 overflow-y-auto font-mono text-[10px]">
                                {labels.map(([key, value]) => (<div key={key}><span className="text-slate-400">{key}</span>=<span className="text-slate-300">{value}</span></div>))}
                              </div>
                            </InfoTip>
                          </div>
                        )}
                      </div>
                    </div>
                  </td>
                  <td className="px-3 py-2"><code className="text-[11px] text-slate-400">{imageShortId(image)}</code></td>
                  <td className="px-3 py-2 text-xs text-slate-400">{image.Created ? formatRelative(image.Created) : '—'}</td>
                  <td className="px-3 py-2 text-xs text-slate-400">{formatBytes(image.Size)}</td>
                  <td className="px-3 py-2">
                    {usedBy(image) === 0
                      ? <span className="badge border border-amber-800/60 bg-amber-950/60 text-amber-300">unused</span>
                      : <span className="text-xs text-slate-400">{usedBy(image)} container{usedBy(image) === 1 ? '' : 's'}</span>}
                  </td>
                  <td className="px-3 py-2 text-right">
                    <div className="flex justify-end gap-1">
                      <button className="btn-primary px-2 py-1 text-xs" onClick={() => setRunTarget(image)}><Icon name="play" className="h-3.5 w-3.5" /> Run</button>
                      <ConfirmButton label="Remove" confirmLabel="Remove image" onConfirm={() => api.removeImage(image.Id).then(load)} />
                    </div>
                  </td>
                </tr>
              );
            })}
            {filtered.length === 0 && <tr><td colSpan={7} className="px-3 py-8 text-center text-slate-400">{images.length === 0 ? 'No images on this host.' : 'No images match the filter.'}</td></tr>}
          </tbody>
        </table>
      </div>

      <div className="space-y-3 md:hidden">
        {filtered.map((image) => {
          const tag = imageTag(image);
          const labels = image.Labels ? Object.entries(image.Labels) : [];
          return (
            <div key={image.Id} className="card space-y-2 p-3">
              <div className="flex items-start gap-2">
                <input
                  type="checkbox"
                  className="mt-0.5 h-4 w-4 shrink-0 rounded border-ink-600 bg-ink-900 accent-sky-500"
                  checked={selected.has(image.Id)}
                  onChange={() => toggleOne(image.Id)}
                  aria-label={`Select ${tag ?? imageShortId(image)}`}
                />
                <Icon name="disc" className="mt-0.5 h-4 w-4 shrink-0 text-slate-400" />
                <div className="min-w-0 flex-1">
                  {tag
                    ? <div className="truncate font-mono text-xs text-slate-200">{tag}</div>
                    : <div className="flex items-center gap-1.5"><span className="badge border border-amber-800/60 bg-amber-950/60 text-amber-300">untagged</span><span className="font-mono text-[11px] text-slate-400">{imageShortId(image)}</span></div>}
                  <div className="text-[11px] text-slate-400">
                    {formatBytes(image.Size)} · {image.Created ? formatRelative(image.Created) : 'unknown'} · {usedBy(image) === 0 ? 'unused' : `${usedBy(image)} container${usedBy(image) === 1 ? '' : 's'}`}
                  </div>
                  {labels.length > 0 && <div className="text-[10px] text-slate-400">{labels.length} label{labels.length === 1 ? '' : 's'}</div>}
                </div>
              </div>
              <div className="flex items-center gap-1">
                <button className="btn-primary px-2 py-1 text-xs" onClick={() => setRunTarget(image)}><Icon name="play" className="h-3.5 w-3.5" /> Run</button>
                <ConfirmButton label="Remove image" confirmLabel="Remove image" onConfirm={() => api.removeImage(image.Id).then(load)} />
              </div>
            </div>
          );
        })}
        {filtered.length === 0 && <EmptyState icon="disc" title={images.length === 0 ? 'No images on this host' : 'No images match'} hint="Pull an image above to get started." />}
      </div>

      <RegistryManager registries={registries} reload={load} notify={notify} />

      {runTarget && (
        <RunImageModal
          image={runTarget}
          onClose={() => setRunTarget(null)}
          onDone={(name) => {
            setRunTarget(null);
            notify(`Started ${name}`, 'success');
            setView('containers');
          }}
          notify={notify}
        />
      )}
    </div>
  );
}

function RunImageModal({
  image,
  onClose,
  onDone,
  notify
}: {
  image: DockerImageSummary;
  onClose: () => void;
  onDone: (name: string) => void;
  notify: Notify;
}) {
  const reference = imageTag(image) ?? image.Id;
  const suggested = (imageTag(image)?.split('/').pop()?.split(':')[0] ?? 'app')
    .replace(/[^a-z0-9_.-]/gi, '-')
    .toLowerCase() || 'app';
  const [name, setName] = useState(suggested);
  const [command, setCommand] = useState('');
  const [env, setEnv] = useState('');
  const [ports, setPorts] = useState('');
  const [restart, setRestart] = useState('unless-stopped');
  const [start, setStart] = useState(true);
  const [busy, setBusy] = useState(false);

  const run = async () => {
    setBusy(true);
    try {
      const result = await api.runImage({
        image: reference,
        name: name.trim() || undefined,
        command: command.trim() || undefined,
        env: env.split('\n').map((line) => line.trim()).filter(Boolean),
        ports: ports.split('\n').map((line) => line.trim()).filter(Boolean),
        restartPolicy: restart,
        start
      });
      onDone(result.name);
    } catch (error) {
      notify((error as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title="Run image" onClose={onClose} wide>
      <div className="space-y-3">
        <div className="flex items-center gap-2 rounded-lg border border-ink-700 bg-ink-900/50 p-2.5">
          <Icon name="disc" className="h-4 w-4 text-slate-400" />
          <span className="truncate font-mono text-xs text-slate-200">{reference}</span>
        </div>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Container name" hint="Leave blank to auto-generate.">
            <input className="input" value={name} onChange={(e) => setName(e.target.value)} placeholder="my-app" />
          </Field>
          <Field label="Restart policy" info="Controls what Docker does if the container exits.">
            <select className="input" value={restart} onChange={(e) => setRestart(e.target.value)}>
              <option value="unless-stopped">Unless stopped</option>
              <option value="always">Always</option>
              <option value="on-failure">On failure</option>
              <option value="no">Never</option>
            </select>
          </Field>
        </div>
        <Field label="Command / args" hint="Leave blank to use the image default. Example: nginx -g 'daemon off;'">
          <input className="input font-mono text-xs" value={command} onChange={(e) => setCommand(e.target.value)} placeholder="(image default)" />
        </Field>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Environment variables" hint="One KEY=VALUE per line.">
            <textarea className="input h-28 font-mono text-xs" value={env} onChange={(e) => setEnv(e.target.value)} placeholder={'TZ=UTC\nPUID=1000'} spellCheck={false} />
          </Field>
          <Field label="Port mappings" hint="One per line: host:container or host:container/udp.">
            <textarea className="input h-28 font-mono text-xs" value={ports} onChange={(e) => setPorts(e.target.value)} placeholder={'8080:80\n53:53/udp'} spellCheck={false} />
          </Field>
        </div>
        <label className="flex items-center gap-2 text-sm">
          <Toggle checked={start} onChange={setStart} label="Start immediately" /> Start immediately
        </label>
        <div className="flex justify-end gap-2 pt-1">
          <button className="btn-ghost" onClick={onClose}>Cancel</button>
          <button className="btn-primary" disabled={busy} onClick={run}>
            <Icon name="play" className="h-4 w-4" /> {busy ? 'Starting…' : 'Run container'}
          </button>
        </div>
      </div>
    </Modal>
  );
}

function RegistryManager({ registries, reload, notify }: { registries: RegistryInfo[]; reload: () => void; notify: Notify }) {
  const [draft, setDraft] = useState({ name: '', url: 'https://index.docker.io/v1/', username: '', password: '', ignoreSslErrors: false, isDefault: false });

  return (
    <Section
      title="Private registries"
      info="Credentials are encrypted at rest in the vault and sent as X-Registry-Auth when pulling. Ignore SSL is for registries with self-signed certificates."
    >
      <div className="space-y-2">
        {registries.map((registry) => (
          <div key={registry.id} className="flex flex-wrap items-center gap-2 rounded-lg border border-ink-800 bg-ink-900/50 p-2 text-sm">
            <Icon name="database" className="h-4 w-4 text-slate-400" />
            <span className="font-medium">{registry.name}</span>
            <span className="truncate text-xs text-slate-400">{registry.url}</span>
            {registry.ignoreSslErrors && <span className="badge border border-amber-800/60 bg-amber-950/60 text-amber-300">ignore SSL</span>}
            {registry.isDefault && <span className="badge border border-sky-800/60 bg-sky-950/60 text-sky-300">default</span>}
            <div className="ml-auto"><ConfirmButton label="Delete" confirmLabel="Delete registry" onConfirm={() => api.deleteRegistry(registry.id).then(reload)} /></div>
          </div>
        ))}
        {registries.length === 0 && <div className="text-sm text-slate-400">No private registries configured.</div>}
      </div>
      <div className="mt-3 grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
        <input className="input" placeholder="name" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
        <input className="input" placeholder="url" value={draft.url} onChange={(e) => setDraft({ ...draft, url: e.target.value })} />
        <input className="input" placeholder="username" value={draft.username} onChange={(e) => setDraft({ ...draft, username: e.target.value })} />
        <input className="input" type="password" placeholder="password / token" value={draft.password} onChange={(e) => setDraft({ ...draft, password: e.target.value })} />
        <label className="flex items-center gap-2 text-sm"><Toggle checked={draft.ignoreSslErrors} onChange={(value) => setDraft({ ...draft, ignoreSslErrors: value })} label="Ignore SSL errors" /> Ignore SSL errors</label>
        <label className="flex items-center gap-2 text-sm"><Toggle checked={draft.isDefault} onChange={(value) => setDraft({ ...draft, isDefault: value })} label="Default registry" /> Default</label>
      </div>
      <button className="btn-primary mt-3" onClick={async () => { try { await api.saveRegistry(draft); notify('Registry saved', 'success'); reload(); } catch (error) { notify((error as Error).message, 'error'); } }}>
        <Icon name="plus" className="h-4 w-4" /> Add registry
      </button>
    </Section>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 7 — Hosts & webhooks
// ────────────────────────────────────────────────────────────────────────────
const HOST_KINDS = [
  { value: 'UnixSocket', label: 'Local Unix socket', hint: 'The host running DockManager (/var/run/docker.sock).' },
  { value: 'HttpsMutualTls', label: 'Remote over mTLS', hint: 'A remote Docker API over TLS, e.g. https://host:2376. Plain unauthenticated TCP is http://host:2375.' },
  { value: 'AgentMtls', label: 'DockManager agent', hint: 'A remote host running the single-binary agent in --agent mode (https://host:2376).' }
];

function HostsView({ notify, refreshHosts }: { notify: Notify; refreshHosts: () => void }) {
  const [tab, setTab] = useState<'hosts' | 'webhooks'>('hosts');
  const [hosts, setHosts] = useState<DockerHostInfo[]>([]);
  const [webhooks, setWebhooks] = useState<WebhookInfo[]>([]);
  const [editing, setEditing] = useState<DockerHostInfo | null>(null);
  const [pendingCerts, setPendingCerts] = useState<{ clientCertPem: string; clientKeyPem: string; caCertPem: string } | undefined>(undefined);
  const [enrollOpen, setEnrollOpen] = useState(false);
  const [enrollment, setEnrollment] = useState<{ compose: string; endpoint: string; clientCertificatePem: string; clientKeyPem: string; caCertificatePem: string } | null>(null);

  const load = useCallback(() => {
    api.hosts().then(setHosts).catch(() => undefined);
    api.webhooks().then(setWebhooks).catch(() => undefined);
  }, []);

  useEffect(load, [load]);

  const emptyHost: DockerHostInfo = { id: 0, name: '', kind: 'HttpsMutualTls', endpoint: 'https://192.168.1.50:2376', enabled: true, wakeOnLanMac: '', lastSeenAt: null, lastError: null };

  return (
    <div className="space-y-4">
      <PageHeader
        title="Hosts & webhooks"
        icon="server"
        subtitle="Where your containers run, and where notifications go."
        info="A Docker host is any machine running the Docker engine that DockManager can control. Add the local socket, a remote host over mTLS, or enrol a remote agent. Webhooks push events to chat and automation tools."
        actions={
          <>
            <button className="btn-ghost" onClick={() => setEnrollOpen(true)}>
              <Icon name="shieldCheck" className="h-4 w-4" /> <span className="hidden sm:inline">Generate agent</span>
            </button>
            <button className="btn-primary" onClick={() => { setEditing(emptyHost); setPendingCerts(undefined); }}><Icon name="plus" className="h-4 w-4" /> Add host</button>
          </>
        }
      />

      <Callout tone="info" title="What you can do here">
        <ul className="list-disc space-y-1 pl-4">
          <li><strong>Add a Docker host</strong> — the local socket, a remote daemon over mTLS, or an enrolled agent.</li>
          <li><strong>Test connectivity</strong> and see the last error if a host is unreachable.</li>
          <li><strong>Wake a sleeping machine</strong> with a Wake-on-LAN magic packet (set its MAC first).</li>
          <li><strong>Configure webhooks</strong> to send stack, CVE and user events to Discord, Slack, Telegram, Ntfy, Gotify or your own endpoint.</li>
        </ul>
      </Callout>

      <Tabs value={tab} onChange={setTab} tabs={[
        { key: 'hosts', label: `Docker hosts`, icon: 'server', badge: <span className="badge border border-transparent bg-ink-800 text-slate-300">{hosts.length}</span> },
        { key: 'webhooks', label: 'Webhooks', icon: 'webhook', badge: <span className="badge border border-transparent bg-ink-800 text-slate-300">{webhooks.length}</span> }
      ]} />

      {tab === 'hosts' && (
        <div className="space-y-3">
          <Section
            title="Connect a remote Docker engine"
            info="DockManager talks to remote daemons over mutually authenticated TLS (mTLS). Agent mode is the lowest-friction option: it exposes the remote socket over mTLS without reconfiguring dockerd."
          >
            <ol className="list-decimal space-y-1 pl-5 text-sm text-slate-400">
              <li>On the remote machine, run Docker and make TCP <code>2376</code> reachable from this host.</li>
              <li><strong>Easiest:</strong> click <em>Generate agent</em>, save the three files from the compose comments into <code>./agent-certs</code> (<code>server.crt</code>, <code>server.key</code>, <code>ca.crt</code>) and run the compose file. The agent serves a certificate signed by DockManager's internal CA, so both sides of the mTLS handshake validate with no host-level trust setup.</li>
              <li>In the enrollment dialog click <em>Add as host</em> — the <code>https://</code> endpoint and client certificates are pre-filled.</li>
              <li>Already have a TLS-enabled daemon? Add a <em>Remote over mTLS</em> host with an <code>https://host:2376</code> endpoint and paste the client certificate, key and the server's certificate (or its CA).</li>
              <li>No usable certificates? Run the daemon with plain TCP (<code>-H tcp://0.0.0.0:2375</code>) and use an <code>http://host:2375</code> endpoint. This is unauthenticated — restrict it to a trusted network.</li>
              <li>Click <em>Test</em>, then pick the host in the top-bar host switcher to manage it from this single location.</li>
            </ol>
          </Section>
          {hosts.map((host) => (
            <Section
              key={host.id}
              title={
                <span className="flex min-w-0 items-center gap-2">
                  <Icon name={host.kind === 'UnixSocket' ? 'plug' : 'server'} className="h-4 w-4 shrink-0 text-slate-400" />
                  <span className="truncate" title={host.name}>{host.name}</span>
                  <span className="badge border border-transparent bg-ink-800 text-slate-300">{host.kind}</span>
                </span>
              }
              description={<span className="break-all font-mono text-[11px]">{host.endpoint}</span>}
              info="Test checks the Docker API. Wake-on-LAN needs a MAC address. Edit to change the endpoint, credentials or Wake-on-LAN settings."
              actions={
                <>
                  <Toggle checked={host.enabled} onChange={async (value) => { await api.saveHost({ id: host.id, name: host.name, endpoint: host.endpoint, kind: host.kind, enabled: value, wakeOnLanMac: host.wakeOnLanMac }); load(); refreshHosts(); }} label="Enabled" />
                  <button className="btn-ghost px-2 py-1 text-xs" onClick={async () => { const online = (await api.testHost(host.id)).online; notify(online ? `${host.name} is online` : `${host.name} is unreachable`, online ? 'success' : 'error'); }}><Icon name="activity" className="h-3.5 w-3.5" /> <span className="hidden sm:inline">Test</span></button>
                  {host.wakeOnLanMac && <button className="btn-ghost px-2 py-1 text-xs" onClick={async () => { const sent = (await api.wakeHost(host.id)).sent; notify(sent ? 'Magic packet sent' : 'Failed to send', sent ? 'success' : 'error'); }}><Icon name="wifi" className="h-3.5 w-3.5" /> <span className="hidden sm:inline">Wake</span></button>}
                  <button className="btn-ghost px-2 py-1 text-xs" onClick={() => { setEditing(host); setPendingCerts(undefined); }}><Icon name="pencil" className="h-3.5 w-3.5" /> <span className="hidden sm:inline">Edit</span></button>
                  <ConfirmButton label="Remove" confirmLabel="Remove host" onConfirm={async () => { await api.deleteHost(host.id); load(); refreshHosts(); }} />
                </>
              }
            >
              <div className="flex flex-wrap gap-4 text-xs text-slate-400">
                <span className="flex items-center gap-1"><Icon name="wifi" className="h-3.5 w-3.5" /> WoL MAC: {host.wakeOnLanMac || 'not set'}</span>
                <span className="flex items-center gap-1"><Icon name="clock" className="h-3.5 w-3.5" /> Last seen: {host.lastSeenAt ? new Date(host.lastSeenAt).toLocaleString() : 'never'}</span>
                {host.lastError && <span className="flex items-center gap-1 text-red-300"><Icon name="alertTriangle" className="h-3.5 w-3.5" /> {host.lastError}</span>}
              </div>
            </Section>
          ))}
          {hosts.length === 0 && <EmptyState icon="server" title="No Docker hosts yet" hint="Add the local socket or a remote host to start managing containers." action={<button className="btn-primary" onClick={() => { setEditing(emptyHost); setPendingCerts(undefined); }}><Icon name="plus" className="h-4 w-4" /> Add host</button>} />}
        </div>
      )}

      {tab === 'webhooks' && <WebhookManager webhooks={webhooks} reload={load} notify={notify} />}

      {editing && (
        <HostForm
          host={editing}
          initialCerts={pendingCerts}
          onClose={() => { setEditing(null); setPendingCerts(undefined); }}
          onSaved={() => { setEditing(null); setPendingCerts(undefined); load(); refreshHosts(); }}
          notify={notify}
        />
      )}

      {enrollOpen && (
        <EnrollAgentModal
          onClose={() => setEnrollOpen(false)}
          onEnrolled={(result) => {
            setEnrollment(result);
            setEnrollOpen(false);
          }}
          notify={notify}
        />
      )}

      {enrollment && (
        <Modal title="mTLS agent enrollment" onClose={() => setEnrollment(null)} wide>
          <p className="mb-1 text-xs text-slate-400">Endpoint: <code>{enrollment.endpoint}</code></p>
          <p className="mb-2 text-[11px] text-slate-400">
            Save the three certificate files listed in the compose comments into <code>./agent-certs</code> on the
            remote host, then run it and click <em>Add as host</em>. The client certificates are pre-filled for you.
          </p>
          <div className="relative">
            <pre className="max-h-80 overflow-auto rounded-lg bg-ink-950 p-3 font-mono text-[11px] text-slate-300">{enrollment.compose}</pre>
            <button type="button" className="btn-ghost absolute right-2 top-2 px-2 py-1 text-xs" onClick={async () => { try { await navigator.clipboard.writeText(enrollment.compose); notify('Copied to clipboard', 'success'); } catch { notify('Copy failed', 'error'); } }}>
              <Icon name="copy" className="h-3.5 w-3.5" /> Copy
            </button>
          </div>
          <div className="mt-3 flex justify-end gap-2">
            <button className="btn-ghost" onClick={() => setEnrollment(null)}>Close</button>
            <button
              className="btn-primary"
              onClick={() => {
                setPendingCerts({
                  clientCertPem: enrollment.clientCertificatePem,
                  clientKeyPem: enrollment.clientKeyPem,
                  caCertPem: enrollment.caCertificatePem
                });
                setEditing({
                  id: 0,
                  name: 'remote-agent',
                  kind: 'AgentMtls',
                  endpoint: enrollment.endpoint,
                  enabled: true,
                  wakeOnLanMac: '',
                  lastSeenAt: null,
                  lastError: null
                });
                setEnrollment(null);
              }}
            >
              <Icon name="plus" className="h-4 w-4" /> Add as host
            </button>
          </div>
        </Modal>
      )}
    </div>
  );
}

/** Collects the remote host name/address for the mTLS agent enrollment (replaces window.prompt on mobile). */
function EnrollAgentModal({
  onClose,
  onEnrolled,
  notify
}: {
  onClose: () => void;
  onEnrolled: (result: { compose: string; endpoint: string; clientCertificatePem: string; clientKeyPem: string; caCertificatePem: string }) => void;
  notify: Notify;
}) {
  const [name, setName] = useState(`agent-${Date.now().toString(36).slice(-4)}`);
  const [host, setHost] = useState('');
  const [port, setPort] = useState(2376);
  const [busy, setBusy] = useState(false);

  const generate = async () => {
    if (!name.trim() || !host.trim()) {
      notify('Give the agent a name and the remote host address', 'error');
      return;
    }
    setBusy(true);
    try {
      const result = await api.enrollAgent(name.trim(), host.trim(), port);
      notify('Agent enrollment generated', 'success');
      onEnrolled({
        compose: result.compose,
        endpoint: result.endpoint,
        clientCertificatePem: result.clientCertificatePem,
        clientKeyPem: result.clientKeyPem,
        caCertificatePem: result.caCertificatePem
      });
    } catch (error) {
      notify((error as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title="Generate an mTLS agent" onClose={onClose}>
      <div className="space-y-3">
        <p className="text-xs text-slate-400">
          Produces the agent's client and server certificates plus a compose file. Save the certificate files it
          lists into <code>./agent-certs</code> on the remote host and run the compose file, then add it as a Docker
          host. Nothing is created on the remote until you run it.
        </p>
        <Field label="Agent name" hint="A label for this host, e.g. homelab-node-2.">
          <input className="input" autoCapitalize="none" autoCorrect="off" spellCheck={false} value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <div className="grid gap-3 sm:grid-cols-[2fr_1fr]">
          <Field label="Remote host address" hint="Hostname or IP where the agent will run.">
            <input className="input font-mono text-xs" autoCapitalize="none" autoCorrect="off" spellCheck={false} value={host} onChange={(e) => setHost(e.target.value)} placeholder="192.168.1.50" />
          </Field>
          <Field label="Port" hint="mTLS port.">
            <input className="input" type="number" inputMode="numeric" value={port} onChange={(e) => setPort(Number(e.target.value))} />
          </Field>
        </div>
        <div className="flex justify-end gap-2 pt-1">
          <button className="btn-ghost" onClick={onClose}>Cancel</button>
          <button className="btn-primary" disabled={busy} onClick={generate}>
            <Icon name="shieldCheck" className="h-4 w-4" /> {busy ? 'Generating…' : 'Generate'}
          </button>
        </div>
      </div>
    </Modal>
  );
}

function HostForm({
  host,
  initialCerts,
  onClose,
  onSaved,
  notify
}: {
  host: DockerHostInfo;
  initialCerts?: { clientCertPem: string; clientKeyPem: string; caCertPem: string };
  onClose: () => void;
  onSaved: () => void;
  notify: Notify;
}) {
  const [draft, setDraft] = useState({ ...host, wakeOnLanMac: host.wakeOnLanMac ?? '' });
  const [certs, setCerts] = useState({
    clientCertPem: initialCerts?.clientCertPem ?? '',
    clientKeyPem: initialCerts?.clientKeyPem ?? '',
    caCertPem: initialCerts?.caCertPem ?? ''
  });
  const isNew = host.id === 0;
  const needsCerts = draft.kind !== 'UnixSocket';

  return (
    <Modal title={isNew ? 'Add Docker host' : `Edit ${host.name}`} onClose={onClose} wide>
      <div className="space-y-3">
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Name"><input className="input" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} placeholder="homelab-node-2" /></Field>
          <Field label="Connection type" hint={HOST_KINDS.find((k) => k.value === draft.kind)?.hint}>
            <select className="input" value={draft.kind} onChange={(e) => setDraft({ ...draft, kind: e.target.value })}>
              {HOST_KINDS.map((kind) => (<option key={kind.value} value={kind.value}>{kind.label}</option>))}
            </select>
          </Field>
        </div>
        <Field label="Endpoint" hint={draft.kind === 'UnixSocket' ? 'unix:///var/run/docker.sock' : 'https://remote-host:2376'}>
          <input className="input font-mono text-xs" value={draft.endpoint} onChange={(e) => setDraft({ ...draft, endpoint: e.target.value })} />
        </Field>
        <div className="flex items-center gap-2"><Toggle checked={draft.enabled} onChange={(value) => setDraft({ ...draft, enabled: value })} label="Enabled" /><span className="text-sm">Enabled</span></div>

        {needsCerts && (
          <div className="space-y-2 rounded-lg border border-ink-700 bg-ink-900/50 p-3">
            <div className="flex items-center gap-1.5 text-sm font-medium">
              mTLS certificates
              <InfoTip label="mTLS certificates">
                Remote Docker hosts must authenticate with a client certificate signed by a CA the remote
                daemon trusts. Paste the PEM blocks here. The <strong>Generate agent</strong> flow creates
                them for you and pre-fills this form.
              </InfoTip>
            </div>
            <p className="text-[11px] text-slate-400">Paste PEM blocks (BEGIN CERTIFICATE / BEGIN … PRIVATE KEY). Leave a field blank to keep the saved certificate.</p>
            <Field label="Client certificate (PEM)">
              <textarea className="input h-20 font-mono text-[10px]" value={certs.clientCertPem} onChange={(e) => setCerts({ ...certs, clientCertPem: e.target.value })} spellCheck={false} />
            </Field>
            <Field label="Client key (PEM)">
              <textarea className="input h-20 font-mono text-[10px]" value={certs.clientKeyPem} onChange={(e) => setCerts({ ...certs, clientKeyPem: e.target.value })} spellCheck={false} />
            </Field>
            <Field label="CA certificate (PEM)">
              <textarea className="input h-20 font-mono text-[10px]" value={certs.caCertPem} onChange={(e) => setCerts({ ...certs, caCertPem: e.target.value })} spellCheck={false} />
            </Field>
          </div>
        )}

        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Wake-on-LAN MAC" hint="e.g. AA:BB:CC:DD:EE:FF"><input className="input font-mono text-xs" value={draft.wakeOnLanMac} onChange={(e) => setDraft({ ...draft, wakeOnLanMac: e.target.value })} /></Field>
          <Field label="WoL broadcast" hint="Defaults to 255.255.255.255"><input className="input font-mono text-xs" value={draft.wakeOnLanBroadcast ?? ''} onChange={(e) => setDraft({ ...draft, wakeOnLanBroadcast: e.target.value })} /></Field>
        </div>
        <div className="flex justify-end gap-2 pt-1">
          <button className="btn-ghost" onClick={onClose}>Cancel</button>
          <button
            className="btn-primary"
            disabled={!draft.name.trim() || !draft.endpoint.trim()}
            onClick={async () => {
              try {
                await api.saveHost({
                  id: draft.id,
                  name: draft.name,
                  endpoint: draft.endpoint,
                  kind: draft.kind,
                  enabled: draft.enabled,
                  wakeOnLanMac: draft.wakeOnLanMac,
                  wakeOnLanBroadcast: draft.wakeOnLanBroadcast,
                  ...(needsCerts
                    ? {
                        clientCertPem: certs.clientCertPem || undefined,
                        clientKeyPem: certs.clientKeyPem || undefined,
                        caCertPem: certs.caCertPem || undefined
                      }
                    : {})
                });
                notify(isNew ? 'Host added' : 'Host saved', 'success');
                onSaved();
              } catch (error) {
                notify((error as Error).message, 'error');
              }
            }}
          >
            Save
          </button>
        </div>
      </div>
    </Modal>
  );
}

const WEBHOOK_KINDS = ['Discord', 'Telegram', 'Slack', 'Ntfy', 'Gotify', 'GenericHmac'];
const WEBHOOK_EVENTS = ['stack.deploy', 'stack.update', 'stack.rollback', 'cve.alert', 'user.pending'];

function WebhookManager({ webhooks, reload, notify }: { webhooks: WebhookInfo[]; reload: () => void; notify: Notify }) {
  const [draft, setDraft] = useState({ name: '', kind: 'Discord', url: '', chatId: '', secret: '', events: WEBHOOK_EVENTS.join(',') });

  return (
    <Section
      title="Webhooks"
      info="Notifications are formatted natively for each provider. Generic HMAC signs the body with X-Hub-Signature-256."
    >
      <div className="space-y-2">
        {webhooks.map((hook) => (
          <div key={hook.id} className="flex flex-wrap items-center gap-2 rounded-lg border border-ink-800 bg-ink-900/50 p-2 text-sm">
            <Icon name="webhook" className="h-4 w-4 text-slate-400" />
            <span className="font-medium">{hook.name}</span>
            <span className="badge border border-transparent bg-ink-800 text-slate-300">{hook.kind}</span>
            <span className="truncate text-xs text-slate-400">{hook.events}</span>
            {hook.lastStatus && <span className="text-xs text-slate-400">{hook.lastStatus}</span>}
            <div className="ml-auto flex gap-1">
              <button className="btn-ghost px-2 py-1 text-xs" onClick={async () => notify((await api.testWebhook(hook.id)).lastStatus, 'info')}><Icon name="zap" className="h-3.5 w-3.5" /> Test</button>
              <ConfirmButton label="Delete" confirmLabel="Delete webhook" onConfirm={() => api.deleteWebhook(hook.id).then(reload)} />
            </div>
          </div>
        ))}
        {webhooks.length === 0 && <div className="text-sm text-slate-400">No webhooks configured.</div>}
      </div>
      <div className="mt-3 grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
        <input className="input" placeholder="name" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
        <select className="input" value={draft.kind} onChange={(e) => setDraft({ ...draft, kind: e.target.value })}>
          {WEBHOOK_KINDS.map((kind) => (<option key={kind}>{kind}</option>))}
        </select>
        <input className="input" placeholder="webhook url" value={draft.url} onChange={(e) => setDraft({ ...draft, url: e.target.value })} />
        <input className="input" placeholder="telegram chat id (optional)" value={draft.chatId} onChange={(e) => setDraft({ ...draft, chatId: e.target.value })} />
        <input className="input" type="password" placeholder="secret / token (optional)" value={draft.secret} onChange={(e) => setDraft({ ...draft, secret: e.target.value })} />
        <input className="input" placeholder="events" value={draft.events} onChange={(e) => setDraft({ ...draft, events: e.target.value })} />
      </div>
      <div className="mt-2 flex flex-wrap gap-1.5">
        {WEBHOOK_EVENTS.map((event) => (
          <button key={event} type="button" className="badge border border-ink-700 bg-ink-800/60 text-slate-300 hover:border-accent-soft/50" onClick={() => { const set = new Set(draft.events.split(',').map((s) => s.trim()).filter(Boolean)); if (set.has(event)) set.delete(event); else set.add(event); setDraft({ ...draft, events: [...set].join(',') }); }}>
            {event}
          </button>
        ))}
      </div>
      <button className="btn-primary mt-3" onClick={async () => { try { await api.saveWebhook(draft); notify('Webhook saved', 'success'); reload(); } catch (error) { notify((error as Error).message, 'error'); } }}>
        <Icon name="plus" className="h-4 w-4" /> Add webhook
      </button>
    </Section>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 8 — Users
// ────────────────────────────────────────────────────────────────────────────
const ROLES = ['Admin', 'Viewer'];

function UsersView({ notify, session }: { notify: Notify; session: SessionUser }) {
  const [users, setUsers] = useState<UserInfo[]>([]);
  const [pending, setPending] = useState<UserInfo[]>([]);
  const [addOpen, setAddOpen] = useState(false);

  const load = useCallback(() => {
    api.users().then(setUsers).catch(() => undefined);
    api.pendingUsers().then(setPending).catch(() => undefined);
  }, []);

  useEffect(load, [load]);

  const setRole = async (id: number, role: string) => {
    try {
      await api.updateUser(id, { role });
      notify(`Role updated to ${role}`, 'success');
      load();
    } catch (error) {
      notify((error as Error).message, 'error');
    }
  };

  return (
    <div className="space-y-4">
      <PageHeader
        title="Users & roles"
        icon="users"
        subtitle="Who can sign in and what they can do."
        info="Roles control access. Admins manage the whole system; Viewers only open published portal apps; Pending accounts are waiting for approval."
        actions={<button className="btn-primary" onClick={() => setAddOpen(true)}><Icon name="plus" className="h-4 w-4" /> Add user</button>}
      />

      <div className="grid gap-3 sm:grid-cols-3">
        {(['Admin', 'Viewer', 'Pending'] as const).map((role) => (
          <div key={role} className="card flex items-start gap-3 p-3">
            <RoleBadge role={role} withLabel={false} />
            <div className="min-w-0">
              <div className="text-sm font-medium">{role}</div>
              <div className="text-[11px] text-slate-400">{roleBlurb(role)}</div>
            </div>
          </div>
        ))}
      </div>

      {pending.length > 0 && (
        <Section
          title={`Pending approvals (${pending.length})`}
          info="Accounts created by SSO sign-in are held here until an administrator assigns a role."
          className="border-amber-800/50"
        >
          <div className="space-y-2">
            {pending.map((user) => (
              <div key={user.id} className="flex flex-wrap items-center gap-2 rounded-lg border border-ink-800 bg-ink-900/50 p-2 text-sm">
                <Icon name="clock" className="h-4 w-4 text-amber-300" />
                <div className="min-w-0">
                  <div className="truncate font-medium">{user.email || user.username}</div>
                  <div className="text-[11px] text-slate-400">{user.provider} · {new Date(user.createdAt).toLocaleDateString()}</div>
                </div>
                <div className="ml-auto flex gap-1">
                  <button className="btn-ghost px-2 py-1 text-xs" onClick={() => api.approveUser(user.id, 'Viewer').then(load)}>Approve as Viewer</button>
                  <button className="btn-primary px-2 py-1 text-xs" onClick={() => api.approveUser(user.id, 'Admin').then(load)}>Approve as Admin</button>
                </div>
              </div>
            ))}
          </div>
        </Section>
      )}

      <Section title={`All users (${users.length})`} info="Change a role from the dropdown, disable an account to block sign-in, or delete it entirely.">
        <div className="space-y-2">
          {users.map((user) => {
            const isSelf = user.id === session.id;
            return (
              <div key={user.id} className="flex flex-wrap items-center gap-3 rounded-lg border border-ink-800 bg-ink-900/50 p-3">
                <span className="inline-flex h-9 w-9 items-center justify-center rounded-full border border-ink-700 bg-ink-850 text-slate-300"><Icon name="user" className="h-4 w-4" /></span>
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="truncate text-sm font-medium">{user.username}</span>
                    {isSelf && <span className="badge border border-ink-700 bg-ink-800 text-slate-400">you</span>}
                    {user.totpEnabled && <span className="badge border border-emerald-800/60 bg-emerald-950/60 text-emerald-300">2FA</span>}
                  </div>
                  <div className="truncate text-[11px] text-slate-400">{user.email || 'no email'} · {user.provider} · last login {user.lastLoginAt ? new Date(user.lastLoginAt).toLocaleDateString() : 'never'}</div>
                </div>
                <div className="ml-auto flex flex-wrap items-center gap-2">
                  <select className="input w-32 py-1 text-xs" aria-label={`Role for ${user.username}`} value={ROLES.includes(user.role) ? user.role : 'Viewer'} disabled={isSelf} onChange={(e) => setRole(user.id, e.target.value)}>
                    {ROLES.map((role) => (<option key={role} value={role}>{role}</option>))}
                  </select>
                  <label className="flex items-center gap-1.5 text-xs text-slate-400">
                    <Toggle checked={!user.disabled} onChange={(value) => api.updateUser(user.id, { disabled: !value }).then(load)} label="Enabled" />
                    {user.disabled ? 'Disabled' : 'Enabled'}
                  </label>
                  <ConfirmButton label="Delete" confirmLabel="Delete user" onConfirm={async () => { try { await api.deleteUser(user.id); notify('User deleted', 'success'); load(); } catch (error) { notify((error as Error).message, 'error'); } }} />
                </div>
              </div>
            );
          })}
          {users.length === 0 && <div className="text-sm text-slate-400">No users.</div>}
        </div>
      </Section>

      {addOpen && <AddUserModal onClose={() => setAddOpen(false)} onSaved={() => { setAddOpen(false); load(); }} notify={notify} />}
    </div>
  );
}

function AddUserModal({ onClose, onSaved, notify }: { onClose: () => void; onSaved: () => void; notify: Notify }) {
  const [username, setUsername] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState('Viewer');
  const [busy, setBusy] = useState(false);

  const create = async () => {
    if (!username.trim() || password.length < 8) {
      notify('Username and a password of at least 8 characters are required', 'error');
      return;
    }
    setBusy(true);
    try {
      await api.createUser({ username: username.trim(), password, email: email.trim() || undefined, role });
      notify(`User ${username.trim()} created`, 'success');
      onSaved();
    } catch (error) {
      notify((error as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title="Add user" onClose={onClose}>
      <div className="max-h-[70vh] space-y-3 overflow-y-auto pr-1">
        <Field label="Username" hint="Lowercase; used to sign in.">
          <input
            className="input"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            placeholder="jane"
            autoComplete="off"
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
            inputMode="text"
          />
        </Field>
        <Field label="Email" hint="Optional; used for SSO matching and notifications.">
          <input className="input" type="email" inputMode="email" autoCapitalize="none" autoCorrect="off" spellCheck={false} value={email} onChange={(e) => setEmail(e.target.value)} placeholder="jane@example.com" />
        </Field>
        <Field label="Password" hint="At least 8 characters. The user can add a passkey or TOTP afterwards."><input className="input" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" /></Field>
        <Field label="Role" info="Admin manages the whole system; Viewer can only open published portal apps.">
          <select className="input" value={role} onChange={(e) => setRole(e.target.value)}>
            {ROLES.map((value) => (<option key={value} value={value}>{value}</option>))}
          </select>
        </Field>
        <div className="flex justify-end gap-2 pt-1">
          <button className="btn-ghost" onClick={onClose}>Cancel</button>
          <button className="btn-primary" disabled={busy} onClick={create}>{busy ? 'Creating…' : 'Create user'}</button>
        </div>
      </div>
    </Modal>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 9 — Security & SSO
// ────────────────────────────────────────────────────────────────────────────
type McpAction = 'list' | 'read' | 'write' | 'delete';

const MCP_ACTIONS: McpAction[] = ['list', 'read', 'write', 'delete'];

const MCP_RESOURCES: { key: string; label: string; hint: string; actions: McpAction[] }[] = [
  { key: 'stack', label: 'Stacks', hint: 'Deploy, start/stop/restart/down, edit YAML, auto-update, delete.', actions: ['list', 'read', 'write', 'delete'] },
  { key: 'container', label: 'Containers', hint: 'List, inspect, start/stop/restart/pause, remove and read logs.', actions: ['list', 'read', 'write', 'delete'] },
  { key: 'exec', label: 'Exec', hint: 'Run a command inside a container.', actions: ['write'] },
  { key: 'image', label: 'Images', hint: 'List, pull, remove and prune unused images.', actions: ['list', 'read', 'write', 'delete'] },
  { key: 'registry', label: 'Registries', hint: 'Private registry credentials.', actions: ['list', 'write', 'delete'] },
  { key: 'host', label: 'Docker hosts', hint: 'List hosts, test connectivity and Wake-on-LAN.', actions: ['list', 'read', 'write'] },
  { key: 'cluster', label: 'Kubernetes', hint: 'Namespaces, pods, deployments, services, scale, restart, apply manifests.', actions: ['list', 'read', 'write'] },
  { key: 'webhook', label: 'Webhooks', hint: 'Notification targets.', actions: ['list', 'write', 'delete'] },
  { key: 'portal', label: 'Portal apps', hint: 'List, edit, hide, reorder, delete and rediscover published apps.', actions: ['list', 'read', 'write', 'delete'] },
  { key: 'user', label: 'Users', hint: 'List accounts only — MCP can never create, edit or delete users.', actions: ['list'] },
  { key: 'system', label: 'System settings', hint: 'Portal hostname, LAN IP and certificate regeneration.', actions: ['read', 'write'] },
  { key: 'audit', label: 'Audit log', hint: 'Read the activity log.', actions: ['list'] },
  { key: 'convert', label: 'Converters', hint: 'docker run ↔ Compose ↔ Kubernetes conversion.', actions: ['read'] }
];

/** Turns the stored scope string back into a `resource:action` set for the editor. */
function scopesToSet(raw: string): Set<string> {
  const set = new Set<string>();
  const text = (raw ?? '').trim();
  if (!text) return set;
  for (const token of text.replace(/;/g, ',').split(',')) {
    const trimmed = token.trim();
    if (!trimmed) continue;
    if (trimmed === '*' || trimmed === '*:*') {
      for (const resource of MCP_RESOURCES) for (const action of resource.actions) set.add(`${resource.key}:${action}`);
      continue;
    }
    if (!trimmed.includes(':')) {
      const actions: McpAction[] = trimmed === 'write' ? ['write'] : ['list', 'read'];
      for (const resource of MCP_RESOURCES) for (const action of resource.actions) if (actions.includes(action)) set.add(`${resource.key}:${action}`);
      continue;
    }
    const [resource, actions] = trimmed.split(':');
    for (const action of actions.split('+')) {
      const normalized = action.trim() as McpAction;
      if (MCP_ACTIONS.includes(normalized)) set.add(`${resource}:${normalized}`);
    }
  }
  return set;
}

/** Splits a stored scope string into readable chips for the key list. */
function scopeChips(raw: string): string[] {
  const text = (raw ?? '').trim();
  if (!text) return [];
  if (text === '*' || text === '*:*') return ['*:*'];
  if (text === 'write') return ['*:write'];
  if (text === 'read') return ['*:read'];
  const entries = text.includes(';') ? text.split(';') : text.split(',');
  return entries.map((entry) => entry.trim()).filter(Boolean);
}

/** Serialises the editor set into the server's `resource:action+action;…` grammar. */
function setToScopes(set: Set<string>): string {
  const byResource = new Map<string, McpAction[]>();
  for (const entry of set) {
    const [resource, action] = entry.split(':') as [string, McpAction];
    const list = byResource.get(resource) ?? [];
    list.push(action);
    byResource.set(resource, list);
  }
  return [...byResource.entries()]
    .sort((a, b) => a[0].localeCompare(b[0]))
    .map(([resource, actions]) => `${resource}:${MCP_ACTIONS.filter((action) => actions.includes(action)).join('+')}`)
    .join(';');
}

function McpKeyModal({ onClose, onSaved, notify }: { onClose: () => void; onSaved: () => void; notify: Notify }) {
  const [name, setName] = useState('');
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set(['stack:list', 'stack:read', 'container:list', 'container:read'])
  );
  const [search, setSearch] = useState('');
  const [busy, setBusy] = useState(false);
  const [created, setCreated] = useState<{ name: string; token: string } | null>(null);
  const [copied, setCopied] = useState(false);
  const [wildcard, setWildcard] = useState(false);

  const scopes = wildcard ? '*:*' : setToScopes(selected);
  const filtered = MCP_RESOURCES.filter(
    (resource) => `${resource.label} ${resource.key} ${resource.hint}`.toLowerCase().includes(search.toLowerCase())
  );

  const toggle = (resource: string, action: McpAction) =>
    setSelected((prev) => {
      const next = new Set(prev);
      const key = `${resource}:${action}`;
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const actionAllOn = (action: McpAction) =>
    MCP_RESOURCES.filter((resource) => resource.actions.includes(action)).every((resource) =>
      selected.has(`${resource.key}:${action}`)
    );

  const toggleAction = (action: McpAction) => {
    const on = actionAllOn(action);
    setSelected((prev) => {
      const next = new Set(prev);
      for (const resource of MCP_RESOURCES) {
        if (!resource.actions.includes(action)) continue;
        if (on) next.delete(`${resource.key}:${action}`);
        else next.add(`${resource.key}:${action}`);
      }
      return next;
    });
  };

  const preset = (kind: 'read' | 'operate' | 'full') => {
    setWildcard(false);
    const next = new Set<string>();
    for (const resource of MCP_RESOURCES) {
      if (kind === 'read') {
        if (resource.actions.includes('list')) next.add(`${resource.key}:list`);
        else if (resource.actions.includes('read')) next.add(`${resource.key}:read`);
      } else if (kind === 'operate') {
        if (resource.actions.includes('write')) next.add(`${resource.key}:write`);
        else if (resource.actions.includes('list')) next.add(`${resource.key}:list`);
      } else {
        for (const action of resource.actions) next.add(`${resource.key}:${action}`);
      }
    }
    setSelected(next);
  };

  const create = async () => {
    if (!name.trim()) {
      notify('Give the key a name', 'error');
      return;
    }
    if (selected.size === 0) {
      notify('Pick at least one permission', 'error');
      return;
    }
    setBusy(true);
    try {
      const result = await api.createMcpKey(name.trim(), scopes);
      setCreated({ name: result.name, token: result.token });
      onSaved();
    } catch (error) {
      notify((error as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };

  if (created) {
    return (
      <Modal title={`MCP key “${created.name}” created`} onClose={onClose}>
        <Callout tone="warn" title="Copy this token now — it is shown only once">
          Store it in your MCP client configuration. DockManager keeps only a hash; if you lose it, revoke the
          key and create a new one.
        </Callout>
        <div className="mt-3 rounded-lg border border-ink-700 bg-ink-950 p-3">
          <code className="block break-all font-mono text-xs text-emerald-300">{created.token}</code>
        </div>
        <div className="mt-3 flex flex-wrap items-center justify-end gap-2">
          <button
            className="btn-ghost"
            onClick={async () => {
              try {
                await navigator.clipboard.writeText(created.token);
                setCopied(true);
              } catch {
                notify('Copy failed — select the text and copy manually', 'error');
              }
            }}
          >
            <Icon name={copied ? 'check' : 'copy'} className="h-4 w-4" /> {copied ? 'Copied' : 'Copy'}
          </button>
          <button className="btn-primary" onClick={onClose}>OK, I saved it</button>
        </div>
      </Modal>
    );
  }

  return (
    <Modal title="Create MCP key" wide onClose={onClose}>
      <div className="space-y-3">
        <Field label="Key name" hint="A label for this token, e.g. “claude-desktop” or “ci-bot”.">
          <input className="input" value={name} onChange={(event) => setName(event.target.value)} placeholder="claude-desktop" />
        </Field>

        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs text-slate-400">Presets:</span>
          <button type="button" className="btn-ghost px-2 py-1 text-xs" onClick={() => preset('read')}>
            <Icon name="eye" className="h-3.5 w-3.5" /> Read-only
          </button>
          <button type="button" className="btn-ghost px-2 py-1 text-xs" onClick={() => preset('operate')}>
            <Icon name="sliders" className="h-3.5 w-3.5" /> Operate
          </button>
          <button type="button" className="btn-ghost px-2 py-1 text-xs" onClick={() => preset('full')}>
            <Icon name="crown" className="h-3.5 w-3.5" /> Full access
          </button>
          <label className="ml-auto flex items-center gap-2 text-[11px] text-slate-400">
            <input type="checkbox" checked={wildcard} onChange={(event) => setWildcard(event.target.checked)} />
            Unrestricted (*:*)
          </label>
          <span className="text-[11px] text-slate-400">write implies read + list · delete is separate</span>
        </div>

        <div className="relative">
          <Icon name="search" className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <input className="input pl-9" placeholder="Filter services…" value={search} onChange={(event) => setSearch(event.target.value)} />
        </div>

        <div className="max-h-72 overflow-y-auto rounded-lg border border-ink-800">
          <div className="sticky top-0 z-10 flex items-center gap-2 border-b border-ink-800 bg-ink-900/95 px-3 py-2 backdrop-blur">
            <span className="flex-1 text-xs font-semibold uppercase tracking-wide text-slate-400">All services</span>
            {MCP_ACTIONS.map((action) => (
              <label key={action} className="flex w-12 flex-col items-center gap-0.5 text-[10px] uppercase text-slate-400">
                <input type="checkbox" checked={actionAllOn(action)} onChange={() => toggleAction(action)} />
                {action}
              </label>
            ))}
          </div>
          {filtered.map((resource) => (
            <div key={resource.key} className="flex items-center gap-2 border-b border-ink-800/60 px-3 py-2 last:border-b-0">
              <div className="min-w-0 flex-1">
                <div className="truncate text-sm text-slate-200">{resource.label}</div>
                <div className="truncate text-[11px] text-slate-400" title={resource.hint}>{resource.hint}</div>
              </div>
              {MCP_ACTIONS.map((action) => {
                const supported = resource.actions.includes(action);
                const checked = selected.has(`${resource.key}:${action}`);
                return (
                  <label key={action} className={classNames('flex w-12 flex-col items-center gap-0.5 text-[10px] uppercase', supported ? 'text-slate-400' : 'text-slate-700')}>
                    <input
                      type="checkbox"
                      disabled={!supported}
                      checked={supported && checked}
                      onChange={() => toggle(resource.key, action)}
                      aria-label={`${action} ${resource.label}`}
                    />
                    {action}
                  </label>
                );
              })}
            </div>
          ))}
          {filtered.length === 0 && <div className="px-3 py-6 text-center text-sm text-slate-400">No services match.</div>}
        </div>

        <div>
          <div className="mb-1 flex items-center gap-2">
            <span className="label mb-0">Scopes</span>
            <InfoTip label="Scopes">
              Sent to the server as <code>resource:actions</code> grants. <code>tools/list</code> returns only the
              tools these scopes allow, and every call is checked. Use <code>*:*</code> for unrestricted access.
            </InfoTip>
            <button
              type="button"
              className="btn-ghost ml-auto px-2 py-0.5 text-[11px]"
              onClick={async () => {
                try {
                  await navigator.clipboard.writeText(scopes);
                  notify('Scopes copied', 'success');
                } catch {
                  notify('Copy failed', 'error');
                }
              }}
            >
              <Icon name="copy" className="h-3 w-3" /> Copy
            </button>
          </div>
          <code className="block break-all rounded-lg border border-ink-700 bg-ink-950 p-2 font-mono text-[11px] text-slate-300">
            {scopes || 'read'}
          </code>
        </div>

        <div className="flex justify-end gap-2 pt-1">
          <button className="btn-ghost" onClick={onClose}>Cancel</button>
          <button className="btn-primary" disabled={busy} onClick={create}>
            <Icon name="plus" className="h-4 w-4" /> {busy ? 'Creating…' : 'Create key'}
          </button>
        </div>
      </div>
    </Modal>
  );
}

/** One-time reveal of an MCP secret (used after rotating a key). */
function McpTokenModal({ name, token, onClose, notify }: { name: string; token: string; onClose: () => void; notify: Notify }) {
  const [copied, setCopied] = useState(false);
  return (
    <Modal title={`New secret for “${name}”`} onClose={onClose}>
      <Callout tone="warn" title="Copy this token now — it is shown only once">
        The previous secret for this key has stopped working. Store the new one in your MCP client configuration.
      </Callout>
      <div className="mt-3 rounded-lg border border-ink-700 bg-ink-950 p-3">
        <code className="block break-all font-mono text-xs text-emerald-300">{token}</code>
      </div>
      <div className="mt-3 flex justify-end gap-2">
        <button
          className="btn-ghost"
          onClick={async () => {
            try {
              await navigator.clipboard.writeText(token);
              setCopied(true);
            } catch {
              notify('Copy failed — select the text and copy manually', 'error');
            }
          }}
        >
          <Icon name={copied ? 'check' : 'copy'} className="h-4 w-4" /> {copied ? 'Copied' : 'Copy'}
        </button>
        <button className="btn-primary" onClick={onClose}>OK, I saved it</button>
      </div>
    </Modal>
  );
}

/** Edit an existing MCP key: rename, enable/disable and re-scope. The secret is never shown here. */
function McpKeyEditModal({
  keyInfo,
  onClose,
  onSaved,
  notify
}: {
  keyInfo: McpKeyInfo;
  onClose: () => void;
  onSaved: () => void;
  notify: Notify;
}) {
  const [name, setName] = useState(keyInfo.name);
  const [enabled, setEnabled] = useState(keyInfo.enabled);
  const [selected, setSelected] = useState<Set<string>>(() => scopesToSet(keyInfo.scopes));
  const [search, setSearch] = useState('');
  const [busy, setBusy] = useState(false);

  const scopes = setToScopes(selected);
  const filtered = MCP_RESOURCES.filter(
    (resource) => `${resource.label} ${resource.key} ${resource.hint}`.toLowerCase().includes(search.toLowerCase())
  );

  const toggle = (resource: string, action: McpAction) =>
    setSelected((prev) => {
      const next = new Set(prev);
      const key = `${resource}:${action}`;
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const actionAllOn = (action: McpAction) =>
    MCP_RESOURCES.filter((resource) => resource.actions.includes(action)).every((resource) =>
      selected.has(`${resource.key}:${action}`)
    );

  const toggleAction = (action: McpAction) => {
    const on = actionAllOn(action);
    setSelected((prev) => {
      const next = new Set(prev);
      for (const resource of MCP_RESOURCES) {
        if (!resource.actions.includes(action)) continue;
        if (on) next.delete(`${resource.key}:${action}`);
        else next.add(`${resource.key}:${action}`);
      }
      return next;
    });
  };

  const save = async () => {
    if (!name.trim()) {
      notify('Give the key a name', 'error');
      return;
    }
    setBusy(true);
    try {
      await api.updateMcpKey(keyInfo.id, { name: name.trim(), enabled, scopes });
      notify('MCP key updated', 'success');
      onSaved();
    } catch (error) {
      notify((error as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={`Edit "${keyInfo.name}"`} wide onClose={onClose}>
      <div className="max-h-[72vh] space-y-3 overflow-y-auto pr-1">
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Key name" hint="Appears in the audit log next to every action this key performs.">
            <input className="input" value={name} onChange={(event) => setName(event.target.value)} />
          </Field>
          <div className="flex items-end">
            <label className="flex items-center gap-2 text-sm">
              <Toggle checked={enabled} onChange={setEnabled} label="Enabled" /> Enabled
            </label>
          </div>
        </div>

        <div className="relative">
          <Icon name="search" className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <input className="input pl-9" placeholder="Filter services…" value={search} onChange={(event) => setSearch(event.target.value)} />
        </div>

        <div className="max-h-64 overflow-y-auto rounded-lg border border-ink-800">
          <div className="sticky top-0 z-10 flex items-center gap-2 border-b border-ink-800 bg-ink-900/95 px-3 py-2 backdrop-blur">
            <span className="flex-1 text-xs font-semibold uppercase tracking-wide text-slate-400">All services</span>
            {MCP_ACTIONS.map((action) => (
              <label key={action} className="flex w-12 flex-col items-center gap-0.5 text-[10px] uppercase text-slate-400">
                <input type="checkbox" checked={actionAllOn(action)} onChange={() => toggleAction(action)} aria-label={`Toggle ${action} on every service`} />
                {action}
              </label>
            ))}
          </div>
          {filtered.map((resource) => (
            <div key={resource.key} className="flex items-center gap-2 border-b border-ink-800/60 px-3 py-2 last:border-b-0">
              <div className="min-w-0 flex-1">
                <div className="truncate text-sm text-slate-200">{resource.label}</div>
                <div className="truncate text-[11px] text-slate-400">{resource.hint}</div>
              </div>
              {MCP_ACTIONS.map((action) => {
                const supported = resource.actions.includes(action);
                const checked = selected.has(`${resource.key}:${action}`);
                return (
                  <label key={action} className={classNames('flex w-12 flex-col items-center gap-0.5 text-[10px] uppercase', supported ? 'text-slate-400' : 'text-slate-700')}>
                    <input
                      type="checkbox"
                      disabled={!supported}
                      checked={supported && checked}
                      onChange={() => toggle(resource.key, action)}
                      aria-label={`${action} ${resource.label}`}
                    />
                    {action}
                  </label>
                );
              })}
            </div>
          ))}
          {filtered.length === 0 && <div className="px-3 py-6 text-center text-sm text-slate-400">No services match.</div>}
        </div>

        <div>
          <span className="label">Scopes</span>
          <code className="block break-all rounded-lg border border-ink-700 bg-ink-950 p-2 font-mono text-[11px] text-slate-300">
            {scopes || '(no permissions)'}
          </code>
        </div>

        <div className="flex justify-end gap-2 pt-1">
          <button className="btn-ghost" onClick={onClose}>Cancel</button>
          <button className="btn-primary" disabled={busy} onClick={save}>{busy ? 'Saving…' : 'Save changes'}</button>
        </div>
      </div>
    </Modal>
  );
}

function SecurityAndSsoView({ notify }: { notify: Notify }) {
  const [passkeys, setPasskeys] = useState<PasskeyInfo[]>([]);
  const [keys, setKeys] = useState<McpKeyInfo[]>([]);
  const [totpSetup, setTotpSetup] = useState<{ secret: string; qrSvg: string; otpauthUri: string } | null>(null);
  const [totpCode, setTotpCode] = useState('');
  const [samlSp, setSamlSp] = useState<Record<string, unknown>>({});
  const [oidcGoogle, setOidcGoogle] = useState<Record<string, unknown>>({});
  const [keyModalOpen, setKeyModalOpen] = useState(false);
  const [editingKey, setEditingKey] = useState<McpKeyInfo | null>(null);
  const [rotatedKey, setRotatedKey] = useState<{ name: string; token: string } | null>(null);

  const load = useCallback(() => {
    api.passkeys().then(setPasskeys).catch(() => undefined);
    api.mcpKeys().then(setKeys).catch(() => undefined);
    api.samlSp().then(setSamlSp).catch(() => undefined);
    api.oidcProvider('google').then(setOidcGoogle).catch(() => undefined);
  }, []);

  useEffect(load, [load]);

  const updateKey = async (id: number, payload: { enabled?: boolean; scopes?: string }) => {
    try {
      await api.updateMcpKey(id, payload);
      notify('MCP key updated', 'success');
      load();
    } catch (error) {
      notify((error as Error).message, 'error');
    }
  };

  const rotateKey = async (key: McpKeyInfo) => {
    try {
      const result = await api.rotateMcpKey(key.id);
      setRotatedKey({ name: result.name, token: result.token });
      load();
    } catch (error) {
      notify((error as Error).message, 'error');
    }
  };

  const registerPasskey = async () => {
    try {
      const begin = await api.passkeyRegisterBegin();
      const credential = await navigator.credentials.create({ publicKey: decodeCredentialCreationOptions(begin.options) });
      if (!credential) return;
      await api.passkeyRegisterComplete(begin.flowId, encodeCredential(credential as PublicKeyCredential), 'Passkey');
      notify('Passkey registered', 'success');
      load();
    } catch (error) {
      notify((error as Error).message, 'error');
    }
  };

  return (
    <div className="space-y-4">
      <PageHeader
        title="Security & SSO"
        icon="shield"
        subtitle="Sign-in methods and machine access."
        info="Register passkeys, enable TOTP two-factor, connect Google/Microsoft OIDC or a SAML 2.0 identity provider, and issue scoped API keys for the MCP server."
      />

      <div className="grid gap-4 lg:grid-cols-2">
        <Section
          title="Passkeys (WebAuthn)"
          info="Passwordless sign-in with a fingerprint, face or hardware key. You can register several and remove them individually."
          actions={<button className="btn-primary px-2 py-1 text-xs" onClick={registerPasskey}><Icon name="plus" className="h-3.5 w-3.5" /> Register</button>}
        >
          <Callout tone="info" title="Passkeys require a trusted certificate">
            Browsers only offer passkeys on a secure origin whose TLS certificate is valid — a
            self-signed warning blocks <code>navigator.credentials</code> with
            “WebAuthn is not supported on sites with TLS certificate errors”. Open this portal by its
            hostname (not an IP address), install the DockManager root CA on the device
            (<a className="text-accent underline decoration-accent/40 underline-offset-2 hover:decoration-accent" href={api.sslDownloadUrl}>download ca.crt</a>),
            then reload before registering. DNS and SAN guidance is in Settings → TLS certificate.
          </Callout>
          <div className="mt-3 space-y-1">
            {passkeys.map((passkey) => (
              <div key={passkey.id} className="flex items-center gap-2 rounded-lg border border-ink-800 bg-ink-900/50 p-2 text-sm">
                <Icon name="key" className="h-4 w-4 text-slate-400" />
                <span className="truncate">{passkey.name}</span>
                <span className="text-xs text-slate-400">{new Date(passkey.createdAt).toLocaleDateString()}</span>
                <ConfirmButton className="ml-auto" label="Remove" confirmLabel="Remove passkey" onConfirm={() => api.deletePasskey(passkey.id).then(load)} />
              </div>
            ))}
            {passkeys.length === 0 && <div className="text-sm text-slate-400">No passkeys registered.</div>}
          </div>
        </Section>

        <Section
          title="TOTP authenticator"
          info="Time-based one-time codes (RFC 6238, 6 digits, 30-second period). Used as a second factor for password logins."
        >
          <div className="flex flex-wrap gap-2">
            <button className="btn-primary" onClick={async () => { setTotpCode(''); setTotpSetup(await api.totpSetup()); }}>
              <Icon name="lock" className="h-4 w-4" /> Set up TOTP
            </button>
            <button className="btn-ghost" onClick={() => api.totpDisable().then(() => notify('TOTP disabled', 'success'))}>Disable</button>
          </div>
        </Section>

        {totpSetup && (
          <Modal title="Set up two-factor authentication" onClose={() => setTotpSetup(null)}>
            <div className="max-h-[70vh] space-y-3 overflow-y-auto pr-1">
              <p className="text-xs text-slate-400">
                Scan the QR code with Google Authenticator, Microsoft Authenticator, 1Password or any RFC 6238
                app, then enter the 6-digit code it shows to finish.
              </p>
              <div
                className="mx-auto w-full max-w-[240px] overflow-hidden rounded-lg bg-white p-3 [&>svg]:h-auto [&>svg]:w-full"
                dangerouslySetInnerHTML={{ __html: totpSetup.qrSvg }}
              />
              <div>
                <div className="label">Manual entry key</div>
                <div className="break-all rounded-lg border border-ink-700 bg-ink-950 p-2 font-mono text-xs text-slate-300">{totpSetup.secret}</div>
              </div>
              <Field label="6-digit code" hint="Refreshes every 30 seconds.">
                <input className="input tracking-[0.4em]" placeholder="000000" value={totpCode} onChange={(e) => setTotpCode(e.target.value)} inputMode="numeric" autoComplete="one-time-code" />
              </Field>
              <div className="flex justify-end gap-2">
                <button className="btn-ghost" onClick={() => setTotpSetup(null)}>Cancel</button>
                <button className="btn-primary" disabled={totpCode.trim().length !== 6} onClick={async () => {
                  try { await api.totpEnable(totpCode); notify('TOTP enabled', 'success'); setTotpSetup(null); setTotpCode(''); }
                  catch (error) { notify((error as Error).message, 'error'); }
                }}>Enable two-factor</button>
              </div>
            </div>
          </Modal>
        )}

        <Section title="Google / Microsoft OIDC" info="OAuth2 / OpenID Connect. Unknown emails are created as Pending and must be approved in Users.">
          <Field label="Google Client ID"><input className="input" value={(oidcGoogle.clientId as string) ?? ''} onChange={(e) => setOidcGoogle({ ...oidcGoogle, clientId: e.target.value })} /></Field>
          <Field label="Google Client Secret" className="mt-2"><input className="input" type="password" placeholder={oidcGoogle.hasSecret ? '(unchanged)' : ''} onChange={(e) => setOidcGoogle({ ...oidcGoogle, clientSecret: e.target.value })} /></Field>
          <label className="mt-2 flex items-center gap-2 text-sm">
            <Toggle checked={Boolean(oidcGoogle.enabled)} onChange={(value) => setOidcGoogle({ ...oidcGoogle, enabled: value })} label="OIDC enabled" /> Enabled
          </label>
          <button className="btn-primary mt-3" onClick={async () => { await api.saveOidcProvider('google', oidcGoogle); notify('OIDC saved (restart to apply)', 'success'); }}>Save Google OIDC</button>
        </Section>

        <Section title="SAML 2.0 Service Provider" info="Connect Microsoft Entra ID, Google Workspace, Okta or JumpCloud. Download the SP metadata to configure your IdP.">
          <Callout tone="info" title="How SAML sign-in works">
            DockManager is the <strong>Service Provider</strong> (SP): users start at DockManager, are sent to your
            identity provider, and come back with a signed assertion. Give the identity provider the{' '}
            <strong>SP metadata</strong> below, then paste the provider's <strong>IdP metadata URL</strong> here.
          </Callout>
          <Field
            label="SP Entity ID"
            className="mt-3"
            info="The unique identifier your identity provider uses for DockManager. Usually the SP metadata URL."
          >
            <input className="input font-mono text-xs" value={(samlSp.entityId as string) ?? ''} onChange={(e) => setSamlSp({ ...samlSp, entityId: e.target.value })} />
          </Field>
          <Field
            label="IdP metadata URL"
            className="mt-2"
            info="A URL the identity provider publishes with its signing certificate and SSO endpoint. DockManager fetches it to validate assertions."
          >
            <input className="input font-mono text-xs" value={(samlSp.idpMetadataUrl as string) ?? ''} onChange={(e) => setSamlSp({ ...samlSp, idpMetadataUrl: e.target.value })} placeholder="https://idp.example.com/app/metadata" />
          </Field>
          <div className="mt-2 flex flex-wrap items-center gap-2">
            <a className="btn-ghost" href={api.samlMetadataUrl} target="_blank" rel="noreferrer"><Icon name="download" className="h-3.5 w-3.5" /> SP metadata</a>
            <a className="btn-ghost" href={api.samlIdpMetadataUrl} target="_blank" rel="noreferrer"><Icon name="download" className="h-3.5 w-3.5" /> IdP metadata</a>
          </div>
          <button className="btn-primary mt-3" onClick={async () => { await api.saveSamlSp(samlSp); notify('SAML SP saved', 'success'); }}>Save SAML SP</button>
        </Section>

        <Section
          className="lg:col-span-2"
          title="MCP API keys"
          info="Scoped bearer tokens (ehdm_…) for the built-in MCP server. Actions are audited and stamped as the MCP actor."
          actions={<button className="btn-primary px-2 py-1 text-xs" onClick={() => setKeyModalOpen(true)}><Icon name="plus" className="h-3.5 w-3.5" /> Create key</button>}
        >
          <Callout tone="info" title="Connect an MCP client">
            Point it at <code>https://{window.location.host}/mcp</code> with the header{' '}
            <code>Authorization: Bearer ehdm_…</code>. DockManager speaks JSON-RPC 2.0 (Streamable HTTP);
            <code>tools/list</code> returns only the tools the key's scopes allow, and users are read-only via MCP
            (Admin accounts are protected). Each key's <strong>name</strong> is what appears in the audit log, so use a
            meaningful one.
          </Callout>
          <div className="mt-3 space-y-2">
            {keys.map((key) => (
              <div key={key.id} className="rounded-lg border border-ink-800 bg-ink-900/50 p-3">
                <div className="flex flex-wrap items-center gap-2">
                  <Icon name="key" className="h-4 w-4 text-slate-400" />
                  <span className="text-sm font-medium">{key.name}</span>
                  {!key.enabled && <span className="badge border border-amber-800/60 bg-amber-950/60 text-amber-300">disabled</span>}
                  <code className="text-[11px] text-slate-400">{key.prefix}…</code>
                  <span className="ml-auto flex flex-wrap items-center gap-2">
                    <label className="flex items-center gap-1.5 text-xs text-slate-400">
                      <Toggle checked={key.enabled} onChange={(value) => updateKey(key.id, { enabled: value })} label={`Enable ${key.name}`} />
                      Enabled
                    </label>
                    <button type="button" className="btn-ghost px-2 py-1 text-xs" onClick={() => setEditingKey(key)}>
                      <Icon name="pencil" className="h-3.5 w-3.5" /> Edit
                    </button>
                    <ConfirmButton label="Rotate" confirmLabel="Rotate secret" icon="refreshCw" onConfirm={() => rotateKey(key)} />
                    <ConfirmButton label="Revoke" confirmLabel="Revoke key" onConfirm={() => api.deleteMcpKey(key.id).then(load)} />
                  </span>
                </div>
                <div className="mt-2 flex flex-wrap gap-1">
                  {scopeChips(key.scopes).map((chip) => (
                    <span key={chip} className="badge border border-ink-700 bg-ink-800/60 font-mono text-[10px] normal-case text-slate-300">{chip}</span>
                  ))}
                  {scopeChips(key.scopes).length === 0 && <span className="text-[11px] text-slate-400">no scopes</span>}
                </div>
                <div className="mt-1 text-[10px] text-slate-400">
                  {key.lastUsedAt ? `last used ${formatRelative(key.lastUsedAt)}` : 'never used'} · created {formatRelative(key.createdAt)}
                </div>
              </div>
            ))}
            {keys.length === 0 && <div className="text-sm text-slate-400">No API keys.</div>}
          </div>
        </Section>

        {keyModalOpen && (
          <McpKeyModal onClose={() => setKeyModalOpen(false)} onSaved={load} notify={notify} />
        )}
        {editingKey && (
          <McpKeyEditModal
            keyInfo={editingKey}
            onClose={() => setEditingKey(null)}
            onSaved={() => { setEditingKey(null); load(); }}
            notify={notify}
          />
        )}
        {rotatedKey && (
          <McpTokenModal name={rotatedKey.name} token={rotatedKey.token} onClose={() => setRotatedKey(null)} notify={notify} />
        )}
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// View 10 — Settings
// ────────────────────────────────────────────────────────────────────────────
function certCovers(cert: CertificateInfo | undefined, domain: string): boolean {
  if (!cert || !domain) return false;
  const dns = cert.dnsNames.map((d) => d.toLowerCase());
  if (dns.includes(domain.toLowerCase())) return true;
  const parts = domain.split('.');
  if (parts.length < 2) return false;
  return dns.includes(`*.${parts.slice(1).join('.')}`);
}

/** Grouped display preferences (kept in localStorage, applied by every relevant view). */
function DisplaySettings() {
  const [dashboardRefresh, setDashboardRefresh] = usePersistentState<number>('ehdm.refresh.dashboard', 10);
  const [containerRefresh, setContainerRefresh] = usePersistentState<number>('ehdm.refresh.containers', 5);
  const [stackRefresh, setStackRefresh] = usePersistentState<number>('ehdm.refresh.stacks', 30);
  const [logTimestamps, setLogTimestamps] = usePersistentState<boolean>('ehdm.logs.timestamps', true);

  return (
    <Section
      title="Display settings"
      description="Defaults for auto-refresh and log presentation. Stored in this browser only."
      info="Each view can still override its own refresh interval while you are on it; these are the defaults a new visit starts with."
    >
      <div className="grid gap-4 sm:grid-cols-3">
        <div>
          <div className="label">Dashboard refresh</div>
          <RefreshControl value={dashboardRefresh} onChange={setDashboardRefresh} label="Dashboard" />
        </div>
        <div>
          <div className="label">Containers refresh</div>
          <RefreshControl value={containerRefresh} onChange={setContainerRefresh} label="Containers" />
        </div>
        <div>
          <div className="label">Stacks refresh</div>
          <RefreshControl value={stackRefresh} onChange={setStackRefresh} label="Stacks" />
        </div>
      </div>
      <label className="mt-4 flex items-center gap-2 text-sm">
        <Toggle checked={logTimestamps} onChange={setLogTimestamps} label="Show log timestamps by default" />
        Show log timestamps by default
      </label>
      <p className="mt-1 text-[11px] text-slate-400">
        Applies to container logs and stack service logs. Turn it off for a cleaner view; you can still toggle it per session.
      </p>
    </Section>
  );
}

function SettingsView({ notify }: { notify: Notify }) {
  const [settings, setSettings] = useState<SystemSettings | null>(null);
  const [info, setInfo] = useState<SystemInfo | null>(null);
  const [domain, setDomain] = useState('');
  const [lanIp, setLanIp] = useState('');
  const [regenerate, setRegenerate] = useState(true);
  const [busy, setBusy] = useState(false);
  const [acme, setAcme] = useState<AcmeSettings | null>(null);
  const [acmeDomain, setAcmeDomain] = useState('');
  const [acmeEmail, setAcmeEmail] = useState('');
  const [acmeEnabled, setAcmeEnabled] = useState(false);
  const [acmeStaging, setAcmeStaging] = useState(false);
  const [acmeBusy, setAcmeBusy] = useState(false);
  const [acmeMessage, setAcmeMessage] = useState<{ tone: 'ok' | 'bad' | 'warn'; text: string } | null>(null);

  const loadAcme = useCallback(() => {
    api.acmeSettings()
      .then((value) => {
        setAcme(value);
        setAcmeDomain(value.config.domain);
        setAcmeEmail(value.config.email);
        setAcmeEnabled(value.config.enabled);
        setAcmeStaging(value.config.useStaging);
      })
      .catch(() => undefined);
  }, []);

  const load = useCallback(() => {
    api.systemSettings().then((value) => { setSettings(value); setDomain(value.portalDomain); setLanIp(value.lanIp); }).catch((e) => notify((e as Error).message, 'error'));
    api.systemInfo().then(setInfo).catch(() => undefined);
    loadAcme();
  }, [notify, loadAcme]);

  useEffect(load, [load]);

  const saveAcmeConfig = async () => {
    setAcmeBusy(true);
    try {
      await api.saveAcme({
        enabled: acmeEnabled,
        email: acmeEmail,
        domain: acmeDomain,
        useStaging: acmeStaging,
        directoryUrl: acme?.config.directoryUrl ?? ''
      });
      notify('Let’s Encrypt settings saved', 'success');
      loadAcme();
    } catch (error) {
      notify((error as Error).message, 'error');
    } finally {
      setAcmeBusy(false);
    }
  };

  const issueAcmeCertificate = async () => {
    setAcmeBusy(true);
    setAcmeMessage({ tone: 'warn', text: 'Contacting the certificate authority… this can take up to a minute.' });
    try {
      const result = await api.issueAcme();
      if (result.ok) {
        setAcmeMessage({
          tone: 'ok',
          text: `Issued for ${result.domains.join(', ')}${result.notAfter ? ` · expires ${new Date(result.notAfter).toLocaleDateString()}` : ''}. Hot-swapped into Kestrel.`
        });
        notify('Certificate issued', 'success');
      } else {
        setAcmeMessage({ tone: 'bad', text: result.error ?? 'Issuance failed.' });
        notify('Issuance failed', 'error');
      }
      load();
    } catch (error) {
      setAcmeMessage({ tone: 'bad', text: (error as Error).message });
      notify((error as Error).message, 'error');
    } finally {
      setAcmeBusy(false);
    }
  };

  const apply = async () => {
    setBusy(true);
    try {
      await api.saveSystemSettings({ portalDomain: domain, lanIp, regenerateCertificate: regenerate });
      notify('Settings applied', 'success');
      load();
    } catch (error) {
      notify((error as Error).message, 'error');
    } finally {
      setBusy(false);
    }
  };

  const covered = certCovers(settings?.certificate, domain);
  const uptimeHours = Math.floor((info?.uptimeSeconds ?? 0) / 3600);
  const sourceLabel = settings?.portalDomainSource === 'EHDM_PORTAL_DOMAIN'
    ? 'EHDM_PORTAL_DOMAIN (environment)'
    : settings?.portalDomainSource === 'database'
      ? 'Saved in DockManager'
      : 'Built-in default';

  return (
    <div className="space-y-4">
      <PageHeader
        title="Settings"
        icon="settings"
        subtitle="Server identity, TLS certificate and system information."
        info="Set the hostname DockManager publishes under so portal links and the TLS certificate line up with your DNS."
      />

      <Section
        title="Server identity & hostname"
        info="The portal domain is the base hostname for every published app: an app with subdomain nextcloud is served at https://nextcloud.<domain>. It must resolve in DNS (a wildcard A record) and be covered by the TLS certificate."
      >
        <div className="grid gap-4 lg:grid-cols-[1fr_1fr]">
          <div className="space-y-3">
            <Field label="Portal domain" hint="For example dock.lan or home.example.com.">
              <input className="input font-mono" value={domain} onChange={(e) => setDomain(e.target.value)} placeholder="dock.lan" />
            </Field>
            <Field label="LAN IP address" hint="Used for the certificate and Wake-on-LAN guidance.">
              <input className="input font-mono" value={lanIp} onChange={(e) => setLanIp(e.target.value)} placeholder="10.10.1.1" />
            </Field>
            <label className="flex items-start gap-2 text-sm">
              <input type="checkbox" className="mt-1" checked={regenerate} onChange={(e) => setRegenerate(e.target.checked)} />
              <span>Regenerate the TLS certificate for this domain (recommended)<span className="block text-[11px] text-slate-400">Adds <code>{domain || 'domain'}</code> and <code>*.{domain || 'domain'}</code> to the certificate SANs and hot-swaps it with no downtime.</span></span>
            </label>
            <div className="flex items-center gap-2">
              <button className="btn-primary" disabled={busy} onClick={apply}><Icon name="check" className="h-4 w-4" /> {busy ? 'Applying…' : 'Apply'}</button>
              <span className="text-xs text-slate-400">Currently: <span className="text-slate-300">{settings?.portalDomain ?? '…'}</span> · source: {sourceLabel}</span>
            </div>
          </div>

          <div className="space-y-3">
            <Callout tone={covered ? 'ok' : 'warn'} title={covered ? 'Certificate covers this domain' : 'Certificate may not cover this domain'}>
              {covered
                ? `The active certificate includes ${domain}. Browsers will trust portal links without warnings (once the internal CA is trusted).`
                : `The active certificate does not include ${domain}. Enable the regeneration option and Apply, or a browser warning will appear.`}
            </Callout>
            <div className="rounded-lg border border-ink-800 bg-ink-900/50 p-3 text-xs text-slate-400">
              <div className="mb-1 font-semibold text-slate-300">DNS checklist</div>
              <ul className="list-disc space-y-1 pl-4">
                <li>Create an <code>A</code> record for <code>{domain || 'dock.lan'}</code> → {lanIp || 'your server IP'}.</li>
                <li>Create a wildcard <code>A</code> record for <code>*.{domain || 'dock.lan'}</code> → {lanIp || 'your server IP'}.</li>
                <li>Point clients at the DNS server that hosts these records, or add them to <code>/etc/hosts</code>.</li>
              </ul>
              <button type="button" className="btn-ghost mt-2 px-2 py-1 text-xs" onClick={async () => { try { await navigator.clipboard.writeText(`*.${domain} A ${lanIp}`); notify('Copied record', 'success'); } catch { notify('Copy failed', 'error'); } }}>
                <Icon name="copy" className="h-3.5 w-3.5" /> Copy wildcard record
              </button>
            </div>
          </div>
        </div>
      </Section>

      <Section
        title="TLS certificate"
        info="An ECDSA P-256 leaf signed by the internal root CA. Regenerating hot-swaps it into Kestrel without dropping connections. Download ca.crt to trust the internal CA on your devices."
      >
        <div className="grid gap-3 sm:grid-cols-2">
          <div className="text-sm">
            <div className="text-xs uppercase tracking-wide text-slate-400">Subject</div>
            <div className="truncate font-mono text-xs text-slate-300">{settings?.certificate.subject || '—'}</div>
            <div className="mt-2 text-xs uppercase tracking-wide text-slate-400">Expires</div>
            <div className="text-xs text-slate-300">{settings?.certificate.notAfter ? new Date(settings.certificate.notAfter).toLocaleString() : '—'}</div>
            <div className="mt-2 text-xs uppercase tracking-wide text-slate-400">Thumbprint</div>
            <div className="truncate font-mono text-[11px] text-slate-400">{settings?.certificate.thumbprint || '—'}</div>
          </div>
          <div>
            <div className="text-xs uppercase tracking-wide text-slate-400">Subject alternative names</div>
            <div className="mt-1 flex flex-wrap gap-1">
              {(settings?.certificate.dnsNames ?? []).map((name) => (<span key={name} className="badge border border-ink-700 bg-ink-800/60 font-mono text-[10px] normal-case text-slate-300">{name}</span>))}
              {(settings?.certificate.ipAddresses ?? []).map((ip) => (<span key={ip} className="badge border border-ink-700 bg-ink-800/60 font-mono text-[10px] normal-case text-slate-400">{ip}</span>))}
              {(settings?.certificate.dnsNames?.length ?? 0) === 0 && <span className="text-xs text-slate-400">none</span>}
            </div>
            <div className="mt-3 flex flex-wrap gap-2">
              <button className="btn-ghost" onClick={async () => { const result = await api.sslRegenerate([domain, `*.${domain}`, 'localhost'], [lanIp, '127.0.0.1', '::1'].filter(Boolean)); notify(`New certificate ${result.thumbprint.slice(0, 12)}… (hot-swapped)`, 'success'); load(); }}>
                <Icon name="refreshCw" className="h-4 w-4" /> Regenerate now
              </button>
              <a className="btn-ghost" href={api.sslDownloadUrl}><Icon name="download" className="h-4 w-4" /> Download ca.crt</a>
            </div>
          </div>
        </div>
        <div className="mt-4 rounded-lg border border-ink-800 bg-ink-900/50 p-3 text-xs text-slate-400">
          <div className="mb-1 font-semibold text-slate-300">Trust this certificate on your devices</div>
          <p className="mb-2">
            Browsers warn until the internal root CA is trusted, and passkeys are blocked outright on a
            certificate-error origin. Download{' '}
            <a className="text-accent underline decoration-accent/40 underline-offset-2 hover:decoration-accent" href={api.sslDownloadUrl}>ca.crt</a>, then:
          </p>
          <ul className="grid gap-1.5 sm:grid-cols-2">
            <li><strong className="text-slate-300">Windows</strong> — double-click the file → Install Certificate → Local Machine → Trusted Root Certification Authorities. (Firefox keeps its own store: Settings → Certificates → Authorities → Import.)</li>
            <li><strong className="text-slate-300">macOS</strong> — open the file in Keychain, then set Trust → Always Trust (or <code>sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain ca.crt</code>).</li>
            <li><strong className="text-slate-300">Linux</strong> — <code>sudo cp ca.crt /usr/local/share/ca-certificates/dockmanager-ca.crt &amp;&amp; sudo update-ca-certificates</code>; Chrome also needs it in NSS (<code>certutil -d sql:$HOME/.pki/nssdb -A -t "C,," -n DockManager -i ca.crt</code>).</li>
            <li><strong className="text-slate-300">iPhone / iPad</strong> — open the file in <em>Safari</em> → Settings → Profile Downloaded → Install, then Settings → General → About → Certificate Trust Settings → enable full trust.</li>
            <li><strong className="text-slate-300">Android</strong> — Settings → Security → Encryption &amp; credentials → Install a certificate → CA certificate.</li>
            <li className="text-amber-300 sm:col-span-2">Use the hostname, not the IP (passkeys need a domain RP ID), and fully restart the browser afterwards.</li>
          </ul>
        </div>
      </Section>

      <Section
        title="Let’s Encrypt (automatic HTTPS)"
        info="Requests a publicly trusted certificate over ACME v2 with an http-01 challenge. The domain must resolve publicly and inbound TCP/80 must reach this server. Renewal runs automatically ~30 days before expiry."
      >
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Domain(s)" hint="Comma-separated; the first is the CN. Example: home.example.com,cloud.example.com">
            <input className="input font-mono" value={acmeDomain} onChange={(e) => setAcmeDomain(e.target.value)} placeholder="home.example.com" />
          </Field>
          <Field label="Contact email" hint="Used by the CA for expiry notices.">
            <input className="input" value={acmeEmail} onChange={(e) => setAcmeEmail(e.target.value)} placeholder="you@example.com" />
          </Field>
        </div>
        <div className="mt-2 flex flex-wrap items-center gap-x-5 gap-y-2 text-sm">
          <label className="flex items-center gap-2">
            <Toggle checked={acmeEnabled} onChange={setAcmeEnabled} label="Let’s Encrypt enabled" /> Enabled
          </label>
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={acmeStaging} onChange={(e) => setAcmeStaging(e.target.checked)} />
            Use the staging CA (for testing)
          </label>
          <span className="text-[11px] text-slate-400">
            {acme?.active ? 'Active certificate is from ACME' : 'Currently using the internal CA'}
            {acme?.directory ? ` · ${acme.directory}` : ''}
          </span>
        </div>
        <div className="mt-3 flex flex-wrap items-center gap-2">
          <button className="btn-primary" disabled={acmeBusy} onClick={saveAcmeConfig}>
            <Icon name="check" className="h-4 w-4" /> Save
          </button>
          <button className="btn-ghost" disabled={acmeBusy} onClick={issueAcmeCertificate}>
            <Icon name="lock" className="h-4 w-4" /> {acmeBusy ? 'Working…' : 'Issue / renew now'}
          </button>
          {acme?.config.lastIssuedAt && (
            <span className="text-xs text-slate-400">Last issued {formatRelative(acme.config.lastIssuedAt)}</span>
          )}
        </div>
        {(acmeMessage || acme?.config.lastError) && (
          <div className="mt-3">
            <Callout
              tone={acmeMessage?.tone === 'ok' ? 'ok' : acmeMessage?.tone === 'bad' || acme?.config.lastError ? 'bad' : 'warn'}
              title={acmeMessage?.tone === 'ok' ? 'Success' : 'Issuance problem'}
            >
              {acmeMessage?.text ?? acme?.config.lastError}
            </Callout>
          </div>
        )}
      </Section>

      <DisplaySettings />

      <div className="grid gap-4 lg:grid-cols-2">
        <Section title="System information" info="Runtime facts reported by the server.">
          <dl className="grid grid-cols-2 gap-y-2 text-sm">
            <dt className="text-slate-400">App version</dt><dd className="text-right font-mono text-xs">{info?.version ? `v${info.version}` : '—'}</dd>
            <dt className="text-slate-400">Build token</dt><dd className="truncate text-right font-mono text-[10px] text-slate-400" title={info?.appVersion}>{info?.appVersion ?? '—'}</dd>
            <dt className="text-slate-400">Boot ID</dt><dd className="text-right font-mono text-xs">{info?.bootId ?? '—'}</dd>
            <dt className="text-slate-400">Portal domain</dt><dd className="text-right font-mono text-xs">{info?.portalDomain ?? '—'}</dd>
            <dt className="text-slate-400">Data directory</dt><dd className="text-right font-mono text-xs">{info?.dataDirectory ?? '—'}</dd>
            <dt className="text-slate-400">Uptime</dt><dd className="text-right text-xs">{uptimeHours}h</dd>
            <dt className="text-slate-400">Proxy routes</dt><dd className="text-right text-xs">{info?.routes ?? 0}</dd>
            <dt className="text-slate-400">Docker hosts</dt><dd className="text-right text-xs">{info?.dockerHosts ?? 0}</dd>
            <dt className="text-slate-400">Kubernetes clusters</dt><dd className="text-right text-xs">{info?.kubeClusters ?? 0}</dd>
            <dt className="text-slate-400">SAML SP / IdP</dt><dd className="text-right text-xs">{info?.samlSpEnabled ? 'on' : 'off'} / {info?.samlIdpEnabled ? 'on' : 'off'}</dd>
          </dl>
        </Section>

        <Section title="About" info="eh.DockManager is the control plane for home servers and enterprise container hosts.">
          <p className="text-sm text-slate-400">
            Port 443 reverse proxy and app portal, Docker and Kubernetes management, health-guarded
            auto-updates and a built-in MCP server. The public landing page lives at{' '}
            <a className="text-accent underline decoration-accent/40 underline-offset-2 hover:decoration-accent" href="/">the portal</a>.
          </p>
          <div className="mt-3 flex flex-wrap gap-2">
            <a className="btn-ghost" href={api.samlIdpMetadataUrl} target="_blank" rel="noreferrer">IdP metadata</a>
            <a className="btn-ghost" href={api.sslDownloadUrl}>Root CA</a>
          </div>
        </Section>
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
  const previousCount = useRef(tabs.length);

  useEffect(() => {
    // Focus a newly opened terminal tab instead of keeping the previous one active.
    if (tabs.length > previousCount.current) setActiveId(tabs[tabs.length - 1].id);
    previousCount.current = tabs.length;
    if (tabs.length > 0 && !tabs.some((t) => t.id === activeId)) setActiveId(tabs[tabs.length - 1].id);
    if (tabs.length === 0) onOpenChange(false);
  }, [tabs, activeId, onOpenChange]);

  if (!open || tabs.length === 0) return null;
  const active = tabs.find((t) => t.id === activeId) ?? tabs[0];

  return (
    <div className="fixed inset-x-0 bottom-0 z-40 flex h-[55vh] flex-col border-t border-ink-700 bg-ink-950 md:bottom-0 md:left-60">
      <div className="flex items-center gap-1 overflow-x-auto border-b border-ink-800 px-2 py-1">
        <Icon name="terminal" className="ml-1 h-4 w-4 shrink-0 text-slate-400" />
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            className={classNames('flex shrink-0 items-center gap-1 rounded px-2 py-1 text-xs', tab.id === active.id ? 'bg-ink-800 text-white' : 'text-slate-400 hover:bg-ink-800/60')}
            onClick={() => setActiveId(tab.id)}
          >
            {tab.title}
            <span
              className="text-slate-400 hover:text-red-300"
              onClick={(event) => { event.stopPropagation(); onClose(tab.id); }}
            >
              <Icon name="x" className="h-3 w-3" />
            </span>
          </button>
        ))}
        <span className="ml-auto flex shrink-0 items-center gap-1">
          <button className="btn-ghost px-2 py-1 text-xs" onClick={() => onOpenChange(false)}>
            <Icon name="chevronDown" className="h-3.5 w-3.5" /> <span className="hidden sm:inline">Hide</span>
          </button>
          <button
            className="btn-ghost px-2 py-1 text-xs"
            onClick={() => { tabs.forEach((tab) => onClose(tab.id)); onOpenChange(false); }}
            aria-label="Close all terminals"
            title="Close all terminals"
          >
            <Icon name="x" className="h-4 w-4" />
          </button>
        </span>
      </div>
      <TerminalPane key={active.id} tab={active} />
      <MobileTouchBar />
    </div>
  );
}

function TerminalPane({ tab }: { tab: TerminalTab }) {
  const containerRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!containerRef.current) return undefined;
    const terminal = new Terminal({
      convertEol: true,
      fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
      fontSize: 13,
      theme: { background: '#00000000', foreground: '#e5e7eb', cursor: '#38bdf8' },
      scrollback: 5000
    });
    const fit = new FitAddon();
    terminal.loadAddon(fit);
    terminal.open(containerRef.current);
    fit.fit();

    const socket = new WebSocket(tab.url);
    socket.binaryType = 'arraybuffer';
    const write = (data: string) => socket.readyState === WebSocket.OPEN && socket.send(new TextEncoder().encode(data));

    socket.onmessage = (event) => {
      if (typeof event.data === 'string') terminal.write(event.data);
      else terminal.write(new Uint8Array(event.data as ArrayBuffer));
    };
    socket.onopen = () => {
      terminal.onData((data) => write(data));
      fit.fit();
    };
    socket.onclose = (event) => {
      // 1000 = clean close; 4001 = the server already explained the failure in-band.
      if (event.code === 1000 || event.code === 4001) return;
      terminal.write(`\r\n\x1b[31m[connection closed${event.reason ? `: ${event.reason}` : ''}]\x1b[0m\r\n`);
      terminal.write('\x1b[90mThe container may have no shell. Try /bin/bash or /bin/ash from Inspect → Exec, or use Inspect → Files.\x1b[0m\r\n');
    };

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
          type="button"
          className="btn-ghost shrink-0 px-2 py-1 text-xs"
          onMouseDown={(event) => { event.preventDefault(); sendKey(key.send); }}
        >
          {key.label}
        </button>
      ))}
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// WebAuthn encoding helpers
// ────────────────────────────────────────────────────────────────────────────
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
  const json: Record<string, unknown> = { id: credential.id, rawId: bufferToBase64Url(credential.rawId), type: credential.type };
  if (response.attestationObject) {
    json.response = { attestationObject: bufferToBase64Url(response.attestationObject), clientDataJSON: bufferToBase64Url(response.clientDataJSON) };
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
