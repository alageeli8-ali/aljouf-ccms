import { App, Button, Card, DatePicker, Form, Input, Modal, Select, Table, Tag } from 'antd';
import { PlusOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router-dom';
import type { Dayjs } from 'dayjs';
import { get, post, type Paged } from '../api/client';
import type { Body } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../components/common';
import { EnumSelect } from '../components/pickers';

const TYPES = ['council', 'committee', 'sub_committee', 'working_team'];
const STATUSES = ['draft', 'active', 'suspended', 'dissolved', 'archived'];
const CLASSES = ['public', 'restricted', 'secret', 'top_secret'];

export default function BodiesPage() {
  const { t, i18n } = useTranslation();
  const { can } = useAuth();
  const [filters, setFilters] = useState<{ q?: string; type?: string; status?: string; page: number }>({ page: 1 });
  const [open, setOpen] = useState(false);
  const q = useQuery({ queryKey: ['bodies', filters], queryFn: () => get<Paged<Body>>('/api/v1/bodies', { ...filters, pageSize: 20 }) });
  return (
    <>
      <PageHeader
        title={t('bodies.title')}
        extra={can('bodies.create') && <Button type="primary" icon={<PlusOutlined />} onClick={() => setOpen(true)}>{t('bodies.new')}</Button>}
      />
      <Card>
        <div className="toolbar">
          <Input.Search placeholder={t('common.search')} allowClear style={{ width: 260 }} onSearch={(v) => setFilters((f) => ({ ...f, q: v, page: 1 }))} />
          <EnumSelect group="enums.bodyType" values={TYPES} placeholder={t('common.type')} allowClear value={filters.type} onChange={(v) => setFilters((f) => ({ ...f, type: v, page: 1 }))} />
          <EnumSelect group="enums.bodyStatus" values={STATUSES} placeholder={t('common.status')} allowClear value={filters.status} onChange={(v) => setFilters((f) => ({ ...f, status: v, page: 1 }))} />
        </div>
        <ErrorAlert error={q.error} />
        <Table<Body>
          rowKey="id" loading={q.isLoading} dataSource={q.data?.items}
          pagination={{ current: filters.page, total: q.data?.total, pageSize: 20, onChange: (page) => setFilters((f) => ({ ...f, page })) }}
          columns={[
            { title: t('common.name'), render: (_, b) => <Link to={`/bodies/${b.id}`}>{i18n.language === 'en' && b.nameEn ? b.nameEn : b.nameAr}</Link> },
            { title: t('common.code'), dataIndex: 'code', render: (v) => <span className="mono">{v}</span> },
            { title: t('common.type'), dataIndex: 'bodyType', render: (v) => t(`enums.bodyType.${v}`) },
            { title: t('bodies.chair'), dataIndex: 'chairName', render: (v) => v ?? '—' },
            { title: t('bodies.secretary'), dataIndex: 'secretaryName', render: (v) => v ?? '—' },
            { title: t('bodies.memberCount'), dataIndex: 'memberCount', width: 90 },
            { title: t('bodies.classification'), dataIndex: 'classification', render: (v) => <Tag>{t(`enums.classification.${v}`)}</Tag> },
            { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="bodyStatus" value={v} /> },
          ]}
        />
      </Card>
      {open && <CreateBodyModal onClose={() => setOpen(false)} />}
    </>
  );
}

function CreateBodyModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const nav = useNavigate();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post<Body>('/api/v1/bodies', v),
    onSuccess: (b) => {
      void qc.invalidateQueries({ queryKey: ['bodies'] });
      message.success(t('common.saved'));
      nav(`/bodies/${b.id}`);
    },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const d = (v?: Dayjs) => v?.format('YYYY-MM-DD');
  return (
    <Modal open title={t('bodies.new')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.create')} cancelText={t('common.cancel')} width={640}>
      <Form form={form} layout="vertical" initialValues={{ bodyType: 'committee', classification: 'restricted' }}
        onFinish={(v) => m.mutate({ ...v, formationDecisionDate: d(v.formationDecisionDate), termStart: d(v.termStart), termEnd: d(v.termEnd) })}>
        <Form.Item name="nameAr" label={t('common.nameAr')} rules={[{ required: true, max: 300 }]}><Input /></Form.Item>
        <Form.Item name="nameEn" label={t('common.nameEn')}><Input dir="ltr" /></Form.Item>
        <div style={{ display: 'flex', gap: 12 }}>
          <Form.Item name="code" label={t('common.code')} extra={t('bodies.codeHelp')} style={{ flex: 1 }}
            rules={[{ required: true, pattern: /^[A-Z][A-Z0-9-]{1,19}$/ }]}><Input dir="ltr" /></Form.Item>
          <Form.Item name="bodyType" label={t('common.type')} style={{ flex: 1 }}>
            <Select options={TYPES.map((v) => ({ value: v, label: t(`enums.bodyType.${v}`) }))} />
          </Form.Item>
          <Form.Item name="classification" label={t('bodies.classification')} style={{ flex: 1 }}>
            <Select options={CLASSES.map((v) => ({ value: v, label: t(`enums.classification.${v}`) }))} />
          </Form.Item>
        </div>
        <Form.Item name="mandateAr" label={t('bodies.mandate')}><Input.TextArea rows={3} /></Form.Item>
        <div style={{ display: 'flex', gap: 12 }}>
          <Form.Item name="formationDecisionNo" label={t('bodies.formationNo')} style={{ flex: 1 }}><Input /></Form.Item>
          <Form.Item name="formationDecisionDate" label={t('bodies.formationDate')} style={{ flex: 1 }}><DatePicker style={{ width: '100%' }} /></Form.Item>
        </div>
        <div style={{ display: 'flex', gap: 12 }}>
          <Form.Item name="termStart" label={t('bodies.termStart')} style={{ flex: 1 }}><DatePicker style={{ width: '100%' }} /></Form.Item>
          <Form.Item name="termEnd" label={t('bodies.termEnd')} style={{ flex: 1 }}
            rules={[({ getFieldValue }) => ({ validator: (_, v) => getFieldValue('bodyType') === 'working_team' && !v ? Promise.reject(new Error(t('bodies.termEnd'))) : Promise.resolve() })]}>
            <DatePicker style={{ width: '100%' }} />
          </Form.Item>
        </div>
      </Form>
    </Modal>
  );
}
