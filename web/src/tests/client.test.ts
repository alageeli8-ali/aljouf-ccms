import { afterEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, resetCsrf } from '../api/client';

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

afterEach(() => { vi.restoreAllMocks(); resetCsrf(); });

describe('api client', () => {
  it('يرفق رمز CSRF بالطلبات المغيرة للحالة', async () => {
    const calls: RequestInit[] = [];
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (String(url).endsWith('/auth/csrf')) return json(200, { token: 'T1' });
      calls.push(init!);
      return json(200, { ok: true });
    }));
    await api('POST', '/api/v1/x', { a: 1 });
    expect((calls[0].headers as Record<string, string>)['X-CSRF-Token']).toBe('T1');
  });

  it('يجدد الرمز ويعيد المحاولة مرة واحدة عند رفض CSRF', async () => {
    let n = 0;
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (String(url).endsWith('/auth/csrf')) return json(200, { token: `T${++n}` });
      return n < 2 ? json(400, { code: 'csrf' }) : json(200, { ok: true });
    }));
    await expect(api('POST', '/api/v1/x', {})).resolves.toEqual({ ok: true });
  });

  it('يحوّل ProblemDetails إلى ApiError برمز المشكلة', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(409, { title: 'يتطلب تأكيدًا', code: 'confirmation-required', impact: [{ count: 2 }] })));
    const err = await api('GET', '/api/v1/x').catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).code).toBe('confirmation-required');
    expect((err as ApiError).problem.impact).toBeTruthy();
  });
});
