import { StrictMode, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { App as AntApp, ConfigProvider } from 'antd';
import arEG from 'antd/locale/ar_EG';
import enUS from 'antd/locale/en_US';
import dayjs from 'dayjs';
import 'dayjs/locale/ar';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ApiError } from './api/client';
import { AuthProvider } from './auth/AuthContext';
import i18n, { applyDirection, initialLang, type Lang } from './i18n';
import { theme } from './theme';
import App from './App';
import './index.css';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: (count, error) => !(error instanceof ApiError && error.status < 500) && count < 2,
      refetchOnWindowFocus: false,
      staleTime: 15_000,
    },
  },
});

function Root() {
  const [lang, setLang] = useState<Lang>(initialLang());
  useEffect(() => {
    applyDirection(lang);
    dayjs.locale(lang === 'ar' ? 'ar' : 'en');
    const handler = (l: string) => setLang(l === 'en' ? 'en' : 'ar');
    i18n.on('languageChanged', handler);
    return () => i18n.off('languageChanged', handler);
  }, [lang]);
  return (
    <ConfigProvider direction={lang === 'ar' ? 'rtl' : 'ltr'} locale={lang === 'ar' ? arEG : enUS} theme={theme}>
      <AntApp>
        <QueryClientProvider client={queryClient}>
          <BrowserRouter>
            <AuthProvider>
              <App />
            </AuthProvider>
          </BrowserRouter>
        </QueryClientProvider>
      </AntApp>
    </ConfigProvider>
  );
}

createRoot(document.getElementById('root')!).render(<StrictMode><Root /></StrictMode>);
