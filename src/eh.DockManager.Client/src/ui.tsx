// File: src/eh.DockManager.Client/src/ui.tsx
import { cloneElement, isValidElement, useEffect, useId, useMemo, useRef, useState } from 'react';
import type { ReactElement, ReactNode } from 'react';
import { Icon, type IconName } from './icons';
import {
  INDENT,
  enterInsertion,
  indentSelection,
  lintEnums,
  suggest,
  type Completion,
  type YamlIssue
} from './yaml';

export type Notify = (message: string, tone?: 'info' | 'success' | 'error') => void;
export type OpenTerminal = (title: string, url: string) => void;

export function classNames(...parts: (string | false | null | undefined)[]): string {
  return parts.filter(Boolean).join(' ');
}

/** Human-readable byte size, e.g. 1536 -> "1.5 KB". */
export function formatBytes(bytes: number | null | undefined, digits = 1): string {
  const value = Number(bytes ?? 0);
  if (!Number.isFinite(value) || value <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  const index = Math.min(units.length - 1, Math.floor(Math.log(value) / Math.log(1024)));
  const scaled = value / 1024 ** index;
  return `${scaled.toFixed(index === 0 ? 0 : digits)} ${units[index]}`;
}

/** Relative time such as "3h ago"; falls back to a locale date for older values. */
export function formatRelative(input: string | number | null | undefined): string {
  if (input === null || input === undefined || input === '') return 'never';
  const date = typeof input === 'number' ? new Date(input * 1000) : new Date(input);
  if (Number.isNaN(date.getTime())) return 'never';
  const seconds = Math.round((Date.now() - date.getTime()) / 1000);
  if (seconds < 60) return 'just now';
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  if (days < 30) return `${days}d ago`;
  return date.toLocaleDateString();
}

// ────────────────────────────────────────────────────────────────────────────
// Status + role pills
// ────────────────────────────────────────────────────────────────────────────
function statusTone(status: string): string {
  const value = status.toLowerCase();
  if (/(running|online|healthy|active|ready|succeeded|up|deployed|bound|public)/.test(value))
    return 'bg-emerald-950/60 text-emerald-300 border-emerald-800/60';
  if (/(paused|starting|pending|degraded|provisioning|creating|restarting|warning|sso|idle|stopped|exited)/.test(value))
    return 'bg-amber-950/60 text-amber-300 border-amber-800/60';
  if (/(failed|error|dead|unhealthy|offline|crash|admin|down)/.test(value))
    return 'bg-red-950/60 text-red-300 border-red-900/60';
  return 'bg-ink-800/80 text-slate-300 border-ink-700';
}

export function StatusPill({ status, dot = true }: { status: string; dot?: boolean }) {
  return (
    <span className={classNames('badge border normal-case tracking-normal', statusTone(status))}>
      {dot && <span className="h-1.5 w-1.5 rounded-full bg-current" />}
      {status}
    </span>
  );
}

const ROLE_META: Record<string, { icon: IconName; tone: string; blurb: string }> = {
  Admin: {
    icon: 'shieldCheck',
    tone: 'bg-accent-muted/60 text-accent border-accent-soft/50',
    blurb: 'Full control: containers, stacks, clusters, hosts, users and settings.'
  },
  Viewer: {
    icon: 'eye',
    tone: 'bg-sky-950/60 text-sky-300 border-sky-800/60',
    blurb: 'Portal access only: can open published apps but cannot change the system.'
  },
  Pending: {
    icon: 'clock',
    tone: 'bg-amber-950/60 text-amber-300 border-amber-800/60',
    blurb: 'Waiting for an administrator to approve access.'
  }
};

export function RoleBadge({ role, withLabel = true }: { role?: string; withLabel?: boolean }) {
  const key = role ?? 'Viewer';
  const meta = ROLE_META[key] ?? ROLE_META.Viewer;
  return (
    <span className={classNames('badge border', meta.tone)} title={meta.blurb}>
      <Icon name={meta.icon} className="h-3.5 w-3.5" />
      {withLabel && key}
    </span>
  );
}

export function roleBlurb(role?: string): string {
  return (ROLE_META[role ?? 'Viewer'] ?? ROLE_META.Viewer).blurb;
}

// ────────────────────────────────────────────────────────────────────────────
// Info tooltip — the "i" affordance. Hover on desktop, tap on touch.
// ────────────────────────────────────────────────────────────────────────────
export function InfoTip({ label, children, className }: { label: string; children: ReactNode; className?: string }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLSpanElement | null>(null);

  useEffect(() => {
    if (!open) return undefined;
    const onDown = (event: PointerEvent) => {
      if (ref.current && !ref.current.contains(event.target as Node)) setOpen(false);
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    document.addEventListener('pointerdown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('pointerdown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  return (
    <span ref={ref} className={classNames('group/tip relative inline-flex align-middle', className)}>
      <button
        type="button"
        aria-label={`About ${label}`}
        aria-expanded={open}
        onClick={(event) => {
          event.stopPropagation();
          setOpen((value) => !value);
        }}
        className="inline-flex h-6 w-6 items-center justify-center rounded-full text-slate-400 transition-colors hover:bg-ink-800 hover:text-accent focus:outline-none focus-visible:ring-2 focus-visible:ring-accent/70"
      >
        <Icon name="info" className="h-3.5 w-3.5" />
      </button>
      <span
        role="tooltip"
        className={classNames(
          'pointer-events-none absolute left-1/2 top-full z-50 mt-2 w-64 max-w-[78vw] -translate-x-1/2 rounded-xl border border-ink-700 bg-ink-850 p-3 text-left text-xs font-normal normal-case leading-relaxed tracking-normal text-slate-300 shadow-2xl transition-opacity duration-150',
          open ? 'visible opacity-100' : 'invisible opacity-0 group-hover/tip:visible group-hover/tip:opacity-100'
        )}
      >
        {children}
      </span>
    </span>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// Layout atoms
// ────────────────────────────────────────────────────────────────────────────
export function PageHeader({
  title,
  subtitle,
  icon,
  info,
  actions
}: {
  title: string;
  subtitle?: ReactNode;
  icon?: IconName;
  info?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <div className="flex flex-wrap items-start gap-3">
      {icon && (
        <span className="mt-0.5 hidden h-10 w-10 items-center justify-center rounded-xl border border-ink-700 bg-ink-850/70 text-accent sm:inline-flex">
          <Icon name={icon} className="h-5 w-5" />
        </span>
      )}
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-1.5">
          <h1 className="truncate text-lg font-semibold sm:text-xl">{title}</h1>
          {info && <InfoTip label={title}>{info}</InfoTip>}
        </div>
        {subtitle && <p className="mt-0.5 text-xs text-slate-400 sm:text-sm">{subtitle}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  );
}

export function Section({
  title,
  description,
  info,
  actions,
  children,
  className
}: {
  title?: ReactNode;
  description?: ReactNode;
  info?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={classNames('card p-4', className)}>
      {(title || actions) && (
        <div className="mb-3 flex flex-wrap items-start gap-2">
          {title && (
            <div className="min-w-0 flex-1">
              <div className="flex items-center gap-1.5">
                <h2 className="text-sm font-semibold">{title}</h2>
                {info && <InfoTip label={typeof title === 'string' ? title : 'section'}>{info}</InfoTip>}
              </div>
              {description && <p className="mt-0.5 text-xs text-slate-400">{description}</p>}
            </div>
          )}
          {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
        </div>
      )}
      {children}
    </section>
  );
}

export function StatTile({
  label,
  value,
  hint,
  icon,
  tone = 'accent',
  info,
  onClick
}: {
  label: string;
  value: string;
  hint?: ReactNode;
  icon: IconName;
  tone?: 'accent' | 'ok' | 'warn' | 'bad' | 'violet';
  info?: ReactNode;
  onClick?: () => void;
}) {
  const tones: Record<string, string> = {
    accent: 'text-accent',
    ok: 'text-emerald-300',
    warn: 'text-amber-300',
    bad: 'text-red-300',
    violet: 'text-violet-300'
  };
  const interactive = Boolean(onClick);
  const body = (
    <>
      <div className="flex items-start justify-between gap-2">
        <span className={classNames('inline-flex h-9 w-9 items-center justify-center rounded-lg border border-ink-700 bg-ink-900/70', tones[tone])}>
          <Icon name={icon} className="h-4 w-4" />
        </span>
        {interactive && (
          <span className="mt-1 inline-flex items-center gap-1 text-[10px] font-medium uppercase tracking-wide text-slate-400 transition-colors group-hover:text-accent">
            Open
            <Icon name="chevronRight" className="h-3 w-3" />
          </span>
        )}
      </div>
      <div className="mt-3 text-2xl font-semibold tabular-nums">{value}</div>
      <div className="text-xs font-medium text-slate-300">{label}</div>
      {hint && <div className="mt-0.5 text-[11px] text-slate-400">{hint}</div>}
    </>
  );

  return (
    <div className="relative">
      {interactive ? (
        <button
          type="button"
          onClick={onClick}
          className="card group flex w-full flex-col p-4 text-left outline outline-1 outline-transparent transition-colors hover:bg-ink-850 hover:outline-accent-soft/50 focus:outline-none focus-visible:ring-2 focus-visible:ring-accent/70"
        >
          {body}
        </button>
      ) : (
        <div className="card flex w-full flex-col p-4">{body}</div>
      )}
      {info && (
        <span className="absolute bottom-3 right-3 z-10">
          <InfoTip label={label}>{info}</InfoTip>
        </span>
      )}
    </div>
  );
}

export function EmptyState({ icon = 'sparkles', title, hint, action }: { icon?: IconName; title: string; hint?: ReactNode; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 rounded-xl border border-dashed border-ink-700 bg-ink-900/40 px-6 py-10 text-center">
      <span className="inline-flex h-11 w-11 items-center justify-center rounded-full border border-ink-700 bg-ink-850 text-slate-400">
        <Icon name={icon} className="h-5 w-5" />
      </span>
      <div className="text-sm font-medium text-slate-300">{title}</div>
      {hint && <p className="max-w-md text-xs text-slate-400">{hint}</p>}
      {action && <div className="mt-2">{action}</div>}
    </div>
  );
}

export function Sparkline({ data, stroke = '#38bdf8', height = 30 }: { data: number[]; stroke?: string; height?: number }) {
  if (data.length < 2) return <div style={{ height }} className="text-[10px] text-slate-400">awaiting samples…</div>;
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

export function Tabs<T extends string>({ value, onChange, tabs }: { value: T; onChange: (value: T) => void; tabs: { key: T; label: string; icon?: IconName; badge?: ReactNode }[] }) {
  return (
    <div className="flex gap-1 overflow-x-auto rounded-xl border border-ink-700 bg-ink-900/60 p-1">
      {tabs.map((tab) => (
        <button
          key={tab.key}
          type="button"
          onClick={() => onChange(tab.key)}
          className={classNames(
            'inline-flex shrink-0 items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-medium transition-colors',
            value === tab.key ? 'bg-accent-muted/60 text-white' : 'text-slate-400 hover:bg-ink-800 hover:text-slate-200'
          )}
        >
          {tab.icon && <Icon name={tab.icon} className="h-3.5 w-3.5" />}
          {tab.label}
          {tab.badge}
        </button>
      ))}
    </div>
  );
}

export function Toggle({ checked, onChange, label }: { checked: boolean; onChange: (value: boolean) => void; label: string }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      onClick={() => onChange(!checked)}
      className={classNames(
        'relative inline-flex h-5 w-9 shrink-0 items-center rounded-full border transition-colors',
        checked ? 'border-accent-soft/60 bg-accent-soft/80' : 'border-ink-700 bg-ink-800'
      )}
    >
      <span className={classNames('inline-block h-3.5 w-3.5 rounded-full bg-white transition-transform', checked ? 'translate-x-[1.15rem]' : 'translate-x-[0.15rem]')} />
    </button>
  );
}

export function Field({ label, hint, info, children, className }: { label: string; hint?: ReactNode; info?: ReactNode; children: ReactNode; className?: string }) {
  // A <label> wrapper alone is not enough: without text inside it the control still has no
  // accessible name, and the visible label sits in a sibling. Put the name on the control itself
  // (keeping any aria-label the caller already set) so the `label`/`select-name` audits pass for
  // inputs, selects and textareas alike without restructuring the caller's markup.
  const child = children as ReactElement<{ 'aria-label'?: string }>;
  const control = isValidElement(children)
    ? cloneElement(child, { 'aria-label': child.props['aria-label'] ?? label })
    : children;
  return (
    <div className={className}>
      <div className="label flex items-center gap-1.5">
        <span>{label}</span>
        {info && <InfoTip label={label}>{info}</InfoTip>}
      </div>
      {control}
      {hint && <p className="mt-1 text-[11px] text-slate-400">{hint}</p>}
    </div>
  );
}

export function ConfirmButton({
  label,
  confirmLabel = 'Confirm',
  onConfirm,
  className,
  icon = 'trash',
  busy = false
}: {
  label: string;
  confirmLabel?: string;
  onConfirm: () => void | Promise<void>;
  className?: string;
  icon?: IconName;
  busy?: boolean;
}) {
  const [armed, setArmed] = useState(false);
  const timer = useRef<number | null>(null);

  useEffect(() => {
    if (!armed) return undefined;
    timer.current = window.setTimeout(() => setArmed(false), 3500);
    return () => {
      if (timer.current) window.clearTimeout(timer.current);
    };
  }, [armed]);

  if (armed) {
    return (
      <button
        type="button"
        className={classNames('btn-danger px-2 py-1 text-xs', className)}
        disabled={busy}
        onClick={() => {
          setArmed(false);
          void onConfirm();
        }}
      >
        <Icon name="alertTriangle" className="h-3.5 w-3.5" />
        {confirmLabel}
      </button>
    );
  }
  return (
    <button type="button" className={classNames('btn-ghost px-2 py-1 text-xs', className)} onClick={() => setArmed(true)}>
      <Icon name={icon} className="h-3.5 w-3.5" />
      {label}
    </button>
  );
}

export function Modal({ title, children, onClose, wide = false }: { title: string; children: ReactNode; onClose: () => void; wide?: boolean }) {
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="fixed inset-0 z-[60] flex items-end justify-center bg-black/60 p-0 sm:items-center sm:p-4" onClick={onClose}>
      <div
        className={classNames('card max-h-[92vh] w-full overflow-y-auto rounded-b-none p-5 sm:rounded-xl', wide ? 'sm:max-w-2xl' : 'sm:max-w-lg')}
        onClick={(event) => event.stopPropagation()}
      >
        <div className="mb-4 flex items-center gap-2">
          <h2 className="text-sm font-semibold">{title}</h2>
          <button type="button" className="btn-ghost ml-auto px-2 py-1 text-xs" onClick={onClose} aria-label="Close dialog">
            <Icon name="x" className="h-4 w-4" />
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}

export function Callout({ tone = 'info', icon, title, children }: { tone?: 'info' | 'warn' | 'ok' | 'bad'; icon?: IconName; title?: ReactNode; children: ReactNode }) {
  const tones = {
    info: 'border-accent-soft/40 bg-accent-muted/20 text-slate-200',
    warn: 'border-amber-800/60 bg-amber-950/30 text-amber-100',
    ok: 'border-emerald-800/60 bg-emerald-950/30 text-emerald-100',
    bad: 'border-red-900/60 bg-red-950/30 text-red-100'
  } as const;
  const icons: Record<string, IconName> = { info: 'info', warn: 'alertTriangle', ok: 'circleCheck', bad: 'circleAlert' };
  return (
    <div className={classNames('flex gap-2.5 rounded-xl border p-3 text-xs leading-relaxed', tones[tone])}>
      <Icon name={icon ?? icons[tone]} className="mt-0.5 h-4 w-4 shrink-0" />
      <div className="min-w-0">
        {title && <div className="font-semibold">{title}</div>}
        <div className={title ? 'mt-0.5' : ''}>{children}</div>
      </div>
    </div>
  );
}

export function Skeleton({ className }: { className?: string }) {
  return <div className={classNames('animate-pulseSoft rounded-lg bg-ink-800/70', className)} />;
}

// ────────────────────────────────────────────────────────────────────────────
// Formatting helpers shared across views
// ────────────────────────────────────────────────────────────────────────────

/** Thousands-separated integer, e.g. 12345 -> "12,345". */
export function formatNumber(value: number | null | undefined, digits = 0): string {
  const n = Number(value ?? 0);
  if (!Number.isFinite(n)) return '0';
  return n.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits });
}

/** One-decimal percentage, e.g. 12.34 -> "12.3%". */
export function formatPercent(value: number | null | undefined, digits = 1): string {
  const n = Number(value ?? 0);
  if (!Number.isFinite(n)) return '0%';
  return `${n.toFixed(digits)}%`;
}

/** Seconds-based rate, e.g. 1536 -> "1.5 KB/s". */
export function formatRate(bytesPerSecond: number | null | undefined): string {
  return `${formatBytes(bytesPerSecond)}/s`;
}

/** Human-friendly duration in seconds, e.g. 3720 -> "1h 2m". */
export function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ${m % 60}m`;
  const d = Math.floor(h / 24);
  return `${d}d ${h % 24}h`;
}

/** A human label for a refresh interval expressed in seconds (0 = "Off"). */
export function intervalLabel(seconds: number): string {
  if (seconds <= 0) return 'Off';
  if (seconds < 60) return `${seconds}s`;
  const m = seconds / 60;
  if (m < 60) return `${m}m`;
  const h = m / 60;
  return h < 24 ? `${h}h` : `${h / 24}d`;
}

// ────────────────────────────────────────────────────────────────────────────
// Persisted UI preference (localStorage) so display settings survive reloads
// ────────────────────────────────────────────────────────────────────────────
export function usePersistentState<T>(key: string, initial: T): [T, (value: T) => void] {
  const [value, setValue] = useState<T>(() => {
    try {
      const raw = window.localStorage.getItem(key);
      return raw === null ? initial : (JSON.parse(raw) as T);
    } catch {
      return initial;
    }
  });
  const update = (next: T) => {
    setValue(next);
    try {
      window.localStorage.setItem(key, JSON.stringify(next));
    } catch {
      /* private mode: keep the in-memory value only */
    }
  };
  return [value, update];
}

// ────────────────────────────────────────────────────────────────────────────
// Refresh interval control — a compact dropdown (Off / 1s / 5s / 10s / 30s / 1m …)
// ────────────────────────────────────────────────────────────────────────────
export const REFRESH_PRESETS = [0, 1, 5, 10, 30, 60, 300, 600, 1800] as const;

export function RefreshControl({
  value,
  onChange,
  label = 'Auto-refresh',
  presets = REFRESH_PRESETS
}: {
  value: number;
  onChange: (seconds: number) => void;
  label?: string;
  presets?: readonly number[];
}) {
  return (
    <label className="flex items-center gap-1.5 text-xs text-slate-400">
      <Icon name="refreshCw" className="h-3.5 w-3.5" />
      <span className="hidden sm:inline">{label}</span>
      <select
        className="input w-20 py-1 text-xs"
        value={value}
        aria-label={label}
        onChange={(event) => onChange(Number(event.target.value))}
      >
        {presets.map((seconds) => (
          <option key={seconds} value={seconds}>{intervalLabel(seconds)}</option>
        ))}
      </select>
    </label>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// Disclosure — always-visible summary line with details hidden until expanded.
// Used for operational info everywhere so panels stay calm but remain deep.
// ────────────────────────────────────────────────────────────────────────────
export function Expand({
  title,
  hint,
  icon = 'chevronRight',
  defaultOpen = false,
  children,
  className
}: {
  title: ReactNode;
  hint?: ReactNode;
  icon?: IconName;
  defaultOpen?: boolean;
  children: ReactNode;
  className?: string;
}) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div className={classNames('rounded-xl border border-ink-800 bg-ink-900/40', className)}>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-slate-300 hover:bg-ink-800/50"
      >
        <Icon name={open ? 'chevronDown' : icon} className="h-4 w-4 shrink-0 text-slate-400" />
        <span className="min-w-0 flex-1 truncate">{title}</span>
        {hint && <span className="shrink-0 text-[11px] text-slate-400">{hint}</span>}
      </button>
      {open && <div className="border-t border-ink-800 px-3 py-2">{children}</div>}
    </div>
  );
}

/** Label/value row for dense operational tables. */
export function MetricRow({ label, value, hint }: { label: ReactNode; value: ReactNode; hint?: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-3 border-b border-ink-800/50 py-1 text-xs last:border-b-0">
      <span className="text-slate-400">{label}</span>
      <span className="text-right tabular-nums text-slate-200">
        {value}
        {hint && <span className="ml-1 text-slate-400">{hint}</span>}
      </span>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────
// TrafficChart — a dual series (In / Out) line chart for network history.
// ────────────────────────────────────────────────────────────────────────────
export function TrafficChart({
  inData,
  outData,
  height = 120,
  inLabel = 'In',
  outLabel = 'Out'
}: {
  inData: number[];
  outData: number[];
  height?: number;
  inLabel?: string;
  outLabel?: string;
}) {
  const length = Math.max(inData.length, outData.length);
  if (length < 2) {
    return (
      <div style={{ height }} className="flex items-center justify-center text-[11px] text-slate-400">
        collecting samples…
      </div>
    );
  }
  const max = Math.max(...inData, ...outData, 1);
  const width = 600;
  const toPoints = (data: number[]) =>
    data
      .map((value, index) => {
        const x = (index / (data.length - 1)) * width;
        const y = height - (value / max) * (height - 6) - 3;
        return `${x.toFixed(1)},${y.toFixed(1)}`;
      })
      .join(' ');
  const area = (data: number[]) => `${toPoints(data)} ${width},${height} 0,${height}`;
  return (
    <div>
      <svg viewBox={`0 0 ${width} ${height}`} className="w-full" style={{ height }} preserveAspectRatio="none">
        <polygon points={area(inData)} fill="rgba(56,189,248,0.12)" />
        <polygon points={area(outData)} fill="rgba(52,211,153,0.12)" />
        <polyline points={toPoints(inData)} fill="none" stroke="#38bdf8" strokeWidth="1.8" vectorEffect="non-scaling-stroke" />
        <polyline points={toPoints(outData)} fill="none" stroke="#34d399" strokeWidth="1.8" vectorEffect="non-scaling-stroke" />
      </svg>
      <div className="mt-1 flex items-center justify-end gap-3 text-[10px] text-slate-400">
        <span className="flex items-center gap-1"><span className="inline-block h-2 w-2 rounded-sm bg-sky-400" /> {inLabel}</span>
        <span className="flex items-center gap-1"><span className="inline-block h-2 w-2 rounded-sm bg-emerald-400" /> {outLabel}</span>
        <span className="ml-auto">peak {formatBytes(max)}/s</span>
      </div>
    </div>
  );
}


// ────────────────────────────────────────────────────────────────────────────
// YamlEditor — a compose-aware textarea
//   * Ctrl/⌘+Space or typing opens schema-driven key/value suggestions (keyboard:
//     ↑/↓ to move, Enter/Tab to apply, Esc to dismiss),
//   * Enter keeps indentation and continues lists with "- ",
//   * Tab / Shift+Tab indent or outdent (selection aware),
//   * issues from the server validator and the local enum lint are listed below and
//     click to jump to the offending line.
// ────────────────────────────────────────────────────────────────────────────
export function YamlEditor({
  value,
  onChange,
  serverIssues = [],
  validate,
  className,
  ariaLabel = 'Compose YAML',
  placeholder
}: {
  value: string;
  onChange: (value: string) => void;
  serverIssues?: YamlIssue[];
  validate?: (value: string) => void;
  className?: string;
  ariaLabel?: string;
  placeholder?: string;
}) {
  const ref = useRef<HTMLTextAreaElement | null>(null);
  const [caret, setCaret] = useState(0);
  const [open, setOpen] = useState(false);
  const [forced, setForced] = useState(false);
  const [selected, setSelected] = useState(0);

  const suggestion = useMemo(() => suggest(value, caret), [value, caret]);
  const issues = useMemo(() => {
    const seen = new Set<string>();
    const all: YamlIssue[] = [];
    for (const issue of [...serverIssues, ...lintEnums(value)]) {
      const key = `${issue.line}:${issue.severity}:${issue.message}`;
      if (seen.has(key)) continue;
      seen.add(key);
      all.push(issue);
    }
    return all.sort((a, b) => a.line - b.line || a.column - b.column);
  }, [serverIssues, value]);
  const errors = issues.filter((issue) => issue.severity === 'error').length;
  const items = suggestion.items;
  const show = open && items.length > 0;

  // Debounced server validation; the local lint already gives instant feedback.
  useEffect(() => {
    if (!validate) return undefined;
    const timer = window.setTimeout(() => validate(value), 700);
    return () => window.clearTimeout(timer);
  }, [value, validate]);

  const syncCaret = () => {
    const el = ref.current;
    if (el) setCaret(el.selectionStart ?? 0);
  };

  const placeCaret = (position: number) => {
    requestAnimationFrame(() => {
      const el = ref.current;
      if (!el) return;
      el.focus();
      el.setSelectionRange(position, position);
      setCaret(position);
    });
  };

  const apply = (completion: Completion) => {
    const el = ref.current;
    const start = suggestion.replaceFrom;
    const end = el?.selectionStart ?? start + suggestion.prefix.length;
    const next = value.slice(0, start) + completion.insertText + value.slice(end);
    onChange(next);
    setOpen(false);
    setForced(false);
    setSelected(0);
    placeCaret(start + completion.insertText.length);
  };

  const onKeyDown = (event: React.KeyboardEvent<HTMLTextAreaElement>) => {
    const el = event.currentTarget;
    const start = el.selectionStart ?? 0;
    const end = el.selectionEnd ?? 0;

    if (event.key === ' ' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      setForced(true);
      setOpen(true);
      setSelected(0);
      return;
    }

    if (show) {
      if (event.key === 'ArrowDown') {
        event.preventDefault();
        setSelected((value0) => Math.min(value0 + 1, items.length - 1));
        return;
      }
      if (event.key === 'ArrowUp') {
        event.preventDefault();
        setSelected((value0) => Math.max(value0 - 1, 0));
        return;
      }
      if (event.key === 'Escape') {
        event.preventDefault();
        setOpen(false);
        return;
      }
      if (event.key === 'Enter' || event.key === 'Tab') {
        event.preventDefault();
        apply(items[selected] ?? items[0]);
        return;
      }
    }

    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      const { insert, caret: caretOffset } = enterInsertion(value, start);
      const next = value.slice(0, start) + insert + value.slice(end);
      onChange(next);
      setOpen(false);
      placeCaret(start + caretOffset);
      return;
    }

    if (event.key === 'Tab') {
      event.preventDefault();
      const result = indentSelection(value, start, end, event.shiftKey);
      onChange(result.text);
      requestAnimationFrame(() => {
        el.setSelectionRange(result.start, result.end);
        setCaret(result.start);
      });
      return;
    }

    if (event.key === 'Escape') setOpen(false);
  };

  const onTextChange = (next: string) => {
    onChange(next);
    const el = ref.current;
    const position = el?.selectionStart ?? next.length;
    setCaret(position);
    const next0 = suggest(next, position);
    setOpen((forced || next0.prefix.length >= 1) && next0.items.length > 0);
    setSelected(0);
  };

  return (
    <div className={classNames('relative', className)}>
      <textarea
        ref={ref}
        value={value}
        onChange={(event) => onTextChange(event.target.value)}
        onKeyDown={onKeyDown}
        onClick={syncCaret}
        onKeyUp={(event) => {
          if (!['ArrowUp', 'ArrowDown', 'Enter', 'Tab', 'Escape'].includes(event.key)) syncCaret();
        }}
        onBlur={() => window.setTimeout(() => setOpen(false), 150)}
        spellCheck={false}
        aria-label={ariaLabel}
        placeholder={placeholder}
        style={{ tabSize: INDENT }}
        className="input h-full w-full resize-none font-mono text-xs leading-5"
      />

      {show && (
        <div className="absolute inset-x-2 bottom-2 z-20 max-h-44 overflow-y-auto rounded-lg border border-ink-700 bg-ink-850/95 p-1 shadow-2xl backdrop-blur">
          <div className="flex items-center gap-2 px-2 py-1 text-[10px] uppercase tracking-wide text-slate-400">
            <Icon name="sparkles" className="h-3 w-3" />
            <span className="truncate">{suggestion.context || 'document'}</span>
            <span className="ml-auto shrink-0 normal-case">↑↓ move · Enter apply · Esc close</span>
          </div>
          {items.map((item, index) => (
            <button
              key={item.label}
              type="button"
              className={classNames(
                'flex w-full items-baseline gap-2 rounded px-2 py-1 text-left text-xs',
                index === selected ? 'bg-accent-muted/60 text-white' : 'text-slate-300 hover:bg-ink-800'
              )}
              onMouseDown={(event) => {
                event.preventDefault();
                apply(item);
              }}
            >
              <span className="font-mono">{item.label}</span>
              {item.detail && <span className="min-w-0 flex-1 truncate text-[10px] text-slate-400">{item.detail}</span>}
            </button>
          ))}
        </div>
      )}

      <div className="mt-1 flex flex-wrap items-center gap-2 text-[11px] text-slate-400">
        <span>
          <kbd className="rounded border border-ink-600 px-1 text-[10px]">Ctrl/⌘ Space</kbd> suggestions
        </span>
        <span><kbd className="rounded border border-ink-600 px-1 text-[10px]">Tab</kbd> indent</span>
        <span>Enter keeps indentation and continues lists</span>
        {errors > 0 && <span className="text-red-300">{errors} error{errors === 1 ? '' : 's'}</span>}
        {errors === 0 && issues.length > 0 && <span className="text-amber-300">{issues.length} warning{issues.length === 1 ? '' : 's'}</span>}
        {errors === 0 && issues.length === 0 && <span className="text-emerald-300">schema ok</span>}
      </div>

      {issues.length > 0 && (
        <div className="mt-1 max-h-28 space-y-0.5 overflow-y-auto rounded-lg border border-ink-800 bg-ink-900/50 p-2">
          {issues.map((issue, index) => (
            <button
              key={`${issue.line}-${index}`}
              type="button"
              className="flex w-full items-start gap-2 rounded px-1 py-0.5 text-left text-[11px] hover:bg-ink-800/60"
              onClick={() => {
                const el = ref.current;
                if (!el) return;
                const lines = value.split('\n');
                let offset = 0;
                for (let i = 0; i < issue.line - 1 && i < lines.length; i += 1) offset += lines[i].length + 1;
                const lineEnd = offset + (lines[issue.line - 1]?.length ?? 0);
                el.focus();
                el.setSelectionRange(offset, lineEnd);
                const ratio = issue.line / Math.max(1, lines.length);
                el.scrollTop = Math.max(0, ratio * el.scrollHeight - el.clientHeight / 2);
              }}
            >
              <Icon
                name={issue.severity === 'error' ? 'circleAlert' : 'alertTriangle'}
                className={classNames('mt-0.5 h-3.5 w-3.5 shrink-0', issue.severity === 'error' ? 'text-red-300' : 'text-amber-300')}
              />
              <span className="min-w-0 flex-1 text-slate-300">
                <span className="font-mono text-slate-400">L{issue.line}:{issue.column}</span> {issue.message}
                {issue.path && <span className="ml-1 font-mono text-slate-500">{issue.path}</span>}
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
