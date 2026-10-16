import { App, Button, Descriptions, Drawer, Dropdown, Form, Input, InputNumber, Modal, Radio, Select, Space, Table, Tag, Typography } from 'antd';
import { ArrowDownOutlined, ArrowUpOutlined, ImportOutlined, MoreOutlined, PaperClipOutlined, PlusOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { get, patch, post, put, type Paged } from '../../api/client';
import type { AgendaItem, Meeting, PendingItem } from '../../api/types';
import { ErrorAlert, StatusTag, errorText } from '../../components/common';
import { Attachments, useReasonPrompt } from '../../components/governance';
import { BodySelect, OrgUnitSelect } from '../../components/pickers';

const PREPARING = ['draft', 'scheduled', 'agenda_published'];

export function AgendaEditor({ meeting, agenda, permissions, onChange }: { meeting: Meeting; agenda: AgendaItem[]; permissions: string[]; onChange: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const reason = useReasonPrompt();
  const [editing, setEditing] = useState<AgendaItem | 'new' | null>(null);
  const [discussing, setDiscussing] = useState<AgendaItem | null>(null);
  const [deferring, setDeferring] = useState<AgendaItem | null>(null);
  const [details, setDetails] = useState<AgendaItem | null>(null);
  const [importing, setImporting] = useState(false);
  const manage = permissions.includes('meeting.manage');
  const preparing = PREPARING.includes(meeting.status);
  const live = meeting.status === 'in_progress';
  const canEditContent = manage && (preparing || live);
  const reorder = useMutation({
    mutationFn: (ids: string[]) => put(`/api/v1/meetings/${meeting.id}/agenda-items/order`, { itemIds: ids }),
    onSuccess: onChange,
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const move = (index: number, delta: number) => {
    const ids = agenda.map((a) => a.id);
    const [x] = ids.splice(index, 1);
    ids.splice(index + delta, 0, x);
    reorder.mutate(ids);
  };
  const totalMinutes = agenda.filter((a) => a.status !== 'withdrawn').reduce((s, a) => s + (a.estimatedMinutes ?? 0), 0);

  const actions = (a: AgendaItem) => {
    const open = a.status === 'pending';
    return [
      { key: 'details', label: t('agenda.details'), onClick: () => setDetails(a) },
      ...(canEditContent && (open || a.status === 'discussed') ? [{ key: 'edit', label: t('common.edit'), onClick: () => setEditing(a) }] : []),
      ...(manage && ['in_progress', 'concluded'].includes(meeting.status) && (open || a.status === 'discussed') ? [{ key: 'discuss', label: t('agenda.recordDiscussion'), onClick: () => setDiscussing(a) }] : []),
      ...(manage && open && !['cancelled', 'closed', 'postponed'].includes(meeting.status) ? [{ key: 'defer', label: t('agenda.defer'), onClick: () => setDeferring(a) }] : []),
      ...(manage && (open || a.status === 'discussed') ? [{
        key: 'refer', label: t('agenda.refer'),
        onClick: () => reason.prompt({
          title: `${t('agenda.refer')} — ${a.titleAr}`, description: t('agenda.referHelp'),
          extra: <Form.Item name="targetBodyId" label={t('agenda.targetBody')} rules={[{ required: true }]}><BodySelect activeOnly style={{ width: '100%' }} /></Form.Item>,
          onSubmit: async (v) => { await post(`/api/v1/agenda-items/${a.id}/refer`, v); onChange(); },
        }),
      }] : []),
      ...(manage && open && (preparing || live) ? [{
        key: 'withdraw', danger: true, label: t('agenda.withdraw'),
        onClick: () => reason.prompt({ title: `${t('agenda.withdraw')} — ${a.titleAr}`, danger: true, onSubmit: async (v) => { await post(`/api/v1/agenda-items/${a.id}/withdraw`, v); onChange(); } }),
      }] : []),
    ];
  };

  return (
    <>
      <div className="toolbar" style={{ justifyContent: 'space-between' }}>
        <Typography.Text type="secondary">{t('agenda.summary', { count: agenda.filter((a) => a.status !== 'withdrawn').length, minutes: totalMinutes })}</Typography.Text>
        {manage && preparing && (
          <Space>
            <Button icon={<ImportOutlined />} onClick={() => setImporting(true)}>{t('agenda.importPending')}</Button>
            <Button type="primary" icon={<PlusOutlined />} onClick={() => setEditing('new')}>{t('meetings.addItem')}</Button>
          </Space>
        )}
      </div>
      <Table<AgendaItem>
        rowKey="id" dataSource={agenda} pagination={false} scroll={{ x: 960 }} loading={reorder.isPending}
        rowClassName={(a) => (['withdrawn', 'deferred', 'referred'].includes(a.status) ? 'ended-row' : '')}
        expandable={{
          rowExpandable: (a) => !!(a.descriptionAr || a.discussionSummary || a.recommendationText || a.memoText),
          expandedRowRender: (a) => (
            <Descriptions size="small" column={1}>
              {a.descriptionAr && <Descriptions.Item label={t('agenda.description')}><div style={{ whiteSpace: 'pre-wrap' }}>{a.descriptionAr}</div></Descriptions.Item>}
              {a.memoText && <Descriptions.Item label={t('meetings.memo')}><div style={{ whiteSpace: 'pre-wrap' }}>{a.memoText}</div></Descriptions.Item>}
              {a.discussionSummary && <Descriptions.Item label={t('agenda.discussionSummary')}><div style={{ whiteSpace: 'pre-wrap' }}>{a.discussionSummary}</div></Descriptions.Item>}
              {a.recommendationText && <Descriptions.Item label={t('agenda.recommendation')}>{a.recommendationText}</Descriptions.Item>}
            </Descriptions>
          ),
        }}
        columns={[
          {
            title: '#', width: 90,
            render: (_, a, i) => (
              <Space size={2}>
                <strong>{a.position}</strong>
                {manage && (preparing || live) && <>
                  <Button size="small" type="text" icon={<ArrowUpOutlined />} disabled={i === 0} onClick={() => move(i, -1)} aria-label={t('agenda.moveUp')} />
                  <Button size="small" type="text" icon={<ArrowDownOutlined />} disabled={i === agenda.length - 1} onClick={() => move(i, 1)} aria-label={t('agenda.moveDown')} />
                </>}
              </Space>
            ),
          },
          {
            title: t('common.title'),
            render: (_, a) => (
              <div>
                <a onClick={() => setDetails(a)}>{a.titleAr}</a>
                <div className="item-meta">
                  {a.carriedFromMeetingId && <span>{t('agenda.carriedFrom')} <Link to={`/meetings/${a.carriedFromMeetingId}`}>{a.carriedFromReference ?? '—'}</Link> · </span>}
                  {a.forwardedToMeetingId && <span>{t('agenda.forwardedTo')} <Link to={`/meetings/${a.forwardedToMeetingId}`}>{a.forwardedToReference ?? '—'}</Link> · </span>}
                  {a.status === 'referred' && a.pendingBodyName && <span>{t('agenda.referredTo')} {a.pendingBodyName} · </span>}
                  {a.statusReason && <span>{t('common.reason')}: {a.statusReason}</span>}
                </div>
              </div>
            ),
          },
          { title: t('meetings.itemType'), dataIndex: 'itemType', width: 110, render: (v) => t(`enums.itemType.${v}`) },
          { title: t('meetings.presenter'), render: (_, a) => <div>{a.presenterText ?? '—'}<div className="item-meta">{a.ownerOrgUnitName ?? a.ownerText ?? ''}</div></div> },
          { title: t('agenda.minutesShort'), dataIndex: 'estimatedMinutes', width: 80, render: (v) => v ?? '—' },
          { title: t('agenda.links'), width: 140, render: (_, a) => (
            <Space size={4} wrap>
              {a.attachmentCount > 0 && <Tag icon={<PaperClipOutlined />}>{a.attachmentCount}</Tag>}
              {a.resolutionCount > 0 && <Tag color="green">{t('agenda.resolutions')}: {a.resolutionCount}</Tag>}
              {a.taskCount > 0 && <Tag color="blue">{t('agenda.tasks')}: {a.taskCount}</Tag>}
            </Space>
          ) },
          { title: t('common.status'), dataIndex: 'status', width: 110, render: (v) => <StatusTag group="itemStatus" value={v} /> },
          { title: '', width: 56, fixed: 'right', render: (_, a) => <Dropdown menu={{ items: actions(a) }} trigger={['click']}><Button size="small" icon={<MoreOutlined />} aria-label={t('common.actions')} /></Dropdown> },
        ]}
      />
      {editing && <ItemModal meetingId={meeting.id} item={editing === 'new' ? undefined : editing} onClose={() => { setEditing(null); onChange(); }} />}
      {discussing && <DiscussionModal item={discussing} onClose={() => { setDiscussing(null); onChange(); }} />}
      {deferring && <DeferModal meeting={meeting} item={deferring} onClose={() => { setDeferring(null); onChange(); }} />}
      {importing && <ImportModal meeting={meeting} onClose={() => { setImporting(false); onChange(); }} />}
      {details && <ItemDrawer item={details} canUpload={permissions.includes('document.upload')} onClose={() => setDetails(null)} />}
      {reason.node}
    </>
  );
}

function ItemModal({ meetingId, item, onClose }: { meetingId: string; item?: AgendaItem; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => (item ? patch(`/api/v1/agenda-items/${item.id}`, v) : post(`/api/v1/meetings/${meetingId}/agenda-items`, v)),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open width={720} title={item ? t('agenda.editItem') : t('meetings.addItem')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending}
      okText={item ? t('common.save') : t('common.add')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)} initialValues={item ?? { itemType: 'for_decision' }}>
        <Form.Item name="titleAr" label={t('common.title')} rules={[{ required: true, max: 400 }]}><Input /></Form.Item>
        <Form.Item name="descriptionAr" label={t('agenda.description')}><Input.TextArea rows={3} maxLength={10000} /></Form.Item>
        <div className="form-row">
          <Form.Item name="itemType" label={t('meetings.itemType')}>
            <Select options={['for_decision', 'for_information', 'for_discussion'].map((v) => ({ value: v, label: t(`enums.itemType.${v}`) }))} />
          </Form.Item>
          <Form.Item name="estimatedMinutes" label={t('meetings.duration')}><InputNumber min={1} max={480} style={{ width: '100%' }} /></Form.Item>
          <Form.Item name="presenterText" label={t('meetings.presenter')}><Input /></Form.Item>
        </div>
        <div className="form-row">
          <Form.Item name="ownerOrgUnitId" label={t('agenda.ownerUnit')}><OrgUnitSelect allowClear style={{ width: '100%' }} /></Form.Item>
          <Form.Item name="ownerText" label={t('agenda.ownerText')}><Input /></Form.Item>
        </div>
        <Form.Item name="memoText" label={t('meetings.memo')}><Input.TextArea rows={4} maxLength={20000} /></Form.Item>
      </Form>
    </Modal>
  );
}

function DiscussionModal({ item, onClose }: { item: AgendaItem; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => patch(`/api/v1/agenda-items/${item.id}`, v),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open width={720} title={`${t('agenda.recordDiscussion')} — ${item.titleAr}`} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)} initialValues={{ discussionSummary: item.discussionSummary, recommendationText: item.recommendationText, status: 'discussed' }}>
        <Form.Item name="discussionSummary" label={t('agenda.discussionSummary')} rules={[{ required: true }]}><Input.TextArea rows={5} /></Form.Item>
        <Form.Item name="recommendationText" label={t('agenda.recommendation')}><Input.TextArea rows={3} /></Form.Item>
        <Form.Item name="status" label={t('common.status')}>
          <Radio.Group options={['discussed', 'pending'].map((v) => ({ value: v, label: t(`enums.itemStatus.${v}`) }))} />
        </Form.Item>
        <Typography.Text type="secondary">{t('agenda.decisionsHint')}</Typography.Text>
      </Form>
    </Modal>
  );
}

function DeferModal({ meeting, item, onClose }: { meeting: Meeting; item: AgendaItem; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const target = Form.useWatch('target', form) as string;
  const later = useQuery({
    queryKey: ['meetings', { bodyId: meeting.bodyId, from: meeting.startsAt }],
    queryFn: () => get<Paged<Meeting>>('/api/v1/meetings', { bodyId: meeting.bodyId, from: meeting.startsAt, pageSize: 50 }),
  });
  const options = (later.data?.items ?? []).filter((m) => m.id !== meeting.id && PREPARING.includes(m.status) && m.startsAt > meeting.startsAt)
    .map((m) => ({ value: m.id, label: `${m.referenceNo ?? '—'} — ${m.titleAr} (${new Date(m.startsAt).toLocaleDateString(i18n.language === 'ar' ? 'ar-SA-u-ca-gregory-nu-latn' : 'en-GB')})` }));
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post(`/api/v1/agenda-items/${item.id}/defer`, { reason: v.reason, targetMeetingId: v.target === 'meeting' ? v.targetMeetingId : undefined }),
    onSuccess: () => { message.success(t('agenda.deferred')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={`${t('agenda.defer')} — ${item.titleAr}`} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('agenda.defer')} cancelText={t('common.cancel')}>
      <Form form={form} layout="vertical" initialValues={{ target: 'meeting' }} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="target" label={t('agenda.deferTo')}>
          <Radio.Group options={[{ value: 'meeting', label: t('agenda.toLaterMeeting') }, { value: 'pending', label: t('agenda.toPendingList') }]} />
        </Form.Item>
        {target === 'meeting' && (
          <Form.Item name="targetMeetingId" label={t('agenda.laterMeeting')} rules={[{ required: true }]} extra={!options.length ? t('agenda.noLaterMeeting') : undefined}>
            <Select options={options} loading={later.isLoading} />
          </Form.Item>
        )}
        <Form.Item name="reason" label={t('common.reason')} rules={[{ required: true, min: 3 }]}><Input.TextArea rows={2} /></Form.Item>
      </Form>
    </Modal>
  );
}

function ImportModal({ meeting, onClose }: { meeting: Meeting; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [selected, setSelected] = useState<string[]>([]);
  const q = useQuery({ queryKey: ['pending-items', meeting.bodyId, 'pending'], queryFn: () => get<PendingItem[]>(`/api/v1/bodies/${meeting.bodyId}/pending-items`, { status: 'pending' }) });
  const m = useMutation({
    mutationFn: () => post(`/api/v1/meetings/${meeting.id}/agenda-items/import`, { pendingItemIds: selected }),
    onSuccess: () => { message.success(t('agenda.imported')); void qc.invalidateQueries({ queryKey: ['pending-items', meeting.bodyId] }); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open width={760} title={t('agenda.importPending')} onCancel={onClose} onOk={() => m.mutate()} okButtonProps={{ disabled: !selected.length }} confirmLoading={m.isPending}
      okText={t('agenda.importSelected', { count: selected.length })} cancelText={t('common.cancel')}>
      <ErrorAlert error={q.error} />
      <Table<PendingItem> rowKey="id" size="small" loading={q.isLoading} dataSource={q.data} pagination={false} locale={{ emptyText: t('pending.none') }}
        rowSelection={{ selectedRowKeys: selected, onChange: (k) => setSelected(k as string[]) }}
        columns={[
          { title: t('common.title'), dataIndex: 'titleAr' },
          { title: t('common.type'), dataIndex: 'kind', render: (v) => <Tag color={v === 'referred' ? 'purple' : 'orange'}>{t(`enums.pendingKind.${v}`)}</Tag> },
          { title: t('pending.source'), render: (_, p) => `${p.sourceBodyName} — ${p.sourceMeetingReference ?? p.sourceMeetingTitle}` },
          { title: t('common.reason'), dataIndex: 'reason' },
        ]} />
    </Modal>
  );
}

function ItemDrawer({ item, canUpload, onClose }: { item: AgendaItem; canUpload: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const q = useQuery({
    queryKey: ['agenda-item', item.id],
    queryFn: () => get<{ item: AgendaItem; resolutions: { id: string; number?: string; titleAr: string; status: string }[]; tasks: { id: string; referenceNo?: string; titleAr: string; status: string; dueDate?: string }[] }>(`/api/v1/agenda-items/${item.id}`),
  });
  const it = q.data?.item ?? item;
  return (
    <Drawer open width={640} title={`${it.position}. ${it.titleAr}`} onClose={onClose}>
      <ErrorAlert error={q.error} />
      <Descriptions size="small" column={1} bordered>
        <Descriptions.Item label={t('common.status')}><StatusTag group="itemStatus" value={it.status} /> {it.statusReason}</Descriptions.Item>
        <Descriptions.Item label={t('meetings.itemType')}>{t(`enums.itemType.${it.itemType}`)}</Descriptions.Item>
        <Descriptions.Item label={t('meetings.presenter')}>{it.presenterText ?? '—'}</Descriptions.Item>
        <Descriptions.Item label={t('agenda.owner')}>{it.ownerOrgUnitName ?? it.ownerText ?? '—'}</Descriptions.Item>
        <Descriptions.Item label={t('meetings.duration')}>{it.estimatedMinutes ?? '—'}</Descriptions.Item>
        <Descriptions.Item label={t('agenda.description')}><div style={{ whiteSpace: 'pre-wrap' }}>{it.descriptionAr ?? '—'}</div></Descriptions.Item>
        <Descriptions.Item label={t('agenda.discussionSummary')}><div style={{ whiteSpace: 'pre-wrap' }}>{it.discussionSummary ?? '—'}</div></Descriptions.Item>
        <Descriptions.Item label={t('agenda.recommendation')}>{it.recommendationText ?? '—'}</Descriptions.Item>
        <Descriptions.Item label={t('agenda.resolutions')}>
          {q.data?.resolutions.length ? q.data.resolutions.map((r) => <div key={r.id}><Link to="/resolutions">{r.number ?? '—'}</Link> {r.titleAr} <StatusTag group="resolutionStatus" value={r.status} /></div>) : '—'}
        </Descriptions.Item>
        <Descriptions.Item label={t('agenda.tasks')}>
          {q.data?.tasks.length ? q.data.tasks.map((x) => <div key={x.id}><Link to={`/tasks/${x.id}`}>{x.referenceNo ?? '—'}</Link> {x.titleAr} <StatusTag group="taskStatus" value={x.status} /></div>) : '—'}
        </Descriptions.Item>
      </Descriptions>
      <Typography.Title level={5} style={{ marginTop: 16 }}><PaperClipOutlined /> {t('bodies.tabs.attachments')}</Typography.Title>
      <Attachments ownerType="agenda_item" ownerId={item.id} canUpload={canUpload} purposes={['agenda_memo', 'attachment']} />
    </Drawer>
  );
}
