// خطاب التكليف: مسودة برقم مرجعي، ثم اعتماد من غير المعد، ثم إرسال عبر التكامل (إن كان جاهزًا فعليًا) أو تسجيل رقم الصادر يدويًا.
import { Alert, App, Button, Card, Col, DatePicker, Descriptions, Form, Input, Modal, Row, Select, Skeleton, Space, Steps, Switch, Tag, Typography } from 'antd';
import { CheckOutlined, FilePdfOutlined, SendOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router-dom';
import type { Dayjs } from 'dayjs';
import { get, newRequestKey, patch, post, postOnce } from '../../api/client';
import type { AssignmentLetter, Doc } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, PageHeader, errorText } from '../../components/common';
import { useReasonPrompt } from '../../components/governance';
import { dualDate, formatDateTime } from '../../lib/format';

export function LetterCreateModal({ taskIds, onClose }: { taskIds: string[]; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const nav = useNavigate();
  const [form] = Form.useForm();
  const [key] = useState(newRequestKey);
  const docs = useQuery({ queryKey: ['documents', 'task', taskIds[0]], queryFn: () => get<Doc[]>('/api/v1/documents', { ownerType: 'task', ownerId: taskIds[0] }) });
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => postOnce<AssignmentLetter>('/api/v1/tasks/letters', { ...v, taskIds }, key),
    onSuccess: (l) => { message.success(t('tasks4.letters.drafted')); onClose(); nav(`/tasks/letters/${l.id}`); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open width={640} title={t('tasks4.letters.create')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('tasks4.letters.createOk')} cancelText={t('common.cancel')}>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('tasks4.letters.createHelp')} />
      <Form form={form} layout="vertical" initialValues={{ shareWithEntity: true }} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="recipientAttention" label={t('tasks4.letters.attention')}><Input /></Form.Item>
        <Form.Item name="subject" label={t('tasks4.letters.subject')} extra={t('tasks4.letters.subjectHelp')}><Input /></Form.Item>
        <Form.Item name="attachmentIds" label={t('tasks4.letters.attachments')}>
          <Select mode="multiple" allowClear options={(docs.data ?? []).filter((d) => d.purpose !== 'assignment_letter').map((d) => ({ value: d.id, label: d.fileName }))} />
        </Form.Item>
        <Form.Item name="shareWithEntity" label={t('tasks4.letters.share')} valuePropName="checked" extra={t('tasks4.letters.shareHelp')}><Switch /></Form.Item>
      </Form>
    </Modal>
  );
}

const STEP: Record<string, number> = { draft: 0, approved: 1, dispatched_manual: 2, dispatched_integration: 2, cancelled: 0 };

export default function LetterPage() {
  const { id } = useParams();
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const reason = useReasonPrompt();
  const [dispatch, setDispatch] = useState(false);
  const [editing, setEditing] = useState(false);
  const q = useQuery({ queryKey: ['letter', id], queryFn: () => get<{ letter: AssignmentLetter; tasks: { id: string; referenceNo?: string | null; title: string; dueDate?: string | null }[]; canManage: boolean; correspondenceIntegrationEnabled: boolean }>(`/api/v1/tasks/letters/${id}`) });
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['letter', id] }); void qc.invalidateQueries({ queryKey: ['task'] }); };
  const act = useMutation({
    mutationFn: (path: string) => post(`/api/v1/tasks/letters/${id}/${path}`),
    onSuccess: () => { message.success(t('common.saved')); refresh(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  if (q.isLoading) return <Skeleton active />;
  if (q.error || !q.data) return <ErrorAlert error={q.error} />;
  const { letter: l, tasks, canManage, correspondenceIntegrationEnabled } = q.data;
  return (
    <>
      <PageHeader title={l.subject} subtitle={<Space wrap><span className="mono">{l.referenceNo}</span><Tag color={l.officiallySent ? 'green' : l.status === 'approved' ? 'blue' : l.status === 'cancelled' ? 'default' : 'gold'}>{t(`tasks4.letters.status.${l.status}`)}</Tag></Space>}
        extra={<>
          <Button icon={<FilePdfOutlined />} href={`/api/v1/tasks/letters/${l.id}/pdf`} target="_blank">PDF</Button>
          {canManage && l.status === 'draft' && <Button onClick={() => setEditing(true)}>{t('common.edit')}</Button>}
          {canManage && l.status === 'draft' && <Button type="primary" icon={<CheckOutlined />} loading={act.isPending} onClick={() => act.mutate('approve')}>{t('tasks4.letters.approve')}</Button>}
          {canManage && l.status === 'approved' && correspondenceIntegrationEnabled && <Button icon={<SendOutlined />} loading={act.isPending} onClick={() => act.mutate('send')}>{t('tasks4.letters.sendIntegration')}</Button>}
          {canManage && l.status === 'approved' && <Button type="primary" onClick={() => setDispatch(true)}>{t('tasks4.letters.recordDispatch')}</Button>}
          {canManage && ['draft', 'approved'].includes(l.status) && <Button danger onClick={() => reason.prompt({ title: t('tasks4.letters.cancel'), danger: true, onSubmit: (v) => post(`/api/v1/tasks/letters/${l.id}/cancel`, v).then(refresh) })}>{t('tasks4.letters.cancel')}</Button>}
        </>} />
      <Card size="small" style={{ marginBottom: 12 }}>
        <Steps size="small" current={STEP[l.status]} status={l.status === 'cancelled' ? 'error' : l.officiallySent ? 'finish' : 'process'}
          items={[{ title: t('tasks4.letters.status.draft') }, { title: t('tasks4.letters.status.approved') }, { title: t('tasks4.letters.sentStep') }]} />
      </Card>
      {!l.officiallySent && l.status === 'approved' && (
        <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('tasks4.letters.notSentYet')}
          description={correspondenceIntegrationEnabled ? t('tasks4.letters.integrationNote') : t('tasks4.letters.manualNote')} />
      )}
      <Row gutter={16}>
        <Col xs={24} lg={15}>
          <Card title={t('tasks4.letters.body')}>
            <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', fontSize: 15, lineHeight: 2 }}>{l.bodyText}</Typography.Paragraph>
          </Card>
        </Col>
        <Col xs={24} lg={9}>
          <Card size="small" title={t('tasks4.letters.details')} style={{ marginBottom: 12 }}>
            <Descriptions size="small" column={1}>
              <Descriptions.Item label={t('tasks4.letters.recipient')}>{l.recipientName}{l.recipientAttention && ` — ${l.recipientAttention}`}</Descriptions.Item>
              <Descriptions.Item label={t('common.dueDate')}>{dualDate(l.dueDate, i18n.language)}</Descriptions.Item>
              <Descriptions.Item label={t('tasks4.letters.preparedBy')}>{l.createdByName} — {formatDateTime(l.createdAt, i18n.language, me?.user.calendar)}</Descriptions.Item>
              <Descriptions.Item label={t('tasks4.letters.approvedBy')}>{l.approvedByName ? `${l.approvedByName} — ${formatDateTime(l.approvedAt, i18n.language, me?.user.calendar)}` : '—'}</Descriptions.Item>
              <Descriptions.Item label={t('tasks4.letters.outgoing')}>{l.outgoingNo ? <><span className="mono">{l.outgoingNo}</span> — {dualDate(l.outgoingDate, i18n.language)}</> : t('tasks4.letters.notSent')}</Descriptions.Item>
              {l.dispatchChannel && <Descriptions.Item label={t('tasks4.letters.channel')}>{t(`tasks4.letters.channels.${l.dispatchChannel}`)}</Descriptions.Item>}
              {l.resolutionId && <Descriptions.Item label={t('tasks4.letters.resolution')}><Link to={`/resolutions/${l.resolutionId}`}>{t('common.open')}</Link></Descriptions.Item>}
              {l.cancelReason && <Descriptions.Item label={t('common.reason')}>{l.cancelReason}</Descriptions.Item>}
            </Descriptions>
          </Card>
          <Card size="small" title={t('tasks4.letters.tasks')}>
            {tasks.map((x) => <div key={x.id}><Link to={`/tasks/${x.id}`}><span className="mono">{x.referenceNo}</span> — {x.title}</Link></div>)}
          </Card>
        </Col>
      </Row>
      {dispatch && <DispatchModal id={l.id} onClose={() => { setDispatch(false); refresh(); }} />}
      {editing && <EditLetterModal letter={l} onClose={() => { setEditing(false); refresh(); }} />}
      {reason.node}
    </>
  );
}

function DispatchModal({ id, onClose }: { id: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, any>) => post(`/api/v1/tasks/letters/${id}/record-dispatch`, { ...v, outgoingDate: (v.outgoingDate as Dayjs).format('YYYY-MM-DD') }),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('tasks4.letters.recordDispatch')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('tasks4.letters.recordDispatchHelp')} />
      <Form form={form} layout="vertical" initialValues={{ channel: 'correspondence_system_manual' }} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="channel" label={t('tasks4.letters.channel')}>
          <Select options={['correspondence_system_manual', 'hand_delivery', 'official_email', 'other'].map((c) => ({ value: c, label: t(`tasks4.letters.channels.${c}`) }))} />
        </Form.Item>
        <div className="form-row">
          <Form.Item name="outgoingNo" label={t('tasks4.letters.outgoingNo')} rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="outgoingDate" label={t('tasks4.letters.outgoingDate')} rules={[{ required: true }]}><DatePicker /></Form.Item>
        </div>
        <Form.Item name="note" label={t('common.notes')}><Input /></Form.Item>
      </Form>
    </Modal>
  );
}

function EditLetterModal({ letter, onClose }: { letter: AssignmentLetter; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => patch(`/api/v1/tasks/letters/${letter.id}`, v),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open width={760} title={t('common.edit')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={letter} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="subject" label={t('tasks4.letters.subject')} rules={[{ required: true }]}><Input /></Form.Item>
        <Form.Item name="recipientAttention" label={t('tasks4.letters.attention')}><Input /></Form.Item>
        <Form.Item name="bodyText" label={t('tasks4.letters.body')} rules={[{ required: true }]}><Input.TextArea autoSize={{ minRows: 8, maxRows: 20 }} /></Form.Item>
      </Form>
    </Modal>
  );
}
