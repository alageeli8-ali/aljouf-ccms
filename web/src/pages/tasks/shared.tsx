// عناصر مشتركة لصفحات المهام: وسم الحالة (من الحالات المهيأة)، والأولوية، وخلية الاستحقاق، ونموذج إنشاء المهمة.
import { Alert, App, Button, Col, DatePicker, Divider, Form, Input, Modal, Radio, Row, Select, Space, Switch, Tag, Tooltip } from 'antd';
import { DeleteOutlined, PlusOutlined, WarningOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Dayjs } from 'dayjs';
import { get, newRequestKey, postOnce } from '../../api/client';
import type { TaskWorkflow } from '../../api/types';
import { errorText } from '../../components/common';
import { BodySelect, EntitySelect, OrgUnitSelect, UserSelect } from '../../components/pickers';
import { dualDate } from '../../lib/format';

export function useWorkflow() {
  return useQuery({ queryKey: ['task-workflow'], queryFn: () => get<TaskWorkflow>('/api/v1/tasks/workflow'), staleTime: 60_000 });
}

/** وسم الحالة باسمها ولونها كما هُيئت؛ و«متأخرة» مؤشر مشتق يظهر بجانبها. */
export function TaskStatusTag({ status, nameAr, nameEn, color, overdue, days }: {
  status: string; nameAr?: string; nameEn?: string; color?: string; overdue?: boolean; days?: number | null;
}) {
  const { t, i18n } = useTranslation();
  const wf = useWorkflow();
  const s = wf.data?.statuses.find((x) => x.code === status);
  const label = i18n.language === 'en' ? nameEn ?? s?.nameEn ?? status : nameAr ?? s?.nameAr ?? status;
  return (
    <Space size={4} wrap>
      <Tag color={color ?? s?.color ?? 'default'}>{label}</Tag>
      {overdue && <Tag color="red" icon={<WarningOutlined />}>{t('tasks4.overdueDays', { n: days ?? 0 })}</Tag>}
    </Space>
  );
}

const priorityColor: Record<string, string> = { low: 'default', normal: 'blue', high: 'orange', critical: 'red' };
export function PriorityTag({ value }: { value: string }) {
  const { t } = useTranslation();
  return <Tag color={priorityColor[value]}>{t(`enums.priority.${value}`)}</Tag>;
}

export function DueCell({ due, original, overdue }: { due?: string | null; original?: string | null; overdue?: boolean }) {
  const { t, i18n } = useTranslation();
  if (!due) return <span>—</span>;
  return (
    <Tooltip title={original && original !== due ? `${t('tasks4.originalDue')}: ${dualDate(original, i18n.language)}` : undefined}>
      <span className={overdue ? 'due-overdue' : undefined}>{dualDate(due, i18n.language)}{original && original !== due && ' *'}</span>
    </Tooltip>
  );
}

export const ASSIGNEE_TYPES = ['member', 'employee', 'org_unit', 'external_entity'];
export const PRIORITIES = ['low', 'normal', 'high', 'critical'];

interface Deliverable { title: string; acceptanceCriteria?: string; required: boolean }

/** إنشاء مهمة: من قرار أو بند أو يدويًا؛ مع المنفذ والمتابع المنفصل والمخرجات ومعايير قبولها. */
export function CreateTaskModal({ onClose, bodyId, resolutionId, resolutionTitle, agendaItemId }: {
  onClose: () => void; bodyId?: string; resolutionId?: string; resolutionTitle?: string; agendaItemId?: string;
}) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [form] = Form.useForm();
  const type = Form.useWatch('assigneeType', form) as string;
  const [key] = useState(newRequestKey);
  const [deliverables, setDeliverables] = useState<Deliverable[]>([]);
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => postOnce('/api/v1/tasks', {
      ...v, bodyId: bodyId ?? v.bodyId, resolutionId, agendaItemId, deliverables: deliverables.filter((d) => d.title.trim()),
    }, key),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['tasks'] }); message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const set = (i: number, patch: Partial<Deliverable>) => setDeliverables((d) => d.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  return (
    <Modal open width={820} title={t('tasks.new')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.create')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={{ assigneeType: 'employee', priority: 'normal', verificationRequired: true }}
        onFinish={(v) => m.mutate({ ...v, dueDate: (v.dueDate as Dayjs).format('YYYY-MM-DD') })}>
        {resolutionTitle && <Alert type="info" showIcon style={{ marginBottom: 12 }} message={`${t('tasks.fromResolution')}: ${resolutionTitle}`} description={t('tasks.draftUntilApproval')} />}
        {!bodyId && <Form.Item name="bodyId" label={t('common.body')} rules={[{ required: true }]}><BodySelect activeOnly /></Form.Item>}
        <Form.Item name="titleAr" label={t('tasks4.required')} rules={[{ required: true, max: 300 }]}><Input /></Form.Item>
        <Form.Item name="description" label={t('common.details')}><Input.TextArea rows={2} /></Form.Item>
        <Divider orientation="left" plain>{t('tasks4.executorSection')}</Divider>
        <Form.Item name="assigneeType" label={t('tasks.assigneeType')}>
          <Radio.Group optionType="button" options={ASSIGNEE_TYPES.map((v) => ({ value: v, label: t(`enums.assigneeType.${v}`) }))} />
        </Form.Item>
        <Row gutter={12}>
          <Col xs={24} md={12}>
            {(type === 'member' || type === 'employee') && <Form.Item name="assigneeUserId" label={t('tasks4.executor')} rules={[{ required: true }]}><UserSelect /></Form.Item>}
            {type === 'org_unit' && <Form.Item name="assigneeOrgUnitId" label={t('tasks.orgUnit')} rules={[{ required: true }]}><OrgUnitSelect /></Form.Item>}
            {type === 'external_entity' && <Form.Item name="assigneeExternalEntityId" label={t('tasks.entity')} rules={[{ required: true }]}><EntitySelect /></Form.Item>}
          </Col>
          <Col xs={24} md={12}>
            {type === 'org_unit' && <Form.Item name="executorUserId" label={t('tasks4.executorPerson')} extra={t('tasks4.executorPersonHelp')}><UserSelect allowClear /></Form.Item>}
            {type === 'external_entity' && <Form.Item name="executorContact" label={t('tasks4.executorContact')}><Input /></Form.Item>}
          </Col>
        </Row>
        <Row gutter={12}>
          <Col xs={24} md={12}>
            <Form.Item name="accountableUserId" label={t('tasks4.followUp')} extra={t('tasks4.followUpHelp')}><UserSelect allowClear /></Form.Item>
          </Col>
          <Col xs={24} md={12}>
            <Form.Item name="verifierUserId" label={t('tasks4.verifier')} extra={t('tasks4.verifierHelp')}><UserSelect allowClear /></Form.Item>
          </Col>
        </Row>
        <Space wrap size={16}>
          <Form.Item name="dueDate" label={t('common.dueDate')} rules={[{ required: true }]}><DatePicker /></Form.Item>
          <Form.Item name="priority" label={t('tasks.priority')}>
            <Select style={{ width: 140 }} options={PRIORITIES.map((v) => ({ value: v, label: t(`enums.priority.${v}`) }))} />
          </Form.Item>
          <Form.Item name="verificationRequired" label={t('tasks4.verificationRequired')} valuePropName="checked" extra={t('tasks4.verificationHelp')}>
            <Switch disabled={type === 'external_entity' || deliverables.length > 0} />
          </Form.Item>
        </Space>
        <Divider orientation="left" plain>{t('tasks4.deliverables')}</Divider>
        <Form.Item name="acceptanceCriteria" label={t('tasks4.acceptanceCriteria')}><Input.TextArea rows={2} /></Form.Item>
        {deliverables.map((d, i) => (
          <Row gutter={8} key={i} align="middle" style={{ marginBottom: 8 }}>
            <Col flex="1 1 200px"><Input placeholder={t('tasks4.deliverableTitle')} value={d.title} onChange={(e) => set(i, { title: e.target.value })} /></Col>
            <Col flex="1 1 260px"><Input placeholder={t('tasks4.acceptanceCriteria')} value={d.acceptanceCriteria} onChange={(e) => set(i, { acceptanceCriteria: e.target.value })} /></Col>
            <Col><Switch checked={d.required} onChange={(v) => set(i, { required: v })} checkedChildren={t('tasks4.requiredShort')} unCheckedChildren={t('tasks4.optional')} /></Col>
            <Col><Button type="text" danger icon={<DeleteOutlined />} onClick={() => setDeliverables((x) => x.filter((_, j) => j !== i))} aria-label={t('common.delete')} /></Col>
          </Row>
        ))}
        <Button icon={<PlusOutlined />} onClick={() => setDeliverables((x) => [...x, { title: '', required: true }])}>{t('tasks4.addDeliverable')}</Button>
        {type === 'external_entity' && (
          <>
            <Divider orientation="left" plain>{t('tasks4.forEntity')}</Divider>
            <Form.Item name="externalBrief" label={t('tasks4.externalBrief')} extra={t('tasks4.externalBriefHelp')}><Input.TextArea rows={2} /></Form.Item>
            {resolutionId && <Form.Item name="shareResolutionWithEntity" label={t('tasks4.shareResolution')} valuePropName="checked"><Switch /></Form.Item>}
          </>
        )}
      </Form>
    </Modal>
  );
}
