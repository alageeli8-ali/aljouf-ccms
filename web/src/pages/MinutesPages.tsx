import { Alert, App, Button, Card, Form, Input, Modal, Select, Skeleton, Space, Table, Tabs, Typography } from 'antd';
import { CheckOutlined, PlusOutlined, SaveOutlined, SendOutlined, DeleteOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router-dom';
import { get, post, put, type Paged } from '../api/client';
import type { Minutes, MinutesSection, Resolution, SignOff } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../components/common';
import { BodySelect, EnumSelect } from '../components/pickers';
import { formatDateTime } from '../lib/format';

const STATUSES = ['draft', 'internal_review', 'member_review', 'pending_signoffs', 'pending_approval', 'approved', 'amended'];

export function MinutesListPage() {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const [filters, setFilters] = useState<{ bodyId?: string; status?: string; page: number }>({ page: 1 });
  const q = useQuery({ queryKey: ['minutes', filters], queryFn: () => get<Paged<Minutes>>('/api/v1/minutes', { ...filters, pageSize: 20 }) });
  return (
    <>
      <PageHeader title={t('minutes.title')} />
      <Card>
        <div className="toolbar">
          <BodySelect placeholder={t('common.body')} allowClear value={filters.bodyId} onChange={(v) => setFilters((f) => ({ ...f, bodyId: v, page: 1 }))} />
          <EnumSelect group="enums.minutesStatus" values={STATUSES} placeholder={t('common.status')} allowClear value={filters.status} onChange={(v) => setFilters((f) => ({ ...f, status: v, page: 1 }))} />
        </div>
        <ErrorAlert error={q.error} />
        <Table<Minutes>
          rowKey="id" loading={q.isLoading} dataSource={q.data?.items}
          pagination={{ current: filters.page, total: q.data?.total, pageSize: 20, onChange: (page) => setFilters((f) => ({ ...f, page })) }}
          columns={[
            { title: t('minutes.meeting'), render: (_, m) => <Link to={`/minutes/${m.id}`}>{m.meetingTitle}</Link> },
            { title: t('meetings.reference'), dataIndex: 'meetingReference', render: (v) => <span className="mono">{v ?? '—'}</span> },
            { title: t('common.body'), dataIndex: 'bodyName' },
            { title: t('meetings.startsAt'), dataIndex: 'meetingStartsAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
            { title: t('minutes.version'), dataIndex: 'versionNo', width: 80 },
            { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="minutesStatus" value={v} /> },
          ]}
        />
      </Card>
    </>
  );
}

interface MinutesDetail { minutes: Minutes; signoffs: SignOff[]; permissions: string[] }

export function MinutesDetailPage() {
  const { id } = useParams();
  const { t, i18n } = useTranslation();
  const { message, modal } = App.useApp();
  const { me } = useAuth();
  const qc = useQueryClient();
  const [form] = Form.useForm<{ sections: MinutesSection[] }>();
  const [signing, setSigning] = useState(false);
  const [proposing, setProposing] = useState(false);
  const q = useQuery({ queryKey: ['minutes', id], queryFn: () => get<MinutesDetail>(`/api/v1/minutes/${id}`) });
  const res = useQuery({ queryKey: ['minutes-resolutions', id], queryFn: () => get<Paged<Resolution>>('/api/v1/resolutions', { minutesId: id, pageSize: 100 }), enabled: q.isSuccess });
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['minutes', id] });
    void qc.invalidateQueries({ queryKey: ['minutes-resolutions', id] });
  };

  useEffect(() => {
    if (q.data) form.setFieldsValue({ sections: q.data.minutes.content.sections });
  }, [q.data, form]);

  const save = useMutation({
    mutationFn: (v: { sections: MinutesSection[] }) => put(`/api/v1/minutes/${id}/draft`, v),
    onSuccess: () => { message.success(t('common.saved')); refresh(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const request = useMutation({
    mutationFn: () => post(`/api/v1/minutes/${id}/request-signoffs`),
    onSuccess: refresh,
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });

  if (q.isLoading) return <Skeleton active />;
  if (q.error) return <ErrorAlert error={q.error} />;
  const { minutes: m, signoffs, permissions } = q.data!;
  const canEdit = permissions.includes('minutes.edit') && !['approved', 'amended'].includes(m.status);
  const mySignoff = signoffs.find((s) => s.userId === me?.user.id && s.status === 'requested' && s.versionNo === m.versionNo);
  const minutesResolutions = res.data?.items ?? [];

  const onSave = (v: { sections: MinutesSection[] }) => {
    if (m.status !== 'draft')
      modal.confirm({ title: t('minutes.editWarning'), okText: t('common.confirm'), cancelText: t('common.cancel'), onOk: () => save.mutateAsync(v) });
    else save.mutate(v);
  };

  return (
    <>
      <PageHeader
        title={m.meetingTitle}
        subtitle={<Space wrap><span className="mono">{m.meetingReference}</span><StatusTag group="minutesStatus" value={m.status} /><span>{m.bodyName}</span>
          {m.contentHash && <Typography.Text type="secondary">{t('minutes.hash')}: <span className="mono">{m.contentHash.slice(0, 16)}…</span></Typography.Text>}</Space>}
        extra={<>
          {canEdit && m.status === 'draft' && <Button icon={<SendOutlined />} loading={request.isPending} onClick={() => request.mutate()}>{t('minutes.requestSignoffs')}</Button>}
          {mySignoff && <Button type="primary" icon={<CheckOutlined />} onClick={() => setSigning(true)}>{t('minutes.signOff')}</Button>}
        </>}
      />
      <Tabs items={[
        {
          key: 'content', label: t('minutes.sections'),
          children: (
            <Card>
              <Form form={form} layout="vertical" onFinish={onSave} disabled={!canEdit}>
                <Form.List name="sections">
                  {(fields, { add, remove }) => (
                    <>
                      {fields.map((f) => (
                        <Card key={f.key} size="small" style={{ marginBottom: 12 }}
                          extra={canEdit && <Button type="text" danger icon={<DeleteOutlined />} onClick={() => remove(f.name)} />}
                          title={<Form.Item name={[f.name, 'title']} noStyle rules={[{ required: true }]}><Input variant="borderless" style={{ fontWeight: 600 }} /></Form.Item>}>
                          <Form.Item name={[f.name, 'key']} hidden><Input /></Form.Item>
                          <Form.Item name={[f.name, 'body']} noStyle><Input.TextArea autoSize={{ minRows: 3 }} /></Form.Item>
                        </Card>
                      ))}
                      {canEdit && (
                        <Space>
                          <Button icon={<PlusOutlined />} onClick={() => add({ key: `s${Date.now()}`, title: '', body: '' })}>{t('common.add')}</Button>
                          <Button type="primary" htmlType="submit" icon={<SaveOutlined />} loading={save.isPending}>{t('minutes.saveDraft')}</Button>
                        </Space>
                      )}
                    </>
                  )}
                </Form.List>
              </Form>
            </Card>
          ),
        },
        {
          key: 'signoffs', label: `${t('minutes.signoffs')} (${signoffs.length})`,
          children: (
            <Card>
              <Alert type="info" showIcon message={t('minutes.attestationNote')} style={{ marginBottom: 12 }} />
              <Table<SignOff>
                rowKey="id" dataSource={signoffs} pagination={false}
                columns={[
                  { title: t('common.name'), dataIndex: 'name' },
                  { title: t('minutes.version'), dataIndex: 'versionNo', width: 80 },
                  { title: t('minutes.method'), dataIndex: 'method', render: (v) => t(`enums.signoffMethod.${v}`) },
                  { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="signoffStatus" value={v} /> },
                  { title: t('common.date'), dataIndex: 'signedAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
                  { title: t('minutes.reservation'), dataIndex: 'reservation', render: (v) => v ?? '—' },
                ]}
              />
            </Card>
          ),
        },
        {
          key: 'resolutions', label: t('minutes.resolutions'),
          children: (
            <Card extra={permissions.includes('resolution.manage') && m.status === 'draft' && <Button icon={<PlusOutlined />} onClick={() => setProposing(true)}>{t('minutes.proposeResolution')}</Button>}>
              <Table<Resolution>
                rowKey="id" dataSource={minutesResolutions} pagination={false}
                columns={[
                  { title: t('common.type'), dataIndex: 'resolutionType', render: (v) => t(`enums.resolutionType.${v}`) },
                  { title: t('common.title'), dataIndex: 'titleAr' },
                  { title: t('resolutions.number'), dataIndex: 'number', render: (v) => v ?? <Typography.Text type="secondary">{t('resolutions.unnumbered')}</Typography.Text> },
                  { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="resolutionStatus" value={v} /> },
                ]}
              />
            </Card>
          ),
        },
      ]} />
      {signing && mySignoff && <SignOffModal minutesId={m.id} hash={m.contentHash!} onClose={() => { setSigning(false); refresh(); }} />}
      {proposing && <ProposeModal minutesId={m.id} onClose={() => { setProposing(false); refresh(); }} />}
    </>
  );
}

function SignOffModal({ minutesId, hash, onClose }: { minutesId: string; hash: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [reservation, setReservation] = useState('');
  const m = useMutation({
    mutationFn: () => post(`/api/v1/minutes/${minutesId}/signoffs`, { contentHash: hash, reservation: reservation || undefined }),
    onSuccess: () => { message.success(t('minutes.signedOk')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('minutes.signOff')} onCancel={onClose} onOk={() => m.mutate()} confirmLoading={m.isPending} okText={t('common.confirm')} cancelText={t('common.cancel')}>
      <Alert type="info" showIcon message={t('minutes.attestationNote')} style={{ marginBottom: 12 }} />
      <Typography.Paragraph>{t('minutes.hash')}: <span className="mono">{hash}</span></Typography.Paragraph>
      <Input.TextArea rows={3} placeholder={t('minutes.reservation')} value={reservation} onChange={(e) => setReservation(e.target.value)} maxLength={2000} />
    </Modal>
  );
}

function ProposeModal({ minutesId, onClose }: { minutesId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post(`/api/v1/minutes/${minutesId}/resolutions`, v),
    onSuccess: onClose,
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('minutes.proposeResolution')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.add')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={{ resolutionType: 'decision' }} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="resolutionType" label={t('common.type')}>
          <Select options={['decision', 'recommendation'].map((v) => ({ value: v, label: t(`enums.resolutionType.${v}`) }))} />
        </Form.Item>
        <Form.Item name="titleAr" label={t('common.title')} rules={[{ required: true, max: 300 }]}><Input /></Form.Item>
        <Form.Item name="textAr" label={t('resolutions.text')} rules={[{ required: true }]}><Input.TextArea rows={4} /></Form.Item>
      </Form>
    </Modal>
  );
}

export function ResolutionsPage() {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const [filters, setFilters] = useState<{ q?: string; bodyId?: string; status?: string; type?: string; page: number }>({ page: 1 });
  const q = useQuery({ queryKey: ['resolutions', filters], queryFn: () => get<Paged<Resolution>>('/api/v1/resolutions', { ...filters, pageSize: 20 }) });
  return (
    <>
      <PageHeader title={t('resolutions.title')} />
      <Card>
        <div className="toolbar">
          <Input.Search placeholder={t('common.search')} allowClear style={{ width: 240 }} onSearch={(v) => setFilters((f) => ({ ...f, q: v, page: 1 }))} />
          <BodySelect placeholder={t('common.body')} allowClear value={filters.bodyId} onChange={(v) => setFilters((f) => ({ ...f, bodyId: v, page: 1 }))} />
          <EnumSelect group="enums.resolutionType" values={['decision', 'recommendation']} placeholder={t('common.type')} allowClear value={filters.type} onChange={(v) => setFilters((f) => ({ ...f, type: v, page: 1 }))} />
          <EnumSelect group="enums.resolutionStatus" values={['proposed', 'issued', 'in_execution', 'executed', 'closed', 'cancelled']} placeholder={t('common.status')} allowClear value={filters.status} onChange={(v) => setFilters((f) => ({ ...f, status: v, page: 1 }))} />
        </div>
        <ErrorAlert error={q.error} />
        <Table<Resolution>
          rowKey="id" loading={q.isLoading} dataSource={q.data?.items}
          expandable={{ expandedRowRender: (r) => <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{r.textAr}</Typography.Paragraph> }}
          pagination={{ current: filters.page, total: q.data?.total, pageSize: 20, onChange: (page) => setFilters((f) => ({ ...f, page })) }}
          columns={[
            { title: t('resolutions.number'), dataIndex: 'number', render: (v) => v ? <span className="mono">{v}</span> : <Typography.Text type="secondary">{t('resolutions.unnumbered')}</Typography.Text> },
            { title: t('common.type'), dataIndex: 'resolutionType', render: (v) => t(`enums.resolutionType.${v}`) },
            { title: t('common.title'), dataIndex: 'titleAr' },
            { title: t('common.body'), dataIndex: 'bodyName' },
            { title: t('meetings.reference'), dataIndex: 'meetingReference', render: (v) => <span className="mono">{v ?? '—'}</span> },
            { title: t('resolutions.tasks'), dataIndex: 'taskCount', width: 80 },
            { title: t('common.date'), dataIndex: 'createdAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
            { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="resolutionStatus" value={v} /> },
          ]}
        />
      </Card>
    </>
  );
}
