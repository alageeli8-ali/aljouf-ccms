import { App, Button, Card, Col, DatePicker, Descriptions, Form, Input, InputNumber, Modal, Row, Select, Space, Switch, Table, Tag } from 'antd';
import { DeleteOutlined, PlusOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import dayjs, { type Dayjs } from 'dayjs';
import { get, put } from '../../api/client';
import type { BodyPolicy, EscalationLevel, EscalationRule, MinutesTemplate, QuorumRule } from '../../api/types';
import { ErrorAlert, errorText } from '../../components/common';
import { formatDate, formatDateTime } from '../../lib/format';

export function quorumText(q: QuorumRule, t: (k: string, o?: Record<string, unknown>) => string) {
  const base = q.type === 'fraction' ? t('policy.ruleFraction', { n: q.numerator, d: q.denominator })
    : q.type === 'fixed' ? t('policy.ruleFixed', { n: q.fixedCount }) : t('policy.ruleMajority');
  return base + (q.chairRequired ? ` — ${t('policy.chairRequired')}` : '');
}

export function PolicyPanel({ bodyId, canEdit }: { bodyId: string; canEdit: boolean }) {
  const { t, i18n } = useTranslation();
  const [open, setOpen] = useState(false);
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['policy', bodyId], queryFn: () => get<{ current?: BodyPolicy; versions: BodyPolicy[] }>(`/api/v1/bodies/${bodyId}/policy`) });
  if (q.error) return <ErrorAlert error={q.error} />;
  const p = q.data?.current;
  const yes = (v: boolean) => (v ? <Tag color="green">{t('common.yes')}</Tag> : <Tag>{t('common.no')}</Tag>);
  return (
    <>
      <div className="toolbar" style={{ justifyContent: 'space-between' }}>
        <span style={{ color: '#8c8c8c' }}>{t('policy.help')}</span>
        {canEdit && <Button type="primary" onClick={() => setOpen(true)}>{t('policy.newVersion')}</Button>}
      </div>
      {p && (
        <Descriptions bordered size="small" column={{ xs: 1, md: 2 }} title={`${t('policy.current')} — v${p.version}`}>
          <Descriptions.Item label={t('policy.effectiveFrom')}>{formatDate(p.effectiveFrom, i18n.language)}</Descriptions.Item>
          <Descriptions.Item label={t('policy.quorum')}>{quorumText(p.quorum, t)}</Descriptions.Item>
          <Descriptions.Item label={t('policy.countRemote')}>{yes(p.quorum.countRemote)}</Descriptions.Item>
          <Descriptions.Item label={t('policy.allowOverride')}>{yes(p.quorum.allowOverride)}</Descriptions.Item>
          <Descriptions.Item label={t('policy.votingThreshold')}>{t(`enums.votingThreshold.${p.voting.threshold}`)}</Descriptions.Item>
          <Descriptions.Item label={t('policy.tieBreaker')}>{t(`enums.tieBreaker.${p.voting.tieBreaker}`)}</Descriptions.Item>
          <Descriptions.Item label={t('policy.allowProxy')}>{yes(p.allowProxy)}{p.allowProxy && ` (${t('policy.maxProxies')}: ${p.maxProxiesPerMember})`}</Descriptions.Item>
          <Descriptions.Item label={t('policy.allowRemote')}>{yes(p.allowRemoteAttendance)}</Descriptions.Item>
          <Descriptions.Item label={t('policy.allowCirculation')}>{yes(p.allowCirculation)}</Descriptions.Item>
          <Descriptions.Item label={t('policy.minutesFlow')} span={2}>
            {p.minutesFlow && (
              <span>
                {['draft', ...(p.minutesFlow.secretaryReview ? ['secretary_review'] : []), ...(p.minutesFlow.chairReview ? ['chair_review'] : []),
                  ...(p.minutesFlow.memberReview ? ['member_review'] : []), ...(p.minutesFlow.signatures !== 'none' ? ['pending_signatures'] : []), 'pending_final_approval', 'approved']
                  .map((x) => t(`enums.minutesStatus.${x}`)).join(' ← ')}
              </span>
            )}
          </Descriptions.Item>
          <Descriptions.Item label={t('policy.signatures')}>{p.minutesFlow ? t(`enums.signatureMode.${p.minutesFlow.signatures}`) : '—'}</Descriptions.Item>
          <Descriptions.Item label={t('policy.signatureFallback')}>{p.minutesFlow ? t(`enums.signoffMethod.${p.minutesFlow.signatureFallback}`) : '—'}</Descriptions.Item>
          <Descriptions.Item label={t('policy.finalApproval')}>{p.minutesFlow ? t(`enums.finalApproval.${p.minutesFlow.finalApproval}`) : '—'}</Descriptions.Item>
          <Descriptions.Item label={t('policy.reapproval')}>{p.minutesFlow ? t(`enums.reapproval.${p.minutesFlow.amendmentReapproval}`) : '—'}</Descriptions.Item>
          <Descriptions.Item label={t('policy.reviewDays')}>{p.minutesReviewDays}</Descriptions.Item>
          <Descriptions.Item label={t('policy.extensionApprover')}>{t(`roles.${p.extensionApprover}`)}</Descriptions.Item>
        </Descriptions>
      )}
      {p?.escalationFlow && <EscalationView rule={p.escalationFlow} />}
      <Card size="small" title={t('policy.versions')} style={{ marginTop: 16 }}>
        <Table<BodyPolicy> rowKey="id" size="small" pagination={false} loading={q.isLoading} dataSource={q.data?.versions} columns={[
          { title: 'v', dataIndex: 'version', width: 50 },
          { title: t('policy.effectiveFrom'), dataIndex: 'effectiveFrom', render: (v) => formatDate(v, i18n.language) },
          { title: t('policy.quorum'), render: (_, v) => quorumText(v.quorum, t) },
          { title: t('policy.allowProxy'), dataIndex: 'allowProxy', render: yes },
          { title: t('policy.allowCirculation'), dataIndex: 'allowCirculation', render: yes },
          { title: t('activity.by'), dataIndex: 'createdByName', render: (v) => v ?? '—' },
          { title: t('common.date'), dataIndex: 'createdAt', render: (v) => formatDateTime(v, i18n.language) },
        ]} />
      </Card>
      {open && <PolicyModal bodyId={bodyId} current={p} onClose={() => { setOpen(false); void qc.invalidateQueries({ queryKey: ['policy', bodyId] }); }} />}
    </>
  );
}

function PolicyModal({ bodyId, current, onClose }: { bodyId: string; current?: BodyPolicy; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const type = Form.useWatch('quorumType', form) as string;
  const proxy = Form.useWatch('allowProxy', form) as boolean;
  const m = useMutation({
    mutationFn: (v: Record<string, any>) => put(`/api/v1/bodies/${bodyId}/policy`, {
      ...v, effectiveFrom: (v.effectiveFrom as Dayjs).format('YYYY-MM-DD'), signoffRequired: v.minutesSignatures !== 'none',
      escalation: {
        workingDays: v.escWorkingDays, repeatOverdueEveryDays: v.escRepeat, reviewOverdueDays: v.escReviewDays,
        levels: ((v.escLevels ?? []) as EscalationLevel[]).map((l, i) => ({ ...l, level: i })),
      },
    }),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const c = current;
  const opt = (group: string, values: string[]) => values.map((v) => ({ value: v, label: t(`enums.${group}.${v}`) }));
  return (
    <Modal open width={760} title={t('policy.newVersion')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <p style={{ color: '#5f6b64' }}>{t('policy.versionHelp')}</p>
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)} initialValues={{
        effectiveFrom: dayjs(), quorumType: c?.quorum.type ?? 'majority', quorumNumerator: c?.quorum.numerator ?? 2, quorumDenominator: c?.quorum.denominator ?? 3,
        quorumFixedCount: c?.quorum.fixedCount, chairRequired: c?.quorum.chairRequired ?? false, countRemote: c?.quorum.countRemote ?? true, allowOverride: c?.quorum.allowOverride ?? false,
        votingThreshold: c?.voting.threshold ?? 'majority_of_votes_cast', abstentions: c?.voting.abstentions ?? 'excluded', tieBreaker: c?.voting.tieBreaker ?? 'chair_casting_vote',
        secretAllowed: c?.voting.secretAllowed ?? false, signoffRequired: c?.signoffRequired ?? false, minutesReviewDays: c?.minutesReviewDays ?? 3,
        extensionApprover: c?.extensionApprover ?? 'secretary', allowProxy: c?.allowProxy ?? false, maxProxiesPerMember: c?.maxProxiesPerMember ?? 1,
        allowRemoteAttendance: c?.allowRemoteAttendance ?? true, allowCirculation: c?.allowCirculation ?? false,
        minutesSecretaryReview: c?.minutesFlow?.secretaryReview ?? false, minutesChairReview: c?.minutesFlow?.chairReview ?? true,
        minutesMemberReview: c?.minutesFlow?.memberReview ?? true, minutesSignatures: c?.minutesFlow?.signatures ?? (c?.signoffRequired ? 'present_members' : 'none'),
        minutesSignatureFallback: c?.minutesFlow?.signatureFallback ?? 'attestation', minutesFinalApproval: c?.minutesFlow?.finalApproval ?? 'chair',
        minutesAmendmentReapproval: c?.minutesFlow?.amendmentReapproval ?? 'full', minutesTemplateCode: c?.minutesFlow?.templateCode ?? 'standard',
        escWorkingDays: c?.escalationFlow?.workingDays ?? true, escRepeat: c?.escalationFlow?.repeatOverdueEveryDays ?? 7,
        escReviewDays: c?.escalationFlow?.reviewOverdueDays ?? 3, escLevels: c?.escalationFlow?.levels ?? DEFAULT_LEVELS,
      }}>
        <Row gutter={12}>
          <Col xs={24} md={8}><Form.Item name="effectiveFrom" label={t('policy.effectiveFrom')} rules={[{ required: true }]}><DatePicker style={{ width: '100%' }} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item name="quorumType" label={t('policy.quorumType')}><Select options={opt('quorumType', ['majority', 'fraction', 'fixed'])} /></Form.Item></Col>
          {type === 'fraction' && <>
            <Col xs={12} md={4}><Form.Item name="quorumNumerator" label={t('policy.numerator')}><InputNumber min={1} max={99} style={{ width: '100%' }} /></Form.Item></Col>
            <Col xs={12} md={4}><Form.Item name="quorumDenominator" label={t('policy.denominator')}><InputNumber min={1} max={100} style={{ width: '100%' }} /></Form.Item></Col>
          </>}
          {type === 'fixed' && <Col xs={24} md={8}><Form.Item name="quorumFixedCount" label={t('policy.fixedCount')} rules={[{ required: true }]}><InputNumber min={1} max={500} style={{ width: '100%' }} /></Form.Item></Col>}
        </Row>
        <Row gutter={12}>
          <Col xs={12} md={6}><Form.Item name="chairRequired" label={t('policy.chairRequired')} valuePropName="checked"><Switch /></Form.Item></Col>
          <Col xs={12} md={6}><Form.Item name="countRemote" label={t('policy.countRemote')} valuePropName="checked"><Switch /></Form.Item></Col>
          <Col xs={12} md={6}><Form.Item name="allowOverride" label={t('policy.allowOverride')} valuePropName="checked"><Switch /></Form.Item></Col>
          <Col xs={12} md={6}><Form.Item name="allowRemoteAttendance" label={t('policy.allowRemote')} valuePropName="checked"><Switch /></Form.Item></Col>
          <Col xs={12} md={6}><Form.Item name="allowProxy" label={t('policy.allowProxy')} valuePropName="checked"><Switch /></Form.Item></Col>
          {proxy && <Col xs={12} md={6}><Form.Item name="maxProxiesPerMember" label={t('policy.maxProxies')}><InputNumber min={1} max={5} /></Form.Item></Col>}
          <Col xs={12} md={6}><Form.Item name="allowCirculation" label={t('policy.allowCirculation')} valuePropName="checked"><Switch /></Form.Item></Col>
          <Col xs={12} md={6}><Form.Item name="secretAllowed" label={t('policy.secretAllowed')} valuePropName="checked"><Switch /></Form.Item></Col>
        </Row>
        <Row gutter={12}>
          <Col xs={24} md={8}><Form.Item name="votingThreshold" label={t('policy.votingThreshold')}><Select options={opt('votingThreshold', ['majority_of_votes_cast', 'majority_of_present', 'absolute_majority_of_members', 'two_thirds_of_present', 'unanimity'])} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item name="abstentions" label={t('policy.abstentions')}><Select options={opt('abstentions', ['excluded', 'count_as_against'])} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item name="tieBreaker" label={t('policy.tieBreaker')}><Select options={opt('tieBreaker', ['chair_casting_vote', 'motion_fails', 'revote'])} /></Form.Item></Col>
        </Row>
        <div className="policy-section-title">{t('policy.minutesFlow')}</div>
        <Row gutter={12}>
          <Col xs={12} md={6}><Form.Item name="minutesSecretaryReview" label={t('enums.minutesStatus.secretary_review')} valuePropName="checked"><Switch /></Form.Item></Col>
          <Col xs={12} md={6}><Form.Item name="minutesChairReview" label={t('enums.minutesStatus.chair_review')} valuePropName="checked"><Switch /></Form.Item></Col>
          <Col xs={12} md={6}><Form.Item name="minutesMemberReview" label={t('enums.minutesStatus.member_review')} valuePropName="checked"><Switch /></Form.Item></Col>
          <Col xs={12} md={6}><Form.Item name="minutesReviewDays" label={t('policy.reviewDays')}><InputNumber min={1} max={60} /></Form.Item></Col>
        </Row>
        <Row gutter={12}>
          <Col xs={24} md={8}><Form.Item name="minutesSignatures" label={t('policy.signatures')}><Select options={opt('signatureMode', ['none', 'present_members', 'present_members_and_chair', 'all_members'])} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item name="minutesSignatureFallback" label={t('policy.signatureFallback')} extra={t('policy.fallbackHelp')}><Select options={opt('signoffMethod', ['attestation', 'wet_signature'])} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item name="minutesFinalApproval" label={t('policy.finalApproval')}><Select options={opt('finalApproval', ['chair', 'recorded_external'])} /></Form.Item></Col>
        </Row>
        <Row gutter={12}>
          <Col xs={24} md={8}><Form.Item name="minutesAmendmentReapproval" label={t('policy.reapproval')}><Select options={opt('reapproval', ['full', 'final_only'])} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item name="minutesTemplateCode" label={t('policy.template')}><TemplateCodeSelect /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item name="extensionApprover" label={t('policy.extensionApprover')}><Select options={['secretary', 'chair'].map((v) => ({ value: v, label: t(`roles.${v}`) }))} /></Form.Item></Col>
        </Row>
        <div className="policy-section-title">{t('tasks4.escalation.title')}</div>
        <p style={{ color: '#5f6b64', marginTop: 0 }}>{t('tasks4.escalation.help')}</p>
        <Row gutter={12}>
          <Col xs={24} md={8}><Form.Item name="escWorkingDays" label={t('tasks4.escalation.workingDays')} valuePropName="checked"><Switch /></Form.Item></Col>
          <Col xs={12} md={8}><Form.Item name="escRepeat" label={t('tasks4.escalation.repeat')}><InputNumber min={1} max={60} /></Form.Item></Col>
          <Col xs={12} md={8}><Form.Item name="escReviewDays" label={t('tasks4.escalation.reviewDays')}><InputNumber min={1} max={30} /></Form.Item></Col>
        </Row>
        <Form.List name="escLevels" rules={[{ validator: async (_, v) => { if (!v || v.length < 1) throw new Error(t('tasks4.escalation.needOne')); } }]}>
          {(fields, { add, remove }, { errors }) => (
            <>
              {fields.map((f, i) => (
                <div key={f.key} className="esc-level-row">
                  <Tag color={i === 0 ? 'blue' : i === 1 ? 'orange' : 'red'}>{t('tasks4.escalation.level', { n: i })}</Tag>
                  <Form.Item name={[f.name, 'trigger']} rules={[{ required: true }]} style={{ minWidth: 170 }}>
                    <Select options={TRIGGERS.map((x) => ({ value: x, label: t(`tasks4.escalation.triggers.${x}`) }))} />
                  </Form.Item>
                  <Form.Item name={[f.name, 'offsetDays']} rules={[{ required: true }]}>
                    <InputNumber min={-30} max={365} addonAfter={t('tasks4.escalation.days')} style={{ width: 130 }} />
                  </Form.Item>
                  <Form.Item name={[f.name, 'notify']} rules={[{ required: true, type: 'array', min: 1 }]} style={{ flex: 1, minWidth: 220 }}>
                    <Select mode="multiple" placeholder={t('tasks4.escalation.notify')} options={RECIPIENTS.map((x) => ({ value: x, label: t(`tasks4.escalation.recipients.${x}`) }))} />
                  </Form.Item>
                  <Button type="text" danger icon={<DeleteOutlined />} onClick={() => remove(f.name)} aria-label={t('common.delete')} />
                </div>
              ))}
              <Form.ErrorList errors={errors} />
              {fields.length < 8 && <Button icon={<PlusOutlined />} onClick={() => add({ trigger: 'overdue_l2', offsetDays: 7, notify: ['chair'] })} style={{ marginBottom: 12 }}>{t('tasks4.escalation.addLevel')}</Button>}
            </>
          )}
        </Form.List>
        <Form.Item name="reason" label={t('common.reason')} rules={[{ required: true, min: 5 }]}><Input.TextArea rows={2} /></Form.Item>
      </Form>
    </Modal>
  );
}

function TemplateCodeSelect({ value, onChange }: { value?: string; onChange?: (v: string) => void }) {
  const q = useQuery({ queryKey: ['minutes-templates'], queryFn: () => get<MinutesTemplate[]>('/api/v1/minutes/templates') });
  return <Select value={value} onChange={onChange} loading={q.isLoading}
    options={(q.data ?? []).filter((x) => x.status === 'active').map((x) => ({ value: x.code, label: x.nameAr }))} />;
}

const TRIGGERS = ['due_soon', 'overdue', 'overdue_l2', 'overdue_l3', 'overdue_l4'];
const RECIPIENTS = ['assignee', 'accountable', 'verifier', 'secretary', 'chair', 'vice_chair', 'assignee_org_unit_manager', 'governance_officer', 'executive'];
const DEFAULT_LEVELS: EscalationLevel[] = [
  { level: 0, trigger: 'due_soon', offsetDays: -3, notify: ['assignee', 'accountable'] },
  { level: 1, trigger: 'overdue', offsetDays: 0, notify: ['assignee', 'accountable', 'secretary'] },
  { level: 2, trigger: 'overdue_l2', offsetDays: 7, notify: ['assignee', 'accountable', 'secretary', 'assignee_org_unit_manager'] },
  { level: 3, trigger: 'overdue_l3', offsetDays: 14, notify: ['secretary', 'chair'] },
];

/** عرض مستويات التنبيه والتصعيد السارية للجهة. */
function EscalationView({ rule }: { rule: EscalationRule }) {
  const { t } = useTranslation();
  return (
    <Card size="small" title={t('tasks4.escalation.title')} style={{ marginTop: 16 }}
      extra={<Space size={12} className="item-meta">
        <span>{rule.workingDays ? t('tasks4.escalation.workingDaysOn') : t('tasks4.escalation.calendarDays')}</span>
        <span>{t('tasks4.escalation.repeatShort', { n: rule.repeatOverdueEveryDays })}</span>
        <span>{t('tasks4.escalation.reviewShort', { n: rule.reviewOverdueDays })}</span>
      </Space>}>
      <Table<EscalationLevel> rowKey="level" size="small" pagination={false} dataSource={rule.levels} columns={[
        { title: t('tasks4.escalation.levelCol'), dataIndex: 'level', width: 80, render: (v: number) => <Tag color={v === 0 ? 'blue' : v === 1 ? 'orange' : 'red'}>{v}</Tag> },
        { title: t('tasks4.escalation.trigger'), dataIndex: 'trigger', render: (v: string) => t(`tasks4.escalation.triggers.${v}`) },
        { title: t('tasks4.escalation.when'), dataIndex: 'offsetDays', render: (v: number, l) => offsetText(v, l.trigger, t) },
        { title: t('tasks4.escalation.notify'), dataIndex: 'notify', render: (v: string[]) => v.map((x) => <Tag key={x}>{t(`tasks4.escalation.recipients.${x}`)}</Tag>) },
      ]} />
    </Card>
  );
}

function offsetText(days: number, trigger: string, t: (k: string, o?: Record<string, unknown>) => string) {
  if (trigger === 'due_soon') return t('tasks4.escalation.beforeDue', { n: Math.abs(days) });
  if (days === 0) return t('tasks4.escalation.onOverdue');
  return t('tasks4.escalation.afterDue', { n: days });
}
