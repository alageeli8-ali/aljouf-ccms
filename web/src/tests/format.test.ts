import { describe, expect, it } from 'vitest';
import { formatDate, percent } from '../lib/format';
import { toQuery } from '../api/client';

describe('format', () => {
  it('يعرض التاريخ الهجري بتقويم أم القرى', () => {
    const h = formatDate('2026-10-16', 'en', 'hijri');
    expect(h).toMatch(/1448/);
  });
  it('التاريخ الصرف لا ينزاح يومًا بسبب المنطقة الزمنية', () => {
    expect(formatDate('2026-10-16', 'en', 'gregorian')).toMatch(/16/);
  });
  it('النسبة', () => {
    expect(percent(1, 4)).toBe(25);
    expect(percent(0, 0)).toBe(0);
  });
  it('بناء الاستعلام يتجاهل القيم الفارغة', () => {
    expect(toQuery({ a: 1, b: '', c: undefined, d: 'x y' })).toBe('?a=1&d=x+y');
  });
});
