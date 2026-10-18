import { describe, expect, it } from 'vitest';
import ar from '../i18n/ar';
import en from '../i18n/en';
import phase7Ar from '../i18n/phase7.ar';
import phase7En from '../i18n/phase7.en';

const lookup = (dict: unknown, key: string) => key.split('.').reduce<unknown>((o, p) => (o && typeof o === 'object' ? (o as Record<string, unknown>)[p] : undefined), dict);
const each = (prefix: string, values: string[]) => values.map((v) => `${prefix}.${v}`);
const keysOf = (o: unknown, prefix = ''): string[] =>
  o && typeof o === 'object' && !Array.isArray(o)
    ? Object.entries(o as Record<string, unknown>).flatMap(([k, v]) => keysOf(v, prefix ? `${prefix}.${k}` : k))
    : [prefix];

const FUNCTIONS = ['minutes_summary', 'agenda_draft', 'minutes_draft', 'transcription', 'extract_decisions', 'extract_tasks', 'link_task', 'smart_search', 'executive_summary', 'ask'];

describe('وحدة الذكاء الاصطناعي', () => {
  it('النصوص متطابقة البنية بين العربية والإنجليزية', () => {
    expect(keysOf(phase7En).sort()).toEqual(keysOf(phase7Ar).sort());
  });

  it('كل القيم الديناميكية مترجمة باللغتين', () => {
    const keys = [
      ...each('ai.functions', FUNCTIONS),
      ...each('ai.status', ['draft', 'saved', 'applied', 'discarded', 'expired', 'held']),
      ...each('ai.recordType', ['minutes', 'resolution', 'task', 'meeting', 'metric', 'document', 'agenda_item']),
      ...each('ai.transcription.status', ['queued', 'running', 'completed', 'failed', 'held', 'cancelled']),
      ...each('aiAdmin.logStatus', ['succeeded', 'failed', 'timeout', 'provider_unavailable', 'not_configured', 'invalid_output', 'blocked_disabled', 'blocked_classification', 'blocked_quota', 'held', 'no_sources']),
      ...each('aiAdmin.secretSource', ['environment', 'database', 'none', 'unreadable']),
      ...each('assistant.intent', ['overdue_resolutions', 'last_action', 'open_tasks', 'resolution_status', 'general']),
      ...each('settings.enumValuesByKey.AI_PROVIDER', ['none', 'mock', 'openai_compatible']),
      ...each('settings.enumValuesByKey.AI_MAX_CLASSIFICATION', ['none', 'public', 'restricted', 'secret']),
      ...each('settings.enumValuesByKey.AI_PROVIDER_HOSTING', ['on_premises', 'external']),
    ];
    for (const k of keys) for (const d of [ar, en]) expect(typeof lookup(d, k), k).toBe('string');
  });

  it('المخرج يُوصف دائمًا بأنه مسودة للمراجعة، والمزوّد التجريبي ليس نموذجًا لغويًا', () => {
    expect(lookup(ar, 'ai.generated')).toContain('مسودة');
    expect(lookup(en, 'ai.generated')).toContain('draft');
    expect(lookup(ar, 'ai.testOnly')).toContain('ليس نموذجًا لغويًا');
    expect(lookup(en, 'ai.testOnly')).toContain('not a language model');
    expect(lookup(ar, 'settings.enumValuesByKey.AI_MAX_CLASSIFICATION.none')).toContain('يمنع');
  });

  it('لا نص يصف الذكاء الاصطناعي بأنه يعتمد أو يوقّع أو يقرر', () => {
    const all = JSON.stringify(phase7Ar);
    for (const claim of ['يعتمد المحضر', 'وقّع الذكاء', 'قرر الذكاء', 'اعتمده الذكاء']) expect(all).not.toContain(claim);
  });
});
