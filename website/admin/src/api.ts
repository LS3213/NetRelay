export interface ApiResponse<T> {
  requestId: string;
  data: T;
}

interface ApiErrorResponse {
  requestId?: string;
  error?: {
    code?: string;
    message?: string;
    retryable?: boolean;
    retryAfterSeconds?: number;
    details?: any;
  };
}

export interface LoginChallenge {
  challengeToken: string;
  expiresAt: string;
}

export interface AdminSession {
  username: string;
  csrfToken: string;
  expiresAt: string;
  reauthenticationExpiresAt: string;
}

export interface AdminIdentity {
  username: string;
  expiresAt: string;
  reauthenticationExpiresAt: string;
}

export interface AuditVerification {
  valid: boolean;
  invalidRecordId: number | null;
  verifiedRecords: number;
}

export class ApiClientError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly code: string,
    public readonly requestId?: string,
    public readonly details?: any,
  ) {
    super(message);
  }
}

let csrfToken = "";

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  headers.set("X-NetRelay-Protocol", "1");
  if (init?.body && !(init.body instanceof FormData)) {
    headers.set("Content-Type", "application/json");
  }
  if (csrfToken && init?.method && init.method !== "GET") {
    headers.set("X-NetRelay-Csrf", csrfToken);
  }

  const response = await fetch(`/api/v1${path}`, {
    ...init,
    credentials: "same-origin",
    headers,
  });

  if (!response.ok) {
    let failure: ApiErrorResponse | undefined;
    try {
      failure = (await response.json()) as ApiErrorResponse;
    } catch {
      failure = undefined;
    }
    throw new ApiClientError(
      failure?.error?.message || `请求失败（HTTP ${response.status}）`,
      response.status,
      failure?.error?.code || "HTTP_ERROR",
      failure?.requestId,
      failure?.error?.details,
    );
  }

  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

export async function login(username: string, password: string) {
  return (
    await request<ApiResponse<LoginChallenge>>("/admin/auth/login", {
      method: "POST",
      body: JSON.stringify({ username, password }),
    })
  ).data;
}

export async function completeTotp(challengeToken: string, code: string) {
  const result = (
    await request<ApiResponse<AdminSession>>("/admin/auth/totp", {
      method: "POST",
      body: JSON.stringify({ challengeToken, code }),
    })
  ).data;
  csrfToken = result.csrfToken;
  return result;
}

export async function restoreSession() {
  const identity = (await request<ApiResponse<AdminIdentity>>("/admin/auth/me")).data;
  csrfToken = (
    await request<ApiResponse<{ csrfToken: string }>>("/admin/auth/csrf")
  ).data.csrfToken;
  return identity;
}

export async function rotateCsrf() {
  csrfToken = (
    await request<ApiResponse<{ csrfToken: string }>>("/admin/auth/csrf")
  ).data.csrfToken;
}

export async function reauthenticate(password: string) {
  await request<void>("/admin/auth/reauthenticate", {
    method: "POST",
    body: JSON.stringify({ password }),
  });
}

export async function logout() {
  try {
    await request<void>("/admin/auth/logout", { method: "POST" });
  } finally {
    csrfToken = "";
  }
}

export async function verifyAudit() {
  return (
    await request<ApiResponse<AuditVerification>>("/admin/audit/verify")
  ).data;
}

// === Releases ===

export interface Release {
  id: string;
  version: string;
  channel: string;
  architecture: string;
  minUpgradableVersion: string;
  packageSize: number;
  sha256: string;
  releaseDate: string;
  changelog: string;
  assetPath: string;
  status: "draft" | "published" | "revoked";
  createdAt: string;
  publishedAt?: string;
  revokedAt?: string;
}

export async function getReleases() {
  return (await request<ApiResponse<Release[]>>("/admin/releases")).data;
}

export async function createRelease(
  formData: FormData,
  onProgress?: (loaded: number, total: number) => void,
) {
  return createReleaseWithProgress(formData, onProgress);
}

export function createReleaseWithProgress(
  formData: FormData,
  onProgress?: (loaded: number, total: number) => void,
): Promise<Release> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", "/api/v1/admin/releases");
    xhr.withCredentials = true;
    xhr.setRequestHeader("X-NetRelay-Protocol", "1");
    if (csrfToken) {
      xhr.setRequestHeader("X-NetRelay-Csrf", csrfToken);
    }

    xhr.upload.onprogress = (event) => {
      onProgress?.(event.loaded, event.lengthComputable ? event.total : 0);
    };
    xhr.onerror = () => reject(new ApiClientError("上传连接失败，请检查网络或服务器状态。", 0, "NETWORK_ERROR"));
    xhr.onabort = () => reject(new ApiClientError("上传已取消。", 0, "UPLOAD_ABORTED"));
    xhr.onload = () => {
      let payload: ApiResponse<Release> | ApiErrorResponse | undefined;
      try {
        payload = xhr.responseText ? JSON.parse(xhr.responseText) : undefined;
      } catch {
        payload = undefined;
      }

      if (xhr.status < 200 || xhr.status >= 300) {
        const failure = payload as ApiErrorResponse | undefined;
        reject(new ApiClientError(
          failure?.error?.message || `请求失败（HTTP ${xhr.status}）`,
          xhr.status,
          failure?.error?.code || "HTTP_ERROR",
          failure?.requestId,
          failure?.error?.details,
        ));
        return;
      }

      const response = payload as ApiResponse<Release> | undefined;
      if (!response?.data) {
        reject(new ApiClientError("服务器未返回更新草稿信息。", xhr.status, "INVALID_RESPONSE"));
        return;
      }
      resolve(response.data);
    };
    xhr.send(formData);
  });
}

export async function publishRelease(id: string) {
  return (await request<ApiResponse<unknown>>(`/admin/releases/${id}/publish`, { method: "POST" })).data;
}

export async function revokeRelease(id: string) {
  return (await request<ApiResponse<unknown>>(`/admin/releases/${id}/revoke`, { method: "POST" })).data;
}

// === Feedback ===

export interface Feedback {
  id: string;
  type: "bug" | "suggestion" | "other";
  title: string;
  content: string;
  contact: string | null;
  hasAttachment: boolean;
  attachmentFilename: string | null;
  attachmentSize: number | null;
  status: "pending" | "resolved" | "ignored";
  createdAt: string;
  statusUpdatedAt?: string;
  deviceIdHash?: string;
  installationId?: string;
  clientVersion?: string;
  osVersion?: string;
}

export interface RegisteredDevice {
  deviceIdHash: string;
  installationId: string;
  clientVersion: string;
  osVersion: string;
  firstSeenAt: string;
  lastSeenAt: string;
}

export async function getFeedbacks() {
  return (await request<ApiResponse<Feedback[]>>("/admin/feedback")).data;
}

export async function getRegisteredDevices() {
  return (await request<ApiResponse<RegisteredDevice[]>>("/admin/devices")).data;
}

export async function updateFeedbackStatus(id: string, status: "pending" | "resolved" | "ignored") {
  return (
    await request<ApiResponse<Feedback>>(`/admin/feedback/${id}/status`, {
      method: "POST",
      body: JSON.stringify({ status }),
    })
  ).data;
}

// === Announcements ===

export interface Announcement {
  id: string;
  title: string;
  content: string;
  severity: "normal" | "important" | "critical";
  targetVersionMin: string | null;
  targetVersionMax: string | null;
  displayTrigger: "once_per_device" | "every_startup";
  status: "draft" | "published" | "revoked";
  createdAt: string;
  publishedAt?: string;
  revokedAt?: string;
  expiresAt?: string;
}

export interface AnnouncementCreateRequest {
  title: string;
  content: string;
  severity: "normal" | "important" | "critical";
  targetVersionMin?: string | null;
  targetVersionMax?: string | null;
  displayTrigger: "once_per_device" | "every_startup";
  expiresAt?: string | null;
}

export async function getAnnouncements() {
  return (await request<ApiResponse<Announcement[]>>("/admin/announcements")).data;
}

export async function createAnnouncement(data: AnnouncementCreateRequest) {
  return (
    await request<ApiResponse<Announcement>>("/admin/announcements", {
      method: "POST",
      body: JSON.stringify(data),
    })
  ).data;
}

export async function editAnnouncement(id: string, data: AnnouncementCreateRequest) {
  return (
    await request<ApiResponse<Announcement>>(`/admin/announcements/${id}`, {
      method: "PUT",
      body: JSON.stringify(data),
    })
  ).data;
}

export async function publishAnnouncement(id: string) {
  return (await request<ApiResponse<Announcement>>(`/admin/announcements/${id}/publish`, { method: "POST" })).data;
}

export async function revokeAnnouncement(id: string) {
  return (await request<ApiResponse<Announcement>>(`/admin/announcements/${id}/revoke`, { method: "POST" })).data;
}

// === Device Blocks ===

export interface DeviceBlock {
  id: string;
  deviceId: string | null;
  installationId: string | null;
  reason: string;
  status: "active" | "revoked";
  createdAt: string;
  expiresAt?: string;
  revokedAt?: string;
}

export interface DeviceBlockCreateRequest {
  deviceId?: string | null;
  installationId?: string | null;
  reason: string;
  expiresAt?: string | null;
}

export async function getDeviceBlocks() {
  return (await request<ApiResponse<DeviceBlock[]>>("/admin/device-blocks")).data;
}

export async function createDeviceBlock(data: DeviceBlockCreateRequest) {
  return (
    await request<ApiResponse<DeviceBlock>>("/admin/device-blocks", {
      method: "POST",
      body: JSON.stringify(data),
    })
  ).data;
}

export async function revokeDeviceBlock(id: string) {
  return (await request<ApiResponse<unknown>>(`/admin/device-blocks/${id}/revoke`, { method: "POST" })).data;
}

// === Policies ===

export interface GlobalPolicy {
  id: string;
  type: "global" | "version_range";
  targetVersionMin: string | null;
  targetVersionMax: string | null;
  reason: string;
  allowUpdate: boolean;
  status: "active" | "revoked";
  createdAt: string;
  expiresAt?: string;
  revokedAt?: string;
}

export interface GlobalPolicyCreateRequest {
  type: "global" | "version_range";
  targetVersionMin?: string | null;
  targetVersionMax?: string | null;
  reason: string;
  allowUpdate: boolean;
  expiresAt?: string | null;
}

export async function getGlobalPolicies() {
  return (await request<ApiResponse<GlobalPolicy[]>>("/admin/policies")).data;
}

export async function createGlobalPolicy(data: GlobalPolicyCreateRequest) {
  return (
    await request<ApiResponse<GlobalPolicy>>("/admin/policies", {
      method: "POST",
      body: JSON.stringify(data),
    })
  ).data;
}

export async function revokeGlobalPolicy(id: string) {
  return (await request<ApiResponse<unknown>>(`/admin/policies/${id}/revoke`, { method: "POST" })).data;
}

export async function downloadFile(path: string): Promise<Blob> {
  const headers = new Headers();
  headers.set("X-NetRelay-Protocol", "1");

  const response = await fetch(`/api/v1${path}`, {
    method: "GET",
    credentials: "same-origin",
    headers,
  });

  if (!response.ok) {
    let failure: ApiErrorResponse | undefined;
    try {
      failure = (await response.json()) as ApiErrorResponse;
    } catch {
      failure = undefined;
    }
    throw new ApiClientError(
      failure?.error?.message || `请求失败（HTTP ${response.status}）`,
      response.status,
      failure?.error?.code || "HTTP_ERROR",
      failure?.requestId,
      failure?.error?.details,
    );
  }

  return await response.blob();
}
