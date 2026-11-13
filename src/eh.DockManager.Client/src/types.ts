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
  Created?: number;
  SizeRw?: number;
  SizeRootFs?: number;
  Labels?: Record<string, string>;
  Ports?: { PrivatePort?: number; PublicPort?: number; Type?: string; IP?: string }[];
}

export interface DockerImageSummary {
  Id: string;
  RepoTags?: string[] | null;
  RepoDigests?: string[] | null;
  Created?: number;
  Size: number;
  SharedSize?: number;
  VirtualSize?: number;
  Containers: number;
  ContainersNames?: string[];
  Labels?: Record<string, string> | null;
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
  wakeOnLanBroadcast?: string | null;
  wakeOnLanPort?: number | null;
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
  appVersion?: string;
  bootId?: string;
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

export interface HealthInfo {
  status: string;
  time: string;
  appVersion: string;
  bootId: string;
}

export interface CertificateInfo {
  subject: string;
  thumbprint: string;
  notAfter: string;
  dnsNames: string[];
  ipAddresses: string[];
}

export interface SystemSettings {
  portalDomain: string;
  portalDomainSource: string;
  portalDomainFromEnv: string;
  lanIp: string;
  dataDirectory: string;
  version: string;
  certificate: CertificateInfo;
}

export interface AcmeConfig {
  enabled: boolean;
  email: string;
  directoryUrl: string;
  domain: string;
  useStaging: boolean;
  lastError?: string | null;
  lastIssuedAt?: string | null;
}

export interface AcmeSettings {
  config: AcmeConfig;
  directory: string;
  active: boolean;
  certificate: { subject: string; notAfter: string; dnsNames: string[]; ipAddresses: string[] };
}

export interface DashboardContainerInfo {
  id: string;
  name: string;
  image: string;
  state: string;
  status: string;
  cpuPercent: number;
  cpuCores: number;
  cpuThrottledPeriods: number;
  cpuThrottledMs: number;
  memoryMb: number;
  memoryLimitMb: number;
  memoryPeakMb: number;
  memoryFailCount: number;
  pidsCurrent: number;
  pidsLimit: number;
  blockReadBytes: number;
  blockWriteBytes: number;
  networkRxBytes: number;
  networkTxBytes: number;
  networkRxPackets: number;
  networkTxPackets: number;
  networkRxErrors: number;
  networkTxErrors: number;
  networkRxDropped: number;
  networkTxDropped: number;
  sizeRwBytes: number;
  sizeRootFsBytes: number;
  ports: string[];
  created: string;
}

export interface DashboardOverall {
  containersTotal: number;
  containersRunning: number;
  containersPaused: number;
  containersStopped: number;
  images: number;
  cpuPercent: number;
  cpuCores: number;
  memoryUsedMb: number;
  memoryTotalMb: number;
  networkRxBytes: number;
  networkTxBytes: number;
  publishedPorts: number;
  diskImagesBytes: number;
  diskContainersBytes: number;
  diskVolumesBytes: number;
  diskBuildCacheBytes: number;
  diskTotalBytes: number;
}

export interface NetworkInterfaceMetric {
  name: string;
  rxBytes: number;
  txBytes: number;
  rxPackets: number;
  txPackets: number;
  rxErrors: number;
  txErrors: number;
  rxDropped: number;
  txDropped: number;
}

export interface NetworkHistoryPoint {
  timestamp: string;
  rxBytes: number;
  txBytes: number;
  rxBytesPerSec: number;
  txBytesPerSec: number;
  rxErrors: number;
  txErrors: number;
  rxDropped: number;
  txDropped: number;
}

export interface HostTcpCounters {
  currEstab: number;
  activeOpens: number;
  passiveOpens: number;
  attemptFails: number;
  estabResets: number;
  retransSegs: number;
  inErrs: number;
  outRsts: number;
}

export interface NetworkSummary {
  rxLast1hBytes: number;
  txLast1hBytes: number;
  rxLast24hBytes: number;
  txLast24hBytes: number;
  trackingMinutes: number;
  trackingSince?: string | null;
  rxPackets: number;
  txPackets: number;
  rxErrors: number;
  txErrors: number;
  rxDropped: number;
  txDropped: number;
  hostTcp?: HostTcpCounters | null;
  hostInterfaces: NetworkInterfaceMetric[];
  history: NetworkHistoryPoint[];
}

export interface ContainerNetworkHistory {
  containerId: string;
  rxLast1hBytes: number;
  txLast1hBytes: number;
  rxLast24hBytes: number;
  txLast24hBytes: number;
  trackingMinutes: number;
  trackingSince?: string | null;
  history: NetworkHistoryPoint[];
}

export interface ExtendedContainerMetric {
  containerId: string;
  cpuPercent: number;
  cpuCores: number;
  cpuThrottledPeriods: number;
  cpuThrottledMs: number;
  memoryMb: number;
  memoryLimitMb: number;
  memoryPeakMb: number;
  memoryFailCount: number;
  pidsCurrent: number;
  pidsLimit: number;
  blockReadBytes: number;
  blockWriteBytes: number;
  networkRxBytes: number;
  networkTxBytes: number;
  networkRxPackets: number;
  networkTxPackets: number;
  networkRxErrors: number;
  networkTxErrors: number;
  networkRxDropped: number;
  networkTxDropped: number;
  interfaces: NetworkInterfaceMetric[];
}

export interface ContainerMetricsResponse {
  stats?: ExtendedContainerMetric | null;
  network: ContainerNetworkHistory;
}

export interface StackServiceStats {
  name: string;
  container: string;
  image?: string | null;
  state: string;
  status: string;
  health: string;
  startedAt?: string | null;
  restartCount: number;
  cpuPercent: number;
  cpuCores: number;
  memoryMb: number;
  memoryLimitMb: number;
  memoryPeakMb: number;
  pidsCurrent: number;
  sizeRwBytes: number;
  networkRxBytes: number;
  networkTxBytes: number;
  networkRxErrors: number;
  networkTxErrors: number;
  networkRxDropped: number;
  networkTxDropped: number;
  ports: string[];
}

export interface StackStats {
  name: string;
  state: string;
  services: StackServiceStats[];
  totals: {
    servicesTotal: number;
    servicesRunning: number;
    cpuPercent: number;
    memoryMb: number;
    memoryLimitMb: number;
    networkRxBytes: number;
    networkTxBytes: number;
    sizeRwBytes: number;
    restartCount: number;
  };
  autoUpdate: {
    enabled: boolean;
    intervalMinutes: number;
    pruneOldImages: boolean;
    rollbackOnFailure: boolean;
    lastDeployedAt?: string | null;
    lastUpdatedAt?: string | null;
    lastUpdateCheckAt?: string | null;
    updateCount: number;
    rollbackCount: number;
    lastError?: string | null;
  };
}

export interface DashboardHostPanel {
  id?: number | null;
  name: string;
  kind: string;
  enabled: boolean;
  online: boolean;
  containersTotal: number;
  containersRunning: number;
  images: number;
  cpuPercent: number;
  cpuCores: number;
  memoryUsedMb: number;
  memoryTotalMb: number;
  diskUsedBytes: number;
  error?: string | null;
}

export interface DashboardResponse {
  generatedAt: string;
  hostId?: number | null;
  hostName: string;
  overall: DashboardOverall;
  containers: DashboardContainerInfo[];
  hosts: DashboardHostPanel[];
  network?: NetworkSummary;
}

export type ViewKey =
  | 'portal'
  | 'dashboard'
  | 'stacks'
  | 'containers'
  | 'kubernetes'
  | 'images'
  | 'hosts'
  | 'users'
  | 'security'
  | 'settings';
