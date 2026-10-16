import { afterEach, describe, expect, it, vi } from 'vitest';
import { newRequestKey, postOnce, resetCsrf } from '../api/client';
import ar from '../i18n/ar';
import en from '../i18n/en';
import { MINUTES_STATUSES } from '../pages/MinutesPages';

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const lookup = (dict: unknown, key: string) => key.split('.').reduce<unknown>((o, p) => (o && typeof o === 'object' ? (o as Record<string, unknown>)[p] : undefined), dict);

afterEach(() => { vi.restoreAllMocks(); resetCsrf(); });

describe('المرحلة 3', () => {
  it('طلب الإنشاء يحمل مفتاح منع التكرار نفسه عند إعادة المحاولة', async () => {
    const keys: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (String(url).endsWith('/auth/csrf')) return json(200, { token: 'T' });
      keys.push((init!.headers as Record<string, string>)['Idempotency-Key']);
      return json(201, { id: 'r1' });
    }));
    const key = newRequestKey();
    await postOnce('/api/v1/minutes/m/resolutions', { titleAr: 'ق' }, key);
    await postOnce('/api/v1/minutes/m/resolutions', { titleAr: 'ق' }, key);
    expect(keys).toEqual([key, key]);
    expect(newRequestKey()).not.toBe(key);
  });

  it('كل حالات المحضر وقيم التصويت والطرق مترجمة بالعربية والإنجليزية', () => {
    const keys = [
      ...MINUTES_STATUSES.map((s) => `enums.minutesStatus.${s}`),
      ...['adopted', 'rejected', 'tied_unresolved', 'no_quorum', 'withdrawn'].map((s) => `enums.voteResult.${s}`),
      ...['electronic', 'show_of_hands', 'secret', 'consensus', 'circulation'].map((s) => `enums.voteMethod.${s}`),
      ...['none', 'present_members', 'present_members_and_chair', 'all_members'].map((s) => `enums.signatureMode.${s}`),
      ...['attestation', 'wet_signature', 'electronic_signature'].map((s) => `enums.signoffMethod.${s}`),
      ...['esign', 'esign_disabled', 'esign_not_integrated'].map((s) => `minutes.methodReason.${s}`),
    ];
    for (const k of keys) for (const d of [ar, en]) expect(typeof lookup(d, k), k).toBe('string');
  });
});
