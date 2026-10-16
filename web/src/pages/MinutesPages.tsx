// قوائم المحاضر والقرارات، وقوالب المحاضر.
import { App, Button, Card, Form, Input, Modal, Select, Space, Switch, Table, Tag, Typography } from 'antd';
import { DeleteOutlined, PlusOutlined, ProfileOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router-dom';
import { get, post, put, type Paged } from '../api/client';
import type { MinutesRecord, MinutesTemplate, ResolutionFull, TemplateSection } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../components/common';
import { BodySelect, EnumSelect } from '../components/pickers';
import { formatDate, formatDateTime } from '../lib/format';
import { CATEGORIES } from './minutes/ResolutionModal';

export { default as MinutesDetailPage } from './minutes/MinutesDetail';
export { default as ResolutionDetailPage } from './resolutions/ResolutionDetail';

export const MINUTES_STATUSES = ['draft', 'secretary_review', 'chair_review', 'member_review', 'pending_signatures', 'pending_final_approval', 'approved', 'returned'];
const RESOLUTION_STATUSES = ['proposed', 'issued', 'referred', 'in_execution', 'executed', 'closed', 'rejected', 'deferred', 'cancelled', 'superseded', 'withdrawn'];

export function MinutesListPage() {
  const { t, i18n } = useTranslation();
  const { me, can } = useAuth();
  const nav = useNavigate();
  const [filters, setFilters] = useState<{ bodyId?: string; status?: string; q?: string; page: number }>({ page: 1 });
  const q = useQuery({ queryKey: ['minutes', filters], queryFn: () => get<Paged<MinutesRecord>>('/api/v1/minutes', { ...filters, pageSize: 20 }) });
  return (
    <>
      <PageHeader title={t('minutes.title')} subtitle={t('minutes.subtitle')}
        extra={can('minutes.templates.manage') && <Button icon={<ProfileOutlined />} onClick={() => nav('/minutes/templates')}>{t('templates.title')}</Button>} />
      <Card>
        <div className="toolbar">
          <Input.Search placeholder={t('minutes.searchHint')} allowClear style={{ width: 260 }} onSearch={(v) => setFilters((f) => ({ ...f, q: v, page: 1 }))} />
          <BodySelect placeholder={t('common.body')} allowClear value={filters.bodyId} onChange={(v) => setFilters((f) => ({ ...f, bodyId: v, page: 1 }))} />
          <EnumSelect group="enums.minutesStatus" values={MINUTES_STATUSES} placeholder={t('common.status')} allowClear value={filters.status} onChange={(v) => setFilters((f) => ({ ...f, status: v, page: 1 }))} />
        </div>
        <ErrorAlert error={q.error} />
        <Table<MinutesRecord>
          rowKey="id" loading={q.isLoading} dataSource={q.data?.items}
          pagination={{ current: filters.page, total: q.data?.total, pageSize: 20, onChange: (page) => setFilters((f) => ({ ...f, page })) }}
          columns={[
            { title: t('minutes.meeting'), render: (_, m) => <Link to={`/minutes/${m.id}`}>{m.meetingTitle}</Link> },
            { title: t('meetings.reference'), dataIndex: 'meetingReference', render: (v) => <span className="mono">{v ?? '—'}</span> },
            { title: t('common.body'), dataIndex: 'bodyName' },
            { title: t('meetings.startsAt'), dataIndex: 'meetingStartsAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
            { title: t('minutes.version'), render: (_, m) => <Space size={4}>{m.versionNo || '—'}{m.amendmentNo > 0 && <Tag color="gold">{t('minutes.amendmentNo', { n: m.amendmentNo })}</Tag>}</Space>, width: 120 },
            { title: t('minutes.resolutionsCount'), dataIndex: 'resolutionCount', width: 90 },
            { title: t('common.status'), dataIndex: 'status', render: (v, m) => <Space size={4}><StatusTag group="minutesStatus" value={v} />{m.openComments > 0 && <Tag color="gold">{t('minutes.openComments', { n: m.openComments })}</Tag>}</Space> },
          ]}
        />
      </Card>
    </>
  );
}

export function ResolutionsPage() {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const [filters, setFilters] = useState<{ q?: string; bodyId?: string; status?: string; type?: string; category?: string; page: number }>({ page: 1 });
  const q = useQuery({ queryKey: ['resolutions', filters], queryFn: () => get<Paged<ResolutionFull>>('/api/v1/resolutions', { ...filters, pageSize: 20 }) });
  return (
    <>
      <PageHeader title={t('resolutions.title')} subtitle={t('resolutions.subtitle')} />
      <Card>
        <div className="toolbar">
          <Input.Search placeholder={t('common.search')} allowClear style={{ width: 240 }} onSearch={(v) => setFilters((f) => ({ ...f, q: v, page: 1 }))} />
          <BodySelect placeholder={t('common.body')} allowClear value={filters.bodyId} onChange={(v) => setFilters((f) => ({ ...f, bodyId: v, page: 1 }))} />
          <EnumSelect group="enums.resolutionType" values={['decision', 'recommendation']} placeholder={t('common.type')} allowClear value={filters.type} onChange={(v) => setFilters((f) => ({ ...f, type: v, page: 1 }))} />
          <EnumSelect group="enums.resolutionCategory" values={CATEGORIES} placeholder={t('resolutions.category')} allowClear value={filters.category} onChange={(v) => setFilters((f) => ({ ...f, category: v, page: 1 }))} />
          <EnumSelect group="enums.resolutionStatus" values={RESOLUTION_STATUSES} placeholder={t('common.status')} allowClear value={filters.status} onChange={(v) => setFilters((f) => ({ ...f, status: v, page: 1 }))} />
        </div>
        <ErrorAlert error={q.error} />
        <Table<ResolutionFull>
          rowKey="id" loading={q.isLoading} dataSource={q.data?.items}
          pagination={{ current: filters.page, total: q.data?.total, pageSize: 20, onChange: (page) => setFilters((f) => ({ ...f, page })) }}
          columns={[
            { title: t('resolutions.number'), dataIndex: 'number', render: (v, r) => <Link to={`/resolutions/${r.id}`}>{v ? <span className="mono">{v}</span> : <Typography.Text type="secondary">{t('resolutions.unnumbered')}</Typography.Text>}</Link> },
            { title: t('common.title'), dataIndex: 'titleAr', render: (v, r) => <Link to={`/resolutions/${r.id}`}>{v}</Link> },
            { title: t('common.type'), dataIndex: 'resolutionType', render: (v) => <Tag color={v === 'decision' ? 'green' : 'purple'}>{t(`enums.resolutionType.${v}`)}</Tag> },
            { title: t('common.body'), dataIndex: 'bodyName' },
            { title: t('resolutions.source'), render: (_, r) => <span className="mono">{r.meetingReference ?? '—'}</span> },
            { title: t('resolutions.vote'), render: (_, r) => r.voteSummary && r.voteSummary.method !== 'consensus' ? t('voting.tallyLine', { f: r.voteSummary.for, a: r.voteSummary.against, s: r.voteSummary.abstain }) : t(`enums.adoptionMethod.${r.adoptionMethod}`) },
            { title: t('resolutions.tasks'), render: (_, r) => `${r.tasksCompleted}/${r.taskCount}`, width: 80 },
            { title: t('resolutions.issuedAt'), dataIndex: 'issuedAt', render: (v) => formatDate(v, i18n.language, me?.user.calendar) },
            { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="resolutionStatus" value={v} /> },
          ]}
        />
      </Card>
    </>
  );
}

const SOURCES = ['meeting_info', 'attendance', 'quorum', 'agenda', 'discussions', 'decisions', 'votes', 'reservations', 'tasks', 'attachments', 'approval'];

export function TemplatesPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const [editing, setEditing] = useState<MinutesTemplate | 'new' | null>(null);
  const q = useQuery({ queryKey: ['minutes-templates'], queryFn: () => get<MinutesTemplate[]>('/api/v1/minutes/templates') });
  return (
    <>
      <PageHeader title={t('templates.title')} subtitle={t('templates.subtitle')} extra={<Button type="primary" icon={<PlusOutlined />} onClick={() => setEditing('new')}>{t('templates.new')}</Button>} />
      <Card>
        <ErrorAlert error={q.error} />
        <Table<MinutesTemplate> rowKey="id" loading={q.isLoading} dataSource={q.data} pagination={false}
          expandable={{ expandedRowRender: (r) => <ol style={{ margin: 0 }}>{r.sections.map((s) => <li key={s.key}>{s.titleAr} <Tag>{s.kind === 'auto' ? t(`templates.sources.${s.source}`) : t('templates.textSection')}</Tag></li>)}</ol> }}
          columns={[
            { title: t('common.name'), dataIndex: 'nameAr', render: (v, r) => <Space>{v}{r.isDefault && <Tag color="green">{t('templates.default')}</Tag>}</Space> },
            { title: t('templates.code'), dataIndex: 'code', render: (v) => <span className="mono">{v}</span> },
            { title: t('templates.sectionsCount'), render: (_, r) => r.sections.length },
            { title: t('templates.version'), dataIndex: 'version' },
            { title: t('templates.usage'), dataIndex: 'usage' },
            { title: t('common.status'), dataIndex: 'status', render: (v) => <Tag color={v === 'active' ? 'green' : 'default'}>{t(`templates.status.${v}`)}</Tag> },
            { title: '', render: (_, r) => <Button size="small" onClick={() => setEditing(r)}>{t('common.edit')}</Button> },
          ]} />
      </Card>
      {editing && <TemplateModal template={editing === 'new' ? undefined : editing} onClose={() => { setEditing(null); void qc.invalidateQueries({ queryKey: ['minutes-templates'] }); }} />}
    </>
  );
}

function TemplateModal({ template, onClose }: { template?: MinutesTemplate; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => template ? put(`/api/v1/minutes/templates/${template.id}`, v) : post('/api/v1/minutes/templates', v),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const initialSections: TemplateSection[] = template?.sections ?? [
    { key: 'meeting_info', titleAr: 'بيانات الاجتماع', kind: 'auto', source: 'meeting_info' },
    { key: 'notes', titleAr: 'ملاحظات', kind: 'text' },
    { key: 'approval', titleAr: 'بيانات المراجعة والاعتماد', kind: 'auto', source: 'approval' },
  ];
  return (
    <Modal open width={820} title={template ? t('templates.edit') : t('templates.new')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)} initialValues={{
        code: template?.code, nameAr: template?.nameAr, nameEn: template?.nameEn, isDefault: template?.isDefault ?? false, status: template?.status ?? 'active', sections: initialSections,
      }}>
        <div className="form-row">
          <Form.Item name="nameAr" label={t('common.name')} rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="nameEn" label={t('templates.nameEn')}><Input dir="ltr" /></Form.Item>
          <Form.Item name="code" label={t('templates.code')} rules={[{ required: true, pattern: /^[a-z][a-z0-9_-]{1,39}$/ }]}><Input dir="ltr" /></Form.Item>
        </div>
        <div className="form-row">
          <Form.Item name="isDefault" label={t('templates.default')} valuePropName="checked"><Switch /></Form.Item>
          <Form.Item name="status" label={t('common.status')}><Select options={['active', 'inactive'].map((v) => ({ value: v, label: t(`templates.status.${v}`) }))} /></Form.Item>
        </div>
        <Typography.Text strong>{t('templates.sections')}</Typography.Text>
        <Form.List name="sections">
          {(fields, { add, remove, move }) => (
            <>
              {fields.map((f, i) => (
                <Space key={f.key} align="baseline" wrap style={{ display: 'flex', marginTop: 8 }}>
                  <Form.Item name={[f.name, 'titleAr']} rules={[{ required: true }]} noStyle><Input placeholder={t('common.title')} style={{ width: 200 }} /></Form.Item>
                  <Form.Item name={[f.name, 'key']} rules={[{ required: true, pattern: /^[a-z][a-z0-9_]{1,39}$/ }]} noStyle><Input dir="ltr" placeholder="key" style={{ width: 120 }} /></Form.Item>
                  <Form.Item name={[f.name, 'kind']} noStyle initialValue="text"><Select style={{ width: 110 }} options={[{ value: 'text', label: t('templates.textSection') }, { value: 'auto', label: t('templates.autoSection') }]} /></Form.Item>
                  <Form.Item noStyle shouldUpdate>
                    {() => form.getFieldValue(['sections', f.name, 'kind']) === 'auto'
                      ? <Form.Item name={[f.name, 'source']} noStyle rules={[{ required: true }]}><Select style={{ width: 170 }} options={SOURCES.map((s) => ({ value: s, label: t(`templates.sources.${s}`) }))} /></Form.Item>
                      : <Form.Item name={[f.name, 'defaultBody']} noStyle><Input placeholder={t('templates.defaultBody')} style={{ width: 170 }} /></Form.Item>}
                  </Form.Item>
                  <Button size="small" disabled={i === 0} onClick={() => move(i, i - 1)}>↑</Button>
                  <Button size="small" disabled={i === fields.length - 1} onClick={() => move(i, i + 1)}>↓</Button>
                  <Button size="small" danger icon={<DeleteOutlined />} onClick={() => remove(f.name)} />
                </Space>
              ))}
              <Button style={{ marginTop: 8 }} icon={<PlusOutlined />} onClick={() => add({ kind: 'text', key: `section_${fields.length + 1}` })}>{t('templates.addSection')}</Button>
            </>
          )}
        </Form.List>
      </Form>
    </Modal>
  );
}
