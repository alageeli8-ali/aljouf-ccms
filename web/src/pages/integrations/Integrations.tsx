// شاشة التكاملات المؤسسية (الوثيقة 23 §4): كل موصل بمفتاحه، ومتطلباته المضبوطة والناقصة، وحالة اتصاله الفعلية، وسجل استدعاءاته، وقاطع الدائرة.
import { Alert, App, Badge, Button, Card, Col, Descriptions, Drawer, Empty, List, Row, Space, Table, Tag, Tooltip, Typography } from 'antd';
import { ApiOutlined, CheckCircleFilled, CloseCircleFilled, ExclamationCircleFilled, HistoryOutlined, KeyOutlined, ReloadOutlined, SyncOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { get, post } from '../../api/client';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, PageHeader, errorText } from '../../components/common';
import { formatDateTime } from '../../lib/format';

interface Requirement { key: string; descriptionAr: string; descriptionEn: string; present: boolean; secret: boolean }
interface Connector {
  code: string; nameAr: string; nameEn: string; featureKey: string | null; enabled: boolean | null; authType: string; requirements: Requirement[]; missing: string[];
  requiredPermissions: string[]; notesAr: string | null; notesEn: string | null; verified: boolean;
  connection: { status: string; detail: string | null; latencyMs: number | null; checkedAt: string } | null;
  calls24h: { succeeded: number; failed: number; retries: number; skipped: number; last: string; lastError: string | null } | null;
  circuit: { state: string; failures: number; openUntil: string | null };
}
interface Call { at: string; operation: string; status: string; attempt: number; durationMs: number | null; httpStatus: number | null; error: string | null; actor: string | null }

function ConnectorCard({ c, onCalls }: { c: Connector; onCalls: (code: string) => void }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const { me, can } = useAuth();
  const qc = useQueryClient();
  const en = i18n.language === 'en';
  const test = useMutation({
    mutationFn: () => post<{ status: string; detail: string }>(`/api/v1/admin/integrations/${c.code}/test`),
    onSuccess: (r) => { (r.status === 'healthy' ? message.success : message.warning)(`${t(`enums.connector.${r.status}`, { defaultValue: r.status })} — ${r.detail}`); void qc.invalidateQueries({ queryKey: ['integrations'] }); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const reset = useMutation({ mutationFn: () => post(`/api/v1/admin/integrations/${c.code}/circuit/reset`), onSuccess: () => void qc.invalidateQueries({ queryKey: ['integrations'] }) });
  const statusIcon = c.verified ? <CheckCircleFilled style={{ color: '#006837' }} /> : c.connection?.status === 'not_configured' || !c.connection
    ? <ExclamationCircleFilled style={{ color: '#ad8b00' }} /> : <CloseCircleFilled style={{ color: '#a8071a' }} />;
  return (
    <Card size="small" style={{ height: '100%' }} title={<Space><ApiOutlined />{en ? c.nameEn : c.nameAr}</Space>}
      extra={c.featureKey ? <Link to="/settings"><Tag color={c.enabled ? 'green' : 'default'}>{c.enabled ? t('integ.enabled') : t('integ.disabled')}</Tag></Link> : <Tag>{t('integ.noFlag')}</Tag>}
      actions={[
        ...(can('admin.settings.manage') ? [<Button key="t" type="link" size="small" icon={<SyncOutlined />} loading={test.isPending} onClick={() => test.mutate()}>{t('integ.test')}</Button>] : []),
        <Button key="c" type="link" size="small" icon={<HistoryOutlined />} onClick={() => onCalls(c.code)}>{t('integ.calls')}</Button>,
        ...(c.circuit.state !== 'closed' && can('admin.settings.manage') ? [<Button key="r" type="link" size="small" danger icon={<ReloadOutlined />} onClick={() => reset.mutate()}>{t('integ.reset')}</Button>] : []),
      ]}>
      <Space direction="vertical" style={{ width: '100%' }} size={8}>
        <Space wrap>
          {statusIcon}
          <Typography.Text strong>{c.verified ? t('integ.verified') : c.connection ? t(`enums.connector.${c.connection.status}`, { defaultValue: c.connection.status }) : t('integ.notVerified')}</Typography.Text>
          {c.featureKey && <Tag bordered={false} className="mono">{c.featureKey}</Tag>}
        </Space>
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {t('integ.lastCheck')}: {c.connection ? `${formatDateTime(c.connection.checkedAt, i18n.language, me?.user.calendar)}${c.connection.latencyMs !== null ? ` · ${c.connection.latencyMs} ms` : ''}` : t('integ.never')}
          {c.connection?.detail && <><br />{c.connection.detail}</>}
        </Typography.Text>
        {c.missing.length > 0 && <Alert type="warning" showIcon message={t('integ.missing')} description={<ul style={{ margin: 0, paddingInlineStart: 18 }}>{c.requirements.filter((r) => !r.present).map((r) => <li key={r.key}><span className="mono">{r.key}</span> — {en ? r.descriptionEn : r.descriptionAr}</li>)}</ul>} />}
        <Descriptions size="small" column={1} colon={false} labelStyle={{ width: 130, color: '#8c8c8c' }}>
          <Descriptions.Item label={t('integ.auth')}>{t(`integ.authType.${c.authType}`, { defaultValue: c.authType })}</Descriptions.Item>
          <Descriptions.Item label={t('integ.requirements')}>
            <Space size={[4, 4]} wrap>{c.requirements.map((r) => (
              <Tooltip key={r.key} title={en ? r.descriptionEn : r.descriptionAr}>
                <Tag color={r.present ? 'green' : 'orange'} icon={r.secret ? <KeyOutlined /> : undefined}><span className="mono" style={{ fontSize: 11 }}>{r.key}</span></Tag>
              </Tooltip>))}
            </Space>
          </Descriptions.Item>
          {c.requiredPermissions.length > 0 && <Descriptions.Item label={t('integ.permissions')}><span className="mono" style={{ fontSize: 12 }}>{c.requiredPermissions.join(' | ')}</span></Descriptions.Item>}
          <Descriptions.Item label={t('integ.calls24h')}>
            {c.calls24h ? <Space size={4} wrap>
              <Badge color="#006837" text={`${t('integ.callStatus.succeeded')} ${c.calls24h.succeeded}`} />
              <Badge color="#a8071a" text={`${t('integ.callStatus.failed')} ${c.calls24h.failed}`} />
              <Badge color="#B28655" text={`${t('integ.callStatus.retrying')} ${c.calls24h.retries}`} />
              <Badge color="#bfbfbf" text={`${t('integ.callStatus.skipped_disabled')} ${c.calls24h.skipped}`} />
            </Space> : '—'}
          </Descriptions.Item>
          <Descriptions.Item label={t('integ.circuitLabel')}><Tag color={c.circuit.state === 'closed' ? 'green' : c.circuit.state === 'open' ? 'red' : 'gold'}>{t(`integ.circuit.${c.circuit.state}`)}</Tag></Descriptions.Item>
          {c.calls24h?.lastError && <Descriptions.Item label={t('integ.lastError')}><Typography.Text type="danger" style={{ fontSize: 12 }}>{c.calls24h.lastError}</Typography.Text></Descriptions.Item>}
        </Descriptions>
        {(en ? c.notesEn : c.notesAr) && <Typography.Paragraph type="secondary" style={{ fontSize: 12, marginBottom: 0 }}>{en ? c.notesEn : c.notesAr}</Typography.Paragraph>}
      </Space>
    </Card>
  );
}

function CallsDrawer({ code, onClose }: { code: string | null; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const q = useQuery({ queryKey: ['integrations', 'calls', code], enabled: !!code, queryFn: () => get<Call[]>(`/api/v1/admin/integrations/${code}/calls`) });
  const color: Record<string, string> = { succeeded: 'green', retrying: 'gold', failed: 'red', timeout: 'volcano', denied: 'magenta', circuit_open: 'purple', skipped_disabled: 'default', not_configured: 'orange' };
  return (
    <Drawer open={!!code} onClose={onClose} width={860} title={`${t('integ.calls')} — ${code ?? ''}`}>
      <ErrorAlert error={q.error} />
      <Table<Call> rowKey={(r, i) => r.at + i} size="small" loading={q.isLoading} dataSource={q.data} pagination={{ pageSize: 25, hideOnSinglePage: true }}
        locale={{ emptyText: <Empty /> }}
        columns={[
          { title: t('integ.col.at'), dataIndex: 'at', width: 150, render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
          { title: t('integ.col.operation'), dataIndex: 'operation', render: (v) => <span className="mono">{v}</span> },
          { title: t('integ.col.status'), dataIndex: 'status', render: (v) => <Tag color={color[v]}>{t(`integ.callStatus.${v}`, { defaultValue: v })}</Tag> },
          { title: t('integ.col.attempt'), dataIndex: 'attempt', align: 'center', width: 70 },
          { title: t('integ.col.duration'), dataIndex: 'durationMs', align: 'center', width: 90, render: (v) => v ?? '—' },
          { title: t('integ.col.http'), dataIndex: 'httpStatus', align: 'center', width: 60, render: (v) => v ?? '—' },
          { title: t('integ.col.error'), dataIndex: 'error', render: (v) => v ? <Typography.Text type="danger" style={{ fontSize: 12 }}>{v}</Typography.Text> : '—' },
          { title: t('integ.col.actor'), dataIndex: 'actor', render: (v) => v ?? '—' },
        ]} />
    </Drawer>
  );
}

interface SyncChange { userId: string; email: string; kind: string; displayNameBefore: string | null; displayNameAfter: string | null; jobTitleBefore: string | null; jobTitleAfter: string | null }

function DirectorySyncCard() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [preview, setPreview] = useState<{ directoryUsers: number; changes: SyncChange[]; notProvisioned: string[]; notProvisionedCount: number } | null>(null);
  const run = useMutation({ mutationFn: () => get<typeof preview>('/api/v1/admin/directory-sync/preview'), onSuccess: setPreview, onError: (e) => message.error(errorText(e, t('common.error'))) });
  const apply = useMutation({
    mutationFn: () => post<{ applied: number }>('/api/v1/admin/directory-sync/apply'),
    onSuccess: (r) => { message.success(t('integ.dirSync.applied', { n: r.applied })); setPreview(null); }, onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Card title={t('integ.dirSync.title')} style={{ marginTop: 16 }} extra={<Space>
      <Button icon={<SyncOutlined />} loading={run.isPending} onClick={() => run.mutate()}>{t('integ.dirSync.preview')}</Button>
      <Button type="primary" disabled={!preview?.changes.length} loading={apply.isPending} onClick={() => apply.mutate()}>{t('integ.dirSync.apply')}</Button>
    </Space>}>
      <Typography.Paragraph type="secondary">{t('integ.dirSync.help')}</Typography.Paragraph>
      {preview && <>
        <Typography.Paragraph>{t('integ.dirSync.users')}: <b>{preview.directoryUsers}</b></Typography.Paragraph>
        <Table<SyncChange> rowKey="userId" size="small" dataSource={preview.changes} pagination={false} locale={{ emptyText: t('integ.dirSync.none') }}
          columns={[
            { title: 'Email', dataIndex: 'email', render: (v) => <span className="mono" dir="ltr">{v}</span> },
            { title: '', dataIndex: 'kind', render: (v) => <Tag color={v === 'disable' ? 'red' : v === 'link' ? 'blue' : 'green'}>{t(`integ.dirSync.kind.${v}`)}</Tag> },
            { title: '←', render: (_, r) => r.kind === 'disable' ? '—' : <>{r.displayNameBefore} → <b>{r.displayNameAfter}</b>{r.jobTitleAfter !== r.jobTitleBefore && <div className="item-meta">{r.jobTitleBefore ?? '—'} → {r.jobTitleAfter ?? '—'}</div>}</> },
          ]} />
        {preview.notProvisionedCount > 0 && <List size="small" header={`${t('integ.dirSync.notProvisioned')} (${preview.notProvisionedCount})`} dataSource={preview.notProvisioned.slice(0, 10)} renderItem={(e) => <List.Item className="mono">{e}</List.Item>} />}
      </>}
    </Card>
  );
}

export function IntegrationsPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const [calls, setCalls] = useState<string | null>(null);
  const q = useQuery({ queryKey: ['integrations'], queryFn: () => get<Connector[]>('/api/v1/admin/integrations') });
  return (
    <>
      <PageHeader title={t('integ.title')} subtitle={t('integ.subtitle')} />
      <ErrorAlert error={q.error} />
      <Row gutter={[16, 16]}>
        {(q.data ?? []).map((c) => <Col key={c.code} xs={24} lg={12} xxl={8}><ConnectorCard c={c} onCalls={setCalls} /></Col>)}
      </Row>
      {can('admin.users.manage') && <DirectorySyncCard />}
      <CallsDrawer code={calls} onClose={() => setCalls(null)} />
    </>
  );
}
