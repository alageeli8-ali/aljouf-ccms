import { App, Button, Card, Descriptions, Form, Modal, Popconfirm, Select, Skeleton, Space, Switch, Table, Tag, Input } from 'antd';
import { CheckCircleOutlined, UserAddOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router-dom';
import { get, post } from '../api/client';
import type { Body, Member } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../components/common';
import { UserSelect } from '../components/pickers';
import { dualDate } from '../lib/format';

const ROLES = ['chair', 'vice_chair', 'secretary', 'member', 'observer'];

export default function BodyDetailPage() {
  const { id } = useParams();
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const { me } = useAuth();
  const qc = useQueryClient();
  const [adding, setAdding] = useState(false);
  const body = useQuery({ queryKey: ['body', id], queryFn: () => get<{ body: Body; permissions: string[] }>(`/api/v1/bodies/${id}`) });
  const members = useQuery({ queryKey: ['members', id], queryFn: () => get<Member[]>(`/api/v1/bodies/${id}/members`), enabled: body.isSuccess });
  const perms = body.data?.permissions ?? [];
  const activate = useMutation({
    mutationFn: () => post(`/api/v1/bodies/${id}/activate`),
    onSuccess: () => { message.success(t('bodies.activated')); void qc.invalidateQueries({ queryKey: ['body', id] }); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const end = useMutation({
    mutationFn: (memberId: string) => post(`/api/v1/bodies/${id}/members/${memberId}/end`, { reason: 'إنهاء من واجهة الإدارة' }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['members', id] }),
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });

  if (body.isLoading) return <Skeleton active />;
  if (body.error) return <ErrorAlert error={body.error} />;
  const b = body.data!.body;
  const canManage = perms.includes('body.members.manage');

  return (
    <>
      <PageHeader
        title={i18n.language === 'en' && b.nameEn ? b.nameEn : b.nameAr}
        subtitle={<Space><span className="mono">{b.code}</span><StatusTag group="bodyStatus" value={b.status} /><Tag>{t(`enums.bodyType.${b.bodyType}`)}</Tag></Space>}
        extra={canManage && ['draft', 'suspended'].includes(b.status) && (
          <Button type="primary" icon={<CheckCircleOutlined />} loading={activate.isPending} onClick={() => activate.mutate()}>{t('bodies.activate')}</Button>
        )}
      />
      <Card style={{ marginBottom: 16 }}>
        <Descriptions column={{ xs: 1, md: 2 }} size="small">
          <Descriptions.Item label={t('bodies.mandate')} span={2}>{b.mandateAr ?? '—'}</Descriptions.Item>
          <Descriptions.Item label={t('bodies.formationNo')}>{b.formationDecisionNo ?? '—'}</Descriptions.Item>
          <Descriptions.Item label={t('bodies.formationDate')}>{dualDate(b.formationDecisionDate, i18n.language)}</Descriptions.Item>
          <Descriptions.Item label={t('bodies.termStart')}>{dualDate(b.termStart, i18n.language)}</Descriptions.Item>
          <Descriptions.Item label={t('bodies.termEnd')}>{dualDate(b.termEnd, i18n.language)}</Descriptions.Item>
          <Descriptions.Item label={t('bodies.classification')}>{t(`enums.classification.${b.classification}`)}</Descriptions.Item>
          <Descriptions.Item label={t('bodies.myPermissions')}>
            <Space wrap size={[4, 4]}>{perms.map((p) => <Tag key={p} className="mono">{p}</Tag>)}</Space>
          </Descriptions.Item>
        </Descriptions>
      </Card>
      <Card title={t('bodies.members')} extra={canManage && <Button icon={<UserAddOutlined />} onClick={() => setAdding(true)}>{t('bodies.addMember')}</Button>}>
        <ErrorAlert error={members.error} />
        <Table<Member>
          rowKey="id" loading={members.isLoading} dataSource={members.data} pagination={false}
          columns={[
            { title: t('common.name'), render: (_, m) => (i18n.language === 'en' && m.displayNameEn ? m.displayNameEn : m.displayNameAr) + (m.userId === me?.user.id ? ' ★' : '') },
            { title: t('users.email'), dataIndex: 'email', render: (v) => <span className="mono">{v}</span> },
            { title: t('bodies.role'), dataIndex: 'role', render: (v) => t(`roles.${v}`) },
            { title: t('bodies.voting'), dataIndex: 'isVoting', render: (v) => (v ? t('common.yes') : t('common.no')) },
            { title: t('bodies.termStart'), dataIndex: 'startDate', render: (v) => dualDate(v, i18n.language) },
            ...(canManage ? [{
              title: t('common.actions'),
              render: (_: unknown, m: Member) => (
                <Popconfirm title={t('bodies.endMember')} onConfirm={() => end.mutate(m.id)} okText={t('common.confirm')} cancelText={t('common.cancel')}>
                  <Button size="small" danger>{t('bodies.endMember')}</Button>
                </Popconfirm>
              ),
            }] : []),
          ]}
        />
      </Card>
      {adding && <AddMemberModal bodyId={id!} onClose={() => setAdding(false)} />}
    </>
  );
}

function AddMemberModal({ bodyId, onClose }: { bodyId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [form] = Form.useForm();
  const role = Form.useWatch('role', form);
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post(`/api/v1/bodies/${bodyId}/members`, v),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['members', bodyId] });
      void qc.invalidateQueries({ queryKey: ['body', bodyId] });
      onClose();
    },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const votingAllowed = !['secretary', 'observer'].includes(role);
  return (
    <Modal open title={t('bodies.addMember')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.add')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={{ role: 'member', isVoting: true }}
        onFinish={(v) => m.mutate({ ...v, isVoting: votingAllowed ? v.isVoting : false })}>
        <Form.Item name="userId" label={t('bodies.user')} rules={[{ required: true }]}><UserSelect /></Form.Item>
        <Form.Item name="role" label={t('bodies.role')}><Select options={ROLES.map((r) => ({ value: r, label: t(`roles.${r}`) }))} /></Form.Item>
        <Form.Item name="isVoting" label={t('bodies.voting')} valuePropName="checked"><Switch disabled={!votingAllowed} /></Form.Item>
        <Form.Item name="appointmentRef" label={t('bodies.formationNo')}><Input /></Form.Item>
      </Form>
    </Modal>
  );
}
