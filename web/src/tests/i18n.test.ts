import { describe, expect, it } from 'vitest';
import ar from '../i18n/ar';
import en from '../i18n/en';

function keys(o: unknown, prefix = ''): string[] {
  if (o === null || typeof o !== 'object') return [prefix];
  return Object.entries(o as Record<string, unknown>).flatMap(([k, v]) => keys(v, prefix ? `${prefix}.${k}` : k));
}

describe('i18n', () => {
  it('العربية والإنجليزية تحملان المفاتيح نفسها', () => {
    expect(keys(en).sort()).toEqual(keys(ar).sort());
  });
  it('لا قيم فارغة', () => {
    for (const dict of [ar, en]) for (const k of keys(dict)) {
      const v = k.split('.').reduce<unknown>((o, p) => (o as Record<string, unknown>)[p], dict);
      expect(typeof v === 'string' && v.length > 0, k).toBe(true);
    }
  });
  it('كل الأدوار الاثنا عشر مترجمة', () => {
    for (const r of ['system_admin', 'governance_officer', 'chair', 'vice_chair', 'secretary', 'member', 'observer', 'task_executor',
      'task_follow_up', 'external_representative', 'executive', 'auditor'])
      expect(ar.roles[r as keyof typeof ar.roles]).toBeTruthy();
  });
});
