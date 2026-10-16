// ينسخ واجهة Swagger UI (من حزمة npm swagger-ui-dist) إلى wwwroot/swagger دون سكربتات مضمنة، لتوافق سياسة أمن المحتوى.
import { cpSync, mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const dist = dirname(require.resolve('swagger-ui-dist/package.json'));
const out = new URL('../../src/backend/Ccms.Host/wwwroot/swagger/', import.meta.url).pathname;
mkdirSync(out, { recursive: true });
for (const f of ['swagger-ui.css', 'swagger-ui-bundle.js', 'swagger-ui-standalone-preset.js']) cpSync(join(dist, f), join(out, f));
writeFileSync(join(out, 'index.html'), `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>CCMS API — Swagger</title>
<link rel="stylesheet" href="swagger-ui.css"></head>
<body><div id="swagger-ui"></div><script src="swagger-ui-bundle.js"></script><script src="init.js"></script></body></html>`);
writeFileSync(join(out, 'init.js'), `window.ui = SwaggerUIBundle({ url: '/openapi/v1.json', dom_id: '#swagger-ui', deepLinking: true });`);
console.log('Swagger UI → ' + out);
