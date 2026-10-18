import { App, Button, Card, List, Tag } from 'antd';
import { CheckOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { get, post, type Paged } from '../api/client';
import type { Notification } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader } from '../components/common';
import { formatDateTime } from '../lib/format';

export function NotificationsPage() {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const nav = useNavigate();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const [page, setPage] = useState(1);
  const q = useQuery({ queryKey: ['notifications', page], queryFn: () => get<Paged<Notification>>('/api/v1/notifications', { page, pageSize: 20 }) });
  const invalidate = () => { void qc.invalidateQueries({ queryKey: ['notifications'] }); void qc.invalidateQueries({ queryKey: ['unread'] }); };
  const readAll = useMutation({ mutationFn: () => post('/api/v1/notifications/read-all'), onSuccess: () => { invalidate(); message.success(t('common.saved')); } });
  const open = async (n: Notification) => {
    if (!n.isRead) { await post(`/api/v1/notifications/${n.id}/read`); invalidate(); }
    if (n.link) nav(n.link);
  };
  return (
    <>
      <PageHeader title={t('notifications.title')} extra={<Button icon={<CheckOutlined />} onClick={() => readAll.mutate()}>{t('notifications.markAll')}</Button>} />
      <Card>
        <ErrorAlert error={q.error} />
        <List
          loading={q.isLoading} dataSource={q.data?.items}
          pagination={{ current: page, total: q.data?.total, pageSize: 20, onChange: setPage }}
          renderItem={(n) => (
            <List.Item onClick={() => void open(n)} style={{ cursor: n.link ? 'pointer' : 'default', background: n.isRead ? undefined : '#f3f8f5', paddingInline: 12 }}>
              <List.Item.Meta
                title={<>{!n.isRead && <Tag color="green">{t('notifications.unread')}</Tag>}{i18n.language === 'en' ? n.titleEn : n.titleAr}</>}
                description={formatDateTime(n.createdAt, i18n.language, me?.user.calendar)}
              />
              {n.severity !== 'info' && <Tag color={n.severity === 'critical' ? 'red' : 'gold'}>{n.severity}</Tag>}
            </List.Item>
          )}
        />
      </Card>
    </>
  );
}
