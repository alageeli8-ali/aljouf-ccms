// مكونات مشتركة لوحدات المرحلة 2: الخط الزمني للنشاط، والمرفقات، ونافذة السبب الإلزامي.
import { App, Button, Empty, Form, Input, Modal, Select, Space, Table, Tag, Timeline, Typography, Upload } from 'antd';
import { DownloadOutlined, UploadOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { api, get } from '../api/client';
import type { ActivityEntry, Doc } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { formatDateTime } from '../lib/format';
import { ErrorAlert, errorText } from './common';

/** الخط الزمني لنشاط سجل (جهة أو اجتماع) مقروءًا من سجل التدقيق في الخادم. */
export function ActivityTimeline({ url, queryKey }: { url: string; queryKey: unknown[] }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const q = useQuery({ queryKey, queryFn: () => get<ActivityEntry[]>(url) });
  if (q.error) return <ErrorAlert error={q.error} />;
  if (!q.isLoading && !q.data?.length) return <Empty description={t('common.empty')} />;
  return (
    <Timeline
      pending={q.isLoading ? t('common.loading') : undefined}
      items={(q.data ?? []).map((a) => ({
        key: a.seq,
        color: a.action.includes('cancel') || a.action.includes('terminated') || a.action.includes('ended') || a.action.includes('failed') ? 'red'
          : a.action.includes('deferred') || a.action.includes('referred') || a.action.includes('rescheduled') || a.action.includes('suspended') ? 'orange' : 'green',
        children: (
          <div>
            <Typography.Text strong>{t(`activity.actions.${a.action}`, { defaultValue: a.action })}</Typography.Text>
            <div style={{ color: '#8c8c8c', fontSize: 12 }}>
              {formatDateTime(a.occurredAt, i18n.language, me?.user.calendar)} — {a.actorName ?? t('activity.system')}
            </div>
            <ActivityDetails json={a.details} />
          </div>
        ),
      }))}
    />
  );
}

const hiddenKeys = new Set(['id', 'itemId', 'memberId', 'newMemberId', 'pendingItemId', 'newItemId', 'userId', 'policyId', 'newMeetingId', 'fromItemId', 'fromMeetingId', 'targetMeetingId', 'targetBodyId', 'chairUserId', 'fromBodyId', 'previous']);

const enumGroups: Record<string, string[]> = {
  status: ['itemStatus', 'meetingStatus', 'bodyStatus', 'memberStatus'], response: ['rsvp'], meetingType: ['meetingType'], mode: ['mode'], scope: ['delegationScope'],
};

/** قيم التفاصيل بلغة الواجهة: المنطقية نعم/لا، والقيم المعدودة بترجمتها، والتواريخ بصيغتها. */
function valueText(key: string, v: unknown, t: (k: string, o?: Record<string, unknown>) => string): string {
  if (typeof v === 'boolean') return v ? t('common.yes') : t('common.no');
  if (key === 'role' && typeof v === 'string') return t(`roles.${v}`, { defaultValue: v });
  for (const g of enumGroups[key] ?? []) {
    const x = t(`enums.${g}.${String(v)}`, { defaultValue: '' });
    if (x) return x;
  }
  if (typeof v === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(v)) return new Date(v).toLocaleString(document.documentElement.lang === 'en' ? 'en-GB' : 'ar-SA-u-nu-latn', { timeZone: 'Asia/Riyadh' });
  return String(v);
}

function ActivityDetails({ json }: { json?: string | null }) {
  const { t } = useTranslation();
  if (!json) return null;
  let data: unknown;
  try { data = JSON.parse(json); } catch { return null; }
  if (!data || typeof data !== 'object' || Array.isArray(data)) return null;
  const entries = Object.entries(data as Record<string, unknown>)
    .filter(([k, v]) => !hiddenKeys.has(k) && v !== null && v !== '' && (typeof v !== 'object'));
  if (!entries.length) return null;
  return (
    <Space size={[4, 4]} wrap style={{ marginTop: 4 }}>
      {entries.slice(0, 8).map(([k, v]) => (
        <Tag key={k} style={{ whiteSpace: 'normal' }}>{t(`activity.fields.${k}`, { defaultValue: k })}: {valueText(k, v, t)}</Tag>
      ))}
    </Space>
  );
}

/** المرفقات المرتبطة بسجل؛ الرفع والتنزيل يخضعان لصلاحية المالك في الخادم. */
export function Attachments({ ownerType, ownerId, canUpload, purposes = ['attachment'] }: { ownerType: string; ownerId: string; canUpload: boolean; purposes?: string[] }) {
  const [purpose, setPurpose] = useState(purposes[0]);
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const { me } = useAuth();
  const qc = useQueryClient();
  const key = ['documents', ownerType, ownerId];
  const q = useQuery({ queryKey: key, queryFn: () => get<Doc[]>('/api/v1/documents', { ownerType, ownerId }) });
  const upload = useMutation({
    mutationFn: (file: File) => {
      const fd = new FormData();
      fd.set('ownerType', ownerType);
      fd.set('ownerId', ownerId);
      fd.set('purpose', purpose);
      fd.set('file', file);
      return api('POST', '/api/v1/documents', fd);
    },
    onSuccess: () => { message.success(t('attachments.uploaded')); void qc.invalidateQueries({ queryKey: key }); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <>
      {canUpload && (
        <Space style={{ marginBottom: 12 }} wrap>
          {purposes.length > 1 && (
            <Select value={purpose} onChange={setPurpose} style={{ minWidth: 180 }} aria-label={t('attachments.purpose')}
              options={purposes.map((p) => ({ value: p, label: t(`enums.docPurpose.${p}`) }))} />
          )}
          <Upload showUploadList={false} beforeUpload={(f) => { upload.mutate(f); return false; }}>
            <Button icon={<UploadOutlined />} loading={upload.isPending}>{t('attachments.upload')}</Button>
          </Upload>
        </Space>
      )}
      <ErrorAlert error={q.error} />
      <Table<Doc>
        rowKey="id" size="small" loading={q.isLoading} dataSource={q.data} pagination={false}
        locale={{ emptyText: t('attachments.none') }}
        columns={[
          { title: t('attachments.file'), dataIndex: 'fileName' },
          { title: t('attachments.purpose'), dataIndex: 'purpose', render: (v) => t(`enums.docPurpose.${v}`, { defaultValue: v }) },
          { title: t('attachments.size'), dataIndex: 'sizeBytes', render: (v: number) => `${Math.max(1, Math.round(v / 1024))} KB` },
          { title: t('attachments.by'), dataIndex: 'uploadedBy' },
          { title: t('common.date'), dataIndex: 'uploadedAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
          { title: '', render: (_, d) => <Button size="small" icon={<DownloadOutlined />} href={`/api/v1/documents/${d.id}/content`} target="_blank" /> },
        ]}
      />
    </>
  );
}

export interface ReasonPromptOptions {
  title: ReactNode;
  description?: ReactNode;
  okText?: string;
  danger?: boolean;
  /** حقول إضافية تُعرض قبل السبب (مثل التاريخ ورقم القرار). */
  extra?: ReactNode;
  initialValues?: Record<string, unknown>;
  reasonRequired?: boolean;
  onSubmit: (values: Record<string, any>) => Promise<unknown>;
}

/** نافذة تطلب سببًا إلزاميًا قبل أي إجراء يغيّر حالة رسمية (إلغاء، تأجيل، إنهاء…). */
export function useReasonPrompt() {
  const [opts, setOpts] = useState<ReasonPromptOptions | null>(null);
  const node = opts ? <ReasonModal opts={opts} onClose={() => setOpts(null)} /> : null;
  return { prompt: setOpts, node };
}

function ReasonModal({ opts, onClose }: { opts: ReasonPromptOptions; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => opts.onSubmit(v),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={opts.title} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending}
      okText={opts.okText ?? t('common.confirm')} cancelText={t('common.cancel')} okButtonProps={{ danger: opts.danger }} destroyOnHidden>
      {opts.description && <Typography.Paragraph type="secondary">{opts.description}</Typography.Paragraph>}
      <Form form={form} layout="vertical" initialValues={opts.initialValues} onFinish={(v) => m.mutate(v)}>
        {opts.extra}
        <Form.Item name="reason" label={t('common.reason')} rules={opts.reasonRequired === false ? [] : [{ required: true, min: 3, message: t('common.reasonRequired') }]}>
          <Input.TextArea rows={3} />
        </Form.Item>
      </Form>
    </Modal>
  );
}

export function displayName(lang: string, ar: string, en?: string | null) {
  return lang === 'en' && en ? en : ar;
}
