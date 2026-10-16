// بطاقة المهمة: المصدر، والمنفذ والمتابع، والمخرجات ومعايير قبولها، والتقدم، والتحقق، والتمديد، والتصعيد، والمرفقات، والخطابات، والسجل.
import { Alert, App, Button, Card, Col, DatePicker, Descriptions, Dropdown, Empty, Form, Input, InputNumber, Modal, Progress, Radio, Row, Select, Skeleton, Space, Switch,
  Table, Tabs, Tag, Timeline, Tooltip, Typography, Upload } from 'antd';
import { CheckOutlined, DownOutlined, FileTextOutlined, NotificationOutlined, SendOutlined, UploadOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import type { Dayjs } from 'dayjs';
import { api, get, newRequestKey, post, postOnce } from '../../api/client';
import type { Doc, TaskAction, TaskCard, TaskDeliverable, TaskUpdateEntry } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, PageHeader, errorText } from '../../components/common';
import { useReasonPrompt } from '../../components/governance';
import { dualDate, formatDateTime } from '../../lib/format';
import { LetterCreateModal } from './Letters';
import { DueCell, PriorityTag, TaskStatusTag } from './shared';

export const DELAY_CAUSES = ['dependency', 'resources', 'scope_change', 'external_party', 'data_availability', 'other'];
export const OFFICIAL_CHANNELS = ['letter', 'email', 'phone', 'meeting', 'correspondence_system', 'other'];

export default function TaskDetail() {
  const { id } = useParams();
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const reason = useReasonPrompt();
  const [params, setParams] = useSearchParams();
  const [review, setReview] = useState<'verify' | 'return' | null>(null);
  const [modal, setModal] = useState<'extension' | 'external' | 'escalate' | 'letter' | null>(null);
  const q = useQuery({ queryKey: ['task', id], queryFn: () => get<TaskCard>(`/api/v1/tasks/${id}`) });
  const refresh = () => { for (const k of [['task', id], ['tasks'], ['documents', 'task', id]]) void qc.invalidateQueries({ queryKey: k }); };
  const act = useMutation({
    mutationFn: ({ action, body }: { action: string; body: Record<string, unknown> }) => post<{ status: string }>(`/api/v1/tasks/${id}/actions/${action}`, body),
    onSuccess: () => { message.success(t('common.saved')); refresh(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  if (q.isLoading) return <Skeleton active />;
  if (q.error || !q.data) return <ErrorAlert error={q.error} />;
  const d = q.data;
  const task = d.task;
  const cap = d.capabilities;
  const dt = (v?: string | null) => formatDateTime(v, i18n.language, me?.user.calendar);
  const name = (a: TaskAction) => (i18n.language === 'en' ? a.nameEn : a.nameAr);

  const run = (a: TaskAction) => {
    if (a.action === 'verify' || a.action === 'return') { setReview(a.action); return; }
    if (a.action === 'submit' || a.action === 'complete') {
      reason.prompt({
        title: name(a), description: t(`tasks4.actionHelp.${a.action}`), okText: name(a), reasonRequired: false,
        extra: <Form.Item name="note" label={t('tasks4.completionNote')} rules={[{ required: true, min: 5 }]}><Input.TextArea rows={3} /></Form.Item>,
        onSubmit: (v) => act.mutateAsync({ action: a.action, body: { note: v.note, reason: v.reason || undefined } }),
      });
      return;
    }
    if (a.requiresReason) {
      reason.prompt({ title: name(a), description: t(`tasks4.actionHelp.${a.action}`, { defaultValue: '' }), danger: a.action === 'cancel' || a.action === 'reject',
        onSubmit: (v) => act.mutateAsync({ action: a.action, body: { reason: v.reason } }) });
      return;
    }
    act.mutate({ action: a.action, body: {} });
  };
  const primary = d.actions.filter((a) => ['accept', 'start', 'submit', 'verify', 'assign', 'resume', 'complete'].includes(a.action));
  const secondary = d.actions.filter((a) => !primary.includes(a));
  const tab = params.get('tab') ?? 'deliverables';

  return (
    <>
      <PageHeader
        title={task.titleAr}
        subtitle={<Space wrap>
          <span className="mono">{task.referenceNo ?? t('tasks4.notNumbered')}</span>
          <TaskStatusTag status={task.status} nameAr={task.statusNameAr} nameEn={task.statusNameEn} color={task.statusColor} overdue={task.isOverdue} days={task.daysOverdue} />
          <PriorityTag value={task.priority} />
          {task.escalationLevel > 0 && task.statusCategory !== 'done' && <Tag color="volcano">{t('tasks4.escalationN', { n: task.escalationLevel })}</Tag>}
          <Link to={`/bodies/${task.bodyId}`}>{task.bodyName}</Link>
        </Space>}
        extra={<>
          {primary.map((a) => <Button key={a.action} type="primary" loading={act.isPending} onClick={() => run(a)} icon={a.action === 'verify' ? <CheckOutlined /> : a.action === 'submit' ? <SendOutlined /> : undefined}>{name(a)}</Button>)}
          {(secondary.length > 0 || cap.canEscalate || cap.canRecordExternal || cap.canLetters || cap.canRequestExtension) && (
            <Dropdown menu={{ items: [
              ...secondary.map((a) => ({ key: a.action, label: name(a), danger: a.action === 'cancel' || a.action === 'reject', onClick: () => run(a) })),
              ...(cap.canRequestExtension ? [{ key: 'ext', label: t('tasks4.requestExtension'), onClick: () => setModal('extension') }] : []),
              ...(cap.canRecordExternal ? [{ key: 'rec', label: t('tasks4.recordExternal'), onClick: () => setModal('external') }] : []),
              ...(cap.canEscalate ? [{ key: 'esc', icon: <NotificationOutlined />, label: t('tasks4.escalate'), onClick: () => setModal('escalate') }] : []),
              ...(cap.canLetters && task.statusCategory !== 'done' && task.statusCategory !== 'closed' ? [{ key: 'letter', icon: <FileTextOutlined />, label: t('tasks4.letters.create'), onClick: () => setModal('letter') }] : []),
            ] }}>
              <Button>{t('common.more')} <DownOutlined /></Button>
            </Dropdown>
          )}
        </>}
      />
      {task.statusReason && ['rejected', 'on_hold', 'cancelled', 'in_progress'].includes(task.status) && (
        <Alert type={task.status === 'in_progress' ? 'warning' : 'info'} showIcon style={{ marginBottom: 12 }} message={t('tasks4.lastReason')} description={task.statusReason} />
      )}
      {task.assigneeType === 'external_entity' && !cap.portalEnabled && (
        <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('tasks4.portalOffNote')} />
      )}
      <Row gutter={16}>
        <Col xs={24} xl={16}>
          <Card style={{ marginBottom: 16 }}>
            <Descriptions size="small" bordered column={{ xs: 1, md: 2 }}>
              <Descriptions.Item label={t('tasks4.sourceLabel')} span={2}>
                {task.resolutionId
                  ? <Link to={`/resolutions/${task.resolutionId}`}>{t(`enums.resolutionType.${task.resolutionType}`)} {task.resolutionNumber ?? ''} — {task.resolutionTitle}</Link>
                  : task.agendaItemId ? <span>{t('tasks4.source.agenda_item')}: {task.agendaItemTitle}</span> : t('tasks4.source.manual')}
                {task.meetingId && <> · <Link to={`/meetings/${task.meetingId}`} className="mono">{task.meetingReference}</Link></>}
              </Descriptions.Item>
              <Descriptions.Item label={t('tasks4.required')} span={2}><Typography.Paragraph style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{task.description ?? task.titleAr}</Typography.Paragraph></Descriptions.Item>
              <Descriptions.Item label={t('tasks4.executor')}>{task.executorName ?? task.assigneeName} <Tag>{t(`enums.assigneeType.${task.assigneeType}`)}</Tag></Descriptions.Item>
              <Descriptions.Item label={t('tasks4.executingUnit')}>{task.executingUnitName ?? '—'}</Descriptions.Item>
              <Descriptions.Item label={t('tasks4.followUp')}>{task.accountableName ?? '—'}</Descriptions.Item>
              <Descriptions.Item label={t('tasks4.verifier')}>{task.verifierName ?? (task.verificationRequired ? t('tasks4.verifierDefault') : t('tasks4.noVerification'))}</Descriptions.Item>
              <Descriptions.Item label={t('tasks4.assignedAt')}>{dt(task.assignedAt)}</Descriptions.Item>
              <Descriptions.Item label={t('common.dueDate')}><DueCell due={task.dueDate} original={task.originalDueDate} overdue={task.isOverdue} />{task.originalDueDate && task.originalDueDate !== task.dueDate && <span className="item-meta"> ({t('tasks4.originalDue')}: {dualDate(task.originalDueDate, i18n.language)})</span>}</Descriptions.Item>
              <Descriptions.Item label={t('tasks4.acceptanceCriteria')} span={2}>{task.acceptanceCriteria ?? '—'}</Descriptions.Item>
              {task.externalBrief && <Descriptions.Item label={t('tasks4.externalBrief')} span={2}>{task.externalBrief}</Descriptions.Item>}
              {task.delayReason && <Descriptions.Item label={t('tasks4.delayReason')} span={2}>{task.delayReason}</Descriptions.Item>}
              {task.correspondenceNo && <Descriptions.Item label={t('tasks4.correspondenceNo')} span={2}><span className="mono">{task.correspondenceNo}</span> — {dualDate(task.correspondenceDate, i18n.language)}</Descriptions.Item>}
            </Descriptions>
          </Card>
          <Card>
            <Tabs activeKey={tab} onChange={(k) => setParams({ tab: k }, { replace: true })} items={[
              { key: 'deliverables', label: `${t('tasks4.deliverables')} (${d.deliverables.length})`, children: <Deliverables card={d} onChange={refresh} /> },
              { key: 'log', label: t('tasks4.log'), children: <UpdatesLog updates={d.updates} canComment={cap.canComment} taskId={task.id} external={task.assigneeType === 'external_entity'} onChange={refresh} /> },
              { key: 'extensions', label: `${t('tasks4.extensions')} (${d.extensions.length})`, children: <Extensions card={d} onChange={refresh} /> },
              { key: 'escalations', label: `${t('tasks4.escalations')} (${d.escalations.length})`, children: <Escalations card={d} onChange={refresh} /> },
              { key: 'attachments', label: t('nav.attachments'), children: <TaskAttachments taskId={task.id} canUpload={cap.canSubmitDeliverables || cap.canManage || cap.canRecordExternal} canShare={cap.canShareDocuments} /> },
              { key: 'letters', label: `${t('tasks4.letters.title')} (${d.letters.length})`, children: <LettersTab card={d} /> },
            ]} />
          </Card>
        </Col>
        <Col xs={24} xl={8}>
          <Card size="small" title={t('tasks.progress')} style={{ marginBottom: 16 }}>
            <Progress type="dashboard" percent={task.progressPct} strokeColor="#006837" size={110} />
            {cap.canProgress && <ProgressForm taskId={task.id} current={task.progressPct} overdue={task.isOverdue} onChange={refresh} />}
          </Card>
          <Card size="small" title={t('tasks4.verification')} style={{ marginBottom: 16 }}>
            {task.completedAt ? (
              <Space direction="vertical" size={2}>
                <span><CheckOutlined style={{ color: '#006837' }} /> {t(`tasks4.completion.${task.completionMethod}`, { defaultValue: '' })}</span>
                {task.verifiedByName && <span>{t('tasks4.verifiedBy')}: <b>{task.verifiedByName}</b></span>}
                <span className="item-meta">{dt(task.completedAt)}</span>
              </Space>
            ) : <Typography.Text type="secondary">{task.verificationRequired ? t('tasks4.verificationPending') : t('tasks4.selfDeclaration')}</Typography.Text>}
            {d.reviews.length > 0 && (
              <Timeline style={{ marginTop: 12 }} items={d.reviews.map((r) => ({
                color: r.decision === 'accepted' ? 'green' : 'red',
                children: <div><b>{t(`tasks4.review.${r.decision}`)}</b> — {r.reviewer}<div className="item-meta">{dt(r.createdAt)}</div>{r.note && <div>{r.note}</div>}</div>,
              }))} />
            )}
          </Card>
          <Card size="small" title={t('tasks4.dates')}>
            <Descriptions size="small" column={1}>
              <Descriptions.Item label={t('tasks4.acceptedAt')}>{dt(task.acceptedAt)}</Descriptions.Item>
              <Descriptions.Item label={t('tasks4.startedAt')}>{dt(task.startedAt)}</Descriptions.Item>
              <Descriptions.Item label={t('tasks4.submittedAt')}>{dt(task.submittedAt)}</Descriptions.Item>
              <Descriptions.Item label={t('tasks4.completedAt')}>{dt(task.completedAt)}</Descriptions.Item>
            </Descriptions>
          </Card>
        </Col>
      </Row>
      {review && <ReviewModal card={d} mode={review} onClose={() => { setReview(null); refresh(); }} />}
      {modal === 'extension' && <ExtensionModal taskId={task.id} due={task.dueDate} onClose={() => { setModal(null); refresh(); }} />}
      {modal === 'external' && <ExternalUpdateModal card={d} onClose={() => { setModal(null); refresh(); }} />}
      {modal === 'escalate' && <EscalateModal taskId={task.id} onClose={() => { setModal(null); refresh(); }} />}
      {modal === 'letter' && <LetterCreateModal taskIds={[task.id]} onClose={() => { setModal(null); refresh(); }} />}
      {reason.node}
    </>
  );
}

function ProgressForm({ taskId, current, overdue, onChange }: { taskId: string; current: number; overdue: boolean; onChange: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post(`/api/v1/tasks/${taskId}/updates`, v),
    onSuccess: () => { form.resetFields(); message.success(t('common.saved')); onChange(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)} initialValues={{ progressPct: current }} style={{ marginTop: 8 }}>
      <Form.Item name="progressPct" label={t('tasks4.newProgress')} extra={t('tasks4.progressHelp')}><InputNumber min={0} max={99} addonAfter="%" /></Form.Item>
      <Form.Item name="note" label={t('tasks.note')} rules={[{ required: true, max: 4000 }]}><Input.TextArea rows={2} /></Form.Item>
      {overdue && <Form.Item name="delayReason" label={t('tasks4.delayReason')}><Input.TextArea rows={2} /></Form.Item>}
      <Button type="primary" htmlType="submit" loading={m.isPending}>{t('tasks.addUpdate')}</Button>
    </Form>
  );
}

const deliverableColor: Record<string, string> = { pending: 'default', submitted: 'blue', accepted: 'green', rejected: 'red' };

export function DeliverableList({ items, canSubmit, onSubmit, portal }: { items: TaskDeliverable[]; canSubmit: boolean; onSubmit: (d: TaskDeliverable) => void; portal?: boolean }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  if (items.length === 0) return <Empty description={t('tasks4.noDeliverables')} />;
  return (
    <Space direction="vertical" style={{ width: '100%' }}>
      {items.map((d) => (
        <Card key={d.id} size="small" className={`deliverable-card st-${d.status}`}
          title={<Space wrap><span>{d.position}. {d.title}</span>{d.required ? <Tag>{t('tasks4.requiredShort')}</Tag> : <Tag>{t('tasks4.optional')}</Tag>}<Tag color={deliverableColor[d.status]}>{t(`tasks4.deliverableStatus.${d.status}`)}</Tag></Space>}
          extra={canSubmit && d.status !== 'accepted' && <Button size="small" icon={<UploadOutlined />} onClick={() => onSubmit(d)}>{t('tasks4.submitDeliverable')}</Button>}>
          {d.acceptanceCriteria && <div><b>{t('tasks4.acceptanceCriteria')}:</b> {d.acceptanceCriteria}</div>}
          {d.reviewNote && <Alert type={d.status === 'rejected' ? 'error' : 'success'} showIcon style={{ marginTop: 8 }} message={d.reviewNote} description={!portal && d.reviewedBy ? `${d.reviewedBy} — ${formatDateTime(d.reviewedAt, i18n.language, me?.user.calendar)}` : undefined} />}
          {d.submissions.map((s) => (
            <div key={s.id} className="submission">
              <span className="item-meta">{s.submittedBy} · {formatDateTime(s.createdAt, i18n.language, me?.user.calendar)} · {t(`tasks4.channel.${s.channel}`)}</span>
              {s.note && <div>{s.note}</div>}
              <Space wrap>{s.documents.map((doc) => <a key={doc.id} href={`/api/v1/documents/${doc.id}/content`} target="_blank" rel="noreferrer"><FileTextOutlined /> {doc.fileName}</a>)}</Space>
            </div>
          ))}
        </Card>
      ))}
    </Space>
  );
}

function Deliverables({ card, onChange }: { card: TaskCard; onChange: () => void }) {
  const { t } = useTranslation();
  const [submitting, setSubmitting] = useState<TaskDeliverable | null>(null);
  const [adding, setAdding] = useState(false);
  return (
    <>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('tasks4.fileNotCompletion')} />
      <DeliverableList items={card.deliverables} canSubmit={card.capabilities.canSubmitDeliverables} onSubmit={setSubmitting} />
      {card.capabilities.canManage && !['done', 'closed'].includes(card.task.statusCategory) && <Button style={{ marginTop: 12 }} onClick={() => setAdding(true)}>{t('tasks4.addDeliverable')}</Button>}
      {submitting && <SubmitDeliverableModal url={`/api/v1/tasks/deliverables/${submitting.id}/submissions`} taskId={card.task.id} deliverable={submitting} onClose={() => { setSubmitting(null); onChange(); }} />}
      {adding && <AddDeliverableModal taskId={card.task.id} onClose={() => { setAdding(false); onChange(); }} />}
    </>
  );
}

function AddDeliverableModal({ taskId, onClose }: { taskId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post(`/api/v1/tasks/${taskId}/deliverables`, v),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('tasks4.addDeliverable')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={{ required: true }} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="title" label={t('tasks4.deliverableTitle')} rules={[{ required: true }]}><Input /></Form.Item>
        <Form.Item name="acceptanceCriteria" label={t('tasks4.acceptanceCriteria')}><Input.TextArea rows={2} /></Form.Item>
        <Form.Item name="required" label={t('tasks4.requiredShort')} valuePropName="checked"><Switch /></Form.Item>
      </Form>
    </Modal>
  );
}

/** تقديم ملفات لمخرج: يرفع الملفات مرفقاتٍ للمهمة ثم يسجل التقديم. لا يغير ذلك حالة المهمة. */
export function SubmitDeliverableModal({ url, taskId, deliverable, onClose }: { url: string; taskId: string; deliverable: TaskDeliverable; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [files, setFiles] = useState<File[]>([]);
  const [note, setNote] = useState('');
  const [key] = useState(newRequestKey);
  const m = useMutation({
    mutationFn: async () => {
      const ids: string[] = [];
      for (const f of files) {
        const fd = new FormData();
        fd.set('ownerType', 'task'); fd.set('ownerId', taskId); fd.set('purpose', 'deliverable'); fd.set('file', f);
        ids.push((await api<{ id: string }>('POST', '/api/v1/documents', fd)).id);
      }
      return postOnce(url, { documentIds: ids, note: note || undefined }, key);
    },
    onSuccess: () => { message.success(t('tasks4.deliverableSubmitted')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={`${t('tasks4.submitDeliverable')}: ${deliverable.title}`} onCancel={onClose} onOk={() => m.mutate()} confirmLoading={m.isPending}
      okButtonProps={{ disabled: files.length === 0 }} okText={t('tasks4.submitDeliverable')} cancelText={t('common.cancel')}>
      {deliverable.acceptanceCriteria && <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('tasks4.acceptanceCriteria')} description={deliverable.acceptanceCriteria} />}
      <Upload multiple beforeUpload={(f) => { setFiles((x) => [...x, f]); return false; }} onRemove={(f) => setFiles((x) => x.filter((y) => y.name !== f.name))}
        fileList={files.map((f) => ({ uid: f.name, name: f.name, status: 'done' as const }))}>
        <Button icon={<UploadOutlined />}>{t('attachments.upload')}</Button>
      </Upload>
      <Input.TextArea rows={3} style={{ marginTop: 12 }} placeholder={t('tasks.note')} value={note} onChange={(e) => setNote(e.target.value)} />
    </Modal>
  );
}

/** التحقق أو الإعادة: قرار لكل مخرج، ولا يُغلق قبل قبول المخرجات الإلزامية. */
function ReviewModal({ card, mode, onClose }: { card: TaskCard; mode: 'verify' | 'return'; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, any>) => post(`/api/v1/tasks/${card.task.id}/actions/${mode}`, {
      note: v.note, reason: v.reason,
      deliverableDecisions: card.deliverables.filter((d) => v[`d_${d.id}`]).map((d) => ({ deliverableId: d.id, decision: v[`d_${d.id}`], note: v[`n_${d.id}`] || undefined })),
    }),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const initial = Object.fromEntries(card.deliverables.filter((d) => d.status === 'submitted' || d.status === 'accepted').map((d) => [`d_${d.id}`, d.status === 'accepted' || mode === 'verify' ? 'accepted' : 'rejected']));
  return (
    <Modal open width={720} title={mode === 'verify' ? t('tasks4.verifyTitle') : t('tasks4.returnTitle')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending}
      okText={mode === 'verify' ? t('tasks4.verifyOk') : t('tasks4.returnOk')} okButtonProps={{ danger: mode === 'return' }} cancelText={t('common.cancel')}>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t(mode === 'verify' ? 'tasks4.verifyHelp' : 'tasks4.returnHelp')} />
      <Form form={form} layout="vertical" initialValues={initial} onFinish={(v) => m.mutate(v)}>
        {card.deliverables.map((d) => (
          <Card key={d.id} size="small" style={{ marginBottom: 8 }} title={`${d.position}. ${d.title}`} extra={d.required && <Tag>{t('tasks4.requiredShort')}</Tag>}>
            {d.acceptanceCriteria && <div className="item-meta" style={{ marginBottom: 6 }}>{t('tasks4.acceptanceCriteria')}: {d.acceptanceCriteria}</div>}
            <Space wrap align="start">
              <Form.Item name={`d_${d.id}`} noStyle><Radio.Group optionType="button" options={[{ value: 'accepted', label: t('tasks4.accept') }, { value: 'rejected', label: t('tasks4.rejectDeliverable') }]} disabled={d.status === 'pending'} /></Form.Item>
              <Form.Item noStyle shouldUpdate>{() => form.getFieldValue(`d_${d.id}`) === 'rejected' && <Form.Item name={`n_${d.id}`} noStyle rules={[{ required: true }]}><Input placeholder={t('tasks4.rejectWhy')} style={{ width: 300 }} /></Form.Item>}</Form.Item>
            </Space>
          </Card>
        ))}
        {mode === 'return'
          ? <Form.Item name="reason" label={t('common.reason')} rules={[{ required: true, min: 3 }]}><Input.TextArea rows={3} /></Form.Item>
          : <Form.Item name="note" label={t('tasks4.verifyNote')}><Input.TextArea rows={2} /></Form.Item>}
      </Form>
    </Modal>
  );
}

export function UpdatesLog({ updates, canComment, taskId, external, onChange, portal }: {
  updates: TaskUpdateEntry[]; canComment: boolean; taskId: string; external: boolean; onChange: () => void; portal?: boolean;
}) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const { message } = App.useApp();
  const [text, setText] = useState('');
  const [share, setShare] = useState(false);
  const [key, setKey] = useState(newRequestKey);
  const m = useMutation({
    mutationFn: () => postOnce(portal ? `/api/v1/portal/tasks/${taskId}/comments` : `/api/v1/tasks/${taskId}/comments`, { text, shareWithEntity: share }, key),
    onSuccess: () => { setText(''); setKey(newRequestKey()); message.success(t('common.saved')); onChange(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <>
      {canComment && (
        <Card size="small" style={{ marginBottom: 12 }}>
          <Input.TextArea rows={2} value={text} onChange={(e) => setText(e.target.value)} placeholder={portal ? t('tasks4.portalReply') : t('tasks4.commentPh')} />
          <Space style={{ marginTop: 8 }}>
            <Button type="primary" disabled={text.trim().length < 2} loading={m.isPending} onClick={() => m.mutate()}>{portal ? t('tasks4.sendReply') : t('tasks4.addComment')}</Button>
            {external && !portal && <Space><Switch size="small" checked={share} onChange={setShare} />{t('tasks4.shareWithEntity')}</Space>}
          </Space>
        </Card>
      )}
      <Timeline items={updates.map((u) => ({
        color: u.updateType === 'escalation' ? 'red' : u.updateType === 'status_change' ? 'blue' : u.channel === 'portal' ? 'purple' : u.channel === 'recorded' ? 'orange' : 'gray',
        children: (
          <div>
            <Space wrap size={6}>
              <b>{t(`tasks4.updateType.${u.updateType}`, { defaultValue: u.updateType })}</b>
              {u.newValue && typeof u.newValue.status === 'string' && <TaskStatusTag status={u.newValue.status} />}
              {u.progressPct != null && <Tag>{u.progressPct}%</Tag>}
              {u.channel !== 'system' && <Tag color={u.channel === 'portal' ? 'purple' : 'orange'}>{t(`tasks4.channel.${u.channel}`)}</Tag>}
              {!portal && u.visibility === 'shared' && external && <Tooltip title={t('tasks4.visibleToEntity')}><Tag>{t('tasks4.shared')}</Tag></Tooltip>}
            </Space>
            <div className="item-meta">{u.author ?? t('activity.system')}{u.authorIsExternal && ` (${t('tasks4.entityRep')})`} · {formatDateTime(u.createdAt, i18n.language, me?.user.calendar)}</div>
            {u.recordedForEntity && <div className="item-meta">{t('tasks4.recordedFor', { entity: u.recordedForEntity, channel: t(`tasks4.officialChannel.${u.officialChannel}`, { defaultValue: '' }), ref: u.reference ?? '—' })}</div>}
            {u.note && <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', margin: '2px 0' }}>{u.note}</Typography.Paragraph>}
            {u.delayReason && <div className="item-meta">{t('tasks4.delayReason')}: {u.delayReason}</div>}
          </div>
        ),
      }))} />
    </>
  );
}

function Extensions({ card, onChange }: { card: TaskCard; onChange: () => void }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const reason = useReasonPrompt();
  const decide = (id: string, decision: string, extra?: Record<string, unknown>) => post(`/api/v1/tasks/extension-requests/${id}/decision`, { decision, ...extra })
    .then(() => { message.success(t('common.saved')); onChange(); });
  return (
    <>
      <Table size="small" rowKey="id" pagination={false} dataSource={card.extensions} locale={{ emptyText: t('tasks4.noExtensions') }}
        columns={[
          { title: t('tasks4.requestedBy'), render: (_, x) => <Space direction="vertical" size={0}><span>{x.requestedBy}</span><span className="item-meta">{t(`tasks4.channel.${x.channel}`)}</span></Space> },
          { title: t('tasks4.dueChange'), render: (_, x) => <span>{dualDate(x.currentDueDate, i18n.language)} ← {dualDate(x.requestedDueDate, i18n.language)}</span> },
          { title: t('tasks4.delayReason'), render: (_, x) => <Space direction="vertical" size={0}><Tag>{t(`tasks4.delayCause.${x.delayCause}`)}</Tag><span>{x.reason}</span></Space> },
          {
            title: t('common.status'), render: (_, x) => (
              <Space direction="vertical" size={0}>
                <Tag color={x.status === 'approved' ? 'green' : x.status === 'rejected' ? 'red' : x.status === 'pending' ? 'gold' : 'default'}>{t(`tasks4.extensionStatus.${x.status}`)}</Tag>
                {x.decidedBy && <span className="item-meta">{x.decidedBy}{x.approvedDueDate && ` — ${dualDate(x.approvedDueDate, i18n.language)}`}</span>}
                {x.decisionNote && <span className="item-meta">{x.decisionNote}</span>}
              </Space>
            ),
          },
          {
            title: '', render: (_, x) => x.status === 'pending' && card.capabilities.canDecideExtension && (
              <Space>
                <Button size="small" type="primary" onClick={() => reason.prompt({
                  title: t('tasks4.approveExtension'), reasonRequired: false, initialValues: {},
                  extra: <Form.Item name="approvedDueDate" label={t('tasks4.approvedDue')} extra={t('tasks4.approvedDueHelp')}><DatePicker /></Form.Item>,
                  onSubmit: (v) => decide(x.id, 'approved', { note: v.reason || undefined, approvedDueDate: (v.approvedDueDate as Dayjs | undefined)?.format('YYYY-MM-DD') }),
                })}>{t('tasks4.approve')}</Button>
                <Button size="small" danger onClick={() => reason.prompt({ title: t('tasks4.rejectExtension'), danger: true, onSubmit: (v) => decide(x.id, 'rejected', { note: v.reason }) })}>{t('tasks4.reject')}</Button>
              </Space>
            ),
          },
        ]} />
      {reason.node}
    </>
  );
}

export function ExtensionModal({ taskId, due, onClose, portal }: { taskId: string; due?: string | null; onClose: () => void; portal?: boolean }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const [key] = useState(newRequestKey);
  const m = useMutation({
    mutationFn: (v: Record<string, any>) => postOnce(portal ? `/api/v1/portal/tasks/${taskId}/extension-requests` : `/api/v1/tasks/${taskId}/extension-requests`,
      { ...v, requestedDueDate: (v.requestedDueDate as Dayjs).format('YYYY-MM-DD') }, key),
    onSuccess: () => { message.success(t('tasks4.extensionSent')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('tasks4.requestExtension')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('tasks4.send')} cancelText={t('common.cancel')}>
      <Typography.Paragraph type="secondary">{t('tasks4.currentDue')}: {dualDate(due, i18n.language)}</Typography.Paragraph>
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)}>
        <Form.Item name="requestedDueDate" label={t('tasks4.requestedDue')} rules={[{ required: true }]}><DatePicker /></Form.Item>
        <Form.Item name="delayCause" label={t('tasks4.delayCauseLabel')} rules={[{ required: true }]}>
          <Select options={DELAY_CAUSES.map((c) => ({ value: c, label: t(`tasks4.delayCause.${c}`) }))} />
        </Form.Item>
        <Form.Item name="reason" label={t('tasks4.delayReason')} rules={[{ required: true, min: 5 }]}><Input.TextArea rows={3} /></Form.Item>
      </Form>
    </Modal>
  );
}

function Escalations({ card, onChange }: { card: TaskCard; onChange: () => void }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const reason = useReasonPrompt();
  if (card.escalations.length === 0) return <Empty description={t('tasks4.noEscalations')} />;
  return (
    <>
      <Timeline items={card.escalations.map((e) => ({
        color: e.kind === 'reminder' ? 'blue' : 'red',
        children: (
          <div>
            <Space wrap>
              <b>{t(`tasks4.escKind.${e.kind}`)}</b>
              {e.kind !== 'reminder' && <Tag color="volcano">{t('tasks4.escalationN', { n: e.level })}</Tag>}
              <span className="item-meta">{formatDateTime(e.createdAt, i18n.language, me?.user.calendar)}{e.createdBy && ` — ${e.createdBy}`}</span>
            </Space>
            {e.reason && <div>{e.reason}</div>}
            <div className="item-meta">{t('tasks4.receivedBy')}: {e.recipients.map((r) => `${r.name} (${t(`tasks4.recipient.${r.role}`)}${r.unit ? ` — ${r.unit}` : ''})`).join('، ') || '—'}</div>
            {e.acknowledgedAt
              ? <div className="ack"><CheckOutlined /> {t('tasks4.acknowledgedBy', { name: e.acknowledgedBy })}: {e.ackNote}</div>
              : e.canAcknowledge && <Button size="small" onClick={() => reason.prompt({
                title: t('tasks4.acknowledge'), description: t('tasks4.acknowledgeHelp'),
                onSubmit: (v) => post(`/api/v1/tasks/escalations/${e.id}/acknowledge`, { note: v.reason }).then(onChange),
              })}>{t('tasks4.acknowledge')}</Button>}
          </div>
        ),
      }))} />
      {reason.node}
    </>
  );
}

const RECIPIENTS = ['assignee', 'accountable', 'verifier', 'secretary', 'chair', 'vice_chair', 'assignee_org_unit_manager', 'governance_officer', 'executive'];

function EscalateModal({ taskId, onClose }: { taskId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post<{ level: number; recipients: number }>(`/api/v1/tasks/${taskId}/escalate`, v),
    onSuccess: (r) => { message.success(t('tasks4.escalated', { level: r.level, n: r.recipients })); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('tasks4.escalate')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('tasks4.escalate')} okButtonProps={{ danger: true }} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)}>
        <Form.Item name="notify" label={t('tasks4.escalateTo')} extra={t('tasks4.escalateToHelp')}>
          <Select mode="multiple" allowClear options={RECIPIENTS.map((r) => ({ value: r, label: t(`tasks4.recipient.${r}`) }))} />
        </Form.Item>
        <Form.Item name="reason" label={t('common.reason')} rules={[{ required: true, min: 5 }]}><Input.TextArea rows={3} /></Form.Item>
      </Form>
    </Modal>
  );
}

/** رد الجهة الخارجية الوارد عبر القنوات الرسمية يسجله مسؤول المتابعة بمرجعه، والبوابة معطلة أو مفعلة. */
function ExternalUpdateModal({ card, onClose }: { card: TaskCard; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const kind = Form.useWatch('kind', form) as string;
  const [files, setFiles] = useState<File[]>([]);
  const [key] = useState(newRequestKey);
  const m = useMutation({
    mutationFn: async (v: Record<string, any>) => {
      const ids: string[] = [];
      for (const f of files) {
        const fd = new FormData();
        fd.set('ownerType', 'task'); fd.set('ownerId', card.task.id); fd.set('purpose', 'deliverable'); fd.set('file', f); fd.set('shareWithEntity', 'true');
        ids.push((await api<{ id: string }>('POST', '/api/v1/documents', fd)).id);
      }
      return postOnce(`/api/v1/tasks/${card.task.id}/external-updates`, {
        ...v, receivedOn: (v.receivedOn as Dayjs).format('YYYY-MM-DD'), requestedDueDate: (v.requestedDueDate as Dayjs | undefined)?.format('YYYY-MM-DD'),
        documentIds: ids.length ? ids : undefined,
      }, key);
    },
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open width={640} title={t('tasks4.recordExternal')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('tasks4.recordExternalHelp')} />
      <Form form={form} layout="vertical" initialValues={{ kind: 'progress', officialChannel: 'letter' }} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="kind" label={t('tasks4.externalKind')}>
          <Select options={['acknowledged', 'declined', 'progress', 'deliverable', 'extension_request', 'comment'].map((k) => ({ value: k, label: t(`tasks4.externalKinds.${k}`) }))} />
        </Form.Item>
        <div className="form-row">
          <Form.Item name="officialChannel" label={t('tasks4.officialChannelLabel')} rules={[{ required: true }]}>
            <Select options={OFFICIAL_CHANNELS.map((c) => ({ value: c, label: t(`tasks4.officialChannel.${c}`) }))} />
          </Form.Item>
          <Form.Item name="reference" label={t('tasks4.reference')}><Input /></Form.Item>
          <Form.Item name="receivedOn" label={t('tasks4.receivedOn')} rules={[{ required: true }]}><DatePicker /></Form.Item>
        </div>
        {kind === 'progress' && <Form.Item name="progressPct" label={t('tasks4.newProgress')}><InputNumber min={0} max={99} addonAfter="%" /></Form.Item>}
        {kind === 'deliverable' && (
          <>
            <Form.Item name="deliverableId" label={t('tasks4.deliverableTitle')} rules={[{ required: true }]}>
              <Select options={card.deliverables.filter((d) => d.status !== 'accepted').map((d) => ({ value: d.id, label: d.title }))} />
            </Form.Item>
            <Upload multiple beforeUpload={(f) => { setFiles((x) => [...x, f]); return false; }} fileList={files.map((f) => ({ uid: f.name, name: f.name, status: 'done' as const }))}
              onRemove={(f) => setFiles((x) => x.filter((y) => y.name !== f.name))}><Button icon={<UploadOutlined />}>{t('attachments.upload')}</Button></Upload>
          </>
        )}
        {kind === 'extension_request' && (
          <div className="form-row">
            <Form.Item name="requestedDueDate" label={t('tasks4.requestedDue')} rules={[{ required: true }]}><DatePicker /></Form.Item>
            <Form.Item name="delayCause" label={t('tasks4.delayCauseLabel')} rules={[{ required: true }]}><Select options={DELAY_CAUSES.map((c) => ({ value: c, label: t(`tasks4.delayCause.${c}`) }))} /></Form.Item>
          </div>
        )}
        <Form.Item name="note" label={t('tasks4.externalNote')} rules={[{ required: true, min: 3 }]} style={{ marginTop: 12 }}><Input.TextArea rows={3} /></Form.Item>
      </Form>
    </Modal>
  );
}

function TaskAttachments({ taskId, canUpload, canShare }: { taskId: string; canUpload: boolean; canShare: boolean }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [share, setShare] = useState(false);
  const key = ['documents', 'task', taskId];
  const q = useQuery({ queryKey: key, queryFn: () => get<(Doc & { sharedWithEntity?: boolean })[]>('/api/v1/documents', { ownerType: 'task', ownerId: taskId }) });
  const upload = useMutation({
    mutationFn: (file: File) => {
      const fd = new FormData();
      fd.set('ownerType', 'task'); fd.set('ownerId', taskId); fd.set('purpose', 'attachment'); fd.set('file', file);
      if (share) fd.set('shareWithEntity', 'true');
      return api('POST', '/api/v1/documents', fd);
    },
    onSuccess: () => { message.success(t('attachments.uploaded')); void qc.invalidateQueries({ queryKey: key }); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const toggle = useMutation({
    mutationFn: ({ id, value }: { id: string; value: boolean }) => post(`/api/v1/documents/${id}/share`, { taskId, share: value }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: key }),
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <>
      {canUpload && (
        <Space style={{ marginBottom: 12 }} wrap>
          <Upload showUploadList={false} beforeUpload={(f) => { upload.mutate(f); return false; }}><Button icon={<UploadOutlined />} loading={upload.isPending}>{t('attachments.upload')}</Button></Upload>
          {canShare && <Space><Switch size="small" checked={share} onChange={setShare} />{t('tasks4.shareWithEntity')}</Space>}
        </Space>
      )}
      <Table size="small" rowKey="id" loading={q.isLoading} dataSource={q.data} pagination={false} locale={{ emptyText: t('attachments.none') }}
        columns={[
          { title: t('attachments.file'), render: (_, d) => <a href={`/api/v1/documents/${d.id}/content`} target="_blank" rel="noreferrer">{d.fileName}</a> },
          { title: t('attachments.purpose'), dataIndex: 'purpose', render: (v) => t(`enums.docPurpose.${v}`, { defaultValue: v }) },
          { title: t('attachments.by'), dataIndex: 'uploadedBy' },
          { title: t('common.date'), dataIndex: 'uploadedAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
          ...(canShare ? [{ title: t('tasks4.sharedWithEntity'), render: (_: unknown, d: Doc & { sharedWithEntity?: boolean }) => <Switch size="small" checked={!!d.sharedWithEntity} loading={toggle.isPending} onChange={(v) => toggle.mutate({ id: d.id, value: v })} /> }] : []),
        ]} />
    </>
  );
}

function LettersTab({ card }: { card: TaskCard }) {
  const { t, i18n } = useTranslation();
  if (card.letters.length === 0) return <Empty description={t('tasks4.letters.none')} />;
  return (
    <Table size="small" rowKey="id" pagination={false} dataSource={card.letters} columns={[
      { title: t('tasks4.letters.reference'), render: (_, l) => <Link to={`/tasks/letters/${l.id}`} className="mono">{l.referenceNo}</Link> },
      { title: t('tasks4.letters.subject'), dataIndex: 'subject' },
      { title: t('common.status'), dataIndex: 'status', render: (v) => <Tag color={v.startsWith('dispatched') ? 'green' : v === 'approved' ? 'blue' : v === 'cancelled' ? 'default' : 'gold'}>{t(`tasks4.letters.status.${v}`)}</Tag> },
      { title: t('tasks4.letters.outgoing'), render: (_, l) => l.outgoingNo ? <span><span className="mono">{l.outgoingNo}</span> — {dualDate(l.outgoingDate, i18n.language)}</span> : <span className="item-meta">{t('tasks4.letters.notSent')}</span> },
    ]} />
  );
}
