import { Card, Input, Table, Tag } from 'antd';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { get, type Paged } from '../api/client';
import type { MembershipRow } from '../api/types';
import { ErrorAlert, PageHeader, StatusTag } from '../components/common';
import { BodySelect, EnumSelect, UserSelect } from '../components/pickers';
import { formatDate } from '../lib/format';

const ROLES = ['chair', 'vice_chair', 'secretary', 'member', 'alternate', 'observer'];

/** سجل العضويات عبر كل الجهات المسموح للمستخدم الاطلاع عليها، بما فيها العضويات المنتهية (التاريخ الكامل). */
export default function MembershipsPage() {
  const { t, i18n } = useTranslation();
  const [f, setF] = useState<{ q?: string; bodyId?: string; userId?: string; role?: string; status?: string; page: number }>({ page: 1, status: 'active' });
  const q = useQuery({ queryKey: ['memberships', f], queryFn: () => get<Paged<MembershipRow>>('/api/v1/memberships', { ...f, pageSize: 25 }) });
  return (
    <>
      <PageHeader title={t('memberships.title')} subtitle={t('memberships.subtitle')} />
      <Card>
        <div className="toolbar">
          <Input.Search placeholder={t('common.search')} allowClear style={{ width: 240 }} onSearch={(v) => setF((x) => ({ ...x, q: v, page: 1 }))} />
          <BodySelect placeholder={t('common.body')} allowClear value={f.bodyId} onChange={(v) => setF((x) => ({ ...x, bodyId: v, page: 1 }))} />
          <UserSelect placeholder={t('bodies.user')} allowClear value={f.userId} onChange={(v) => setF((x) => ({ ...x, userId: v, page: 1 }))} />
          <EnumSelect group="roles" values={ROLES} placeholder={t('members.capacity')} allowClear value={f.role} onChange={(v) => setF((x) => ({ ...x, role: v, page: 1 }))} />
          <EnumSelect group="enums.memberStatus" values={['active', 'suspended', 'ended']} placeholder={t('common.status')} allowClear value={f.status}
            onChange={(v) => setF((x) => ({ ...x, status: v, page: 1 }))} />
        </div>
        <ErrorAlert error={q.error} />
        <Table<MembershipRow>
          rowKey="id" loading={q.isLoading} dataSource={q.data?.items} scroll={{ x: 1000 }}
          rowClassName={(m) => (m.status === 'ended' ? 'ended-row' : '')}
          pagination={{ current: f.page, total: q.data?.total, pageSize: 25, onChange: (page) => setF((x) => ({ ...x, page })) }}
          columns={[
            { title: t('common.name'), render: (_, m) => <div>{m.displayNameAr}<div className="item-meta mono">{m.email}</div></div> },
            { title: t('common.body'), render: (_, m) => <div><Link to={`/bodies/${m.bodyId}?tab=members`}>{m.bodyName}</Link><div className="item-meta mono">{m.bodyReference}</div></div> },
            { title: t('members.capacity'), dataIndex: 'role', render: (v) => <Tag>{t(`roles.${v}`)}</Tag> },
            { title: t('bodies.voting'), dataIndex: 'isVoting', render: (v) => (v ? t('common.yes') : t('common.no')) },
            { title: t('members.start'), dataIndex: 'startDate', render: (v) => formatDate(v, i18n.language) },
            { title: t('members.endDate'), dataIndex: 'endDate', render: (v) => formatDate(v, i18n.language) },
            { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="memberStatus" value={v} /> },
            { title: t('members.endReason'), dataIndex: 'endReason', render: (v) => v ?? '—' },
          ]}
        />
      </Card>
    </>
  );
}
