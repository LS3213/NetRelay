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
  ) {
    super(message);
  }
}

let csrfToken = "";

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  headers.set("X-NetRelay-Protocol", "1");
  if (init?.body) {
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
