// تهيئة دورة المهام: الحالات (الاسم واللون والترتيب وإضافة حالات تنفيذ أو انتظار) والانتقالات (الصلاحية والأطراف والسبب والتفعيل).
import { App, Button, Card, Form, Input, InputNumber, Modal, Select, Skeleton, Space, Switch, Table, Tag, Typography } from 'antd';
import { PlusOutlined } from '@ant-design/icons';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { post, put } from '../../api/client';
import type { WorkflowStatus, WorkflowTransition } from '../../api/types';
import { ErrorAlert, PageHeader, errorText } from '../../components/common';
import { TaskStatusTag, useWorkflow } from './shared';

const COLORS = ['default', 'blue', 'geekblue', 'processing', 'cyan', 'green', 'gold', 'orange', 'volcano', 'red', 'purple', 'magenta'];

export default function WorkflowPage() {
  const { t, i18n } = useTranslation();
  const q = useWorkflow();
  const [status, setStatus] = useState<WorkflowStatus | 'new' | null>(null);
  const [transition, setTransition] = useState<WorkflowTransition | 'new' | null>(null);
  if (q.isLoading) return <Skeleton active />;
  if (q.error || !q.data) return <ErrorAlert error={q.error} />;
  const wf = q.data;
  const nm = (x: { nameAr: string; nameEn: string }) => (i18n.language === 'en' ? x.nameEn : x.nameAr);
  return (
    <>
      <PageHeader title={t('tasks4.workflow.title')} subtitle={t('tasks4.workflow.subtitle')} />
      <Card title={t('tasks4.workflow.statuses')} style={{ marginBottom: 16 }}
        extra={wf.canManage && <Button icon={<PlusOutlined />} onClick={() => setStatus('new')}>{t('tasks4.workflow.addStatus')}</Button>}>
        <Table size="small" rowKey="code" pagination={false} dataSource={wf.statuses} columns={[
          { title: t('common.status'), render: (_, s) => <Tag color={s.color}>{nm(s)}</Tag> },
          { title: t('tasks4.workflow.code'), dataIndex: 'code', render: (v) => <span className="mono">{v}</span> },
          { title: t('tasks4.workflow.category'), dataIndex: 'category', render: (v) => t(`tasks4.workflow.categories.${v}`) },
          { title: t('tasks4.workflow.tasks'), dataIndex: 'tasks' },
          { title: '', render: (_, s) => <Space>{s.isSystem && <Tag>{t('tasks4.workflow.system')}</Tag>}{!s.active && <Tag color="red">{t('tasks4.workflow.inactive')}</Tag>}</Space> },
          { title: '', render: (_, s) => wf.canManage && s.category !== 'derived' && <Button size="small" onClick={() => setStatus(s)}>{t('common.edit')}</Button> },
        ]} />
        <Typography.Paragraph type="secondary" style={{ marginTop: 8 }}>{t('tasks4.workflow.overdueNote')}</Typography.Paragraph>
      </Card>
      <Card title={t('tasks4.workflow.transitions')} extra={wf.canManage && <Button icon={<PlusOutlined />} onClick={() => setTransition('new')}>{t('tasks4.workflow.addTransition')}</Button>}>
        <Table size="small" rowKey="id" pagination={false} dataSource={wf.transitions}
          rowClassName={(x) => (x.active ? '' : 'ended-row')}
          columns={[
            { title: t('tasks4.workflow.action'), render: (_, x) => <Space direction="vertical" size={0}><b>{nm(x)}</b><span className="mono item-meta">{x.action}</span></Space> },
            { title: t('tasks4.workflow.from'), render: (_, x) => <TaskStatusTag status={x.from} /> },
            { title: t('tasks4.workflow.to'), render: (_, x) => <TaskStatusTag status={x.to} /> },
            { title: t('tasks4.workflow.permission'), dataIndex: 'permission', render: (v) => t(`tasks4.workflow.perms.${v}`) },
            { title: t('tasks4.workflow.actors'), dataIndex: 'actors', render: (v: string[]) => v.length ? v.map((a) => <Tag key={a}>{t(`tasks4.workflow.actorNames.${a}`)}</Tag>) : <span className="item-meta">{t('tasks4.workflow.anyActor')}</span> },
            { title: t('tasks4.workflow.reason'), dataIndex: 'requiresReason', render: (v) => (v ? t('common.yes') : t('common.no')) },
            { title: '', render: (_, x) => wf.canManage && <Button size="small" onClick={() => setTransition(x)}>{t('common.edit')}</Button> },
          ]} />
        <Typography.Paragraph type="secondary" style={{ marginTop: 8 }}>{t('tasks4.workflow.guards')}</Typography.Paragraph>
      </Card>
      {status && <StatusModal status={status === 'new' ? undefined : status} onClose={() => setStatus(null)} />}
      {transition && <TransitionModal transition={transition === 'new' ? undefined : transition} statuses={wf.statuses} onClose={() => setTransition(null)} />}
    </>
  );
}

function StatusModal({ status, onClose }: { status?: WorkflowStatus; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => (status ? put(`/api/v1/tasks/workflow/statuses/${status.code}`, v) : post('/api/v1/tasks/workflow/statuses', v)),
    onSuccess: () => { message.success(t('common.saved')); void qc.invalidateQueries({ queryKey: ['task-workflow'] }); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={status ? t('common.edit') : t('tasks4.workflow.addStatus')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={status ?? { category: 'waiting', color: 'gold', sortOrder: 55, active: true }} onFinish={(v) => m.mutate(v)}>
        {!status && <Form.Item name="code" label={t('tasks4.workflow.code')} rules={[{ required: true, pattern: /^[a-z][a-z0-9_]{1,39}$/ }]}><Input dir="ltr" /></Form.Item>}
        <div className="form-row">
          <Form.Item name="nameAr" label={t('common.name')} rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="nameEn" label={t('templates.nameEn')} rules={[{ required: true }]}><Input dir="ltr" /></Form.Item>
        </div>
        <div className="form-row">
          {!status && <Form.Item name="category" label={t('tasks4.workflow.category')}><Select options={['active', 'waiting'].map((c) => ({ value: c, label: t(`tasks4.workflow.categories.${c}`) }))} /></Form.Item>}
          <Form.Item name="color" label={t('tasks4.workflow.color')}><Select options={COLORS.map((c) => ({ value: c, label: <Tag color={c}>{c}</Tag> }))} /></Form.Item>
          <Form.Item name="sortOrder" label={t('tasks4.workflow.order')}><InputNumber min={0} max={1000} /></Form.Item>
        </div>
        {status && !status.isSystem && <Form.Item name="active" label={t('tasks4.workflow.active')} valuePropName="checked"><Switch /></Form.Item>}
        <Form.Item name="reason" label={t('common.reason')} rules={[{ required: true, min: 5 }]}><Input /></Form.Item>
      </Form>
    </Modal>
  );
}

function TransitionModal({ transition, statuses, onClose }: { transition?: WorkflowTransition; statuses: WorkflowStatus[]; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => (transition ? put(`/api/v1/tasks/workflow/transitions/${transition.id}`, v) : post('/api/v1/tasks/workflow/transitions', v)),
    onSuccess: () => { message.success(t('common.saved')); void qc.invalidateQueries({ queryKey: ['task-workflow'] }); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const opts = statuses.filter((s) => s.category !== 'derived' && s.active).map((s) => ({ value: s.code, label: i18n.language === 'en' ? s.nameEn : s.nameAr }));
  return (
    <Modal open width={640} title={transition ? t('common.edit') : t('tasks4.workflow.addTransition')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={transition ?? { permission: 'task.update', actors: ['executor', 'follow_up'], requiresReason: false, active: true }} onFinish={(v) => m.mutate(v)}>
        {!transition && (
          <div className="form-row">
            <Form.Item name="action" label={t('tasks4.workflow.action')} rules={[{ required: true, pattern: /^[a-z][a-z0-9_]{1,39}$/ }]}><Input dir="ltr" /></Form.Item>
            <Form.Item name="from" label={t('tasks4.workflow.from')} rules={[{ required: true }]}><Select options={opts} /></Form.Item>
            <Form.Item name="to" label={t('tasks4.workflow.to')} rules={[{ required: true }]}><Select options={opts} /></Form.Item>
          </div>
        )}
        <div className="form-row">
          <Form.Item name="nameAr" label={t('common.name')} rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="nameEn" label={t('templates.nameEn')} rules={[{ required: true }]}><Input dir="ltr" /></Form.Item>
        </div>
        <Form.Item name="permission" label={t('tasks4.workflow.permission')}>
          <Select options={['task.update', 'task.manage', 'task.verify'].map((p) => ({ value: p, label: t(`tasks4.workflow.perms.${p}`) }))} />
        </Form.Item>
        <Form.Item name="actors" label={t('tasks4.workflow.actors')} extra={t('tasks4.workflow.actorsHelp')}>
          <Select mode="multiple" allowClear options={['executor', 'follow_up', 'verifier', 'manager'].map((a) => ({ value: a, label: t(`tasks4.workflow.actorNames.${a}`) }))} />
        </Form.Item>
        <Space size={24}>
          <Form.Item name="requiresReason" label={t('tasks4.workflow.reason')} valuePropName="checked"><Switch /></Form.Item>
          <Form.Item name="active" label={t('tasks4.workflow.active')} valuePropName="checked"><Switch /></Form.Item>
        </Space>
        <Form.Item name="reason" label={t('tasks4.workflow.changeReason')} rules={[{ required: true, min: 5 }]}><Input /></Form.Item>
      </Form>
    </Modal>
  );
}
