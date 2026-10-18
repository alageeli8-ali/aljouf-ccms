// وحدة الذكاء الاصطناعي (ADR-021): كل مخرج مسودة للمراجعة البشرية بمراجع لسجلات حقيقية، ولا يُطبق إلا بإجراء المستخدم عبر الواجهات الأساسية.
// الواجهة تخفي الإجراءات غير المتاحة، لكن القرار النهائي في الخادم دائمًا.
import {
  Alert, App, Badge, Button, Card, Checkbox, Col, Descriptions, Empty, Form, Input, List, Modal, Progress, Row, Select, Space, Statistic, Table, Tabs, Tag, Tooltip, Typography, Upload,
} from 'antd';
import {
  AudioOutlined, BulbOutlined, CheckOutlined, CloudServerOutlined, CopyOutlined, DeleteOutlined, ExperimentOutlined, FileSearchOutlined, KeyOutlined, LinkOutlined,
  PlusOutlined, ReloadOutlined, RobotOutlined, SaveOutlined, SearchOutlined, SendOutlined, UnlockOutlined, UploadOutlined, WarningOutlined,
} from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { api, get, post, put, type Paged } from '../../api/client';
import type {
  AiCitation, AiFact, AiJob, AiOutput, AiOverview, AiRequestLog, AiResult, AiStatement, AiStatus, MinutesDetail, SearchHitView,
} from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, PageHeader, errorText } from '../../components/common';
import { BodySelect, OrgUnitSelect, UserSelect } from '../../components/pickers';
import { formatDateTime } from '../../lib/format';

type Content = Record<string, unknown>;

// ============================ الحالة ============================

/** حالة الوحدة للمستخدم؛ لا يُستدعى الخادم إن كان AI_ENABLED معطلًا. */
export function useAiStatus() {
  const { feature } = useAuth();
  const enabled = feature('AI_ENABLED');
  const q = useQuery({ queryKey: ['ai-status'], queryFn: () => get<AiStatus>('/api/v1/ai/status'), enabled, staleTime: 20_000, retry: false });
  return { enabled, status: enabled ? q.data : undefined, fn: (code: string) => enabled && !!q.data?.functions[code] };
}

// ============================ عناصر مشتركة ============================

export function GeneratedBanner({ testOnly, provider, model, warnings, compact }: { testOnly: boolean; provider?: string; model?: string; warnings?: string[]; compact?: boolean }) {
  const { t } = useTranslation();
  return (
    <div className="ai-banner">
      <Space wrap size={6}>
        <Tag icon={<RobotOutlined />} color="purple">{t('ai.generated')}</Tag>
        {testOnly && <Tag icon={<ExperimentOutlined />} color="red">{t('ai.testOnly')}</Tag>}
        {!compact && provider && <span className="item-meta mono">{provider} · {model}</span>}
      </Space>
      {!compact && <div className="ai-banner-help">{t('ai.generatedHelp')}</div>}
      {(warnings ?? []).length > 0 && (
        <Alert type="warning" showIcon icon={<WarningOutlined />} style={{ marginTop: 8 }} message={t('ai.warnings')}
          description={<ul className="ai-warnings">{warnings!.map((w, i) => <li key={i}>{w}</li>)}</ul>} />
      )}
    </div>
  );
}

function RecordLink({ c }: { c: AiCitation }) {
  if (!c.link) return <>{c.label}</>;
  return c.link.startsWith('/api/') ? <a href={c.link} target="_blank" rel="noreferrer">{c.label}</a> : <Link to={c.link}>{c.label}</Link>;
}

export function Citations({ citations }: { citations?: AiCitation[] | null }) {
  const { t } = useTranslation();
  if (!citations?.length) return null;
  return (
    <div className="ai-citations">
      <Typography.Text type="secondary"><LinkOutlined /> {t('ai.references')}:</Typography.Text>
      {citations.map((c) => (
        <Tag key={c.ref} className="ai-cite"><span className="mono ai-ref">{c.ref}</span> {t(`ai.recordType.${c.type}`, { defaultValue: c.type })}: <RecordLink c={c} /></Tag>
      ))}
    </div>
  );
}

function Refs({ refs, citations }: { refs: string[]; citations?: AiCitation[] | null }) {
  return (
    <span className="ai-refs">
      {refs.map((r) => {
        const c = citations?.find((x) => x.ref === r);
        return <Tooltip key={r} title={c ? c.label : r}><sup className="ai-ref mono">{r}</sup></Tooltip>;
      })}
    </span>
  );
}

function Statements({ items, citations, empty }: { items?: AiStatement[] | null; citations?: AiCitation[] | null; empty?: string }) {
  if (!items?.length) return <Typography.Text type="secondary">{empty ?? '—'}</Typography.Text>;
  return <ul className="ai-statements">{items.map((s, i) => <li key={i}>{s.text} <Refs refs={s.refs} citations={citations} /></li>)}</ul>;
}

function useOutputActions(onDone: () => void) {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const save = useMutation({
    mutationFn: (id: string) => post(`/api/v1/ai/outputs/${id}/save`, {}),
    onSuccess: () => { message.success(t('ai.saved')); onDone(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const discard = (id: string) => {
    let reason = '';
    modal.confirm({
      title: t('ai.discard'), okText: t('ai.discard'), cancelText: t('common.cancel'), okButtonProps: { danger: true },
      content: <Input.TextArea rows={2} placeholder={t('ai.discardReason')} onChange={(e) => { reason = e.target.value; }} />,
      onOk: () => post(`/api/v1/ai/outputs/${id}/discard`, { reason: reason.trim() || t('ai.discardDefault') }).then(() => { message.success(t('ai.discarded')); onDone(); }),
    });
  };
  const release = useMutation({
    mutationFn: (id: string) => post(`/api/v1/ai/outputs/${id}/release`),
    onSuccess: () => { message.success(t('ai.released')); onDone(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const applied = (id: string, recordType: string, recordId: string, index?: number) =>
    post(`/api/v1/ai/outputs/${id}/applied`, { refs: [{ recordType, recordId, index }] });
  return { save, discard, release, applied };
}

function OutputToolbar({ outputId, onDone, saved }: { outputId?: string | null; onDone: () => void; saved?: boolean }) {
  const { t } = useTranslation();
  const a = useOutputActions(onDone);
  if (!outputId) return null;
  return (
    <Space style={{ marginTop: 12 }}>
      <Button icon={<SaveOutlined />} disabled={saved} loading={a.save.isPending} onClick={() => a.save.mutate(outputId)}>{saved ? t('ai.status.saved') : t('ai.save')}</Button>
      <Button icon={<DeleteOutlined />} onClick={() => a.discard(outputId)}>{t('ai.discard')}</Button>
    </Space>
  );
}

// ============================ عرض المخرجات حسب الوظيفة ============================

interface ViewCtx {
  outputId?: string | null;
  citations?: AiCitation[] | null;
  minutes?: MinutesDetail;
  meetingId?: string;
  canAddAgenda?: boolean;
  onApplied?: () => void;
}

export function OutputBody({ fn, content, ctx }: { fn: string; content: Content; ctx: ViewCtx }) {
  const { t } = useTranslation();
  switch (fn) {
    case 'minutes_summary':
      return (
        <div className="ai-sections">
          <h4>{t('ai.summary.points')}</h4><Statements items={content.points as AiStatement[]} citations={ctx.citations} />
          <h4>{t('ai.summary.decisions')}</h4><Statements items={content.decisions as AiStatement[]} citations={ctx.citations} empty={t('ai.none')} />
          <h4>{t('ai.summary.followUps')}</h4><Statements items={content.followUps as AiStatement[]} citations={ctx.citations} empty={t('ai.none')} />
        </div>
      );
    case 'extract_decisions': return <DecisionItems items={content.items as DecisionItem[]} ctx={ctx} />;
    case 'extract_tasks': return <TaskItems items={content.items as TaskItem[]} ctx={ctx} />;
    case 'minutes_draft': return <DraftResult content={content as unknown as DraftContent} ctx={ctx} />;
    case 'agenda_draft': return <AgendaItems items={content.items as AgendaItem[]} ctx={ctx} />;
    case 'link_task': return <LinkResult content={content as unknown as LinkContent} />;
    case 'transcription': return <TranscriptText text={String(content.text ?? '')} />;
    case 'executive_summary': return <ExecutiveBody content={content} citations={ctx.citations} />;
    case 'ask': return <Statements items={content.answer as AiStatement[]} citations={ctx.citations} empty={t('assistant.noAnswer')} />;
    default: return <pre className="mono">{JSON.stringify(content, null, 2)}</pre>;
  }
}

interface DecisionItem { kind: string; title: string; text: string; quote: string; refs: string[]; agendaItemId?: string | null; agendaTitle?: string | null; possibleDuplicateOf?: { id: string; title: string; number?: string | null } | null }

function DecisionItems({ items, ctx }: { items: DecisionItem[]; ctx: ViewCtx }) {
  const { t } = useTranslation();
  const [create, setCreate] = useState<{ item: DecisionItem; index: number } | null>(null);
  if (!items?.length) return <Empty description={t('ai.decisions.empty')} />;
  const canCreate = !!ctx.minutes?.actions.canManageResolutions && !!ctx.outputId;
  return (
    <>
      {items.map((d, i) => (
        <Card key={i} size="small" className="ai-item" title={<Space><Tag color={d.kind === 'decision' ? 'green' : 'blue'}>{t(`ai.decisions.kind.${d.kind}`)}</Tag>{d.title}</Space>}
          extra={canCreate && <Button size="small" icon={<PlusOutlined />} onClick={() => setCreate({ item: d, index: i })}>{t('ai.decisions.create')}</Button>}>
          <blockquote className="ai-quote">{d.quote} <Refs refs={d.refs} citations={ctx.citations} /></blockquote>
          {d.agendaTitle && <div className="item-meta">{t('ai.agendaItem')}: {d.agendaTitle}</div>}
          {d.possibleDuplicateOf && <Alert type="warning" showIcon style={{ marginTop: 8 }} message={t('ai.decisions.duplicate', { title: d.possibleDuplicateOf.number ?? d.possibleDuplicateOf.title })} />}
        </Card>
      ))}
      {create && <CreateResolutionModal item={create.item} index={create.index} ctx={ctx} onClose={() => setCreate(null)} />}
    </>
  );
}

function CreateResolutionModal({ item, index, ctx, onClose }: { item: DecisionItem; index: number; ctx: ViewCtx; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const a = useOutputActions(() => ctx.onApplied?.());
  const save = useMutation({
    mutationFn: async (v: { titleAr: string; textAr: string; resolutionType: string }) => {
      const r = await post<{ id: string }>(`/api/v1/minutes/${ctx.minutes!.minutes.id}/resolutions`, { ...v, agendaItemId: item.agendaItemId ?? undefined, category: 'administrative' });
      await a.applied(ctx.outputId!, 'resolution', r.id, index);
    },
    onSuccess: () => { message.success(t('ai.createdApplied')); ctx.onApplied?.(); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('ai.decisions.create')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={save.isPending} okText={t('common.create')} cancelText={t('common.cancel')}>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('ai.reviewBeforeCreate')} />
      <Form form={form} layout="vertical" initialValues={{ titleAr: item.title, textAr: item.text, resolutionType: item.kind }} onFinish={(v) => save.mutate(v)}>
        <Form.Item name="resolutionType" label={t('ai.decisions.type')}><Select options={['decision', 'recommendation'].map((k) => ({ value: k, label: t(`ai.decisions.kind.${k}`) }))} /></Form.Item>
        <Form.Item name="titleAr" label={t('common.title')} rules={[{ required: true }]}><Input maxLength={300} /></Form.Item>
        <Form.Item name="textAr" label={t('ai.decisions.text')} rules={[{ required: true }]}><Input.TextArea rows={4} /></Form.Item>
      </Form>
    </Modal>
  );
}

interface TaskItem {
  title: string; quote: string; refs: string[]; dueText?: string | null; dueDate?: string | null; dueBasis?: string | null;
  assignee?: { matched: boolean; type?: string; id?: string; name?: string; rawText?: string } | null; resolutionId?: string | null; resolutionTitle?: string | null;
}

function TaskItems({ items, ctx }: { items: TaskItem[]; ctx: ViewCtx }) {
  const { t } = useTranslation();
  const [create, setCreate] = useState<{ item: TaskItem; index: number } | null>(null);
  if (!items?.length) return <Empty description={t('ai.tasks.empty')} />;
  const canCreate = !!ctx.outputId && !!ctx.minutes;
  return (
    <>
      {items.map((x, i) => (
        <Card key={i} size="small" className="ai-item" title={x.title}
          extra={canCreate && <Button size="small" icon={<PlusOutlined />} onClick={() => setCreate({ item: x, index: i })}>{t('ai.tasks.create')}</Button>}>
          <blockquote className="ai-quote">{x.quote} <Refs refs={x.refs} citations={ctx.citations} /></blockquote>
          <Descriptions size="small" column={{ xs: 1, md: 3 }}>
            <Descriptions.Item label={t('ai.tasks.assignee')}>
              {x.assignee?.matched ? <Space><Tag color="green" icon={<CheckOutlined />}>{t('ai.tasks.matched')}</Tag>{x.assignee.name}</Space>
                : x.assignee ? <Space><Tag color="orange">{t('ai.tasks.unmatched')}</Tag>«{x.assignee.rawText}»</Space>
                : <Typography.Text type="secondary">{t('ai.tasks.noAssignee')}</Typography.Text>}
            </Descriptions.Item>
            <Descriptions.Item label={t('ai.tasks.due')}>
              {x.dueDate ? <Space direction="vertical" size={0}><span className="mono">{x.dueDate}</span>
                <span className="item-meta">{x.dueBasis?.startsWith('relative') ? t('ai.tasks.basis.relative', { text: x.dueText }) : t(`ai.tasks.basis.${x.dueBasis}`, { text: x.dueText })}</span></Space>
                : <Typography.Text type="secondary">{t('ai.tasks.noDue')}</Typography.Text>}
            </Descriptions.Item>
            <Descriptions.Item label={t('ai.tasks.resolution')}>{x.resolutionId ? <Link to={`/resolutions/${x.resolutionId}`}>{x.resolutionTitle}</Link> : '—'}</Descriptions.Item>
          </Descriptions>
        </Card>
      ))}
      {create && <CreateTaskModal item={create.item} index={create.index} ctx={ctx} onClose={() => setCreate(null)} />}
    </>
  );
}

function CreateTaskModal({ item, index, ctx, onClose }: { item: TaskItem; index: number; ctx: ViewCtx; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const { me } = useAuth();
  const [form] = Form.useForm();
  const a = useOutputActions(() => ctx.onApplied?.());
  const kind = item.assignee?.matched && item.assignee.type === 'member' ? 'member' : 'org_unit';
  const save = useMutation({
    mutationFn: async (v: { titleAr: string; dueDate: string; assigneeOrgUnitId?: string; assigneeUserId?: string; accountableUserId: string }) => {
      const r = await post<{ id: string }>('/api/v1/tasks', {
        bodyId: ctx.minutes!.minutes.bodyId, resolutionId: item.resolutionId ?? undefined, titleAr: v.titleAr, assigneeType: kind,
        assigneeOrgUnitId: kind === 'org_unit' ? v.assigneeOrgUnitId : undefined, assigneeUserId: kind === 'member' ? v.assigneeUserId : undefined,
        accountableUserId: v.accountableUserId, dueDate: v.dueDate, description: item.quote,
      });
      await a.applied(ctx.outputId!, 'task', r.id, index);
    },
    onSuccess: () => { message.success(t('ai.createdApplied')); ctx.onApplied?.(); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('ai.tasks.create')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={save.isPending} okText={t('common.create')} cancelText={t('common.cancel')}>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('ai.reviewBeforeCreate')} />
      <Form form={form} layout="vertical" onFinish={(v) => save.mutate(v)} initialValues={{
        titleAr: item.title, dueDate: item.dueDate ?? undefined, accountableUserId: me?.user.id,
        assigneeOrgUnitId: kind === 'org_unit' && item.assignee?.matched ? item.assignee.id : undefined, assigneeUserId: kind === 'member' ? item.assignee?.id : undefined,
      }}>
        <Form.Item name="titleAr" label={t('common.title')} rules={[{ required: true }]}><Input maxLength={300} /></Form.Item>
        {kind === 'org_unit'
          ? <Form.Item name="assigneeOrgUnitId" label={t('ai.tasks.assignee')} rules={[{ required: true }]} extra={!item.assignee?.matched ? t('ai.tasks.pickAssignee') : undefined}><OrgUnitSelect /></Form.Item>
          : <Form.Item name="assigneeUserId" label={t('ai.tasks.assignee')} rules={[{ required: true }]}><UserSelect /></Form.Item>}
        <Form.Item name="accountableUserId" label={t('ai.tasks.accountable')} rules={[{ required: true }]}><UserSelect /></Form.Item>
        <Form.Item name="dueDate" label={t('ai.tasks.due')} rules={[{ required: true }]} extra={!item.dueDate ? t('ai.tasks.pickDue') : undefined}><Input type="date" /></Form.Item>
      </Form>
    </Modal>
  );
}

interface DraftContent {
  sections: { key: string; title: string; body: string; refs: string[] }[];
  items: { agendaItemId: string; agendaTitle: string; discussion?: string | null; recommendation?: string | null; refs: string[] }[];
}

function DraftResult({ content, ctx }: { content: DraftContent; ctx: ViewCtx }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const a = useOutputActions(() => ctx.onApplied?.());
  const minutes = ctx.minutes;
  const canApply = !!minutes && minutes.minutes.status === 'draft' && minutes.actions.canEdit && !!ctx.outputId;
  const apply = useMutation({
    mutationFn: async (which: { sections: string[]; items: string[] }) => {
      const cur = minutes!.minutes.content;
      const sections = cur.sections.map((s) => {
        const n = content.sections.find((x) => x.key === s.key && which.sections.includes(x.key));
        return n ? { ...s, body: n.body } : s;
      });
      const items = [...(cur.items ?? [])];
      for (const n of content.items.filter((x) => which.items.includes(x.agendaItemId))) {
        const i = items.findIndex((x) => x.agendaItemId === n.agendaItemId);
        const v = { agendaItemId: n.agendaItemId, discussion: n.discussion ?? '', recommendation: n.recommendation ?? '' };
        if (i >= 0) items[i] = v; else items.push(v);
      }
      await put(`/api/v1/minutes/${minutes!.minutes.id}/draft`, { sections: sections.map((s) => ({ key: s.key, title: s.title, body: s.body ?? '' })), items });
      await a.applied(ctx.outputId!, 'minutes', minutes!.minutes.id);
    },
    onSuccess: () => { message.success(t('ai.draft.applied')); ctx.onApplied?.(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  if (!content.sections?.length && !content.items?.length) return <Empty description={t('ai.draft.empty')} />;
  return (
    <>
      {canApply && <Button type="primary" icon={<CheckOutlined />} loading={apply.isPending} style={{ marginBottom: 12 }}
        onClick={() => apply.mutate({ sections: content.sections.map((s) => s.key), items: content.items.map((i) => i.agendaItemId) })}>{t('ai.draft.applyAll')}</Button>}
      {content.sections.map((s) => (
        <Card key={s.key} size="small" className="ai-item" title={s.title}
          extra={canApply && <Button size="small" onClick={() => apply.mutate({ sections: [s.key], items: [] })}>{t('ai.draft.applyOne')}</Button>}>
          <div className="ai-pre">{s.body}</div><Refs refs={s.refs} citations={ctx.citations} />
        </Card>
      ))}
      {content.items.map((it) => (
        <Card key={it.agendaItemId} size="small" className="ai-item" title={it.agendaTitle}
          extra={canApply && <Button size="small" onClick={() => apply.mutate({ sections: [], items: [it.agendaItemId] })}>{t('ai.draft.applyOne')}</Button>}>
          {it.discussion && <><div className="item-meta">{t('ai.draft.discussion')}</div><div className="ai-pre">{it.discussion}</div></>}
          {it.recommendation && <><div className="item-meta">{t('ai.draft.recommendation')}</div><div className="ai-pre">{it.recommendation}</div></>}
          <Refs refs={it.refs} citations={ctx.citations} />
        </Card>
      ))}
    </>
  );
}

interface AgendaItem { title: string; reason?: string | null; itemType: string; refs: string[] }

function AgendaItems({ items, ctx }: { items: AgendaItem[]; ctx: ViewCtx }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const a = useOutputActions(() => ctx.onApplied?.());
  const [added, setAdded] = useState<number[]>([]);
  const add = useMutation({
    mutationFn: async ({ item, index }: { item: AgendaItem; index: number }) => {
      const r = await post<{ id: string }>(`/api/v1/meetings/${ctx.meetingId}/agenda-items`, { titleAr: item.title, itemType: item.itemType, descriptionAr: item.reason ?? undefined });
      await a.applied(ctx.outputId!, 'agenda_item', r.id, index);
      return index;
    },
    onSuccess: (i) => { setAdded((x) => [...x, i]); message.success(t('ai.createdApplied')); void qc.invalidateQueries({ queryKey: ['meeting', ctx.meetingId] }); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  if (!items?.length) return <Empty description={t('ai.agenda.empty')} />;
  return (
    <List dataSource={items} renderItem={(it, i) => (
      <List.Item actions={ctx.canAddAgenda && ctx.outputId ? [
        <Button key="add" size="small" icon={added.includes(i) ? <CheckOutlined /> : <PlusOutlined />} disabled={added.includes(i)} loading={add.isPending && add.variables?.index === i}
          onClick={() => add.mutate({ item: it, index: i })}>{added.includes(i) ? t('ai.agenda.added') : t('ai.agenda.add')}</Button>] : []}>
        <List.Item.Meta title={<Space><Tag>{t(`ai.agenda.type.${it.itemType}`)}</Tag>{it.title}<Refs refs={it.refs} citations={ctx.citations} /></Space>} description={it.reason} />
      </List.Item>
    )} />
  );
}

interface LinkContent { best?: { resolutionId: string; title: string; number?: string | null; confidence: number; reason?: string | null } | null; alternatives: { resolutionId: string; title: string; number?: string | null; confidence: number }[] }

function LinkResult({ content }: { content: LinkContent }) {
  const { t } = useTranslation();
  if (!content.best) return <Alert type="info" showIcon message={t('ai.link.none')} />;
  return (
    <>
      <Descriptions size="small" column={1} bordered>
        <Descriptions.Item label={t('ai.link.best')}><Link to={`/resolutions/${content.best.resolutionId}`}>{content.best.number ?? ''} — {content.best.title}</Link></Descriptions.Item>
        <Descriptions.Item label={t('ai.link.confidence')}><Progress percent={Math.round(content.best.confidence * 100)} size="small" style={{ maxWidth: 220 }} /></Descriptions.Item>
        {content.best.reason && <Descriptions.Item label={t('ai.link.reason')}>{content.best.reason}</Descriptions.Item>}
      </Descriptions>
      {content.alternatives.length > 0 && <div style={{ marginTop: 8 }}><Typography.Text type="secondary">{t('ai.link.alternatives')}: </Typography.Text>
        {content.alternatives.map((x) => <Tag key={x.resolutionId}><Link to={`/resolutions/${x.resolutionId}`}>{x.number ?? x.title}</Link> ({Math.round(x.confidence * 100)}٪)</Tag>)}</div>}
      <Alert type="info" showIcon style={{ marginTop: 8 }} message={t('ai.link.manual')} />
    </>
  );
}

function TranscriptText({ text }: { text: string }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  return (
    <>
      <div className="ai-pre ai-transcript">{text}</div>
      <Button size="small" icon={<CopyOutlined />} style={{ marginTop: 8 }} onClick={() => void navigator.clipboard?.writeText(text).then(() => message.success(t('ai.copied')))}>{t('ai.copy')}</Button>
    </>
  );
}

function ExecutiveBody({ content, citations }: { content: Content; citations?: AiCitation[] | null }) {
  const { t } = useTranslation();
  const head = content.headline as AiStatement | null;
  return (
    <div className="ai-sections">
      {head && <div className="ai-headline">{head.text} <Refs refs={head.refs} citations={citations} /></div>}
      <h4>{t('assistant.exec.highlights')}</h4><Statements items={content.highlights as AiStatement[]} citations={citations} empty={t('ai.none')} />
      <h4>{t('assistant.exec.risks')}</h4><Statements items={content.risks as AiStatement[]} citations={citations} empty={t('ai.none')} />
      <h4>{t('assistant.exec.attention')}</h4><Statements items={content.attention as AiStatement[]} citations={citations} empty={t('ai.none')} />
    </div>
  );
}

// ============================ المخرجات السابقة ============================

function OutputHistory({ subjectType, subjectId, ctx, onChange }: { subjectType: string; subjectId: string; ctx: ViewCtx; onChange: () => void }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const [open, setOpen] = useState<AiOutput | null>(null);
  const q = useQuery({ queryKey: ['ai-outputs', subjectType, subjectId], queryFn: () => get<AiOutput[]>('/api/v1/ai/outputs', { subjectType, subjectId }) });
  const a = useOutputActions(() => { onChange(); void q.refetch(); setOpen(null); });
  const items = (q.data ?? []).filter((o) => o.status !== 'discarded');
  if (!items.length) return null;
  return (
    <Card size="small" title={t('ai.history')} style={{ marginTop: 16 }}>
      <List size="small" dataSource={items} renderItem={(o) => (
        <List.Item actions={[<Button key="v" size="small" type="link" onClick={() => setOpen(o)}>{t('ai.view')}</Button>]}>
          <Space wrap>
            <Tag icon={<RobotOutlined />} color="purple">{t(`ai.functions.${o.function}`)}</Tag>
            <Tag color={o.status === 'held' ? 'orange' : o.status === 'saved' || o.status === 'applied' ? 'green' : undefined}>{t(`ai.status.${o.status}`)}</Tag>
            {o.testOnly && <Tag color="red">{t('ai.testShort')}</Tag>}
            <span className="item-meta">{o.requestedByName} — {formatDateTime(o.createdAt, i18n.language, me?.user.calendar)}</span>
          </Space>
        </List.Item>
      )} />
      {open && (
        <Modal open width={860} title={t(`ai.functions.${open.function}`)} onCancel={() => setOpen(null)} footer={<Space>
          {open.can.release && <Button icon={<UnlockOutlined />} onClick={() => a.release.mutate(open.id)}>{t('ai.release')}</Button>}
          {open.can.save && <Button icon={<SaveOutlined />} onClick={() => a.save.mutate(open.id)}>{t('ai.save')}</Button>}
          {open.can.discard && <Button danger icon={<DeleteOutlined />} onClick={() => a.discard(open.id)}>{t('ai.discard')}</Button>}
          <Button onClick={() => setOpen(null)}>{t('common.close')}</Button>
        </Space>}>
          <GeneratedBanner testOnly={open.testOnly} provider={open.provider} model={open.model} warnings={open.warnings} />
          {open.status === 'held' ? <Alert type="warning" showIcon message={t('ai.heldNotice')} />
            : open.content ? <OutputBody fn={open.function} content={open.content} ctx={{ ...ctx, outputId: open.can.applied ? open.id : null, citations: open.citations }} />
            : <Alert type="info" showIcon message={t('ai.purged')} />}
          <Citations citations={open.citations} />
          {open.appliedRefs.length > 0 && <div className="item-meta" style={{ marginTop: 8 }}>{t('ai.appliedCount', { n: open.appliedRefs.length })}</div>}
        </Modal>
      )}
    </Card>
  );
}

// ============================ لوحة المحضر ============================

export function useAiOutputsExist(subjectType: string, subjectId?: string) {
  const q = useQuery({ queryKey: ['ai-outputs', subjectType, subjectId], queryFn: () => get<AiOutput[]>('/api/v1/ai/outputs', { subjectType, subjectId }), enabled: !!subjectId });
  return (q.data ?? []).some((o) => o.status !== 'discarded');
}

function NotReady({ status }: { status?: AiStatus }) {
  const { t } = useTranslation();
  if (!status || status.provider.ready) return null;
  return <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('ai.notReady', { reason: status.provider.reasonText })} />;
}

export function AiMinutesPanel({ detail, onChange }: { detail: MinutesDetail; onChange: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const { enabled, status, fn } = useAiStatus();
  const m = detail.minutes;
  const perm = (p: string) => detail.permissions.includes(p);
  const [result, setResult] = useState<{ fn: string; data: AiResult } | null>(null);
  const [notes, setNotes] = useState<string | null>(null);
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['ai-outputs', 'minutes', m.id] }); onChange(); };
  const run = useMutation({
    mutationFn: ({ fn: f, path, body }: { fn: string; path: string; body?: unknown }) => post<AiResult>(`/api/v1/ai/minutes/${m.id}/${path}`, body ?? {}).then((data) => ({ fn: f, data })),
    onSuccess: (r) => { setResult(r); setNotes(null); void qc.invalidateQueries({ queryKey: ['ai-outputs', 'minutes', m.id] }); void qc.invalidateQueries({ queryKey: ['ai-status'] }); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const ready = !!status?.provider.ready;
  const useAi = perm('ai.use');
  const buttons: { key: string; show: boolean; label: string; icon: ReactNode; onClick: () => void }[] = [
    { key: 'minutes_summary', show: fn('minutes_summary') && useAi, label: t('ai.run.summary'), icon: <BulbOutlined />, onClick: () => run.mutate({ fn: 'minutes_summary', path: 'summary' }) },
    { key: 'extract_decisions', show: fn('extract_decisions') && useAi && perm('minutes.edit'), label: t('ai.run.decisions'), icon: <FileSearchOutlined />, onClick: () => run.mutate({ fn: 'extract_decisions', path: 'extract-decisions' }) },
    { key: 'extract_tasks', show: fn('extract_tasks') && useAi && (perm('minutes.edit') || perm('resolution.manage')), label: t('ai.run.tasks'), icon: <FileSearchOutlined />, onClick: () => run.mutate({ fn: 'extract_tasks', path: 'extract-tasks' }) },
    { key: 'minutes_draft', show: fn('minutes_draft') && useAi && perm('minutes.edit') && m.status === 'draft', label: t('ai.run.draft'), icon: <RobotOutlined />, onClick: () => { setNotes(''); setResult(null); } },
  ];
  const visible = buttons.filter((b) => b.show);
  return (
    <div className="ai-panel">
      {!enabled && <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('ai.disabledArchive')} />}
      {enabled && <NotReady status={status} />}
      {enabled && visible.length > 0 && (
        <Space wrap style={{ marginBottom: 12 }}>
          {visible.map((b) => <Button key={b.key} icon={b.icon} disabled={!ready} loading={run.isPending && run.variables?.fn === b.key} onClick={b.onClick}>{b.label}</Button>)}
          <Typography.Text type="secondary" className="item-meta">{t('ai.quota', { used: status?.usage.myToday ?? 0, limit: status?.usage.dailyLimit ?? 0 })}</Typography.Text>
        </Space>
      )}
      {enabled && visible.length === 0 && <Typography.Paragraph type="secondary">{t('ai.noActions')}</Typography.Paragraph>}
      {notes !== null && (
        <Card size="small" title={t('ai.draft.notes')} style={{ marginBottom: 12 }}>
          <Typography.Paragraph type="secondary">{t('ai.draft.notesHelp')}</Typography.Paragraph>
          <Input.TextArea rows={8} value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={20000} showCount />
          <Space style={{ marginTop: 8 }}>
            <Button type="primary" icon={<SendOutlined />} disabled={notes.trim().length < 20 || !ready} loading={run.isPending}
              onClick={() => run.mutate({ fn: 'minutes_draft', path: 'draft-from-notes', body: { notes } })}>{t('ai.draft.generate')}</Button>
            <Button onClick={() => setNotes(null)}>{t('common.cancel')}</Button>
          </Space>
        </Card>
      )}
      {result && (
        <Card size="small" title={t(`ai.functions.${result.fn}`)} className="ai-result">
          <GeneratedBanner testOnly={result.data.testOnly} provider={result.data.provider} model={result.data.model} warnings={result.data.warnings} />
          {result.data.noSources ? <Alert type="info" showIcon message={t('ai.noSources')} />
            : <OutputBody fn={result.fn} content={result.data.content} ctx={{ outputId: result.data.outputId, citations: result.data.citations, minutes: detail, onApplied: refresh }} />}
          <Citations citations={result.data.citations} />
          <OutputToolbar outputId={result.data.outputId} onDone={() => { setResult(null); refresh(); }} />
        </Card>
      )}
      <OutputHistory subjectType="minutes" subjectId={m.id} ctx={{ minutes: detail, onApplied: refresh }} onChange={refresh} />
    </div>
  );
}

// ============================ لوحة الاجتماع: جدول الأعمال والتفريغ ============================

export function AiMeetingPanel({ meetingId, bodyId, permissions, preparing }: { meetingId: string; bodyId: string; permissions: string[]; preparing: boolean }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const { enabled, status, fn } = useAiStatus();
  const manage = permissions.includes('meeting.manage');
  const [result, setResult] = useState<AiResult | null>(null);
  const agenda = useMutation({
    mutationFn: () => post<AiResult>(`/api/v1/ai/bodies/${bodyId}/agenda-draft`, { meetingId }),
    onSuccess: (r) => { setResult(r); void qc.invalidateQueries({ queryKey: ['ai-outputs', 'meeting', meetingId] }); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <div className="ai-panel">
      {!enabled && <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('ai.disabledArchive')} />}
      {enabled && <NotReady status={status} />}
      {enabled && fn('agenda_draft') && manage && (
        <Card size="small" title={<Space><BulbOutlined />{t('ai.functions.agenda_draft')}</Space>} style={{ marginBottom: 16 }}
          extra={<Button icon={<RobotOutlined />} disabled={!status?.provider.ready} loading={agenda.isPending} onClick={() => agenda.mutate()}>{t('ai.run.agenda')}</Button>}>
          <Typography.Paragraph type="secondary">{t('ai.agenda.help')}</Typography.Paragraph>
          {result && (
            <>
              <GeneratedBanner testOnly={result.testOnly} provider={result.provider} model={result.model} warnings={result.warnings} />
              {result.noSources ? <Alert type="info" showIcon message={t('ai.agenda.empty')} />
                : <OutputBody fn="agenda_draft" content={result.content} ctx={{ outputId: result.outputId, citations: result.citations, meetingId, canAddAgenda: preparing && manage }} />}
              {!preparing && <Alert type="info" showIcon style={{ marginTop: 8 }} message={t('ai.agenda.notPreparing')} />}
              <Citations citations={result.citations} />
              <OutputToolbar outputId={result.outputId} onDone={() => setResult(null)} />
            </>
          )}
        </Card>
      )}
      <TranscriptionCard meetingId={meetingId} />
      <OutputHistory subjectType="meeting" subjectId={meetingId} ctx={{ meetingId, canAddAgenda: preparing && manage }} onChange={() => undefined} />
    </div>
  );
}

function TranscriptionCard({ meetingId }: { meetingId: string }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const { me } = useAuth();
  const { status, fn } = useAiStatus();
  const [file, setFile] = useState<File | null>(null);
  const [consent, setConsent] = useState(false);
  const [reference, setReference] = useState('');
  const [view, setView] = useState<string | null>(null);
  const q = useQuery({ queryKey: ['ai-jobs', meetingId], queryFn: () => get<{ enabled: boolean; canUpload: boolean; jobs: AiJob[] }>(`/api/v1/ai/meetings/${meetingId}/transcriptions`),
    refetchInterval: (d) => (d.state.data?.jobs.some((j) => j.status === 'queued' || j.status === 'running') ? 5000 : false) });
  const upload = useMutation({
    mutationFn: () => {
      const fd = new FormData();
      fd.append('file', file!);
      fd.append('consentConfirmed', String(consent));
      fd.append('consentReference', reference);
      return api('POST', `/api/v1/ai/meetings/${meetingId}/transcriptions`, fd);
    },
    onSuccess: () => { message.success(t('ai.transcription.queued')); setFile(null); setConsent(false); setReference(''); void q.refetch(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const act = useMutation({
    mutationFn: ({ id, action, reason }: { id: string; action: 'resume' | 'cancel'; reason?: string }) => post(`/api/v1/ai/jobs/${id}/${action}`, action === 'cancel' ? { reason } : {}),
    onSuccess: () => void q.refetch(),
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const outputQ = useQuery({ queryKey: ['ai-output', view], queryFn: () => get<AiOutput>(`/api/v1/ai/outputs/${view}`), enabled: !!view });
  const jobs = q.data?.jobs ?? [];
  if (!fn('transcription') && jobs.length === 0) return null;
  return (
    <Card size="small" title={<Space><AudioOutlined />{t('ai.transcription.title')}</Space>}>
      {q.data?.canUpload && (
        <>
          <Typography.Paragraph type="secondary">{t('ai.transcription.help')}</Typography.Paragraph>
          {status && !status.provider.transcriptionReady && <Alert type="warning" showIcon style={{ marginBottom: 8 }} message={t('ai.transcription.notAvailable', { reason: status.provider.transcriptionReason })} />}
          <Space direction="vertical" style={{ width: '100%' }}>
            <Upload beforeUpload={(f) => { setFile(f); return false; }} maxCount={1} accept="audio/*" onRemove={() => setFile(null)} fileList={file ? [{ uid: '1', name: file.name, status: 'done' }] : []}>
              <Button icon={<UploadOutlined />}>{t('ai.transcription.file')}</Button>
            </Upload>
            <Checkbox checked={consent} onChange={(e) => setConsent(e.target.checked)}>{t('ai.transcription.consent')}</Checkbox>
            <Input value={reference} onChange={(e) => setReference(e.target.value)} placeholder={t('ai.transcription.consentRef')} maxLength={300} />
            <Button type="primary" icon={<SendOutlined />} disabled={!file || !consent || reference.trim().length < 3 || !status?.provider.transcriptionReady}
              loading={upload.isPending} onClick={() => upload.mutate()}>{t('ai.transcription.upload')}</Button>
          </Space>
        </>
      )}
      {jobs.length > 0 && (
        <Table<AiJob> size="small" rowKey="id" dataSource={jobs} pagination={false} style={{ marginTop: 12 }} columns={[
          { title: t('ai.transcription.file'), dataIndex: 'fileName', render: (v, j) => <a href={`/api/v1/documents/${j.documentId}/content`} target="_blank" rel="noreferrer">{v}</a> },
          { title: t('ai.transcription.consentRef'), render: (_, j) => j.consent.reference },
          { title: t('common.status'), render: (_, j) => <Space direction="vertical" size={0}>
            <Tag color={{ completed: 'green', failed: 'red', held: 'orange', cancelled: 'default', queued: 'blue', running: 'processing' }[j.status]}>{t(`ai.transcription.status.${j.status}`)}</Tag>
            {(j.lastError || j.heldReason) && <span className="item-meta">{j.lastError ?? t(`ai.transcription.held.${j.heldReason}`, { defaultValue: j.heldReason ?? '' })}</span>}
          </Space> },
          { title: t('ai.when'), dataIndex: 'createdAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
          { title: t('common.actions'), render: (_, j) => <Space>
            {j.outputId && <Button size="small" type="link" onClick={() => setView(j.outputId!)}>{t('ai.transcription.text')}</Button>}
            {j.can.resume && <Button size="small" icon={<ReloadOutlined />} onClick={() => act.mutate({ id: j.id, action: 'resume' })}>{t('ai.transcription.resume')}</Button>}
            {j.can.cancel && <Button size="small" danger onClick={() => act.mutate({ id: j.id, action: 'cancel', reason: t('ai.transcription.cancelDefault') })}>{t('ai.transcription.cancel')}</Button>}
          </Space> },
        ]} />
      )}
      {view && outputQ.data && (
        <Modal open width={760} title={t('ai.transcription.text')} onCancel={() => setView(null)} footer={null}>
          <GeneratedBanner testOnly={outputQ.data.testOnly} provider={outputQ.data.provider} model={outputQ.data.model} warnings={outputQ.data.warnings} />
          {outputQ.data.status === 'held' ? <Alert type="warning" showIcon message={t('ai.heldNotice')} /> : <TranscriptText text={String(outputQ.data.content?.text ?? '')} />}
        </Modal>
      )}
    </Card>
  );
}

// ============================ المهمة: اقتراح القرار المرتبط ============================

export function AiTaskLink({ taskId }: { taskId: string }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const { fn, status } = useAiStatus();
  const [r, setR] = useState<AiResult | null>(null);
  const run = useMutation({
    mutationFn: () => post<AiResult>(`/api/v1/ai/tasks/${taskId}/link-suggestion`),
    onSuccess: setR,
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  if (!fn('link_task')) return null;
  return (
    <div style={{ marginTop: 8 }}>
      <Button size="small" icon={<RobotOutlined />} disabled={!status?.provider.ready} loading={run.isPending} onClick={() => run.mutate()}>{t('ai.run.link')}</Button>
      {r && <div className="ai-inline-result">
        <GeneratedBanner testOnly={r.testOnly} compact warnings={r.warnings} />
        <OutputBody fn="link_task" content={r.content} ctx={{ citations: r.citations }} />
      </div>}
    </div>
  );
}

// ============================ البحث والمساعد ============================

const TYPES = ['resolution', 'task', 'minutes', 'meeting'];

function HitsTable({ items, showProcessable }: { items: SearchHitView[]; showProcessable?: boolean }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  return (
    <Table<SearchHitView> rowKey={(h) => h.type + h.id} dataSource={items} size="middle" pagination={{ pageSize: 10, hideOnSinglePage: true }}
      locale={{ emptyText: t('assistant.noResults') }} columns={[
        { title: t('assistant.type'), dataIndex: 'type', width: 110, render: (v) => <Tag>{t(`ai.recordType.${v}`)}</Tag> },
        { title: t('common.title'), render: (_, h) => <Space direction="vertical" size={0}>
          <Link to={h.link}>{h.reference ? <span className="mono">{h.reference} </span> : null}{h.title}</Link>
          {h.snippet && <span className="item-meta">{h.snippet}</span>}
        </Space> },
        { title: t('assistant.body'), dataIndex: 'bodyName', width: 200 },
        { title: t('common.status'), dataIndex: 'status', width: 130, render: (v, h) => v ? t(`assistant.status.${h.type}.${v}`, { defaultValue: v }) : '—' },
        { title: t('assistant.due'), dataIndex: 'dueDate', width: 110, render: (v) => v ? <span className="mono">{v}</span> : '—' },
        { title: t('assistant.date'), dataIndex: 'date', width: 150, render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
        ...(showProcessable ? [{ title: '', width: 40, render: (_: unknown, h: SearchHitView) => h.aiProcessable === false ? <Tooltip title={t('assistant.notProcessable')}><WarningOutlined style={{ color: '#ad6800' }} /></Tooltip> : null }] : []),
      ]} />
  );
}

export function SearchPage() {
  const { t } = useTranslation();
  const { can } = useAuth();
  const { enabled, fn } = useAiStatus();
  const tabs = [
    { key: 'search', label: <span><SearchOutlined /> {t('assistant.tabs.search')}</span>, children: <TraditionalSearch /> },
    ...(fn('ask') ? [{ key: 'ask', label: <span><RobotOutlined /> {t('assistant.tabs.ask')}</span>, children: <AskTab /> }] : []),
    ...(fn('smart_search') ? [{ key: 'smart', label: <span><RobotOutlined /> {t('assistant.tabs.smart')}</span>, children: <SmartSearchTab /> }] : []),
    ...(fn('executive_summary') && can('reports.executive') ? [{ key: 'exec', label: <span><RobotOutlined /> {t('assistant.tabs.exec')}</span>, children: <ExecutiveTab /> }] : []),
  ];
  return (
    <>
      <PageHeader title={t('assistant.title')} subtitle={enabled ? t('assistant.subtitle') : t('assistant.subtitleNoAi')} />
      <Tabs items={tabs} destroyInactiveTabPane={false} />
    </>
  );
}

function TraditionalSearch() {
  const { t } = useTranslation();
  const [query, setQuery] = useState<{ q: string; types?: string[]; bodyId?: string; status?: string; overdue?: boolean } | null>(null);
  const [form] = Form.useForm();
  const r = useQuery({
    queryKey: ['search', query], enabled: !!query,
    queryFn: () => get<{ items: SearchHitView[]; tokens: string[] }>('/api/v1/search', { q: query!.q, types: query!.types?.join(','), bodyId: query!.bodyId, status: query!.status, overdue: query!.overdue }),
  });
  return (
    <Card>
      <Typography.Paragraph type="secondary">{t('assistant.traditionalHelp')}</Typography.Paragraph>
      <Form form={form} layout="inline" onFinish={(v) => setQuery({ ...v, q: v.q ?? '' })} style={{ marginBottom: 16, rowGap: 8 }}>
        <Form.Item name="q" style={{ minWidth: 320 }}><Input prefix={<SearchOutlined />} placeholder={t('assistant.searchPlaceholder')} allowClear /></Form.Item>
        <Form.Item name="types"><Select mode="multiple" allowClear style={{ minWidth: 200 }} placeholder={t('assistant.type')} options={TYPES.map((x) => ({ value: x, label: t(`ai.recordType.${x}`) }))} /></Form.Item>
        <Form.Item name="bodyId"><BodySelect allowClear placeholder={t('assistant.body')} style={{ minWidth: 220 }} /></Form.Item>
        <Form.Item name="status"><Select allowClear style={{ minWidth: 130 }} placeholder={t('common.status')} options={[{ value: 'open', label: t('assistant.open') }]} /></Form.Item>
        <Form.Item name="overdue" valuePropName="checked"><Checkbox>{t('assistant.overdueOnly')}</Checkbox></Form.Item>
        <Form.Item><Button type="primary" htmlType="submit" icon={<SearchOutlined />}>{t('assistant.search')}</Button></Form.Item>
      </Form>
      <ErrorAlert error={r.error} />
      {query && <HitsTable items={r.data?.items ?? []} />}
    </Card>
  );
}

function AskTab() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const { status } = useAiStatus();
  const [q, setQ] = useState('');
  const run = useMutation({
    mutationFn: (query: string) => post<AiResult<{ noAnswer: boolean; answer: AiStatement[] }> & { intent: { intent: string }; facts: AiFact[] }>('/api/v1/ai/ask', { query }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['ai-status'] }),
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const examples = [t('assistant.examples.e1'), t('assistant.examples.e2'), t('assistant.examples.e3')];
  const d = run.data;
  return (
    <Card>
      <NotReady status={status} />
      <Space.Compact style={{ width: '100%', maxWidth: 820 }}>
        <Input size="large" value={q} onChange={(e) => setQ(e.target.value)} placeholder={t('assistant.askPlaceholder')} maxLength={500} onPressEnter={() => q.trim().length >= 3 && run.mutate(q)} />
        <Button size="large" type="primary" icon={<SendOutlined />} loading={run.isPending} disabled={q.trim().length < 3 || !status?.provider.ready} onClick={() => run.mutate(q)}>{t('assistant.ask')}</Button>
      </Space.Compact>
      <div style={{ marginTop: 8 }}>{examples.map((x) => <Tag key={x} className="ai-example" onClick={() => { setQ(x); run.mutate(x); }}>{x}</Tag>)}</div>
      {d && (
        <div className="ai-result" style={{ marginTop: 16 }}>
          <GeneratedBanner testOnly={d.testOnly} provider={d.provider} model={d.model} warnings={d.warnings} />
          <div className="item-meta" style={{ marginBottom: 6 }}>{t('assistant.intentLabel')}: <Tag>{t(`assistant.intent.${d.intent.intent}`)}</Tag></div>
          {d.content.noAnswer ? <Alert type="info" showIcon message={t('assistant.noAnswer')} />
            : <Card size="small" title={t('assistant.answer')}><Statements items={d.content.answer} citations={d.citations} /></Card>}
          <Citations citations={d.citations} />
          {d.facts.length > 0 && (
            <Card size="small" title={t('assistant.facts')} style={{ marginTop: 12 }}>
              <List size="small" dataSource={d.facts} renderItem={(f) => <List.Item><Space align="start"><span className="mono ai-ref">{f.ref}</span>
                <span>{f.link ? <Link to={f.link}>{f.text}</Link> : f.text}</span></Space></List.Item>} />
            </Card>
          )}
        </div>
      )}
    </Card>
  );
}

function SmartSearchTab() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const { status } = useAiStatus();
  const [q, setQ] = useState('');
  const run = useMutation({
    mutationFn: (query: string) => post<{ interpretation: Record<string, unknown>; items: SearchHitView[]; testOnly: boolean; provider: string; model: string; warnings: string[] }>('/api/v1/ai/search', { query }),
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const d = run.data;
  const f = d?.interpretation as { types?: string[]; status?: string | null; overdue?: boolean; bodyName?: string | null; from?: string | null; to?: string | null; keywords?: string[] } | undefined;
  return (
    <Card>
      <NotReady status={status} />
      <Space.Compact style={{ width: '100%', maxWidth: 820 }}>
        <Input size="large" value={q} onChange={(e) => setQ(e.target.value)} placeholder={t('assistant.smartPlaceholder')} maxLength={500} onPressEnter={() => q.trim().length >= 3 && run.mutate(q)} />
        <Button size="large" type="primary" icon={<SearchOutlined />} loading={run.isPending} disabled={q.trim().length < 3 || !status?.provider.ready} onClick={() => run.mutate(q)}>{t('assistant.search')}</Button>
      </Space.Compact>
      {d && f && (
        <div style={{ marginTop: 16 }}>
          <GeneratedBanner testOnly={d.testOnly} compact warnings={d.warnings} />
          <div className="ai-interpretation">
            <Typography.Text type="secondary">{t('assistant.interpretation')}:</Typography.Text>
            {(f.types ?? []).map((x) => <Tag key={x} color="blue">{t(`ai.recordType.${x}`)}</Tag>)}
            {f.overdue && <Tag color="red">{t('assistant.overdueOnly')}</Tag>}
            {f.status === 'open' && <Tag color="gold">{t('assistant.open')}</Tag>}
            {f.bodyName && <Tag color="green">{f.bodyName}</Tag>}
            {f.from && <Tag className="mono">{f.from} → {f.to}</Tag>}
            {(f.keywords ?? []).map((k) => <Tag key={k}>«{k}»</Tag>)}
          </div>
          <HitsTable items={d.items} showProcessable />
        </div>
      )}
    </Card>
  );
}

function ExecutiveTab() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const { status } = useAiStatus();
  const [form] = Form.useForm();
  const run = useMutation({
    mutationFn: (v: { bodyId?: string; from?: string; to?: string }) => post<AiResult & { extra: { from: string; to: string; facts: AiFact[] } }>('/api/v1/ai/executive-summary', {
      bodyId: v.bodyId || undefined, from: v.from || undefined, to: v.to || undefined,
    }),
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const d = run.data;
  return (
    <Card>
      <NotReady status={status} />
      <Form form={form} layout="inline" onFinish={(v) => run.mutate(v)} style={{ rowGap: 8, marginBottom: 16 }}>
        <Form.Item name="bodyId"><BodySelect allowClear placeholder={t('assistant.exec.allBodies')} style={{ minWidth: 240 }} /></Form.Item>
        <Form.Item name="from" label={t('assistant.exec.from')}><Input type="date" /></Form.Item>
        <Form.Item name="to" label={t('assistant.exec.to')}><Input type="date" /></Form.Item>
        <Form.Item><Button type="primary" htmlType="submit" icon={<RobotOutlined />} loading={run.isPending} disabled={!status?.provider.ready}>{t('assistant.exec.generate')}</Button></Form.Item>
      </Form>
      {d && (
        <div className="ai-result">
          <GeneratedBanner testOnly={d.testOnly} provider={d.provider} model={d.model} warnings={d.warnings} />
          <div className="item-meta">{t('assistant.exec.period', { from: d.extra.from, to: d.extra.to })}</div>
          <ExecutiveBody content={d.content} citations={d.citations} />
          <Citations citations={d.citations} />
          <OutputToolbar outputId={d.outputId} onDone={() => run.reset()} />
        </div>
      )}
    </Card>
  );
}

// ============================ إدارة الذكاء الاصطناعي ============================

export function AiAdminPage() {
  const { t, i18n } = useTranslation();
  const { can } = useAuth();
  const { message, modal } = App.useApp();
  const qc = useQueryClient();
  const manage = can('admin.settings.manage');
  const o = useQuery({ queryKey: ['ai-overview'], queryFn: () => get<AiOverview>('/api/v1/admin/ai/overview') });
  const [logFilter, setLogFilter] = useState<{ status?: string; function?: string; page: number }>({ page: 1 });
  const log = useQuery({ queryKey: ['ai-log', logFilter], queryFn: () => get<Paged<AiRequestLog>>('/api/v1/admin/ai/requests', { ...logFilter, pageSize: 15 }) });
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['ai-overview'] }); void qc.invalidateQueries({ queryKey: ['ai-log'] }); };
  const check = useMutation({ mutationFn: () => post<{ status: string; detail: string }>('/api/v1/admin/connectors/ai/check'), onSuccess: (r) => { message.info(`${t(`enums.connector.${r.status}`)} — ${r.detail}`); refresh(); }, onError: (e) => message.error(errorText(e, t('common.error'))) });
  const maintenance = useMutation({ mutationFn: () => post('/api/v1/ai/maintenance/run'), onSuccess: (r) => { message.info(JSON.stringify(r)); refresh(); } });
  const faults = useMutation({ mutationFn: (mode: string) => put('/api/v1/ai/mock/faults', { mode, delayMs: 0, count: 0 }), onSuccess: refresh });
  const [secretOpen, setSecretOpen] = useState(false);
  if (o.isLoading) return <Card loading />;
  if (o.error) return <ErrorAlert error={o.error} />;
  const d = o.data!;
  const s = d.settings;
  const lang = i18n.language;
  const statusColor: Record<string, string> = { succeeded: 'green', held: 'orange', blocked_disabled: 'default', blocked_classification: 'magenta', blocked_quota: 'volcano', no_sources: 'default', provider_unavailable: 'red', timeout: 'red', failed: 'red', invalid_output: 'red', not_configured: 'gold' };
  return (
    <>
      <PageHeader title={t('aiAdmin.title')} subtitle={t('aiAdmin.subtitle')} extra={<Space>
        <Link to="/settings"><Button>{t('aiAdmin.openSettings')}</Button></Link>
        {manage && <Button icon={<ReloadOutlined />} loading={maintenance.isPending} onClick={() => maintenance.mutate()}>{t('aiAdmin.maintenance')}</Button>}
      </Space>} />
      <Row gutter={[16, 16]}>
        <Col xs={24} lg={12}>
          <Card title={<Space><RobotOutlined />{t('aiAdmin.state')}</Space>}>
            <Descriptions column={1} size="small" bordered>
              <Descriptions.Item label={t('aiAdmin.activation')}>{s.enabled ? <Badge status="success" text={t('aiAdmin.enabled')} /> : <Badge status="default" text={t('aiAdmin.disabled')} />}</Descriptions.Item>
              <Descriptions.Item label={t('aiAdmin.provider')}><Space wrap>{(lang === 'en' ? s.providerNameEn : s.providerName) ?? t('settings.enumValues.none')}{s.testOnly && <Tag color="red" icon={<ExperimentOutlined />}>{t('ai.testOnly')}</Tag>}</Space></Descriptions.Item>
              <Descriptions.Item label={t('aiAdmin.readiness')}>{d.readiness.ready ? <Tag color="green">{t('aiAdmin.ready')}</Tag> : <Tag color="orange">{d.readiness.text}</Tag>}</Descriptions.Item>
              <Descriptions.Item label={t('aiAdmin.connection')}><Space wrap>{d.connection ? <><Tag>{t(`enums.connector.${d.connection.status}`)}</Tag><span className="item-meta">{d.connection.detail}</span></> : '—'}
                {manage && <Button size="small" icon={<CloudServerOutlined />} loading={check.isPending} onClick={() => check.mutate()}>{t('settings.check')}</Button>}</Space></Descriptions.Item>
              <Descriptions.Item label={t('aiAdmin.transcription')}>{d.readiness.transcriptionReady ? <Tag color="green">{t('aiAdmin.ready')}</Tag> : <span className="item-meta">{d.readiness.transcriptionText}</span>}</Descriptions.Item>
              <Descriptions.Item label={t('aiAdmin.endpoint')}><span className="mono">{s.baseUrl || '—'}</span> {s.model && <Tag className="mono">{s.model}</Tag>}</Descriptions.Item>
              <Descriptions.Item label={t('aiAdmin.allowedHosts')}>{d.allowedHosts.length ? d.allowedHosts.map((h) => <Tag key={h} className="mono">{h}</Tag>) : <span className="item-meta">{t('aiAdmin.noHosts')}</span>}</Descriptions.Item>
              <Descriptions.Item label={t('aiAdmin.timeout')}>{s.timeoutSeconds} {t('aiAdmin.seconds')}</Descriptions.Item>
            </Descriptions>
            <Alert type="info" showIcon style={{ marginTop: 12 }} message={t('aiAdmin.enabledVsConnected')} />
          </Card>
        </Col>
        <Col xs={24} lg={12}>
          <Card title={<Space><KeyOutlined />{t('aiAdmin.policyAndSecret')}</Space>}>
            <Descriptions column={1} size="small" bordered>
              <Descriptions.Item label={t('aiAdmin.hosting')}>{t(`settings.enumValuesByKey.AI_PROVIDER_HOSTING.${s.hosting}`)}</Descriptions.Item>
              <Descriptions.Item label={t('aiAdmin.maxClass')}>{t(`settings.enumValuesByKey.AI_MAX_CLASSIFICATION.${s.configuredMaxClassification}`)}</Descriptions.Item>
              <Descriptions.Item label={t('aiAdmin.effectiveMax')}><Tag color="purple">{t(`settings.enumValuesByKey.AI_MAX_CLASSIFICATION.${s.effectiveMaxClassification}`)}</Tag></Descriptions.Item>
              <Descriptions.Item label={t('aiAdmin.retention')}>{s.retentionDays} {t('aiAdmin.days')}</Descriptions.Item>
              <Descriptions.Item label={t('aiAdmin.secret')}><Space wrap>
                <Tag color={d.secret.source === 'none' || d.secret.source === 'unreadable' ? 'orange' : 'green'}>{t(`aiAdmin.secretSource.${d.secret.source}`)}</Tag>
                {d.secret.fingerprint && <span className="mono item-meta">{t('aiAdmin.fingerprint')}: {d.secret.fingerprint}</span>}
                {d.secret.updatedAt && <span className="item-meta">{d.secret.updatedBy} — {formatDateTime(d.secret.updatedAt, lang)}</span>}
                {manage && d.secret.source !== 'environment' && <Button size="small" icon={<KeyOutlined />} disabled={!d.secret.canStore} onClick={() => setSecretOpen(true)}>{t('aiAdmin.setSecret')}</Button>}
                {manage && d.secret.source === 'database' && <Button size="small" danger onClick={() => modal.confirm({
                  title: t('aiAdmin.clearSecret'), okButtonProps: { danger: true }, okText: t('common.confirm'), cancelText: t('common.cancel'),
                  onOk: () => post('/api/v1/admin/secrets/AI_API_KEY/clear', { reason: t('aiAdmin.clearReason') }).then(refresh),
                })}>{t('aiAdmin.clearSecret')}</Button>}
              </Space>
              {!d.secret.canStore && d.secret.source !== 'environment' && <div className="item-meta" style={{ marginTop: 4 }}>{t('aiAdmin.storeUnavailable')}</div>}</Descriptions.Item>
            </Descriptions>
            <Typography.Paragraph type="secondary" style={{ marginTop: 12, marginBottom: 0 }}>{t('aiAdmin.secretHelp')}</Typography.Paragraph>
          </Card>
        </Col>
        <Col xs={24}>
          <Card title={t('aiAdmin.usage', { period: d.usage.period })}>
            <Row gutter={[24, 16]}>
              <Col xs={12} md={6}><Statistic title={t('aiAdmin.today')} value={d.usage.todayRequests} suffix={<span className="item-meta">/ {s.dailyPerUser} {t('aiAdmin.perUser')}</span>} /></Col>
              <Col xs={24} md={9}>
                <div className="item-meta">{t('aiAdmin.monthRequests')}</div>
                <Progress percent={Math.min(100, d.usage.requestsPct)} status={d.usage.requestsPct >= 100 ? 'exception' : d.usage.requestsPct >= d.usage.alertPercent ? 'active' : 'normal'}
                  format={() => `${d.usage.monthRequests} / ${s.monthlyRequests}`} strokeColor={d.usage.requestsPct >= d.usage.alertPercent ? '#d48806' : undefined} />
              </Col>
              <Col xs={24} md={9}>
                <div className="item-meta">{t('aiAdmin.monthTokens')}</div>
                <Progress percent={Math.min(100, d.usage.tokensPct)} status={d.usage.tokensPct >= 100 ? 'exception' : 'normal'} format={() => `${d.usage.monthTokens.toLocaleString('en-US')} / ${s.monthlyTokens.toLocaleString('en-US')}`}
                  strokeColor={d.usage.tokensPct >= d.usage.alertPercent ? '#d48806' : undefined} />
              </Col>
            </Row>
            <div className="item-meta" style={{ marginTop: 8 }}>{t('aiAdmin.alertAt', { p: d.usage.alertPercent })}</div>
            {d.alerts.length > 0 && <List size="small" style={{ marginTop: 8 }} dataSource={d.alerts} renderItem={(a) => (
              <List.Item><Space><WarningOutlined style={{ color: a.threshold >= 100 ? '#cf1322' : '#d48806' }} />
                {t('aiAdmin.alertLine', { metric: t(`aiAdmin.metric.${a.metric}`), threshold: a.threshold, value: a.value, limit: a.limit })}
                <span className="item-meta">{formatDateTime(a.at, lang)}</span></Space></List.Item>
            )} />}
          </Card>
        </Col>
        <Col xs={24} lg={10}>
          <Card title={t('aiAdmin.functions')} size="small">
            <List size="small" dataSource={d.functions} renderItem={(f) => (
              <List.Item><Space><Badge status={f.enabled ? 'success' : 'default'} />{t(`ai.functions.${f.code}`)}</Space><span className="mono item-meta">{f.flag}</span></List.Item>
            )} />
          </Card>
        </Col>
        <Col xs={24} lg={14}>
          <Card title={t('aiAdmin.operations')} size="small">
            <Row gutter={16} style={{ marginBottom: 12 }}>
              <Col span={6}><Statistic title={t('aiAdmin.inflight')} value={d.inflight} /></Col>
              <Col span={6}><Statistic title={t('aiAdmin.heldOutputs')} value={d.outputs.held} /></Col>
              <Col span={6}><Statistic title={t('aiAdmin.keptOutputs')} value={d.outputs.kept} /></Col>
              <Col span={6}><Statistic title={t('aiAdmin.jobs')} value={`${d.jobs.active} / ${d.jobs.held} / ${d.jobs.failed}`} /></Col>
            </Row>
            <Table size="small" rowKey={(r) => r.function + r.status} pagination={false} dataSource={d.byFunction} scroll={{ y: 220 }} columns={[
              { title: t('aiAdmin.function'), dataIndex: 'function', render: (v) => t(`ai.functions.${v}`) },
              { title: t('common.status'), dataIndex: 'status', render: (v) => <Tag color={statusColor[v]}>{t(`aiAdmin.logStatus.${v}`)}</Tag> },
              { title: t('aiAdmin.count'), dataIndex: 'count', width: 70 },
              { title: t('aiAdmin.tokens'), dataIndex: 'tokens', width: 90 },
              { title: 'ms', dataIndex: 'avgMs', width: 70 },
            ]} />
            {d.mock && manage && (
              <div className="ai-mock-faults">
                <Typography.Text type="secondary"><ExperimentOutlined /> {t('aiAdmin.mockFaults')}: </Typography.Text>
                <Select size="small" value={d.mock.mode} style={{ minWidth: 160 }} onChange={(v) => faults.mutate(v)}
                  options={['none', 'unavailable', 'timeout', 'error', 'invalid_credentials', 'invalid_output', 'hallucinate'].map((x) => ({ value: x, label: t(`aiAdmin.fault.${x}`) }))} />
              </div>
            )}
          </Card>
        </Col>
        <Col xs={24}>
          <Card title={t('aiAdmin.log')} extra={<Space>
            <Select allowClear size="small" style={{ minWidth: 180 }} placeholder={t('aiAdmin.function')} value={logFilter.function} onChange={(v) => setLogFilter((f) => ({ ...f, function: v, page: 1 }))}
              options={d.functions.map((f) => ({ value: f.code, label: t(`ai.functions.${f.code}`) }))} />
            <Select allowClear size="small" style={{ minWidth: 180 }} placeholder={t('common.status')} value={logFilter.status} onChange={(v) => setLogFilter((f) => ({ ...f, status: v, page: 1 }))}
              options={Object.keys(statusColor).map((x) => ({ value: x, label: t(`aiAdmin.logStatus.${x}`) }))} />
          </Space>}>
            <Typography.Paragraph type="secondary">{t('aiAdmin.logHelp')}</Typography.Paragraph>
            <Table<AiRequestLog> size="small" rowKey="id" dataSource={log.data?.items} loading={log.isLoading}
              pagination={{ current: logFilter.page, total: log.data?.total, pageSize: 15, onChange: (page) => setLogFilter((f) => ({ ...f, page })) }} columns={[
                { title: t('ai.when'), dataIndex: 'at', width: 170, render: (v) => <span style={{ whiteSpace: 'nowrap' }}>{formatDateTime(v, lang)}</span> },
                { title: t('aiAdmin.user'), dataIndex: 'user', width: 150 },
                { title: t('aiAdmin.function'), dataIndex: 'function', width: 230, render: (v) => <span style={{ whiteSpace: 'nowrap' }}>{t(`ai.functions.${v}`)}</span> },
                { title: t('common.status'), dataIndex: 'status', render: (v, r) => <Space direction="vertical" size={0}><Tag color={statusColor[v]}>{t(`aiAdmin.logStatus.${v}`)}</Tag>{r.errorCode && <span className="mono item-meta">{r.errorCode}</span>}</Space> },
                { title: 'ms', dataIndex: 'durationMs', width: 70, render: (v) => v ?? '—' },
                { title: t('aiAdmin.tokens'), width: 120, render: (_, r) => r.inputTokens == null ? '—' : <span>{r.inputTokens} + {r.outputTokens ?? 0}{r.usageEstimated && <Tooltip title={t('aiAdmin.estimated')}> ≈</Tooltip>}</span> },
                { title: t('aiAdmin.sources'), width: 110, render: (_, r) => <span>{r.sources ?? '—'}{r.excluded > 0 && <Tag color="magenta" style={{ marginInlineStart: 4 }}>-{r.excluded}</Tag>}{r.redactions > 0 && <Tooltip title={t('aiAdmin.redactions')}><Tag style={{ marginInlineStart: 4 }}>✱{r.redactions}</Tag></Tooltip>}</span> },
                { title: t('aiAdmin.classification'), dataIndex: 'classification', width: 100, render: (v) => v ? t(`settings.enumValuesByKey.AI_MAX_CLASSIFICATION.${v}`, { defaultValue: v }) : '—' },
                { title: t('aiAdmin.provider'), dataIndex: 'provider', width: 130, render: (v) => <span className="mono">{v ?? '—'}</span> },
              ]} />
          </Card>
        </Col>
      </Row>
      {secretOpen && <SecretModal onClose={(saved) => { setSecretOpen(false); if (saved) refresh(); }} />}
    </>
  );
}

function SecretModal({ onClose }: { onClose: (saved: boolean) => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const save = useMutation({
    mutationFn: (v: { value: string; reason: string }) => put('/api/v1/admin/secrets/AI_API_KEY', v),
    onSuccess: () => { message.success(t('aiAdmin.secretSaved')); onClose(true); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('aiAdmin.setSecret')} onCancel={() => onClose(false)} onOk={() => form.submit()} confirmLoading={save.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('aiAdmin.secretHelp')} />
      <Form form={form} layout="vertical" onFinish={(v) => save.mutate(v)}>
        <Form.Item name="value" label={t('aiAdmin.secretValue')} rules={[{ required: true, min: 8 }]}><Input.Password autoComplete="off" /></Form.Item>
        <Form.Item name="reason" label={t('common.reason')} rules={[{ required: true, min: 5 }]}><Input.TextArea rows={2} /></Form.Item>
      </Form>
    </Modal>
  );
}
