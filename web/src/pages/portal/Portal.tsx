// بوابة الجهات الخارجية: تكليفات الجهة فقط، ببطاقة مقيدة، وإجراءات الجهة (تأكيد الاستلام، التحديث، المخرجات، الرد، طلب التمديد).
import { Alert, App, Button, Card, Col, Descriptions, Empty, Form, Input, InputNumber, Progress, Result, Row, Skeleton, Space, Table, Tabs, Tag, Typography, Upload } from 'antd';
import { CheckOutlined, DownloadOutlined, FileTextOutlined, UploadOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ApiError, api, get, post, type Paged } from '../../api/client';
import type { PortalCard, PortalMe, PortalTask, TaskAction, TaskDeliverable } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, PageHeader, errorText } from '../../components/common';
import { useReasonPrompt } from '../../components/governance';
import { dualDate, formatDateTime } from '../../lib/format';
import { DeliverableList, ExtensionModal, SubmitDeliverableModal, UpdatesLog } from '../tasks/TaskDetail';
import { DueCell, PriorityTag } from '../tasks/shared';

function PortalBlocked({ error }: { error: unknown }) {
  const { t } = useTranslation();
  const nav = useNavigate();
  if (error instanceof ApiError && error.problem.code === 'password-change-required')
    return <Result status="warning" title={t('portal.passwordFirst')} extra={<Button type="primary" onClick={() => nav('/account/password')}>{t('portal.changePassword')}</Button>} />;
  if (error instanceof ApiError && error.problem.code === 'feature-disabled')
    return <Result status="info" title={t('portal.disabled')} subTitle={t('portal.disabledHelp')} />;
  return <ErrorAlert error={error} />;
}

export function PortalHome() {
  const { t, i18n } = useTranslation();
  const nav = useNavigate();
  const [status, setStatus] = useState<string | undefined>();
  const me = useQuery({ queryKey: ['portal-me'], queryFn: () => get<PortalMe>('/api/v1/portal/me'), retry: false });
  const q = useQuery({ queryKey: ['portal-tasks', status], queryFn: () => get<Paged<PortalTask>>('/api/v1/portal/tasks', { status }), retry: false, enabled: !me.error });
  if (me.error) return <PortalBlocked error={me.error} />;
  return (
    <>
      <PageHeader title={t('portal.title')} subtitle={me.data?.entityNameAr} />
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('portal.scopeNote')} />
      <div className="kpi-row">
        {[
          { key: undefined, v: me.data?.open, l: t('portal.open') },
          { key: 'assigned', v: me.data?.awaitingAcknowledgement, l: t('portal.awaitingAck'), tone: (me.data?.awaitingAcknowledgement ?? 0) > 0 ? 'warning' : undefined },
          { key: 'overdue', v: me.data?.overdue, l: t('portal.overdue'), tone: (me.data?.overdue ?? 0) > 0 ? 'critical' : undefined },
          { key: 'completed', v: me.data?.completed, l: t('portal.completed') },
        ].map((x) => (
          <button type="button" key={x.l} className={`kpi-tile${x.tone ? ` tone-${x.tone}` : ''}${status === x.key ? ' selected' : ''}`} onClick={() => setStatus(x.key)}>
            <span className="kpi-value">{x.v ?? '—'}</span><span className="kpi-label">{x.l}</span>
          </button>
        ))}
      </div>
      <Card>
        <ErrorAlert error={q.error} />
        <Table<PortalTask> rowKey="id" loading={q.isLoading} dataSource={q.data?.items} pagination={false}
          onRow={(r) => ({ onClick: () => nav(`/portal/tasks/${r.id}`), style: { cursor: 'pointer' } })}
          locale={{ emptyText: t('portal.none') }}
          columns={[
            { title: t('tasks.reference'), dataIndex: 'referenceNo', render: (v) => <span className="mono">{v}</span> },
            { title: t('common.title'), dataIndex: 'titleAr' },
            { title: t('portal.assigningBody'), dataIndex: 'assigningBody' },
            { title: t('tasks.priority'), dataIndex: 'priority', render: (v) => <PriorityTag value={v} /> },
            { title: t('common.dueDate'), render: (_, r) => <DueCell due={r.dueDate} original={r.originalDueDate} overdue={r.isOverdue} /> },
            { title: t('tasks.progress'), dataIndex: 'progressPct', width: 120, render: (v) => <Progress percent={v} size="small" strokeColor="#006837" /> },
            { title: t('common.status'), render: (_, r) => <Space size={4} wrap><Tag color={r.statusColor}>{i18n.language === 'en' ? r.statusNameEn : r.statusNameAr}</Tag>{r.isOverdue && <Tag color="red">{t('portal.overdue')}</Tag>}{r.pendingExtension && <Tag color="gold">{t('tasks4.extensionPending')}</Tag>}</Space> },
          ]} />
      </Card>
    </>
  );
}

export function PortalTaskPage() {
  const { id } = useParams();
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const reason = useReasonPrompt();
  const [submitting, setSubmitting] = useState<TaskDeliverable | null>(null);
  const [ext, setExt] = useState(false);
  const q = useQuery({ queryKey: ['portal-task', id], queryFn: () => get<PortalCard>(`/api/v1/portal/tasks/${id}`), retry: false });
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['portal-task', id] }); void qc.invalidateQueries({ queryKey: ['portal-tasks'] }); void qc.invalidateQueries({ queryKey: ['portal-me'] }); };
  const act = useMutation({
    mutationFn: ({ action, body }: { action: string; body: Record<string, unknown> }) => post(`/api/v1/portal/tasks/${id}/actions/${action}`, body),
    onSuccess: () => { message.success(t('common.saved')); refresh(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  if (q.isLoading) return <Skeleton active />;
  if (q.error || !q.data) return <PortalBlocked error={q.error} />;
  const d = q.data;
  const task = d.task;
  const run = (a: TaskAction) => {
    const label = i18n.language === 'en' ? a.nameEn : a.nameAr;
    if (a.action === 'submit')
      return reason.prompt({ title: label, description: t('portal.submitHelp'), reasonRequired: false,
        extra: <Form.Item name="note" label={t('tasks4.completionNote')} rules={[{ required: true, min: 5 }]}><Input.TextArea rows={3} /></Form.Item>,
        onSubmit: (v) => act.mutateAsync({ action: 'submit', body: { note: v.note } }) });
    if (a.requiresReason)
      return reason.prompt({ title: label, danger: a.action === 'reject', onSubmit: (v) => act.mutateAsync({ action: a.action, body: { reason: v.reason } }) });
    act.mutate({ action: a.action, body: {} });
  };
  return (
    <>
      <PageHeader title={task.titleAr}
        subtitle={<Space wrap><Link to="/portal">{t('portal.title')}</Link><span className="mono">{task.referenceNo}</span><Tag color={task.statusColor}>{i18n.language === 'en' ? task.statusNameEn : task.statusNameAr}</Tag><PriorityTag value={task.priority} /></Space>}
        extra={<>
          {d.actions.map((a) => <Button key={a.action} type={a.action === 'reject' ? 'default' : 'primary'} danger={a.action === 'reject'} loading={act.isPending} onClick={() => run(a)}>{i18n.language === 'en' ? a.nameEn : a.nameAr}</Button>)}
          {d.capabilities.canRequestExtension && !task.pendingExtension && <Button onClick={() => setExt(true)}>{t('tasks4.requestExtension')}</Button>}
        </>} />
      {task.status === 'assigned' && <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('portal.ackPrompt')} />}
      {task.statusReason && task.status === 'in_progress' && <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('portal.returned')} description={task.statusReason} />}
      <Row gutter={16}>
        <Col xs={24} xl={16}>
          <Card style={{ marginBottom: 16 }}>
            <Descriptions size="small" bordered column={{ xs: 1, md: 2 }}>
              <Descriptions.Item label={t('portal.assigningBody')}>{task.assigningBody}</Descriptions.Item>
              <Descriptions.Item label={t('portal.followUp')}>{task.followUpName}{task.followUpEmail && <> — <a href={`mailto:${task.followUpEmail}`}>{task.followUpEmail}</a></>}</Descriptions.Item>
              <Descriptions.Item label={t('tasks4.required')} span={2}><Typography.Paragraph style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{task.externalBrief ?? task.description ?? task.titleAr}</Typography.Paragraph></Descriptions.Item>
              {task.acceptanceCriteria && <Descriptions.Item label={t('tasks4.acceptanceCriteria')} span={2}>{task.acceptanceCriteria}</Descriptions.Item>}
              <Descriptions.Item label={t('common.dueDate')}><DueCell due={task.dueDate} original={task.originalDueDate} overdue={task.isOverdue} /></Descriptions.Item>
              <Descriptions.Item label={t('tasks4.assignedAt')}>{formatDateTime(task.assignedAt, i18n.language, me?.user.calendar)}</Descriptions.Item>
              {task.resolutionNumber && <Descriptions.Item label={t('portal.resolution')} span={2}><b>{task.resolutionNumber}</b> — {task.resolutionTitle}<Typography.Paragraph type="secondary" style={{ whiteSpace: 'pre-wrap', margin: '4px 0 0' }}>{task.resolutionText}</Typography.Paragraph></Descriptions.Item>}
              {task.correspondenceNo && <Descriptions.Item label={t('portal.letterNo')} span={2}><span className="mono">{task.correspondenceNo}</span> — {dualDate(task.correspondenceDate, i18n.language)}</Descriptions.Item>}
            </Descriptions>
          </Card>
          <Card>
            <Tabs items={[
              { key: 'deliverables', label: t('tasks4.deliverables'), children: <DeliverableList items={d.deliverables} portal canSubmit={d.capabilities.canSubmitDeliverables} onSubmit={setSubmitting} /> },
              { key: 'updates', label: t('portal.updates'), children: <UpdatesLog portal updates={d.updates} canComment={d.capabilities.canComment} taskId={task.id} external onChange={refresh} /> },
              { key: 'files', label: t('portal.files'), children: <PortalFiles card={d} onChange={refresh} /> },
              { key: 'extensions', label: t('tasks4.extensions'), children: d.extensions.length ? (
                <Table size="small" rowKey="id" pagination={false} dataSource={d.extensions} columns={[
                  { title: t('tasks4.requestedDue'), dataIndex: 'requestedDueDate', render: (v) => dualDate(v, i18n.language) },
                  { title: t('tasks4.delayReason'), dataIndex: 'reason' },
                  { title: t('common.status'), dataIndex: 'status', render: (v) => <Tag>{t(`tasks4.extensionStatus.${v}`)}</Tag> },
                  { title: t('tasks4.approvedDue'), dataIndex: 'approvedDueDate', render: (v) => dualDate(v, i18n.language) },
                  { title: t('common.notes'), dataIndex: 'decisionNote' },
                ]} />) : <Empty description={t('tasks4.noExtensions')} /> },
            ]} />
          </Card>
        </Col>
        <Col xs={24} xl={8}>
          <Card size="small" title={t('tasks.progress')} style={{ marginBottom: 16 }}>
            <Progress type="dashboard" percent={task.progressPct} strokeColor="#006837" size={110} />
            {d.capabilities.canProgress && <PortalProgress taskId={task.id} current={task.progressPct} overdue={task.isOverdue} onChange={refresh} />}
          </Card>
          {d.letters.length > 0 && (
            <Card size="small" title={t('portal.letters')}>
              {d.letters.map((l) => <div key={l.id}><FileTextOutlined /> {l.subject} — <span className="mono">{l.outgoingNo}</span></div>)}
            </Card>
          )}
          {task.completedAt && <Alert type="success" showIcon icon={<CheckOutlined />} style={{ marginTop: 16 }} message={t('portal.completedNote')} />}
        </Col>
      </Row>
      {submitting && <SubmitDeliverableModal url={`/api/v1/portal/deliverables/${submitting.id}/submissions`} taskId={task.id} deliverable={submitting} onClose={() => { setSubmitting(null); refresh(); }} />}
      {ext && <ExtensionModal portal taskId={task.id} due={task.dueDate} onClose={() => { setExt(false); refresh(); }} />}
      {reason.node}
    </>
  );
}

function PortalProgress({ taskId, current, overdue, onChange }: { taskId: string; current: number; overdue: boolean; onChange: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post(`/api/v1/portal/tasks/${taskId}/progress`, v),
    onSuccess: () => { form.resetFields(['note', 'delayReason']); message.success(t('common.saved')); onChange(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Form form={form} layout="vertical" initialValues={{ progressPct: current }} onFinish={(v) => m.mutate(v)}>
      <Form.Item name="progressPct" label={t('tasks4.newProgress')}><InputNumber min={0} max={99} addonAfter="%" /></Form.Item>
      <Form.Item name="note" label={t('tasks.note')} rules={[{ required: true }]}><Input.TextArea rows={2} /></Form.Item>
      {overdue && <Form.Item name="delayReason" label={t('tasks4.delayReason')}><Input.TextArea rows={2} /></Form.Item>}
      <Button type="primary" htmlType="submit" loading={m.isPending}>{t('portal.updateStatus')}</Button>
    </Form>
  );
}

function PortalFiles({ card, onChange }: { card: PortalCard; onChange: () => void }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const upload = useMutation({
    mutationFn: (f: File) => {
      const fd = new FormData();
      fd.set('ownerType', 'task'); fd.set('ownerId', card.task.id); fd.set('purpose', 'attachment'); fd.set('file', f);
      return api('POST', '/api/v1/documents', fd);
    },
    onSuccess: () => { message.success(t('attachments.uploaded')); onChange(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <>
      {card.capabilities.canSubmitDeliverables && (
        <Upload showUploadList={false} beforeUpload={(f) => { upload.mutate(f); return false; }}>
          <Button icon={<UploadOutlined />} loading={upload.isPending} style={{ marginBottom: 12 }}>{t('portal.uploadFile')}</Button>
        </Upload>
      )}
      <Table size="small" rowKey="id" pagination={false} dataSource={card.documents} locale={{ emptyText: t('attachments.none') }} columns={[
        { title: t('attachments.file'), dataIndex: 'fileName' },
        { title: t('portal.source'), render: (_, d) => (d.fromEntity ? <Tag color="purple">{t('portal.fromEntity')}</Tag> : <Tag>{t('portal.fromOffice')}</Tag>) },
        { title: t('common.date'), dataIndex: 'uploadedAt', render: (v) => formatDateTime(v, i18n.language) },
        { title: '', render: (_, d) => <Button size="small" icon={<DownloadOutlined />} href={`/api/v1/documents/${d.id}/content`} target="_blank" /> },
      ]} />
    </>
  );
}

/** تغيير كلمة المرور (إلزامي لحسابات الجهات بعد كلمة المرور المؤقتة). */
export function ChangePasswordPage() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const { me, reload } = useAuth();
  const nav = useNavigate();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post('/api/v1/auth/password', v),
    onSuccess: async () => { message.success(t('portal.passwordChanged')); await reload(); nav(me?.portalUser ? '/portal' : '/'); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Card style={{ maxWidth: 480, margin: '24px auto' }} title={t('portal.changePassword')}>
      {me?.user.mustChangePassword && <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('portal.mustChange')} />}
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate({ currentPassword: v.currentPassword, newPassword: v.newPassword })}>
        <Form.Item name="currentPassword" label={t('portal.currentPassword')} rules={[{ required: true }]}><Input.Password autoComplete="current-password" /></Form.Item>
        <Form.Item name="newPassword" label={t('portal.newPassword')} extra={t('portal.passwordPolicy')} rules={[{ required: true, min: 10 }]}><Input.Password autoComplete="new-password" /></Form.Item>
        <Form.Item name="confirm" label={t('portal.confirmPassword')} dependencies={['newPassword']}
          rules={[{ required: true }, ({ getFieldValue }) => ({ validator: (_, v) => (v === getFieldValue('newPassword') ? Promise.resolve() : Promise.reject(new Error(t('portal.mismatch')))) })]}>
          <Input.Password autoComplete="new-password" />
        </Form.Item>
        <Button type="primary" htmlType="submit" loading={m.isPending} block>{t('common.save')}</Button>
      </Form>
    </Card>
  );
}
