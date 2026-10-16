import { App, Button, DatePicker, Dropdown, Form, Input, Modal, Select, Space, Switch, Table, Tag, Tooltip } from 'antd';
import { CrownOutlined, HistoryOutlined, MoreOutlined, SafetyCertificateOutlined, UserAddOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import dayjs, { type Dayjs } from 'dayjs';
import { get, post } from '../../api/client';
import type { Delegation, Member } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, StatusTag, errorText } from '../../components/common';
import { displayName, useReasonPrompt } from '../../components/governance';
import { EntitySelect, UserSelect } from '../../components/pickers';
import { formatDate, formatDateTime } from '../../lib/format';

const ADD_ROLES = ['chair', 'vice_chair', 'secretary', 'member', 'alternate', 'observer'];
const CHANGE_ROLES = ['vice_chair', 'secretary', 'member', 'alternate', 'observer'];
const NON_VOTING = ['secretary', 'observer', 'alternate'];
const d = (v?: Dayjs | null) => v?.format('YYYY-MM-DD');

export function MembersPanel({ bodyId, bodyStatus, perms }: { bodyId: string; bodyStatus: string; perms: string[] }) {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const reason = useReasonPrompt();
  const [history, setHistory] = useState(false);
  const [adding, setAdding] = useState(false);
  const [changing, setChanging] = useState<Member | null>(null);
  const [chairChange, setChairChange] = useState(false);
  const q = useQuery({ queryKey: ['members', bodyId, history], queryFn: () => get<Member[]>(`/api/v1/bodies/${bodyId}/members`, { includeEnded: history }) });
  const canManage = perms.includes('body.members.manage') && !['dissolved', 'archived'].includes(bodyStatus);
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['members', bodyId] });
    void qc.invalidateQueries({ queryKey: ['body', bodyId] });
    void qc.invalidateQueries({ queryKey: ['body-activity', bodyId] });
  };
  const principals = (q.data ?? []).filter((m) => m.status !== 'ended' && m.isVoting && m.role !== 'alternate');

  const actions = (m: Member) => [
    ...(m.role !== 'chair' ? [{ key: 'change', label: t('members.change'), onClick: () => setChanging(m) }] : []),
    ...(m.status === 'active' && m.role !== 'chair' ? [{
      key: 'suspend', label: t('members.suspend'),
      onClick: () => reason.prompt({ title: t('members.suspend'), onSubmit: async (v) => { await post(`/api/v1/bodies/${bodyId}/members/${m.id}/suspend`, v); refresh(); } }),
    }] : []),
    ...(m.status === 'suspended' ? [{
      key: 'reactivate', label: t('members.reactivate'),
      onClick: () => reason.prompt({ title: t('members.reactivate'), onSubmit: async (v) => { await post(`/api/v1/bodies/${bodyId}/members/${m.id}/reactivate`, v); refresh(); } }),
    }] : []),
    {
      key: 'end', danger: true, label: t('members.end'),
      onClick: () => reason.prompt({
        title: `${t('members.end')} — ${m.displayNameAr}`, danger: true, description: m.role === 'alternate' ? undefined : t('members.endHelp'),
        extra: <Form.Item name="endDate" label={t('members.endDate')}><DatePicker style={{ width: '100%' }} /></Form.Item>,
        onSubmit: async (v) => { await post(`/api/v1/bodies/${bodyId}/members/${m.id}/end`, { ...v, endDate: d(v.endDate) }); refresh(); },
      }),
    },
  ];

  return (
    <>
      <div className="toolbar" style={{ justifyContent: 'space-between' }}>
        <Space>
          <Switch checked={history} onChange={setHistory} /> <span><HistoryOutlined /> {t('members.showHistory')}</span>
        </Space>
        {canManage && (
          <Space>
            <Button icon={<CrownOutlined />} onClick={() => setChairChange(true)}>{t('members.changeChair')}</Button>
            <Button type="primary" icon={<UserAddOutlined />} onClick={() => setAdding(true)}>{t('bodies.addMember')}</Button>
          </Space>
        )}
      </div>
      <ErrorAlert error={q.error} />
      <Table<Member>
        rowKey="id" size="small" loading={q.isLoading} dataSource={q.data} pagination={false} scroll={{ x: 1100 }}
        rowClassName={(m) => (m.status === 'ended' ? 'ended-row' : '')}
        expandable={{
          rowExpandable: (m) => !!(m.endReason || m.appointmentRef || m.workPhone),
          expandedRowRender: (m) => (
            <Space direction="vertical" size={2}>
              {m.appointmentRef && <span>{t('members.appointmentRef')}: {m.appointmentRef}</span>}
              {m.workPhone && <span>{t('members.workPhone')}: <span className="mono">{m.workPhone}</span></span>}
              {m.endReason && <span>{t('members.endReason')}: {m.endReason}</span>}
            </Space>
          ),
        }}
        columns={[
          {
            title: t('common.name'), fixed: 'left', width: 220,
            render: (_, m) => (
              <div>
                {displayName(i18n.language, m.displayNameAr, m.displayNameEn)}{' '}
                {m.verifiedIdentity && <Tooltip title={t('members.verified')}><SafetyCertificateOutlined style={{ color: '#006837' }} /></Tooltip>}
                <div className="item-meta">{[m.jobTitle, m.orgUnitName ?? m.entityName].filter(Boolean).join(' — ')}</div>
                <div className="item-meta"><bdi>{m.email}</bdi></div>
              </div>
            ),
          },
          { title: t('bodies.role'), dataIndex: 'role', render: (v, m) => <Tag color={v === 'chair' ? 'green' : v === 'alternate' ? 'gold' : undefined}>{t(`roles.${v}`)}{m.alternateForName ? ` ← ${m.alternateForName}` : ''}</Tag> },
          { title: t('bodies.voting'), dataIndex: 'isVoting', width: 90, render: (v) => (v ? t('common.yes') : t('common.no')) },
          { title: t('members.start'), dataIndex: 'startDate', render: (v) => formatDate(v, i18n.language) },
          { title: t('members.endDate'), dataIndex: 'endDate', render: (v) => formatDate(v, i18n.language) },
          { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="memberStatus" value={v} /> },
          ...(canManage ? [{
            title: '', width: 56, fixed: 'right' as const,
            render: (_: unknown, m: Member) => m.status !== 'ended' && (
              <Dropdown menu={{ items: actions(m) }} trigger={['click']}><Button size="small" icon={<MoreOutlined />} aria-label={t('common.actions')} /></Dropdown>
            ),
          }] : []),
        ]}
      />
      {adding && <AddMemberModal bodyId={bodyId} principals={principals} onClose={() => { setAdding(false); refresh(); }} />}
      {changing && <ChangeMemberModal bodyId={bodyId} member={changing} principals={principals.filter((p) => p.id !== changing.id)} onClose={() => { setChanging(null); refresh(); }} />}
      {chairChange && <ChangeChairModal bodyId={bodyId} onClose={() => { setChairChange(false); refresh(); }} />}
      {reason.node}
    </>
  );
}

function PrincipalSelect({ principals, value, onChange }: { principals: Member[]; value?: string; onChange?: (v: string) => void }) {
  const { t } = useTranslation();
  return <Select value={value} onChange={onChange} options={principals.map((p) => ({ value: p.id, label: `${p.displayNameAr} — ${t(`roles.${p.role}`)}` }))} />;
}

function AddMemberModal({ bodyId, principals, onClose }: { bodyId: string; principals: Member[]; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const role = Form.useWatch('role', form) as string;
  const m = useMutation({
    mutationFn: (v: Record<string, any>) => post(`/api/v1/bodies/${bodyId}/members`, {
      ...v, isVoting: NON_VOTING.includes(v.role) ? false : v.isVoting, startDate: d(v.startDate), endDate: d(v.endDate),
    }),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('bodies.addMember')} width={640} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.add')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={{ role: 'member', isVoting: true }} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="userId" label={t('bodies.user')} rules={[{ required: true }]} extra={t('members.identityHelp')}><UserSelect /></Form.Item>
        <div className="form-row">
          <Form.Item name="role" label={t('members.capacity')}><Select options={ADD_ROLES.map((r) => ({ value: r, label: t(`roles.${r}`) }))} /></Form.Item>
          <Form.Item name="isVoting" label={t('bodies.voting')} valuePropName="checked"><Switch disabled={NON_VOTING.includes(role)} /></Form.Item>
        </div>
        {role === 'alternate' && (
          <Form.Item name="alternateForMemberId" label={t('members.alternateFor')} rules={[{ required: true }]} extra={t('members.alternateHelp')}>
            <PrincipalSelect principals={principals} />
          </Form.Item>
        )}
        <Form.Item name="representsEntityId" label={t('members.represents')}><EntitySelect allowClear style={{ width: '100%' }} /></Form.Item>
        <div className="form-row">
          <Form.Item name="startDate" label={t('members.start')}><DatePicker style={{ width: '100%' }} /></Form.Item>
          <Form.Item name="endDate" label={t('members.endDate')}><DatePicker style={{ width: '100%' }} /></Form.Item>
          <Form.Item name="appointmentRef" label={t('members.appointmentRef')}><Input /></Form.Item>
        </div>
      </Form>
    </Modal>
  );
}

function ChangeMemberModal({ bodyId, member, principals, onClose }: { bodyId: string; member: Member; principals: Member[]; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const role = Form.useWatch('role', form) as string;
  const m = useMutation({
    mutationFn: (v: Record<string, any>) => post(`/api/v1/bodies/${bodyId}/members/${member.id}/change`, {
      ...v, isVoting: NON_VOTING.includes(v.role) ? false : v.isVoting, effectiveDate: d(v.effectiveDate),
    }),
    onSuccess: () => { message.success(t('members.changed')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={`${t('members.change')} — ${member.displayNameAr}`} width={600} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <p style={{ color: '#5f6b64' }}>{t('members.changeHelp')}</p>
      <Form form={form} layout="vertical" initialValues={{ role: member.role, isVoting: member.isVoting, alternateForMemberId: member.alternateForMemberId }} onFinish={(v) => m.mutate(v)}>
        <div className="form-row">
          <Form.Item name="role" label={t('members.capacity')}><Select options={CHANGE_ROLES.map((r) => ({ value: r, label: t(`roles.${r}`) }))} /></Form.Item>
          <Form.Item name="isVoting" label={t('bodies.voting')} valuePropName="checked"><Switch disabled={NON_VOTING.includes(role)} /></Form.Item>
          <Form.Item name="effectiveDate" label={t('lifecycle.effectiveDate')}><DatePicker style={{ width: '100%' }} /></Form.Item>
        </div>
        {role === 'alternate' && (
          <Form.Item name="alternateForMemberId" label={t('members.alternateFor')} rules={[{ required: true }]}><PrincipalSelect principals={principals} /></Form.Item>
        )}
        <Form.Item name="appointmentRef" label={t('members.appointmentRef')}><Input /></Form.Item>
        <Form.Item name="reason" label={t('common.reason')} rules={[{ required: true, min: 3 }]}><Input.TextArea rows={2} /></Form.Item>
      </Form>
    </Modal>
  );
}

function ChangeChairModal({ bodyId, onClose }: { bodyId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, any>) => post(`/api/v1/bodies/${bodyId}/change-chair`, { ...v, effectiveDate: d(v.effectiveDate) }),
    onSuccess: () => { message.success(t('members.chairChanged')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('members.changeChair')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <p style={{ color: '#5f6b64' }}>{t('members.changeChairHelp')}</p>
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)}>
        <Form.Item name="userId" label={t('members.newChair')} rules={[{ required: true }]}><UserSelect /></Form.Item>
        <div className="form-row">
          <Form.Item name="decisionNo" label={t('bodies.decisionNo')}><Input /></Form.Item>
          <Form.Item name="effectiveDate" label={t('lifecycle.effectiveDate')}><DatePicker style={{ width: '100%' }} /></Form.Item>
        </div>
        <Form.Item name="reason" label={t('common.reason')} rules={[{ required: true, min: 3 }]}><Input.TextArea rows={2} /></Form.Item>
      </Form>
    </Modal>
  );
}

export function DelegationsPanel({ bodyId, perms }: { bodyId: string; perms: string[] }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const [open, setOpen] = useState(false);
  const q = useQuery({ queryKey: ['delegations', bodyId], queryFn: () => get<Delegation[]>(`/api/v1/bodies/${bodyId}/delegations`) });
  const revoke = useMutation({
    mutationFn: (id: string) => post(`/api/v1/bodies/${bodyId}/delegations/${id}/revoke`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['delegations', bodyId] }),
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const canDelegate = perms.includes('body.delegate') || perms.includes('body.members.manage');
  return (
    <>
      <div className="toolbar" style={{ justifyContent: 'space-between' }}>
        <span style={{ color: '#8c8c8c' }}>{t('delegations.help')}</span>
        {canDelegate && <Button type="primary" onClick={() => setOpen(true)}>{t('delegations.new')}</Button>}
      </div>
      <ErrorAlert error={q.error} />
      <Table<Delegation> rowKey="id" size="small" loading={q.isLoading} dataSource={q.data} pagination={false} locale={{ emptyText: t('common.empty') }} columns={[
        { title: t('delegations.scope'), dataIndex: 'scope', render: (v) => <Tag color={v === 'chair_authority' ? 'green' : 'blue'}>{t(`enums.delegationScope.${v}`)}</Tag> },
        { title: t('delegations.delegator'), dataIndex: 'delegatorName' },
        { title: t('delegations.delegate'), dataIndex: 'delegateName' },
        { title: t('delegations.from'), dataIndex: 'validFrom', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
        { title: t('delegations.to'), dataIndex: 'validTo', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
        { title: t('common.reason'), dataIndex: 'reason' },
        { title: t('common.status'), render: (_, x) => x.status === 'revoked' ? <Tag>{t('delegations.revoked')}</Tag> : x.effective ? <Tag color="green">{t('delegations.effective')}</Tag> : <Tag color="gold">{t('delegations.notEffective')}</Tag> },
        { title: '', render: (_, x) => x.status === 'active' && (x.delegatorUserId === me?.user.id || perms.includes('body.members.manage')) && (
          <Button size="small" danger loading={revoke.isPending} onClick={() => revoke.mutate(x.id)}>{t('delegations.revoke')}</Button>
        ) },
      ]} />
      {open && <DelegationModal bodyId={bodyId} onBehalf={perms.includes('body.members.manage')} onClose={() => { setOpen(false); void qc.invalidateQueries({ queryKey: ['delegations', bodyId] }); }} />}
    </>
  );
}

function DelegationModal({ bodyId, onBehalf, onClose }: { bodyId: string; onBehalf: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const members = useQuery({ queryKey: ['members', bodyId, false], queryFn: () => get<Member[]>(`/api/v1/bodies/${bodyId}/members`) });
  const opts = (members.data ?? []).filter((m) => m.status === 'active').map((m) => ({ value: m.userId, label: `${m.displayNameAr} — ${t(`roles.${m.role}`)}` }));
  const m = useMutation({
    mutationFn: (v: Record<string, any>) => post(`/api/v1/bodies/${bodyId}/delegations`, {
      ...v, validFrom: (v.range[0] as Dayjs).toISOString(), validTo: (v.range[1] as Dayjs).toISOString(), range: undefined,
    }),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('delegations.new')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={{ scope: 'chair_authority', range: [dayjs(), dayjs().add(7, 'day')] }} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="scope" label={t('delegations.scope')}>
          <Select options={['chair_authority', 'attendance_proxy'].map((v) => ({ value: v, label: t(`enums.delegationScope.${v}`) }))} />
        </Form.Item>
        {onBehalf && <Form.Item name="delegatorUserId" label={t('delegations.delegator')} extra={t('delegations.onBehalfHelp')}><Select allowClear options={opts} /></Form.Item>}
        <Form.Item name="delegateUserId" label={t('delegations.delegate')} rules={[{ required: true }]}><Select options={opts} /></Form.Item>
        <Form.Item name="range" label={t('delegations.period')} rules={[{ required: true }]}><DatePicker.RangePicker showTime style={{ width: '100%' }} /></Form.Item>
        <Form.Item name="reason" label={t('common.reason')} rules={[{ required: true, min: 3 }]}><Input.TextArea rows={2} /></Form.Item>
      </Form>
    </Modal>
  );
}
