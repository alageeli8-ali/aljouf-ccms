// التواريخ: تُخزن بـ UTC وتُعرض بتوقيت الرياض، ميلاديًا أو هجريًا (أم القرى) حسب تفضيل المستخدم (NFR-USA-02).
export type CalendarPref = 'gregorian' | 'hijri';

const TZ = 'Asia/Riyadh';

function locale(lang: string, calendar: CalendarPref) {
  if (calendar === 'hijri') return lang === 'ar' ? 'ar-SA-u-ca-islamic-umalqura-nu-latn' : 'en-SA-u-ca-islamic-umalqura';
  return lang === 'ar' ? 'ar-SA-u-ca-gregory-nu-latn' : 'en-GB';
}

export function formatDateTime(value: string | null | undefined, lang: string, calendar: CalendarPref = 'gregorian'): string {
  if (!value) return '—';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '—';
  return new Intl.DateTimeFormat(locale(lang, calendar), {
    timeZone: TZ, year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
  }).format(d);
}

export function formatDate(value: string | null | undefined, lang: string, calendar: CalendarPref = 'gregorian'): string {
  if (!value) return '—';
  // التاريخ الصرف (YYYY-MM-DD) يُعامل كتاريخ محلي دون إزاحة زمنية
  const d = /^\d{4}-\d{2}-\d{2}$/.test(value) ? new Date(`${value}T12:00:00+03:00`) : new Date(value);
  if (Number.isNaN(d.getTime())) return '—';
  return new Intl.DateTimeFormat(locale(lang, calendar), { timeZone: TZ, year: 'numeric', month: 'short', day: 'numeric' }).format(d);
}

export function dualDate(value: string | null | undefined, lang: string): string {
  if (!value) return '—';
  return `${formatDate(value, lang, 'gregorian')} — ${formatDate(value, lang, 'hijri')}`;
}

export function percent(part: number, total: number): number {
  return total > 0 ? Math.round((part / total) * 100) : 0;
}
