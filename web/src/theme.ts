import type { ThemeConfig } from 'antd';

// ألوان هوية المكتب الاستراتيجي لتطوير منطقة الجوف: الأخضر 006837، والبني 553D26، والذهبي B28655
export const brand = {
  green: '#006837',
  greenDark: '#00512b',
  greenSoft: '#e6f1ec',
  brown: '#553D26',
  tan: '#B28655',
  tanSoft: '#f6efe6',
  ink: '#1f2a24',
  muted: '#5f6b64',
  surface: '#ffffff',
  canvas: '#f4f5f1',
  line: '#dfe3dc',
};

export const theme: ThemeConfig = {
  token: {
    colorPrimary: brand.green,
    colorLink: brand.green,
    colorInfo: brand.green,
    colorWarning: '#b7791f',
    colorError: '#b42318',
    colorSuccess: '#2f7d32',
    colorText: brand.ink,
    colorTextSecondary: brand.muted,
    colorBorder: brand.line,
    colorBgLayout: brand.canvas,
    borderRadius: 6,
    fontFamily: '"Plex Arabic", "IBM Plex Sans Arabic", system-ui, -apple-system, "Segoe UI", sans-serif',
    fontSize: 14,
  },
  components: {
    Layout: { siderBg: brand.brown, triggerBg: '#4a3521', headerBg: brand.surface, headerHeight: 60, headerPadding: '0 20px' },
    Menu: {
      darkItemBg: brand.brown,
      darkSubMenuItemBg: '#4a3521',
      darkItemSelectedBg: brand.green,
      darkItemColor: '#e9dfd2',
      darkItemHoverColor: '#ffffff',
      darkGroupTitleColor: brand.tan,
    },
    Table: { headerBg: '#eef1ec', headerColor: brand.ink, rowHoverBg: '#f6f8f4' },
    Card: { headerFontSize: 15 },
    Button: { primaryShadow: 'none' },
  },
};
