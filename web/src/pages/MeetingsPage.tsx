import { App, Button, Card, DatePicker, Form, Input, InputNumber, Modal, Select, Table } from 'antd';
import { PlusOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router-dom';
import type { Dayjs } from 'dayjs';
import { get, post, type Paged } from '../api/client';
import type { Meeting } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../components/common';
import { BodySelect, EnumSelect } from '../components/pickers';
import { formatDateTime } from '../lib/format';

const STATUSES = ['draft', 'scheduled', 'agenda_published', 'in_progress', 'concluded', 'closed', 'postponed', 'cancelled', 'quorum_failed'];

export default function MeetingsPage() {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const [filters, setFilters] = useState<{ q?: string; bodyId?: string; status?: string; page: number }>({ page: 1 });
  const [open, setOpen] = useState(false);
  const q = useQuery({ queryKey: ['meetings', filters], queryFn: () => get<Paged<Meeting>>('/api/v1/meetings', { ...filters, pageSize: 20 }) });
  const canCreate = (me?.bodyRoles ?? []).some((r) => ['chair', 'vice_chair', 'secretary'].includes(r.role));
  return (
    <>
      <PageHeader title={t('meetings.title')} extra={canCreate && <Button type="primary" icon={<PlusOutlined />} onClick={() => setOpen(true)}>{t('meetings.new')}</Button>} />
      <Card>
        <div className="toolbar">
          <Input.Search placeholder={t('common.search')} allowClear style={{ width: 240 }} onSearch={(v) => setFilters((f) => ({ ...f, q: v, page: 1 }))} />
          <BodySelect placeholder={t('common.body')} allowClear value={filters.bodyId} onChange={(v) => setFilters((f) => ({ ...f, bodyId: v, page: 1 }))} />
          <EnumSelect group="enums.meetingStatus" values={STATUSES} placeholder={t('common.status')} allowClear value={filters.status} onChange={(v) => setFilters((f) => ({ ...f, status: v, page: 1 }))} />
        </div>
        <ErrorAlert error={q.error} />
        <Table<Meeting>
          rowKey="id" loading={q.isLoading} dataSource={q.data?.items}
          pagination={{ current: filters.page, total: q.data?.total, pageSize: 20, onChange: (page) => setFilters((f) => ({ ...f, page })) }}
          columns={[
            { title: t('meetings.reference'), dataIndex: 'referenceNo', render: (v) => <span className="mono">{v ?? '—'}</span> },
            { title: t('common.title'), render: (_, m) => <Link to={`/meetings/${m.id}`}>{m.titleAr}</Link> },
            { title: t('common.body'), dataIndex: 'bodyName' },
            { title: t('meetings.startsAt'), dataIndex: 'startsAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
            { title: t('meetings.mode'), dataIndex: 'mode', render: (v) => t(`enums.mode.${v}`) },
            { title: t('meetings.agendaCount'), dataIndex: 'agendaCount', width: 80 },
            { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="meetingStatus" value={v} /> },
          ]}
        />
      </Card>
      {open && <CreateMeetingModal onClose={() => setOpen(false)} />}
    </>
  );
}

function CreateMeetingModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const nav = useNavigate();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post<Meeting>('/api/v1/meetings', v),
    onSuccess: (meeting) => {
      void qc.invalidateQueries({ queryKey: ['meetings'] });
      nav(`/meetings/${meeting.id}`);
    },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('meetings.new')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.create')} cancelText={t('common.cancel')} width={600}>
      <Form form={form} layout="vertical" initialValues={{ meetingType: 'regular', mode: 'in_person', durationMinutes: 60 }}
        onFinish={(v) => m.mutate({ ...v, startsAt: (v.startsAt as Dayjs).toISOString() })}>
        <Form.Item name="bodyId" label={t('common.body')} rules={[{ required: true }]}><BodySelect activeOnly /></Form.Item>
        <Form.Item name="titleAr" label={t('common.title')} rules={[{ required: true, max: 300 }]}><Input /></Form.Item>
        <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
          <Form.Item name="startsAt" label={t('meetings.startsAt')} rules={[{ required: true }]}><DatePicker showTime={{ format: 'HH:mm' }} format="YYYY-MM-DD HH:mm" /></Form.Item>
          <Form.Item name="durationMinutes" label={t('meetings.duration')}><InputNumber min={15} max={720} step={15} /></Form.Item>
          <Form.Item name="meetingType" label={t('common.type')}>
            <Select style={{ width: 140 }} options={['regular', 'extraordinary'].map((v) => ({ value: v, label: t(`enums.meetingType.${v}`) }))} />
          </Form.Item>
          <Form.Item name="mode" label={t('meetings.mode')}>
            <Select style={{ width: 140 }} options={['in_person', 'remote', 'hybrid'].map((v) => ({ value: v, label: t(`enums.mode.${v}`) }))} />
          </Form.Item>
        </div>
        <Form.Item name="location" label={t('meetings.location')}><Input /></Form.Item>
      </Form>
    </Modal>
  );
}
