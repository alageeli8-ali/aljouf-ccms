// المراجعة: ملاحظات الأعضاء، والتوقيعات وسجل الاعتماد، والإصدارات ومقارنتها.
import { App, Button, Card, Col, Empty, Form, Input, Modal, Row, Select, Space, Table, Tag, Timeline, Typography } from 'antd';
import { CheckOutlined, CloseOutlined, DiffOutlined, FilePdfOutlined } from '@ant-design/icons';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { get, newRequestKey, post, postOnce } from '../../api/client';
import type { MinutesComment, MinutesDetail, MinutesSnapshot, MinutesVersion } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorAlert, StatusTag, errorText } from '../../components/common';
import { ESignPanel } from '../esign/ESign';
import { useReasonPrompt } from '../../components/governance';
import { formatDateTime } from '../../lib/format';

export function CommentsPanel({ detail, onChange }: { detail: MinutesDetail; onChange: () => void }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const { me } = useAuth();
  const id = detail.minutes.id;
  const [form] = Form.useForm();
  const [key, setKey] = useState(newRequestKey);
  const reason = useReasonPrompt();
  const q = useQuery({ queryKey: ['minutes-comments', id], queryFn: () => get<MinutesComment[]>(`/api/v1/minutes/${id}/comments`) });
  const add = useMutation({
    mutationFn: (v: { anchor: string; text: string }) => postOnce(`/api/v1/minutes/${id}/comments`, v, key),
    onSuccess: () => { message.success(t('minutes.commentAdded')); form.resetFields(['text']); setKey(newRequestKey()); void q.refetch(); onChange(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const resolve = useMutation({
    mutationFn: ({ cid, status, note }: { cid: string; status: string; note?: string }) => post(`/api/v1/minutes/comments/${cid}/resolve`, { status, note }),
    onSuccess: () => { void q.refetch(); onChange(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const anchors = [
    ...detail.template.map((s) => ({ value: s.key, label: s.titleAr })),
    ...detail.snapshot.resolutions.map((r) => ({ value: `resolution:${r.id}`, label: `${t('minutes.decision')}: ${r.titleAr}` })),
  ];
  const anchorLabel = (a: string) => anchors.find((x) => x.value === a)?.label ?? a;
  return (
    <>
      {detail.actions.canComment && (
        <Card size="small" style={{ marginBottom: 12 }}>
          <Form form={form} layout="vertical" initialValues={{ anchor: detail.template[0]?.key }} onFinish={(v) => add.mutate(v)}>
            <Row gutter={12}>
              <Col xs={24} md={8}><Form.Item name="anchor" label={t('minutes.commentOn')} rules={[{ required: true }]}><Select options={anchors} /></Form.Item></Col>
              <Col xs={24} md={16}><Form.Item name="text" label={t('minutes.comment')} rules={[{ required: true, min: 2 }]}><Input.TextArea autoSize={{ minRows: 2, maxRows: 6 }} /></Form.Item></Col>
            </Row>
            <Button type="primary" htmlType="submit" loading={add.isPending}>{t('minutes.addComment')}</Button>
          </Form>
        </Card>
      )}
      <ErrorAlert error={q.error} />
      {!q.isLoading && !q.data?.length && <Empty description={t('minutes.noComments')} />}
      <Space direction="vertical" style={{ width: '100%' }}>
        {(q.data ?? []).map((c) => (
          <Card key={c.id} size="small" className={`comment-card ${c.status}`}
            title={<Space wrap><b>{c.author}</b><Tag>{anchorLabel(c.anchor)}</Tag>{c.versionNo && <Tag>v{c.versionNo}</Tag>}<StatusTag group="commentStatus" value={c.status} /></Space>}
            extra={<span className="item-meta">{formatDateTime(c.createdAt, i18n.language, me?.user.calendar)}</span>}>
            <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', marginBottom: 4 }}>{c.text}</Typography.Paragraph>
            {c.status !== 'open' && <Typography.Text type="secondary">{c.resolver}: {c.resolutionNote ?? t(`enums.commentStatus.${c.status}`)}</Typography.Text>}
            {c.status === 'open' && detail.actions.canResolveComments && (
              <Space>
                <Button size="small" icon={<CheckOutlined />} onClick={() => resolve.mutate({ cid: c.id, status: 'accepted', note: t('minutes.acceptedNote') })}>{t('minutes.accept')}</Button>
                <Button size="small" icon={<CloseOutlined />} onClick={() => reason.prompt({
                  title: t('minutes.reject'), description: t('minutes.rejectWhy'),
                  onSubmit: (v) => resolve.mutateAsync({ cid: c.id, status: 'rejected', note: v.reason as string }),
                })}>{t('minutes.reject')}</Button>
              </Space>
            )}
          </Card>
        ))}
      </Space>
      {reason.node}
    </>
  );
}

export function SignaturesPanel({ detail, onChange }: { detail: MinutesDetail; onChange?: () => void }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const dt = (v?: string | null) => formatDateTime(v, i18n.language, me?.user.calendar);
  return (
    <>
      <Typography.Title level={5}>{t('esign.panelTitle')}</Typography.Title>
      <div style={{ marginBottom: 20 }}><ESignPanel minutesId={detail.minutes.id} onChanged={onChange} /></div>
      <Typography.Title level={5}>{t('minutes.signoffs')}</Typography.Title>
      <Table size="small" rowKey="id" pagination={false} dataSource={detail.signoffs} locale={{ emptyText: t('minutes.noSignoffs') }}
        rowClassName={(s) => (s.status === 'invalidated' ? 'ended-row' : '')}
        columns={[
          { title: t('common.name'), dataIndex: 'name' },
          { title: t('minutes.version'), dataIndex: 'versionNo', width: 70 },
          { title: t('minutes.method'), render: (_, s) => <Space direction="vertical" size={0}><span>{t(`enums.signoffMethod.${s.method}`)}</span>{s.methodReason && <span className="item-meta">{t(`minutes.methodReason.${s.methodReason.split(' ')[0]}`, { defaultValue: s.methodReason })}</span>}</Space> },
          { title: t('common.status'), dataIndex: 'status', render: (v, s) => <Space direction="vertical" size={0}><StatusTag group="signoffStatus" value={v} />{s.invalidatedReason && <span className="item-meta">{s.invalidatedReason}</span>}</Space> },
          { title: t('common.date'), dataIndex: 'signedAt', render: dt },
          { title: t('minutes.reservationShort'), dataIndex: 'reservation', render: (v) => v ?? '—' },
          { title: t('minutes.recordedBy'), dataIndex: 'recordedBy', render: (v, s) => v && s.method === 'wet_signature' ? v : '—' },
        ]} />
      <Typography.Title level={5} style={{ marginTop: 20 }}>{t('minutes.approvalLog')}</Typography.Title>
      {detail.approvals.length === 0 ? <Empty description={t('minutes.notYetReviewed')} /> : (
        <Timeline items={detail.approvals.map((a) => ({
          key: a.id,
          color: a.decision === 'returned' ? 'red' : a.decision === 'approved' ? 'green' : a.decision === 'amendment_opened' ? 'orange' : 'blue',
          children: (
            <div>
              <Typography.Text strong>{t(`enums.approvalDecision.${a.decision}`)}</Typography.Text> — {t(`enums.minutesStatus.${a.step}`)}
              {a.versionNo && <Tag style={{ marginInlineStart: 6 }}>v{a.versionNo}</Tag>}
              <div className="item-meta">
                {a.actor}{a.capacity && ` (${t(`enums.capacity.${a.capacity.split(':')[0]}`, { defaultValue: a.capacity })})`}
                {a.onBehalfOf && ` — ${t('minutes.onBehalfOf', { name: a.onBehalfOf })}`}
                {a.method && ` — ${t(`enums.approvalMethod.${a.method}`)}`} — {dt(a.createdAt)}
              </div>
              {a.externalApprover && <div>{t('minutes.externalApprover')}: <b>{a.externalApprover}</b> — {t('minutes.reference')}: <span className="mono">{a.externalReference}</span> — {a.externalDate}</div>}
              {a.evidenceDocumentId && <a href={`/api/v1/documents/${a.evidenceDocumentId}/content`} target="_blank" rel="noreferrer">{t('minutes.evidence')}</a>}
              {a.note && <Typography.Paragraph style={{ margin: 0 }}>{a.note}</Typography.Paragraph>}
              {a.contentHash && <span className="mono item-meta">{a.contentHash.slice(0, 16)}…</span>}
            </div>
          ),
        }))} />
      )}
    </>
  );
}

interface VersionView { version: { versionNo: number; content: MinutesDetail['minutes']['content']; snapshot: MinutesSnapshot; contentHash: string; changeSummary?: string | null }; issuedResolutions: { id: string; number?: string | null; titleAr: string }[] }

export function VersionsPanel({ minutesId }: { minutesId: string }) {
  const { t, i18n } = useTranslation();
  const { me } = useAuth();
  const q = useQuery({ queryKey: ['minutes-versions', minutesId], queryFn: () => get<MinutesVersion[]>(`/api/v1/minutes/${minutesId}/versions`) });
  const [compare, setCompare] = useState<[number, number] | null>(null);
  const [sel, setSel] = useState<number[]>([]);
  return (
    <>
      <Space style={{ marginBottom: 12 }}>
        <Button icon={<DiffOutlined />} disabled={sel.length !== 2} onClick={() => setCompare([Math.min(sel[0], sel[1]), Math.max(sel[0], sel[1])])}>{t('minutes.compare')}</Button>
        <Typography.Text type="secondary">{t('minutes.compareHelp')}</Typography.Text>
      </Space>
      <ErrorAlert error={q.error} />
      <Table<MinutesVersion> size="small" rowKey="versionNo" loading={q.isLoading} dataSource={q.data} pagination={false}
        rowSelection={{ type: 'checkbox', selectedRowKeys: sel, onChange: (k) => setSel((k as number[]).slice(-2)) }}
        columns={[
          { title: t('minutes.version'), dataIndex: 'versionNo', width: 70, render: (v, r) => <Space>{v}{r.approved && <Tag color="green">{t('enums.minutesStatus.approved')}</Tag>}{r.amendmentNo > 0 && <Tag color="gold">{t('minutes.amendmentNo', { n: r.amendmentNo })}</Tag>}</Space> },
          { title: t('minutes.hash'), dataIndex: 'contentHash', render: (v: string) => <span className="mono" title={v}>{v.slice(0, 16)}…</span> },
          { title: t('minutes.changeSummary'), dataIndex: 'changeSummary', render: (v) => v ?? '—' },
          { title: t('minutes.issuedHere'), dataIndex: 'resolutionsIssued', width: 90 },
          { title: t('activity.by'), dataIndex: 'createdBy' },
          { title: t('common.date'), dataIndex: 'createdAt', render: (v) => formatDateTime(v, i18n.language, me?.user.calendar) },
          { title: '', render: (_, r) => <Button size="small" icon={<FilePdfOutlined />} href={`/api/v1/minutes/${minutesId}/pdf?versionNo=${r.versionNo}`} target="_blank">PDF</Button> },
        ]} />
      {compare && <CompareModal minutesId={minutesId} a={compare[0]} b={compare[1]} onClose={() => setCompare(null)} />}
    </>
  );
}

function CompareModal({ minutesId, a, b, onClose }: { minutesId: string; a: number; b: number; onClose: () => void }) {
  const { t } = useTranslation();
  const va = useQuery({ queryKey: ['minutes-version', minutesId, a], queryFn: () => get<VersionView>(`/api/v1/minutes/${minutesId}/versions/${a}`) });
  const vb = useQuery({ queryKey: ['minutes-version', minutesId, b], queryFn: () => get<VersionView>(`/api/v1/minutes/${minutesId}/versions/${b}`) });
  const rows: { area: string; before: string; after: string }[] = [];
  if (va.data && vb.data) {
    const A = va.data.version, B = vb.data.version;
    const keys = new Set([...A.content.sections.map((s) => s.key), ...B.content.sections.map((s) => s.key)]);
    for (const k of keys) {
      const x = A.content.sections.find((s) => s.key === k), y = B.content.sections.find((s) => s.key === k);
      if ((x?.body ?? '') !== (y?.body ?? '')) rows.push({ area: y?.title ?? x?.title ?? k, before: x?.body ?? '—', after: y?.body ?? '—' });
    }
    for (const item of B.snapshot.agenda) {
      const old = A.snapshot.agenda.find((i) => i.id === item.id);
      if ((old?.discussion ?? '') !== (item.discussion ?? '')) rows.push({ area: `${t('minutes.itemN', { n: item.position })} — ${t('agenda.discussion')}`, before: old?.discussion ?? '—', after: item.discussion ?? '—' });
    }
    for (const r of B.snapshot.resolutions) {
      const old = A.snapshot.resolutions.find((x) => x.id === r.id);
      if (!old) rows.push({ area: `${t('minutes.decision')}: ${r.titleAr}`, before: '—', after: r.textAr });
      else if (old.textAr !== r.textAr || old.responsible !== r.responsible) rows.push({ area: `${t('minutes.decision')}: ${r.titleAr}`, before: `${old.textAr}\n${old.responsible ?? ''}`, after: `${r.textAr}\n${r.responsible ?? ''}` });
    }
    for (const r of A.snapshot.resolutions.filter((x) => !B.snapshot.resolutions.some((y) => y.id === x.id)))
      rows.push({ area: `${t('minutes.decision')}: ${r.titleAr}`, before: r.textAr, after: t('minutes.removed') });
  }
  return (
    <Modal open width={960} title={t('minutes.compareTitle', { a, b })} onCancel={onClose} footer={null}>
      <Table size="small" rowKey={(r) => r.area} loading={va.isLoading || vb.isLoading} dataSource={rows} pagination={false}
        locale={{ emptyText: t('minutes.noDifferences') }}
        columns={[
          { title: t('minutes.area'), dataIndex: 'area', width: 200 },
          { title: `v${a}`, dataIndex: 'before', render: (v) => <span className="diff-old">{v}</span> },
          { title: `v${b}`, dataIndex: 'after', render: (v) => <span className="diff-new">{v}</span> },
        ]} />
    </Modal>
  );
}
