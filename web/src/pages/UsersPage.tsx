import { App, Button, Card, Form, Input, Modal, Select, Space, Table, Tabs, Tag } from 'antd';
import { PlusOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { get, patch, post, put, type Paged } from '../api/client';
import type { UserRow } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../components/common';
import { EntitySelect, EnumSelect } from '../components/pickers';
import { formatDateTime } from '../lib/format';

const SYSTEM_ROLES = ['system_admin', 'governance_officer', 'executive', 'auditor', 'external_representative'];
const CLEARANCES = ['public', 'restricted', 'secret', 'top_secret'];

interface RoleInfo { code: string; scope: string; nameAr: string; nameEn: string; permissions: string[] }

export default function UsersPage() {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const { me } = useAuth();
  const qc = useQueryClient();
  const [filters, setFilters] = useState<{ q?: string; role?: string; status?: string; page: number }>({ page: 1 });
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<UserRow | null>(null);
  const q = useQuery({ queryKey: ['users', filters], queryFn: () => get<Paged<UserRow>>('/api/v1/users', { ...filters, pageSize: 20 }) });
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => get<RoleInfo[]>('/api/v1/roles') });
  const toggle = useMutation({
    mutationFn: (u: UserRow) => patch(`/api/v1/users/${u.id}`, { status: u.status === 'active' ? 'disabled' : 'active' }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['users'] }),
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <>
      <PageHeader title={t('users.title')} extra={<Button type="primary" icon={<PlusOutlined />} onClick={() => setCreating(true)}>{t('users.new')}</Button>} />
      <Tabs items={[
        {
          key: 'users', label: t('users.title'),
          children: (
            <Card>
              <div className="toolbar">
                <Input.Search placeholder={t('common.search')} allowClear style={{ width: 260 }} onSearch={(v) => setFilters((f) => ({ ...f, q: v, page: 1 }))} />
                <EnumSelect group="roles" values={SYSTEM_ROLES} placeholder={t('users.systemRoles')} allowClear value={filters.role} onChange={(v) => setFilters((f) => ({ ...f, role: v, page: 1 }))} />
                <EnumSelect group="enums.userStatus" values={['active', 'disabled']} placeholder={t('common.status')} allowClear value={filters.status} onChange={(v) => setFilters((f) => ({ ...f, status: v, page: 1 }))} />
              </div>
              <ErrorAlert error={q.error} />
              <Table<UserRow>
                rowKey="id" loading={q.isLoading} dataSource={q.data?.items}
                pagination={{ current: filters.page, total: q.data?.total, pageSize: 20, onChange: (page) => setFilters((f) => ({ ...f, page })) }}
                columns={[
                  { title: t('common.name'), render: (_, u) => i18n.language === 'en' && u.displayNameEn ? u.displayNameEn : u.displayNameAr },
                  { title: t('users.email'), dataIndex: 'email', render: (v) => <span className="mono">{v}</span> },
                  { title: t('users.systemRoles'), dataIndex: 'systemRoles', render: (v: string[]) => <Space wrap size={[4, 4]}>{v.map((r) => <Tag key={r} color="green">{t(`roles.${r}`)}</Tag>)}</Space> },
                  { title: t('users.userType'), dataIndex: 'userType', render: (v) => t(`enums.userType.${v}`) },
                  { title: t('users.clearance'), dataIndex: 'clearance', render: (v) => t(`enums.classification.${v}`) },
                  { title: t('users.lastLogin'), dataIndex: 'lastLoginAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
                  { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="userStatus" value={v} /> },
                  {
                    title: t('common.actions'),
                    render: (_, u) => (
                      <Space>
                        <Button size="small" onClick={() => setEditing(u)}>{t('users.editRoles')}</Button>
                        {u.id !== me?.user.id && (
                          <Button size="small" danger={u.status === 'active'} onClick={() => toggle.mutate(u)}>{u.status === 'active' ? t('users.disable') : t('users.enable')}</Button>
                        )}
                      </Space>
                    ),
                  },
                ]}
              />
            </Card>
          ),
        },
        {
          key: 'catalog', label: t('users.rolesCatalog'),
          children: (
            <Card>
              <Table<RoleInfo>
                rowKey="code" dataSource={roles.data} loading={roles.isLoading} pagination={false}
                columns={[
                  { title: t('bodies.role'), render: (_, r) => (i18n.language === 'en' ? r.nameEn : r.nameAr) },
                  { title: t('users.scope'), dataIndex: 'scope', render: (v) => <Tag>{v}</Tag> },
                  { title: t('users.permissions'), dataIndex: 'permissions', render: (v: string[]) => <Space wrap size={[4, 4]}>{v.map((p) => <Tag key={p} className="mono">{p}</Tag>)}</Space> },
                ]}
              />
            </Card>
          ),
        },
      ]} />
      {creating && <CreateUserModal onClose={() => setCreating(false)} />}
      {editing && <RolesModal user={editing} onClose={() => setEditing(null)} />}
    </>
  );
}

function CreateUserModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [form] = Form.useForm();
  const roles = Form.useWatch('systemRoles', form) as string[] | undefined;
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post('/api/v1/users', v),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['users'] }); message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('users.new')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.create')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={{ clearance: 'restricted', systemRoles: [] }} onFinish={(v) => m.mutate({ ...v, initialPassword: v.initialPassword || undefined })}>
        <Form.Item name="email" label={t('users.email')} rules={[{ required: true, type: 'email' }]}><Input dir="ltr" /></Form.Item>
        <Form.Item name="displayNameAr" label={t('common.nameAr')} rules={[{ required: true }]}><Input /></Form.Item>
        <Form.Item name="displayNameEn" label={t('common.nameEn')}><Input dir="ltr" /></Form.Item>
        <Form.Item name="systemRoles" label={t('users.systemRoles')}>
          <Select mode="multiple" options={SYSTEM_ROLES.map((r) => ({ value: r, label: t(`roles.${r}`) }))} />
        </Form.Item>
        {roles?.includes('external_representative') && (
          <Form.Item name="externalEntityId" label={t('users.externalEntity')} rules={[{ required: true }]}><EntitySelect /></Form.Item>
        )}
        <Form.Item name="clearance" label={t('users.clearance')}>
          <Select options={CLEARANCES.map((c) => ({ value: c, label: t(`enums.classification.${c}`) }))} />
        </Form.Item>
        <Form.Item name="initialPassword" label={t('users.initialPassword')} rules={[{ min: 12 }]}><Input.Password dir="ltr" autoComplete="new-password" /></Form.Item>
      </Form>
    </Modal>
  );
}

function RolesModal({ user, onClose }: { user: UserRow; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: { roles: string[]; reason: string }) => put(`/api/v1/users/${user.id}/system-roles`, v),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['users'] }); message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={`${t('users.editRoles')} — ${user.displayNameAr}`} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={{ roles: user.systemRoles.filter((r) => r !== 'employee') }} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="roles" label={t('users.systemRoles')}>
          <Select mode="multiple" options={SYSTEM_ROLES.map((r) => ({ value: r, label: t(`roles.${r}`) }))} />
        </Form.Item>
        <Form.Item name="reason" label={t('common.reason')} rules={[{ required: true, min: 5 }]}><Input.TextArea rows={2} /></Form.Item>
      </Form>
    </Modal>
  );
}
