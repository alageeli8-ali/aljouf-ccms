import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import ar from './ar';
import en from './en';
import { localStorageSafe } from '../api/client';

export type Lang = 'ar' | 'en';

export const initialLang = (): Lang => (localStorageSafe('ccms.lang') === 'en' ? 'en' : 'ar');

void i18n.use(initReactI18next).init({
  resources: { ar: { translation: ar }, en: { translation: en } },
  lng: initialLang(),
  fallbackLng: 'ar',
  interpolation: { escapeValue: false },
  returnNull: false,
});

export function applyDirection(lang: Lang) {
  document.documentElement.lang = lang;
  document.documentElement.dir = lang === 'ar' ? 'rtl' : 'ltr';
}

export default i18n;
