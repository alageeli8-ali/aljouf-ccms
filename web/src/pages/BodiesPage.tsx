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
import { displayName } from '../components/governance';
import { BodySelect, EntitySelect, EnumSelect, OrgUnitSelect } from '../components/pickers';
import { formatDate } from '../lib/format';

export const BODY_TYPES = ['supreme_council', 'steering_council', 'executive_council', 'permanent_committee', 'temporary_committee', 'working_team', 'sub_committee', 'joint_committee'];
export const BODY_STATUSES = ['draft', 'active', 'suspended', 'dissolved', 'archived'];
export const CLASSES = ['public', 'restricted', 'secret', 'top_secret'];
export const FREQUENCIES = ['weekly', 'biweekly', 'monthly', 'quarterly', 'semiannual', 'annual', 'as_needed'];

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
        subtitle={t('bodies.subtitle')}
        extra={can('bodies.create') && <Button type="primary" icon={<PlusOutlined />} onClick={() => setOpen(true)}>{t('bodies.new')}</Button>}
      />
      <Card>
        <div className="toolbar">
          <Input.Search placeholder={t('bodies.searchHint')} allowClear style={{ width: 280 }} onSearch={(v) => setFilters((f) => ({ ...f, q: v, page: 1 }))} />
          <EnumSelect group="enums.bodyType" values={BODY_TYPES} placeholder={t('common.type')} allowClear value={filters.type} style={{ minWidth: 200 }}
            onChange={(v) => setFilters((f) => ({ ...f, type: v, page: 1 }))} />
          <EnumSelect group="enums.bodyStatus" values={BODY_STATUSES} placeholder={t('common.status')} allowClear value={filters.status}
            onChange={(v) => setFilters((f) => ({ ...f, status: v, page: 1 }))} />
        </div>
        <ErrorAlert error={q.error} />
        <Table<Body>
          rowKey="id" loading={q.isLoading} dataSource={q.data?.items} scroll={{ x: 1100 }}
          pagination={{ current: filters.page, total: q.data?.total, pageSize: 20, onChange: (page) => setFilters((f) => ({ ...f, page })) }}
          columns={[
            { title: t('bodies.referenceNo'), dataIndex: 'referenceNo', width: 140, render: (v) => <span className="mono">{v}</span> },
            {
              title: t('common.name'),
              render: (_, b) => (
                <div>
                  <Link to={`/bodies/${b.id}`}>{displayName(i18n.language, b.nameAr, b.nameEn)}</Link>
                  {b.parentName && <div style={{ fontSize: 12, color: '#8c8c8c' }}>{t('bodies.parent')}: {b.parentName}</div>}
                </div>
              ),
            },
            { title: t('common.type'), dataIndex: 'bodyType', render: (v) => <Tag color={v.includes('council') ? 'green' : v === 'working_team' ? 'gold' : 'blue'}>{t(`enums.bodyType.${v}`)}</Tag> },
            { title: t('bodies.chair'), dataIndex: 'chairName', render: (v) => v ?? '—' },
            { title: t('bodies.secretary'), dataIndex: 'secretaryName', render: (v) => v ?? '—' },
            { title: t('bodies.memberCount'), dataIndex: 'memberCount', width: 90 },
            { title: t('bodies.nextMeeting'), dataIndex: 'nextMeetingAt', render: (v) => formatDate(v, i18n.language) },
            { title: t('bodies.termEnd'), dataIndex: 'termEnd', render: (v) => formatDate(v, i18n.language) },
            { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="bodyStatus" value={v} /> },
          ]}
        />
      </Card>
      {open && <CreateBodyModal onClose={() => setOpen(false)} />}
    </>
  );
}

const d = (v?: Dayjs | null) => v?.format('YYYY-MM-DD');

function CreateBodyModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const nav = useNavigate();
  const [form] = Form.useForm();
  const type = Form.useWatch('bodyType', form) as string | undefined;
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post<Body>('/api/v1/bodies', v),
    onSuccess: (b) => {
      void qc.invalidateQueries({ queryKey: ['bodies'] });
      message.success(t('bodies.createdRef', { ref: b.referenceNo }));
      nav(`/bodies/${b.id}`);
    },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const needsEnd = type === 'temporary_committee' || type === 'working_team';
  return (
    <Modal open title={t('bodies.new')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.create')} cancelText={t('common.cancel')} width={760}>
      <Form form={form} layout="vertical" initialValues={{ bodyType: 'permanent_committee', classification: 'restricted', meetingFrequency: 'monthly' }}
        onFinish={(v) => m.mutate({ ...v, formationDecisionDate: d(v.formationDecisionDate), termStart: d(v.termStart), termEnd: d(v.termEnd) })}>
        <div className="form-row">
          <Form.Item name="bodyType" label={t('common.type')} rules={[{ required: true }]}>
            <Select options={BODY_TYPES.map((v) => ({ value: v, label: t(`enums.bodyType.${v}`) }))} />
          </Form.Item>
          <Form.Item name="code" label={t('common.code')} extra={t('bodies.codeHelp')} rules={[{ required: true, pattern: /^[A-Z][A-Z0-9-]{1,19}$/, message: t('bodies.codeHelp') }]}>
            <Input dir="ltr" />
          </Form.Item>
          <Form.Item name="classification" label={t('bodies.classification')}>
            <Select options={CLASSES.map((v) => ({ value: v, label: t(`enums.classification.${v}`) }))} />
          </Form.Item>
        </div>
        <div className="form-row">
          <Form.Item name="nameAr" label={t('common.nameAr')} rules={[{ required: true, max: 300 }]}><Input /></Form.Item>
          <Form.Item name="nameEn" label={t('common.nameEn')} rules={[{ required: true, max: 300 }]}><Input dir="ltr" /></Form.Item>
        </div>
        {type === 'sub_committee' && (
          <Form.Item name="parentBodyId" label={t('bodies.parent')} rules={[{ required: true }]} extra={t('bodies.parentHelp')}><BodySelect /></Form.Item>
        )}
        {type === 'joint_committee' && (
          <Form.Item name="partnerEntityIds" label={t('bodies.partners')} rules={[{ required: true }]}><EntitySelect mode="multiple" style={{ width: '100%' }} /></Form.Item>
        )}
        <Form.Item name="objectiveAr" label={t('bodies.objective')}><Input.TextArea rows={2} maxLength={3000} /></Form.Item>
        <Form.Item name="mandateAr" label={t('bodies.mandate')}><Input.TextArea rows={3} maxLength={10000} /></Form.Item>
        <div className="form-row">
          <Form.Item name="authorityRef" label={t('bodies.authorityRef')}><Input /></Form.Item>
          <Form.Item name="ownerOrgUnitId" label={t('bodies.ownerUnit')}><OrgUnitSelect /></Form.Item>
        </div>
        <div className="form-row">
          <Form.Item name="formationDecisionNo" label={t('bodies.formationNo')}><Input /></Form.Item>
          <Form.Item name="formationDecisionDate" label={t('bodies.formationDate')}><DatePicker style={{ width: '100%' }} /></Form.Item>
          <Form.Item name="meetingFrequency" label={t('bodies.frequency')}>
            <Select options={FREQUENCIES.map((v) => ({ value: v, label: t(`enums.frequency.${v}`) }))} />
          </Form.Item>
        </div>
        <div className="form-row">
          <Form.Item name="termStart" label={t('bodies.termStart')}><DatePicker style={{ width: '100%' }} /></Form.Item>
          <Form.Item name="termEnd" label={t('bodies.termEnd')} rules={[{ required: needsEnd, message: t('bodies.termEndRequired') }]}>
            <DatePicker style={{ width: '100%' }} />
          </Form.Item>
        </div>
        <p style={{ color: '#8c8c8c', margin: 0 }}>{t('bodies.createHint')}</p>
      </Form>
    </Modal>
  );
}
