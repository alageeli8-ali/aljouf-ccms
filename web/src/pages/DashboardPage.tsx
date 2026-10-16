import { Card, Col, List, Progress, Row, Skeleton, Statistic, Typography } from 'antd';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { get } from '../api/client';
import type { Dashboard } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader } from '../components/common';
import { formatDateTime, percent } from '../lib/format';

export default function DashboardPage() {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const q = useQuery({ queryKey: ['dashboard'], queryFn: () => get<Dashboard>('/api/v1/dashboard') });
  const c = q.data?.counters;
  const name = i18n.language === 'en' ? me?.user.displayNameEn ?? me?.user.displayNameAr : me?.user.displayNameAr;
  const tiles: { key: keyof Dashboard['counters']; to: string; tone?: string }[] = [
    { key: 'myOpenTasks', to: '/tasks' },
    { key: 'myPendingSignoffs', to: '/minutes', tone: 'tan' },
    { key: 'upcomingMeetings', to: '/meetings' },
    { key: 'overdueTasks', to: '/tasks?overdue=true', tone: 'warn' },
    { key: 'activeBodies', to: '/bodies' },
    { key: 'minutesInProgress', to: '/minutes', tone: 'tan' },
    { key: 'openResolutions', to: '/resolutions' },
    { key: 'openTasks', to: '/tasks' },
  ];
  return (
    <>
      <PageHeader title={`${t('dashboard.greeting')}${i18n.language === 'ar' ? '، ' : ', '}${name ?? ''}`} subtitle={t('app.org')} />
      <ErrorAlert error={q.error} />
      {q.isLoading ? <Skeleton active /> : c && (
        <>
          <Row gutter={[16, 16]}>
            {tiles.map((tile) => (
              <Col xs={12} md={6} key={tile.key}>
                <Link to={tile.to}>
                  <Card className={`stat-tile ${tile.tone ?? ''}`} hoverable size="small">
                    <Statistic title={t(`dashboard.${tile.key}`)} value={c[tile.key]} valueStyle={tile.tone === 'warn' && c[tile.key] > 0 ? { color: '#b42318' } : undefined} />
                  </Card>
                </Link>
              </Col>
            ))}
          </Row>
          <Row gutter={[16, 16]} style={{ marginTop: 16 }}>
            <Col xs={24} lg={16}>
              <Card title={t('dashboard.nextMeetings')}>
                <List
                  dataSource={q.data?.upcomingMeetings ?? []}
                  locale={{ emptyText: t('dashboard.noMeetings') }}
                  renderItem={(m) => (
                    <List.Item>
                      <List.Item.Meta
                        title={<Link to={`/meetings/${m.id}`}>{m.titleAr}</Link>}
                        description={`${m.bodyName} · ${m.referenceNo ?? ''}`}
                      />
                      <Typography.Text>{formatDateTime(m.startsAt, i18n.language, me?.user.calendar)}</Typography.Text>
                    </List.Item>
                  )}
                />
              </Card>
            </Col>
            <Col xs={24} lg={8}>
              <Card title={t('dashboard.completionRate')}>
                <div style={{ display: 'flex', justifyContent: 'center', padding: 12 }}>
                  <Progress type="dashboard" percent={percent(c.completedTasks, c.completedTasks + c.openTasks)} strokeColor="#006837" />
                </div>
                <Typography.Paragraph type="secondary" style={{ textAlign: 'center', marginBottom: 0 }}>
                  {t('dashboard.completedTasks')}: {c.completedTasks} / {c.completedTasks + c.openTasks}
                </Typography.Paragraph>
              </Card>
            </Col>
          </Row>
        </>
      )}
    </>
  );
}
