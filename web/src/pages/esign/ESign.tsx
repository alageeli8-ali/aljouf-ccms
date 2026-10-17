// وحدة التوقيع الإلكتروني في الواجهة: لوحة طلبات التوقيع في المحضر، والتوقيع بإعادة التحقق، والتحقق من النسخ، ومسارات التوقيع،
// وصفحة المزوّد التجريبي، والتحقق من ملف موقّع. الأنواع الثلاثة تُعرض بأسمائها الدقيقة ولا يُدّعى أي اعتماد نظامي.
import {
  Alert, App, Button, Card, Checkbox, Collapse, Descriptions, Empty, Form, Input, InputNumber, Modal, Result, Select, Skeleton, Space, Switch, Table, Tag,
  Timeline, Tooltip, Typography, Upload,
} from 'antd';
import {
  CheckCircleOutlined, CloseCircleOutlined, ExperimentOutlined, FileProtectOutlined, LinkOutlined, PauseCircleOutlined, PlayCircleOutlined, PlusOutlined,
  ReloadOutlined, SafetyCertificateOutlined, StopOutlined, SwapOutlined, UploadOutlined, WarningOutlined,
} from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { api, get, newRequestKey, post, postOnce, put } from '../../api/client';
import type { Body, ESignMinutes, ESignReport, ESignRequest, ESignRoute, PdfSignatureCheck } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, PageHeader, errorText } from '../../components/common';
import { useReasonPrompt } from '../../components/governance';
import { formatDateTime } from '../../lib/format';

const STATUS_COLOR: Record<string, string> = {
  queued: 'gold', in_progress: 'processing', on_hold: 'orange', completed: 'green', declined: 'red', voided: 'default', cancelled: 'default', expired: 'volcano', failed: 'red',
};
const SIGNER_COLOR: Record<string, string> = { waiting: 'default', pending: 'gold', signed: 'green', declined: 'red', cancelled: 'default' };
const OVERALL_COLOR: Record<string, string> = {
  valid: 'green', valid_test_only: 'gold', valid_untrusted_chain: 'orange', documented_approval: 'blue', incomplete: 'default', invalid: 'red', no_signatures: 'default',
};

export const short = (h?: string | null, n = 16) => (h ? `${h.slice(0, n)}…` : '—');

export function TypeTag({ type, label }: { type: string; label?: string }) {
  const { t } = useTranslation();
  const icon = type === 'in_app_approval' ? <CheckCircleOutlined /> : type === 'pdf_digital_signature' ? <SafetyCertificateOutlined /> : <LinkOutlined />;
  return <Tag icon={icon} color={type === 'in_app_approval' ? 'blue' : type === 'pdf_digital_signature' ? 'purple' : 'cyan'}>{label ?? t(`esign.types.${type}`)}</Tag>;
}

function ProviderTag({ r }: { r: Pick<ESignRequest, 'provider' | 'providerName' | 'productionGrade' | 'type'> }) {
  const { t } = useTranslation();
  if (r.type === 'in_app_approval') return <Tag>{t('esign.internal')}</Tag>;
  return r.productionGrade ? <Tag>{r.providerName}</Tag> : <Tag color="red" icon={<ExperimentOutlined />}>{t('esign.mockTag')}</Tag>;
}

// ============ لوحة التوقيع في المحضر ============

export function ESignPanel({ minutesId, onChanged }: { minutesId: string; onChanged?: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['esign', minutesId], queryFn: () => get<ESignMinutes>(`/api/v1/esign/minutes/${minutesId}`) });
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['esign', minutesId] }); onChanged?.(); };
  const start = useMutation({
    mutationFn: () => postOnce(`/api/v1/esign/minutes/${minutesId}/requests`, {}, newRequestKey()),
    onSuccess: () => { message.success(t('esign.started')); refresh(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  if (q.isLoading) return <Skeleton active />;
  if (q.error || !q.data) return <ErrorAlert error={q.error} />;
  const d = q.data;
  const s = d.module.settings;
  return (
    <Space direction="vertical" style={{ width: '100%' }} size={12}>
      <Alert showIcon type={!d.module.enabled ? 'info' : d.module.ready ? (s.productionGrade || s.method === 'in_app_approval' ? 'success' : 'warning') : 'warning'}
        message={!d.module.enabled ? t('esign.moduleDisabled') : <Space wrap>{t('esign.moduleEnabled')}<TypeTag type={s.method} />{s.method !== 'in_app_approval' && (s.productionGrade ? <Tag>{s.providerName}</Tag> : <Tag color="red" icon={<ExperimentOutlined />}>{t('esign.mockTag')}</Tag>)}</Space>}
        description={!d.module.enabled ? t('esign.moduleDisabledHelp') : <>{d.module.notice} {!d.module.ready && t(`minutes.methodReason.${d.module.reason}`, { defaultValue: d.module.reason })}</>}
        action={d.canStart && <Button type="primary" icon={<PlayCircleOutlined />} loading={start.isPending} onClick={() => start.mutate()}>{t('esign.start')}</Button>} />
      {d.requests.length === 0
        ? <Empty description={t('esign.noRequests')} />
        : d.requests.map((r, i) => <RequestCard key={r.id} r={r} defaultOpen={i === 0} onChange={refresh} />)}
    </Space>
  );
}

function RequestCard({ r, defaultOpen, onChange }: { r: ESignRequest; defaultOpen: boolean; onChange: () => void }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const { message } = App.useApp();
  const reason = useReasonPrompt();
  const [signing, setSigning] = useState<'sign' | 'provider' | null>(null);
  const [verify, setVerify] = useState(false);
  const dt = (v?: string | null) => formatDateTime(v, i18n.language, me?.user.calendar);
  const act = (path: string, body?: object) => post(`/api/v1/esign/requests/${r.id}/${path}`, body ?? {}).then(onChange);
  const resume = useMutation({ mutationFn: () => act('resume'), onError: (e) => message.error(errorText(e, t('common.error'))) });
  const live = ['queued', 'in_progress', 'on_hold'].includes(r.status);
  const steps = [...new Set(r.signers.map((s) => s.step))];
  return (
    <Card size="small" className={`esign-card st-${r.status}`}
      title={<Space wrap><FileProtectOutlined /><span className="mono">{r.reference}</span><Tag>{t('minutes.version')} {r.versionNo}</Tag>
        <TypeTag type={r.type} label={r.typeLabel} /><ProviderTag r={r} /><Tag color={STATUS_COLOR[r.status]}>{t(`esign.status.${r.status}`)}</Tag></Space>}
      extra={<Space wrap>
        <Button size="small" icon={<SafetyCertificateOutlined />} onClick={() => setVerify(true)}>{t('esign.verify')}</Button>
        {r.my.canSign && <Button size="small" type="primary" onClick={() => setSigning('sign')}>{r.type === 'in_app_approval' ? t('esign.approve') : t('esign.signDigital')}</Button>}
        {r.my.canOpenProviderSession && <Button size="small" type="primary" icon={<LinkOutlined />} onClick={() => setSigning('provider')}>{t('esign.signAtProvider')}</Button>}
        {r.my.onBehalf.length > 0 && !r.my.canSign && <Button size="small" onClick={() => setSigning('sign')}>{t('esign.signOnBehalf')}</Button>}
        {r.my.canDecline && <Button size="small" danger icon={<CloseCircleOutlined />} onClick={() => reason.prompt({
          title: t('esign.decline'), description: t('esign.declineHelp'), danger: true, okText: t('esign.decline'),
          onSubmit: (v) => act('decline', { reason: v.reason }),
        })}>{t('esign.decline')}</Button>}
      </Space>}>
      <Alert type={r.type === 'in_app_approval' ? 'info' : r.productionGrade ? 'warning' : 'error'} showIcon style={{ marginBottom: 12 }} message={r.typeNotice} />
      {r.manage.actionRequired && (
        <Alert type="warning" showIcon icon={<WarningOutlined />} style={{ marginBottom: 12 }} message={t('esign.actionRequired')} description={t('esign.actionRequiredHelp')} />
      )}
      {r.statusReason && <Alert type={['declined', 'failed', 'expired'].includes(r.status) ? 'error' : 'info'} style={{ marginBottom: 12 }} message={r.statusReason} />}
      {r.my.notYourTurn && <Alert type="info" style={{ marginBottom: 12 }} message={t('esign.notYourTurn')} />}
      {r.status === 'queued' && (
        <Alert type="warning" style={{ marginBottom: 12 }} message={t('esign.queued', { n: r.dispatchAttempts })}
          description={r.lastError ? `${t('esign.lastError')}: ${r.lastError}${r.nextAttemptAt ? ` — ${t('esign.nextAttempt')}: ${dt(r.nextAttemptAt)}` : ''}` : undefined} />
      )}
      <div className="esign-steps">
        {steps.map((step) => (
          <div key={step} className="esign-step">
            <div className="esign-step-head">{t('esign.step', { n: step })} — {t(`esign.roles.${r.signers.find((s) => s.step === step)!.role}`)}
              {r.sequential && <span className="item-meta"> · {t('esign.sequential')}</span>}</div>
            {r.signers.filter((s) => s.step === step).map((s) => (
              <div key={s.id} className={`esign-signer st-${s.status}`}>
                <span className="esign-pos">{s.position}</span>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div><b>{s.name}</b> <Tag color={SIGNER_COLOR[s.status]} style={{ marginInlineStart: 6 }}>{t(`esign.signerStatus.${s.status}`)}</Tag>
                    {s.onBehalf && <Tag color="purple">{t('esign.byDelegate', { name: s.actedBy })}</Tag>}</div>
                  <div className="item-meta">
                    {s.signedAt && <>{dt(s.signedAt)}</>}
                    {s.reauth && <> · {t('esign.reauthDone')}</>}
                    {s.certificate && <Tooltip title={`${s.certificate.subject} — ${s.certificate.issuer}`}> · {t('esign.cert')}: <span className="mono">{s.certificate.serial.slice(0, 12)}</span> ({t(`esign.chain.${s.certificate.chain}`)})</Tooltip>}
                    {s.declineReason && <> · {t('esign.declineReason')}: {s.declineReason}</>}
                  </div>
                </div>
              </div>
            ))}
          </div>
        ))}
      </div>
      <Collapse ghost defaultActiveKey={defaultOpen ? [] : []} items={[
        {
          key: 'details', label: t('esign.details'), children: (
            <Descriptions size="small" column={1} bordered>
              <Descriptions.Item label={t('esign.route')}>{r.route.nameAr} <span className="item-meta">({t(`esign.routeSource.${r.route.source}`)})</span></Descriptions.Item>
              <Descriptions.Item label={t('esign.expiresAt')}>{dt(r.expiresAt)}</Descriptions.Item>
              <Descriptions.Item label={t('esign.documentHash')}><span className="mono" title={r.documentSha256}>{short(r.documentSha256, 24)}</span>{' '}
                <a href={`/api/v1/documents/${r.documentId}/content`} target="_blank" rel="noreferrer">{t('esign.viewDocument')}</a></Descriptions.Item>
              <Descriptions.Item label={t('esign.contentHash')}><span className="mono" title={r.contentHash}>{short(r.contentHash, 24)}</span></Descriptions.Item>
              {r.signedDocumentId && <Descriptions.Item label={t('esign.signedCopy')}><span className="mono">{short(r.signedDocumentSha256, 24)}</span>{' '}
                <a href={`/api/v1/documents/${r.signedDocumentId}/content`} target="_blank" rel="noreferrer">{t('esign.download')}</a></Descriptions.Item>}
              {r.providerRef && <Descriptions.Item label={t('esign.providerRef')}><span className="mono">{r.providerRef}</span></Descriptions.Item>}
              {r.completedAt && <Descriptions.Item label={t('esign.completedAt')}>{dt(r.completedAt)}</Descriptions.Item>}
            </Descriptions>
          ),
        },
        ...(r.events.length > 0 ? [{
          key: 'events', label: t('esign.events', { n: r.events.length }), children: (
            <Timeline items={r.events.map((e, i) => ({
              key: i, color: e.kind.includes('fail') || e.kind.includes('rejected') || e.kind === 'declined' ? 'red' : e.kind === 'signed' || e.kind === 'completed' ? 'green' : 'gray',
              children: <div><b>{t(`esign.eventKinds.${e.kind}`, { defaultValue: e.kind })}</b> <span className="item-meta">— {t(`esign.sources.${e.source}`)}{e.actor ? ` · ${e.actor}` : ''} · {dt(e.at)}</span>
                {typeof e.data?.reason === 'string' && <div className="item-meta">{e.data.reason}</div>}
                {typeof e.data?.error === 'string' && <div className="item-meta">{e.data.error}</div>}</div>,
            }))} />
          ),
        }] : []),
        ...(r.callbacks.length > 0 ? [{
          key: 'callbacks', label: t('esign.callbacks', { n: r.callbacks.length }), children: (
            <Table size="small" rowKey="eventId" pagination={false} dataSource={r.callbacks} columns={[
              { title: t('esign.callbackEvent'), dataIndex: 'event', render: (v) => v ?? '—' },
              { title: t('esign.callbackSignature'), dataIndex: 'signatureValid', render: (v) => (v ? <Tag color="green">{t('esign.valid')}</Tag> : <Tag color="red">{t('esign.invalid')}</Tag>) },
              { title: t('esign.callbackOutcome'), dataIndex: 'outcome', render: (v) => t(`esign.outcomes.${v}`, { defaultValue: v }) },
              { title: t('common.date'), dataIndex: 'at', render: dt },
            ]} />
          ),
        }] : []),
      ]} />
      {live && (r.manage.canFallback || r.manage.canHold || r.manage.canResume) && (
        <div className="esign-manage">
          <span className="item-meta">{t('esign.manageTitle')}:</span>
          {r.manage.canHold && <Button size="small" icon={<PauseCircleOutlined />} onClick={() => reason.prompt({
            title: t('esign.hold'), description: t('esign.holdHelp'), onSubmit: (v) => act('hold', { reason: v.reason }),
          })}>{t('esign.hold')}</Button>}
          {r.manage.canResume && <Button size="small" icon={<ReloadOutlined />} loading={resume.isPending} onClick={() => resume.mutate()}>{t('esign.resume')}</Button>}
          {r.manage.canFallback && <Button size="small" danger icon={<SwapOutlined />} onClick={() => reason.prompt({
            title: t('esign.fallback'), description: t('esign.fallbackHelp'), danger: true, okText: t('esign.fallback'),
            onSubmit: (v) => act('fallback', { reason: v.reason }),
          })}>{t('esign.fallback')}</Button>}
        </div>
      )}
      {signing && <SignDialog r={r} mode={signing} onClose={() => setSigning(null)} onDone={onChange} />}
      {verify && <VerifyModal requestId={r.id} onClose={() => setVerify(false)} />}
      {reason.node}
    </Card>
  );
}

/** التوقيع: اطلاع على النسخة وبصمتها، وإعادة التحقق من الهوية، ثم الموافقة أو التوقيع الرقمي أو الانتقال لصفحة المزوّد. */
function SignDialog({ r, mode, onClose, onDone }: { r: ESignRequest; mode: 'sign' | 'provider'; onClose: () => void; onDone: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const nav = useNavigate();
  const [form] = Form.useForm();
  const [key] = useState(newRequestKey());
  const behalf = Form.useWatch('onBehalfOf', form) as string | undefined;
  const m = useMutation({
    mutationFn: async (v: { password?: string; onBehalfOf?: string; note?: string }) => {
      let reauthToken: string | undefined;
      if (r.my.reauthRequired) reauthToken = (await post<{ reauthToken: string }>('/api/v1/esign/reauth', { password: v.password })).reauthToken;
      if (mode === 'provider') return post<{ url: string }>(`/api/v1/esign/requests/${r.id}/provider-session`, { reauthToken });
      return postOnce(`/api/v1/esign/requests/${r.id}/sign`, { reauthToken, onBehalfOf: v.onBehalfOf || undefined, note: v.note || undefined }, key);
    },
    onSuccess: (res) => {
      if (mode === 'provider') { nav((res as { url: string }).url); return; }
      message.success(r.type === 'in_app_approval' ? t('esign.approved') : t('esign.signedOk'));
      onDone(); onClose();
    },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const onlyBehalf = !r.my.canSign && r.my.onBehalf.length > 0;
  return (
    <Modal open title={mode === 'provider' ? t('esign.signAtProvider') : r.type === 'in_app_approval' ? t('esign.approveTitle') : t('esign.signDigitalTitle')}
      onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={mode === 'provider' ? t('esign.continueToProvider') : t('esign.confirmSign')}
      cancelText={t('common.cancel')} destroyOnHidden>
      <Alert type={r.type === 'in_app_approval' ? 'info' : r.productionGrade ? 'warning' : 'error'} showIcon style={{ marginBottom: 12 }}
        message={<TypeTag type={r.type} label={r.typeLabel} />} description={r.typeNotice} />
      <Descriptions size="small" column={1} bordered style={{ marginBottom: 12 }}>
        <Descriptions.Item label={t('esign.request')}><span className="mono">{r.reference}</span> — {t('minutes.version')} {r.versionNo}</Descriptions.Item>
        <Descriptions.Item label={t('esign.documentHash')}><span className="mono" style={{ fontSize: 11 }}>{r.currentDocumentSha256 ?? r.documentSha256}</span></Descriptions.Item>
      </Descriptions>
      <Form form={form} layout="vertical" initialValues={{ onBehalfOf: onlyBehalf ? r.my.onBehalf[0].userId : undefined }} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="read" valuePropName="checked" rules={[{ validator: async (_, v) => { if (!v) throw new Error(t('esign.mustRead')); } }]}>
          <Checkbox>{t('esign.readConfirm')} <a href={`/api/v1/documents/${r.currentDocumentId ?? r.documentId}/content`} target="_blank" rel="noreferrer">{t('esign.viewDocument')}</a></Checkbox>
        </Form.Item>
        {mode === 'sign' && r.my.onBehalf.length > 0 && (
          <Form.Item name="onBehalfOf" label={t('esign.onBehalfOf')} extra={t('esign.onBehalfHelp')}>
            <Select allowClear={!onlyBehalf} options={r.my.onBehalf.map((o) => ({ value: o.userId, label: o.name }))} placeholder={t('esign.myself')} />
          </Form.Item>
        )}
        {behalf && <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('esign.behalfNotice')} />}
        {r.my.reauthRequired && (
          <Form.Item name="password" label={t('esign.reauthPassword')} extra={t('esign.reauthHelp')} rules={[{ required: true, message: t('esign.reauthRequired') }]}>
            <Input.Password autoComplete="current-password" />
          </Form.Item>
        )}
        {mode === 'sign' && <Form.Item name="note" label={t('esign.note')}><Input maxLength={500} /></Form.Item>}
      </Form>
    </Modal>
  );
}

// ============ تقرير التحقق ============

function VerifyModal({ requestId, onClose }: { requestId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const q = useQuery({ queryKey: ['esign-verify', requestId], queryFn: () => get<ESignReport>(`/api/v1/esign/requests/${requestId}/verify`) });
  return (
    <Modal open width={860} title={t('esign.verifyTitle')} onCancel={onClose} footer={<Button onClick={onClose}>{t('common.close')}</Button>}>
      {q.isLoading ? <Skeleton active /> : q.error ? <ErrorAlert error={q.error} /> : <VerificationView report={q.data!} />}
    </Modal>
  );
}

export function VerificationView({ report }: { report: ESignReport }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const sigs: PdfSignatureCheck[] = report.pdfSignatures ?? report.signatures ?? [];
  const yes = (v: boolean) => (v ? <Tag color="green" icon={<CheckCircleOutlined />}>{t('common.yes')}</Tag> : <Tag color="red" icon={<CloseCircleOutlined />}>{t('common.no')}</Tag>);
  return (
    <Space direction="vertical" style={{ width: '100%' }} size={12}>
      <Alert showIcon type={report.overall === 'invalid' ? 'error' : report.overall === 'valid' ? 'success' : report.overall === 'documented_approval' ? 'info' : 'warning'}
        message={<Space wrap>{t('esign.overall')}: <Tag color={OVERALL_COLOR[report.overall]}>{t(`esign.overallValues.${report.overall}`)}</Tag>
          {report.typeLabel && <TypeTag type={report.type!} label={report.typeLabel} />}</Space>}
        description={t(`esign.overallHelp.${report.overall}`, { defaultValue: '' })} />
      {report.document && (
        <Descriptions size="small" column={1} bordered title={t('esign.documentIntegrity')}>
          <Descriptions.Item label={t('esign.expectedHash')}><span className="mono" style={{ fontSize: 11 }}>{report.document.expectedSha256}</span></Descriptions.Item>
          <Descriptions.Item label={t('esign.actualHash')}><span className="mono" style={{ fontSize: 11 }}>{report.document.actualSha256 ?? '—'}</span></Descriptions.Item>
          <Descriptions.Item label={t('esign.intact')}>{yes(report.document.intact)}</Descriptions.Item>
        </Descriptions>
      )}
      {report.signedDocument && (
        <Descriptions size="small" column={{ xs: 1, md: 2 }} bordered title={t('esign.signedCopy')}>
          <Descriptions.Item label={t('esign.matchesRecord')}>{yes(report.signedDocument.matchesRecord)}</Descriptions.Item>
          <Descriptions.Item label={t('esign.originalPreserved')}>{yes(report.signedDocument.originalPreserved)}</Descriptions.Item>
          <Descriptions.Item label={t('esign.modifiedAfter')} span={2}>{report.signedDocument.modifiedAfterLastSignature ? <Tag color="red">{t('common.yes')}</Tag> : <Tag color="green">{t('common.no')}</Tag>}</Descriptions.Item>
        </Descriptions>
      )}
      {report.matchesRecord !== undefined && (
        <Alert type={report.matchesRecord ? 'success' : 'info'} message={report.matchesRecord
          ? t('esign.uploadMatches', { ref: report.matchesRecord.reference, v: report.matchesRecord.versionNo }) : t('esign.uploadNoMatch')} />
      )}
      {sigs.length > 0 && (
        <Table size="small" rowKey="index" pagination={false} dataSource={sigs} scroll={{ x: 760 }} columns={[
          { title: '#', dataIndex: 'index', width: 40 },
          { title: t('esign.signer'), render: (_, s) => <Space direction="vertical" size={0}><b>{s.signerName ?? '—'}</b><span className="item-meta" style={{ fontSize: 11 }}>{s.certificateIssuer}</span></Space> },
          { title: t('esign.signingTime'), dataIndex: 'signingTime', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
          { title: t('esign.digest'), dataIndex: 'digestMatches', render: yes },
          { title: t('esign.signatureValid'), dataIndex: 'signatureValid', render: yes },
          { title: t('esign.certValid'), dataIndex: 'certificateValidAtSigning', render: yes },
          { title: t('esign.chainCol'), dataIndex: 'chain', render: (v, s) => <Tooltip title={s.chainDetail}><Tag color={v === 'trusted' ? 'green' : v === 'test_only' ? 'gold' : 'red'}>{t(`esign.chain.${v}`)}</Tag></Tooltip> },
          { title: t('esign.subsequent'), dataIndex: 'subsequentChanges', render: (v) => <Tag color={v === 'other_changes' ? 'red' : 'default'}>{t(`esign.subsequentValues.${v}`)}</Tag> },
        ]} />
      )}
      {report.approvals && (
        <Table size="small" rowKey={(a) => a.signer} pagination={false} dataSource={report.approvals} columns={[
          { title: t('esign.signer'), dataIndex: 'signer' },
          { title: t('esign.roleCol'), dataIndex: 'role', render: (v) => t(`esign.roles.${v === 'member' ? 'members' : v}`) },
          { title: t('common.status'), dataIndex: 'status', render: (v) => <Tag color={SIGNER_COLOR[v]}>{t(`esign.signerStatus.${v}`)}</Tag> },
          { title: t('common.date'), dataIndex: 'at', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
          { title: t('esign.boundHash'), dataIndex: 'documentSha256', render: (v) => <span className="mono">{short(v, 12)}</span> },
        ]} />
      )}
      {report.notes.length > 0 && <ul className="esign-notes">{report.notes.map((n) => <li key={n}>{n}</li>)}</ul>}
      {report.legalNotice && <Typography.Paragraph type="secondary" style={{ fontSize: 12, marginBottom: 0 }}>{report.legalNotice}</Typography.Paragraph>}
    </Space>
  );
}

// ============ صفحة التحقق من ملف ============

export function SignatureVerifyPage() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [report, setReport] = useState<ESignReport | null>(null);
  const m = useMutation({
    mutationFn: (file: File) => { const fd = new FormData(); fd.append('file', file); return api<ESignReport>('POST', '/api/v1/esign/verify', fd); },
    onSuccess: setReport,
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <>
      <PageHeader title={t('esign.verifyPage')} subtitle={t('esign.verifyPageHelp')} />
      <Card style={{ marginBottom: 16 }}>
        <Upload.Dragger accept="application/pdf" showUploadList={false} beforeUpload={(f) => { m.mutate(f); return false; }} disabled={m.isPending}>
          <p className="ant-upload-drag-icon"><UploadOutlined /></p>
          <p>{t('esign.dropPdf')}</p>
          <p className="item-meta">{t('esign.dropPdfHelp')}</p>
        </Upload.Dragger>
      </Card>
      {m.isPending && <Skeleton active />}
      {report && <Card><VerificationView report={report} /></Card>}
    </>
  );
}

// ============ مسارات التوقيع ============

const ROLES = ['secretary', 'members', 'chair'];
const MEETING_TYPES = ['regular', 'extraordinary', 'urgent', 'circulation'];
const TYPES = ['in_app_approval', 'provider_esign', 'pdf_digital_signature'];

export function SignatureRoutesPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const reason = useReasonPrompt();
  const q = useQuery({ queryKey: ['esign-routes'], queryFn: () => get<{ routes: ESignRoute[]; defaultRoute: { steps: { order: number; role: string }[]; sequential: boolean; signatureType: string } }>('/api/v1/esign/routes') });
  const [edit, setEdit] = useState<ESignRoute | 'new' | null>(null);
  if (q.isLoading) return <Skeleton active />;
  if (q.error || !q.data) return <ErrorAlert error={q.error} />;
  const steps = (s: { role: string; allowDelegation?: boolean }[]) => s.map((x) => t(`esign.roles.${x.role}`) + (x.allowDelegation ? ` (${t('esign.delegationAllowed')})` : '')).join(' ← ');
  return (
    <>
      <PageHeader title={t('esign.routesTitle')} subtitle={t('esign.routesHelp')}
        extra={<Button type="primary" icon={<PlusOutlined />} onClick={() => setEdit('new')}>{t('esign.newRoute')}</Button>} />
      <Card size="small" style={{ marginBottom: 16 }} title={t('esign.defaultRoute')}>
        <Space wrap>{steps(q.data.defaultRoute.steps)}<TypeTag type={q.data.defaultRoute.signatureType} />
          <Tag>{q.data.defaultRoute.sequential ? t('esign.sequential') : t('esign.parallel')}</Tag></Space>
        <Typography.Paragraph type="secondary" style={{ marginTop: 8, marginBottom: 0 }}>{t('esign.defaultRouteHelp')}</Typography.Paragraph>
      </Card>
      <Card>
        <Table size="small" rowKey="id" pagination={false} dataSource={q.data.routes} rowClassName={(r) => (r.active ? '' : 'ended-row')} columns={[
          { title: t('common.name'), dataIndex: 'nameAr', render: (v, r) => <Space direction="vertical" size={0}><b>{v}</b><span className="item-meta">v{r.version}{!r.active && ` · ${t('esign.inactive')}`}</span></Space> },
          { title: t('esign.scopeBody'), dataIndex: 'bodyName', render: (v) => v ?? t('esign.allBodies') },
          { title: t('esign.scopeMeetingType'), dataIndex: 'meetingType', render: (v) => (v ? t(`enums.meetingType.${v}`) : t('esign.allTypes')) },
          { title: t('esign.stepsCol'), render: (_, r) => steps(r.steps) },
          { title: t('esign.typeCol'), dataIndex: 'signatureType', render: (v) => (v ? <TypeTag type={v} /> : <span className="item-meta">{t('esign.fromSettings')}</span>) },
          { title: '', render: (_, r) => r.active && <Space>
            <Button size="small" onClick={() => setEdit(r)}>{t('common.edit')}</Button>
            <Button size="small" danger icon={<StopOutlined />} onClick={() => reason.prompt({
              title: t('esign.deactivate'), danger: true, onSubmit: (v) => post(`/api/v1/esign/routes/${r.id}/deactivate`, { reason: v.reason }).then(() => qc.invalidateQueries({ queryKey: ['esign-routes'] })),
            })} />
          </Space> },
        ]} />
      </Card>
      {edit && <RouteModal route={edit === 'new' ? undefined : edit} onClose={() => setEdit(null)} />}
      {reason.node}
    </>
  );
}

function RouteModal({ route, onClose }: { route?: ESignRoute; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [form] = Form.useForm();
  const bodies = useQuery({ queryKey: ['bodies-all'], queryFn: () => get<{ items: Body[] }>('/api/v1/bodies?pageSize=200') });
  const m = useMutation({
    mutationFn: (v: Record<string, any>) => {
      const body = { ...v, steps: (v.steps as { role: string; allowDelegation?: boolean }[]).map((s, i) => ({ order: i + 1, role: s.role, allowDelegation: !!s.allowDelegation })) };
      return route ? put(`/api/v1/esign/routes/${route.id}`, body) : post('/api/v1/esign/routes', body);
    },
    onSuccess: () => { message.success(t('common.saved')); void qc.invalidateQueries({ queryKey: ['esign-routes'] }); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open width={720} title={route ? t('common.edit') : t('esign.newRoute')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending}
      okText={t('common.save')} cancelText={t('common.cancel')} destroyOnHidden>
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)} initialValues={route
        ? { ...route, steps: route.steps }
        : { steps: [{ role: 'members' }, { role: 'chair', allowDelegation: true }], expiryDays: 14 }}>
        <Form.Item name="nameAr" label={t('common.name')} rules={[{ required: true }]}><Input /></Form.Item>
        <div className="form-row">
          <Form.Item name="bodyId" label={t('esign.scopeBody')}>
            <Select allowClear disabled={!!route} placeholder={t('esign.allBodies')} loading={bodies.isLoading}
              options={(bodies.data?.items ?? []).map((b) => ({ value: b.id, label: i18n.language === 'en' ? b.nameEn ?? b.nameAr : b.nameAr }))} />
          </Form.Item>
          <Form.Item name="meetingType" label={t('esign.scopeMeetingType')}>
            <Select allowClear disabled={!!route} placeholder={t('esign.allTypes')} options={MEETING_TYPES.map((x) => ({ value: x, label: t(`enums.meetingType.${x}`) }))} />
          </Form.Item>
        </div>
        <Form.List name="steps">
          {(fields, { add, remove, move }) => (
            <div className="route-steps">
              <div className="policy-section-title">{t('esign.stepsCol')}</div>
              {fields.map((f, i) => (
                <div key={f.key} className="route-step-row">
                  <Tag>{t('esign.step', { n: i + 1 })}</Tag>
                  <Form.Item name={[f.name, 'role']} rules={[{ required: true }]} style={{ minWidth: 180, marginBottom: 0 }}>
                    <Select options={ROLES.map((x) => ({ value: x, label: t(`esign.roles.${x}`) }))} />
                  </Form.Item>
                  <Form.Item noStyle shouldUpdate>{() => form.getFieldValue(['steps', f.name, 'role']) === 'chair' && (
                    <Form.Item name={[f.name, 'allowDelegation']} valuePropName="checked" style={{ marginBottom: 0 }}><Checkbox>{t('esign.delegationAllowed')}</Checkbox></Form.Item>
                  )}</Form.Item>
                  <Space style={{ marginInlineStart: 'auto' }}>
                    <Button size="small" disabled={i === 0} onClick={() => move(i, i - 1)}>↑</Button>
                    <Button size="small" danger disabled={fields.length === 1} onClick={() => remove(f.name)}>{t('common.delete')}</Button>
                  </Space>
                </div>
              ))}
              {fields.length < 3 && <Button icon={<PlusOutlined />} onClick={() => add({ role: ROLES.find((r) => !form.getFieldValue('steps')?.some((s: { role: string }) => s?.role === r)) })}>{t('esign.addStep')}</Button>}
              <Typography.Paragraph type="secondary" style={{ marginTop: 6 }}>{t('esign.stepsHelp')}</Typography.Paragraph>
            </div>
          )}
        </Form.List>
        <div className="form-row">
          <Form.Item name="signatureType" label={t('esign.typeCol')} extra={t('esign.typeHelp')}>
            <Select allowClear placeholder={t('esign.fromSettings')} options={TYPES.map((x) => ({ value: x, label: t(`esign.types.${x}`) }))} />
          </Form.Item>
          <Form.Item name="sequential" label={t('esign.sequential')} valuePropName="checked"><Switch /></Form.Item>
          <Form.Item name="expiryDays" label={t('esign.expiryDays')}><InputNumber min={1} max={90} /></Form.Item>
        </div>
        <Form.Item name="reason" label={t('common.reason')} rules={[{ required: true, min: 5 }]}><Input /></Form.Item>
      </Form>
    </Modal>
  );
}

// ============ صفحة المزوّد التجريبي ============

interface MockSession {
  testOnly: boolean; notice: string; providerRef: string; reference: string; envelopeStatus: string; signerName: string; signerStatus: string; order: number;
  sequential: boolean; documentSha256: string; expiresAt: string; turn: boolean; signers: { name: string; order: number; status: string }[];
}

export function MockProviderPage() {
  const { token } = useParams();
  const { t } = useTranslation();
  const { message } = App.useApp();
  const nav = useNavigate();
  const reason = useReasonPrompt();
  const q = useQuery({ queryKey: ['mock-session', token], queryFn: () => get<MockSession>(`/api/v1/esign/mock/sessions/${token}`) });
  const sign = useMutation({
    mutationFn: () => post(`/api/v1/esign/mock/sessions/${token}/sign`, {}),
    onSuccess: () => { message.success(t('esign.mockSigned')); void q.refetch(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  if (q.isLoading) return <Skeleton active />;
  if (q.error || !q.data) return <ErrorAlert error={q.error} />;
  const s = q.data;
  const done = s.signerStatus !== 'pending' || s.envelopeStatus !== 'sent';
  return (
    <div className="mock-provider">
      <div className="mock-banner"><ExperimentOutlined /> {t('esign.mockBanner')}</div>
      <Card title={<Space><SafetyCertificateOutlined />{t('esign.mockTitle')}</Space>} style={{ maxWidth: 760, margin: '0 auto' }}>
        <Alert type="error" showIcon style={{ marginBottom: 16 }} message={s.notice} />
        <Descriptions size="small" column={1} bordered style={{ marginBottom: 16 }}>
          <Descriptions.Item label={t('esign.mockEnvelope')}><span className="mono">{s.providerRef}</span> — {s.reference}</Descriptions.Item>
          <Descriptions.Item label={t('esign.signer')}>{s.signerName} ({t('esign.orderN', { n: s.order })})</Descriptions.Item>
          <Descriptions.Item label={t('esign.documentHash')}><span className="mono" style={{ fontSize: 11 }}>{s.documentSha256}</span></Descriptions.Item>
        </Descriptions>
        <Table size="small" rowKey="order" pagination={false} dataSource={s.signers} style={{ marginBottom: 16 }} columns={[
          { title: '#', dataIndex: 'order', width: 50 }, { title: t('esign.signer'), dataIndex: 'name' },
          { title: t('common.status'), dataIndex: 'status', render: (v) => <Tag color={v === 'signed' ? 'green' : v === 'declined' ? 'red' : 'gold'}>{t(`esign.mockStatus.${v}`)}</Tag> },
        ]} />
        {done ? (
          <Result status={s.signerStatus === 'signed' ? 'success' : 'info'} title={t(`esign.mockStatus.${s.signerStatus}`)}
            extra={<Button type="primary" onClick={() => nav(-1)}>{t('esign.backToCcms')}</Button>} />
        ) : (
          <Space wrap>
            <Button type="primary" icon={<SafetyCertificateOutlined />} disabled={!s.turn} loading={sign.isPending} onClick={() => sign.mutate()}>{t('esign.mockSign')}</Button>
            <Button danger onClick={() => reason.prompt({
              title: t('esign.decline'), danger: true, onSubmit: (v) => post(`/api/v1/esign/mock/sessions/${token}/decline`, { reason: v.reason }).then(() => q.refetch()),
            })}>{t('esign.decline')}</Button>
            {!s.turn && <Typography.Text type="secondary">{t('esign.notYourTurn')}</Typography.Text>}
          </Space>
        )}
      </Card>
      <div style={{ textAlign: 'center', marginTop: 12 }}><Link to="/">{t('esign.backToCcms')}</Link></div>
      {reason.node}
    </div>
  );
}
