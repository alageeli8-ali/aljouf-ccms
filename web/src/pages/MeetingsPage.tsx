import { App, Button, Calendar, Card, DatePicker, Form, Input, InputNumber, Modal, Radio, Segmented, Select, Space, Table, Tag, Tooltip } from 'antd';
import { CalendarOutlined, PlusOutlined, UnorderedListOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import dayjs, { type Dayjs } from 'dayjs';
import { get, patch, post, type Paged } from '../api/client';
import type { CalendarMeeting, Meeting } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../components/common';
import { BodySelect, EnumSelect } from '../components/pickers';
import { formatDateTime, formatMonth } from '../lib/format';

export const MEETING_STATUSES = ['draft', 'scheduled', 'agenda_published', 'in_progress', 'concluded', 'closed', 'postponed', 'cancelled', 'quorum_failed'];
export const MEETING_TYPES = ['regular', 'extraordinary', 'urgent', 'circulation'];

export default function MeetingsPage() {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const [params, setParams] = useSearchParams();
  const view = params.get('view') ?? 'calendar';
  const [filters, setFilters] = useState<{ q?: string; bodyId?: string; status?: string; type?: string; page: number }>({ page: 1 });
  const [creating, setCreating] = useState(params.get('new') === '1');
  const canCreate = (me?.bodyRoles ?? []).some((r) => ['chair', 'vice_chair', 'secretary'].includes(r.role));
  return (
    <>
      <PageHeader
        title={t('meetings.title')}
        subtitle={t('meetings.subtitle')}
        extra={<>
          <Segmented value={view} onChange={(v) => setParams({ view: String(v) }, { replace: true })} options={[
            { value: 'calendar', icon: <CalendarOutlined />, label: t('calendar.title') },
            { value: 'list', icon: <UnorderedListOutlined />, label: t('meetings.list') },
          ]} />
          {canCreate && <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreating(true)}>{t('meetings.new')}</Button>}
        </>}
      />
      <Card>
        <div className="toolbar">
          <BodySelect placeholder={t('common.body')} allowClear value={filters.bodyId} onChange={(v) => setFilters((f) => ({ ...f, bodyId: v, page: 1 }))} />
          {view === 'list' && <>
            <Input.Search placeholder={t('common.search')} allowClear style={{ width: 240 }} onSearch={(v) => setFilters((f) => ({ ...f, q: v, page: 1 }))} />
            <EnumSelect group="enums.meetingType" values={MEETING_TYPES} placeholder={t('common.type')} allowClear value={filters.type} onChange={(v) => setFilters((f) => ({ ...f, type: v, page: 1 }))} />
            <EnumSelect group="enums.meetingStatus" values={MEETING_STATUSES} placeholder={t('common.status')} allowClear value={filters.status} style={{ minWidth: 200 }}
              onChange={(v) => setFilters((f) => ({ ...f, status: v, page: 1 }))} />
          </>}
        </div>
        {view === 'calendar' ? <MeetingsCalendar bodyId={filters.bodyId} /> : <MeetingsList filters={filters} setPage={(page) => setFilters((f) => ({ ...f, page }))} lang={i18n.language} calendar={me?.user.calendar} />}
      </Card>
      {creating && <MeetingFormModal initialBodyId={params.get('bodyId') ?? undefined} onClose={() => { setCreating(false); if (params.get('new')) setParams({}, { replace: true }); }} />}
    </>
  );
}

function MeetingsList({ filters, setPage, lang, calendar }: { filters: Record<string, unknown> & { page: number }; setPage: (p: number) => void; lang: string; calendar?: 'gregorian' | 'hijri' }) {
  const { t } = useTranslation();
  const q = useQuery({ queryKey: ['meetings', filters], queryFn: () => get<Paged<Meeting>>('/api/v1/meetings', { ...filters, pageSize: 20 }) });
  return (
    <>
      <ErrorAlert error={q.error} />
      <Table<Meeting>
        rowKey="id" loading={q.isLoading} dataSource={q.data?.items} scroll={{ x: 1000 }}
        pagination={{ current: filters.page, total: q.data?.total, pageSize: 20, onChange: setPage }}
        columns={[
          { title: t('meetings.reference'), dataIndex: 'referenceNo', render: (v) => <span className="mono">{v ?? '—'}</span> },
          { title: t('common.title'), dataIndex: 'titleAr', render: (v, m) => <Link to={`/meetings/${m.id}`}>{v}</Link> },
          { title: t('common.body'), dataIndex: 'bodyName' },
          { title: t('common.type'), dataIndex: 'meetingType', render: (v) => <Tag color={v === 'regular' ? undefined : v === 'circulation' ? 'geekblue' : 'orange'}>{t(`enums.meetingType.${v}`)}</Tag> },
          { title: t('meetings.startsAt'), dataIndex: 'startsAt', render: (v) => formatDateTime(v, lang, calendar) },
          { title: t('meetings.agendaCount'), dataIndex: 'agendaCount', width: 80 },
          { title: t('invitations.accepted'), render: (_, m) => m.invitedCount ? `${m.acceptedCount}/${m.invitedCount}` : '—' },
          { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="meetingStatus" value={v} /> },
        ]}
      />
    </>
  );
}

function MeetingsCalendar({ bodyId }: { bodyId?: string }) {
  const { t, i18n } = useTranslation();
  const nav = useNavigate();
  const [month, setMonth] = useState(() => dayjs().startOf('month'));
  const from = month.startOf('month').subtract(7, 'day');
  const to = month.endOf('month').add(8, 'day');
  const q = useQuery({
    queryKey: ['calendar', from.toISOString(), bodyId],
    queryFn: () => get<CalendarMeeting[]>('/api/v1/meetings/calendar', { from: from.toISOString(), to: to.toISOString(), bodyId }),
  });
  const byDay = useMemo(() => {
    const map = new Map<string, CalendarMeeting[]>();
    for (const m of q.data ?? []) {
      const key = riyadhDay(m.startsAt);
      map.set(key, [...(map.get(key) ?? []), m]);
    }
    return map;
  }, [q.data]);
  const hhmm = (v: string) => new Intl.DateTimeFormat(i18n.language === 'ar' ? 'ar-SA-u-nu-latn' : 'en-GB', { timeZone: 'Asia/Riyadh', hour: '2-digit', minute: '2-digit' }).format(new Date(v));
  return (
    <>
      <ErrorAlert error={q.error} />
      <Calendar
        value={month}
        onPanelChange={(v) => setMonth(v.startOf('month'))}
        onSelect={(v, info) => { if (info.source === 'month' || info.source === 'year') setMonth(v.startOf('month')); }}
        headerRender={({ value, onChange }) => (
          <Space style={{ padding: 8, justifyContent: 'space-between', width: '100%' }} wrap>
            <Space>
              <Button onClick={() => onChange(value.subtract(1, 'month'))}>{t('calendar.prev')}</Button>
              <Button onClick={() => onChange(dayjs())}>{t('calendar.today')}</Button>
              <Button onClick={() => onChange(value.add(1, 'month'))}>{t('calendar.next')}</Button>
            </Space>
            <strong>{formatMonth(value.date(15).format('YYYY-MM-DD'), i18n.language)} — {formatMonth(value.date(15).format('YYYY-MM-DD'), i18n.language, 'hijri')}</strong>
            <Space size={4} wrap>{MEETING_TYPES.map((x) => <span key={x} className={`cal-event ${x}`} style={{ display: 'inline-block' }}>{t(`enums.meetingType.${x}`)}</span>)}</Space>
          </Space>
        )}
        cellRender={(date, info) => {
          if (info.type !== 'date') return info.originNode;
          const list = byDay.get(date.format('YYYY-MM-DD')) ?? [];
          return (
            <div>
              {list.slice(0, 3).map((m) => (
                <Tooltip key={m.id} title={`${m.bodyName} — ${m.titleAr} (${t(`enums.meetingStatus.${m.status}`)})`}>
                  <a className={`cal-event ${m.meetingType} ${m.status}`} onClick={(e) => { e.stopPropagation(); nav(`/meetings/${m.id}`); }}>
                    <bdi>{hhmm(m.startsAt)}</bdi> <bdi>{m.bodyCode}</bdi> · <bdi>{m.titleAr}</bdi>
                  </a>
                </Tooltip>
              ))}
              {list.length > 3 && <span className="item-meta">+{list.length - 3}</span>}
            </div>
          );
        }}
      />
    </>
  );
}

function riyadhDay(v: string) {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Riyadh', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(v));
  return parts;
}

/** نموذج إنشاء الاجتماع أو تعديله. نوع التمرير والحضور عن بعد يُتحقق منهما في الخادم وفق سياسة الجهة. */
export function MeetingFormModal({ meeting, initialBodyId, onClose }: { meeting?: Meeting; initialBodyId?: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const nav = useNavigate();
  const [form] = Form.useForm();
  const type = Form.useWatch('meetingType', form) as string;
  const mode = Form.useWatch('mode', form) as string;
  const bodyId = Form.useWatch('bodyId', form) as string | undefined;
  const editing = !!meeting;
  const locked = !!meeting?.invitationsSentAt;
  const previous = useQuery({
    queryKey: ['meetings', { bodyId, prev: true }],
    queryFn: () => get<Paged<Meeting>>('/api/v1/meetings', { bodyId, pageSize: 30 }),
    enabled: !!bodyId && !editing,
  });
  useEffect(() => { if (type === 'circulation') form.setFieldValue('mode', 'remote'); }, [type, form]);
  const m = useMutation({
    mutationFn: (v: Record<string, any>) => {
      const startsAt = (v.startsAt as Dayjs).toISOString();
      const body: Record<string, any> = { ...v, startsAt, circulationDeadline: v.circulationDeadline ? (v.circulationDeadline as Dayjs).toISOString() : undefined };
      if (editing) {
        const { bodyId: _b, meetingType: _t, previousMeetingId: _p, ...rest } = body;
        return patch<Meeting>(`/api/v1/meetings/${meeting!.id}`, locked ? { ...rest, startsAt: undefined } : rest);
      }
      return post<Meeting>('/api/v1/meetings', body);
    },
    onSuccess: (res) => {
      void qc.invalidateQueries({ queryKey: ['meetings'] });
      void qc.invalidateQueries({ queryKey: ['calendar'] });
      void qc.invalidateQueries({ queryKey: ['meeting', meeting?.id] });
      message.success(t('common.saved'));
      onClose();
      if (!editing) nav(`/meetings/${res.id}`);
    },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const duration = meeting?.plannedEndAt ? dayjs(meeting.plannedEndAt).diff(dayjs(meeting.startsAt), 'minute') : 60;
  return (
    <Modal open width={720} title={editing ? t('meetings.edit') : t('meetings.new')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending}
      okText={editing ? t('common.save') : t('common.create')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)} initialValues={meeting ? {
        bodyId: meeting.bodyId, titleAr: meeting.titleAr, titleEn: meeting.titleEn, meetingType: meeting.meetingType, mode: meeting.mode,
        startsAt: dayjs(meeting.startsAt), durationMinutes: duration, location: meeting.location, onlineLink: meeting.onlineLink,
        circulationDeadline: meeting.circulationDeadline ? dayjs(meeting.circulationDeadline) : undefined,
      } : { bodyId: initialBodyId, meetingType: 'regular', mode: 'in_person', durationMinutes: 90, startsAt: dayjs().add(7, 'day').hour(10).minute(0) }}>
        <Form.Item name="bodyId" label={t('common.body')} rules={[{ required: true }]}><BodySelect activeOnly style={{ width: '100%' }} /></Form.Item>
        <Form.Item name="meetingType" label={t('common.type')}>
          <Radio.Group disabled={editing} optionType="button" options={MEETING_TYPES.map((v) => ({ value: v, label: t(`enums.meetingType.${v}`) }))} />
        </Form.Item>
        {type && type !== 'regular' && <p className="item-meta" style={{ marginTop: -12 }}>{t(`meetings.typeHelp.${type}`)}</p>}
        <div className="form-row">
          <Form.Item name="titleAr" label={t('common.title')} rules={[{ required: true, max: 300 }]}><Input /></Form.Item>
          <Form.Item name="titleEn" label={t('meetings.titleEn')}><Input dir="ltr" /></Form.Item>
        </div>
        <div className="form-row">
          <Form.Item name="startsAt" label={type === 'circulation' ? t('meetings.circulationStart') : t('meetings.startsAt')} rules={[{ required: true }]}
            extra={locked ? t('meetings.useReschedule') : undefined}>
            <DatePicker showTime={{ format: 'HH:mm', minuteStep: 5 }} format="YYYY-MM-DD HH:mm" disabled={locked} style={{ width: '100%' }} />
          </Form.Item>
          {type === 'circulation'
            ? <Form.Item name="circulationDeadline" label={t('meetings.circulationDeadline')} rules={[{ required: true }]}><DatePicker showTime format="YYYY-MM-DD HH:mm" style={{ width: '100%' }} /></Form.Item>
            : <Form.Item name="durationMinutes" label={t('meetings.duration')}><InputNumber min={15} max={720} step={15} style={{ width: '100%' }} /></Form.Item>}
        </div>
        {type !== 'circulation' && <>
          <Form.Item name="mode" label={t('meetings.mode')}>
            <Radio.Group optionType="button" options={['in_person', 'remote', 'hybrid'].map((v) => ({ value: v, label: t(`enums.mode.${v}`) }))} />
          </Form.Item>
          <div className="form-row">
            {mode !== 'remote' && <Form.Item name="location" label={t('meetings.location')} rules={[{ required: true }]}><Input /></Form.Item>}
            {mode !== 'in_person' && <Form.Item name="onlineLink" label={t('meetings.onlineLink')} rules={[{ required: true, type: 'url' }]}><Input dir="ltr" placeholder="https://" /></Form.Item>}
          </div>
        </>}
        {!editing && (
          <Form.Item name="previousMeetingId" label={t('meetings.previous')} extra={t('meetings.previousHelp')}>
            <Select allowClear options={(previous.data?.items ?? []).filter((x) => !['cancelled', 'postponed'].includes(x.status)).map((x) => ({ value: x.id, label: `${x.referenceNo ?? '—'} — ${x.titleAr}` }))} />
          </Form.Item>
        )}
      </Form>
    </Modal>
  );
}
