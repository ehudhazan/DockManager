// File: src/eh.DockManager.Client/src/api.ts
import type {
  AcmeConfig,
  AcmeSettings,
  AuditLogEntry,
  CertificateInfo,
  ContainerMetricsResponse,
  ContainerNetworkHistory,
  ConvertResult,
  CveFinding,
  DashboardResponse,
  DockerContainerSummary,
  DockerHostInfo,
  DockerImageSummary,
  HealthInfo,
  KubeClusterInfo,
  LandingResponse,
  McpKeyInfo,
  NetworkSummary,
  PasskeyInfo,
  PortalApp,
  PortalAppInput,
  PortalCandidatesResponse,
  RegistryInfo,
  SessionUser,
  StackStats,
  StackSummary,
  SystemInfo,
  SystemSettings,
  TarEntryInfo,
  UserInfo,
  WebhookInfo
} from './types';
import type { YamlIssue } from './yaml';

const jsonHeaders = { 'Content-Type': 'application/json' };

async function req<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(path, { credentials: 'same-origin', ...init });
  const text = await response.text();
  const body = text.length > 0 ? safeParse(text) : undefined;
  if (!response.ok) {
    const message = (body && (body.error || body.title)) || `${response.status} ${response.statusText}`;
    throw new Error(typeof message === 'string' ? message : JSON.stringify(message));
  }
  return body as T;
}

/** Raw-text request for endpoints that return YAML/plain text rather than JSON. */
async function reqText(path: string, init: RequestInit = {}): Promise<string> {
  const response = await fetch(path, { credentials: 'same-origin', ...init });
  const body = await response.text();
  if (!response.ok) throw new Error(body || `${response.status} ${response.statusText}`);
  return body;
}

function safeParse(text: string): Record<string, unknown> {
  try {
    return JSON.parse(text);
  } catch {
    return { raw: text };
  }
}

const get = <T>(path: string) => req<T>(path);
const post = <T>(path: string, body?: unknown) =>
  req<T>(path, { method: 'POST', headers: jsonHeaders, body: body === undefined ? undefined : JSON.stringify(body) });
const put = <T>(path: string, body?: unknown) =>
  req<T>(path, { method: 'PUT', headers: jsonHeaders, body: body === undefined ? undefined : JSON.stringify(body) });
const del = <T>(path: string) => req<T>(path, { method: 'DELETE' });

export const api = {
  // ── Auth ────────────────────────────────────────────────────────────────
  me: () => get<SessionUser>('/api/auth/me'),
  login: (username: string, password: string, totpCode?: string) =>
    post<{ authenticated: boolean; requiresTotp?: boolean; role?: string }>('/api/auth/login', {
      username,
      password,
      totpCode
    }),
  logout: () => post<{ ok: boolean }>('/api/auth/logout'),
  totpSetup: () => post<{ secret: string; otpauthUri: string; qrSvg: string }>('/api/auth/totp/setup'),
  totpEnable: (code: string) => post<{ enabled: boolean }>('/api/auth/totp/enable', { code }),
  totpDisable: () => post<{ enabled: boolean }>('/api/auth/totp/disable'),
  passkeys: () => get<PasskeyInfo[]>('/api/auth/passkey/list'),
  passkeyRegisterBegin: () => post<{ flowId: string; options: unknown }>('/api/auth/passkey/register/begin'),
  passkeyRegisterComplete: (flowId: string, credential: string, name: string) =>
    post<{ ok: boolean }>('/api/auth/passkey/register/complete', { flowId, credential, name }),
  passkeyLoginBegin: (username?: string) =>
    post<{ flowId: string; options: unknown }>('/api/auth/passkey/login/begin', { username }),
  passkeyLoginComplete: (flowId: string, credential: string) =>
    post<{ authenticated: boolean }>('/api/auth/passkey/login/complete', { flowId, credential }),
  deletePasskey: (id: number) => del<{ ok: boolean }>(`/api/auth/passkey/${id}`),
  oidcProviders: () => get<{ google: boolean; microsoft: boolean }>('/api/auth/oidc/providers'),

  // ── System ──────────────────────────────────────────────────────────────
  systemInfo: () => get<SystemInfo>('/api/system/info'),
  health: () => get<HealthInfo>('/api/system/health'),
  network: (hostId?: number) => get<NetworkSummary>(`/api/system/network${hostId ? `?hostId=${hostId}` : ''}`),
  dashboard: (hostId?: number, fresh = false) => {
    const params = new URLSearchParams();
    if (hostId) params.set('hostId', String(hostId));
    if (fresh) params.set('fresh', 'true');
    const query = params.toString();
    return get<DashboardResponse>(`/api/system/dashboard${query ? `?${query}` : ''}`);
  },
  audit: (limit = 200) => get<AuditLogEntry[]>(`/api/system/audit?limit=${limit}`),
  sslRegenerate: (dnsNames: string[], ipAddresses: string[]) =>
    post<{ ok: boolean; subject: string; thumbprint: string; expires: string }>('/api/system/ssl/regenerate', {
      dnsNames,
      ipAddresses
    }),
  sslDownloadUrl: '/api/system/ssl/download',
  systemSettings: () => get<SystemSettings>('/api/system/settings'),
  saveSystemSettings: (payload: { portalDomain?: string; lanIp?: string; regenerateCertificate?: boolean }) =>
    put<{ ok: boolean; portalDomain: string; certificate: CertificateInfo }>('/api/system/settings', payload),

  // ── Let's Encrypt (ACME) ────────────────────────────────────────────────
  acmeSettings: () => get<AcmeSettings>('/api/system/acme'),
  saveAcme: (payload: Partial<AcmeConfig>) =>
    put<{ saved: boolean; directory: string }>('/api/system/acme', payload),
  issueAcme: () =>
    post<{ ok: boolean; domains: string[]; notAfter?: string | null; error?: string | null }>('/api/system/acme/issue'),

  // ── Portal ──────────────────────────────────────────────────────────────
  landing: (includeHidden = false) =>
    get<LandingResponse>(`/api/portal/landing${includeHidden ? '?includeHidden=true' : ''}`),
  portalApps: () => get<PortalApp[]>('/api/portal/apps'),
  savePortalApp: (app: Partial<PortalApp>) => put<{ id: number; subdomain: string }>('/api/portal/apps', app),
  deletePortalApp: (subdomain: string) => del<{ deleted: boolean }>(`/api/portal/apps/${subdomain}`),
  reorderPortalApps: (order: string[]) => post<{ reordered: number }>('/api/portal/apps/reorder', { order }),
  refreshPortal: () => post<{ refreshed: boolean }>('/api/portal/refresh'),
  portalCandidates: (params: { hostId?: number; clusterId?: number }) =>
    get<PortalCandidatesResponse>(
      `/api/portal/available?${params.hostId !== undefined ? `hostId=${params.hostId}` : `clusterId=${params.clusterId}`}`
    ),
  bulkAddPortalApps: (apps: PortalAppInput[]) => post<{ added: number }>('/api/portal/apps/bulk', { apps }),

  // ── Stacks ──────────────────────────────────────────────────────────────
  stacks: () => get<StackSummary[]>('/api/stacks'),
  stack: (name: string) => get<{ name: string; yaml: string; meta: unknown }>(`/api/stacks/${name}`),
  stackStats: (name: string) => get<StackStats>(`/api/stacks/${name}/stats`),
  validateStack: (yaml: string) =>
    post<{ valid: boolean; name?: string | null; serviceCount: number; issues: YamlIssue[] }>(
      '/api/stacks/validate',
      { yaml }
    ),
  saveStack: (name: string, payload: Record<string, unknown>) => put<{ saved: boolean }>(`/api/stacks/${name}`, payload),
  deleteStack: (name: string) => del<{ deleted: boolean }>(`/api/stacks/${name}`),
  deployStack: (name: string) => post<unknown>(`/api/stacks/${name}/deploy`),
  stackAction: (name: string, action: string) => post<unknown>(`/api/stacks/${name}/action`, { action }),
  setAutoUpdate: (name: string, payload: Record<string, unknown>) =>
    put<{ saved: boolean }>(`/api/stacks/${name}/autoupdate`, payload),
  convertDockerRun: (command: string) => post<ConvertResult>('/api/stacks/convert/docker-run', { source: command }),
  convertToK8s: (payload: { source?: string; name?: string }) =>
    reqText('/api/stacks/convert/compose-to-k8s', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify(payload)
    }),
  importStack: (name: string, url: string) => post<{ imported: boolean }>('/api/stacks/import', { name, url }),
  saveSecret: (name: string, value: string) => post<{ saved: boolean }>('/api/stacks/secrets', { name, value }),

  // ── Containers ──────────────────────────────────────────────────────────
  containers: (hostId?: number) =>
    get<DockerContainerSummary[]>(`/api/containers${hostId ? `?hostId=${hostId}` : ''}`),
  container: (id: string, hostId?: number) =>
    get<Record<string, unknown>>(`/api/containers/${encodeURIComponent(id)}${hostId ? `?hostId=${hostId}` : ''}`),
  containerAction: (id: string, action: string, hostId?: number) =>
    post<{ ok: boolean }>(`/api/containers/${encodeURIComponent(id)}/action${hostId ? `?hostId=${hostId}` : ''}`, {
      action
    }),
  logsUrl: (id: string, tail = 300, timestamps = false) =>
    `/api/containers/${encodeURIComponent(id)}/logs?tail=${tail}&timestamps=${timestamps ? 1 : 0}`,
  statsUrl: (id: string) => `/api/containers/${encodeURIComponent(id)}/stats`,
  containerMetrics: (id: string) => get<ContainerMetricsResponse>(`/api/containers/${encodeURIComponent(id)}/metrics`),
  containerNetwork: (id: string) => get<ContainerNetworkHistory>(`/api/containers/${encodeURIComponent(id)}/network`),
  execUrl: (id: string, cmd: string) =>
    `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/exec/${encodeURIComponent(id)}?cmd=${encodeURIComponent(cmd)}`,
  archiveList: (id: string, path: string) =>
    get<TarEntryInfo[]>(`/api/containers/${encodeURIComponent(id)}/archive?path=${encodeURIComponent(path)}`),
  archiveFileUrl: (id: string, path: string) =>
    `/api/containers/${encodeURIComponent(id)}/archive/file?path=${encodeURIComponent(path)}`,
  exportCompose: (id: string) => post<ConvertResult>(`/api/containers/${encodeURIComponent(id)}/export-compose`),
  cveScan: (id: string) => post<CveFinding[]>(`/api/containers/${encodeURIComponent(id)}/cve-scan`),

  // ── Images & registries ─────────────────────────────────────────────────
  images: (hostId?: number) =>
    get<DockerImageSummary[]>(`/api/images${hostId ? `?hostId=${hostId}` : ''}`),
  pruneImages: (dangling: boolean) =>
    post<{ ImagesDeleted?: unknown[]; SpaceReclaimed?: number }>(`/api/images/prune?dangling=${dangling}`),
  pullUrl: (image: string, tag = 'latest', registryId?: number) =>
    `/api/images/pull?image=${encodeURIComponent(image)}&tag=${encodeURIComponent(tag)}` +
    (registryId ? `&registryId=${registryId}` : ''),
  pullImage: (image: string, tag: string, registryId?: number) =>
    post<unknown>('/api/images/pull', { image, tag, registryId }),
  removeImage: (id: string) => del<{ removed: boolean }>(`/api/images/${encodeURIComponent(id)}`),
  runImage: (payload: {
    image: string;
    name?: string;
    command?: string;
    env?: string[];
    ports?: string[];
    restartPolicy?: string;
    start?: boolean;
  }) => post<{ id: string; name: string }>('/api/containers', payload),
  registries: () => get<RegistryInfo[]>('/api/registries'),
  saveRegistry: (payload: Record<string, unknown>) => post<{ id: number }>('/api/registries', payload),
  deleteRegistry: (id: number) => del<{ deleted: boolean }>(`/api/registries/${id}`),

  // ── Hosts ───────────────────────────────────────────────────────────────
  hosts: () => get<DockerHostInfo[]>('/api/hosts'),
  saveHost: (payload: Record<string, unknown>) => post<{ id: number }>('/api/hosts', payload),
  deleteHost: (id: number) => del<{ deleted: boolean }>(`/api/hosts/${id}`),
  testHost: (id: number) => post<{ online: boolean }>(`/api/hosts/${id}/test`),
  wakeHost: (id: number) => post<{ sent: boolean }>(`/api/hosts/${id}/wake`),
  enrollAgent: (name: string, host?: string, port?: number) =>
    post<{
      clientCertificatePem: string;
      clientKeyPem: string;
      caCertificatePem: string;
      endpoint: string;
      compose: string;
    }>('/api/hosts/agent/enroll', { name, host, port }),

  // ── Kubernetes ──────────────────────────────────────────────────────────
  clusters: () => get<KubeClusterInfo[]>('/api/clusters'),
  saveCluster: (payload: Record<string, unknown>) => post<{ id: number }>('/api/clusters', payload),
  deleteCluster: (id: number) => del<{ deleted: boolean }>(`/api/clusters/${id}`),
  testCluster: (id: number) => post<{ online: boolean }>(`/api/clusters/${id}/test`),
  namespaces: (id: number) => get<{ items?: unknown[] }>(`/api/clusters/${id}/namespaces`),
  pods: (id: number, namespace = '') =>
    get<{ items?: unknown[] }>(`/api/clusters/${id}/pods${namespace ? `?namespace=${namespace}` : ''}`),
  deployments: (id: number, namespace = '') =>
    get<{ items?: unknown[] }>(`/api/clusters/${id}/deployments${namespace ? `?namespace=${namespace}` : ''}`),
  services: (id: number, namespace = '') =>
    get<{ items?: unknown[] }>(`/api/clusters/${id}/services${namespace ? `?namespace=${namespace}` : ''}`),
  httpRoutes: (id: number, namespace = '') =>
    get<{ items?: unknown[] }>(`/api/clusters/${id}/httproutes${namespace ? `?namespace=${namespace}` : ''}`),
  applyManifest: (id: number, yaml: string, namespace = 'default') =>
    post<{ applied: string[] }>(`/api/clusters/${id}/apply`, { yaml, namespace }),
  restartDeployment: (id: number, ns: string, name: string) =>
    post<{ ok: boolean }>(`/api/clusters/${id}/deployments/${ns}/${name}/restart`),
  scaleDeployment: (id: number, ns: string, name: string, replicas: number) =>
    post<{ ok: boolean }>(`/api/clusters/${id}/deployments/${ns}/${name}/scale`, { replicas }),

  // ── Webhooks ────────────────────────────────────────────────────────────
  webhooks: () => get<WebhookInfo[]>('/api/webhooks'),
  saveWebhook: (payload: Record<string, unknown>) => post<{ id: number }>('/api/webhooks', payload),
  deleteWebhook: (id: number) => del<{ deleted: boolean }>(`/api/webhooks/${id}`),
  testWebhook: (id: number) => post<{ lastStatus: string }>(`/api/webhooks/${id}/test`),

  // ── Users & MCP keys ────────────────────────────────────────────────────
  users: () => get<UserInfo[]>('/api/users'),
  pendingUsers: () => get<UserInfo[]>('/api/users/pending'),
  approveUser: (id: number, role: string) => post<{ approved: boolean }>(`/api/users/${id}/approve`, { role }),
  updateUser: (id: number, payload: { role?: string; disabled?: boolean }) =>
    put<{ updated: boolean; role: string; disabled: boolean }>(`/api/users/${id}`, payload),
  createUser: (payload: { username: string; password: string; email?: string; role?: string }) =>
    post<{ id: number; username: string; role: string }>('/api/users', payload),
  deleteUser: (id: number) => del<{ deleted: boolean }>(`/api/users/${id}`),
  mcpKeys: () =>
    get<{ id: number; name: string; prefix: string; scopes: string; enabled: boolean; createdAt: string }[]>(
      '/api/mcp/keys'
    ),
  createMcpKey: (name: string, scopes: string) =>
    post<{ id: number; name: string; token: string }>('/api/mcp/keys', { name, scopes }),
  updateMcpKey: (id: number, payload: { name?: string; enabled?: boolean; scopes?: string }) =>
    put<{ updated: boolean; id: number; name: string; scopes: string; enabled: boolean }>(`/api/mcp/keys/${id}`, payload),
  rotateMcpKey: (id: number) =>
    post<{ id: number; name: string; token: string }>(`/api/mcp/keys/${id}/rotate`),
  deleteMcpKey: (id: number) => del<{ deleted: boolean }>(`/api/mcp/keys/${id}`),

  // ── SSO configuration ───────────────────────────────────────────────────
  oidcProvider: (provider: string) =>
    get<{ provider: string; enabled: boolean; clientId: string; authority?: string; displayName?: string; hasSecret: boolean }>(
      `/api/security/oidc/${provider}`
    ),
  saveOidcProvider: (provider: string, payload: Record<string, unknown>) =>
    put<{ saved: boolean }>(`/api/security/oidc/${provider}`, payload),
  samlSp: () => get<Record<string, unknown>>('/api/security/saml/sp'),
  saveSamlSp: (payload: Record<string, unknown>) => put<{ saved: boolean }>('/api/security/saml/sp', payload),
  samlIdp: () => get<Record<string, unknown>>('/api/security/saml/idp'),
  saveSamlIdp: (payload: Record<string, unknown>) => put<{ saved: boolean }>('/api/security/saml/idp', payload),
  samlMetadataUrl: '/api/auth/saml2/metadata',
  samlIdpMetadataUrl: '/api/saml/idp/metadata',
  ssoLoginUrl: '/api/auth/saml2/login?returnUrl=%2F'
};

export type Api = typeof api;
export { McpKeyInfo };
