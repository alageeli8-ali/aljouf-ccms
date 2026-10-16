// عميل الواجهات البرمجية: جلسة كعكة HttpOnly من الخادم (BFF) + رمز CSRF في ترويسة لكل طلب يغيّر الحالة.
// قرارات السماح والمنع كلها في الخادم؛ الواجهة تعرض نتيجتها فقط.

export interface Problem {
  type?: string;
  title?: string;
  status: number;
  code?: string;
  detail?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
  [key: string]: unknown;
}

export class ApiError extends Error {
  readonly problem: Problem;
  constructor(problem: Problem) {
    super(problem.title ?? `HTTP ${problem.status}`);
    this.problem = problem;
  }
  get status() {
    return this.problem.status;
  }
  get code() {
    return this.problem.code ?? '';
  }
}

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

const BASE = (import.meta.env.VITE_API_BASE ?? '/').replace(/\/$/, '');
let csrfToken: string | null = null;
let onUnauthenticated: (() => void) | null = null;

export function setUnauthenticatedHandler(handler: () => void) {
  onUnauthenticated = handler;
}

export async function refreshCsrf(): Promise<void> {
  const r = await fetch(`${BASE}/api/v1/auth/csrf`, { credentials: 'same-origin' });
  csrfToken = ((await r.json()) as { token: string }).token;
}

export function resetCsrf() {
  csrfToken = null;
}

export function toQuery(params?: Record<string, unknown>): string {
  if (!params) return '';
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue;
    q.set(k, String(v));
  }
  const s = q.toString();
  return s ? `?${s}` : '';
}

async function parseProblem(r: Response): Promise<Problem> {
  try {
    const body = await r.json();
    return { status: r.status, ...body };
  } catch {
    return { status: r.status, title: r.statusText };
  }
}

export async function api<T>(method: string, path: string, body?: unknown, init?: { raw?: boolean; retried?: boolean; idempotencyKey?: string }): Promise<T> {
  const unsafe = method !== 'GET' && method !== 'HEAD';
  if (unsafe && !csrfToken) await refreshCsrf();
  const isForm = body instanceof FormData;
  const headers: Record<string, string> = { 'Accept-Language': localStorageSafe('ccms.lang') ?? 'ar' };
  if (unsafe && csrfToken) headers['X-CSRF-Token'] = csrfToken;
  if (body !== undefined && !isForm) headers['Content-Type'] = 'application/json';
  if (init?.idempotencyKey) headers['Idempotency-Key'] = init.idempotencyKey;
  const r = await fetch(`${BASE}${path}`, {
    method,
    credentials: 'same-origin',
    headers,
    body: body === undefined ? undefined : isForm ? body : JSON.stringify(body),
  });
  if (r.status === 400 && unsafe && !init?.retried) {
    const p = await parseProblem(r.clone());
    if (p.code === 'csrf') {
      await refreshCsrf();
      return api<T>(method, path, body, { ...init, retried: true });
    }
  }
  if (!r.ok) {
    const problem = await parseProblem(r);
    if (r.status === 401 && onUnauthenticated && !path.startsWith('/api/v1/auth/login')) onUnauthenticated();
    throw new ApiError(problem);
  }
  if (r.status === 204 || init?.raw) return undefined as T;
  const text = await r.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const get = <T>(path: string, params?: Record<string, unknown>) => api<T>('GET', path + toQuery(params));
export const post = <T>(path: string, body?: unknown) => api<T>('POST', path, body);
/** طلب إنشاء بمفتاح منع التكرار: إعادة المحاولة أو النقر المزدوج بالمفتاح نفسه لا تنشئ سجلًا ثانيًا (يُعاد الرد الأول من الخادم). */
export const postOnce = <T>(path: string, body: unknown, key: string) => api<T>('POST', path, body, { idempotencyKey: key });

export function newRequestKey(): string {
  try {
    return crypto.randomUUID();
  } catch {
    return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}-${Math.random().toString(36).slice(2)}`;
  }
}

export const put = <T>(path: string, body: unknown) => api<T>('PUT', path, body);
export const patch = <T>(path: string, body: unknown) => api<T>('PATCH', path, body);

export function localStorageSafe(key: string, value?: string): string | null {
  // التخزين المحلي لتفضيلات العرض فقط (اللغة)؛ قد يكون غير متاح في بعض المتصفحات
  try {
    if (value !== undefined) {
      window.localStorage.setItem(key, value);
      return value;
    }
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}
