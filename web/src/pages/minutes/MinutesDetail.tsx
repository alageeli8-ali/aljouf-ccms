// صفحة المحضر: المحرر، ودورة الاعتماد القابلة للتهيئة، والملاحظات، والإصدارات، والتوقيعات، وسجل النشاط.
import { Alert, App, Badge, Button, Card, Col, Dropdown, Input, Modal, Row, Select, Skeleton, Space, Steps, Tabs, Tag, Typography } from 'antd';
import {
  AuditOutlined, CheckCircleOutlined, DownOutlined, EditOutlined, FilePdfOutlined, RollbackOutlined, SafetyCertificateOutlined, SendOutlined, UploadOutlined,
} from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { get, post } from '../../api/client';
import type { MinutesDetail } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../../components/common';
import { ActivityTimeline, useReasonPrompt } from '../../components/governance';
import { formatDateTime } from '../../lib/format';
import { MinutesEditor } from './Editor';
import { CommentsPanel, SignaturesPanel, VersionsPanel } from './Review';
import { ExternalApprovalModal, WetSignatureModal } from './Approvals';

export default function MinutesDetailPage() {
  const { id } = useParams();
  const [params, setParams] = useSearchParams();
  const { t, i18n } = useTranslation();
  const { message, modal } = App.useApp();
  const { me } = useAuth();
  const qc = useQueryClient();
  const reason = useReasonPrompt();
  const [signing, setSigning] = useState(false);
  const [wet, setWet] = useState(false);
  const [external, setExternal] = useState(false);
  const q = useQuery({ queryKey: ['minutes', id], queryFn: () => get<MinutesDetail>(`/api/v1/minutes/${id}`) });
  const refresh = () => {
    for (const k of [['minutes', id], ['minutes-resolutions', id], ['minutes-comments', id], ['minutes-versions', id], ['minutes-activity', id], ['minutes']])
      void qc.invalidateQueries({ queryKey: k });
  };
  const act = useMutation({
    mutationFn: ({ path, body }: { path: string; body?: unknown; ok?: string }) => post<{ status: string }>(`/api/v1/minutes/${id}/${path}`, body ?? {}),
    onSuccess: (r, v) => { message.success(v.ok ?? t(`minutes.statusNow`, { status: t(`enums.minutesStatus.${r?.status}`, { defaultValue: r?.status ?? '' }) })); refresh(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });

  if (q.isLoading) return <Skeleton active />;
  if (q.error) return <ErrorAlert error={q.error} />;
  const d = q.data!;
  const m = d.minutes;
  const a = d.actions;
  const hash = m.contentHash ?? '';

  // مراحل الدورة كما تحددها سياسة الجهة؛ «معاد للتعديل» يظهر خطأً على المرحلة الأولى
  const flow = ['draft', ...d.steps, 'approved'];
  const currentIndex = m.status === 'returned' ? 0 : Math.max(0, flow.indexOf(m.status));
  const stepItems = flow.map((s, i) => ({
    title: t(`enums.minutesStatus.${s}`),
    description: i === currentIndex && m.status === 'member_review' && m.reviewDeadline
      ? t('minutes.until', { date: formatDateTime(m.reviewDeadline, i18n.language, me?.user.calendar) }) : undefined,
  }));

  const submit = () => reason.prompt({
    title: t('minutes.submit'), description: t('minutes.submitHelp', { steps: d.steps.map((s) => t(`enums.minutesStatus.${s}`)).join(' ← ') }),
    okText: t('minutes.submit'), reasonRequired: false,
    extra: null,
    onSubmit: (v) => act.mutateAsync({ path: 'submit', body: { changeSummary: v.reason || undefined }, ok: t('minutes.submitted') }),
  });

  const approveStep = () => modal.confirm({
    title: t(`minutes.approveStep.${m.status}`, { defaultValue: t('minutes.approveStepGeneric') }),
    content: <HashNote hash={hash} />, okText: t('common.confirm'), cancelText: t('common.cancel'),
    onOk: () => act.mutateAsync({ path: 'review/approve', body: { contentHash: hash } }),
  });

  const finalApprove = () => modal.confirm({
    title: t('minutes.finalApprove'), width: 520, okText: t('minutes.finalApprove'), cancelText: t('common.cancel'),
    content: <><Typography.Paragraph>{t('minutes.finalApproveHelp')}</Typography.Paragraph><HashNote hash={hash} /></>,
    onOk: () => act.mutateAsync({ path: 'approve', body: { contentHash: hash }, ok: t('minutes.approvedOk') }),
  });

  const more = [
    ...(a.canReturn ? [{
      key: 'return', icon: <RollbackOutlined />, danger: true, label: t('minutes.return'),
      onClick: () => reason.prompt({ title: t('minutes.return'), description: t('minutes.returnHelp'), danger: true, onSubmit: (v) => act.mutateAsync({ path: 'return', body: v }) }),
    }] : []),
    ...(a.canRecordWet ? [{ key: 'wet', icon: <UploadOutlined />, label: t('minutes.recordWet'), onClick: () => setWet(true) }] : []),
    ...(a.canAmend ? [{
      key: 'amend', icon: <EditOutlined />, label: t('minutes.amend'),
      onClick: () => reason.prompt({ title: t('minutes.amend'), description: t('minutes.amendHelp', { mode: t(`enums.reapproval.${m.workflow.amendmentReapproval}`) }), onSubmit: (v) => act.mutateAsync({ path: 'amend', body: v }) }),
    }] : []),
  ];

  const tab = params.get('tab') ?? 'editor';
  return (
    <>
      <PageHeader
        title={m.meetingTitle}
        subtitle={<Space wrap>
          <Link to={`/meetings/${m.meetingId}`} className="mono">{m.meetingReference}</Link>
          <StatusTag group="minutesStatus" value={m.status} />
          <Link to={`/bodies/${m.bodyId}`}>{m.bodyName}</Link>
          {m.amendmentNo > 0 && <Tag color="gold">{t('minutes.amendmentNo', { n: m.amendmentNo })}</Tag>}
          {d.currentVersion && <span>{t('minutes.version')} {d.currentVersion.no}</span>}
        </Space>}
        extra={<>
          {a.canSubmit && <Button type="primary" icon={<SendOutlined />} onClick={submit}>{t('minutes.submit')}</Button>}
          {a.canApproveStep && <Button type="primary" icon={<CheckCircleOutlined />} loading={act.isPending} onClick={approveStep}>{t(`minutes.approveStep.${m.status}`, { defaultValue: t('minutes.approveStepGeneric') })}</Button>}
          {a.canSign && <Button type="primary" icon={<AuditOutlined />} onClick={() => setSigning(true)}>{t('minutes.signOff')}</Button>}
          {a.canApprove && <Button type="primary" icon={<SafetyCertificateOutlined />} loading={act.isPending} onClick={finalApprove}>{t('minutes.finalApprove')}</Button>}
          {a.canRecordExternal && <Button type="primary" icon={<SafetyCertificateOutlined />} onClick={() => setExternal(true)}>{t('minutes.recordExternal')}</Button>}
          <Dropdown menu={{
            items: [
              { key: 'cur', label: m.status === 'approved' ? t('minutes.pdfApproved') : t('minutes.pdfDraft'), onClick: () => window.open(`/api/v1/minutes/${m.id}/pdf`, '_blank') },
              ...(d.currentVersion && m.status !== 'approved' ? [{ key: 'ver', label: t('minutes.pdfVersion', { n: d.currentVersion.no }), onClick: () => window.open(`/api/v1/minutes/${m.id}/pdf?versionNo=${d.currentVersion!.no}`, '_blank') }] : []),
            ],
          }}><Button icon={<FilePdfOutlined />}>PDF <DownOutlined /></Button></Dropdown>
          {more.length > 0 && <Dropdown menu={{ items: more }}><Button>{t('common.more')} <DownOutlined /></Button></Dropdown>}
        </>}
      />

      <Card size="small" style={{ marginBottom: 12 }}>
        <Steps size="small" current={currentIndex} status={m.status === 'returned' ? 'error' : m.status === 'approved' ? 'finish' : 'process'} items={stepItems} responsive />
      </Card>
      {m.status === 'returned' && m.statusReason && <Alert type="error" showIcon style={{ marginBottom: 12 }} message={t('minutes.returnedBecause')} description={m.statusReason} />}
      {m.amendmentNo > 0 && m.status !== 'approved' && <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('minutes.amendingBanner', { n: m.amendmentNo, v: m.approvedVersionNo })} description={m.amendmentReason} />}
      {a.submitBlockedReason && a.canEdit && <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t(`minutes.blocked.${a.submitBlockedReason}`)} />}
      {m.status === 'approved' && (
        <Alert type="success" showIcon style={{ marginBottom: 12 }} message={t('minutes.approvedBanner', { v: m.approvedVersionNo, by: m.approvedByName ?? '—', at: formatDateTime(m.approvedAt, i18n.language, me?.user.calendar) })}
          description={<Space wrap><span>{t('minutes.verificationCode')}: <b className="mono">{m.verificationCode}</b></span>{m.pdfSha256 && <span>PDF SHA-256: <span className="mono">{m.pdfSha256.slice(0, 24)}…</span></span>}</Space>} />
      )}

      <Row gutter={16}>
        <Col xs={24} xl={18}>
          <Card>
            <Tabs activeKey={tab} onChange={(k) => setParams({ tab: k }, { replace: true })} items={[
              { key: 'editor', label: t('minutes.tabs.editor'), children: <MinutesEditor detail={d} onChange={refresh} /> },
              { key: 'comments', label: <span>{t('minutes.tabs.comments')} <Badge count={m.openComments} size="small" /></span>, children: <CommentsPanel detail={d} onChange={refresh} /> },
              { key: 'signatures', label: t('minutes.tabs.signatures'), children: <SignaturesPanel detail={d} onChange={refresh} /> },
              { key: 'versions', label: t('minutes.tabs.versions'), children: <VersionsPanel minutesId={m.id} /> },
              { key: 'activity', label: t('minutes.tabs.activity'), children: <ActivityTimeline url={`/api/v1/minutes/${m.id}/activity`} queryKey={['minutes-activity', m.id]} /> },
            ]} />
          </Card>
        </Col>
        <Col xs={24} xl={6}>
          <SidePanel detail={d} />
        </Col>
      </Row>

      {signing && <SignModal minutesId={m.id} hash={hash} onClose={() => { setSigning(false); refresh(); }} />}
      {wet && <WetSignatureModal detail={d} onClose={() => { setWet(false); refresh(); }} />}
      {external && <ExternalApprovalModal detail={d} onClose={() => { setExternal(false); refresh(); }} />}
      {reason.node}
    </>
  );
}

function HashNote({ hash }: { hash: string }) {
  const { t } = useTranslation();
  return <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>{t('minutes.hash')}: <span className="mono">{hash.slice(0, 32)}…</span></Typography.Paragraph>;
}

/** ملخص السياسة المطبقة على المحضر: من يراجع، ومن يوقع وبأي طريقة، ومن يعتمد. */
function SidePanel({ detail }: { detail: MinutesDetail }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const m = detail.minutes;
  const w = m.workflow;
  const sig = detail.signatureMethod;
  return (
    <Space direction="vertical" style={{ width: '100%' }} size={12}>
      <Card size="small" title={t('minutes.policyTitle')}>
        <ul className="policy-list">
          <li>{w.secretaryReview ? '✓' : '—'} {t('enums.minutesStatus.secretary_review')}</li>
          <li>{w.chairReview ? '✓' : '—'} {t('enums.minutesStatus.chair_review')}</li>
          <li>{w.memberReview ? '✓' : '—'} {t('enums.minutesStatus.member_review')}{w.memberReview && ` (${t('minutes.days', { n: w.memberReviewDays })})`}</li>
          <li>{w.signatures !== 'none' ? '✓' : '—'} {t(`enums.signatureMode.${w.signatures}`)}</li>
          <li>✓ {t(`enums.finalApproval.${w.finalApproval}`)}</li>
        </ul>
        {w.signatures !== 'none' && (
          <div className="method-note">
            <b>{t('minutes.signatureMethod')}: {t(`enums.signoffMethod.${sig.method}`)}</b>
            <div>{t(`minutes.methodReason.${sig.reason}`, { defaultValue: sig.reason })}</div>
          </div>
        )}
      </Card>
      <Card size="small" title={t('minutes.versionCard')}>
        {detail.currentVersion ? (
          <Space direction="vertical" size={2}>
            <span>{t('minutes.version')} <b>{detail.currentVersion.no}</b> — {detail.currentVersion.createdBy}</span>
            <span style={{ color: '#5f6b64', fontSize: 12 }}>{formatDateTime(detail.currentVersion.createdAt, i18n.language, me?.user.calendar)}</span>
            <span className="mono" style={{ fontSize: 11 }} title={detail.currentVersion.hash}>{detail.currentVersion.hash.slice(0, 20)}…</span>
            {detail.currentVersion.changeSummary && <Typography.Text type="secondary">{detail.currentVersion.changeSummary}</Typography.Text>}
          </Space>
        ) : <Typography.Text type="secondary">{t('minutes.noVersionYet')}</Typography.Text>}
      </Card>
      <Card size="small" title={t('minutes.summaryCard')}>
        <Space direction="vertical" size={4}>
          <span>{t('minutes.resolutionsCount')}: <b>{detail.snapshot.resolutions.length}</b></span>
          <span>{t('minutes.tasksCount')}: <b>{detail.snapshot.tasks.length}</b></span>
          <span>{t('minutes.attendanceCount')}: <b>{detail.snapshot.attendance.filter((x) => x.status === 'present').length}</b> / {detail.snapshot.attendance.length}</span>
          {detail.snapshot.quorum && <Tag color={detail.snapshot.quorum.isMet ? 'green' : 'red'}>{t('minutes.quorumLine', { c: detail.snapshot.quorum.counted, r: detail.snapshot.quorum.required })}</Tag>}
        </Space>
      </Card>
    </Space>
  );
}

function SignModal({ minutesId, hash, onClose }: { minutesId: string; hash: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [reservation, setReservation] = useState('');
  const [withReservation, setWithReservation] = useState('no');
  const m = useMutation({
    mutationFn: () => post(`/api/v1/minutes/${minutesId}/signoffs`, { contentHash: hash, reservation: withReservation === 'yes' && reservation ? reservation : undefined }),
    onSuccess: () => { message.success(t('minutes.signedOk')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('minutes.signOff')} onCancel={onClose} onOk={() => m.mutate()} confirmLoading={m.isPending} okText={t('minutes.signConfirm')} cancelText={t('common.cancel')}>
      <Alert type="info" showIcon message={t('minutes.attestationNote')} style={{ marginBottom: 12 }} />
      <Typography.Paragraph>{t('minutes.hash')}: <span className="mono">{hash.slice(0, 32)}…</span></Typography.Paragraph>
      <Select value={withReservation} onChange={setWithReservation} style={{ width: '100%', marginBottom: 8 }}
        options={[{ value: 'no', label: t('minutes.signWithout') }, { value: 'yes', label: t('minutes.signWith') }]} />
      {withReservation === 'yes' && <Input.TextArea rows={3} placeholder={t('minutes.reservation')} value={reservation} onChange={(e) => setReservation(e.target.value)} maxLength={2000} />}
    </Modal>
  );
}
