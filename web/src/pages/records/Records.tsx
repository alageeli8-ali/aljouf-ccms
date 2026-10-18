// إدارة السجلات (الوثيقة 23 §3): سجل المستندات، وقواعد الاحتفاظ، والأرشفة، والحجز القانوني، وطلبات الإتلاف بمعتمد مختلف وشهادة.
import { App, Button, Card, Checkbox, Form, Input, InputNumber, Modal, Popconfirm, Select, Space, Switch, Table, Tabs, Tag, Tooltip, Typography } from 'antd';
import { DeleteOutlined, FilePdfOutlined, LockOutlined, SafetyOutlined, InboxOutlined, EditOutlined, CheckOutlined, CloseOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { get, post, put } from '../../api/client';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, PageHeader, errorText } from '../../components/common';
import { formatDateTime } from '../../lib/format';

interface RegisterItem {
  id: string; fileName: string; versionNo: number; recordsClass: string | null; recordsClassName: string; finalAction: string; lifecycleStatus: string;
  retentionUntil: string | null; legalHold: boolean; locked: boolean; classification: string; uploadedAt: string; sizeBytes: number; usage: string;
}
interface Rule {
  code: string; nameAr: string; nameEn: string; purposes: string[]; retentionYears: number; archiveAfterYears: number; finalAction: string; legalBasis: string | null;
  active: boolean; version: number; updatedAt: string; documents: number;
}
interface DestructionReq {
  id: string; referenceNo: string; documents: number; reason: string; status: string; requestedBy: string; requestedAt: string; decidedBy: string | null;
  decidedAt: string | null; decisionNote: string | null; executedAt: string | null; requestedById: string;
}

const lifeColor: Record<string, string> = { active: 'green', archived: 'blue', destruction_pending: 'orange', destroyed: 'default' };

function Register() {
  const { t, i18n } = useTranslation();
  const { message, modal } = App.useApp();
  const { me, can } = useAuth();
  const qc = useQueryClient();
  const [filters, setFilters] = useState<{ due?: boolean; status?: string; recordsClass?: string; q?: string; page: number }>({ page: 1 });
  const [selected, setSelected] = useState<string[]>([]);
  const q = useQuery({ queryKey: ['records', 'register', filters], queryFn: () => get<{ total: number; items: RegisterItem[] }>('/api/v1/records/register', filters) });
  const rules = useQuery({ queryKey: ['records', 'rules'], queryFn: () => get<Rule[]>('/api/v1/records/retention-rules') });
  const manage = can('records.manage');
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['records'] }); setSelected([]); };
  const act = useMutation({ mutationFn: (fn: () => Promise<unknown>) => fn(), onSuccess: () => { message.success(t('common.saved')); refresh(); }, onError: (e) => message.error(errorText(e, t('common.error'))) });
  const prompt = (title: string, label: string, min: number, run: (v: string) => Promise<unknown>) => {
    let value = '';
    modal.confirm({
      title, icon: null, content: <Input.TextArea autoFocus rows={3} placeholder={label} onChange={(e) => { value = e.target.value; }} />,
      onOk: () => (value.trim().length < min ? Promise.reject(message.error(label)) : act.mutateAsync(() => run(value.trim()))),
    });
  };
  return (
    <Card>
      <Space wrap style={{ marginBottom: 12 }}>
        <Input.Search allowClear placeholder={t('records.col.file')} style={{ width: 240 }} onSearch={(v) => setFilters({ ...filters, q: v || undefined, page: 1 })} />
        <Select allowClear placeholder={t('records.col.lifecycle')} style={{ width: 170 }} onChange={(v) => setFilters({ ...filters, status: v, page: 1 })}
          options={['active', 'archived', 'destruction_pending', 'destroyed'].map((s) => ({ value: s, label: t(`records.lifecycle.${s}`) }))} />
        <Select allowClear placeholder={t('records.col.class')} style={{ width: 220 }} onChange={(v) => setFilters({ ...filters, recordsClass: v, page: 1 })}
          options={(rules.data ?? []).map((r) => ({ value: r.code, label: i18n.language === 'en' ? r.nameEn : r.nameAr }))} />
        <Checkbox onChange={(e) => setFilters({ ...filters, due: e.target.checked || undefined, page: 1 })}>{t('records.dueOnly')}</Checkbox>
        {manage && <Button icon={<InboxOutlined />} onClick={() => act.mutate(async () => { const r = await post<{ archived: number }>('/api/v1/records/archive-due'); message.info(t('records.archivedCount', { n: r.archived })); })}>{t('records.applyArchive')}</Button>}
        {manage && <Button danger icon={<DeleteOutlined />} disabled={!selected.length}
          onClick={() => prompt(t('records.actions.destroy'), t('records.destroyReason'), 10, (reason) => post('/api/v1/records/destruction-requests', { documentIds: selected, reason }))}>
          {t('records.actions.destroy')} ({selected.length})</Button>}
      </Space>
      <ErrorAlert error={q.error ?? act.error} />
      <Table<RegisterItem> rowKey="id" size="small" loading={q.isLoading} dataSource={q.data?.items} scroll={{ x: 1100 }}
        rowSelection={manage ? { selectedRowKeys: selected, onChange: (k) => setSelected(k as string[]), getCheckboxProps: (r) => ({ disabled: r.lifecycleStatus === 'destroyed' || r.lifecycleStatus === 'destruction_pending' }) } : undefined}
        pagination={{ current: filters.page, total: q.data?.total, pageSize: 50, onChange: (p) => setFilters({ ...filters, page: p }) }}
        columns={[
          { title: t('records.col.file'), render: (_, r) => <Space size={4}>{r.locked && <Tooltip title={t('docs.lockedHint')}><LockOutlined style={{ color: '#B28655' }} /></Tooltip>}<span>{r.fileName}</span></Space> },
          { title: t('records.col.version'), dataIndex: 'versionNo', align: 'center', width: 70 },
          { title: t('records.col.class'), render: (_, r) => r.recordsClass ? <span>{r.recordsClassName} <Tag bordered={false}>{t(`records.finalAction.${r.finalAction}`)}</Tag></span> : '—' },
          { title: t('records.col.lifecycle'), dataIndex: 'lifecycleStatus', render: (v) => <Tag color={lifeColor[v]}>{t(`records.lifecycle.${v}`)}</Tag> },
          { title: t('records.col.retentionUntil'), dataIndex: 'retentionUntil', render: (v) => v ? <span className="mono">{v}</span> : '—' },
          { title: t('records.col.hold'), dataIndex: 'legalHold', align: 'center', render: (v) => v ? <Tag color="red" icon={<SafetyOutlined />}>{t('records.actions.hold')}</Tag> : '—' },
          { title: t('records.col.usage'), dataIndex: 'usage', render: (v: string) => <span className="item-meta mono">{v}</span> },
          { title: t('common.date'), dataIndex: 'uploadedAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
          ...(manage ? [{
            title: '', width: 190, render: (_: unknown, r: RegisterItem) => r.lifecycleStatus === 'destroyed' ? null : (
              <Space size={4}>
                {r.lifecycleStatus === 'active' && <Button size="small" onClick={() => act.mutate(() => post(`/api/v1/documents/${r.id}/archive`, { reason: null }))}>{t('records.actions.archive')}</Button>}
                {r.legalHold
                  ? <Button size="small" onClick={() => act.mutate(() => post(`/api/v1/documents/${r.id}/legal-hold`, { hold: false }))}>{t('records.actions.release')}</Button>
                  : <Button size="small" danger onClick={() => prompt(t('records.actions.hold'), t('records.holdReason'), 3, (reason) => post(`/api/v1/documents/${r.id}/legal-hold`, { hold: true, reason }))}>{t('records.actions.hold')}</Button>}
              </Space>
            ),
          }] : []),
        ]} />
    </Card>
  );
}

function Rules() {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const { can } = useAuth();
  const qc = useQueryClient();
  const [editing, setEditing] = useState<Rule | null>(null);
  const [form] = Form.useForm();
  const q = useQuery({ queryKey: ['records', 'rules'], queryFn: () => get<Rule[]>('/api/v1/records/retention-rules') });
  const save = useMutation({
    mutationFn: (v: Record<string, unknown>) => put(`/api/v1/records/retention-rules/${editing!.code}`, v),
    onSuccess: () => { message.success(t('records.ruleSaved')); setEditing(null); void qc.invalidateQueries({ queryKey: ['records'] }); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Card>
      <ErrorAlert error={q.error} />
      <Table<Rule> rowKey="code" size="small" loading={q.isLoading} dataSource={q.data} pagination={false} scroll={{ x: 1000 }}
        columns={[
          { title: t('records.col.name'), render: (_, r) => <Space direction="vertical" size={0}><b>{i18n.language === 'en' ? r.nameEn : r.nameAr}</b><span className="mono item-meta">{r.code}</span></Space> },
          { title: t('records.col.purposes'), dataIndex: 'purposes', render: (v: string[]) => <Space size={[2, 2]} wrap>{v.map((p) => <Tag key={p} bordered={false}>{t(`enums.docPurpose.${p}`, { defaultValue: p })}</Tag>)}</Space> },
          { title: t('records.col.retentionYears'), dataIndex: 'retentionYears', align: 'center', render: (v, r) => r.finalAction === 'permanent' ? '∞' : v },
          { title: t('records.col.archiveAfter'), dataIndex: 'archiveAfterYears', align: 'center' },
          { title: t('records.col.final'), dataIndex: 'finalAction', render: (v) => <Tag color={v === 'permanent' ? 'green' : v === 'destroy' ? 'red' : 'gold'}>{t(`records.finalAction.${v}`)}</Tag> },
          { title: t('records.col.basis'), dataIndex: 'legalBasis', render: (v) => <span className="item-meta">{v ?? '—'}</span> },
          { title: t('records.col.documents'), dataIndex: 'documents', align: 'center' },
          { title: t('records.col.ruleVersion'), dataIndex: 'version', align: 'center' },
          ...(can('records.manage') ? [{ title: '', width: 80, render: (_: unknown, r: Rule) => <Button size="small" icon={<EditOutlined />} onClick={() => { setEditing(r); form.setFieldsValue(r); }}>{t('records.actions.edit')}</Button> }] : []),
        ]} />
      <Modal open={!!editing} title={editing?.nameAr} onCancel={() => setEditing(null)} onOk={() => form.submit()} confirmLoading={save.isPending} destroyOnClose>
        <Form form={form} layout="vertical" onFinish={(v) => save.mutate(v)}>
          <Form.Item name="nameAr" label="الاسم (عربي)" rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="nameEn" label="Name (English)" rules={[{ required: true }]}><Input dir="ltr" /></Form.Item>
          <Space>
            <Form.Item name="retentionYears" label={t('records.col.retentionYears')}><InputNumber min={0} max={100} /></Form.Item>
            <Form.Item name="archiveAfterYears" label={t('records.col.archiveAfter')}><InputNumber min={0} max={100} /></Form.Item>
          </Space>
          <Form.Item name="finalAction" label={t('records.col.final')}><Select options={['permanent', 'destroy', 'review'].map((v) => ({ value: v, label: t(`records.finalAction.${v}`) }))} /></Form.Item>
          <Form.Item name="legalBasis" label={t('records.col.basis')}><Input.TextArea rows={2} /></Form.Item>
          <Form.Item name="active" valuePropName="checked"><Switch /></Form.Item>
        </Form>
      </Modal>
    </Card>
  );
}

function Destruction() {
  const { t, i18n } = useTranslation();
  const { message, modal } = App.useApp();
  const { me, can } = useAuth();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['records', 'destruction'], queryFn: () => get<DestructionReq[]>('/api/v1/records/destruction-requests') });
  const act = useMutation({ mutationFn: (fn: () => Promise<unknown>) => fn(), onSuccess: () => { message.success(t('common.saved')); void qc.invalidateQueries({ queryKey: ['records'] }); }, onError: (e) => message.error(errorText(e, t('common.error'))) });
  const decide = (r: DestructionReq, approve: boolean) => {
    let note = '';
    modal.confirm({
      title: approve ? t('records.actions.approve') : t('records.actions.reject'), icon: null,
      content: <Input.TextArea rows={3} placeholder={t('records.decisionNote')} onChange={(e) => { note = e.target.value; }} />,
      onOk: () => act.mutateAsync(() => post(`/api/v1/records/destruction-requests/${r.id}/decide`, { approve, note: note || null })),
    });
  };
  const statusColor: Record<string, string> = { pending: 'gold', approved: 'blue', rejected: 'default', executed: 'red' };
  return (
    <Card>
      <Typography.Paragraph type="secondary">{t('records.fourEyes')}</Typography.Paragraph>
      <ErrorAlert error={q.error} />
      <Table<DestructionReq> rowKey="id" size="small" loading={q.isLoading} dataSource={q.data} pagination={{ pageSize: 20, hideOnSinglePage: true }} scroll={{ x: 1000 }}
        columns={[
          { title: t('records.col.reference'), dataIndex: 'referenceNo', render: (v) => <span className="mono">{v}</span> },
          { title: t('records.col.documents'), dataIndex: 'documents', align: 'center' },
          { title: t('records.col.reason'), dataIndex: 'reason' },
          { title: t('records.col.status'), dataIndex: 'status', render: (v) => <Tag color={statusColor[v]}>{t(`records.requestStatus.${v}`)}</Tag> },
          { title: t('records.col.requestedBy'), render: (_, r) => <Space direction="vertical" size={0}><span>{r.requestedBy}</span><span className="item-meta">{formatDateTime(r.requestedAt, i18n.language, me?.user.calendar)}</span></Space> },
          { title: t('records.col.decidedBy'), render: (_, r) => r.decidedBy ? <Space direction="vertical" size={0}><span>{r.decidedBy}</span><span className="item-meta">{r.decisionNote ?? ''}</span></Space> : '—' },
          {
            title: '', width: 260, render: (_, r) => (
              <Space size={4} wrap>
                {r.status === 'pending' && can('records.destruction.approve') && r.requestedById !== me?.user.id && <>
                  <Button size="small" type="primary" icon={<CheckOutlined />} onClick={() => decide(r, true)}>{t('records.actions.approve')}</Button>
                  <Button size="small" icon={<CloseOutlined />} onClick={() => decide(r, false)}>{t('records.actions.reject')}</Button>
                </>}
                {r.status === 'approved' && can('records.manage') && (
                  <Popconfirm title={t('records.executeConfirm')} onConfirm={() => act.mutate(() => post(`/api/v1/records/destruction-requests/${r.id}/execute`))}>
                    <Button size="small" danger icon={<DeleteOutlined />}>{t('records.actions.execute')}</Button>
                  </Popconfirm>
                )}
                {r.status === 'executed' && <Button size="small" icon={<FilePdfOutlined />} href={`/api/v1/records/destruction-requests/${r.id}/certificate?format=pdf`} target="_blank">{t('records.actions.certificate')}</Button>}
              </Space>
            ),
          },
        ]} />
    </Card>
  );
}

export function RecordsPage() {
  const { t } = useTranslation();
  return (
    <>
      <PageHeader title={t('records.title')} subtitle={t('records.subtitle')} />
      <Tabs items={[
        { key: 'register', label: t('records.tabs.register'), children: <Register /> },
        { key: 'rules', label: t('records.tabs.rules'), children: <Rules /> },
        { key: 'destruction', label: t('records.tabs.destruction'), children: <Destruction /> },
      ]} />
    </>
  );
}
