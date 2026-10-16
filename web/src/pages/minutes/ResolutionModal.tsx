// نموذج القرار أو التوصية في مسودة المحضر. يُرسل بمفتاح منع التكرار: النقر المزدوج أو إعادة المحاولة لا تنشئ قرارًا ثانيًا.
import { Alert, App, Col, Form, Input, Modal, Radio, Row, Select, Switch } from 'antd';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { get, newRequestKey, patch, postOnce, type Paged } from '../../api/client';
import type { MinutesSnapshot, ResolutionDetail, ResolutionFull } from '../../api/types';
import { errorText } from '../../components/common';
import { BodySelect, EntitySelect, OrgUnitSelect } from '../../components/pickers';

export const CATEGORIES = ['administrative', 'financial', 'technical', 'policy', 'organizational', 'procedural', 'other'];

export function ResolutionModal({ minutesId, bodyId, agenda, resolutionId, onClose }: {
  minutesId: string; bodyId: string; agenda: MinutesSnapshot['agenda']; resolutionId?: string; onClose: () => void;
}) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const [key] = useState(newRequestKey);
  const type = Form.useWatch('resolutionType', form) as string | undefined;
  const supersedes = Form.useWatch('supersedesResolutionId', form) as string | undefined;
  const existing = useQuery({ queryKey: ['resolution', resolutionId], queryFn: () => get<ResolutionDetail>(`/api/v1/resolutions/${resolutionId}`), enabled: !!resolutionId });
  const issued = useQuery({
    queryKey: ['resolutions', 'supersedable', bodyId],
    queryFn: () => get<Paged<ResolutionFull>>('/api/v1/resolutions', { bodyId, pageSize: 100 }),
  });
  useEffect(() => {
    const r = existing.data?.resolution;
    if (r) form.setFieldsValue({
      resolutionType: r.resolutionType, category: r.category, titleAr: r.titleAr, textAr: r.textAr, textEn: r.textEn, agendaItemId: r.agendaItemId,
      authorityText: r.authorityText, targetBodyId: r.targetBodyId, targetAuthorityText: r.targetAuthorityText, responsibleOrgUnitId: r.responsibleOrgUnitId,
      responsibleExternalEntityId: r.responsibleExternalEntityId, responsibleText: r.responsibleText, adoptionMethod: r.adoptionMethod, requiresFollowUp: r.requiresFollowUp,
    });
  }, [existing.data, form]);
  const m = useMutation({
    mutationFn: (v: Record<string, unknown>) => resolutionId ? patch(`/api/v1/resolutions/${resolutionId}`, v) : postOnce(`/api/v1/minutes/${minutesId}/resolutions`, v, key),
    onSuccess: () => { message.success(t('common.saved')); onClose(); },
    onError: (e) => message.error(errorText(e, t('common.error'))),
  });
  const candidates = (issued.data?.items ?? []).filter((r) => r.number && ['issued', 'in_execution', 'executed', 'referred'].includes(r.status));
  const submit = (v: Record<string, unknown>) => {
    const clean = Object.fromEntries(Object.entries(v).filter(([, x]) => x !== undefined && x !== ''));
    if (resolutionId) { delete clean.supersedesResolutionId; delete clean.supersedeKind; }
    m.mutate(clean);
  };
  return (
    <Modal open width={780} title={resolutionId ? t('minutes.editResolution') : t('minutes.proposeResolution')} onCancel={onClose} onOk={() => form.submit()}
      confirmLoading={m.isPending} okText={t('common.save')} cancelText={t('common.cancel')} destroyOnHidden>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('minutes.resolutionHelp')} />
      <Form form={form} layout="vertical" onFinish={submit} initialValues={{ resolutionType: 'decision', category: 'administrative', adoptionMethod: 'consensus', requiresFollowUp: true }}>
        <Row gutter={12}>
          <Col xs={24} md={10}>
            <Form.Item name="resolutionType" label={t('common.type')}>
              <Radio.Group optionType="button" options={['decision', 'recommendation'].map((v) => ({ value: v, label: t(`enums.resolutionType.${v}`) }))} />
            </Form.Item>
          </Col>
          <Col xs={24} md={7}>
            <Form.Item name="category" label={t('resolutions.category')}>
              <Select options={CATEGORIES.map((v) => ({ value: v, label: t(`enums.resolutionCategory.${v}`) }))} />
            </Form.Item>
          </Col>
          <Col xs={24} md={7}>
            <Form.Item name="agendaItemId" label={t('resolutions.agendaItem')}>
              <Select allowClear options={agenda.map((a) => ({ value: a.id, label: `${a.position}. ${a.titleAr}` }))} />
            </Form.Item>
          </Col>
        </Row>
        <Form.Item name="titleAr" label={t('common.title')} rules={[{ required: true, max: 300 }]}><Input /></Form.Item>
        <Form.Item name="textAr" label={t('resolutions.officialText')} rules={[{ required: true, max: 10000 }]}><Input.TextArea autoSize={{ minRows: 3, maxRows: 10 }} /></Form.Item>
        <Form.Item name="textEn" label={t('resolutions.textEn')}><Input.TextArea dir="ltr" autoSize={{ minRows: 1, maxRows: 6 }} /></Form.Item>
        <Row gutter={12}>
          <Col xs={24} md={12}><Form.Item name="authorityText" label={t('resolutions.authority')} extra={t('resolutions.authorityHelp')}><Input /></Form.Item></Col>
          <Col xs={24} md={12}>
            <Form.Item name="adoptionMethod" label={t('resolutions.adoptionMethod')} extra={t('resolutions.adoptionHelp')}>
              <Select options={['consensus', 'vote', 'chair_decision'].map((v) => ({ value: v, label: t(`enums.adoptionMethod.${v}`) }))} />
            </Form.Item>
          </Col>
        </Row>
        {type === 'recommendation' && (
          <Row gutter={12}>
            <Col xs={24} md={12}><Form.Item name="targetBodyId" label={t('resolutions.targetBody')} extra={t('resolutions.targetHelp')}><BodySelect allowClear activeOnly /></Form.Item></Col>
            <Col xs={24} md={12}><Form.Item name="targetAuthorityText" label={t('resolutions.targetAuthority')}><Input placeholder={t('resolutions.targetAuthorityPh')} /></Form.Item></Col>
          </Row>
        )}
        <Row gutter={12}>
          <Col xs={24} md={8}><Form.Item name="responsibleOrgUnitId" label={t('resolutions.responsibleUnit')}><OrgUnitSelect allowClear /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item name="responsibleExternalEntityId" label={t('resolutions.responsibleEntity')}><EntitySelect allowClear /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item name="responsibleText" label={t('resolutions.responsibleText')}><Input /></Form.Item></Col>
        </Row>
        {!resolutionId && (
          <Row gutter={12}>
            <Col xs={24} md={14}>
              <Form.Item name="supersedesResolutionId" label={t('resolutions.supersedes')} extra={t('resolutions.supersedesHelp')}>
                <Select allowClear showSearch optionFilterProp="label" options={candidates.map((r) => ({ value: r.id, label: `${r.number} — ${r.titleAr}` }))} />
              </Form.Item>
            </Col>
            <Col xs={24} md={10}>
              <Form.Item name="supersedeKind" label={t('resolutions.supersedeKind')} rules={supersedes ? [{ required: true }] : []}>
                <Radio.Group disabled={!supersedes} optionType="button" options={['amends', 'revokes'].map((v) => ({ value: v, label: t(`enums.supersedeKind.${v}`) }))} />
              </Form.Item>
            </Col>
          </Row>
        )}
        <Form.Item name="requiresFollowUp" label={t('resolutions.requiresFollowUp')} valuePropName="checked"><Switch /></Form.Item>
      </Form>
    </Modal>
  );
}
