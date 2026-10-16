import { Result, Spin } from 'antd';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useAuth } from './auth/AuthContext';
import AppLayout from './layout/AppLayout';
import BodiesPage from './pages/BodiesPage';
import BodyDetailPage from './pages/BodyDetailPage';
import DashboardPage from './pages/DashboardPage';
import LoginPage from './pages/LoginPage';
import MeetingDetailPage from './pages/MeetingDetailPage';
import MeetingsPage from './pages/MeetingsPage';
import MembershipsPage from './pages/MembershipsPage';
import { MinutesDetailPage, MinutesListPage, ResolutionsPage } from './pages/MinutesPages';
import { NotificationsPage, ReportsPage } from './pages/ReportsAndNotifications';
import SettingsPage from './pages/SettingsPage';
import { PortalPage, TasksPage } from './pages/TasksPages';
import UsersPage from './pages/UsersPage';

function RequireAuth({ children }: { children: ReactNode }) {
  const { me, loading } = useAuth();
  const loc = useLocation();
  if (loading) return <div style={{ display: 'grid', placeItems: 'center', height: '100vh' }}><Spin size="large" /></div>;
  if (!me) return <Navigate to={`/login?returnUrl=${encodeURIComponent(loc.pathname + loc.search)}`} replace />;
  return <>{children}</>;
}

/** يخفي الصفحة عمن لا يملك الصلاحية؛ الخادم يرفض الطلبات على أي حال (NFR-SEC-08). */
function RequirePermission({ permission, children }: { permission: string; children: ReactNode }) {
  const { can } = useAuth();
  const { t } = useTranslation();
  return can(permission) ? <>{children}</> : <Result status="403" title="403" subTitle={t('common.forbidden')} />;
}

export default function App() {
  const { t } = useTranslation();
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<RequireAuth><AppLayout /></RequireAuth>}>
        <Route index element={<DashboardPage />} />
        <Route path="dashboard" element={<DashboardPage />} />
        <Route path="bodies" element={<BodiesPage />} />
        <Route path="bodies/:id" element={<BodyDetailPage />} />
        <Route path="memberships" element={<MembershipsPage />} />
        <Route path="meetings" element={<MeetingsPage />} />
        <Route path="meetings/:id" element={<MeetingDetailPage />} />
        <Route path="minutes" element={<MinutesListPage />} />
        <Route path="minutes/:id" element={<MinutesDetailPage />} />
        <Route path="resolutions" element={<ResolutionsPage />} />
        <Route path="tasks" element={<TasksPage />} />
        <Route path="tasks/:id" element={<TasksPage />} />
        <Route path="portal" element={<PortalPage />} />
        <Route path="reports" element={<ReportsPage />} />
        <Route path="notifications" element={<NotificationsPage />} />
        <Route path="users" element={<RequirePermission permission="admin.users.manage"><UsersPage /></RequirePermission>} />
        <Route path="settings" element={<RequirePermission permission="admin.settings.view"><SettingsPage /></RequirePermission>} />
        <Route path="*" element={<Result status="404" title="404" subTitle={t('common.notFound')} />} />
      </Route>
    </Routes>
  );
}
