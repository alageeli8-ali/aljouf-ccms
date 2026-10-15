-- V005: كتالوج الأدوار والصلاحيات (06-rbac) ومفاتيح الإعداد المركزية (07-feature-flags).

INSERT INTO iam.roles (code, scope, name_ar, name_en, sort_order) VALUES
  ('system_admin',            'system',   'مدير النظام',                 'System administrator',        1),
  ('governance_officer',      'system',   'مسؤول المجالس واللجان',        'Governance officer',          2),
  ('executive',               'system',   'الإدارة العليا',               'Executive management',        3),
  ('auditor',                 'system',   'مدقق مخول',                    'Authorized auditor',          4),
  ('external_representative', 'system',   'ممثل جهة خارجية',              'External entity representative', 5),
  ('employee',                'system',   'موظف',                         'Employee',                    6),
  ('chair',                   'body',     'رئيس المجلس أو اللجنة',        'Chair',                       10),
  ('vice_chair',              'body',     'نائب الرئيس',                  'Vice chair',                  11),
  ('secretary',               'body',     'أمين السر',                    'Secretary',                   12),
  ('member',                  'body',     'عضو',                          'Member',                      13),
  ('observer',                'body',     'مدعو أو مراقب',                'Invitee / observer',          14),
  ('task_executor',           'relation', 'مسؤول تنفيذ المهام',           'Task executor',               20),
  ('task_follow_up',          'relation', 'مسؤول متابعة',                 'Follow-up officer',           21);

INSERT INTO iam.permissions (code, scope, description_ar) VALUES
  ('admin.users.manage',    'system', 'إدارة المستخدمين وأدوارهم النظامية'),
  ('admin.settings.view',   'system', 'عرض إعدادات النظام وسجل تغييراتها'),
  ('admin.settings.manage', 'system', 'تعديل إعدادات النظام ومفاتيح الخصائص'),
  ('org.manage',            'system', 'إدارة الهيكل التنظيمي والجهات الخارجية'),
  ('bodies.create',         'system', 'إنشاء مجلس أو لجنة أو فريق عمل'),
  ('reports.executive',     'system', 'تقارير الإدارة العليا المجمعة'),
  ('audit.read',            'system', 'الاطلاع على سجل التدقيق'),
  ('users.directory',       'system', 'البحث في دليل المستخدمين'),
  ('body.view',             'body',   'الاطلاع على الجهة وأعضائها'),
  ('body.update',           'body',   'تعديل بيانات الجهة'),
  ('body.members.manage',   'body',   'إدارة عضويات الجهة'),
  ('meeting.view',          'body',   'الاطلاع على الاجتماعات'),
  ('meeting.manage',        'body',   'إنشاء الاجتماعات وتعديلها وجدول أعمالها'),
  ('minutes.view',          'body',   'الاطلاع على المحاضر'),
  ('minutes.edit',          'body',   'إعداد المحاضر'),
  ('minutes.sign',          'body',   'إقرار المحاضر أو توقيعها'),
  ('minutes.approve',       'body',   'اعتماد المحاضر'),
  ('resolution.view',       'body',   'الاطلاع على القرارات والتوصيات'),
  ('resolution.manage',     'body',   'إدارة القرارات والتوصيات'),
  ('task.view',             'body',   'الاطلاع على المهام'),
  ('task.manage',           'body',   'إنشاء المهام وإسنادها وإلغاؤها'),
  ('task.update',           'task',   'تحديث تقدم المهمة'),
  ('task.verify',           'body',   'التحقق من إنجاز المهام'),
  ('document.view',         'body',   'الاطلاع على المرفقات'),
  ('document.upload',       'body',   'رفع المرفقات'),
  ('reports.body',          'body',   'تقارير الجهة'),
  ('portal.access',         'entity', 'بوابة الجهات الخارجية');

INSERT INTO iam.role_permissions (role_code, permission_code)
SELECT r, p FROM (VALUES
  ('system_admin','admin.users.manage'), ('system_admin','admin.settings.view'), ('system_admin','admin.settings.manage'),
  ('system_admin','org.manage'), ('system_admin','users.directory'),

  ('governance_officer','bodies.create'), ('governance_officer','org.manage'), ('governance_officer','users.directory'),
  ('governance_officer','body.view'), ('governance_officer','body.update'), ('governance_officer','body.members.manage'),
  ('governance_officer','meeting.view'), ('governance_officer','resolution.view'), ('governance_officer','task.view'),
  ('governance_officer','reports.body'),

  ('executive','reports.executive'), ('executive','users.directory'), ('executive','body.view'), ('executive','meeting.view'),
  ('executive','resolution.view'), ('executive','task.view'), ('executive','reports.body'),

  ('auditor','audit.read'), ('auditor','admin.settings.view'), ('auditor','users.directory'),

  ('external_representative','portal.access'), ('external_representative','task.view'), ('external_representative','task.update'),
  ('external_representative','document.view'), ('external_representative','document.upload'),

  ('employee','users.directory'),

  ('chair','body.view'), ('chair','meeting.view'), ('chair','meeting.manage'), ('chair','minutes.view'), ('chair','minutes.approve'),
  ('chair','resolution.view'), ('chair','resolution.manage'), ('chair','task.view'), ('chair','task.manage'), ('chair','task.verify'),
  ('chair','document.view'), ('chair','document.upload'), ('chair','reports.body'),

  ('vice_chair','body.view'), ('vice_chair','meeting.view'), ('vice_chair','meeting.manage'), ('vice_chair','minutes.view'),
  ('vice_chair','minutes.sign'), ('vice_chair','resolution.view'), ('vice_chair','task.view'), ('vice_chair','document.view'),
  ('vice_chair','document.upload'), ('vice_chair','reports.body'),

  ('secretary','body.view'), ('secretary','body.update'), ('secretary','meeting.view'), ('secretary','meeting.manage'),
  ('secretary','minutes.view'), ('secretary','minutes.edit'), ('secretary','resolution.view'), ('secretary','resolution.manage'),
  ('secretary','task.view'), ('secretary','task.manage'), ('secretary','task.update'), ('secretary','task.verify'),
  ('secretary','document.view'), ('secretary','document.upload'), ('secretary','reports.body'),

  ('member','body.view'), ('member','meeting.view'), ('member','minutes.view'), ('member','minutes.sign'),
  ('member','resolution.view'), ('member','task.view'), ('member','document.view'),

  ('observer','body.view'), ('observer','meeting.view'), ('observer','minutes.view'), ('observer','resolution.view'),
  ('observer','document.view'),

  ('task_executor','task.view'), ('task_executor','task.update'), ('task_executor','document.view'), ('task_executor','document.upload'),
  ('task_follow_up','task.view'), ('task_follow_up','task.update'), ('task_follow_up','document.view'), ('task_follow_up','document.upload')
) AS t(r, p);

INSERT INTO config.feature_flags (key, category, value_type, value, default_value, allowed_values, parent_key, connector_code, name_ar, name_en, description_ar, sort_order) VALUES
  ('AI_ENABLED', 'ai', 'boolean', 'false', 'false', NULL, NULL, 'ai',
   'الذكاء الاصطناعي', 'Artificial intelligence', 'المفتاح الأعلى لكل وظائف الذكاء الاصطناعي؛ تعطيله يعطّل كل ما تحته', 10),
  ('AI_MINUTES_SUMMARY_ENABLED', 'ai', 'boolean', 'false', 'false', NULL, 'AI_ENABLED', 'ai',
   'تلخيص المحاضر والمذكرات', 'Minutes summary', 'ملخصات آلية للمذكرات والمحاضر تُعرض كمقترح', 11),
  ('AI_DECISION_EXTRACTION_ENABLED', 'ai', 'boolean', 'false', 'false', NULL, 'AI_ENABLED', 'ai',
   'استخراج القرارات', 'Decision extraction', 'اقتراح القرارات والتوصيات من نص المحضر', 12),
  ('AI_TASK_EXTRACTION_ENABLED', 'ai', 'boolean', 'false', 'false', NULL, 'AI_ENABLED', 'ai',
   'استخراج المهام', 'Task extraction', 'اقتراح المهام ومكلفيها ومواعيدها من القرارات', 13),
  ('AI_SMART_SEARCH_ENABLED', 'ai', 'boolean', 'false', 'false', NULL, 'AI_ENABLED', 'ai',
   'البحث الذكي', 'Smart search', 'البحث الدلالي والمساعد على الأرشيف ضمن صلاحيات المستخدم', 14),
  ('AI_AUDIO_TRANSCRIPTION_ENABLED', 'ai', 'boolean', 'false', 'false', NULL, 'AI_ENABLED', 'ai',
   'التفريغ الصوتي', 'Audio transcription', 'تفريغ تسجيلات الاجتماعات بموافقة موثقة من الحضور', 15),

  ('E_SIGNATURE_ENABLED', 'esignature', 'boolean', 'false', 'false', NULL, NULL, 'esign',
   'التوقيع الإلكتروني', 'Electronic signature', 'المفتاح الأعلى لوحدة التوقيع؛ النظام يعمل بالإقرار الداخلي عند تعطيله', 20),
  ('E_SIGNATURE_PROVIDER', 'esignature', 'enum', 'none', 'none', ARRAY['none','sandbox','national_provider'], 'E_SIGNATURE_ENABLED', 'esign',
   'مزوّد التوقيع', 'Signature provider', 'none: غير محدد؛ sandbox: محاكي للاختبار فقط؛ national_provider: مزوّد معتمد (بعد التعاقد)', 21),
  ('E_SIGNATURE_METHOD', 'esignature', 'enum', 'remote_certificate', 'remote_certificate', ARRAY['remote_certificate','otp_confirmation'], 'E_SIGNATURE_ENABLED', 'esign',
   'طريقة التوقيع', 'Signature method', 'remote_certificate: توقيع رقمي عن بعد بشهادة المزوّد؛ otp_confirmation: توقيع إلكتروني برمز تحقق', 22),
  ('E_SIGNATURE_MEMBER_REQUIRED', 'esignature', 'boolean', 'false', 'false', NULL, 'E_SIGNATURE_ENABLED', 'esign',
   'توقيع الأعضاء إلزامي', 'Member signature required', 'يُطلب توقيع الأعضاء الحاضرين إلكترونيًا قبل الاعتماد', 23),
  ('E_SIGNATURE_CHAIR_REQUIRED', 'esignature', 'boolean', 'false', 'false', NULL, 'E_SIGNATURE_ENABLED', 'esign',
   'توقيع الرئيس إلزامي', 'Chair signature required', 'يوقّع الرئيس إلكترونيًا على النسخة المعتمدة', 24),
  ('E_SIGNATURE_SECRETARY_REQUIRED', 'esignature', 'boolean', 'false', 'false', NULL, 'E_SIGNATURE_ENABLED', 'esign',
   'توقيع أمين السر إلزامي', 'Secretary signature required', 'يوقّع أمين السر إلكترونيًا على المحضر بصفته معدّه', 25),
  ('E_SIGNATURE_SEQUENTIAL', 'esignature', 'boolean', 'false', 'false', NULL, 'E_SIGNATURE_ENABLED', 'esign',
   'التوقيع المتسلسل', 'Sequential signing', 'التوقيع بترتيب محدد بدل التوقيع المتوازي', 26),
  ('E_SIGNATURE_PDF_OUTPUT', 'esignature', 'boolean', 'true', 'true', NULL, 'E_SIGNATURE_ENABLED', 'esign',
   'إخراج PDF موقّع', 'Signed PDF output', 'إصدار ملف PDF يحمل التوقيعات بصيغة PAdES', 27),
  ('E_SIGNATURE_REAUTH_REQUIRED', 'esignature', 'boolean', 'true', 'true', NULL, 'E_SIGNATURE_ENABLED', 'esign',
   'إعادة التحقق قبل التوقيع', 'Re-authentication before signing', 'يُطلب من الموقّع إعادة إثبات هويته قبل كل توقيع', 28),

  ('EXTERNAL_PORTAL_ENABLED', 'platform', 'boolean', 'false', 'false', NULL, NULL, NULL,
   'بوابة الجهات الخارجية', 'External entity portal', 'يسمح لممثلي الجهات الخارجية بالاطلاع على مهام جهتهم وتحديثها', 30),
  ('EMAIL_INTEGRATION_ENABLED', 'integration', 'boolean', 'false', 'false', NULL, NULL, 'email',
   'تكامل البريد', 'Email integration', 'إرسال الإشعارات بالبريد؛ عند تعطيله تُسجَّل الرسائل في صندوق الصادر ولا تُرسل', 40),
  ('CORRESPONDENCE_INTEGRATION_ENABLED', 'integration', 'boolean', 'false', 'false', NULL, NULL, 'correspondence',
   'تكامل نظام المراسلات', 'Correspondence integration', 'إنشاء الخطابات الصادرة للجهات الخارجية في نظام المراسلات', 41),
  ('SSO_ENABLED', 'integration', 'boolean', 'false', 'false', NULL, NULL, 'sso',
   'الدخول الموحد', 'Single sign-on', 'الدخول عبر Microsoft Entra ID / Active Directory', 42);

INSERT INTO config.connector_status (code, status, detail) VALUES
  ('ai', 'not_configured', 'لم يُفحص بعد'),
  ('esign', 'not_configured', 'لم يُفحص بعد'),
  ('email', 'not_configured', 'لم يُفحص بعد'),
  ('correspondence', 'not_configured', 'لم يُفحص بعد'),
  ('sso', 'not_configured', 'لم يُفحص بعد');
