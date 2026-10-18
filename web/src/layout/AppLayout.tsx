import { Avatar, Badge, Button, Drawer, Dropdown, Grid, Layout, Menu, Space, Tag, Typography } from 'antd';
import {
  AppstoreOutlined, AuditOutlined, BarChartOutlined, BellOutlined, CalendarOutlined, CheckSquareOutlined, FileTextOutlined,
  GlobalOutlined, IdcardOutlined, LogoutOutlined, SettingOutlined, TeamOutlined, UserOutlined, SolutionOutlined, ExportOutlined, SafetyCertificateOutlined, BranchesOutlined,
  RobotOutlined, SearchOutlined, ApiOutlined, InboxOutlined, MenuOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
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
  const [drawerOpen, setDrawerOpen] = useState(false);
  const lang = i18n.language as Lang;
  // الجوال (< md): قائمة منسدلة جانبية بدل الشريط الثابت؛ اللوحي (< lg): شريط أيقونات مطوي.
  const bp = Grid.useBreakpoint();
  const phone = bp.md === false;
  const compact = bp.lg === false;
  useEffect(() => { setDrawerOpen(false); }, [loc.pathname]);
  const rail = !phone && (collapsed || compact);

  const unread = useQuery({ queryKey: ['unread'], queryFn: () => get<{ count: number }>('/api/v1/notifications/unread-count'), refetchInterval: 30_000 });
  const info = useQuery({ queryKey: ['system-info'], queryFn: () => get<{ environment: string; version: string }>('/api/v1/system/info'), staleTime: Infinity });

  const switchLang = () => {
    const next: Lang = lang === 'ar' ? 'en' : 'ar';
    localStorageSafe('ccms.lang', next);
    void i18n.changeLanguage(next);
    applyDirection(next);
  };

  const showReports = can('reports.executive') || can('reports.body') || can('audit.read') || (me?.bodyRoles.length ?? 0) > 0 || hasRole('governance_officer');
  const portalItems = [{ type: 'group' as const, label: rail ? null : t('nav.portal'), children: [
    { key: '/portal', icon: <ExportOutlined />, label: <Link to="/portal">{t('portal.myAssignments')}</Link> },
    { key: '/notifications', icon: <BellOutlined />, label: <Link to="/notifications">{t('nav.notifications')}</Link> },
  ] }];
  const internalItems = [
    { type: 'group' as const, label: rail ? null : t('nav.work'), children: [
      { key: '/', icon: <AppstoreOutlined />, label: <Link to="/">{t('nav.dashboard')}</Link> },
      { key: '/tasks', icon: <CheckSquareOutlined />, label: <Link to="/tasks">{t('nav.tasks')}</Link> },
      { key: '/notifications', icon: <BellOutlined />, label: <Link to="/notifications">{t('nav.notifications')}</Link> },
      ...(hasRole('external_representative') && feature('EXTERNAL_PORTAL_ENABLED') && !me?.portalUser
        ? [{ key: '/portal', icon: <ExportOutlined />, label: <Link to="/portal">{t('nav.portal')}</Link> }] : []),
    ] },
    { type: 'group' as const, label: rail ? null : t('nav.governance'), children: [
      { key: '/bodies', icon: <TeamOutlined />, label: <Link to="/bodies">{t('nav.bodies')}</Link> },
      { key: '/memberships', icon: <IdcardOutlined />, label: <Link to="/memberships">{t('nav.memberships')}</Link> },
      { key: '/meetings', icon: <CalendarOutlined />, label: <Link to="/meetings">{t('nav.meetings')}</Link> },
      { key: '/minutes', icon: <FileTextOutlined />, label: <Link to="/minutes">{t('nav.minutes')}</Link> },
      { key: '/resolutions', icon: <SolutionOutlined />, label: <Link to="/resolutions">{t('nav.resolutions')}</Link> },
      ...(showReports ? [{ key: '/reports', icon: <BarChartOutlined />, label: <Link to="/reports">{t('nav.reports')}</Link> }] : []),
      { key: '/search', icon: <SearchOutlined />, label: <Link to="/search">{t('nav.search')}</Link> },
      { key: '/signature-verify', icon: <SafetyCertificateOutlined />, label: <Link to="/signature-verify">{t('nav.signatureVerify')}</Link> },
      ...(can('records.manage') || can('records.destruction.approve') ? [{ key: '/records', icon: <InboxOutlined />, label: <Link to="/records">{t('nav.records')}</Link> }] : []),
    ] },
    ...(can('admin.users.manage') || can('admin.settings.view') || can('esign.routes.manage') || can('ai.logs.view') ? [{ type: 'group' as const, label: rail ? null : t('nav.administration'), children: [
      ...(can('admin.users.manage') ? [{ key: '/users', icon: <UserOutlined />, label: <Link to="/users">{t('nav.users')}</Link> }] : []),
      ...(can('admin.settings.view') ? [{ key: '/settings', icon: <SettingOutlined />, label: <Link to="/settings">{t('nav.settings')}</Link> }] : []),
      ...(can('admin.settings.view') ? [{ key: '/integrations', icon: <ApiOutlined />, label: <Link to="/integrations">{t('nav.integrations')}</Link> }] : []),
      ...(can('admin.settings.view') || can('ai.logs.view') ? [{ key: '/ai-admin', icon: <RobotOutlined />, label: <Link to="/ai-admin">{t('nav.aiAdmin')}</Link> }] : []),
      ...(can('esign.routes.manage') ? [{ key: '/signature-routes', icon: <BranchesOutlined />, label: <Link to="/signature-routes">{t('nav.signatureRoutes')}</Link> }] : []),
    ] }] : []),
  ];
  const items = me?.portalUser ? portalItems : internalItems;
  const selected = '/' + (loc.pathname.split('/')[1] ?? '');
  const name = lang === 'en' ? me?.user.displayNameEn ?? me?.user.displayNameAr : me?.user.displayNameAr;
  const roles = [...new Set([...(me?.systemRoles ?? []).filter((r) => r !== 'employee'), ...(me?.bodyRoles ?? []).map((b) => b.role)])];

  const brand = (folded: boolean) => (
    <div style={{ padding: folded ? '18px 8px' : '18px 18px 12px', color: '#fff' }}>
      <div className="brand-mark" style={{ fontSize: folded ? 14 : 18, lineHeight: 1.3, textAlign: folded ? 'center' : 'start' }}>
        {folded ? <AuditOutlined /> : t('app.short')}
      </div>
      {!folded && <div style={{ color: '#d9c4a8', fontSize: 12, marginTop: 4 }}>{t('app.org')}</div>}
    </div>
  );
  const menu = (folded: boolean) => (
    <Menu theme="dark" mode="inline" inlineCollapsed={folded} selectedKeys={[selected === '/' ? '/' : selected]} items={items} style={{ borderInlineEnd: 0, marginTop: 8 }} />
  );

  return (
    <Layout style={{ minHeight: '100vh' }}>
      {!phone && (
        <Sider collapsible={!compact} collapsed={rail} onCollapse={setCollapsed} width={244} theme="dark" trigger={compact ? null : undefined}>
          {brand(rail)}
          <div className="zigzag" />
          {menu(rail)}
        </Sider>
      )}
      {phone && (
        <Drawer open={drawerOpen} onClose={() => setDrawerOpen(false)} placement={lang === 'ar' ? 'right' : 'left'} width={272} closable={false}
          styles={{ body: { padding: 0, background: '#553D26' } }} rootClassName="nav-drawer">
          {brand(false)}
          <div className="zigzag" />
          {menu(false)}
        </Drawer>
      )}
      <Layout>
        <Header className="app-header" style={{ paddingInline: phone ? 12 : undefined }}>
          <div className="app-header-start">
            {phone && <Button type="text" icon={<MenuOutlined />} aria-label={t('nav.menu')} onClick={() => setDrawerOpen(true)} />}
            <Typography.Text strong className="app-header-title">{compact ? t('app.short') : t('app.name')}</Typography.Text>
            {info.data && info.data.environment !== 'Production' && (
              <Tag color="gold" style={{ marginInlineEnd: 0 }} title={`${t('common.environment')}: ${info.data.environment}`}>
                {!phone && <>{t('common.environment')}: </>}<bdi>{info.data.environment}</bdi>
              </Tag>
            )}
          </div>
          <Space size={phone ? 2 : 8} style={{ flex: 'none' }}>
            <Button type="text" icon={<GlobalOutlined />} onClick={switchLang} aria-label={t('common.language')}>{phone ? null : t('common.language')}</Button>
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
                {!compact && <span className="app-header-user">{name}</span>}
              </Space>
            </Dropdown>
          </Space>
        </Header>
        <Content style={{ padding: phone ? 12 : compact ? 16 : 24, maxWidth: 1440, width: '100%', margin: '0 auto', minWidth: 0 }}>
          <Outlet />
        </Content>
      </Layout>
    </Layout>
  );
}
