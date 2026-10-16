import { App, Button, Card, Form, Modal, Select, Space, Table, Tag, Tooltip, Typography } from 'antd';
import { CheckCircleTwoTone, CloseCircleTwoTone, LogoutOutlined, ReloadOutlined, SaveOutlined, UserAddOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { get, post, put } from '../../api/client';
import type { AgendaItem, Invitee, Meeting, QuorumResult, QuorumView } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, StatusTag, errorText } from '../../components/common';
import { UserSelect } from '../../components/pickers';
import { formatDateTime } from '../../lib/format';
import { quorumText } from '../body/Policy';

type Draft = { status?: string; mode?: string; proxyHolderUserId?: string };

export function AttendancePanel({ meeting, invitees, loading, agenda, permissions, onChange }: {
  meeting: Meeting; invitees: Invitee[]; loading: boolean; agenda: AgendaItem[]; permissions: string[]; onChange: () => void;
}) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const { me } = useAuth();
  const qc = useQueryClient();
  const [drafts, setDrafts] = useState<Record<string, Draft>>({});
  const [guest, setGuest] = useState(false);
  const canRecord = permissions.includes('meeting.attendance') && ['agenda_published', 'in_progress'].includes(meeting.status) && meeting.meetingType !== 'circulation';
  const canInvite = permissions.includes('meeting.invite') && ['draft', 'scheduled', 'agenda_published'].includes(meeting.status);
  const voters = invitees.filter((x) => x.isVoting && x.role !== 'alternate');
  const quorum = useQuery({ queryKey: ['quorum', meeting.id], queryFn: () => get<QuorumView>(`/api/v1/meetings/${meeting.id}/quorum`) });
  const save = useMutation({
    mutationFn: () => put<{ quorum: QuorumResult }>(`/api/v1/meetings/${meeting.id}/attendance`, {
      records: Object.entries(drafts).map(([userId, d]) => ({ userId, status: d.status, mode: d.status === 'present' ? d.mode ?? 'in_person' : undefined, proxyHolderUserId: d.status === 'proxy' ? d.proxyHolderUserId : undefined })),
    }),
    onSuccess: (r) => {
      setDrafts({});
      message.success(r.quorum.isMet ? t('quorum.metNow', { counted: r.quorum.counted, required: r.quorum.required }) : t('quorum.notMetNow', { counted: r.quorum.counted, required: r.quorum.required }));
      void qc.invalidateQueries({ queryKey: ['quorum', meeting.id] });
      onChange();
    },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const depart = useMutation({
    mutationFn: (userId: string) => post<{ quorumLost: boolean; quorum: QuorumResult }>(`/api/v1/meetings/${meeting.id}/attendance/${userId}/depart`),
    onSuccess: (r) => {
      if (r.quorumLost) message.warning(t('quorum.lost', { counted: r.quorum.counted, required: r.quorum.required }));
      else message.success(t('attendance.departed'));
      void qc.invalidateQueries({ queryKey: ['quorum', meeting.id] });
      onChange();
    },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const set = (userId: string, patch: Draft) => setDrafts((d) => ({ ...d, [userId]: { ...currentOf(userId), ...d[userId], ...patch } }));
  const byId = useMemo(() => new Map(invitees.map((x) => [x.userId, x])), [invitees]);
  const currentOf = (userId: string): Draft => {
    const x = byId.get(userId);
    return { status: x?.attendanceStatus ?? undefined, mode: x?.attendanceMode ?? 'in_person', proxyHolderUserId: x?.proxyHolderUserId ?? undefined };
  };
  const value = (x: Invitee) => ({ ...currentOf(x.userId), ...drafts[x.userId] });

  return (
    <>
      {quorum.data && <QuorumSummary view={quorum.data} />}
      <div className="toolbar" style={{ justifyContent: 'space-between', marginTop: 12 }}>
        <Space wrap>
          <Tag>{t('invitations.counts', {
            invited: invitees.filter((x) => x.sentAt).length,
            accepted: invitees.filter((x) => x.response === 'accepted').length,
            declined: invitees.filter((x) => x.response === 'declined').length,
            pending: invitees.filter((x) => x.sentAt && x.response === 'pending').length,
          })}</Tag>
          {!meeting.invitationsSentAt && <Typography.Text type="secondary">{t('invitations.notSentYet')}</Typography.Text>}
        </Space>
        <Space>
          {canInvite && <Button icon={<UserAddOutlined />} onClick={() => setGuest(true)}>{t('invitations.addGuest')}</Button>}
          {canRecord && (
            <Button type="primary" icon={<SaveOutlined />} disabled={!Object.keys(drafts).length} loading={save.isPending} onClick={() => save.mutate()}>
              {t('attendance.save', { count: Object.keys(drafts).length })}
            </Button>
          )}
        </Space>
      </div>
      <Table<Invitee>
        rowKey="userId" size="small" loading={loading} dataSource={invitees} pagination={false} scroll={{ x: 1150 }}
        columns={[
          {
            title: t('common.name'), fixed: 'left', width: 220,
            render: (_, x) => (
              <div>
                {x.name}{x.userId === me?.user.id ? ' ★' : ''}
                <div className="item-meta">{[x.jobTitle, x.orgUnit].filter(Boolean).join(' — ')}</div>
              </div>
            ),
          },
          { title: t('bodies.role'), width: 170, render: (_, x) => (
            <Space size={2} direction="vertical">
              <Tag color={x.role === 'guest' ? 'purple' : x.role === 'alternate' ? 'gold' : undefined}>{t(`roles.${x.role}`, { defaultValue: t('invitations.guest') })}</Tag>
              {x.alternateForName && <span className="item-meta">{t('members.alternateFor')}: {x.alternateForName}</span>}
              {x.agendaItemTitle && <span className="item-meta">{t('invitations.forItem')}: {x.agendaItemTitle}</span>}
            </Space>
          ) },
          { title: t('bodies.voting'), width: 70, render: (_, x) => (x.isVoting ? t('common.yes') : t('common.no')) },
          { title: t('invitations.response'), width: 170, render: (_, x) => !x.sentAt ? <Tag>{t('invitations.notSent')}</Tag> : (
            <Tooltip title={x.responseNote}>
              <StatusTag group="rsvp" value={x.response ?? 'pending'} />
              {x.responseNote && <div className="item-meta">{x.responseNote}</div>}
            </Tooltip>
          ) },
          {
            title: t('attendance.status'), width: canRecord ? 360 : 200,
            render: (_, x) => {
              const v = value(x);
              if (!canRecord) return x.attendanceStatus ? (
                <span><StatusTag group="attendance" value={x.attendanceStatus} /> {x.attendanceMode && t(`enums.attendanceMode.${x.attendanceMode}`)} {x.proxyHolderName && `← ${x.proxyHolderName}`}</span>
              ) : '—';
              return (
                <Space size={4} wrap>
                  <Select size="small" style={{ width: 110 }} placeholder={t('attendance.choose')} value={v.status} onChange={(s) => set(x.userId, { status: s })}
                    options={['present', 'absent', 'excused', ...(x.isVoting && x.role !== 'alternate' ? ['proxy'] : [])].map((s) => ({ value: s, label: t(`enums.attendance.${s}`) }))} />
                  {v.status === 'present' && (
                    <Select size="small" style={{ width: 100 }} value={v.mode} onChange={(mode) => set(x.userId, { mode })}
                      options={['in_person', 'remote'].map((s) => ({ value: s, label: t(`enums.attendanceMode.${s}`) }))} />
                  )}
                  {v.status === 'proxy' && (
                    <Select size="small" style={{ width: 150 }} placeholder={t('attendance.proxyHolder')} value={v.proxyHolderUserId} onChange={(p) => set(x.userId, { proxyHolderUserId: p })}
                      options={voters.filter((o) => o.userId !== x.userId).map((o) => ({ value: o.userId, label: o.name }))} />
                  )}
                  {drafts[x.userId] && <Tag color="gold">{t('attendance.unsaved')}</Tag>}
                </Space>
              );
            },
          },
          { title: t('attendance.times'), width: 170, render: (_, x) => x.arrivedAt ? (
            <div className="item-meta">
              {formatDateTime(x.arrivedAt, i18n.language)}{x.leftAt && <div>{t('attendance.left')}: {formatDateTime(x.leftAt, i18n.language)}</div>}
            </div>
          ) : '—' },
          ...(canRecord && meeting.status === 'in_progress' ? [{
            title: '', width: 60,
            render: (_: unknown, x: Invitee) => x.attendanceStatus === 'present' && !x.leftAt && (
              <Tooltip title={t('attendance.depart')}><Button size="small" icon={<LogoutOutlined />} loading={depart.isPending} onClick={() => depart.mutate(x.userId)} /></Tooltip>
            ),
          }] : []),
        ]}
      />
      {guest && <GuestModal meetingId={meeting.id} agenda={agenda} onClose={() => { setGuest(false); onChange(); }} />}
    </>
  );
}

function GuestModal({ meetingId, agenda, onClose }: { meetingId: string; agenda: AgendaItem[]; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post(`/api/v1/meetings/${meetingId}/guests`, v),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('invitations.addGuest')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.add')} cancelText={t('common.cancel')}>
      <p style={{ color: '#5f6b64' }}>{t('invitations.guestHelp')}</p>
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)}>
        <Form.Item name="userId" label={t('bodies.user')} rules={[{ required: true }]}><UserSelect /></Form.Item>
        <Form.Item name="agendaItemId" label={t('invitations.forItem')}>
          <Select allowClear options={agenda.filter((a) => a.status === 'pending').map((a) => ({ value: a.id, label: `${a.position}. ${a.titleAr}` }))} />
        </Form.Item>
      </Form>
    </Modal>
  );
}

export function QuorumSummary({ view }: { view: QuorumView }) {
  const { t } = useTranslation();
  const r = view.result;
  return (
    <div className={`quorum-box ${r.isMet ? 'met' : 'unmet'}`}>
      {r.isMet ? <CheckCircleTwoTone twoToneColor="#006837" style={{ fontSize: 28 }} /> : <CloseCircleTwoTone twoToneColor="#b42318" style={{ fontSize: 28 }} />}
      <span className="quorum-num">{r.counted} / {r.required}</span>
      <div>
        <strong>{t(`quorum.mode.${view.mode}`)}: {r.isMet ? t('quorum.met') : t('quorum.notMet')}</strong>
        <div className="item-meta">{t('quorum.summary', { counted: r.counted, required: r.required, eligible: r.eligible })} — {quorumText(view.policy.quorum, t)} (v{view.policy.version})</div>
      </div>
      {r.chairRequired && <Tag color={r.chairPresent ? 'green' : 'red'}>{r.chairPresent ? t('quorum.chairPresent') : t('quorum.chairAbsent')}</Tag>}
    </div>
  );
}

export function QuorumPanel({ meetingId, canCheck }: { meetingId: string; canCheck: boolean }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['quorum', meetingId], queryFn: () => get<QuorumView>(`/api/v1/meetings/${meetingId}/quorum`) });
  const check = useMutation({
    mutationFn: () => post(`/api/v1/meetings/${meetingId}/quorum/check`),
    onSuccess: () => { message.success(t('quorum.recorded')); void qc.invalidateQueries({ queryKey: ['quorum', meetingId] }); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  if (q.error) return <ErrorAlert error={q.error} />;
  if (!q.data) return null;
  const v = q.data;
  return (
    <Space direction="vertical" style={{ width: '100%' }} size="middle">
      <QuorumSummary view={v} />
      <Space>
        <Button icon={<ReloadOutlined />} onClick={() => void q.refetch()}>{t('common.refresh')}</Button>
        {canCheck && <Button onClick={() => check.mutate()} loading={check.isPending}>{t('quorum.recordCheck')}</Button>}
      </Space>
      <Card size="small" title={t('quorum.breakdown')}>
        <Table rowKey={(l) => l.userId + l.role} size="small" pagination={false} dataSource={v.result.lines} columns={[
          { title: t('common.name'), dataIndex: 'name' },
          { title: t('bodies.role'), dataIndex: 'role', render: (r) => t(`roles.${r}`) },
          { title: t('quorum.principal'), dataIndex: 'principal', render: (p) => (p ? t('common.yes') : t('common.no')) },
          { title: t('quorum.counted'), dataIndex: 'counted', render: (c) => (c ? <Tag color="green">{t('common.yes')}</Tag> : <Tag>{t('common.no')}</Tag>) },
          { title: t('quorum.basis'), dataIndex: 'reason', render: (r: string) => t(r.startsWith('non_voting') ? 'quorum.reasons.non_voting' : `quorum.reasons.${r}`, { defaultValue: r }) },
        ]} />
      </Card>
      <Card size="small" title={t('quorum.history')}>
        <Table rowKey="id" size="small" pagination={false} dataSource={v.history} locale={{ emptyText: t('common.empty') }} columns={[
          { title: t('common.date'), dataIndex: 'checkedAt', render: (d) => formatDateTime(d, i18n.language) },
          { title: t('quorum.context'), dataIndex: 'context', render: (c) => t(`enums.quorumContext.${c}`) },
          { title: t('quorum.result'), render: (_, h) => <span><Tag color={h.isMet ? 'green' : 'red'}>{h.isMet ? t('quorum.met') : t('quorum.notMet')}</Tag> {h.counted}/{h.required} ({t('quorum.of')} {h.eligible})</span> },
          { title: t('quorum.overrideReason'), dataIndex: 'overrideReason', render: (o) => o ?? '—' },
          { title: t('activity.by'), dataIndex: 'checkedBy', render: (b) => b ?? '—' },
        ]} />
      </Card>
    </Space>
  );
}
