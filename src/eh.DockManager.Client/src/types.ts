// File: src/eh.DockManager.Client/src/types.ts

export type Role = 'Pending' | 'Viewer' | 'Admin';

export interface SessionUser {
  authenticated: boolean;
  id?: number;
  username?: string;
  email?: string;
  role?: Role;
  provider?: string;
  amr?: string;
}

export interface PortalApp {
  id: number;
  subdomain: string;
  name: string;
  icon?: string | null;
  category: string;
  auth: 'Public' | 'Sso' | 'Admin';
  autoscale: 'None' | 'Idle15m' | 'Idle1h';
  targetPort?: number | null;
  containerName?: string | null;
  externalUrl?: string | null;
  sortOrder: number;
  hidden: boolean;
  manual: boolean;
  autoDiscovered: boolean;
  url?: string;
}

export interface LandingResponse {
  domain: string;
  authenticated: boolean;
  apps: PortalApp[];
}

export interface StackSummary {
  name: string;
  state: string;
  autoUpdateEnabled: boolean;
  autoUpdateIntervalMinutes: number;
  pruneOldImages: boolean;
  rollbackOnFailure: boolean;
  lastDeployedAt?: string | null;
  lastUpdatedAt?: string | null;
  updateCount: number;
  rollbackCount: number;
  lastError?: string | null;
}

export interface DockerContainerSummary {
  Id: string;
  Names: string[];
  Image: string;
  ImageID: string;
  State: string;
  Status: string;
  Labels?: Record<string, string>;
  Ports?: { PrivatePort?: number; PublicPort?: number; Type?: string; IP?: string }[];
}

export interface RegistryInfo {
  id: number;
  name: string;
  url: string;
  username?: string | null;
  ignoreSslErrors: boolean;
  isDefault: boolean;
  hasPassword: boolean;
}

export interface DockerHostInfo {
  id: number;
  name: string;
  kind: string;
  endpoint: string;
  enabled: boolean;
  wakeOnLanMac?: string | null;
  lastSeenAt?: string | null;
  lastError?: string | null;
}

export interface KubeClusterInfo {
  id: number;
  name: string;
  enabled: boolean;
  serverUrl: string;
  context?: string | null;
  lastSeenAt?: string | null;
  lastError?: string | null;
}

export interface WebhookInfo {
  id: number;
  name: string;
  kind: string;
  url: string;
  chatId?: string | null;
  enabled: boolean;
  events: string;
  hasSecret: boolean;
  lastFiredAt?: string | null;
  lastStatus?: string | null;
}

export interface UserInfo {
  id: number;
  username: string;
  email: string;
  role: Role;
  provider: string;
  approved: boolean;
  disabled: boolean;
  totpEnabled: boolean;
  createdAt: string;
  lastLoginAt?: string | null;
}

export interface McpKeyInfo {
  id: number;
  name: string;
  prefix: string;
  scopes: string;
  enabled: boolean;
  createdAt: string;
  lastUsedAt?: string | null;
}

export interface AuditLogEntry {
  id: number;
  actor: string;
  actorDetail?: string | null;
  action: string;
  target?: string | null;
  detail?: string | null;
  success: boolean;
  timestamp: string;
}

export interface PasskeyInfo {
  id: number;
  name: string;
  createdAt: string;
  lastUsedAt?: string | null;
  backupEligible: boolean;
}

export interface MetricSample {
  containerId: string;
  cpuPercent: number;
  memoryMb: number;
  memoryLimitMb: number;
  networkRxBytes: number;
  networkTxBytes: number;
  timestamp: string;
}

export interface TarEntryInfo {
  name: string;
  isDirectory: boolean;
  size: number;
  modified: string;
  mode: string;
}

export interface CveFinding {
  id: string;
  package: string;
  version: string;
  summary?: string | null;
  severity?: string | null;
}

export interface ConvertResult {
  yaml: string;
  name?: string;
}

export interface SystemInfo {
  version: string;
  portalDomain: string;
  dataDirectory: string;
  uptimeSeconds: number;
  routes: number;
  dockerHosts: number;
  kubeClusters: number;
  samlSpEnabled: boolean;
  samlIdpEnabled: boolean;
  authentication: boolean;
}

export type ViewKey =
  | 'portal'
  | 'dashboard'
  | 'stacks'
  | 'containers'
  | 'kubernetes'
  | 'images'
  | 'hosts'
  | 'security';
