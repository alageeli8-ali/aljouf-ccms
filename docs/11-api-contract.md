# 11 — عقد الواجهات البرمجية (REST)

المصدر الملزم للعقد هو ملف OpenAPI 3.1 المولَّد من الشيفرة في كل بناء (`/openapi/v1.json`)، ويُراجع الفرق فيه ضمن كل طلب دمج. هذه الوثيقة تحدد الاصطلاحات والفهرس الذي يُبنى عليه.

## 1. الاصطلاحات

| البند | القاعدة |
|---|---|
| المسار | `/api/v1/{resource}`؛ أسماء جمع بصيغة kebab-case |
| الإصدار | في المسار؛ التغيير الكاسر يتطلب `/v2` مع إبقاء `/v1` فترة انتقالية |
| الصيغة | JSON بأسماء camelCase؛ التواريخ ISO 8601 (`timestamptz` بـ `Z`، والتاريخ الصرف `YYYY-MM-DD`) |
| اللغة | `Accept-Language: ar` (افتراضي) أو `en`؛ الحقول ثنائية اللغة تُعاد كلاهما (`titleAr`, `titleEn`) |
| الأخطاء | `application/problem+json` (RFC 9457) مع `type` من `/problems/{code}`، و`errors` للتحقق، و`traceId` |
| التزامن | `ETag` في القراءة، و`If-Match` إلزامي في PUT/PATCH/الانتقالات؛ عدم التطابق → 412 |
| عدم التكرار | `Idempotency-Key` لعمليات الإنشاء والانتقالات الحساسة (اعتماد، تصويت، إقرار)؛ يُحفظ 24 ساعة |
| الترقيم | `?page=1&pageSize=25` (حد 100)، والرد `{ items, page, pageSize, total }` |
| الترتيب والتصفية | `?sort=-startsAt&status=scheduled&bodyId=...` |
| الانتقالات | أفعال صريحة `POST /{resource}/{id}/{action}` لا PATCH على الحالة |
| الصلاحية | السجل خارج النطاق → 404؛ داخل النطاق بلا صلاحية فعل → 403 `problems/forbidden-action` |
| المفاتيح | نقطة نهاية لقدرة معطلة → 404 `problems/feature-disabled` مع اسم المفتاح |
| الأمن | كعكة الجلسة + ترويسة `X-CSRF-Token` لكل طلب غير GET |
| حد المعدل | 429 مع `Retry-After` |

### رموز المشكلات الموحدة

`validation`، `not-found`، `forbidden-action`، `feature-disabled`، `concurrency-conflict`، `invalid-transition`، `quorum-not-met`، `not-eligible-to-vote`، `signoffs-incomplete`، `separation-of-duties`، `classification-restricted`، `integration-unavailable`، `rate-limited`.

## 2. فهرس نقاط النهاية

### 2.1 النظام والجلسة

| الطريقة والمسار | الوصف | الصلاحية |
|---|---|---|
| `GET /auth/login` · `GET /auth/callback` · `POST /auth/logout` | الدخول الموحد (BFF) | — |
| `GET /api/v1/me` | المستخدم، وأدواره، وعضوياته، وتفضيلاته | مصادق |
| `PATCH /api/v1/me/preferences` | اللغة، والتقويم، والملخص اليومي | مصادق |
| `GET /api/v1/features?bodyId=` | لقطة المفاتيح الفعلية | مصادق |
| `GET /api/v1/health/live` · `/ready` | فحص الصحة | — (شبكة داخلية) |

### 2.2 IAM / ORG

| الطريقة والمسار | الوصف | الصلاحية |
|---|---|---|
| `GET/POST /api/v1/users` · `GET/PATCH /api/v1/users/{id}` | إدارة المستخدمين | `admin.users` |
| `POST /api/v1/users/{id}/system-roles` · `DELETE .../system-roles/{assignmentId}` | الأدوار النظامية | `admin.roles` |
| `GET/POST /api/v1/delegations` · `POST /api/v1/delegations/{id}/revoke` | التفويض | المفوِّض / المسؤول |
| `GET /api/v1/directory?q=` | البحث في الدليل | مصادق |
| `GET/POST /api/v1/org-units` · `PATCH /api/v1/org-units/{id}` · `POST /api/v1/org-units/import` | الهيكل | `admin.users` |
| `GET/POST /api/v1/external-entities` · `PATCH .../{id}` | الجهات الخارجية | `bodies.update` أو `admin.users` |

### 2.3 BOD

| الطريقة والمسار | الوصف | الصلاحية |
|---|---|---|
| `GET /api/v1/bodies` · `POST /api/v1/bodies` | القائمة والإنشاء | `bodies.view` / `bodies.create` |
| `GET/PATCH /api/v1/bodies/{id}` | التفاصيل والتعديل | `bodies.view` / `bodies.update` |
| `POST /api/v1/bodies/{id}/activate` · `/suspend` · `/resume` · `/dissolve` · `/archive` | انتقالات الحالة | `bodies.change_status` |
| `GET/POST /api/v1/bodies/{id}/members` · `PATCH/POST .../members/{memberId}/end` | العضويات | `bodies.manage_members` |
| `GET/POST /api/v1/bodies/{id}/policies` · `GET .../policies/effective?on=` | إصدارات السياسة | `bodies.manage_policy` / `bodies.view` |
| `GET /api/v1/bodies/{id}/members/as-of?date=` | التشكيل في تاريخ | `bodies.view` |

### 2.4 MTG / ATT

| الطريقة والمسار | الوصف | الصلاحية |
|---|---|---|
| `GET /api/v1/meetings?bodyId=&from=&to=&status=` · `POST /api/v1/meetings` | القائمة والإنشاء | `meetings.view` / `meetings.create` |
| `GET/PATCH /api/v1/meetings/{id}` | — | `meetings.view` / `meetings.update` |
| `POST /api/v1/meetings/{id}/schedule` | تثبيت الموعد والترقيم والسياسة | `meetings.create` |
| `POST /api/v1/meetings/{id}/publish-agenda` | النشر وإرسال الدعوات | `meetings.publish_agenda` |
| `POST /api/v1/meetings/{id}/postpone` · `/cancel` · `/open` · `/close` · `/mark-quorum-failed` | انتقالات | حسب 06 §3 |
| `GET/POST /api/v1/meetings/{id}/agenda-items` · `PATCH/DELETE .../{itemId}` · `POST .../reorder` · `POST .../{itemId}/carry-over` | جدول الأعمال | `meetings.update` |
| `POST /api/v1/bodies/{id}/agenda-requests` · `POST /api/v1/agenda-requests/{id}/accept` · `/reject` | طلبات الإدراج | `meetings.agenda_requests` مفعّل |
| `GET/POST /api/v1/meetings/{id}/invitations` · `POST .../invitations/{invId}/respond` | الدعوات والردود | المدعو / أمين السر |
| `GET/PUT /api/v1/meetings/{id}/attendance` · `POST .../attendance/{userId}/depart` · `POST .../check-in` | الحضور | `attendance.record` / `attendance.self_check_in` |
| `POST /api/v1/meetings/{id}/quorum-checks` · `GET .../quorum-checks` · `GET .../quorum-forecast` | النصاب | `attendance.record` / `meetings.view` |
| `POST /api/v1/agenda-items/{id}/conflicts` | إفصاح تعارض | `conflict.declare` |

### 2.5 DLB

| الطريقة والمسار | الوصف | الصلاحية |
|---|---|---|
| `GET/PUT /api/v1/agenda-items/{id}/notes` | ملاحظات المداولة | `deliberation.write_notes` |
| `GET/POST /api/v1/agenda-items/{id}/motions` · `PATCH /api/v1/motions/{id}` | المقترحات | `motions.manage` |
| `POST /api/v1/motions/{id}/open` · `/close` · `/withdraw` · `/adopt-by-consensus` | الانتقالات | `votes.open_close` / `motions.manage` |
| `POST /api/v1/motions/{id}/votes` | الإدلاء بالصوت | `votes.cast` |
| `PUT /api/v1/motions/{id}/show-of-hands` | تسجيل رفع الأيدي | `votes.record_show_of_hands` |
| `GET /api/v1/motions/{id}/result` | النتيجة (بعد الإغلاق، أو حية لمن يملك) | `meetings.view` |
| `POST /api/v1/motions/{id}/dissents` | رأي مخالف/تحفظ | عضو مصوت |
| `GET/POST /api/v1/bodies/{id}/circulations` · `POST /api/v1/circulations/{id}/open` · `/cancel` | التمرير | `circulations.manage` |

### 2.6 MIN

| الطريقة والمسار | الوصف | الصلاحية |
|---|---|---|
| `POST /api/v1/meetings/{id}/minutes` · `POST /api/v1/circulations/{id}/minutes` | إنشاء المسودة من بيانات الاجتماع | `minutes.edit` |
| `GET /api/v1/minutes/{id}` · `GET .../versions` · `GET .../versions/{no}` · `GET .../diff?from=&to=` | القراءة والإصدارات والفروقات | `minutes.view` |
| `PUT /api/v1/minutes/{id}/draft` | حفظ المسودة (لا يُنشئ إصدارًا) | `minutes.edit` |
| `POST /api/v1/minutes/{id}/submit-internal-review` · `/submit-member-review` · `/request-signoffs` · `/submit-for-approval` | الانتقالات (كل منها يُنشئ إصدارًا ببصمة إن تغير المحتوى) | `minutes.submit_for_review` |
| `GET/POST /api/v1/minutes/{id}/comments` · `POST /api/v1/minutes-comments/{id}/resolve` | الملاحظات | `minutes.comment` / `minutes.edit` |
| `GET /api/v1/minutes/{id}/signoffs` · `POST .../signoffs` | الإقرار (body: `contentHash`, `reservation`, `method`) | `minutes.sign_off` |
| `POST /api/v1/minutes/{id}/signoffs/wet-evidence` | دليل التوقيع الورقي | `minutes.record_wet_signature` |
| `POST /api/v1/minutes/{id}/approve` · `/return` | اعتماد أو إعادة (body: `versionNo`, `contentHash`) | `minutes.approve` |
| `POST /api/v1/minutes/{id}/amendments` | بدء محضر تصحيح | `minutes.amend` |
| `GET /api/v1/minutes/{id}/pdf` | PDF المعتمد | `minutes.view` |
| `GET /api/v1/verify/{verificationCode}` | صفحة التحقق (حالة وبصمة، بلا محتوى) | مصادق |

### 2.7 RES / TSK

| الطريقة والمسار | الوصف | الصلاحية |
|---|---|---|
| `GET/POST /api/v1/minutes/{id}/resolutions` · `PATCH/DELETE /api/v1/resolutions/{id}` | المقترحة في المسودة | `minutes.edit` |
| `GET /api/v1/resolutions?bodyId=&status=&year=` · `GET /api/v1/resolutions/{id}` | السجل | `resolutions.view` |
| `POST /api/v1/resolutions/{id}/start-execution` · `/mark-executed` · `/close` · `/cancel` · `/supersede` · `/refer` | الانتقالات | حسب 06 §3 |
| `GET/POST /api/v1/resolutions/{id}/tasks` · `POST /api/v1/minutes/{id}/tasks` | المهام المسودة | `minutes.edit` |
| `GET /api/v1/tasks?bodyId=&status=&assignee=&overdue=` · `GET /api/v1/tasks/{id}` | القائمة والتفاصيل | `tasks.view_body` / `tasks.view_own` |
| `GET /api/v1/my/tasks` | مهامي | مصادق |
| `PATCH /api/v1/tasks/{id}` | تعديل (مسودة) أو موعد/أولوية (أمين السر) | `tasks.manage` |
| `POST /api/v1/tasks/{id}/updates` | تحديث تقدم/تعليق/دليل | `tasks.update_progress` |
| `POST /api/v1/tasks/{id}/start` · `/submit-for-verification` · `/verify` · `/return` · `/reassign` · `/hold` · `/resume` · `/cancel` | الانتقالات | حسب 06 §3 |
| `POST /api/v1/tasks/{id}/extension-requests` · `POST /api/v1/extension-requests/{id}/approve` · `/reject` · `/withdraw` | التمديد | `tasks.request_extension` / `tasks.decide_extension` |
| `POST /api/v1/tasks/bulk-update` | تحديث جماعي | `tasks.manage` |
| `PUT /api/v1/tasks/{id}/correspondence` | رقم الخطاب الصادر | `tasks.manage` |

### 2.8 DOC / NTF / RPT / SRC

| الطريقة والمسار | الوصف | الصلاحية |
|---|---|---|
| `POST /api/v1/documents` (multipart، مع `ownerType`, `ownerId`, `purpose`) | الرفع | صلاحية تعديل المالك |
| `GET /api/v1/documents/{id}` · `GET .../content` · `POST .../versions` | البيانات والتنزيل والإصدارات | مشتقة من المالك |
| `GET /api/v1/notifications` · `POST .../{id}/read` · `POST .../read-all` · `GET .../unread-count` · `GET /api/v1/notifications/stream` (SSE) | الإشعارات | مصادق |
| `GET /api/v1/dashboard/me` · `/executive` · `/bodies/{id}` | اللوحات | حسب الدور |
| `GET /api/v1/reports/{reportCode}?...` · `GET .../export?format=xlsx|pdf` | التقارير | `reports.*` |
| `GET /api/v1/search?q=&bodyId=&type=&from=&to=` | البحث | `search.query` |
| `POST /api/v1/legal-holds` · `POST .../{id}/release` | الحجز النظامي | `governance_officer` |

### 2.9 ADM / AUD

| الطريقة والمسار | الوصف | الصلاحية |
|---|---|---|
| `GET /api/v1/admin/feature-flags` · `PUT .../{key}` (body: `enabled`, `reason`) · `GET .../{key}/impact` · `GET .../{key}/history` | المفاتيح | `admin.feature_flags` |
| `PUT /api/v1/admin/feature-flags/{key}/overrides/{bodyId}` · `DELETE ...` | تجاوز الجهة | `admin.body_feature_overrides` |
| `GET/PUT /api/v1/admin/settings/{key}` | الإعدادات | `admin.settings` |
| `GET/POST/PATCH /api/v1/admin/lookups` · `/admin/holidays` · `/admin/templates` | القوائم والتقويم والقوالب | `admin.settings` / `admin.templates` |
| `GET /api/v1/admin/outbox` · `POST .../{id}/retry` | الصادر | `admin.outbox` |
| `GET /api/v1/admin/health` · `POST /api/v1/admin/connectors/{code}/test` | الصحة والموصلات | `admin.health` |
| `GET /api/v1/audit/events?...` · `GET .../export` · `GET /api/v1/audit/integrity` | التدقيق | `audit.read` / `audit.export` |

### 2.10 SIG (اختياري — `module.esignature`)

| الطريقة والمسار | الوصف | الصلاحية |
|---|---|---|
| `GET /api/v1/esign/requests?minutesId=` | طلبات التوقيع لمحضر | `minutes.view` |
| `POST /api/v1/esign/requests/{id}/start` | بدء جلسة التوقيع لدى المزوّد (يعيد رابط التوجيه) | الموقِّع نفسه |
| `POST /api/v1/esign/requests/{id}/resend` · `/cancel` | إعادة/إلغاء | `minutes.edit` |
| `POST /api/v1/esign/callbacks/{providerCode}` | استقبال نتيجة المزوّد (توقيع الرسالة يُتحقق منه) | المزوّد |
| `GET /api/v1/esign/artifacts/{id}` | الأدلة وتقرير التحقق | `minutes.view` / `auditor` |

### 2.11 AI (اختياري — `module.ai`)

| الطريقة والمسار | الوصف | المفتاح |
|---|---|---|
| `POST /api/v1/ai/agenda-items/{id}/summary` | تلخيص المذكرة | `ai.memo_summary` |
| `PUT /api/v1/ai/meetings/{id}/transcript` | ملاحظات/تفريغ الاجتماع (مع إقرار الموافقة) | `ai.minutes_draft` |
| `POST /api/v1/ai/minutes/{id}/draft` | مسودة محضر | `ai.minutes_draft` |
| `POST /api/v1/ai/minutes/{id}/extract-actions` | اقتراح القرارات والمهام | `ai.action_extraction` |
| `POST /api/v1/ai/assistant/ask` | سؤال المساعد | `ai.assistant_qa` |
| `POST /api/v1/ai/translate` | ترجمة نص | `ai.translation` |
| `GET /api/v1/ai/jobs/{id}` · `GET /api/v1/ai/suggestions/{id}` | حالة المهمة والاقتراح | طالبها |
| `POST /api/v1/ai/suggestions/{id}/accept` (body: العناصر المقبولة وتعديلاتها) · `/reject` | القبول البشري — يستدعي أوامر الوحدات الأساسية | صاحب الصلاحية الأساسية المقابلة |

## 3. مثال: اعتماد المحضر

```http
POST /api/v1/minutes/0192f0c4-.../approve
If-Match: "w/8812"
Idempotency-Key: 6b1f...
X-CSRF-Token: ...
Content-Type: application/json

{ "versionNo": 4, "contentHash": "9f2c...e1", "note": "" }
```

ردود محتملة:

| الحالة | المعنى |
|---|---|
| 200 | `{ status: "approved", approvedVersionNo: 4, issuedResolutions: [...], activatedTasks: [...], pdfDocumentId }` |
| 409 `signoffs-incomplete` | `{ missing: [{ userId, name }] }` |
| 409 `invalid-transition` | المحضر ليس في `pending_approval` |
| 409 `separation-of-duties` | المستخدم هو مُعد الإصدار |
| 412 | ETag قديم — تغيّر المحضر |
| 422 `validation` | البصمة لا تطابق الإصدار |
