-- V014: فهارس الأداء الناتجة عن اختبار الحجم (الوثيقة 24 §6).
-- مشغّل سلسلة التدقيق يقرأ آخر حدث بـ ORDER BY chain_seq DESC NULLS LAST، والفهرس الفريد (ASC) لا يخدم هذا الترتيب،
-- فكان كل حدث تدقيق يمسح الجدول كاملًا (يزداد بطء كل عملية مدققة مع نمو السجل). هذا الفهرس يجعل القراءة فورية.
CREATE INDEX IF NOT EXISTS audit_chain_desc_idx ON audit.audit_events (chain_seq DESC NULLS LAST);

-- قائمة المهام الافتراضية مرتبة بالاستحقاق ثم الأحدث: فهرس يتيح قراءة أول صفحة دون فرز الجدول كاملًا
CREATE INDEX IF NOT EXISTS tasks_due_created_idx ON tasks.tasks (due_date ASC NULLS LAST, created_at DESC);

-- البحث التقليدي (LIKE '%كلمة%' على النص المطبع): فهارس ثلاثية الأحرف (pg_trgm) على التعبيرات نفسها التي يستخدمها الاستعلام.
-- pg_trgm امتداد موثوق في PostgreSQL 13+ يُنشئه مالك القاعدة دون صلاحية مدير الخادم.
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE INDEX IF NOT EXISTS resolutions_search_title_trgm ON resolutions.resolutions USING gin (public.ar_norm(title_ar || ' ' || coalesce(number, '')) gin_trgm_ops);
CREATE INDEX IF NOT EXISTS resolutions_search_text_trgm ON resolutions.resolutions USING gin (public.ar_norm(text_ar) gin_trgm_ops);
CREATE INDEX IF NOT EXISTS tasks_search_title_trgm ON tasks.tasks USING gin (public.ar_norm(title_ar || ' ' || coalesce(reference_no, '')) gin_trgm_ops);
CREATE INDEX IF NOT EXISTS tasks_search_text_trgm ON tasks.tasks USING gin (public.ar_norm(coalesce(description, '') || ' ' || coalesce(acceptance_criteria, '')) gin_trgm_ops);
CREATE INDEX IF NOT EXISTS meetings_search_title_trgm ON meetings.meetings USING gin (public.ar_norm(title_ar || ' ' || coalesce(reference_no, '')) gin_trgm_ops);
CREATE INDEX IF NOT EXISTS documents_search_name_trgm ON docs.documents USING gin (public.ar_norm(file_name) gin_trgm_ops);
CREATE INDEX IF NOT EXISTS meetings_search_location_trgm ON meetings.meetings USING gin (public.ar_norm(coalesce(location, '')) gin_trgm_ops);
