// محرر المحضر العربي (RTL): يُبنى من قالب الجهة؛ الأقسام النصية تُحرر، والآلية تُعرض من بيانات الاجتماع (الحضور، النصاب، القرارات، التصويت…).
import { Alert, App, Button, Descriptions, Empty, Input, Popconfirm, Space, Table, Tag, Typography } from 'antd';
import { DeleteOutlined, EditOutlined, PlusOutlined, SaveOutlined, ScheduleOutlined } from '@ant-design/icons';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { post, put } from '../../api/client';
import type { MinutesDetail, MinutesSnapshot, TemplateSection } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { StatusTag, errorText } from '../../components/common';
import { Attachments } from '../../components/governance';
import { formatDateTime } from '../../lib/format';
import { CreateTaskModal } from '../TasksPages';
import { ResolutionModal } from './ResolutionModal';

type Texts = Record<string, { title: string; body: string }>;
type Items = Record<string, { discussion: string; recommendation: string }>;

export function MinutesEditor({ detail, onChange }: { detail: MinutesDetail; onChange: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const m = detail.minutes;
  const editable = detail.actions.canEdit;
  const [texts, setTexts] = useState<Texts>({});
  const [items, setItems] = useState<Items>({});
  const [dirty, setDirty] = useState(false);
  const [resolution, setResolution] = useState<{ id?: string } | null>(null);
  const [task, setTask] = useState<{ id: string; title: string } | null>(null);

  useEffect(() => {
    const tx: Texts = {};
    for (const s of m.content.sections ?? []) tx[s.key] = { title: s.title, body: s.body ?? '' };
    for (const s of detail.template.filter((x) => x.kind === 'text')) if (!tx[s.key]) tx[s.key] = { title: s.titleAr, body: s.defaultBody ?? '' };
    const it: Items = {};
    for (const a of detail.snapshot.agenda) it[a.id] = { discussion: a.discussion ?? '', recommendation: a.recommendation ?? '' };
    setTexts(tx);
    setItems(it);
    setDirty(false);
  }, [m.updatedAt, m.status, detail.snapshot.agenda, detail.template, m.content.sections]);

  const save = useMutation({
    mutationFn: () => put(`/api/v1/minutes/${m.id}/draft`, {
      sections: Object.entries(texts).map(([key, v]) => ({ key, title: v.title, body: v.body })),
      items: Object.entries(items).map(([agendaItemId, v]) => ({ agendaItemId, discussion: v.discussion || null, recommendation: v.recommendation || null })),
    }),
    onSuccess: () => { message.success(t('common.saved')); setDirty(false); onChange(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });

  const withdraw = useMutation({
    mutationFn: (id: string) => post(`/api/v1/resolutions/${id}/withdraw`, { reason: t('minutes.withdrawnFromDraft') }),
    onSuccess: () => { message.success(t('common.saved')); onChange(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });

  const sections: TemplateSection[] = detail.template.length ? detail.template
    : (m.content.sections ?? []).map((s) => ({ key: s.key, titleAr: s.title, kind: 'text' as const }));
  const extra = Object.keys(texts).filter((k) => !sections.some((s) => s.key === k));

  return (
    <div className="minutes-doc">
      {editable && dirty && (
        <Alert type="warning" showIcon className="minutes-savebar" message={t('minutes.unsaved')}
          action={<Button type="primary" size="small" icon={<SaveOutlined />} loading={save.isPending} onClick={() => save.mutate()}>{t('minutes.saveDraft')}</Button>} />
      )}
      {!editable && m.status !== 'approved' && <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('minutes.lockedInReview')} />}
      {sections.map((s) => (
        <section key={s.key} className="minutes-section" id={`sec-${s.key}`}>
          <h3>{texts[s.key]?.title || s.titleAr}</h3>
          {s.kind === 'text'
            ? <TextSection value={texts[s.key]?.body ?? ''} editable={editable} onChange={(body) => { setTexts((x) => ({ ...x, [s.key]: { title: x[s.key]?.title ?? s.titleAr, body } })); setDirty(true); }} />
            : <AutoSection source={s.source ?? ''} detail={detail} items={items} editable={editable}
              onItem={(id, field, v) => { setItems((x) => ({ ...x, [id]: { ...x[id], [field]: v } })); setDirty(true); }}
              onAddResolution={() => setResolution({})} onEditResolution={(id) => setResolution({ id })} onWithdraw={(id) => withdraw.mutate(id)}
              onAddTask={(id, title) => setTask({ id, title })} />}
        </section>
      ))}
      {extra.map((k) => (
        <section key={k} className="minutes-section">
          <h3>{texts[k].title}</h3>
          <TextSection value={texts[k].body} editable={editable} onChange={(body) => { setTexts((x) => ({ ...x, [k]: { ...x[k], body } })); setDirty(true); }} />
        </section>
      ))}
      {editable && (
        <Space style={{ marginTop: 8 }}>
          <Button type="primary" icon={<SaveOutlined />} disabled={!dirty} loading={save.isPending} onClick={() => save.mutate()}>{t('minutes.saveDraft')}</Button>
        </Space>
      )}
      {resolution && <ResolutionModal minutesId={m.id} bodyId={m.bodyId} agenda={detail.snapshot.agenda} resolutionId={resolution.id}
        onClose={() => { setResolution(null); onChange(); void qc.invalidateQueries({ queryKey: ['minutes-resolutions', m.id] }); }} />}
      {task && <CreateTaskModal bodyId={m.bodyId} resolutionId={task.id} resolutionTitle={task.title} onClose={() => { setTask(null); onChange(); }} />}
    </div>
  );
}

function TextSection({ value, editable, onChange }: { value: string; editable: boolean; onChange: (v: string) => void }) {
  const { t } = useTranslation();
  if (editable) return <Input.TextArea value={value} onChange={(e) => onChange(e.target.value)} autoSize={{ minRows: 2, maxRows: 14 }} placeholder={t('minutes.writeHere')} />;
  return value ? <Typography.Paragraph style={{ whiteSpace: 'pre-wrap' }}>{value}</Typography.Paragraph> : <Typography.Text type="secondary">—</Typography.Text>;
}

interface AutoProps {
  source: string;
  detail: MinutesDetail;
  items: Items;
  editable: boolean;
  onItem: (id: string, field: 'discussion' | 'recommendation', v: string) => void;
  onAddResolution: () => void;
  onEditResolution: (id: string) => void;
  onWithdraw: (id: string) => void;
  onAddTask: (id: string, title: string) => void;
}

function AutoSection(p: AutoProps) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const s: MinutesSnapshot = p.detail.snapshot;
  const dt = (v?: string | null) => formatDateTime(v, i18n.language, me?.user.calendar);
  const auto = <Tag className="auto-tag">{t('minutes.autoFilled')}</Tag>;
  switch (p.source) {
    case 'meeting_info':
      return (
        <>
          {auto}
          <Descriptions size="small" bordered column={{ xs: 1, md: 2 }}>
            <Descriptions.Item label={t('meetings.reference')}><span className="mono">{s.meeting.referenceNo}</span></Descriptions.Item>
            <Descriptions.Item label={t('common.body')}>{s.meeting.bodyName} <span className="mono">({s.meeting.bodyReference})</span></Descriptions.Item>
            <Descriptions.Item label={t('meetings.type')}>{t(`enums.meetingType.${s.meeting.meetingType}`)} — {t(`enums.mode.${s.meeting.mode}`)}</Descriptions.Item>
            <Descriptions.Item label={t('meetings.startsAt')}>{dt(s.meeting.startsAt)}</Descriptions.Item>
            <Descriptions.Item label={t('meetings.actualStart')}>{dt(s.meeting.actualStartedAt)}</Descriptions.Item>
            <Descriptions.Item label={t('meetings.actualEnd')}>{dt(s.meeting.actualEndedAt)}</Descriptions.Item>
            <Descriptions.Item label={t('meetings.location')}>{s.meeting.location ?? '—'}</Descriptions.Item>
            <Descriptions.Item label={t('meetings.previous')}>{s.meeting.previousReference ?? '—'}</Descriptions.Item>
            <Descriptions.Item label={t('bodies.chair')}>{s.meeting.chairName ?? '—'}</Descriptions.Item>
            <Descriptions.Item label={t('bodies.secretary')}>{s.meeting.secretaryName ?? '—'}</Descriptions.Item>
          </Descriptions>
        </>
      );
    case 'attendance':
      return (
        <>
          {auto}
          <Table size="small" pagination={false} rowKey="userId" dataSource={s.attendance} columns={[
            { title: t('common.name'), dataIndex: 'name' },
            { title: t('members.role'), dataIndex: 'role', render: (v) => t(`roles.${v}`) },
            { title: t('attendance.status'), render: (_, a) => a.status
              ? <Space size={4}><StatusTag group="attendance" value={a.status} />{a.mode && a.status === 'present' && <Tag>{t(`enums.attendanceMode.${a.mode}`)}</Tag>}</Space>
              : <Typography.Text type="secondary">{t('minutes.notRecorded')}</Typography.Text> },
            { title: t('common.notes'), render: (_, a) => a.status === 'proxy' ? t('minutes.proxyTo', { name: a.proxyHolder }) : a.leftAt ? t('minutes.leftEarly') : a.note ?? '' },
          ]} />
          {s.guests.length > 0 && <Typography.Paragraph style={{ marginTop: 8 }}>{t('minutes.guests')}: {s.guests.map((g) => g.name).join('، ')}</Typography.Paragraph>}
        </>
      );
    case 'quorum':
      return s.quorum ? (
        <div className={`quorum-box ${s.quorum.isMet ? 'met' : 'unmet'}`}>
          <span className="quorum-num">{s.quorum.counted} / {s.quorum.required}</span>
          <span>{t('quorum.summary', { counted: s.quorum.counted, required: s.quorum.required, eligible: s.quorum.eligible })}</span>
          {s.quorum.overrideReason && <Tag color="orange">{t('minutes.override')}: {s.quorum.overrideReason}</Tag>}
        </div>
      ) : <Typography.Text type="secondary">{t('minutes.noQuorumCheck')}</Typography.Text>;
    case 'agenda':
      return (
        <ol className="minutes-agenda">
          {s.agenda.map((a) => <li key={a.id}>{a.titleAr} <StatusTag group="itemStatus" value={a.status} />{a.presenter && <span className="item-meta"> — {a.presenter}</span>}</li>)}
        </ol>
      );
    case 'discussions':
      return (
        <>
          {s.agenda.filter((a) => a.status !== 'withdrawn').map((a) => (
            <div key={a.id} className="minutes-item">
              <Typography.Text strong>{t('minutes.itemN', { n: a.position })}: {a.titleAr}</Typography.Text>
              {p.editable ? (
                <>
                  <Input.TextArea value={p.items[a.id]?.discussion ?? ''} onChange={(e) => p.onItem(a.id, 'discussion', e.target.value)} autoSize={{ minRows: 2, maxRows: 10 }}
                    placeholder={t('minutes.discussionPlaceholder')} style={{ marginTop: 6 }} />
                  <Input value={p.items[a.id]?.recommendation ?? ''} onChange={(e) => p.onItem(a.id, 'recommendation', e.target.value)} placeholder={t('minutes.recommendationPlaceholder')} style={{ marginTop: 6 }} />
                </>
              ) : (
                <>
                  <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', marginTop: 4 }}>{a.discussion || '—'}</Typography.Paragraph>
                  {a.recommendation && <Typography.Paragraph><b>{t('agenda.recommendation')}:</b> {a.recommendation}</Typography.Paragraph>}
                </>
              )}
            </div>
          ))}
        </>
      );
    case 'decisions':
      return <Decisions {...p} />;
    case 'votes': {
      const voted = s.resolutions.filter((r) => r.vote);
      return voted.length ? (
        <Table size="small" pagination={false} rowKey="id" dataSource={voted} columns={[
          { title: t('minutes.decision'), dataIndex: 'titleAr' },
          { title: t('voting.method'), render: (_, r) => t(`enums.voteMethod.${r.vote!.method}`) },
          { title: t('voting.for'), render: (_, r) => r.vote!.for, width: 60 },
          { title: t('voting.against'), render: (_, r) => r.vote!.against, width: 60 },
          { title: t('voting.abstain'), render: (_, r) => r.vote!.abstain, width: 70 },
          { title: t('voting.result'), render: (_, r) => <Space size={4}><StatusTag group="voteResult" value={r.vote!.result} />{r.vote!.chairCasting && <Tag>{t('voting.chairCasting')}</Tag>}</Space> },
        ]} />
      ) : <Typography.Text type="secondary">{t('minutes.noVotes')}</Typography.Text>;
    }
    case 'reservations': {
      const signoffRes = p.detail.signoffs.filter((x) => x.reservation && x.status === 'valid');
      if (!s.reservations.length && !signoffRes.length) return <Typography.Text type="secondary">{t('minutes.noReservations')}</Typography.Text>;
      return (
        <ul className="minutes-list">
          {s.reservations.map((r, i) => <li key={i}><b>{r.name}</b> — {t('minutes.onResolution', { title: r.resolutionTitle })}: {r.text}</li>)}
          {signoffRes.map((r) => <li key={r.id}><b>{r.name}</b> — {t('minutes.atSignoff')}: {r.reservation}</li>)}
        </ul>
      );
    }
    case 'tasks':
      return s.tasks.length ? (
        <Table size="small" pagination={false} rowKey="id" dataSource={s.tasks} columns={[
          { title: t('common.title'), render: (_, x) => <Link to={`/tasks/${x.id}`}>{x.referenceNo ? <><span className="mono">{x.referenceNo}</span>{" — "}</> : null}{x.titleAr}</Link> },
          { title: t('tasks.assignee'), dataIndex: 'assignee' },
          { title: t('common.dueDate'), dataIndex: 'dueDate' },
          { title: t('minutes.decision'), dataIndex: 'resolutionTitle' },
          { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="taskStatus" value={v} /> },
        ]} />
      ) : <Typography.Text type="secondary">{t('minutes.noTasks')}</Typography.Text>;
    case 'attachments':
      return (
        <>
          {s.attachments.length > 0 && (
            <ul className="minutes-list">{s.attachments.map((a, i) => <li key={i}>{a.fileName} <Tag>{t(`minutes.owner.${a.ownerType}`, { defaultValue: a.ownerType })}</Tag></li>)}</ul>
          )}
          <Attachments ownerType="minutes" ownerId={p.detail.minutes.id} canUpload={p.detail.permissions.includes('document.upload')}
            purposes={['attachment', 'wet_signature', 'approval_evidence']} />
        </>
      );
    case 'approval':
      return <ApprovalSummary detail={p.detail} />;
    default:
      return <Empty />;
  }
}

function Decisions(p: AutoProps) {
  const { t } = useTranslation();
  const list = p.detail.snapshot.resolutions;
  const canManage = p.detail.actions.canManageResolutions;
  const byPos = useMemo(() => new Map(p.detail.snapshot.agenda.map((a) => [a.id, a.position])), [p.detail.snapshot.agenda]);
  return (
    <>
      {list.length === 0 && <Typography.Text type="secondary">{t('minutes.noResolutions')}</Typography.Text>}
      {list.map((r) => (
        <div key={r.id} className={`resolution-card ${r.type}`}>
          <div className="resolution-head">
            <Space wrap size={6}>
              {r.number ? <Link to={`/resolutions/${r.id}`} className="mono">{r.number}</Link> : <Tag>{t('resolutions.proposedN', { n: r.position })}</Tag>}
              <Tag color={r.type === 'decision' ? 'green' : 'purple'}>{t(`enums.resolutionType.${r.type}`)}</Tag>
              <Tag>{t(`enums.resolutionCategory.${r.category}`)}</Tag>
              {r.agendaItemId && <Tag color="default">{t('minutes.itemN', { n: byPos.get(r.agendaItemId) ?? r.agendaPosition })}</Tag>}
              {r.vote ? <StatusTag group="voteResult" value={r.vote.result} /> : <Tag>{t(`enums.adoptionMethod.${r.adoptionMethod}`)}</Tag>}
              {r.status !== 'proposed' && <StatusTag group="resolutionStatus" value={r.status} />}
            </Space>
            {canManage && r.status === 'proposed' && (
              <Space size={4}>
                <Button size="small" type="text" icon={<EditOutlined />} onClick={() => p.onEditResolution(r.id)} aria-label={t('common.edit')} />
                <Button size="small" type="text" icon={<ScheduleOutlined />} onClick={() => p.onAddTask(r.id, r.titleAr)}>{t('minutes.addTask')}</Button>
                <Popconfirm title={t('minutes.withdrawConfirm')} onConfirm={() => p.onWithdraw(r.id)} okText={t('common.confirm')} cancelText={t('common.cancel')}>
                  <Button size="small" type="text" danger icon={<DeleteOutlined />} aria-label={t('minutes.withdraw')} />
                </Popconfirm>
              </Space>
            )}
          </div>
          <Typography.Text strong>{r.titleAr}</Typography.Text>
          <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', margin: '4px 0' }}>{r.textAr}</Typography.Paragraph>
          <div className="item-meta">
            {[
              r.authorityText && `${t('resolutions.authority')}: ${r.authorityText}`,
              r.targetBody && `${t('resolutions.target')}: ${r.targetBody}`,
              r.responsible && `${t('resolutions.responsible')}: ${r.responsible}`,
              r.supersedes && `${t(`enums.supersedeKind.${r.supersedeKind}`)}: ${r.supersedes}`,
              r.vote && t('voting.tallyLine', { f: r.vote.for, a: r.vote.against, s: r.vote.abstain }),
            ].filter(Boolean).join(' · ')}
          </div>
        </div>
      ))}
      {canManage && <Button icon={<PlusOutlined />} onClick={p.onAddResolution} style={{ marginTop: 8 }}>{t('minutes.proposeResolution')}</Button>}
    </>
  );
}

function ApprovalSummary({ detail }: { detail: MinutesDetail }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const current = detail.currentVersion?.no;
  const signoffs = detail.signoffs.filter((s) => s.versionNo === current && s.status !== 'invalidated');
  const approvals = detail.approvals;
  if (!signoffs.length && !approvals.length) return <Typography.Text type="secondary">{t('minutes.notYetReviewed')}</Typography.Text>;
  return (
    <>
      {signoffs.length > 0 && (
        <Table size="small" pagination={false} rowKey="id" dataSource={signoffs} style={{ marginBottom: 8 }} columns={[
          { title: t('common.name'), dataIndex: 'name' },
          { title: t('minutes.method'), dataIndex: 'method', render: (v) => t(`enums.signoffMethod.${v}`) },
          { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="signoffStatus" value={v} /> },
          { title: t('common.date'), dataIndex: 'signedAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
        ]} />
      )}
      <ul className="minutes-list">
        {approvals.map((a) => (
          <li key={a.id}>
            <b>{t(`enums.approvalDecision.${a.decision}`)}</b> — {t(`enums.minutesStatus.${a.step}`)} — {a.actor}
            {a.onBehalfOf && ` (${t('minutes.onBehalfOf', { name: a.onBehalfOf })})`}
            {a.externalApprover && ` — ${t('minutes.externalApprover')}: ${a.externalApprover} (${a.externalReference})`}
            <span className="item-meta"> — {formatDateTime(a.createdAt, i18n.language, me?.user.calendar)}</span>
          </li>
        ))}
      </ul>
    </>
  );
}
