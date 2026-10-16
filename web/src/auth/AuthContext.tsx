import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { get, post, refreshCsrf, resetCsrf, setUnauthenticatedHandler, ApiError } from '../api/client';
import type { FeatureMap, Me } from '../api/types';

interface AuthState {
  me: Me | null;
  features: FeatureMap;
  loading: boolean;
  /** صلاحية نظامية — لإظهار عناصر الواجهة فقط؛ القرار النهائي في الخادم */
  can: (permission: string) => boolean;
  hasRole: (role: string) => boolean;
  feature: (key: string) => boolean;
  reload: () => Promise<void>;
  login: (email: string, password: string) => Promise<void>;
  logout: (everywhere?: boolean) => Promise<void>;
}

const Ctx = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [features, setFeatures] = useState<FeatureMap>({});
  const [loading, setLoading] = useState(true);
  const qc = useQueryClient();
  const meRef = useRef<Me | null>(null);
  meRef.current = me;

  const reload = useCallback(async () => {
    try {
      const [m, f] = await Promise.all([get<Me>('/api/v1/me'), get<FeatureMap>('/api/v1/features')]);
      setMe(m);
      setFeatures(f);
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) setMe(null);
      else throw e;
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    // انتهاء الجلسة أثناء الاستخدام فقط؛ لا يُمسح شيء قبل الدخول حتى لا تُعاد تهيئة صفحة الدخول
    setUnauthenticatedHandler(() => {
      if (!meRef.current) return;
      setMe(null);
      qc.clear();
    });
    void reload();
  }, [reload, qc]);

  const login = useCallback(async (email: string, password: string) => {
    await refreshCsrf();
    await post('/api/v1/auth/login', { email, password });
    resetCsrf(); // الرمز مرتبط بالهوية؛ يُجدد بعد الدخول
    await reload();
  }, [reload]);

  const logout = useCallback(async (everywhere = false) => {
    try {
      await post(everywhere ? '/api/v1/auth/logout-all' : '/api/v1/auth/logout');
    } finally {
      resetCsrf();
      setMe(null);
      qc.clear();
    }
  }, [qc]);

  const value = useMemo<AuthState>(() => ({
    me,
    features,
    loading,
    can: (p) => !!me?.permissions.includes(p),
    hasRole: (r) => !!me && (me.systemRoles.includes(r) || me.bodyRoles.some((b) => b.role === r)),
    feature: (k) => !!features[k]?.enabled,
    reload,
    login,
    logout,
  }), [me, features, loading, reload, login, logout]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useAuth() {
  const c = useContext(Ctx);
  if (!c) throw new Error('AuthProvider missing');
  return c;
}
