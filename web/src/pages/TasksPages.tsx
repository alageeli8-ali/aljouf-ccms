import { App, Button, Card, DatePicker, Descriptions, Drawer, Form, Input, InputNumber, Modal, Progress, Radio, Select, Space, Switch, Table, Tag, Timeline, Typography } from 'antd';
import { PlusOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useNavigate, useSearchParams } from 'react-router-dom';
import type { Dayjs } from 'dayjs';
import { get, post, type Paged } from '../api/client';
import type { Task, TaskUpdate } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../components/common';
import { BodySelect, EntitySelect, EnumSelect, OrgUnitSelect, UserSelect } from '../components/pickers';
import { dualDate, formatDateTime } from '../lib/format';

const STATUSES = ['assigned', 'in_progress', 'pending_verification', 'completed', 'returned', 'on_hold', 'cancelled'];

function useTaskColumns() {
  const { t, i18n } = useTranslation();
  return [
    { title: t('tasks.reference'), dataIndex: 'referenceNo', render: (v: string) => <span className="mono">{v ?? '—'}</span> },
    { title: t('common.title'), dataIndex: 'titleAr' },
    { title: t('common.body'), dataIndex: 'bodyName' },
    {
      title: t('tasks.assignee'),
      render: (_: unknown, r: Task) => <Space size={4}><Tag>{t(`enums.assigneeType.${r.assigneeType}`)}</Tag>{r.assigneeName}</Space>,
    },
    { title: t('common.dueDate'), render: (_: unknown, r: Task) => <span style={r.isOverdue ? { color: '#b42318', fontWeight: 600 } : undefined}>{dualDate(r.dueDate, i18n.language)}</span> },
    { title: t('tasks.progress'), dataIndex: 'progressPct', width: 130, render: (v: number) => <Progress percent={v} size="small" strokeColor="#006837" /> },
    {
      title: t('common.status'),
      render: (_: unknown, r: Task) => <Space size={4}><StatusTag group="taskStatus" value={r.status} />{r.isOverdue && <Tag color="red">{t('tasks.overdue')}</Tag>}</Space>,
    },
  ];
}

export function TasksPage() {
  const { t } = useTranslation();
  const { me } = useAuth();
  const nav = useNavigate();
  const { id } = useParams();
  const [params] = useSearchParams();
  const [filters, setFilters] = useState<{ q?: string; bodyId?: string; status?: string; overdue?: boolean; page: number }>({ page: 1, overdue: params.get('overdue') === 'true' || undefined });
  const [creating, setCreating] = useState(false);
  const q = useQuery({ queryKey: ['tasks', filters], queryFn: () => get<Paged<Task>>('/api/v1/tasks', { ...filters, pageSize: 20 }) });
  const columns = useTaskColumns();
  const canCreate = (me?.bodyRoles ?? []).some((r) => ['chair', 'secretary'].includes(r.role));
  return (
    <>
      <PageHeader title={t('tasks.title')} extra={canCreate && <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreating(true)}>{t('tasks.new')}</Button>} />
      <Card>
        <div className="toolbar">
          <Input.Search placeholder={t('common.search')} allowClear style={{ width: 240 }} onSearch={(v) => setFilters((f) => ({ ...f, q: v, page: 1 }))} />
          <BodySelect placeholder={t('common.body')} allowClear value={filters.bodyId} onChange={(v) => setFilters((f) => ({ ...f, bodyId: v, page: 1 }))} />
          <EnumSelect group="enums.taskStatus" values={STATUSES} placeholder={t('common.status')} allowClear value={filters.status} onChange={(v) => setFilters((f) => ({ ...f, status: v, page: 1 }))} />
          <Space><Switch checked={!!filters.overdue} onChange={(v) => setFilters((f) => ({ ...f, overdue: v || undefined, page: 1 }))} />{t('tasks.overdueOnly')}</Space>
        </div>
        <ErrorAlert error={q.error} />
        <Table<Task>
          rowKey="id" loading={q.isLoading} dataSource={q.data?.items} columns={columns}
          onRow={(r) => ({ onClick: () => nav(`/tasks/${r.id}`), style: { cursor: 'pointer' } })}
          pagination={{ current: filters.page, total: q.data?.total, pageSize: 20, onChange: (page) => setFilters((f) => ({ ...f, page })) }}
        />
      </Card>
      {creating && <CreateTaskModal onClose={() => setCreating(false)} />}
      {id && <TaskDrawer id={id} onClose={() => nav('/tasks')} />}
    </>
  );
}

export function PortalPage() {
  const { t } = useTranslation();
  const { me } = useAuth();
  const nav = useNavigate();
  const q = useQuery({ queryKey: ['portal'], queryFn: () => get<Task[]>('/api/v1/portal/tasks') });
  return (
    <>
      <PageHeader title={t('nav.portal')} subtitle={me?.user.externalEntityName} />
      <Card>
        <ErrorAlert error={q.error} />
        <Table<Task> rowKey="id" loading={q.isLoading} dataSource={q.data} columns={useTaskColumns()} pagination={false}
          onRow={(r) => ({ onClick: () => nav(`/tasks/${r.id}`), style: { cursor: 'pointer' } })} />
      </Card>
    </>
  );
}

function TaskDrawer({ id, onClose }: { id: string; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const { me } = useAuth();
  const qc = useQueryClient();
  const [form] = Form.useForm();
  const q = useQuery({ queryKey: ['task', id], queryFn: () => get<{ task: Task; updates: TaskUpdate[]; permissions: string[] }>(`/api/v1/tasks/${id}`) });
  const update = useMutation({
    mutationFn: (v: { progressPct?: number; note: string }) => post(`/api/v1/tasks/${id}/updates`, v),
    onSuccess: () => {
      form.resetFields();
      void qc.invalidateQueries({ queryKey: ['task', id] });
      void qc.invalidateQueries({ queryKey: ['tasks'] });
      message.success(t('common.saved'));
    },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const task = q.data?.task;
  const canUpdate = q.data?.permissions.includes('task.update') && task && ['assigned', 'in_progress', 'returned'].includes(task.status);
  return (
    <Drawer open width={560} onClose={onClose} title={task?.titleAr ?? t('common.loading')} placement={i18n.language === 'ar' ? 'left' : 'right'}>
      <ErrorAlert error={q.error} />
      {task && (
        <>
          <Descriptions column={1} size="small" bordered>
            <Descriptions.Item label={t('tasks.reference')}><span className="mono">{task.referenceNo}</span></Descriptions.Item>
            <Descriptions.Item label={t('common.status')}><StatusTag group="taskStatus" value={task.status} /></Descriptions.Item>
            <Descriptions.Item label={t('common.body')}>{task.bodyName}</Descriptions.Item>
            <Descriptions.Item label={t('tasks.assignee')}>{t(`enums.assigneeType.${task.assigneeType}`)} — {task.assigneeName}</Descriptions.Item>
            <Descriptions.Item label={t('tasks.accountable')}>{task.accountableName ?? '—'}</Descriptions.Item>
            <Descriptions.Item label={t('common.dueDate')}>{dualDate(task.dueDate, i18n.language)}</Descriptions.Item>
            <Descriptions.Item label={t('tasks.priority')}>{t(`enums.priority.${task.priority}`)}</Descriptions.Item>
            <Descriptions.Item label={t('tasks.progress')}><Progress percent={task.progressPct} size="small" strokeColor="#006837" /></Descriptions.Item>
            {task.description && <Descriptions.Item label={t('common.details')}>{task.description}</Descriptions.Item>}
          </Descriptions>
          {canUpdate && (
            <Card size="small" title={t('tasks.addUpdate')} style={{ marginTop: 16 }}>
              <Form form={form} layout="vertical" onFinish={(v) => update.mutate(v)}>
                <Form.Item name="progressPct" label={t('tasks.progress')}><InputNumber min={0} max={100} addonAfter="%" /></Form.Item>
                <Form.Item name="note" label={t('tasks.note')} rules={[{ required: true, max: 4000 }]}><Input.TextArea rows={3} /></Form.Item>
                <Button type="primary" htmlType="submit" loading={update.isPending}>{t('common.save')}</Button>
              </Form>
            </Card>
          )}
          <Typography.Title level={5} style={{ marginTop: 20 }}>{t('tasks.timeline')}</Typography.Title>
          <Timeline items={(q.data?.updates ?? []).map((u) => ({
            children: <><Typography.Text strong>{u.author}</Typography.Text> · {formatDateTime(u.createdAt, i18n.language, me?.user.calendar)}{u.progressPct != null && ` · ${u.progressPct}%`}<div>{u.note}</div></>,
          }))} />
        </>
      )}
    </Drawer>
  );
}

function CreateTaskModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [form] = Form.useForm();
  const type = Form.useWatch('assigneeType', form);
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post('/api/v1/tasks', v),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['tasks'] }); message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('tasks.new')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.create')} cancelText={t('common.cancel')} width={620}>
      <Form form={form} layout="vertical" initialValues={{ assigneeType: 'user', priority: 'normal', verificationRequired: true }}
        onFinish={(v) => m.mutate({ ...v, dueDate: (v.dueDate as Dayjs).format('YYYY-MM-DD') })}>
        <Form.Item name="bodyId" label={t('common.body')} rules={[{ required: true }]}><BodySelect activeOnly /></Form.Item>
        <Form.Item name="titleAr" label={t('common.title')} rules={[{ required: true, max: 300 }]}><Input /></Form.Item>
        <Form.Item name="description" label={t('common.details')}><Input.TextArea rows={2} /></Form.Item>
        <Form.Item name="assigneeType" label={t('tasks.assigneeType')}>
          <Radio.Group optionType="button" options={['user', 'org_unit', 'external_entity'].map((v) => ({ value: v, label: t(`enums.assigneeType.${v}`) }))} />
        </Form.Item>
        {type === 'user' && <Form.Item name="assigneeUserId" label={t('tasks.assignee')} rules={[{ required: true }]}><UserSelect /></Form.Item>}
        {type === 'org_unit' && <Form.Item name="assigneeOrgUnitId" label={t('tasks.orgUnit')} rules={[{ required: true }]}><OrgUnitSelect /></Form.Item>}
        {type === 'external_entity' && <Form.Item name="assigneeExternalEntityId" label={t('tasks.entity')} rules={[{ required: true }]}><EntitySelect /></Form.Item>}
        {type !== 'user' && <Form.Item name="accountableUserId" label={t('tasks.accountable')} extra={t('tasks.accountableHelp')}><UserSelect allowClear /></Form.Item>}
        <Space wrap>
          <Form.Item name="dueDate" label={t('common.dueDate')} rules={[{ required: true }]}><DatePicker /></Form.Item>
          <Form.Item name="priority" label={t('tasks.priority')}>
            <Select style={{ width: 140 }} options={['low', 'normal', 'high', 'critical'].map((v) => ({ value: v, label: t(`enums.priority.${v}`) }))} />
          </Form.Item>
        </Space>
      </Form>
    </Modal>
  );
}
