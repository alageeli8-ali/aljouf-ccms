/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// البناء يُخرج إلى wwwroot في Ccms.Host فتُخدم الواجهة من الأصل نفسه للواجهات البرمجية (BFF — ADR-004)
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/backend/Ccms.Host/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 2500,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://127.0.0.1:5080',
      '/auth': 'http://127.0.0.1:5080',
      '/openapi': 'http://127.0.0.1:5080',
    },
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
