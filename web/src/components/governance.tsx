// مكونات مشتركة لوحدات المرحلة 2: الخط الزمني للنشاط، والمرفقات، ونافذة السبب الإلزامي.
import { App, Button, Drawer, Empty, Form, Input, Modal, Select, Space, Table, Tag, Timeline, Tooltip, Typography, Upload } from 'antd';
import { CloudUploadOutlined, DownloadOutlined, HistoryOutlined, LockOutlined, UploadOutlined } from '@ant-design/icons';
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
  const [versionsOf, setVersionsOf] = useState<Doc | null>(null);
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
          { title: t('attachments.file'), render: (_, d) => <Space size={4}>
            {d.locked && <Tooltip title={t('docs.lockedHint')}><LockOutlined style={{ color: '#B28655' }} /></Tooltip>}
            <span>{d.fileName}</span>
            {(d.versionNo ?? 1) > 1 && <Tag bordered={false} className="mono">v{d.versionNo}</Tag>}
          </Space> },
          { title: t('attachments.purpose'), dataIndex: 'purpose', render: (v) => t(`enums.docPurpose.${v}`, { defaultValue: v }) },
          { title: t('attachments.size'), dataIndex: 'sizeBytes', render: (v: number) => `${Math.max(1, Math.round(v / 1024))} KB` },
          { title: t('attachments.by'), dataIndex: 'uploadedBy' },
          { title: t('common.date'), dataIndex: 'uploadedAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
          { title: '', width: 130, render: (_, d) => <Space size={4}>
            <Button size="small" icon={<DownloadOutlined />} href={`/api/v1/documents/${d.id}/content`} target="_blank" aria-label={t('common.download', { defaultValue: 'download' })} />
            <Tooltip title={t('docs.versions')}><Button size="small" icon={<HistoryOutlined />} onClick={() => setVersionsOf(d)} aria-label={t('docs.versions')} /></Tooltip>
          </Space> },
        ]}
      />
      <VersionsDrawer doc={versionsOf} canUpload={canUpload} onClose={() => setVersionsOf(null)} onChanged={() => void qc.invalidateQueries({ queryKey: key })} />
    </>
  );
}

interface DocVersion { id: string; versionNo: number; fileName: string; sha256: string; isCurrent: boolean; locked: boolean; lifecycleStatus: string; versionNote: string | null; uploadedAt: string; uploadedBy: string; sizeBytes: number }

/** إصدارات المستند: الاستبدال بإصدار جديد يحفظ السابق؛ المقفل (موقّع/معتمد) يتطلب سببًا وصلاحية مسؤول السجلات (الخادم يفرضهما). */
function VersionsDrawer({ doc, canUpload, onClose, onChanged }: { doc: Doc | null; canUpload: boolean; onClose: () => void; onChanged: () => void }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const { me, can, feature } = useAuth();
  const qc = useQueryClient();
  const [note, setNote] = useState('');
  const key = ['documents', 'versions', doc?.id];
  const q = useQuery({ queryKey: key, enabled: !!doc, queryFn: () => get<DocVersion[]>(`/api/v1/documents/${doc!.id}/versions`) });
  const current = q.data?.find((v) => v.isCurrent);
  const allowed = !!current && current.lifecycleStatus !== 'destroyed' && (current.locked ? can('records.manage') : canUpload);
  const upload = useMutation({
    mutationFn: (file: File) => { const fd = new FormData(); fd.set('file', file); fd.set('note', note); return api('POST', `/api/v1/documents/${current!.id}/versions`, fd); },
    onSuccess: () => { message.success(t('docs.versionSaved')); setNote(''); void qc.invalidateQueries({ queryKey: key }); onChanged(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const deposit = useMutation({
    mutationFn: () => api('POST', `/api/v1/documents/${current!.id}/repository-export`),
    onSuccess: () => message.success(t('docs.exported')), onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Drawer open={!!doc} onClose={onClose} width={720} title={`${t('docs.versions')} — ${doc?.fileName ?? ''}`}
      extra={can('records.manage') && feature('DOCUMENT_REPOSITORY_ENABLED') && current ? <Button icon={<CloudUploadOutlined />} loading={deposit.isPending} onClick={() => deposit.mutate()}>{t('docs.exportRepo')}</Button> : null}>
      {current?.locked && <Typography.Paragraph type="warning"><LockOutlined /> {t('docs.lockedHint')}</Typography.Paragraph>}
      {allowed && (
        <Space.Compact style={{ width: '100%', marginBottom: 12 }}>
          <Input value={note} onChange={(e) => setNote(e.target.value)} placeholder={t('docs.versionNote')} maxLength={1000} />
          <Upload showUploadList={false} beforeUpload={(f) => { upload.mutate(f); return false; }}>
            <Button type="primary" icon={<UploadOutlined />} loading={upload.isPending}>{t('docs.newVersion')}</Button>
          </Upload>
        </Space.Compact>
      )}
      <ErrorAlert error={q.error} />
      <Table<DocVersion> rowKey="id" size="small" loading={q.isLoading} dataSource={q.data} pagination={false}
        columns={[
          { title: '#', dataIndex: 'versionNo', width: 50, render: (v) => <span className="mono">v{v}</span> },
          { title: t('attachments.file'), render: (_, v) => <Space direction="vertical" size={0}><span>{v.fileName}</span>{v.versionNote && <span className="item-meta">{v.versionNote}</span>}<span className="mono item-meta" dir="ltr">{v.sha256.slice(0, 16)}…</span></Space> },
          { title: t('common.status'), render: (_, v) => v.lifecycleStatus === 'destroyed' ? <Tag>{t('docs.destroyed')}</Tag> : v.isCurrent ? <Tag color="green">{t('docs.current')}</Tag> : <Tag>{t('docs.superseded')}</Tag> },
          { title: t('attachments.by'), render: (_, v) => <Space direction="vertical" size={0}><span>{v.uploadedBy}</span><span className="item-meta">{formatDateTime(v.uploadedAt, i18n.language, me?.user.calendar)}</span></Space> },
          { title: '', width: 50, render: (_, v) => v.lifecycleStatus === 'destroyed' ? null : <Button size="small" icon={<DownloadOutlined />} href={`/api/v1/documents/${v.id}/content`} target="_blank" /> },
        ]} />
    </Drawer>
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
