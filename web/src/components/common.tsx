import { Alert, Tag, Typography } from 'antd';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';

const statusColors: Record<string, string> = {
  active: 'green', draft: 'default', suspended: 'orange', dissolved: 'red', archived: 'default',
  scheduled: 'blue', agenda_published: 'geekblue', in_progress: 'processing', concluded: 'cyan', closed: 'default',
  postponed: 'orange', cancelled: 'red', quorum_failed: 'volcano',
  internal_review: 'gold', member_review: 'gold', pending_signoffs: 'orange', pending_approval: 'purple', approved: 'green', amended: 'default',
  proposed: 'default', issued: 'green', in_execution: 'processing', executed: 'cyan', rejected: 'red', deferred: 'orange', referred: 'purple', superseded: 'default',
  assigned: 'blue', pending_verification: 'purple', pending_review: 'purple', completed: 'green', returned: 'volcano', on_hold: 'orange', overdue: 'red', new: 'default',
  requested: 'gold', valid: 'green', invalidated: 'red',
  healthy: 'green', not_configured: 'default', failing: 'red', degraded: 'orange', disabled: 'default',
  succeeded: 'green', rejected_validation: 'orange', confirmation_required: 'gold', denied: 'red', failed: 'red',
  pending: 'gold', sent: 'green', logged: 'default', suppressed: 'default',
  ended: 'default', accepted: 'green', declined: 'red', tentative: 'gold', present: 'green', absent: 'red', excused: 'orange', proxy: 'purple',
  discussed: 'green', withdrawn: 'default', dropped: 'default',
};

export function StatusTag({ group, value }: { group: string; value: string }) {
  const { t } = useTranslation();
  return <Tag color={statusColors[value] ?? 'default'}>{t(`enums.${group}.${value}`, { defaultValue: value })}</Tag>;
}

export function PageHeader({ title, subtitle, extra }: { title: ReactNode; subtitle?: ReactNode; extra?: ReactNode }) {
  return (
    <div className="page-header">
      <div>
        <h1>{title}</h1>
        {subtitle && <p>{subtitle}</p>}
      </div>
      {extra && <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>{extra}</div>}
    </div>
  );
}

/** يعرض أخطاء الخادم كما هي (ProblemDetails) مع رسائل التحقق لكل حقل. */
export function ErrorAlert({ error }: { error: unknown }) {
  const { t } = useTranslation();
  if (!error) return null;
  if (error instanceof ApiError) {
    const title = error.status === 403 ? t('common.forbidden') : error.status === 404 ? t('common.notFound') : error.problem.title ?? t('common.error');
    const details = error.problem.errors ? Object.entries(error.problem.errors).map(([k, v]) => `${k}: ${v.join('، ')}`) : [];
    return <Alert type="error" showIcon message={title} description={details.length ? <ul style={{ margin: 0 }}>{details.map((d) => <li key={d}>{d}</li>)}</ul> : undefined} style={{ marginBottom: 12 }} />;
  }
  return <Alert type="error" showIcon message={t('common.error')} style={{ marginBottom: 12 }} />;
}

export function errorText(error: unknown, fallback: string): string {
  if (error instanceof ApiError) {
    const fields = error.problem.errors ? ' — ' + Object.values(error.problem.errors).flat().join('، ') : '';
    return (error.problem.title ?? fallback) + fields;
  }
  return fallback;
}

export function Mono({ children }: { children: ReactNode }) {
  return <Typography.Text className="mono" copyable={false}>{children}</Typography.Text>;
}
