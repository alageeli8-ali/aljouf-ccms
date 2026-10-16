// سجل القرار المستقل: المصدر، والنص الرسمي، ونتيجة التصويت، والجهة المنفذة، والمهام، وحالة التنفيذ، والقرار الذي يعدّله أو يلغيه، وتاريخ الحالات.
import { Alert, Button, Card, Col, Descriptions, Dropdown, Form, Input, Progress, Radio, Row, Skeleton, Space, Table, Tag, Timeline, Typography } from 'antd';
import { DownOutlined, PlusOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router-dom';
import { get, post } from '../../api/client';
import type { ResolutionDetail } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag } from '../../components/common';
import { Attachments, useReasonPrompt } from '../../components/governance';
import { formatDate, formatDateTime } from '../../lib/format';
import { CreateTaskModal } from '../TasksPages';

const TRANSITIONS: Record<string, string[]> = {
  issued: ['start_execution', 'mark_executed', 'refer', 'cancel'],
  in_execution: ['mark_executed', 'cancel'],
  executed: ['close'],
  referred: ['record_outcome', 'cancel'],
};

export default function ResolutionDetailPage() {
  const { id } = useParams();
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const qc = useQueryClient();
  const reason = useReasonPrompt();
  const [task, setTask] = useState(false);
  const q = useQuery({ queryKey: ['resolution', id], queryFn: () => get<ResolutionDetail>(`/api/v1/resolutions/${id}`) });
  if (q.isLoading) return <Skeleton active />;
  if (q.error) return <ErrorAlert error={q.error} />;
  const d = q.data!;
  const r = d.resolution;
  const manage = d.permissions.includes('resolution.manage');
  const dt = (v?: string | null) => formatDateTime(v, i18n.language, me?.user.calendar);
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['resolution', id] }); void qc.invalidateQueries({ queryKey: ['resolutions'] }); };

  const transition = (action: string) => reason.prompt({
    title: t(`resolutions.actions.${action}`), description: t(`resolutions.actionHelp.${action}`),
    danger: action === 'cancel', reasonRequired: ['mark_executed', 'cancel', 'record_outcome'].includes(action),
    extra: <>
      {action === 'record_outcome' && (
        <Form.Item name="outcome" label={t('resolutions.outcome')} rules={[{ required: true }]}>
          <Radio.Group optionType="button" options={['adopted', 'modified', 'rejected'].map((v) => ({ value: v, label: t(`enums.referralOutcome.${v}`) }))} />
        </Form.Item>
      )}
      {['cancel', 'record_outcome'].includes(action) && (
        <Form.Item name="reference" label={t('resolutions.reference')} rules={action === 'cancel' ? [{ required: true }] : []}><Input /></Form.Item>
      )}
    </>,
    onSubmit: async (v) => { await post(`/api/v1/resolutions/${id}/transition`, { action, ...v }); refresh(); },
  });

  const actions = (TRANSITIONS[r.status] ?? []).filter((a) => a !== 'refer' || (r.resolutionType === 'recommendation' && (r.targetBodyId || r.targetAuthorityText)));
  const v = r.voteSummary;
  return (
    <>
      <PageHeader
        title={r.titleAr}
        subtitle={<Space wrap>
          {r.number ? <span className="mono">{r.number}</span> : <Tag>{t('resolutions.unnumbered')}</Tag>}
          <Tag color={r.resolutionType === 'decision' ? 'green' : 'purple'}>{t(`enums.resolutionType.${r.resolutionType}`)}</Tag>
          <Tag>{t(`enums.resolutionCategory.${r.category}`)}</Tag>
          <StatusTag group="resolutionStatus" value={r.status} />
        </Space>}
        extra={<>
          {manage && ['issued', 'in_execution', 'referred'].includes(r.status) && <Button icon={<PlusOutlined />} onClick={() => setTask(true)}>{t('resolutions.newTask')}</Button>}
          {manage && actions.length > 0 && (
            <Dropdown menu={{ items: actions.map((a) => ({ key: a, label: t(`resolutions.actions.${a}`), danger: a === 'cancel', onClick: () => transition(a) })) }}>
              <Button type="primary">{t('resolutions.changeStatus')} <DownOutlined /></Button>
            </Dropdown>
          )}
        </>}
      />
      {r.statusReason && <Alert showIcon type={['cancelled', 'superseded', 'rejected'].includes(r.status) ? 'warning' : 'info'} style={{ marginBottom: 12 }} message={r.statusReason} />}
      {d.supersededBy.map((s) => (
        <Alert key={s.id} type="warning" showIcon style={{ marginBottom: 12 }}
          message={<span>{t(`resolutions.supersededBy.${s.kind}`)} <Link to={`/resolutions/${s.id}`}>{s.number ?? s.titleAr}</Link></span>} />
      ))}
      <Row gutter={16}>
        <Col xs={24} lg={16}>
          <Card title={t('resolutions.officialText')} style={{ marginBottom: 16 }}>
            <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', fontSize: 15, lineHeight: 1.9 }}>{r.textAr}</Typography.Paragraph>
            {r.textEn && <Typography.Paragraph dir="ltr" type="secondary" style={{ whiteSpace: 'pre-wrap' }}>{r.textEn}</Typography.Paragraph>}
          </Card>
          <Card title={t('resolutions.details')} style={{ marginBottom: 16 }}>
            <Descriptions bordered size="small" column={{ xs: 1, md: 2 }}>
              <Descriptions.Item label={t('common.body')}><Link to={`/bodies/${r.bodyId}`}>{r.bodyName}</Link></Descriptions.Item>
              <Descriptions.Item label={t('resolutions.meeting')}>{r.meetingId ? <Link to={`/meetings/${r.meetingId}`} className="mono">{r.meetingReference}</Link> : '—'}</Descriptions.Item>
              <Descriptions.Item label={t('resolutions.agendaItem')}>{r.agendaItemTitle ? `${r.agendaItemPosition}. ${r.agendaItemTitle}` : '—'}</Descriptions.Item>
              <Descriptions.Item label={t('resolutions.minutes')}>{r.minutesId ? <Link to={`/minutes/${r.minutesId}`}>{t('resolutions.minutesVersion', { v: r.minutesVersionNo ?? '—' })}</Link> : '—'}</Descriptions.Item>
              <Descriptions.Item label={t('resolutions.issuedAt')}>{dt(r.issuedAt)}</Descriptions.Item>
              <Descriptions.Item label={t('resolutions.approvedAt')}>{dt(r.approvedAt)}</Descriptions.Item>
              <Descriptions.Item label={t('resolutions.authority')}>{r.authorityText ?? '—'}</Descriptions.Item>
              <Descriptions.Item label={t('resolutions.adoptionMethod')}>{t(`enums.adoptionMethod.${r.adoptionMethod}`)}</Descriptions.Item>
              <Descriptions.Item label={t('resolutions.responsible')}>{[r.responsibleOrgUnitName, r.responsibleEntityName, r.responsibleText].filter(Boolean).join(' — ') || '—'}</Descriptions.Item>
              <Descriptions.Item label={t('resolutions.requiresFollowUp')}>{r.requiresFollowUp ? t('common.yes') : t('common.no')}</Descriptions.Item>
              {r.resolutionType === 'recommendation' && <Descriptions.Item label={t('resolutions.target')}>{r.targetBodyName ?? r.targetAuthorityText ?? '—'}</Descriptions.Item>}
              {r.referralOutcome && <Descriptions.Item label={t('resolutions.outcome')}>{t(`enums.referralOutcome.${r.referralOutcome}`)} — {r.referralNote}</Descriptions.Item>}
              {r.supersedesResolutionId && <Descriptions.Item label={t(`enums.supersedeKind.${r.supersedeKind}`)}><Link to={`/resolutions/${r.supersedesResolutionId}`}>{r.supersedesNumber ?? r.supersedesTitle}</Link></Descriptions.Item>}
            </Descriptions>
          </Card>
          <Card title={t('resolutions.linkedTasks')} style={{ marginBottom: 16 }}>
            <Table size="small" rowKey="id" pagination={false} dataSource={d.tasks} locale={{ emptyText: t('resolutions.noTasks') }} columns={[
              { title: t('common.title'), render: (_, x) => <Link to={`/tasks/${x.id}`}>{x.referenceNo && <><span className="mono">{x.referenceNo}</span>{" — "}</>}{x.titleAr}</Link> },
              { title: t('tasks.assignee'), dataIndex: 'assigneeName' },
              { title: t('common.dueDate'), dataIndex: 'dueDate', render: (v) => formatDate(v, i18n.language, me?.user.calendar) },
              { title: t('tasks.progress'), dataIndex: 'progressPct', render: (p: number) => <Progress percent={p} size="small" style={{ width: 120 }} /> },
              { title: t('common.status'), dataIndex: 'status', render: (s) => <StatusTag group="taskStatus" value={s} /> },
            ]} />
          </Card>
          <Card title={`${t('nav.attachments')} (${d.documentCount})`}>
            <Attachments ownerType="resolution" ownerId={r.id} canUpload={d.permissions.includes('document.upload')} />
          </Card>
        </Col>
        <Col xs={24} lg={8}>
          <Card title={t('resolutions.voteResult')} style={{ marginBottom: 16 }}>
            {v ? (
              <Space direction="vertical" style={{ width: '100%' }}>
                <Space><StatusTag group="voteResult" value={v.result} /><Tag>{t(`enums.voteMethod.${v.method}`)}</Tag>{v.chairCasting && <Tag>{t('voting.chairCasting')}</Tag>}</Space>
                <VoteBars f={v.for} a={v.against} s={v.abstain} />
                <span className="item-meta">{t('voting.basis', { present: v.present, members: v.members, threshold: t(`enums.votingThreshold.${v.threshold}`) })}</span>
              </Space>
            ) : <Typography.Text type="secondary">{t(`enums.adoptionMethod.${r.adoptionMethod}`)}</Typography.Text>}
          </Card>
          {d.reservations.length > 0 && (
            <Card title={t('resolutions.reservations')} style={{ marginBottom: 16 }}>
              {d.reservations.map((x, i) => <Typography.Paragraph key={i}><b>{x.name}:</b> {x.text}</Typography.Paragraph>)}
            </Card>
          )}
          <Card title={t('resolutions.history')}>
            <Timeline items={d.history.map((h, i) => ({
              key: i, color: ['cancelled', 'superseded', 'rejected'].includes(h.toStatus) ? 'red' : h.toStatus === 'proposed' ? 'gray' : 'green',
              children: <div>
                <StatusTag group="resolutionStatus" value={h.toStatus} />
                <div className="item-meta">{h.changedBy ?? t('activity.system')} — {dt(h.changedAt)}</div>
                {h.reason && <div>{h.reason}</div>}
                {h.reference && <div className="item-meta">{t('resolutions.reference')}: {h.reference}</div>}
              </div>,
            }))} />
          </Card>
        </Col>
      </Row>
      {task && <CreateTaskModal bodyId={r.bodyId} resolutionId={r.id} resolutionTitle={r.titleAr} onClose={() => { setTask(false); refresh(); }} />}
      {reason.node}
    </>
  );
}

export function VoteBars({ f, a, s }: { f: number; a: number; s: number }) {
  const { t } = useTranslation();
  const total = Math.max(1, f + a + s);
  return (
    <div className="vote-bars">
      {[['for', f, '#006837'], ['against', a, '#b42318'], ['abstain', s, '#B28655']].map(([k, n, c]) => (
        <div key={k as string} className="vote-bar-row">
          <span className="vote-bar-label">{t(`voting.${k}`)}</span>
          <div className="vote-bar-track"><div className="vote-bar-fill" style={{ width: `${((n as number) / total) * 100}%`, background: c as string }} /></div>
          <b className="vote-bar-num">{n as number}</b>
        </div>
      ))}
    </div>
  );
}
