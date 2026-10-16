// مسارات الاعتماد البديلة: تسجيل التوقيعات الورقية، وتسجيل اعتماد صاحب الصلاحية خارج النظام بمرجع ودليل.
import { Alert, App, Button, DatePicker, Form, Input, Modal, Select, Space, Upload } from 'antd';
import { UploadOutlined } from '@ant-design/icons';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Dayjs } from 'dayjs';
import { api, get, post } from '../../api/client';
import type { Doc, MinutesDetail } from '../../api/types';
import { errorText } from '../../components/common';

function EvidencePicker({ minutesId, purpose, value, onChange }: { minutesId: string; purpose: string; value?: string; onChange?: (v: string) => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const docs = useQuery({ queryKey: ['documents', 'minutes', minutesId], queryFn: () => get<Doc[]>('/api/v1/documents', { ownerType: 'minutes', ownerId: minutesId }) });
  const upload = useMutation({
    mutationFn: (file: File) => {
      const fd = new FormData();
      fd.set('ownerType', 'minutes');
      fd.set('ownerId', minutesId);
      fd.set('purpose', purpose);
      fd.set('file', file);
      return api<{ id: string }>('POST', '/api/v1/documents', fd);
    },
    onSuccess: (r) => { void docs.refetch(); onChange?.(r.id); message.success(t('attachments.uploaded')); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Space.Compact style={{ width: '100%' }}>
      <Select value={value} onChange={onChange} style={{ width: '100%' }} placeholder={t('minutes.chooseEvidence')}
        options={(docs.data ?? []).filter((d) => d.purpose === purpose).map((d) => ({ value: d.id, label: d.fileName }))} />
      <Upload showUploadList={false} beforeUpload={(f) => { upload.mutate(f); return false; }}>
        <Button icon={<UploadOutlined />} loading={upload.isPending}>{t('attachments.upload')}</Button>
      </Upload>
    </Space.Compact>
  );
}

export function WetSignatureModal({ detail, onClose }: { detail: MinutesDetail; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const pending = detail.signoffs.filter((s) => s.status === 'requested' && s.versionNo === detail.currentVersion?.no);
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post(`/api/v1/minutes/${detail.minutes.id}/signoffs/wet`, v),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const [all] = useState(pending.map((p) => p.userId));
  return (
    <Modal open title={t('minutes.recordWet')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')} width={620}>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('minutes.wetHelp')} />
      <Form form={form} layout="vertical" initialValues={{ userIds: all }} onFinish={(v) => m.mutate(v)}>
        <Form.Item name="documentId" label={t('minutes.signedPage')} rules={[{ required: true }]}><EvidencePicker minutesId={detail.minutes.id} purpose="wet_signature" /></Form.Item>
        <Form.Item name="userIds" label={t('minutes.signers')} rules={[{ required: true }]}>
          <Select mode="multiple" options={pending.map((p) => ({ value: p.userId, label: p.name }))} />
        </Form.Item>
        <Form.Item name="note" label={t('common.notes')}><Input /></Form.Item>
      </Form>
    </Modal>
  );
}

export function ExternalApprovalModal({ detail, onClose }: { detail: MinutesDetail; onClose: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => post(`/api/v1/minutes/${detail.minutes.id}/record-approval`, {
      ...v, approvedOn: (v.approvedOn as Dayjs).format('YYYY-MM-DD'), contentHash: detail.minutes.contentHash,
    }),
    onSuccess: () => { message.success(t('minutes.approvedOk')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  return (
    <Modal open title={t('minutes.recordExternal')} onCancel={onClose} onOk={() => form.submit()} confirmLoading={m.isPending} okText={t('minutes.recordExternal')} cancelText={t('common.cancel')} width={640}>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('minutes.externalHelp')} />
      <Form form={form} layout="vertical" onFinish={(v) => m.mutate(v)}>
        <div className="form-row">
          <Form.Item name="approverName" label={t('minutes.approverName')} rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="approverTitle" label={t('minutes.approverTitle')}><Input /></Form.Item>
        </div>
        <div className="form-row">
          <Form.Item name="referenceNo" label={t('minutes.reference')} rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="approvedOn" label={t('minutes.approvedOn')} rules={[{ required: true }]}><DatePicker style={{ width: '100%' }} /></Form.Item>
        </div>
        <Form.Item name="documentId" label={t('minutes.approvalDocument')} rules={[{ required: true }]}><EvidencePicker minutesId={detail.minutes.id} purpose="approval_evidence" /></Form.Item>
        <Form.Item name="note" label={t('common.notes')}><Input.TextArea rows={2} /></Form.Item>
      </Form>
    </Modal>
  );
}
