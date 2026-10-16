import { Select } from 'antd';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { get, type Paged } from '../api/client';
import type { Body, DirectoryUser, ExternalEntity, OrgUnit } from '../api/types';

type Props<T = string> = { value?: T; onChange?: (v: T) => void; placeholder?: string; allowClear?: boolean; style?: React.CSSProperties; status?: string };

export function BodySelect({ value, onChange, placeholder, allowClear, style, activeOnly }: Props & { activeOnly?: boolean }) {
  const { i18n } = useTranslation();
  const q = useQuery({ queryKey: ['bodies', 'picker', activeOnly], queryFn: () => get<Paged<Body>>('/api/v1/bodies', { pageSize: 100, status: activeOnly ? 'active' : undefined }) });
  return (
    <Select
      value={value} onChange={onChange} placeholder={placeholder} allowClear={allowClear} style={{ minWidth: 220, ...style }}
      loading={q.isLoading} showSearch optionFilterProp="label"
      options={(q.data?.items ?? []).map((b) => ({ value: b.id, label: `${i18n.language === 'en' && b.nameEn ? b.nameEn : b.nameAr} (${b.code})` }))}
    />
  );
}

export function UserSelect({ value, onChange, placeholder, allowClear, style }: Props) {
  const [q, setQ] = useState('');
  const users = useQuery({ queryKey: ['directory', q], queryFn: () => get<DirectoryUser[]>('/api/v1/directory', { q }) });
  return (
    <Select
      value={value} onChange={onChange} placeholder={placeholder} allowClear={allowClear} style={{ minWidth: 240, ...style }}
      showSearch filterOption={false} onSearch={setQ} loading={users.isLoading}
      options={(users.data ?? []).map((u) => ({ value: u.id, label: `${u.displayNameAr} — ${u.email}` }))}
    />
  );
}

export function OrgUnitSelect({ value, onChange, placeholder, style }: Props) {
  const q = useQuery({ queryKey: ['org-units'], queryFn: () => get<OrgUnit[]>('/api/v1/org-units') });
  return <Select value={value} onChange={onChange} placeholder={placeholder} style={{ minWidth: 220, ...style }} options={(q.data ?? []).map((o) => ({ value: o.id, label: `${o.nameAr} (${o.code})` }))} />;
}

export function EntitySelect({ value, onChange, placeholder, style, allowClear }: Props) {
  const q = useQuery({ queryKey: ['external-entities'], queryFn: () => get<ExternalEntity[]>('/api/v1/external-entities') });
  return <Select value={value} onChange={onChange} placeholder={placeholder} allowClear={allowClear} style={{ minWidth: 220, ...style }} options={(q.data ?? []).map((e) => ({ value: e.id, label: e.nameAr }))} />;
}

export function EnumSelect({ group, values, value, onChange, placeholder, allowClear, style, mode }: Props<any> & { group: string; values: string[]; mode?: 'multiple' }) {
  const { t } = useTranslation();
  return (
    <Select value={value} onChange={onChange} placeholder={placeholder} allowClear={allowClear} mode={mode} style={{ minWidth: 160, ...style }}
      options={values.map((v) => ({ value: v, label: t(`${group}.${v}`, { defaultValue: v }) }))} />
  );
}
