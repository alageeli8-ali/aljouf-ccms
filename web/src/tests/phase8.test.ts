import { describe, expect, it } from 'vitest';
import ar from '../i18n/ar';
import en from '../i18n/en';
import phase8Ar from '../i18n/phase8.ar';
import phase8En from '../i18n/phase8.en';

const lookup = (dict: unknown, key: string) => key.split('.').reduce<unknown>((o, p) => (o && typeof o === 'object' ? (o as Record<string, unknown>)[p] : undefined), dict);
const each = (prefix: string, values: string[]) => values.map((v) => `${prefix}.${v}`);
const keysOf = (o: unknown, prefix = ''): string[] =>
  o && typeof o === 'object' && !Array.isArray(o)
    ? Object.entries(o as Record<string, unknown>).flatMap(([k, v]) => keysOf(v, prefix ? `${prefix}.${k}` : k))
    : [prefix];

describe('وحدة التقارير والسجلات والتكاملات', () => {
  it('النصوص متطابقة البنية بين العربية والإنجليزية', () => {
    expect(keysOf(phase8En).sort()).toEqual(keysOf(phase8Ar).sort());
  });

  it('كل القيم الديناميكية مترجمة باللغتين', () => {
    const keys = [
      ...each('rpt.basis', ['period', 'snapshot', 'mixed']),
      ...each('rpt.groups', ['meetings', 'resolutions', 'tasks', 'minutes']),
      ...each('rpt.kinds', ['council', 'committee']),
      ...each('rpt.assigneeType', ['org_unit', 'external_entity']),
      ...each('records.lifecycle', ['active', 'archived', 'destruction_pending', 'destroyed']),
      ...each('records.finalAction', ['permanent', 'destroy', 'review']),
      ...each('records.requestStatus', ['pending', 'approved', 'rejected', 'executed']),
      ...each('integ.callStatus', ['succeeded', 'failed', 'retrying', 'timeout', 'skipped_disabled', 'denied', 'circuit_open', 'not_configured']),
      ...each('integ.circuit', ['closed', 'open', 'half_open']),
      ...each('integ.authType', ['none', 'oauth2_client_credentials', 'oidc_authorization_code', 'api_key', 'smtp_auth', 'smtp_relay', 'provider_specific']),
      ...each('integ.dirSync.kind', ['update', 'link', 'disable']),
      ...each('ai.recordType', ['body', 'member', 'document', 'minutes', 'resolution', 'task', 'meeting']),
      'nav.records', 'nav.integrations',
    ];
    for (const k of keys) for (const d of [ar, en]) expect(typeof lookup(d, k), k).toBe('string');
  });

  it('الصدق في التكاملات: «يعمل فعليًا» مشروط بفحص ناجح، والتفعيل لا يعني الاتصال', () => {
    expect(lookup(ar, 'integ.verified')).toContain('فحص ناجح');
    expect(lookup(ar, 'integ.subtitle')).toContain('التفعيل وحده لا يعني الاتصال');
    expect(lookup(en, 'integ.subtitle')).toContain('enabling alone does not mean connected');
  });

  it('اللوحة تصرح بأن الأرقام فعلية ومقيدة بالصلاحية', () => {
    expect(lookup(ar, 'rpt.live')).toContain('لا أرقام تجريبية');
    expect(lookup(en, 'rpt.live')).toContain('No sample numbers');
  });
});
