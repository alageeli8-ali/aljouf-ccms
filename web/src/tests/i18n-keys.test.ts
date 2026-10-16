import { describe, expect, it } from 'vitest';
import ar from '../i18n/ar';
import en from '../i18n/en';

// مصادر الواجهة نصًا خامًا عبر Vite (دون الاعتماد على وحدات Node)
const sources = import.meta.glob(['../**/*.tsx', '../**/*.ts', '!../tests/**', '!../i18n/**'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

const lookup = (dict: unknown, key: string) => key.split('.').reduce<unknown>((o, p) => (o && typeof o === 'object' ? (o as Record<string, unknown>)[p] : undefined), dict);

describe('i18n coverage', () => {
  it('كل مفتاح ثابت مستخدم في الواجهة موجود بالعربية والإنجليزية', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(10);
    const missing: string[] = [];
    let checked = 0;
    for (const [file, text] of Object.entries(sources)) {
      for (const m of text.matchAll(/\bt\(\s*'([a-zA-Z0-9_.]+)'/g)) {
        const key = m[1];
        if (/defaultValue/.test(text.slice(m.index!, m.index! + key.length + 60))) continue;
        checked++;
        for (const [name, dict] of [['ar', ar], ['en', en]] as const)
          if (typeof lookup(dict, key) !== 'string') missing.push(`${name}:${key} (${file})`);
      }
    }
    expect(checked).toBeGreaterThan(300);
    expect(missing).toEqual([]);
  });
});
