import { describe, expect, it } from 'vitest';
import ar from '../i18n/ar';
import en from '../i18n/en';

const lookup = (dict: unknown, key: string) => key.split('.').reduce<unknown>((o, p) => (o && typeof o === 'object' ? (o as Record<string, unknown>)[p] : undefined), dict);
const each = (prefix: string, values: string[]) => values.map((v) => `${prefix}.${v}`);

describe('وحدة التوقيع الإلكتروني', () => {
  it('كل القيم الديناميكية مترجمة باللغتين', () => {
    const keys = [
      ...each('esign.types', ['in_app_approval', 'provider_esign', 'pdf_digital_signature']),
      ...each('esign.status', ['queued', 'in_progress', 'on_hold', 'completed', 'declined', 'voided', 'cancelled', 'expired', 'failed']),
      ...each('esign.signerStatus', ['waiting', 'pending', 'signed', 'declined', 'cancelled']),
      ...each('esign.roles', ['secretary', 'members', 'chair']),
      ...each('esign.chain', ['trusted', 'test_only', 'untrusted']),
      ...each('esign.overallValues', ['valid', 'valid_test_only', 'valid_untrusted_chain', 'documented_approval', 'incomplete', 'invalid', 'no_signatures']),
      ...each('esign.subsequentValues', ['none', 'signatures_only', 'other_changes']),
      ...each('esign.outcomes', ['applied', 'duplicate', 'rejected_signature', 'rejected_mismatch', 'rejected_unknown', 'deferred_disabled', 'deferred_on_hold', 'ignored']),
      ...each('esign.sources', ['system', 'user', 'provider', 'worker', 'admin']),
      ...each('esign.routeSource', ['route', 'settings']),
      ...each('esign.mockStatus', ['pending', 'signed', 'declined']),
      ...each('enums.signoffMethod', ['attestation', 'wet_signature', 'in_app_approval', 'provider_esign', 'pdf_digital_signature', 'esign_module']),
      ...each('minutes.methodReason', ['esign', 'esign_disabled', 'esign_provider_not_configured', 'esign_provider_not_integrated', 'esign_mock_blocked_in_production',
        'esign_switched_to_fallback', 'esign_no_signers']),
      ...each('settings.enumValues', ['none', 'mock', 'national_provider', 'in_app_approval', 'provider_esign', 'pdf_digital_signature']),
    ];
    for (const k of keys) for (const d of [ar, en]) expect(typeof lookup(d, k), k).toBe('string');
  });

  it('الموافقة داخل النظام لا تُعرض توقيعًا رقميًا، والمزوّد التجريبي موسوم بأنه ليس إنتاجيًا', () => {
    expect(lookup(ar, 'esign.overallValues.documented_approval')).toContain('ليست توقيعًا رقميًا');
    expect(lookup(en, 'esign.overallValues.documented_approval')).toContain('not a digital signature');
    expect(lookup(ar, 'esign.mockBanner')).toContain('ليس خدمة توقيع إنتاجية');
    expect(lookup(en, 'esign.mockTag')).toContain('not production');
  });
});
