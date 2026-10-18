import { Alert, App, Badge, Button, Card, Checkbox, Descriptions, Input, List, Modal, Select, Space, Switch, Table, Tabs, Tag, Tooltip, Typography } from 'antd';
import { ApiOutlined, LockOutlined, ReloadOutlined, SendOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError, get, post, put, type Paged } from '../api/client';
import type { Connector, FeatureFlag, FlagHistory, ImpactItem } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorAlert, PageHeader, StatusTag, errorText } from '../components/common';
import { EnumSelect } from '../components/pickers';
import { formatDateTime } from '../lib/format';

const CATEGORIES: FeatureFlag['category'][] = ['ai', 'esignature', 'integration', 'platform'];

/** System Configuration & Feature Management — كل قرار تفعيل أو منع يُتخذ في الخادم؛ هذه الصفحة تعرض الحالة وتطلب التغيير فقط. */
export default function SettingsPage() {
  const { t } = useTranslation();
  const { can, reload } = useAuth();
  const canManage = can('admin.settings.manage');
  const [change, setChange] = useState<{ flag: FeatureFlag; value: string } | null>(null);
  const flags = useQuery({ queryKey: ['admin-features'], queryFn: () => get<FeatureFlag[]>('/api/v1/admin/features') });

  return (
    <>
      <PageHeader title={t('settings.title')} subtitle={t('settings.subtitle')} />
      {!canManage && <Alert type="info" showIcon message={t('settings.readOnly')} style={{ marginBottom: 16 }} />}
      <Tabs items={[
        {
          key: 'features', label: t('settings.features'),
          children: (
            <>
              <ErrorAlert error={flags.error} />
              {CATEGORIES.map((cat) => (
                <FlagCard key={cat} category={cat} flags={(flags.data ?? []).filter((f) => f.category === cat)} loading={flags.isLoading}
                  canManage={canManage} onChange={(flag, value) => setChange({ flag, value })} />
              ))}
            </>
          ),
        },
        { key: 'history', label: t('settings.history'), children: <HistoryTab flags={flags.data ?? []} /> },
        { key: 'connectors', label: t('settings.connectors'), children: <ConnectorsTab canManage={canManage} /> },
        { key: 'outbox', label: t('settings.outbox'), children: <OutboxTab canManage={canManage} /> },
      ]} />
      {change && <ChangeModal flag={change.flag} value={change.value} onClose={(saved) => { setChange(null); if (saved) void reload(); }} />}
    </>
  );
}

function FlagCard({ category, flags, loading, canManage, onChange }: {
  category: FeatureFlag['category']; flags: FeatureFlag[]; loading: boolean; canManage: boolean; onChange: (f: FeatureFlag, v: string) => void;
}) {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const check = useMutation({
    mutationFn: (code: string) => post<{ status: string; detail: string }>(`/api/v1/admin/connectors/${code}/check`),
    onSuccess: (r) => { message.info(`${t(`enums.connector.${r.status}`)} — ${r.detail}`); void qc.invalidateQueries({ queryKey: ['admin-features'] }); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const parentConnector = flags.find((f) => !f.parentKey && f.connector)?.connector;
  return (
    <Card
      title={t(`settings.category.${category}`)}
      style={{ marginBottom: 16 }}
      extra={parentConnector && (
        <Space>
          <Typography.Text type="secondary">{t('settings.connection')}:</Typography.Text>
          <StatusTag group="connector" value={parentConnector.status} />
          {canManage && <Button size="small" icon={<ApiOutlined />} loading={check.isPending} onClick={() => check.mutate(parentConnector.code)}>{t('settings.check')}</Button>}
        </Space>
      )}
    >
      <Table<FeatureFlag>
        rowKey="key" dataSource={flags} loading={loading} pagination={false} size="middle"
        rowClassName={(f) => (f.blockedBy ? 'flag-row-blocked' : '')}
        columns={[
          {
            title: t('common.name'),
            render: (_, f) => (
              <div style={{ paddingInlineStart: f.parentKey ? 20 : 0 }}>
                <Typography.Text strong>{i18n.language === 'en' ? f.nameEn : f.nameAr}</Typography.Text>{' '}
                <span className="mono" style={{ fontSize: 12, color: '#5f6b64' }}>{f.key}</span>
                <div style={{ color: '#5f6b64', fontSize: 12 }}>{i18n.language === 'en' ? f.descriptionEn : f.descriptionAr}</div>
              </div>
            ),
          },
          {
            title: t('common.status'), width: 200,
            render: (_, f) => {
              const disabled = !canManage || f.locked;
              const control = f.valueType === 'boolean'
                ? <Switch checked={f.value === 'true'} disabled={disabled} onChange={(v) => onChange(f, String(v))} aria-label={f.key} />
                : f.valueType === 'enum'
                  ? <Select size="small" value={f.value} disabled={disabled} style={{ minWidth: 200 }} onChange={(v) => onChange(f, v)}
                      options={(f.allowedValues ?? []).map((v) => ({ value: v, label: t(`settings.enumValuesByKey.${f.key}.${v}`, { defaultValue: t(`settings.enumValues.${v}`, { defaultValue: v }) }) }))} />
                  : <ValueEditor flag={f} disabled={disabled} onSubmit={(v) => onChange(f, v)} />;
              return f.locked ? <Tooltip title={t('settings.locked')}><Space>{control}<LockOutlined /></Space></Tooltip> : control;
            },
          },
          {
            title: t('settings.setting'), width: 230,
            render: (_, f) => (
              <Space direction="vertical" size={2}>
                <Badge status={f.effectiveEnabled ? 'success' : 'default'} text={f.effectiveEnabled ? t('settings.effective') : t('settings.inactive')} />
                {f.blockedBy && <Tag color="orange">{t('settings.blockedBy')}: <span className="mono">{f.blockedBy}</span></Tag>}
                {f.locked && <Tag icon={<LockOutlined />} color="purple">{t('settings.locked')}</Tag>}
              </Space>
            ),
          },
          {
            title: t('settings.connection'), width: 220,
            render: (_, f) => f.connector ? (
              <Tooltip title={f.connector.detail}>
                <Space direction="vertical" size={0}>
                  <StatusTag group="connector" value={f.connector.status} />
                  <Typography.Text type="secondary" style={{ fontSize: 11 }}>{t('settings.lastCheck')}: {formatDateTime(f.connector.checkedAt, i18n.language)}</Typography.Text>
                </Space>
              </Tooltip>
            ) : '—',
          },
        ]}
      />
    </Card>
  );
}

/** قيمة عددية أو نصية: تُحرر ثم تُرسل لنافذة التأكيد بالسبب؛ الحدود يتحقق منها الخادم أيضًا. */
function ValueEditor({ flag, disabled, onSubmit }: { flag: FeatureFlag; disabled: boolean; onSubmit: (v: string) => void }) {
  const { t } = useTranslation();
  const [v, setV] = useState(flag.value);
  useEffect(() => setV(flag.value), [flag.value]);
  const changed = v.trim() !== flag.value;
  return (
    <Space.Compact size="small" style={{ minWidth: 200 }}>
      {flag.valueType === 'integer'
        ? <Input type="number" value={v} disabled={disabled} min={flag.minValue ?? undefined} max={flag.maxValue ?? undefined} onChange={(e) => setV(e.target.value)}
            onPressEnter={() => changed && onSubmit(v.trim())} aria-label={flag.key} style={{ width: 130 }} />
        : <Input value={v} disabled={disabled} maxLength={flag.maxLength ?? undefined} placeholder={t('settings.emptyValue')} onChange={(e) => setV(e.target.value)}
            onPressEnter={() => changed && onSubmit(v.trim())} aria-label={flag.key} style={{ width: 200 }} className="mono" />}
      <Button disabled={disabled || !changed} onClick={() => onSubmit(v.trim())}>{t('common.save')}</Button>
    </Space.Compact>
  );
}

function ChangeModal({ flag, value, onClose }: { flag: FeatureFlag; value: string; onClose: (saved: boolean) => void }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [reason, setReason] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [impact, setImpact] = useState<ImpactItem[]>([]);
  const preview = useQuery({
    queryKey: ['impact', flag.key, value],
    queryFn: () => get<{ requiresConfirmation: boolean; items: ImpactItem[] }>(`/api/v1/admin/features/${flag.key}/impact`, { value }),
  });
  useEffect(() => { if (preview.data) setImpact(preview.data.items.filter((i) => i.count > 0)); }, [preview.data]);
  const save = useMutation({
    mutationFn: () => put(`/api/v1/admin/features/${flag.key}`, { value, reason, confirmImpact: confirmed }),
    onSuccess: () => {
      message.success(t('common.saved'));
      void qc.invalidateQueries({ queryKey: ['admin-features'] });
      void qc.invalidateQueries({ queryKey: ['flag-history'] });
      onClose(true);
    },
    onError: (e) => {
      if (e instanceof ApiError && e.code === 'confirmation-required') {
        setImpact(((e.problem.impact as ImpactItem[]) ?? []).filter((i) => i.count > 0));
        setConfirmed(false);
      }
      message.error(errorText(e, t('common.error')));
    },
  });
  const needsConfirm = impact.length > 0;
  return (
    <Modal open title={t('settings.changeTitle')} onCancel={() => onClose(false)} onOk={() => save.mutate()} confirmLoading={save.isPending}
      okButtonProps={{ disabled: reason.trim().length < 5 || (needsConfirm && !confirmed), danger: needsConfirm }} okText={t('common.confirm')} cancelText={t('common.cancel')}>
      <Descriptions column={1} size="small" bordered style={{ marginBottom: 12 }}>
        <Descriptions.Item label={t('common.name')}>{i18n.language === 'en' ? flag.nameEn : flag.nameAr} <span className="mono">({flag.key})</span></Descriptions.Item>
        <Descriptions.Item label={t('settings.from')}><span className="mono">{flag.value || '—'}</span></Descriptions.Item>
        <Descriptions.Item label={t('settings.to')}><span className="mono">{value || '—'}</span></Descriptions.Item>
      </Descriptions>
      {needsConfirm && (
        <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('settings.impactTitle')}
          description={<>
            <List size="small" dataSource={impact} renderItem={(i) => <List.Item>{i18n.language === 'en' ? i.descriptionEn : i.descriptionAr}: <strong>{i.count}</strong></List.Item>} />
            <Checkbox checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)}>{t('settings.impactConfirm')}</Checkbox>
          </>} />
      )}
      <Typography.Text>{t('settings.reasonRequired')}</Typography.Text>
      <Input.TextArea rows={3} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={1000} style={{ marginTop: 6 }} />
    </Modal>
  );
}

function HistoryTab({ flags }: { flags: FeatureFlag[] }) {
  const { t, i18n } = useTranslation();
  const [filters, setFilters] = useState<{ key?: string; result?: string; page: number }>({ page: 1 });
  const q = useQuery({ queryKey: ['flag-history', filters], queryFn: () => get<Paged<FlagHistory>>('/api/v1/admin/features/history', { ...filters, pageSize: 20 }) });
  return (
    <Card>
      <div className="toolbar">
        <Select allowClear placeholder={t('common.name')} style={{ minWidth: 280 }} value={filters.key} onChange={(v) => setFilters((f) => ({ ...f, key: v, page: 1 }))}
          options={flags.map((f) => ({ value: f.key, label: f.key }))} showSearch />
        <EnumSelect group="enums.result" values={['succeeded', 'rejected_validation', 'confirmation_required', 'denied', 'failed']} allowClear
          placeholder={t('settings.result')} value={filters.result} onChange={(v) => setFilters((f) => ({ ...f, result: v, page: 1 }))} />
      </div>
      <ErrorAlert error={q.error} />
      <Table<FlagHistory>
        rowKey="id" dataSource={q.data?.items} loading={q.isLoading} size="middle"
        pagination={{ current: filters.page, total: q.data?.total, pageSize: 20, onChange: (page) => setFilters((f) => ({ ...f, page })) }}
        expandable={{ expandedRowRender: (h) => <pre className="mono" style={{ margin: 0, whiteSpace: 'pre-wrap' }}>{h.resultDetail ?? ''}{h.impact ? `\n${h.impact}` : ''}</pre>, rowExpandable: (h) => !!(h.resultDetail || h.impact) }}
        columns={[
          { title: t('settings.when'), dataIndex: 'changedAt', render: (v) => formatDateTime(v, i18n.language) },
          { title: t('common.name'), dataIndex: 'key', render: (v) => <span className="mono">{v}</span> },
          { title: t('settings.oldValue'), dataIndex: 'oldValue', render: (v) => <span className="mono">{v ?? '—'}</span> },
          { title: t('settings.newValue'), dataIndex: 'newValue', render: (v) => <span className="mono">{v ?? '—'}</span> },
          { title: t('settings.actor'), dataIndex: 'actor' },
          { title: t('common.reason'), dataIndex: 'reason' },
          { title: t('settings.result'), dataIndex: 'result', render: (v) => <StatusTag group="result" value={v} /> },
        ]}
      />
    </Card>
  );
}

function ConnectorsTab({ canManage }: { canManage: boolean }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['connectors'], queryFn: () => get<Connector[]>('/api/v1/admin/connectors') });
  const check = useMutation({
    mutationFn: (code: string) => post(`/api/v1/admin/connectors/${code}/check`),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ['connectors'] }); void qc.invalidateQueries({ queryKey: ['admin-features'] }); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Card>
      <ErrorAlert error={q.error} />
      <Table<Connector>
        rowKey="code" dataSource={q.data} loading={q.isLoading} pagination={false}
        columns={[
          { title: t('common.name'), render: (_, c) => <>{i18n.language === 'en' ? c.nameEn : c.nameAr} <span className="mono">({c.code})</span></> },
          { title: t('settings.connection'), dataIndex: 'status', render: (v) => <StatusTag group="connector" value={v} /> },
          { title: t('common.details'), dataIndex: 'detail' },
          { title: 'ms', dataIndex: 'latencyMs', width: 70, render: (v) => v ?? '—' },
          { title: t('settings.lastCheck'), dataIndex: 'checkedAt', render: (v) => formatDateTime(v, i18n.language) },
          ...(canManage ? [{ title: t('common.actions'), render: (_: unknown, c: Connector) => <Button size="small" icon={<ReloadOutlined />} loading={check.isPending && check.variables === c.code} onClick={() => check.mutate(c.code)}>{t('settings.check')}</Button> }] : []),
        ]}
      />
    </Card>
  );
}

interface OutboxRow { id: string; eventType: string; channel: string; recipient: string; subject: string; status: string; attempts: number; lastError?: string | null; createdAt: string }

function OutboxTab({ canManage }: { canManage: boolean }) {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const [page, setPage] = useState(1);
  const q = useQuery({ queryKey: ['outbox', page], queryFn: () => get<Paged<OutboxRow>>('/api/v1/admin/outbox', { page, pageSize: 20 }) });
  const dispatch = useMutation({
    mutationFn: () => post<{ sent: number; logged: number; failed: number; deferred: number }>('/api/v1/admin/outbox/dispatch'),
    onSuccess: (r) => { message.info(JSON.stringify(r)); void qc.invalidateQueries({ queryKey: ['outbox'] }); },
  });
  return (
    <Card extra={canManage && <Button icon={<SendOutlined />} loading={dispatch.isPending} onClick={() => dispatch.mutate()}>{t('settings.dispatch')}</Button>}>
      <ErrorAlert error={q.error} />
      <Table<OutboxRow>
        rowKey="id" dataSource={q.data?.items} loading={q.isLoading} size="middle"
        pagination={{ current: page, total: q.data?.total, pageSize: 20, onChange: setPage }}
        columns={[
          { title: t('settings.when'), dataIndex: 'createdAt', render: (v) => formatDateTime(v, i18n.language) },
          { title: t('settings.recipient'), dataIndex: 'recipient', render: (v) => <span className="mono">{v}</span> },
          { title: t('common.title'), dataIndex: 'subject' },
          { title: t('settings.attempts'), dataIndex: 'attempts', width: 90 },
          { title: t('common.status'), dataIndex: 'status', render: (v) => <StatusTag group="outbox" value={v} /> },
        ]}
      />
    </Card>
  );
}
