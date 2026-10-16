import { App, Alert, Button, Card, DatePicker, Descriptions, Dropdown, Form, Input, Modal, Select, Skeleton, Space, Table, Tabs, Tag } from 'antd';
import { CheckCircleOutlined, DownOutlined, EditOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import type { Dayjs } from 'dayjs';
import { get, patch, post, type Paged } from '../api/client';
import type { BodyDetail, Meeting, PendingItem } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../components/common';
import { ActivityTimeline, Attachments, displayName, useReasonPrompt } from '../components/governance';
import { dualDate, formatDateTime } from '../lib/format';
import { CLASSES, FREQUENCIES } from './BodiesPage';
import { DelegationsPanel, MembersPanel } from './body/Members';
import { PolicyPanel } from './body/Policy';

const d = (v?: Dayjs | null) => v?.format('YYYY-MM-DD');

export default function BodyDetailPage() {
  const { id } = useParams();
  const [params, setParams] = useSearchParams();
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const reason = useReasonPrompt();
  const [editing, setEditing] = useState(false);
  const q = useQuery({ queryKey: ['body', id], queryFn: () => get<BodyDetail>(`/api/v1/bodies/${id}`) });
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['body', id] });
    void qc.invalidateQueries({ queryKey: ['body-activity', id] });
    void qc.invalidateQueries({ queryKey: ['members', id] });
  };
  const activate = useMutation({
    mutationFn: () => post(`/api/v1/bodies/${id}/activate`),
    onSuccess: () => { message.success(t('bodies.activated')); refresh(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });

  if (q.isLoading) return <Skeleton active />;
  if (q.error) return <ErrorAlert error={q.error} />;
  const { body: b, permissions: perms, terms, children, partners } = q.data!;
  const has = (p: string) => perms.includes(p);
  const lifecycle = has('body.lifecycle.manage');
  const closed = ['dissolved', 'archived'].includes(b.status);

  const act = (action: string, title: string, opts: { danger?: boolean; extra?: React.ReactNode; map?: (v: Record<string, any>) => Record<string, unknown>; description?: string } = {}) =>
    reason.prompt({
      title, danger: opts.danger, extra: opts.extra, description: opts.description,
      onSubmit: async (v) => { await post(`/api/v1/bodies/${id}/${action}`, opts.map ? opts.map(v) : v); refresh(); },
    });

  const termFields = (renew: boolean) => (
    <>
      <div className="form-row">
        {renew && <Form.Item name="termStart" label={t('bodies.termStart')} rules={[{ required: true }]}><DatePicker style={{ width: '100%' }} /></Form.Item>}
        <Form.Item name="termEnd" label={t('bodies.newTermEnd')} rules={[{ required: true }]}><DatePicker style={{ width: '100%' }} /></Form.Item>
      </div>
      <div className="form-row">
        <Form.Item name="decisionNo" label={t('bodies.decisionNo')}><Input /></Form.Item>
        <Form.Item name="decisionDate" label={t('bodies.decisionDate')}><DatePicker style={{ width: '100%' }} /></Form.Item>
      </div>
    </>
  );
  const mapTerm = (v: Record<string, any>) => ({ ...v, termStart: d(v.termStart), termEnd: d(v.termEnd), decisionDate: d(v.decisionDate) });

  const lifecycleItems = [
    ...(b.status === 'active' ? [{ key: 'suspend', label: t('lifecycle.suspend'), onClick: () => act('suspend', t('lifecycle.suspend')) }] : []),
    ...(b.status === 'suspended' ? [{ key: 'resume', label: t('lifecycle.resume'), onClick: () => act('resume', t('lifecycle.resume')) }] : []),
    ...(!closed ? [
      { key: 'extend', label: t('lifecycle.extend'), onClick: () => act('extend', t('lifecycle.extend'), { extra: termFields(false), map: mapTerm, description: t('lifecycle.extendHelp') }) },
      { key: 'renew', label: t('lifecycle.renew'), onClick: () => act('renew', t('lifecycle.renew'), { extra: termFields(true), map: mapTerm, description: t('lifecycle.renewHelp') }) },
    ] : []),
    ...(b.status === 'active' || b.status === 'suspended' ? [{
      key: 'terminate', danger: true, label: t('lifecycle.terminate'),
      onClick: () => act('terminate', t('lifecycle.terminate'), {
        danger: true, description: t('lifecycle.terminateHelp'), map: (v) => ({ ...v, effectiveDate: d(v.effectiveDate) }),
        extra: <div className="form-row"><Form.Item name="decisionNo" label={t('bodies.decisionNo')}><Input /></Form.Item><Form.Item name="effectiveDate" label={t('lifecycle.effectiveDate')}><DatePicker style={{ width: '100%' }} /></Form.Item></div>,
      }),
    }] : []),
    ...(b.status === 'dissolved' ? [{ key: 'archive', label: t('lifecycle.archive'), onClick: () => act('archive', t('lifecycle.archive')) }] : []),
  ];

  const tab = params.get('tab') ?? 'overview';
  return (
    <>
      <PageHeader
        title={displayName(i18n.language, b.nameAr, b.nameEn)}
        subtitle={<Space wrap><span className="mono">{b.referenceNo}</span><span className="mono">{b.code}</span><Tag>{t(`enums.bodyType.${b.bodyType}`)}</Tag><StatusTag group="bodyStatus" value={b.status} /></Space>}
        extra={<>
          {has('body.update') && !closed && <Button icon={<EditOutlined />} onClick={() => setEditing(true)}>{t('common.edit')}</Button>}
          {lifecycle && b.status === 'draft' && (
            <Button type="primary" icon={<CheckCircleOutlined />} loading={activate.isPending} onClick={() => activate.mutate()}>{t('bodies.activate')}</Button>
          )}
          {lifecycle && lifecycleItems.length > 0 && (
            <Dropdown menu={{ items: lifecycleItems }}><Button>{t('lifecycle.actions')} <DownOutlined /></Button></Dropdown>
          )}
        </>}
      />
      {activate.error instanceof Error && <ActivationProblems error={activate.error} />}
      {b.statusReason && <Alert type={closed ? 'warning' : 'info'} showIcon message={`${t('common.reason')}: ${b.statusReason}`} style={{ marginBottom: 12 }} />}
      <Card>
        <Tabs activeKey={tab} onChange={(k) => setParams({ tab: k }, { replace: true })} items={[
          { key: 'overview', label: t('bodies.tabs.overview'), children: <Overview data={q.data!} /> },
          { key: 'members', label: t('bodies.tabs.members'), children: <MembersPanel bodyId={id!} bodyStatus={b.status} perms={perms} /> },
          { key: 'policy', label: t('bodies.tabs.policy'), children: <PolicyPanel bodyId={id!} canEdit={has('body.policy.manage') && !closed} /> },
          { key: 'delegations', label: t('bodies.tabs.delegations'), children: <DelegationsPanel bodyId={id!} perms={perms} /> },
          { key: 'meetings', label: t('bodies.tabs.meetings'), children: <BodyMeetings bodyId={id!} canManage={has('meeting.manage')} /> },
          { key: 'pending', label: t('bodies.tabs.pending'), children: <PendingItems bodyId={id!} canManage={has('meeting.manage')} /> },
          { key: 'structure', label: t('bodies.tabs.structure'), children: <Structure parent={b.parentBodyId ? { id: b.parentBodyId, name: b.parentName } : null} children={children} partners={partners} terms={terms} /> },
          { key: 'attachments', label: t('bodies.tabs.attachments'), children: <Attachments ownerType="body" ownerId={id!} canUpload={has('document.upload')} purposes={['formation_decision', 'attachment']} /> },
          { key: 'activity', label: t('bodies.tabs.activity'), children: <ActivityTimeline url={`/api/v1/bodies/${id}/activity`} queryKey={['body-activity', id]} /> },
        ]} />
      </Card>
      {editing && <EditBodyModal data={q.data!} onClose={() => { setEditing(false); refresh(); }} />}
      {reason.node}
    </>
  );
}

function ActivationProblems({ error }: { error: Error }) {
  const { t } = useTranslation();
  const problems = Object.keys((error as { problem?: { problems?: Record<string, string> } }).problem?.problems ?? {});
  if (!problems.length) return <ErrorAlert error={error} />;
  return <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('bodies.formationIncomplete')}
    description={<ul style={{ margin: 0 }}>{problems.map((p) => <li key={p}>{t(`bodies.problems.${p}`)}</li>)}</ul>} />;
}

function Overview({ data }: { data: BodyDetail }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const b = data.body;
  return (
    <Descriptions column={{ xs: 1, md: 2 }} size="small" bordered>
      <Descriptions.Item label={t('bodies.referenceNo')}><span className="mono">{b.referenceNo}</span></Descriptions.Item>
      <Descriptions.Item label={t('common.type')}>{t(`enums.bodyType.${b.bodyType}`)}</Descriptions.Item>
      <Descriptions.Item label={t('common.nameAr')}>{b.nameAr}</Descriptions.Item>
      <Descriptions.Item label={t('common.nameEn')}><span dir="ltr">{b.nameEn ?? '—'}</span></Descriptions.Item>
      <Descriptions.Item label={t('bodies.objective')} span={2}>{b.objectiveAr ?? '—'}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.mandate')} span={2}><div style={{ whiteSpace: 'pre-wrap' }}>{b.mandateAr ?? '—'}</div></Descriptions.Item>
      <Descriptions.Item label={t('bodies.authorityRef')}>{b.authorityRef ?? '—'}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.ownerUnit')}>{b.ownerOrgUnitName ?? '—'}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.formationNo')}>{b.formationDecisionNo ?? '—'}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.formationDate')}>{dualDate(b.formationDecisionDate, i18n.language)}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.termStart')}>{dualDate(b.termStart, i18n.language)}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.termEnd')}>{dualDate(b.termEnd, i18n.language)}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.chair')}>{b.chairName ?? '—'}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.viceChair')}>{b.viceChairName ?? '—'}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.secretary')}>{b.secretaryName ?? '—'}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.memberCount')}>{b.memberCount}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.frequency')}>{t(`enums.frequency.${b.meetingFrequency}`)}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.classification')}>{t(`enums.classification.${b.classification}`)}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.lastMeeting')}>{formatDateTime(b.lastMeetingAt, i18n.language, me?.user.calendar)}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.nextMeeting')}>{formatDateTime(b.nextMeetingAt, i18n.language, me?.user.calendar)}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.myPermissions')} span={2}>
        <Space wrap size={[4, 4]}>{data.permissions.map((p) => <Tag key={p}>{t(`permissions.${p}`, { defaultValue: p })}</Tag>)}</Space>
      </Descriptions.Item>
    </Descriptions>
  );
}

function Structure({ parent, children, partners, terms }: {
  parent: { id: string; name?: string | null } | null; children: BodyDetail['children']; partners: BodyDetail['partners']; terms: BodyDetail['terms'];
}) {
  const { t, i18n } = useTranslation();
  return (
    <Space direction="vertical" style={{ width: '100%' }} size="large">
      <Descriptions size="small" column={1} bordered>
        <Descriptions.Item label={t('bodies.parent')}>{parent ? <Link to={`/bodies/${parent.id}`}>{parent.name}</Link> : '—'}</Descriptions.Item>
        <Descriptions.Item label={t('bodies.partners')}>{partners.length ? partners.map((p) => <Tag key={p.id}>{p.nameAr}</Tag>) : '—'}</Descriptions.Item>
      </Descriptions>
      <Card size="small" title={t('bodies.children')}>
        <Table rowKey="id" size="small" pagination={false} dataSource={children} locale={{ emptyText: t('common.empty') }} columns={[
          { title: t('bodies.referenceNo'), dataIndex: 'referenceNo', render: (v) => <span className="mono">{v}</span> },
          { title: t('common.name'), render: (_, c) => <Link to={`/bodies/${c.id}`}>{c.nameAr}</Link> },
          { title: t('common.type'), dataIndex: 'bodyType', render: (v) => t(`enums.bodyType.${v}`) },
          { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="bodyStatus" value={v} /> },
        ]} />
      </Card>
      <Card size="small" title={t('bodies.termsHistory')}>
        <Table rowKey={(r) => r.createdAt + r.changeType} size="small" pagination={false} dataSource={terms} columns={[
          { title: t('common.type'), dataIndex: 'changeType', render: (v) => <Tag>{t(`enums.termChange.${v}`)}</Tag> },
          { title: t('bodies.termStart'), dataIndex: 'termStart', render: (v) => dualDate(v, i18n.language) },
          { title: t('bodies.termEnd'), dataIndex: 'termEnd', render: (v) => dualDate(v, i18n.language) },
          { title: t('bodies.decisionNo'), dataIndex: 'decisionNo', render: (v) => v ?? '—' },
          { title: t('common.reason'), dataIndex: 'reason', render: (v) => v ?? '—' },
          { title: t('activity.by'), dataIndex: 'createdBy', render: (v) => v ?? '—' },
        ]} />
      </Card>
    </Space>
  );
}

function BodyMeetings({ bodyId, canManage }: { bodyId: string; canManage: boolean }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const q = useQuery({ queryKey: ['meetings', { bodyId }], queryFn: () => get<Paged<Meeting>>('/api/v1/meetings', { bodyId, pageSize: 50 }) });
  return (
    <>
      {canManage && <Link to={`/meetings?new=1&bodyId=${bodyId}`}><Button type="primary" style={{ marginBottom: 12 }}>{t('meetings.new')}</Button></Link>}
      <ErrorAlert error={q.error} />
      <Table<Meeting> rowKey="id" size="small" loading={q.isLoading} dataSource={q.data?.items} pagination={false} columns={[
        { title: t('meetings.reference'), dataIndex: 'referenceNo', render: (v, m) => <Link to={`/meetings/${m.id}`} className="mono">{v ?? '—'}</Link> },
        { title: t('common.title'), dataIndex: 'titleAr', render: (v, m) => <Link to={`/meetings/${m.id}`}>{v}</Link> },
        { title: t('common.type'), dataIndex: 'meetingType', render: (v) => t(`enums.meetingType.${v}`) },
        { title: t('meetings.startsAt'), dataIndex: 'startsAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
        { title: t('meetings.agendaCount'), dataIndex: 'agendaCount' },
        { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="meetingStatus" value={v} /> },
      ]} />
    </>
  );
}

export function PendingItems({ bodyId, canManage }: { bodyId: string; canManage: boolean }) {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const reason = useReasonPrompt();
  const [status, setStatus] = useState<string | undefined>('pending');
  const q = useQuery({ queryKey: ['pending-items', bodyId, status], queryFn: () => get<PendingItem[]>(`/api/v1/bodies/${bodyId}/pending-items`, { status }) });
  return (
    <>
      <div className="toolbar">
        <Select value={status} onChange={setStatus} allowClear placeholder={t('common.status')} style={{ width: 200 }}
          options={['pending', 'scheduled', 'dropped'].map((v) => ({ value: v, label: t(`enums.pendingStatus.${v}`) }))} />
        <span style={{ color: '#8c8c8c', alignSelf: 'center' }}>{t('pending.help')}</span>
      </div>
      <ErrorAlert error={q.error} />
      <Table<PendingItem> rowKey="id" size="small" loading={q.isLoading} dataSource={q.data} pagination={false} locale={{ emptyText: t('pending.none') }} columns={[
        { title: t('common.title'), dataIndex: 'titleAr' },
        { title: t('common.type'), dataIndex: 'kind', render: (v) => <Tag color={v === 'referred' ? 'purple' : 'orange'}>{t(`enums.pendingKind.${v}`)}</Tag> },
        { title: t('pending.source'), render: (_, p) => <span>{p.sourceBodyId !== bodyId ? `${p.sourceBodyName} — ` : ''}<Link to={`/meetings/${p.sourceMeetingId}`}>{p.sourceMeetingReference ?? p.sourceMeetingTitle}</Link></span> },
        { title: t('common.reason'), dataIndex: 'reason' },
        { title: t('common.date'), dataIndex: 'createdAt', render: (v) => formatDateTime(v, i18n.language) },
        { title: t('common.status'), dataIndex: 'status', render: (v, p) => p.scheduledMeetingId
          ? <Link to={`/meetings/${p.scheduledMeetingId}`}><StatusTag group="pendingStatus" value={v} /> {p.scheduledMeetingReference}</Link>
          : <StatusTag group="pendingStatus" value={v} /> },
        ...(canManage ? [{
          title: t('common.actions'),
          render: (_: unknown, p: PendingItem) => p.status === 'pending' && (
            <Button size="small" danger onClick={() => reason.prompt({
              title: t('pending.drop'), danger: true,
              onSubmit: async (v) => { await post(`/api/v1/pending-items/${p.id}/drop`, v); void qc.invalidateQueries({ queryKey: ['pending-items', bodyId] }); },
            })}>{t('pending.drop')}</Button>
          ),
        }] : []),
      ]} />
      {reason.node}
    </>
  );
}

function EditBodyModal({ data, onClose }: { data: BodyDetail; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const b = data.body;
  const m = useMutation({
    mutationFn: (v: Record<string, any>) => patch(`/api/v1/bodies/${b.id}`, { ...v, formationDecisionDate: d(v.formationDecisionDate) }),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('common.edit')} width={720} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)} initialValues={{
        nameAr: b.nameAr, nameEn: b.nameEn, objectiveAr: b.objectiveAr, mandateAr: b.mandateAr, authorityRef: b.authorityRef,
        formationDecisionNo: b.formationDecisionNo, meetingFrequency: b.meetingFrequency, classification: b.classification,
      }}>
        <div className="form-row">
          <Form.Item name="nameAr" label={t('common.nameAr')} rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="nameEn" label={t('common.nameEn')}><Input dir="ltr" /></Form.Item>
        </div>
        <Form.Item name="objectiveAr" label={t('bodies.objective')}><Input.TextArea rows={2} /></Form.Item>
        <Form.Item name="mandateAr" label={t('bodies.mandate')}><Input.TextArea rows={4} /></Form.Item>
        <div className="form-row">
          <Form.Item name="authorityRef" label={t('bodies.authorityRef')}><Input /></Form.Item>
          <Form.Item name="formationDecisionNo" label={t('bodies.formationNo')}><Input /></Form.Item>
          <Form.Item name="formationDecisionDate" label={t('bodies.formationDate')}><DatePicker style={{ width: '100%' }} /></Form.Item>
        </div>
        <div className="form-row">
          <Form.Item name="meetingFrequency" label={t('bodies.frequency')}><Select options={FREQUENCIES.map((v) => ({ value: v, label: t(`enums.frequency.${v}`) }))} /></Form.Item>
          <Form.Item name="classification" label={t('bodies.classification')}><Select options={CLASSES.map((v) => ({ value: v, label: t(`enums.classification.${v}`) }))} /></Form.Item>
        </div>
      </Form>
    </Modal>
  );
}
