// التقارير ولوحة الإدارة العليا (الوثيقة 23 §1–2): أرقام فعلية من الخادم، وتعريف موثق لكل مؤشر، وتصدير PDF وExcel بالصلاحية.
import { Alert, Button, Card, Col, Collapse, DatePicker, Descriptions, Empty, Form, Progress, Row, Select, Space, Table, Tabs, Tag, Tooltip, Typography } from 'antd';
import { DownloadOutlined, FileExcelOutlined, FilePdfOutlined, InfoCircleOutlined, ReloadOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import dayjs, { type Dayjs } from 'dayjs';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { get, toQuery } from '../../api/client';
import { BodySelect, EntitySelect, OrgUnitSelect } from '../../components/pickers';
import { ErrorAlert, PageHeader } from '../../components/common';

export interface Filters { from: string; to: string; bodyId?: string; bodyKind?: string; orgUnitId?: string; entityId?: string; status?: string }

interface KpiDefinition { code: string; titleAr: string; titleEn: string; formulaAr: string; formulaEn: string; basis: 'period' | 'snapshot' | 'mixed' }
interface BodyRow {
  bodyId: string; code: string; name: string; bodyType: string; status: string; meetingsHeld: number; attendanceRate: number | null; resolutionsIssued: number;
  resolutionsOverdue: number; tasksCompleted: number; tasksOverdue: number; onTimeRate: number | null; avgApprovalDays: number | null; minutesPending: number;
}
interface ComplianceRow {
  assigneeType: string; assigneeId: string; name: string; assigned: number; completed: number; completedOnTime: number; openOverdue: number;
  onTimeRate: number | null; complianceIndex: number | null; avgDelayDays: number | null;
}
interface Executive {
  asOf: string; kpis: Record<string, number | null>; bodies: BodyRow[]; compliance: ComplianceRow[]; definitions: KpiDefinition[]; scopeBodies: number;
}

const GROUPS: { key: string; codes: string[] }[] = [
  { key: 'meetings', codes: ['bodies_active', 'meetings_planned', 'meetings_held', 'attendance_rate'] },
  { key: 'resolutions', codes: ['resolutions_issued', 'resolutions_executed', 'resolutions_overdue'] },
  { key: 'tasks', codes: ['tasks_open', 'tasks_completed', 'tasks_overdue', 'task_avg_days', 'task_on_time_rate'] },
  { key: 'minutes', codes: ['minutes_pending_review', 'minutes_pending_signatures', 'minutes_pending_approval', 'minutes_avg_approval_days'] },
];
const PCT = new Set(['attendance_rate', 'task_on_time_rate']);
const DAYS = new Set(['task_avg_days', 'minutes_avg_approval_days']);
const ALERT = new Set(['resolutions_overdue', 'tasks_overdue']);
const num = (v: number) => v.toLocaleString('en-US', { maximumFractionDigits: 1 });

export const defaultFilters = (): Filters => ({ from: dayjs().startOf('year').format('YYYY-MM-DD'), to: dayjs().format('YYYY-MM-DD') });

export function FilterBar({ value, onChange, showStatus }: { value: Filters; onChange: (f: Filters) => void; showStatus?: boolean }) {
  const { t } = useTranslation();
  const [form] = Form.useForm();
  const submit = (v: { period?: [Dayjs, Dayjs]; bodyId?: string; bodyKind?: string; orgUnitId?: string; entityId?: string; status?: string }) => onChange({
    from: (v.period?.[0] ?? dayjs(value.from)).format('YYYY-MM-DD'), to: (v.period?.[1] ?? dayjs(value.to)).format('YYYY-MM-DD'),
    bodyId: v.bodyId, bodyKind: v.bodyKind, orgUnitId: v.orgUnitId, entityId: v.entityId, status: v.status,
  });
  return (
    <Form form={form} layout="inline" style={{ rowGap: 8, marginBottom: 16 }} onFinish={submit}
      initialValues={{ period: [dayjs(value.from), dayjs(value.to)], bodyId: value.bodyId, bodyKind: value.bodyKind, orgUnitId: value.orgUnitId, entityId: value.entityId }}>
      <Form.Item name="period" label={t('rpt.filters.period')}><DatePicker.RangePicker allowClear={false} /></Form.Item>
      <Form.Item name="bodyKind"><Select allowClear placeholder={t('rpt.filters.kind')} style={{ minWidth: 150 }} options={['council', 'committee'].map((k) => ({ value: k, label: t(`rpt.kinds.${k}`) }))} /></Form.Item>
      <Form.Item name="bodyId"><BodySelect allowClear placeholder={t('rpt.filters.body')} /></Form.Item>
      <Form.Item name="orgUnitId"><OrgUnitSelect allowClear placeholder={t('rpt.filters.unit')} style={{ minWidth: 180 }} /></Form.Item>
      <Form.Item name="entityId"><EntitySelect allowClear placeholder={t('rpt.filters.entity')} style={{ minWidth: 180 }} /></Form.Item>
      {showStatus && <Form.Item name="status"><Select allowClear placeholder={t('rpt.filters.status')} style={{ minWidth: 130 }} options={['active', 'draft', 'suspended', 'dissolved'].map((s) => ({ value: s, label: t(`rpt.bodyStatus.${s}`) }))} /></Form.Item>}
      <Form.Item><Space>
        <Button type="primary" htmlType="submit">{t('rpt.filters.apply')}</Button>
        <Button icon={<ReloadOutlined />} onClick={() => { const d = defaultFilters(); form.setFieldsValue({ period: [dayjs(d.from), dayjs(d.to)], bodyId: undefined, bodyKind: undefined, orgUnitId: undefined, entityId: undefined, status: undefined }); onChange(d); }}>{t('rpt.filters.reset')}</Button>
      </Space></Form.Item>
    </Form>
  );
}

function KpiTile({ def, value, sub, lang }: { def: KpiDefinition; value: number | null | undefined; sub?: string; lang: string }) {
  const { t } = useTranslation();
  const isNull = value === null || value === undefined;
  const display = isNull ? '—' : PCT.has(def.code) ? `${num(value)}%` : num(value);
  const alert = ALERT.has(def.code) && !isNull && value > 0;
  return (
    <Card size="small" className="kpi-tile" styles={{ body: { padding: '12px 14px' } }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'start', gap: 8 }}>
        <Typography.Text type="secondary" style={{ fontSize: 13 }}>{lang === 'en' ? def.titleEn : def.titleAr}</Typography.Text>
        <Tooltip title={<><b>{t('rpt.formula')}:</b> {lang === 'en' ? def.formulaEn : def.formulaAr}</>}>
          <InfoCircleOutlined aria-label={t('rpt.formula')} style={{ color: '#8c8c8c' }} />
        </Tooltip>
      </div>
      <div className="kpi-value" style={{ fontSize: 28, fontWeight: 700, lineHeight: 1.25, color: alert ? '#a8071a' : '#1f1f1f', fontVariantNumeric: 'tabular-nums' }} title={isNull ? t('rpt.noValue') : undefined}>
        {display}{DAYS.has(def.code) && !isNull && <span style={{ fontSize: 14, fontWeight: 400, marginInlineStart: 4 }}>{t('rpt.days')}</span>}
      </div>
      <Space size={4} wrap>
        <Tag bordered={false} color={def.basis === 'snapshot' ? 'blue' : def.basis === 'period' ? 'green' : 'gold'} style={{ marginInlineEnd: 0 }}>{t(`rpt.basis.${def.basis}`)}</Tag>
        {sub && <Typography.Text type="secondary" style={{ fontSize: 12 }}>{sub}</Typography.Text>}
      </Space>
    </Card>
  );
}

/** نسبة بشريط وقيمة نصية (اللون ليس الحامل الوحيد للمعنى). */
function Rate({ value }: { value: number | null }) {
  if (value === null) return <Typography.Text type="secondary">—</Typography.Text>;
  const color = value >= 80 ? '#006837' : value >= 50 ? '#B28655' : '#a8071a';
  return <Space size={6} style={{ minWidth: 120 }}><Progress percent={value} size="small" showInfo={false} strokeColor={color} style={{ width: 70, margin: 0 }} /><span className="mono">{num(value)}%</span></Space>;
}

function ExecutiveDashboard() {
  const { t, i18n } = useTranslation();
  const lang = i18n.language;
  const [f, setF] = useState<Filters>(defaultFilters());
  const q = useQuery({ queryKey: ['rpt', 'executive', f], queryFn: () => get<Executive>('/api/v1/reports/executive', { ...f }) });
  const defs = useMemo(() => new Map((q.data?.definitions ?? []).map((d) => [d.code, d])), [q.data]);
  const k = q.data?.kpis ?? {};
  const sub = (code: string) => code === 'attendance_rate' && k.attendance_expected ? `${k.attendance_present} ${t('rpt.of')} ${k.attendance_expected}`
    : code === 'task_on_time_rate' && k.tasks_completed ? `${k.tasks_on_time} ${t('rpt.of')} ${k.tasks_completed}` : undefined;
  return (
    <>
      <FilterBar value={f} onChange={setF} showStatus />
      <ErrorAlert error={q.error} />
      {q.data && <Alert type="info" showIcon style={{ marginBottom: 16 }} message={t('rpt.live', { n: q.data.scopeBodies })} description={`${t('rpt.asOf')}: ${q.data.asOf}`} />}
      {GROUPS.map((g) => (
        <div key={g.key} style={{ marginBottom: 16 }}>
          <Typography.Title level={5} style={{ marginTop: 0 }}>{t(`rpt.groups.${g.key}`)}</Typography.Title>
          <Row gutter={[12, 12]}>
            {g.codes.map((c) => defs.get(c) && (
              <Col key={c} xs={12} md={8} xl={g.codes.length > 4 ? 4 : 6} style={g.codes.length > 4 ? { flex: '1 1 18%' } : undefined}>
                <KpiTile def={defs.get(c)!} value={k[c]} sub={sub(c)} lang={lang} />
              </Col>
            ))}
          </Row>
        </div>
      ))}
      <Card title={t('rpt.bodiesTitle')} style={{ marginBottom: 16 }} loading={q.isLoading}>
        <Table<BodyRow> rowKey="bodyId" dataSource={q.data?.bodies} size="small" pagination={{ pageSize: 10, hideOnSinglePage: true }} scroll={{ x: 1000 }}
          locale={{ emptyText: <Empty description={t('rpt.reports.empty')} /> }}
          columns={[
            { title: t('rpt.col.body'), render: (_, r) => <Link to={`/bodies/${r.bodyId}`}>{r.name} <span className="mono">({r.code})</span></Link> },
            { title: t('rpt.col.type'), dataIndex: 'bodyType', render: (v) => t(`enums.bodyType.${v}`, { defaultValue: v }) },
            { title: t('rpt.col.held'), dataIndex: 'meetingsHeld', align: 'center' },
            { title: t('rpt.col.attendance'), dataIndex: 'attendanceRate', render: (v) => <Rate value={v} /> },
            { title: t('rpt.col.issued'), dataIndex: 'resolutionsIssued', align: 'center' },
            { title: t('rpt.col.resOverdue'), dataIndex: 'resolutionsOverdue', align: 'center', render: (v: number) => v > 0 ? <Tag color="red">{v}</Tag> : v },
            { title: t('rpt.col.completed'), dataIndex: 'tasksCompleted', align: 'center' },
            { title: t('rpt.col.overdue'), dataIndex: 'tasksOverdue', align: 'center', render: (v: number) => v > 0 ? <Tag color="red">{v}</Tag> : v },
            { title: t('rpt.col.onTime'), dataIndex: 'onTimeRate', render: (v) => <Rate value={v} /> },
            { title: t('rpt.col.approvalDays'), dataIndex: 'avgApprovalDays', align: 'center', render: (v) => v === null ? '—' : num(v) },
            { title: t('rpt.col.pending'), dataIndex: 'minutesPending', align: 'center' },
          ]} />
      </Card>
      <Card title={t('rpt.complianceTitle')} style={{ marginBottom: 16 }} loading={q.isLoading}>
        <Table<ComplianceRow> rowKey={(r) => r.assigneeType + r.assigneeId} dataSource={q.data?.compliance} size="small" pagination={{ pageSize: 10, hideOnSinglePage: true }}
          locale={{ emptyText: <Empty description={t('rpt.reports.empty')} /> }}
          columns={[
            { title: t('rpt.col.assignee'), dataIndex: 'name' },
            { title: t('rpt.col.assigneeType'), dataIndex: 'assigneeType', render: (v) => <Tag>{t(`rpt.assigneeType.${v}`)}</Tag> },
            { title: t('rpt.col.assigned'), dataIndex: 'assigned', align: 'center' },
            { title: t('rpt.col.completed'), dataIndex: 'completed', align: 'center' },
            { title: t('rpt.col.completedOnTime'), dataIndex: 'completedOnTime', align: 'center' },
            { title: t('rpt.col.openOverdue'), dataIndex: 'openOverdue', align: 'center', render: (v: number) => v > 0 ? <Tag color="red">{v}</Tag> : v },
            { title: t('rpt.col.index'), dataIndex: 'complianceIndex', render: (v) => <Rate value={v} />, sorter: (a, b) => (a.complianceIndex ?? -1) - (b.complianceIndex ?? -1) },
            { title: t('rpt.col.avgDelay'), dataIndex: 'avgDelayDays', align: 'center', render: (v) => v === null ? '—' : `${num(v)} ${t('rpt.days')}` },
          ]} />
      </Card>
      <Collapse items={[{
        key: 'defs', label: t('rpt.definitions'), children: (
          <Descriptions column={1} size="small" bordered>
            {(q.data?.definitions ?? []).map((d) => (
              <Descriptions.Item key={d.code} label={<Space direction="vertical" size={0}><span>{lang === 'en' ? d.titleEn : d.titleAr}</span><Tag bordered={false}>{t(`rpt.basis.${d.basis}`)}</Tag></Space>}>
                {lang === 'en' ? d.formulaEn : d.formulaAr}
              </Descriptions.Item>
            ))}
          </Descriptions>
        ),
      }]} />
    </>
  );
}

interface ReportColumn { key: string; ar: string; en: string; kind: string }
interface ReportDef { code: string; titleAr: string; titleEn: string; descriptionAr: string; descriptionEn: string; permission: string; exportPermission: string; bodyScoped: boolean; columns: ReportColumn[] }
interface Catalog { items: { report: ReportDef; canExport: boolean }[]; maxRows: number; labels: Record<string, { ar: string; en: string }> }
interface RunResult { rowCount: number; truncated: boolean; rows: Record<string, unknown>[] }

function cellText(c: ReportColumn, v: unknown, lang: string, labels: Catalog['labels']): string {
  if (v === null || v === undefined || v === '') return '—';
  if (c.kind.startsWith('status:')) { const l = labels?.[`${c.kind.slice(7)}.${v}`]; return l ? (lang === 'en' ? l.en : l.ar) : String(v); }
  if (c.kind === 'datetime') return new Date(String(v)).toLocaleString(lang === 'en' ? 'en-GB' : 'ar-SA-u-nu-latn-ca-gregory', { timeZone: 'Asia/Riyadh', dateStyle: 'short', timeStyle: 'short' });
  if (c.kind === 'pct') return `${num(Number(v))}%`;
  if (c.kind === 'num') return num(Number(v));
  return String(v);
}

function ReportsTab() {
  const { t, i18n } = useTranslation();
  const lang = i18n.language;
  const [code, setCode] = useState<string>();
  const [f, setF] = useState<Filters>(defaultFilters());
  const cat = useQuery({ queryKey: ['rpt', 'catalog'], queryFn: () => get<Catalog>('/api/v1/reports/catalog') });
  const current = cat.data?.items.find((i) => i.report.code === code);
  const run = useQuery({ queryKey: ['rpt', 'run', code, f], enabled: !!code, queryFn: () => get<RunResult>(`/api/v1/reports/run/${code}`, { ...f }) });
  const href = (format: string) => `/api/v1/reports/run/${code}${toQuery({ ...f, format, lang })}`;
  return (
    <>
      <ErrorAlert error={cat.error} />
      <Row gutter={[12, 12]} style={{ marginBottom: 16 }}>
        {(cat.data?.items ?? []).map(({ report: r }) => (
          <Col key={r.code} xs={24} sm={12} xl={6}>
            <Card size="small" hoverable onClick={() => setCode(r.code)} className={code === r.code ? 'report-card selected' : 'report-card'}
              style={{ height: '100%', borderColor: code === r.code ? '#006837' : undefined, boxShadow: code === r.code ? '0 0 0 1px #006837' : undefined }}>
              <Typography.Text strong>{lang === 'en' ? r.titleEn : r.titleAr}</Typography.Text>
              <div className="item-meta" style={{ marginTop: 4 }}>{lang === 'en' ? r.descriptionEn : r.descriptionAr}</div>
            </Card>
          </Col>
        ))}
      </Row>
      {!current ? <Empty description={t('rpt.reports.choose')} /> : (
        <Card title={lang === 'en' ? current.report.titleEn : current.report.titleAr}
          extra={<Space wrap>
            <Tooltip title={current.canExport ? t('rpt.reports.audited') : t('rpt.reports.noExport')}>
              <Button icon={<FilePdfOutlined />} disabled={!current.canExport} href={current.canExport ? href('pdf') : undefined}>{t('rpt.reports.exportPdf')}</Button>
            </Tooltip>
            <Tooltip title={current.canExport ? t('rpt.reports.audited') : t('rpt.reports.noExport')}>
              <Button icon={<FileExcelOutlined />} type="primary" disabled={!current.canExport} href={current.canExport ? href('xlsx') : undefined}>{t('rpt.reports.exportXlsx')}</Button>
            </Tooltip>
          </Space>}>
          <FilterBar value={f} onChange={setF} />
          {current.report.code === 'overdue_tasks' && <Alert type="info" showIcon message={t('rpt.reports.snapshotNote')} style={{ marginBottom: 12 }} />}
          <ErrorAlert error={run.error} />
          {run.data?.truncated && <Alert type="warning" showIcon message={t('rpt.reports.truncated', { n: cat.data?.maxRows })} style={{ marginBottom: 12 }} />}
          <Typography.Paragraph type="secondary"><DownloadOutlined /> {t('rpt.reports.rows')}: <b>{run.data?.rowCount ?? '…'}</b> — {t('rpt.reports.audited')}</Typography.Paragraph>
          <Table rowKey={(_, i) => String(i)} dataSource={run.data?.rows} loading={run.isLoading} size="small" scroll={{ x: 'max-content' }}
            pagination={{ pageSize: 20, hideOnSinglePage: true }} locale={{ emptyText: <Empty description={t('rpt.reports.empty')} /> }}
            columns={current.report.columns.map((c) => ({
              title: lang === 'en' ? c.en : c.ar, dataIndex: c.key, key: c.key,
              align: ['int', 'num', 'pct'].includes(c.kind) ? 'center' as const : undefined,
              render: (v: unknown) => c.kind.startsWith('status:') ? <Tag>{cellText(c, v, lang, cat.data!.labels)}</Tag> : cellText(c, v, lang, cat.data!.labels),
            }))} />
        </Card>
      )}
    </>
  );
}

export function ReportsPage() {
  const { t } = useTranslation();
  return (
    <>
      <PageHeader title={t('rpt.title')} />
      <Tabs items={[
        { key: 'dashboard', label: t('rpt.tabs.dashboard'), children: <ExecutiveDashboard /> },
        { key: 'reports', label: t('rpt.tabs.reports'), children: <ReportsTab /> },
      ]} />
    </>
  );
}
