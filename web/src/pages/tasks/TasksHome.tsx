// قوائم المهام ولوحاتها: جدول بمرشحات، ولوحة كانبان حسب الحالة، ولوحة مؤشرات محسوبة من البيانات الفعلية.
import { Button, Card, Col, DatePicker, Empty, Input, Progress, Row, Segmented, Select, Skeleton, Space, Switch, Table, Tag, Tooltip, Typography } from 'antd';
import { AppstoreOutlined, BarChartOutlined, PlusOutlined, SettingOutlined, UnorderedListOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import type { Dayjs } from 'dayjs';
import { get, type Paged } from '../../api/client';
import type { BoardColumn, TaskDashboard, TaskGroupRow, TaskItem } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, PageHeader } from '../../components/common';
import { BodySelect, EntitySelect, EnumSelect, OrgUnitSelect } from '../../components/pickers';
import { dualDate } from '../../lib/format';
import { ASSIGNEE_TYPES, CreateTaskModal, DueCell, PRIORITIES, PriorityTag, TaskStatusTag, useWorkflow } from './shared';

interface Filters {
  q?: string; bodyId?: string; status?: string; priority?: string; assigneeType?: string; orgUnitId?: string; externalEntityId?: string;
  dueFrom?: string; dueTo?: string; mine?: boolean; escalated?: boolean; sort?: string; page: number;
}

export default function TasksHome() {
  const { t } = useTranslation();
  const { me, can } = useAuth();
  const nav = useNavigate();
  const [params, setParams] = useSearchParams();
  const view = params.get('view') ?? 'list';
  const [filters, setFilters] = useState<Filters>({ page: 1, status: params.get('overdue') === 'true' ? 'overdue' : params.get('status') ?? undefined, mine: params.get('mine') === 'true' || undefined });
  const [creating, setCreating] = useState(false);
  const canCreate = (me?.bodyRoles ?? []).some((r) => ['chair', 'secretary'].includes(r.role));
  const set = (patch: Partial<Filters>) => setFilters((f) => ({ ...f, ...patch, page: 1 }));
  const wf = useWorkflow();
  const statusOptions = [...(wf.data?.statuses ?? []).filter((s) => s.active).map((s) => s.code)];

  return (
    <>
      <PageHeader title={t('tasks.title')} subtitle={t('tasks4.subtitle')}
        extra={<>
          <Segmented value={view} onChange={(v) => setParams({ view: String(v) }, { replace: true })} options={[
            { value: 'list', icon: <UnorderedListOutlined />, label: t('tasks4.views.list') },
            { value: 'board', icon: <AppstoreOutlined />, label: t('tasks4.views.board') },
            { value: 'dashboard', icon: <BarChartOutlined />, label: t('tasks4.views.dashboard') },
          ]} />
          {can('task.workflow.manage') && <Button icon={<SettingOutlined />} onClick={() => nav('/tasks/workflow')}>{t('tasks4.workflow.title')}</Button>}
          {canCreate && <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreating(true)}>{t('tasks.new')}</Button>}
        </>} />
      <Card style={{ marginBottom: 12 }} size="small">
        <div className="toolbar" style={{ marginBottom: 0 }}>
          <Input.Search placeholder={t('common.search')} allowClear style={{ width: 200 }} onSearch={(v) => set({ q: v || undefined })} />
          <BodySelect placeholder={t('common.body')} allowClear value={filters.bodyId} onChange={(v) => set({ bodyId: v })} />
          {view === 'list' && (
            <Select placeholder={t('common.status')} allowClear value={filters.status} onChange={(v) => set({ status: v })} style={{ minWidth: 160 }}
              options={statusOptions.map((s) => ({ value: s, label: <TaskStatusTag status={s} /> }))} />
          )}
          <EnumSelect group="enums.priority" values={PRIORITIES} placeholder={t('tasks.priority')} allowClear value={filters.priority} onChange={(v) => set({ priority: v })} />
          <EnumSelect group="enums.assigneeType" values={ASSIGNEE_TYPES} placeholder={t('tasks.assigneeType')} allowClear value={filters.assigneeType} onChange={(v) => set({ assigneeType: v })} />
          <OrgUnitSelect placeholder={t('tasks4.executingUnit')} allowClear value={filters.orgUnitId} onChange={(v) => set({ orgUnitId: v })} />
          <EntitySelect placeholder={t('tasks.entity')} allowClear value={filters.externalEntityId} onChange={(v: string) => set({ externalEntityId: v })} />
          {view !== 'board' && (
            <DatePicker.RangePicker placeholder={[t('tasks4.dueFrom'), t('tasks4.dueTo')]}
              onChange={(r) => set({ dueFrom: (r?.[0] as Dayjs | null)?.format('YYYY-MM-DD'), dueTo: (r?.[1] as Dayjs | null)?.format('YYYY-MM-DD') })} />
          )}
          <Space><Switch size="small" checked={!!filters.mine} onChange={(v) => set({ mine: v || undefined })} />{t('tasks4.mine')}</Space>
        </div>
      </Card>
      {view === 'list' && <TaskTable filters={filters} setPage={(page) => setFilters((f) => ({ ...f, page }))} setSort={(sort) => set({ sort })} />}
      {view === 'board' && <TaskBoard filters={filters} />}
      {view === 'dashboard' && <TaskDashboardView filters={filters} onDrill={(f) => { setFilters((x) => ({ ...x, ...f, page: 1 })); setParams({ view: 'list' }, { replace: true }); }} />}
      {creating && <CreateTaskModal onClose={() => setCreating(false)} />}
    </>
  );
}

function TaskTable({ filters, setPage, setSort }: { filters: Filters; setPage: (p: number) => void; setSort: (s?: string) => void }) {
  const { t } = useTranslation();
  const nav = useNavigate();
  const q = useQuery({ queryKey: ['tasks', filters], queryFn: () => get<Paged<TaskItem>>('/api/v1/tasks', { ...filters, pageSize: 20 }) });
  return (
    <Card>
      <ErrorAlert error={q.error} />
      <Table<TaskItem>
        rowKey="id" loading={q.isLoading} dataSource={q.data?.items}
        onRow={(r) => ({ onClick: () => nav(`/tasks/${r.id}`), style: { cursor: 'pointer' } })}
        onChange={(_, __, sorter) => {
          const s = Array.isArray(sorter) ? sorter[0] : sorter;
          setSort(s?.order ? String(s.columnKey) : undefined);
        }}
        pagination={{ current: filters.page, total: q.data?.total, pageSize: 20, onChange: setPage, showTotal: (n) => t('tasks4.totalN', { n }) }}
        columns={[
          { title: t('tasks.reference'), dataIndex: 'referenceNo', width: 150, render: (v) => <span className="mono">{v ?? t('tasks4.notNumbered')}</span> },
          {
            title: t('common.title'), dataIndex: 'titleAr', render: (v, r) => (
              <Space direction="vertical" size={0}>
                <span>{v}</span>
                <span className="item-meta">{r.resolutionNumber ? `${t(`enums.resolutionType.${r.resolutionType}`, { defaultValue: '' })} ${r.resolutionNumber}` : t(`tasks4.source.${r.sourceType}`, { defaultValue: '' })} · {r.bodyName}</span>
              </Space>
            ),
          },
          {
            title: t('tasks4.executor'), render: (_, r) => (
              <Space direction="vertical" size={0}>
                <Space size={4}><Tag>{t(`enums.assigneeType.${r.assigneeType}`)}</Tag>{r.assigneeName}</Space>
                {r.executorName && r.executorName !== r.assigneeName && <span className="item-meta">{r.executorName}</span>}
              </Space>
            ),
          },
          { title: t('tasks4.followUp'), dataIndex: 'accountableName' },
          { title: t('tasks.priority'), dataIndex: 'priority', key: 'priority', sorter: true, render: (v) => <PriorityTag value={v} /> },
          { title: t('common.dueDate'), key: 'due', sorter: true, render: (_, r) => <DueCell due={r.dueDate} original={r.originalDueDate} overdue={r.isOverdue} /> },
          { title: t('tasks.progress'), dataIndex: 'progressPct', key: 'progress', sorter: true, width: 120, render: (v: number) => <Progress percent={v} size="small" strokeColor="#006837" /> },
          {
            title: t('common.status'), render: (_, r) => (
              <Space size={4} wrap>
                <TaskStatusTag status={r.status} nameAr={r.statusNameAr} nameEn={r.statusNameEn} color={r.statusColor} overdue={r.isOverdue} days={r.daysOverdue} />
                {r.escalationLevel > 0 && r.statusCategory !== 'done' && <Tag color="volcano">{t('tasks4.escalationN', { n: r.escalationLevel })}</Tag>}
                {r.pendingExtension && <Tag color="gold">{t('tasks4.extensionPending')}</Tag>}
              </Space>
            ),
          },
        ]}
      />
    </Card>
  );
}

function TaskBoard({ filters }: { filters: Filters }) {
  const { t, i18n } = useTranslation();
  const q = useQuery({ queryKey: ['tasks-board', filters], queryFn: () => get<BoardColumn[]>('/api/v1/tasks/board', { ...filters }) });
  if (q.isLoading) return <Skeleton active />;
  return (
    <>
      <ErrorAlert error={q.error} />
      <div className="kanban">
        {(q.data ?? []).filter((c) => c.items.length > 0 || !['new', 'cancelled'].includes(c.status)).map((c) => (
          <div key={c.status} className={`kanban-col cat-${c.category}`}>
            <div className="kanban-head"><Tag color={c.color}>{i18n.language === 'en' ? c.nameEn : c.nameAr}</Tag><span className="kanban-count">{c.items.length}</span></div>
            {c.items.length === 0 && <Typography.Text type="secondary" className="item-meta">{t('tasks4.emptyColumn')}</Typography.Text>}
            {c.items.map((x) => (
              <Link key={x.id} to={`/tasks/${x.id}`} className={`kanban-card${x.isOverdue ? ' overdue' : ''}`}>
                <div className="kanban-card-head"><span className="mono">{x.referenceNo ?? '—'}</span><PriorityTag value={x.priority} /></div>
                <div className="kanban-title">{x.titleAr}</div>
                <div className="item-meta">{x.assigneeName}</div>
                <div className="kanban-card-foot">
                  <span className={x.isOverdue ? 'due-overdue' : 'item-meta'}>{dualDate(x.dueDate, i18n.language)}</span>
                  <Progress percent={x.progressPct} size="small" showInfo={false} strokeColor="#006837" style={{ width: 70, margin: 0 }} />
                </div>
              </Link>
            ))}
          </div>
        ))}
      </div>
    </>
  );
}

/** شريط أفقي واحد اللون لمقادير المجموعات، والرقم بلون النص؛ المتأخر بلون الحالة الحرجة مع وسم نصي. */
function GroupBars({ rows, label, onClick }: { rows: TaskGroupRow[]; label: (r: TaskGroupRow) => string; onClick?: (r: TaskGroupRow) => void }) {
  const { t } = useTranslation();
  const max = Math.max(1, ...rows.map((r) => r.total));
  if (rows.length === 0) return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} />;
  return (
    <div className="group-bars">
      {rows.map((r) => (
        <Tooltip key={(r.key ?? '') + label(r)} title={t('tasks4.dash.groupTip', { total: r.total, open: r.open, completed: r.completed, overdue: r.overdue, rate: r.completionRate, avg: r.avgProgress })}>
          <button type="button" className="group-bar-row" onClick={() => onClick?.(r)}>
            <span className="group-bar-label">{label(r)}</span>
            <span className="group-bar-track">
              <span className="group-bar-fill done" style={{ width: `${(r.completed / max) * 100}%` }} />
              <span className="group-bar-fill open" style={{ width: `${((r.open - r.overdue) / max) * 100}%` }} />
              <span className="group-bar-fill late" style={{ width: `${(r.overdue / max) * 100}%` }} />
            </span>
            <span className="group-bar-num">{r.completionRate}%</span>
            {r.overdue > 0 && <Tag color="red" style={{ margin: 0 }}>{t('tasks4.dash.lateN', { n: r.overdue })}</Tag>}
          </button>
        </Tooltip>
      ))}
      <div className="group-legend">
        <span><i className="sw done" />{t('tasks4.dash.completed')}</span><span><i className="sw open" />{t('tasks4.dash.openOnTime')}</span><span><i className="sw late" />{t('tasks4.dash.overdue')}</span>
      </div>
    </div>
  );
}

function TaskDashboardView({ filters, onDrill }: { filters: Filters; onDrill: (f: Partial<Filters>) => void }) {
  const { t, i18n } = useTranslation();
  const { mine: _mine, status: _status, page: _page, sort: _sort, q: _q, escalated: _e, ...f } = filters;
  const q = useQuery({ queryKey: ['tasks-dashboard', f], queryFn: () => get<TaskDashboard>('/api/v1/tasks/dashboard', f) });
  if (q.isLoading) return <Skeleton active />;
  if (q.error || !q.data) return <ErrorAlert error={q.error} />;
  const d = q.data;
  const k = d.kpis;
  const tiles: { key: string; value: string | number; label: string; tone?: string; drill?: Partial<Filters> }[] = [
    { key: 'open', value: k.open, label: t('tasks4.dash.open') },
    { key: 'overdue', value: k.overdue, label: t('tasks4.dash.overdue'), tone: k.overdue > 0 ? 'critical' : undefined, drill: { status: 'overdue' } },
    { key: 'due', value: k.dueSoon, label: t('tasks4.dash.dueSoon'), tone: k.dueSoon > 0 ? 'warning' : undefined },
    { key: 'review', value: k.pendingReview, label: t('tasks4.dash.pendingReview'), drill: { status: 'pending_review' } },
    { key: 'rate', value: `${k.completionRate}%`, label: t('tasks4.dash.completionRate') },
    { key: 'ontime', value: `${k.onTimeRate}%`, label: t('tasks4.dash.onTimeRate') },
    { key: 'esc', value: k.escalated, label: t('tasks4.dash.escalated'), tone: k.escalated > 0 ? 'warning' : undefined, drill: { escalated: true } },
    { key: 'ext', value: k.pendingExtensions, label: t('tasks4.dash.pendingExtensions') },
  ];
  return (
    <>
      <div className="kpi-row">
        {tiles.map((x) => (
          <button type="button" key={x.key} className={`kpi-tile${x.tone ? ` tone-${x.tone}` : ''}`} onClick={() => x.drill && onDrill(x.drill)} disabled={!x.drill}>
            <span className="kpi-value">{x.value}</span>
            <span className="kpi-label">{x.label}</span>
          </button>
        ))}
      </div>
      <Typography.Paragraph type="secondary" className="item-meta">{t('tasks4.dash.basis', { total: k.total, completed: k.completed, cancelled: k.cancelled, avg: k.avgProgress })}</Typography.Paragraph>
      <Row gutter={[16, 16]}>
        <Col xs={24} lg={12}>
          <Card title={t('tasks4.dash.byStatus')} size="small">
            <Table size="small" pagination={false} rowKey="status" dataSource={d.byStatus.filter((s) => s.count > 0)}
              columns={[
                { title: t('common.status'), render: (_, s) => <Tag color={s.color}>{i18n.language === 'en' ? s.nameEn : s.nameAr}</Tag> },
                { title: t('tasks4.dash.count'), dataIndex: 'count', width: 90 },
                { title: '', render: (_, s) => <Progress percent={Math.round((s.count / Math.max(1, k.total)) * 100)} size="small" strokeColor={s.status === 'overdue' ? '#b42318' : '#006837'} /> },
              ]}
              onRow={(s) => ({ onClick: () => onDrill({ status: s.status }), style: { cursor: 'pointer' } })} />
          </Card>
        </Col>
        <Col xs={24} lg={12}>
          <Card title={t('tasks4.dash.byDue')} size="small">
            <Table size="small" pagination={false} rowKey="bucket" dataSource={d.byDue}
              columns={[
                { title: t('tasks4.dash.bucket'), dataIndex: 'bucket', render: (v) => t(`tasks4.dash.buckets.${v}`) },
                { title: t('tasks4.dash.count'), dataIndex: 'count', width: 90, render: (v, r) => <span className={r.bucket === 'overdue' && v > 0 ? 'due-overdue' : undefined}>{v}</span> },
              ]} />
          </Card>
        </Col>
        <Col xs={24} lg={12}>
          <Card title={t('tasks4.dash.byExecutor')} size="small">
            <GroupBars rows={d.byExecutor} label={(r) => `${r.name ?? '—'} · ${t(`enums.assigneeType.${r.kind}`, { defaultValue: '' })}`}
              onClick={(r) => onDrill(r.kind === 'external_entity' ? { externalEntityId: r.key ?? undefined } : r.kind === 'org_unit' ? { orgUnitId: r.key ?? undefined } : { assigneeType: r.kind ?? undefined })} />
          </Card>
        </Col>
        <Col xs={24} lg={12}>
          <Card title={t('tasks4.dash.byBody')} size="small" style={{ marginBottom: 16 }}>
            <GroupBars rows={d.byBody} label={(r) => r.name ?? '—'} onClick={(r) => onDrill({ bodyId: r.key ?? undefined })} />
          </Card>
          <Card title={t('tasks4.dash.byPriority')} size="small">
            <GroupBars rows={d.byPriority} label={(r) => t(`enums.priority.${r.key}`)} onClick={(r) => onDrill({ priority: r.key ?? undefined })} />
          </Card>
        </Col>
        <Col xs={24} lg={12}>
          <Card title={t('tasks4.dash.topOverdue')} size="small">
            <Table size="small" pagination={false} rowKey="id" dataSource={d.topOverdue} locale={{ emptyText: t('tasks4.dash.noneOverdue') }}
              columns={[
                { title: t('common.title'), render: (_, x) => <Link to={`/tasks/${x.id}`}>{x.titleAr}</Link> },
                { title: t('tasks4.executor'), dataIndex: 'executorName' },
                { title: t('tasks4.dash.daysLate'), dataIndex: 'daysOverdue', width: 90, render: (v) => <span className="due-overdue">{v}</span> },
              ]} />
          </Card>
        </Col>
        <Col xs={24} lg={12}>
          <Card title={t('tasks4.dash.executive')} size="small" extra={<span className="item-meta">{t('tasks4.dash.executiveHelp')}</span>}>
            <Table size="small" pagination={false} rowKey="id" dataSource={d.executive} locale={{ emptyText: t('tasks4.dash.noneExecutive') }}
              columns={[
                { title: t('common.title'), render: (_, x) => <Link to={`/tasks/${x.id}`}>{x.titleAr}</Link> },
                { title: t('common.body'), dataIndex: 'bodyName' },
                { title: t('tasks4.escalationCol'), dataIndex: 'escalationLevel', width: 90, render: (v) => <Tag color="volcano">{t('tasks4.escalationN', { n: v })}</Tag> },
              ]} />
          </Card>
        </Col>
      </Row>
    </>
  );
}
