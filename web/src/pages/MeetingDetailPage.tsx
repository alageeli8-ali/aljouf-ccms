import { Alert, App, Badge, Button, Card, DatePicker, Descriptions, Dropdown, Form, Input, InputNumber, Modal, Skeleton, Space, Tabs, Tag } from 'antd';
import {
  CalendarOutlined, CheckOutlined, CloseOutlined, DownOutlined, EditOutlined, FileAddOutlined, FileTextOutlined, LinkOutlined,
  PlayCircleOutlined, QuestionOutlined, SendOutlined, StopOutlined,
} from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import type { Dayjs } from 'dayjs';
import { ApiError, get, post } from '../api/client';
import type { Invitee, MeetingDetail, Minutes, QuorumResult, QuorumView } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../components/common';
import { ActivityTimeline, useReasonPrompt } from '../components/governance';
import { formatDate, formatDateTime } from '../lib/format';
import { MeetingFormModal } from './MeetingsPage';
import { AgendaEditor } from './meeting/Agenda';
import { AttendancePanel, QuorumPanel } from './meeting/Attendance';
import { VotingPanel } from './meeting/Voting';

export default function MeetingDetailPage() {
  const { id } = useParams();
  const [params, setParams] = useSearchParams();
  const { t, i18n } = useTranslation();
  const { message, modal } = App.useApp();
  const { me } = useAuth();
  const nav = useNavigate();
  const qc = useQueryClient();
  const reason = useReasonPrompt();
  const [editing, setEditing] = useState(false);
  const [quorumBlock, setQuorumBlock] = useState<QuorumResult | null>(null);
  const q = useQuery({ queryKey: ['meeting', id], queryFn: () => get<MeetingDetail>(`/api/v1/meetings/${id}`) });
  const invitees = useQuery({ queryKey: ['invitations', id], queryFn: () => get<Invitee[]>(`/api/v1/meetings/${id}/invitations`), enabled: q.isSuccess });
  const refresh = () => {
    for (const k of [['meeting', id], ['invitations', id], ['quorum', id], ['meeting-activity', id], ['meetings'], ['calendar']]) void qc.invalidateQueries({ queryKey: k });
  };
  const run = (path: string, body?: unknown, ok?: string) => post<Record<string, unknown>>(`/api/v1/meetings/${id}/${path}`, body).then((r) => { message.success(ok ?? t('common.saved')); refresh(); return r; });
  const action = useMutation({
    mutationFn: ({ path, body, ok }: { path: string; body?: unknown; ok?: string }) => run(path, body, ok),
    onError: (e) => {
      if (e instanceof ApiError && e.code === 'quorum-not-met' && e.problem.quorum) { setQuorumBlock(e.problem.quorum as QuorumResult); return; }
      message.error(errorText(e, t('common.error')));
    },
  });
  const createMinutes = useMutation({
    mutationFn: () => post<Minutes>(`/api/v1/meetings/${id}/minutes`),
    onSuccess: (m) => nav(`/minutes/${m.id}`),
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });

  if (q.isLoading) return <Skeleton active />;
  if (q.error) return <ErrorAlert error={q.error} />;
  const { meeting: m, agenda, permissions } = q.data!;
  const can = (p: string) => permissions.includes(p);
  const manage = can('meeting.manage');
  const invite = can('meeting.invite');
  const attend = can('meeting.attendance');
  const preparing = ['draft', 'scheduled', 'agenda_published'].includes(m.status);
  const circulation = m.meetingType === 'circulation';
  const mine = invitees.data?.find((x) => x.userId === me?.user.id && x.sentAt);

  const reschedule = () => reason.prompt({
    title: t('meetings.reschedule'), description: t('meetings.rescheduleHelp'),
    extra: (
      <div className="form-row">
        <Form.Item name="startsAt" label={t('meetings.newStartsAt')} rules={[{ required: true }]}><DatePicker showTime format="YYYY-MM-DD HH:mm" style={{ width: '100%' }} /></Form.Item>
        <Form.Item name="durationMinutes" label={t('meetings.duration')}><InputNumber min={15} max={720} style={{ width: '100%' }} /></Form.Item>
        <Form.Item name="location" label={t('meetings.location')}><Input /></Form.Item>
      </div>
    ),
    onSubmit: async (v) => {
      const r = await post<{ id: string }>(`/api/v1/meetings/${id}/reschedule`, { ...v, startsAt: (v.startsAt as Dayjs).toISOString() });
      refresh();
      nav(`/meetings/${r.id}`);
    },
  });

  const more = [
    ...(manage && preparing ? [{ key: 'edit', icon: <EditOutlined />, label: t('meetings.edit'), onClick: () => setEditing(true) }] : []),
    ...(manage && ['scheduled', 'agenda_published', 'quorum_failed'].includes(m.status) ? [{ key: 'reschedule', icon: <CalendarOutlined />, label: t('meetings.reschedule'), onClick: reschedule }] : []),
    ...(invite && m.status === 'agenda_published' ? [{ key: 'remind', icon: <SendOutlined />, label: t('invitations.remind'), onClick: () => action.mutate({ path: 'invitations/remind', ok: t('invitations.reminded') }) }] : []),
    ...(attend && m.status === 'agenda_published' && !circulation ? [{
      key: 'qf', icon: <StopOutlined />, label: t('meetings.markQuorumFailed'),
      onClick: () => reason.prompt({ title: t('meetings.markQuorumFailed'), danger: true, onSubmit: (v) => run('mark-quorum-failed', v) }),
    }] : []),
    ...(manage && preparing ? [{
      key: 'cancel', icon: <CloseOutlined />, danger: true, label: t('meetings.cancelMeeting'),
      onClick: () => reason.prompt({ title: t('meetings.cancelMeeting'), danger: true, description: t('meetings.cancelHelp'), onSubmit: (v) => run('cancel', v) }),
    }] : []),
  ];

  const primary = (
    <>
      {manage && m.status === 'draft' && (
        <Button type="primary" icon={<CalendarOutlined />} loading={action.isPending} onClick={() => action.mutate({ path: 'schedule', ok: t('meetings.scheduled') })}>{t('meetings.schedule')}</Button>
      )}
      {invite && ['scheduled', 'agenda_published'].includes(m.status) && (
        <Button type={m.status === 'scheduled' ? 'primary' : 'default'} icon={<SendOutlined />} loading={action.isPending}
          onClick={() => modal.confirm({
            title: m.status === 'scheduled' ? t('invitations.send') : t('invitations.sendNew'), content: t('invitations.sendHelp'),
            okText: t('common.confirm'), cancelText: t('common.cancel'),
            onOk: () => action.mutateAsync({ path: 'invitations/send', ok: t('invitations.sent') }),
          })}>
          {m.status === 'scheduled' ? t('invitations.send') : t('invitations.sendNew')}
        </Button>
      )}
      {attend && m.status === 'agenda_published' && !circulation && (
        <Button type="primary" icon={<PlayCircleOutlined />} loading={action.isPending} onClick={() => action.mutate({ path: 'open', body: {}, ok: t('meetings.opened') })}>{t('meetings.open')}</Button>
      )}
      {attend && (m.status === 'in_progress' || (circulation && m.status === 'agenda_published')) && (
        <Button type="primary" danger={!circulation} icon={<StopOutlined />}
          onClick={() => modal.confirm({
            title: circulation ? t('meetings.closeCirculation') : t('meetings.close'), content: t('meetings.closeHelp'),
            okText: t('common.confirm'), cancelText: t('common.cancel'),
            onOk: () => action.mutateAsync({ path: 'close', ok: t('meetings.closed') }),
          })}>
          {circulation ? t('meetings.closeCirculation') : t('meetings.close')}
        </Button>
      )}
      {m.minutesId
        ? <Button icon={<FileTextOutlined />} onClick={() => nav(`/minutes/${m.minutesId}`)}>{t('meetings.openMinutes')}</Button>
        : can('minutes.edit') && ['in_progress', 'concluded'].includes(m.status) &&
          <Button icon={<FileAddOutlined />} loading={createMinutes.isPending} onClick={() => createMinutes.mutate()}>{t('meetings.createMinutes')}</Button>}
      {more.length > 0 && <Dropdown menu={{ items: more }}><Button>{t('common.more')} <DownOutlined /></Button></Dropdown>}
    </>
  );

  const tab = params.get('tab') ?? 'overview';
  const pendingResponses = m.status === 'agenda_published' ? (invitees.data ?? []).filter((x) => x.sentAt && x.response === 'pending').length : 0;
  return (
    <>
      <PageHeader
        title={m.titleAr}
        subtitle={<Space wrap>
          <span className="mono">{m.referenceNo ?? t('meetings.unnumbered')}</span>
          <Tag color={m.meetingType === 'regular' ? undefined : m.meetingType === 'circulation' ? 'geekblue' : 'orange'}>{t(`enums.meetingType.${m.meetingType}`)}</Tag>
          <StatusTag group="meetingStatus" value={m.status} />
          <Link to={`/bodies/${m.bodyId}`}>{m.bodyName}</Link>
        </Space>}
        extra={primary}
      />
      {m.statusReason && <Alert showIcon type={['cancelled', 'postponed', 'quorum_failed'].includes(m.status) ? 'warning' : 'info'} message={`${t('common.reason')}: ${m.statusReason}`} style={{ marginBottom: 12 }} />}
      {mine && m.status === 'agenda_published' && <RsvpBar meetingId={id!} me={mine} onDone={refresh} circulation={circulation} />}
      <Card>
        <Tabs activeKey={tab} onChange={(k) => setParams({ tab: k }, { replace: true })} items={[
          { key: 'overview', label: t('meetings.tabs.overview'), children: <Overview detail={q.data!} lang={i18n.language} calendar={me?.user.calendar} /> },
          { key: 'agenda', label: <span>{t('meetings.tabs.agenda')} <Badge count={agenda.length} color="#006837" showZero size="small" /></span>,
            children: <AgendaEditor meeting={m} agenda={agenda} permissions={permissions} onChange={refresh} /> },
          { key: 'attendance', label: <span>{t('meetings.tabs.attendance')} {pendingResponses > 0 && <Badge count={pendingResponses} size="small" />}</span>,
            children: <AttendancePanel meeting={m} invitees={invitees.data ?? []} loading={invitees.isLoading} agenda={agenda} permissions={permissions} onChange={refresh} /> },
          { key: 'quorum', label: t('meetings.tabs.quorum'), children: <QuorumPanel meetingId={id!} canCheck={attend} /> },
          { key: 'voting', label: t('meetings.tabs.voting'), children: <VotingPanel meeting={m} /> },
          { key: 'activity', label: t('meetings.tabs.activity'), children: <ActivityTimeline url={`/api/v1/meetings/${id}/activity`} queryKey={['meeting-activity', id]} /> },
        ]} />
      </Card>
      {editing && <MeetingFormModal meeting={m} onClose={() => { setEditing(false); refresh(); }} />}
      {quorumBlock && <QuorumBlockedModal meetingId={id!} result={quorumBlock} onClose={() => setQuorumBlock(null)} onOpened={refresh} />}
      {reason.node}
    </>
  );
}

function Overview({ detail, lang, calendar }: { detail: MeetingDetail; lang: string; calendar?: 'gregorian' | 'hijri' }) {
  const { t } = useTranslation();
  const m = detail.meeting;
  const link = (id?: string | null, label?: string | null) => (id ? <Link to={`/meetings/${id}`}>{label ?? t('common.open')}</Link> : '—');
  return (
    <Descriptions bordered size="small" column={{ xs: 1, md: 2 }}>
      <Descriptions.Item label={m.meetingType === 'circulation' ? t('meetings.circulationStart') : t('meetings.startsAt')}>{formatDateTime(m.startsAt, lang, calendar)}</Descriptions.Item>
      <Descriptions.Item label={t('common.hijri')}>{formatDate(m.startsAt, lang, 'hijri')}</Descriptions.Item>
      {m.meetingType === 'circulation'
        ? <Descriptions.Item label={t('meetings.circulationDeadline')}>{formatDateTime(m.circulationDeadline, lang, calendar)}</Descriptions.Item>
        : <Descriptions.Item label={t('meetings.plannedEnd')}>{formatDateTime(m.plannedEndAt, lang, calendar)}</Descriptions.Item>}
      <Descriptions.Item label={t('meetings.mode')}>{t(`enums.mode.${m.mode}`)}</Descriptions.Item>
      <Descriptions.Item label={t('meetings.location')}>{m.location ?? '—'}</Descriptions.Item>
      <Descriptions.Item label={t('meetings.onlineLink')}>{m.onlineLink ? <a href={m.onlineLink} target="_blank" rel="noreferrer" dir="ltr"><LinkOutlined /> {m.onlineLink}</a> : '—'}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.chair')}>{m.chairName ?? '—'}</Descriptions.Item>
      <Descriptions.Item label={t('bodies.secretary')}>{m.secretaryName ?? '—'}</Descriptions.Item>
      <Descriptions.Item label={t('meetings.invitationsSentAt')}>{formatDateTime(m.invitationsSentAt, lang, calendar)}</Descriptions.Item>
      <Descriptions.Item label={t('invitations.accepted')}>{m.invitedCount ? `${m.acceptedCount} / ${m.invitedCount}` : '—'}</Descriptions.Item>
      <Descriptions.Item label={t('meetings.actualStart')}>{formatDateTime(m.actualStartedAt, lang, calendar)}</Descriptions.Item>
      <Descriptions.Item label={t('meetings.actualEnd')}>{formatDateTime(m.actualEndedAt, lang, calendar)}</Descriptions.Item>
      <Descriptions.Item label={t('meetings.previous')}>{link(m.previousMeetingId, m.previousReference ?? m.previousTitle)}</Descriptions.Item>
      <Descriptions.Item label={t('meetings.next')}>{link(m.nextMeetingId)}</Descriptions.Item>
      {m.rescheduledFromMeetingId && <Descriptions.Item label={t('meetings.rescheduledFrom')}>{link(m.rescheduledFromMeetingId, m.rescheduledFromReference)}</Descriptions.Item>}
      {m.rescheduledToId && <Descriptions.Item label={t('meetings.rescheduledTo')}>{link(m.rescheduledToId)}</Descriptions.Item>}
      <Descriptions.Item label={t('bodies.classification')}>{t(`enums.classification.${m.classification}`)}</Descriptions.Item>
    </Descriptions>
  );
}

function RsvpBar({ meetingId, me, onDone, circulation }: { meetingId: string; me: Invitee; onDone: () => void; circulation: boolean }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [note, setNote] = useState('');
  const m = useMutation({
    mutationFn: (response: string) => post(`/api/v1/meetings/${meetingId}/rsvp`, { response, note: note || undefined }),
    onSuccess: () => { message.success(t('invitations.responded')); onDone(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Alert type={me.response === 'pending' ? 'warning' : me.response === 'accepted' ? 'success' : 'info'} showIcon style={{ marginBottom: 12 }}
      message={me.response === 'pending' ? (circulation ? t('invitations.circulationAsk') : t('invitations.youAreInvited')) : `${t('invitations.yourResponse')}: ${t(`enums.rsvp.${me.response}`)}`}
      description={
        <Space wrap>
          <Input placeholder={t('invitations.note')} value={note} onChange={(e) => setNote(e.target.value)} style={{ width: 260 }} maxLength={500} />
          <Button icon={<CheckOutlined />} type={me.response === 'accepted' ? 'primary' : 'default'} loading={m.isPending} onClick={() => m.mutate('accepted')}>{circulation ? t('invitations.approveParticipation') : t('enums.rsvp.accepted')}</Button>
          <Button icon={<QuestionOutlined />} type={me.response === 'tentative' ? 'primary' : 'default'} onClick={() => m.mutate('tentative')}>{t('enums.rsvp.tentative')}</Button>
          <Button icon={<CloseOutlined />} danger={me.response === 'declined'} onClick={() => m.mutate('declined')}>{t('invitations.decline')}</Button>
        </Space>
      } />
  );
}

function QuorumBlockedModal({ meetingId, result, onClose, onOpened }: { meetingId: string; result: QuorumResult; onClose: () => void; onOpened: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [reason, setReason] = useState('');
  const policy = useQuery({ queryKey: ['quorum', meetingId], queryFn: () => get<QuorumView>(`/api/v1/meetings/${meetingId}/quorum`) });
  const allowOverride = policy.data?.policy.quorum.allowOverride;
  const m = useMutation({
    mutationFn: () => post(`/api/v1/meetings/${meetingId}/open`, { overrideReason: reason }),
    onSuccess: () => { message.success(t('meetings.opened')); onOpened(); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('quorum.notMet')} onCancel={onClose} footer={allowOverride ? undefined : null} onOk={() => m.mutate()}
      okText={t('quorum.openWithOverride')} okButtonProps={{ danger: true, disabled: reason.trim().length < 3 }} confirmLoading={m.isPending} cancelText={t('common.close')}>
      <div className="quorum-box unmet" style={{ marginBottom: 12 }}>
        <span className="quorum-num">{result.counted} / {result.required}</span>
        <span>{t('quorum.summary', { counted: result.counted, required: result.required, eligible: result.eligible })}</span>
        {result.chairRequired && !result.chairPresent && <Tag color="red">{t('quorum.chairAbsent')}</Tag>}
      </div>
      <p>{allowOverride ? t('quorum.overrideHelp') : t('quorum.noOverride')}</p>
      {allowOverride && <Input.TextArea rows={2} value={reason} onChange={(e) => setReason(e.target.value)} placeholder={t('quorum.overrideReason')} />}
    </Modal>
  );
}
