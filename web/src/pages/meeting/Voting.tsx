// التصويت داخل الاجتماع وبالتمرير: الفتح بقاعدة الجهة، والإدلاء بالصوت (بالأصالة أو الإنابة أو البدلية)، ورفع الأيدي، والإغلاق والنتيجة.
import { Alert, App, Button, Card, Empty, Input, Modal, Popconfirm, Progress, Radio, Select, Space, Table, Tag, Typography } from 'antd';
import { CheckOutlined, PlayCircleOutlined, StopOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { get, newRequestKey, post, postOnce, type Paged } from '../../api/client';
import type { Meeting, Motion, MotionList, ResolutionFull } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, StatusTag, errorText } from '../../components/common';
import { formatDateTime } from '../../lib/format';
import { VoteBars } from '../resolutions/ResolutionDetail';

export function VotingPanel({ meeting }: { meeting: Meeting }) {
  const { t } = useTranslation();
  const id = meeting.id;
  const qc = useQueryClient();
  const motions = useQuery({ queryKey: ['motions', id], queryFn: () => get<MotionList>(`/api/v1/meetings/${id}/motions`), refetchInterval: 15000 });
  const res = useQuery({ queryKey: ['meeting-resolutions', id], queryFn: () => get<Paged<ResolutionFull>>('/api/v1/resolutions', { meetingId: id, pageSize: 100 }) });
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['motions', id] }); void qc.invalidateQueries({ queryKey: ['meeting-resolutions', id] }); void qc.invalidateQueries({ queryKey: ['meeting', id] }); };
  if (motions.error || res.error) return <ErrorAlert error={motions.error ?? res.error} />;
  const list = motions.data;
  const resolutions = (res.data?.items ?? []).filter((r) => r.status !== 'withdrawn').sort((a, b) => a.position - b.position);
  const circulation = meeting.meetingType === 'circulation';
  const canOpen = (list?.canManage ?? false) && (circulation ? meeting.status === 'agenda_published' : meeting.status === 'in_progress');

  if (!meeting.minutesId) return <Alert type="info" showIcon message={t('voting.needMinutes')} />;
  if (!res.isLoading && resolutions.length === 0)
    return <Empty description={<span>{t('voting.noResolutions')} <Link to={`/minutes/${meeting.minutesId}`}>{t('meetings.openMinutes')}</Link></span>} />;
  return (
    <Space direction="vertical" style={{ width: '100%' }} size={12}>
      {!circulation && meeting.status !== 'in_progress' && <Alert type="info" showIcon message={t('voting.onlyInProgress')} />}
      {resolutions.map((r) => {
        const ms = (list?.items ?? []).filter((m) => m.resolutionId === r.id);
        const open = ms.find((m) => m.status === 'open');
        const last = [...ms].reverse().find((m) => m.status === 'closed');
        return (
          <Card key={r.id} size="small" className="vote-card"
            title={<Space wrap><Tag color={r.resolutionType === 'decision' ? 'green' : 'purple'}>{t(`enums.resolutionType.${r.resolutionType}`)}</Tag><span>{r.titleAr}</span></Space>}
            extra={<Space>{last && <StatusTag group="voteResult" value={last.result!} />}<StatusTag group="resolutionStatus" value={r.status} /></Space>}>
            <Typography.Paragraph type="secondary" ellipsis={{ rows: 2, expandable: true }} style={{ marginBottom: 8 }}>{r.textAr}</Typography.Paragraph>
            {open && <OpenMotion motion={open} canManage={list!.canManage} onChange={refresh} />}
            {!open && last && <ClosedMotion motion={last} />}
            {!open && r.status === 'proposed' && canOpen && (!last || ['tied_unresolved', 'no_quorum'].includes(last.result ?? '')) && <OpenVote meetingId={id} resolutionId={r.id} circulation={circulation} again={!!last} onChange={refresh} />}
          </Card>
        );
      })}
    </Space>
  );
}

function OpenVote({ meetingId, resolutionId, circulation, again, onChange }: { meetingId: string; resolutionId: string; circulation: boolean; again: boolean; onChange: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [method, setMethod] = useState(circulation ? 'circulation' : 'electronic');
  const [key] = useState(newRequestKey);
  const m = useMutation({
    mutationFn: () => postOnce(`/api/v1/meetings/${meetingId}/motions`, { resolutionId, method }, key),
    onSuccess: () => { message.success(t('voting.opened')); onChange(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Space wrap>
      {!circulation && <Select value={method} onChange={setMethod} style={{ width: 200 }} options={['electronic', 'show_of_hands', 'secret', 'consensus'].map((v) => ({ value: v, label: t(`enums.voteMethod.${v}`) }))} />}
      <Button type="primary" icon={<PlayCircleOutlined />} loading={m.isPending} onClick={() => m.mutate()}>{again ? t('voting.revote') : method === 'consensus' ? t('voting.adoptConsensus') : t('voting.open')}</Button>
    </Space>
  );
}

function OpenMotion({ motion, canManage, onChange }: { motion: Motion; canManage: boolean; onChange: () => void }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const { me } = useAuth();
  const [hands, setHands] = useState(false);
  const close = useMutation({
    mutationFn: () => post<{ result: string }>(`/api/v1/motions/${motion.id}/close`),
    onSuccess: (r) => { message.success(t('voting.closedResult', { result: t(`enums.voteResult.${r.result}`) })); onChange(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const withdraw = useMutation({
    mutationFn: () => post(`/api/v1/motions/${motion.id}/withdraw`, { reason: t('voting.withdrawnReason') }),
    onSuccess: onChange,
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <div className="motion-open">
      <Space wrap style={{ marginBottom: 8 }}>
        <Tag color="processing">{t('voting.openNow')}</Tag>
        <Tag>{t(`enums.voteMethod.${motion.method}`)}</Tag>
        {motion.round > 1 && <Tag>{t('voting.round', { n: motion.round })}</Tag>}
        {motion.deadlineAt && <Tag color="gold">{t('voting.deadline')}: {formatDateTime(motion.deadlineAt, i18n.language, me?.user.calendar)}</Tag>}
      </Space>
      <Progress percent={Math.round((motion.votedCount / Math.max(1, motion.eligibleCount)) * 100)} format={() => t('voting.progress', { v: motion.votedCount, e: motion.eligibleCount })} />
      {motion.method !== 'show_of_hands' && motion.myBallots.map((b) => <Ballot key={b.principalUserId} motion={motion} ballot={b} onChange={onChange} />)}
      {motion.method === 'show_of_hands' && motion.myBallots.length > 0 && <Typography.Text type="secondary">{t('voting.handsBySecretary')}</Typography.Text>}
      <div className="eligible-list">
        {motion.eligible.map((e) => (
          <Tag key={e.principalUserId} color={e.voted ? 'green' : 'default'}>
            {e.voted && <CheckOutlined />} {e.name}{e.capacity !== 'self' && ` (${t(`voting.capacity.${e.capacity}`)})`}
          </Tag>
        ))}
      </div>
      {canManage && (
        <Space wrap style={{ marginTop: 8 }}>
          {motion.method === 'show_of_hands' && <Button onClick={() => setHands(true)}>{t('voting.recordHands')}</Button>}
          <Popconfirm title={t('voting.closeConfirm')} onConfirm={() => close.mutate()} okText={t('common.confirm')} cancelText={t('common.cancel')}>
            <Button type="primary" danger icon={<StopOutlined />} loading={close.isPending}>{t('voting.close')}</Button>
          </Popconfirm>
          {motion.votedCount === 0 && <Button onClick={() => withdraw.mutate()} loading={withdraw.isPending}>{t('voting.withdraw')}</Button>}
        </Space>
      )}
      {hands && <HandsModal motion={motion} onClose={() => { setHands(false); onChange(); }} />}
    </div>
  );
}

function Ballot({ motion, ballot, onChange }: { motion: Motion; ballot: Motion['myBallots'][number]; onChange: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const { me } = useAuth();
  const [reservation, setReservation] = useState('');
  const [showRes, setShowRes] = useState(false);
  const [key] = useState(newRequestKey);
  const cast = useMutation({
    mutationFn: (choice: string) => postOnce<{ closed: boolean; result?: string }>(`/api/v1/motions/${motion.id}/votes`, {
      choice, principalUserId: ballot.principalUserId === me?.user.id ? undefined : ballot.principalUserId, reservation: reservation.trim() || undefined,
    }, key),
    onSuccess: (r) => { message.success(r.closed ? t('voting.closedResult', { result: t(`enums.voteResult.${r.result}`) }) : t('voting.recorded')); onChange(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const who = ballot.principalUserId === me?.user.id ? t('voting.yourVote') : t('voting.voteFor', { name: ballot.name, capacity: t(`voting.capacity.${ballot.capacity}`) });
  if (ballot.voted)
    return <Alert type="success" showIcon style={{ margin: '8px 0' }} message={`${who}: ${ballot.choice ? t(`voting.${ballot.choice}`) : t('voting.castSecret')}`} />;
  return (
    <div className="ballot">
      <Typography.Text strong>{who}</Typography.Text>
      <Space wrap style={{ marginTop: 6 }}>
        <Button className="vote-for" loading={cast.isPending} onClick={() => cast.mutate('for')}>{t('voting.for')}</Button>
        <Button className="vote-against" loading={cast.isPending} onClick={() => cast.mutate('against')}>{t('voting.against')}</Button>
        <Button loading={cast.isPending} onClick={() => cast.mutate('abstain')}>{t('voting.abstain')}</Button>
        <Button type="link" onClick={() => setShowRes((x) => !x)}>{t('voting.addReservation')}</Button>
      </Space>
      {showRes && <Input.TextArea style={{ marginTop: 6 }} rows={2} maxLength={2000} value={reservation} onChange={(e) => setReservation(e.target.value)} placeholder={t('voting.reservationPh')} />}
      {motion.method === 'secret' && <div className="item-meta">{t('voting.secretNote')}</div>}
    </div>
  );
}

function HandsModal({ motion, onClose }: { motion: Motion; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const pending = motion.eligible.filter((e) => !e.voted);
  const [choices, setChoices] = useState<Record<string, string>>({});
  const m = useMutation({
    mutationFn: () => post(`/api/v1/motions/${motion.id}/record`, { votes: Object.entries(choices).map(([principalUserId, choice]) => ({ principalUserId, choice })) }),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open width={640} title={t('voting.recordHands')} onCancel={onClose} onOk={() => m.mutate()} confirmLoading={m.isPending}
      okButtonProps={{ disabled: Object.keys(choices).length === 0 }} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Table size="small" rowKey="principalUserId" pagination={false} dataSource={pending} columns={[
        { title: t('common.name'), render: (_, e) => <span>{e.name}{e.capacity !== 'self' && <span className="item-meta"> ({t(`voting.capacity.${e.capacity}`)})</span>}</span> },
        {
          title: t('voting.choice'), render: (_, e) => (
            <Radio.Group value={choices[e.principalUserId]} onChange={(ev) => setChoices((c) => ({ ...c, [e.principalUserId]: ev.target.value }))}
              options={['for', 'against', 'abstain'].map((v) => ({ value: v, label: t(`voting.${v}`) }))} optionType="button" size="small" />
          ),
        },
      ]} />
    </Modal>
  );
}

function ClosedMotion({ motion }: { motion: Motion }) {
  const { t } = useTranslation();
  const tl = motion.tally;
  return (
    <div className="motion-closed">
      <Space wrap style={{ marginBottom: 6 }}>
        <Tag>{t(`enums.voteMethod.${motion.method}`)}</Tag>
        {tl?.chairCasting && <Tag color="gold">{t('voting.chairCasting')}</Tag>}
        {motion.round > 1 && <Tag>{t('voting.round', { n: motion.round })}</Tag>}
      </Space>
      {tl && motion.method !== 'consensus' && <VoteBars f={tl.for} a={tl.against} s={tl.abstain} />}
      {motion.votes && motion.votes.length > 0 && (
        <Table size="small" rowKey={(v) => v.principalUserId ?? v.castAt} pagination={false} dataSource={motion.votes} style={{ marginTop: 8 }} columns={[
          { title: t('common.name'), dataIndex: 'name' },
          { title: t('voting.choice'), dataIndex: 'choice', render: (v) => t(`voting.${v}`) },
          { title: t('voting.capacityTitle'), dataIndex: 'capacity', render: (v) => t(`voting.capacity.${v}`, { defaultValue: v ?? '' }) },
        ]} />
      )}
      {motion.method === 'secret' && <div className="item-meta">{t('voting.secretNote')}</div>}
      {motion.reservations.length > 0 && <ul className="minutes-list">{motion.reservations.map((r, i) => <li key={i}><b>{r.name}:</b> {r.text}</li>)}</ul>}
    </div>
  );
}
