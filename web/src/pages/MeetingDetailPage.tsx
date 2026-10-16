import { App, Button, Card, Descriptions, Form, Input, InputNumber, Modal, Select, Skeleton, Space, Table } from 'antd';
import { CalendarOutlined, FileAddOutlined, FileTextOutlined, PlusOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { get, post } from '../api/client';
import type { AgendaItem, Meeting, Minutes } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../components/common';
import { formatDateTime, formatDate } from '../lib/format';

export default function MeetingDetailPage() {
  const { id } = useParams();
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const { me } = useAuth();
  const nav = useNavigate();
  const qc = useQueryClient();
  const [adding, setAdding] = useState(false);
  const q = useQuery({ queryKey: ['meeting', id], queryFn: () => get<{ meeting: Meeting; agenda: AgendaItem[]; permissions: string[] }>(`/api/v1/meetings/${id}`) });
  const refresh = () => void qc.invalidateQueries({ queryKey: ['meeting', id] });
  const schedule = useMutation({
    mutationFn: () => post(`/api/v1/meetings/${id}/schedule`),
    onSuccess: () => { message.success(t('meetings.scheduled')); refresh(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const createMinutes = useMutation({
    mutationFn: () => post<Minutes>(`/api/v1/meetings/${id}/minutes`),
    onSuccess: (m) => nav(`/minutes/${m.id}`),
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });

  if (q.isLoading) return <Skeleton active />;
  if (q.error) return <ErrorAlert error={q.error} />;
  const { meeting: m, agenda, permissions } = q.data!;
  const manage = permissions.includes('meeting.manage');
  const editable = ['draft', 'scheduled', 'agenda_published'].includes(m.status);

  return (
    <>
      <PageHeader
        title={m.titleAr}
        subtitle={<Space><span className="mono">{m.referenceNo ?? '—'}</span><StatusTag group="meetingStatus" value={m.status} /><Link to={`/bodies/${m.bodyId}`}>{m.bodyName}</Link></Space>}
        extra={<>
          {manage && m.status === 'draft' && <Button type="primary" icon={<CalendarOutlined />} loading={schedule.isPending} onClick={() => schedule.mutate()}>{t('meetings.schedule')}</Button>}
          {m.minutesId
            ? <Button icon={<FileTextOutlined />} onClick={() => nav(`/minutes/${m.minutesId}`)}>{t('meetings.openMinutes')}</Button>
            : permissions.includes('minutes.edit') && m.status !== 'draft' && m.status !== 'cancelled' &&
              <Button icon={<FileAddOutlined />} loading={createMinutes.isPending} onClick={() => createMinutes.mutate()}>{t('meetings.createMinutes')}</Button>}
        </>}
      />
      <Card style={{ marginBottom: 16 }}>
        <Descriptions size="small" column={{ xs: 1, md: 3 }}>
          <Descriptions.Item label={t('meetings.startsAt')}>{formatDateTime(m.startsAt, i18n.language, me?.user.calendar)}</Descriptions.Item>
          <Descriptions.Item label={t('common.hijri')}>{formatDate(m.startsAt, i18n.language, 'hijri')}</Descriptions.Item>
          <Descriptions.Item label={t('meetings.mode')}>{t(`enums.mode.${m.mode}`)}</Descriptions.Item>
          <Descriptions.Item label={t('meetings.location')}>{m.location ?? '—'}</Descriptions.Item>
          <Descriptions.Item label={t('common.type')}>{t(`enums.meetingType.${m.meetingType}`)}</Descriptions.Item>
        </Descriptions>
      </Card>
      <Card title={t('meetings.agenda')} extra={manage && editable && <Button icon={<PlusOutlined />} onClick={() => setAdding(true)}>{t('meetings.addItem')}</Button>}>
        <Table<AgendaItem>
          rowKey="id" dataSource={agenda} pagination={false}
          expandable={{ expandedRowRender: (a) => a.memoText ?? '—', rowExpandable: (a) => !!a.memoText }}
          columns={[
            { title: '#', dataIndex: 'position', width: 50 },
            { title: t('common.title'), dataIndex: 'titleAr' },
            { title: t('meetings.itemType'), dataIndex: 'itemType', render: (v) => t(`enums.itemType.${v}`) },
            { title: t('meetings.presenter'), dataIndex: 'presenterText', render: (v) => v ?? '—' },
            { title: t('meetings.duration'), dataIndex: 'estimatedMinutes', render: (v) => v ?? '—' },
          ]}
        />
      </Card>
      {adding && <AgendaModal meetingId={id!} onClose={() => { setAdding(false); refresh(); }} />}
    </>
  );
}

function AgendaModal({ meetingId, onClose }: { meetingId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post(`/api/v1/meetings/${meetingId}/agenda-items`, v),
    onSuccess: onClose,
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('meetings.addItem')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.add')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={{ itemType: 'for_decision' }} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="titleAr" label={t('common.title')} rules={[{ required: true, max: 400 }]}><Input /></Form.Item>
        <Space>
          <Form.Item name="itemType" label={t('meetings.itemType')}>
            <Select style={{ width: 160 }} options={['for_decision', 'for_information', 'for_discussion'].map((v) => ({ value: v, label: t(`enums.itemType.${v}`) }))} />
          </Form.Item>
          <Form.Item name="estimatedMinutes" label={t('meetings.duration')}><InputNumber min={1} max={480} /></Form.Item>
        </Space>
        <Form.Item name="presenterText" label={t('meetings.presenter')}><Input /></Form.Item>
        <Form.Item name="memoText" label={t('meetings.memo')}><Input.TextArea rows={4} /></Form.Item>
      </Form>
    </Modal>
  );
}
