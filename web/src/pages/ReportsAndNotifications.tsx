import { App, Button, Card, Col, Empty, List, Progress, Row, Table, Tag, Typography } from 'antd';
import { CheckOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { get, post, type Paged } from '../api/client';
import type { Notification } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag } from '../components/common';
import { formatDateTime, percent } from '../lib/format';

interface TasksByBody { bodyId: string; code: string; nameAr: string; total: number; completed: number; open: number; overdue: number }
interface MeetingsByMonth { month: number; heldOrPlanned: number; cancelled: number }
interface ResolutionsByStatus { status: string; type: string; count: number }

export function ReportsPage() {
  const { t, i18n } = useTranslation();
  const tasks = useQuery({ queryKey: ['rpt', 'tasks'], queryFn: () => get<TasksByBody[]>('/api/v1/reports/tasks-by-body') });
  const meetings = useQuery({ queryKey: ['rpt', 'meetings'], queryFn: () => get<MeetingsByMonth[]>('/api/v1/reports/meetings-by-month') });
  const res = useQuery({ queryKey: ['rpt', 'res'], queryFn: () => get<ResolutionsByStatus[]>('/api/v1/reports/resolutions-by-status') });
  const monthName = (m: number) => new Intl.DateTimeFormat(i18n.language === 'ar' ? 'ar-SA-u-ca-gregory' : 'en-GB', { month: 'long' }).format(new Date(2026, m - 1, 1));
  const maxMeetings = Math.max(1, ...(meetings.data ?? []).map((m) => m.heldOrPlanned + m.cancelled));
  return (
    <>
      <PageHeader title={t('reports.title')} />
      <ErrorAlert error={tasks.error ?? meetings.error ?? res.error} />
      <Card title={t('reports.tasksByBody')} style={{ marginBottom: 16 }}>
        <Table<TasksByBody>
          rowKey="bodyId" dataSource={tasks.data} loading={tasks.isLoading} pagination={false}
          columns={[
            { title: t('common.body'), render: (_, r) => <>{r.nameAr} <span className="mono">({r.code})</span></> },
            { title: t('common.total'), dataIndex: 'total' },
            { title: t('reports.open'), dataIndex: 'open' },
            { title: t('reports.overdue'), dataIndex: 'overdue', render: (v: number) => v > 0 ? <Tag color="red">{v}</Tag> : v },
            { title: t('reports.completed'), dataIndex: 'completed' },
            { title: t('dashboard.completionRate'), width: 200, render: (_, r) => <Progress percent={percent(r.completed, r.total)} size="small" strokeColor="#006837" /> },
          ]}
        />
      </Card>
      <Row gutter={16}>
        <Col xs={24} lg={14}>
          <Card title={t('reports.meetingsByMonth')}>
            {(meetings.data ?? []).length === 0 ? <Empty /> : (meetings.data ?? []).map((m) => (
              <div key={m.month} style={{ display: 'grid', gridTemplateColumns: '90px 1fr 40px', alignItems: 'center', gap: 8, marginBottom: 6 }}>
                <Typography.Text>{monthName(m.month)}</Typography.Text>
                <div style={{ display: 'flex', height: 14, background: '#eef1ec', borderRadius: 3, overflow: 'hidden' }} title={`${t('reports.held')}: ${m.heldOrPlanned} · ${t('reports.cancelled')}: ${m.cancelled}`}>
                  <div style={{ width: `${(m.heldOrPlanned / maxMeetings) * 100}%`, background: '#006837' }} />
                  <div style={{ width: `${(m.cancelled / maxMeetings) * 100}%`, background: '#B28655' }} />
                </div>
                <Typography.Text strong>{m.heldOrPlanned + m.cancelled}</Typography.Text>
              </div>
            ))}
            <div style={{ marginTop: 8 }}><Tag color="#006837">{t('reports.held')}</Tag><Tag color="#B28655">{t('reports.cancelled')}</Tag></div>
          </Card>
        </Col>
        <Col xs={24} lg={10}>
          <Card title={t('reports.resolutionsByStatus')}>
            <Table<ResolutionsByStatus>
              rowKey={(r) => r.status + r.type} dataSource={res.data} pagination={false} size="small"
              columns={[
                { title: t('common.type'), dataIndex: 'type', render: (v) => t(`enums.resolutionType.${v}`) },
                { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="resolutionStatus" value={v} /> },
                { title: t('common.total'), dataIndex: 'count' },
              ]}
            />
          </Card>
        </Col>
      </Row>
    </>
  );
}

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
