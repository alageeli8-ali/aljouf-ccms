import { Alert, Button, Card, Divider, Form, Input, Space, Typography } from 'antd';
import { LockOutlined, MailOutlined, WindowsOutlined, GlobalOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Navigate, useNavigate, useSearchParams } from 'react-router-dom';
import { ApiError, get, localStorageSafe } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { applyDirection, type Lang } from '../i18n';

interface Providers {
  local: { enabled: boolean; breakGlassOnly: boolean };
  sso: { featureEnabled: boolean; configured: boolean; connectionStatus: string; available: boolean; loginUrl: string };
}

export default function LoginPage() {
  const { t, i18n } = useTranslation();
  const { me, login } = useAuth();
  const nav = useNavigate();
  const [params] = useSearchParams();
  const [error, setError] = useState<string | null>(params.get('error') ? t('login.ssoUnavailable') : null);
  const [busy, setBusy] = useState(false);
  const providers = useQuery({ queryKey: ['providers'], queryFn: () => get<Providers>('/api/v1/auth/providers') });
  const info = useQuery({ queryKey: ['system-info'], queryFn: () => get<{ environment: string }>('/api/v1/system/info'), staleTime: Infinity });

  if (me) return <Navigate to={params.get('returnUrl') ?? '/'} replace />;

  const submit = async (v: { email: string; password: string }) => {
    setBusy(true);
    setError(null);
    try {
      await login(v.email, v.password);
      nav(params.get('returnUrl') ?? '/', { replace: true });
    } catch (e) {
      setError(e instanceof ApiError && e.code === 'account-locked' ? t('login.locked')
        : e instanceof ApiError && e.code === 'portal-disabled' ? t('login.portalDisabled') : t('login.failed'));
    } finally {
      setBusy(false);
    }
  };

  const switchLang = () => {
    const next: Lang = i18n.language === 'ar' ? 'en' : 'ar';
    localStorageSafe('ccms.lang', next);
    void i18n.changeLanguage(next);
    applyDirection(next);
  };

  const sso = providers.data?.sso;
  const local = providers.data?.local;

  return (
    <div className="login-shell">
      <section className="login-art">
        <div>
          <div className="brand-mark" style={{ fontSize: 30 }}>{t('app.short')}</div>
          <div style={{ color: '#e8d9c4', marginTop: 8, fontSize: 15 }}>{t('app.org')}</div>
        </div>
        <div style={{ maxWidth: 460, position: 'relative', zIndex: 1 }}>
          <Typography.Title level={2} style={{ color: '#fff', marginTop: 0 }}>{t('app.name')}</Typography.Title>
          <Typography.Paragraph style={{ color: '#e6efe9', fontSize: 15 }}>{t('login.notice')}</Typography.Paragraph>
        </div>
      </section>
      <section className="login-form">
        <Card style={{ width: '100%', maxWidth: 400, border: 0 }}>
          <Space style={{ width: '100%', justifyContent: 'space-between' }}>
            <Typography.Title level={3} style={{ margin: 0 }}>{t('login.title')}</Typography.Title>
            <Button type="text" icon={<GlobalOutlined />} onClick={switchLang}>{t('common.language')}</Button>
          </Space>
          <Typography.Paragraph type="secondary">{t('login.subtitle')}</Typography.Paragraph>
          {info.data?.environment === 'Trial' && <Alert type="warning" showIcon message={t('common.trial')} description={t('common.trialNotice')} style={{ marginBottom: 16 }} />}
          {error && <Alert type="error" showIcon message={error} style={{ marginBottom: 16 }} />}

          {sso?.featureEnabled && (
            <>
              <Button block size="large" icon={<WindowsOutlined />} disabled={!sso.available} href={sso.available ? sso.loginUrl : undefined}>
                {t('login.sso')}
              </Button>
              {!sso.available && (
                <Typography.Paragraph type="secondary" style={{ marginTop: 8, fontSize: 12 }}>
                  {sso.configured ? t('login.ssoUnavailable') : t('login.ssoNotConfigured')}
                </Typography.Paragraph>
              )}
              {local?.enabled && <Divider plain>{t('login.or')}</Divider>}
            </>
          )}

          {local?.enabled ? (
            <Form layout="vertical" onFinish={submit} requiredMark={false}>
              <Form.Item name="email" label={t('login.email')} rules={[{ required: true, type: 'email' }]}>
                <Input prefix={<MailOutlined />} autoComplete="username" dir="ltr" size="large" />
              </Form.Item>
              <Form.Item name="password" label={t('login.password')} rules={[{ required: true }]}>
                <Input.Password prefix={<LockOutlined />} autoComplete="current-password" dir="ltr" size="large" />
              </Form.Item>
              <Button type="primary" htmlType="submit" block size="large" loading={busy}>{t('login.submit')}</Button>
            </Form>
          ) : providers.data && !sso?.featureEnabled ? (
            <Alert type="warning" showIcon message={t('login.localDisabled')} />
          ) : null}
        </Card>
      </section>
    </div>
  );
}
