import { Avatar, Badge, Button, Dropdown, Layout, Menu, Space, Tag, Typography } from 'antd';
import {
  AppstoreOutlined, AuditOutlined, BarChartOutlined, BellOutlined, CalendarOutlined, CheckSquareOutlined, FileTextOutlined,
  GlobalOutlined, IdcardOutlined, LogoutOutlined, SettingOutlined, TeamOutlined, UserOutlined, SolutionOutlined, ExportOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { get, localStorageSafe } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { applyDirection, type Lang } from '../i18n';

const { Header, Sider, Content } = Layout;

export default function AppLayout() {
  const { t, i18n } = useTranslation();
  const { me, can, hasRole, feature, logout } = useAuth();
  const nav = useNavigate();
  const loc = useLocation();
  const [collapsed, setCollapsed] = useState(false);
  const lang = i18n.language as Lang;

  const unread = useQuery({ queryKey: ['unread'], queryFn: () => get<{ count: number }>('/api/v1/notifications/unread-count'), refetchInterval: 30_000 });
  const info = useQuery({ queryKey: ['system-info'], queryFn: () => get<{ environment: string; version: string }>('/api/v1/system/info'), staleTime: Infinity });

  const switchLang = () => {
    const next: Lang = lang === 'ar' ? 'en' : 'ar';
    localStorageSafe('ccms.lang', next);
    void i18n.changeLanguage(next);
    applyDirection(next);
  };

  const showReports = can('reports.executive') || (me?.bodyRoles.length ?? 0) > 0 || hasRole('governance_officer');
  const portalItems = [{ type: 'group' as const, label: collapsed ? null : t('nav.portal'), children: [
    { key: '/portal', icon: <ExportOutlined />, label: <Link to="/portal">{t('portal.myAssignments')}</Link> },
    { key: '/notifications', icon: <BellOutlined />, label: <Link to="/notifications">{t('nav.notifications')}</Link> },
  ] }];
  const internalItems = [
    { type: 'group' as const, label: collapsed ? null : t('nav.work'), children: [
      { key: '/', icon: <AppstoreOutlined />, label: <Link to="/">{t('nav.dashboard')}</Link> },
      { key: '/tasks', icon: <CheckSquareOutlined />, label: <Link to="/tasks">{t('nav.tasks')}</Link> },
      { key: '/notifications', icon: <BellOutlined />, label: <Link to="/notifications">{t('nav.notifications')}</Link> },
      ...(hasRole('external_representative') && feature('EXTERNAL_PORTAL_ENABLED') && !me?.portalUser
        ? [{ key: '/portal', icon: <ExportOutlined />, label: <Link to="/portal">{t('nav.portal')}</Link> }] : []),
    ] },
    { type: 'group' as const, label: collapsed ? null : t('nav.governance'), children: [
      { key: '/bodies', icon: <TeamOutlined />, label: <Link to="/bodies">{t('nav.bodies')}</Link> },
      { key: '/memberships', icon: <IdcardOutlined />, label: <Link to="/memberships">{t('nav.memberships')}</Link> },
      { key: '/meetings', icon: <CalendarOutlined />, label: <Link to="/meetings">{t('nav.meetings')}</Link> },
      { key: '/minutes', icon: <FileTextOutlined />, label: <Link to="/minutes">{t('nav.minutes')}</Link> },
      { key: '/resolutions', icon: <SolutionOutlined />, label: <Link to="/resolutions">{t('nav.resolutions')}</Link> },
      ...(showReports ? [{ key: '/reports', icon: <BarChartOutlined />, label: <Link to="/reports">{t('nav.reports')}</Link> }] : []),
    ] },
    ...(can('admin.users.manage') || can('admin.settings.view') ? [{ type: 'group' as const, label: collapsed ? null : t('nav.administration'), children: [
      ...(can('admin.users.manage') ? [{ key: '/users', icon: <UserOutlined />, label: <Link to="/users">{t('nav.users')}</Link> }] : []),
      ...(can('admin.settings.view') ? [{ key: '/settings', icon: <SettingOutlined />, label: <Link to="/settings">{t('nav.settings')}</Link> }] : []),
    ] }] : []),
  ];
  const items = me?.portalUser ? portalItems : internalItems;
  const selected = '/' + (loc.pathname.split('/')[1] ?? '');
  const name = lang === 'en' ? me?.user.displayNameEn ?? me?.user.displayNameAr : me?.user.displayNameAr;
  const roles = [...new Set([...(me?.systemRoles ?? []).filter((r) => r !== 'employee'), ...(me?.bodyRoles ?? []).map((b) => b.role)])];

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Sider collapsible collapsed={collapsed} onCollapse={setCollapsed} width={244} breakpoint="lg" theme="dark">
        <div style={{ padding: collapsed ? '18px 8px' : '18px 18px 12px', color: '#fff' }}>
          <div className="brand-mark" style={{ fontSize: collapsed ? 14 : 18, lineHeight: 1.3, textAlign: collapsed ? 'center' : 'start' }}>
            {collapsed ? <AuditOutlined /> : t('app.short')}
          </div>
          {!collapsed && <div style={{ color: '#d9c4a8', fontSize: 12, marginTop: 4 }}>{t('app.org')}</div>}
        </div>
        <div className="zigzag" />
        <Menu theme="dark" mode="inline" selectedKeys={[selected === '/' ? '/' : selected]} items={items} style={{ borderInlineEnd: 0, marginTop: 8 }} />
      </Sider>
      <Layout>
        <Header style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', borderBottom: '1px solid #dfe3dc' }}>
          <Space size={12}>
            <Typography.Text strong style={{ fontSize: 15 }}>{t('app.name')}</Typography.Text>
            {info.data && info.data.environment !== 'Production' && <Tag color="gold">{t('common.environment')}: <bdi>{info.data.environment}</bdi></Tag>}
          </Space>
          <Space size={8}>
            <Button type="text" icon={<GlobalOutlined />} onClick={switchLang}>{t('common.language')}</Button>
            <Badge count={unread.data?.count ?? 0} size="small" offset={[-4, 4]}>
              <Button type="text" icon={<BellOutlined />} aria-label={t('nav.notifications')} onClick={() => nav('/notifications')} />
            </Badge>
            <Dropdown
              menu={{
                items: [
                  { key: 'roles', disabled: true, label: roles.map((r) => t(`roles.${r}`)).join('، ') || t('roles.employee') },
                  { type: 'divider' },
                  { key: 'password', icon: <IdcardOutlined />, label: t('portal.changePassword'), onClick: () => nav('/account/password') },
                  { key: 'logout', icon: <LogoutOutlined />, label: t('common.logout'), onClick: () => void logout().then(() => nav('/login')) },
                  { key: 'logout-all', icon: <LogoutOutlined />, label: t('common.logoutAll'), onClick: () => void logout(true).then(() => nav('/login')) },
                ],
              }}
            >
              <Space style={{ cursor: 'pointer' }}>
                <Avatar style={{ background: '#B28655' }} icon={<UserOutlined />} />
                <span>{name}</span>
              </Space>
            </Dropdown>
          </Space>
        </Header>
        <Content style={{ padding: 24, maxWidth: 1440, width: '100%', margin: '0 auto' }}>
          <Outlet />
        </Content>
      </Layout>
    </Layout>
  );
}
