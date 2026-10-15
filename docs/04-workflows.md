# 04 — مخططات سير العمل

المخططات تصف التسلسل والمسؤوليات؛ الحالات وقواعد الانتقال الملزمة في [08](08-minutes-decisions-tasks-lifecycle.md)، والصلاحيات في [06](06-rbac.md).

## WF-01 تشكيل جهة حوكمة

```mermaid
flowchart TD
  A(["قرار تشكيل صادر"]) --> B["مسؤول شؤون المجالس: إنشاء الجهة (draft)<br/>النوع، الرمز، الاختصاصات، التصنيف، المدة، مستند القرار"]
  B --> C["اختيار قالب سياسة أو تخصيصها<br/>(النصاب، التصويت، الإقرار، المهل، التصعيد)"]
  C --> D["إضافة العضويات بالأدوار والتواريخ<br/>الأعضاء الخارجيون: حساب ضيف Entra"]
  D --> E{"رئيس نشط واحد<br/>+ أمين سر واحد على الأقل؟"}
  E -->|لا| D
  E -->|نعم| F["activate ← active"]
  F --> G["إشعار الأعضاء BodyActivated"]
  G --> H(["الجهة جاهزة للاجتماعات"])
  F -.لاحقًا.-> I["تعديل سياسة ← إصدار جديد ساري من تاريخه"]
  F -.لاحقًا.-> J["انتهاء مدة فريق العمل ← تنبيه ← dissolve ← archive"]
```

## WF-02 إعداد الاجتماع والدعوات

```mermaid
sequenceDiagram
  autonumber
  actor S as أمين السر
  participant SYS as النظام
  actor M as الأعضاء
  participant CAL as التقويم (موصل اختياري)
  S->>SYS: إنشاء اجتماع (draft) ثم schedule
  SYS-->>S: رقم الاجتماع + السياسة السارية مثبتة
  S->>SYS: إضافة البنود والمذكرات والمرفقات
  opt طلبات إدراج (meetings.agenda_requests)
    M->>SYS: طلب إدراج بند
    S->>SYS: قبول ← بند جديد
  end
  S->>SYS: publish-agenda
  SYS->>SYS: دعوات للأعضاء النشطين في تاريخ الاجتماع + الضيوف لبنودهم
  SYS-->>M: إشعار داخلي + بريد (صادر)
  alt integrations.graph_calendar مفعّل
    SYS->>CAL: إنشاء حدث من الصندوق الخدمي
    CAL-->>SYS: معرّف الحدث (integ.external_refs)
  else معطل
    SYS-->>M: ملف .ics مرفق بالبريد
  end
  M->>SYS: قبول / اعتذار / مبدئي
  SYS->>SYS: توقع النصاب
  alt توقع عدم اكتمال النصاب (قبل meetings.quorum_forecast_hours)
    SYS-->>S: تنبيه + خيار التأجيل
  end
```

## WF-03 انعقاد الاجتماع: الحضور والنصاب والمداولة والتصويت

```mermaid
flowchart TD
  A(["موعد الاجتماع"]) --> B["أمين السر: تسجيل الحضور<br/>حاضر/عن بعد/معتذر/منيب"]
  B --> C["فحص نصاب الافتتاح (08 §5)"]
  C --> D{"مكتمل؟"}
  D -->|لا| E{"تجاوز مسموح بالسياسة؟"}
  E -->|لا| F["mark-quorum-failed<br/>اقتراح اجتماع بديل بالبنود نفسها"] --> Z1(["نهاية"])
  E -->|نعم بسبب| G
  D -->|نعم| G["open ← in_progress"]
  G --> H["البند التالي"]
  H --> I["إفصاحات تعارض المصالح للبند"]
  I --> J["ملاحظات المداولة (أمين السر)"]
  J --> K{"نوع البند"}
  K -->|للاطلاع/للنقاش| L["discussed"]
  K -->|للقرار| M["صياغة مقترح"]
  M --> N{"طريقة الاعتماد"}
  N -->|توافق| O["adopt-by-consensus"]
  N -->|تصويت| P["فحص نصاب البند (طرح المُفصِحين)"]
  P --> Q{"مكتمل؟"}
  Q -->|لا| R["تأجيل البند deferred"]
  Q -->|نعم| S["open vote<br/>إلكتروني / رفع أيدي / سري"]
  S --> T["التحقق من أحقية كل صوت"]
  T --> U["close ← حساب النتيجة (08 §6)"]
  U --> V["آراء مخالفة/تحفظات"]
  O --> L
  V --> L
  R --> L
  L --> W{"بنود متبقية؟"}
  W -->|نعم| X{"مغادرة عضو؟"} -->|نعم| P2["فحص نصاب departure"] --> H
  X -->|لا| H
  W -->|لا| Y["close ← concluded<br/>إنشاء مسودة المحضر آليًا"]
  Y --> Z2(["WF-05"])
```

## WF-04 القرار بالتمرير

```mermaid
sequenceDiagram
  autonumber
  actor S as أمين السر
  actor C as الرئيس
  participant SYS as النظام
  actor M as الأعضاء المصوتون
  S->>SYS: إنشاء تمرير + نص المقترح + المهلة
  C->>SYS: الموافقة على الإرسال (أو أمين السر بتوجيه مسجل)
  SYS->>SYS: open ← رقم التمرير
  SYS-->>M: طلب تصويت
  loop حتى المهلة أو اكتمال الأصوات
    M->>SYS: مع / ضد / ممتنع + تحفظ اختياري
  end
  SYS->>SYS: closed ← نصاب الردود + النتيجة
  SYS->>SYS: إنشاء مسودة محضر تمرير
  S->>SYS: مراجعة المسودة ← pending_approval
  C->>SYS: approve
  SYS-->>M: صدور القرار برقمه
```

## WF-05 مراجعة المحضر وإقراره واعتماده

```mermaid
sequenceDiagram
  autonumber
  actor S as أمين السر
  actor M as الأعضاء الحاضرون
  actor C as الرئيس / المفوَّض
  participant MIN as وحدة المحاضر
  participant SIG as وحدة التوقيع (اختيارية)
  S->>MIN: تحرير المسودة (أقسام + قرارات + مهام مسودة)
  opt مراجعة داخلية
    S->>MIN: submit-internal-review
  end
  S->>MIN: submit-member-review ← إصدار vN + بصمة
  MIN-->>M: إشعار المراجعة + المهلة
  M->>MIN: ملاحظات على الأقسام
  S->>MIN: معالجة الملاحظات ← draft ← إصدار vN+1
  alt السياسة تشترط الإقرار
    S->>MIN: request-signoffs
    loop لكل موقِّع مطلوب
      alt الطريقة: إقرار داخلي
        M->>MIN: إقرار على البصمة + تحفظ اختياري
      else الطريقة: توقيع إلكتروني (module.esignature)
        MIN->>SIG: طلب توقيع على PDF الإصدار
        M->>SIG: التوقيع لدى المزوّد
        SIG-->>MIN: SignatureCompleted ← إقرار method=electronic_signature
      else الطريقة: دليل ورقي
        S->>MIN: رفع النسخة الموقعة ورقيًا لكل موقِّع
      end
    end
    MIN->>MIN: اكتمال ← pending_approval
    MIN-->>C: المحضر جاهز للاعتماد + التحفظات
  else لا يُشترط
    S->>MIN: submit-for-approval
  end
  alt اعتماد
    C->>MIN: approve (vN، البصمة)
    MIN->>MIN: إصدار القرارات + تفعيل المهام + إقفال (معاملة واحدة)
    MIN-->>M: إشعار الاعتماد
    MIN->>MIN: PDF + رمز التحقق (العامل)
    opt esign.chair_signing
      MIN->>SIG: توقيع الرئيس على PDF المعتمد
    end
  else إعادة
    C->>MIN: return + ملاحظات
    MIN->>MIN: draft + إبطال الإقرارات
    MIN-->>S: إعادة بملاحظات
  end
```

## WF-06 تنفيذ المهمة والتحقق منها

```mermaid
flowchart TD
  A(["اعتماد المحضر"]) --> B["المهام المسودة ← assigned + ترقيم"]
  B --> C{"نوع المكلَّف"}
  C -->|مستخدم| D["إشعار المكلَّف"]
  C -->|إدارة| E["إشعار مدير الإدارة / المتابِع"]
  C -->|جهة خارجية| F{"integrations.qemam؟"}
  F -->|مفعّل| G["إنشاء خطاب صادر في قمم ← حفظ رقمه"]
  F -->|معطل| H["أمين السر/المتابِع يُدخل رقم الخطاب يدويًا"]
  G --> E2["إشعار المتابِع الداخلي"]
  H --> E2
  D & E & E2 --> I["start ← in_progress"]
  I --> J["تحديثات تقدم + أدلة"]
  J --> K{"يحتاج تمديدًا؟"}
  K -->|نعم| L["طلب تمديد ← صاحب القرار يوافق/يرفض"] --> J
  K -->|لا| M["submit-for-verification + ملاحظة الإنجاز + الدليل"]
  M --> N{"verification_required"}
  N -->|لا| P["completed"]
  N -->|نعم| O{"المحقق"}
  O -->|مستوفٍ| P
  O -->|غير مستوفٍ| Q["returned + السبب"] --> I
  P --> R["إن كانت آخر مهمة للقرار ← القرار executed"]
```

## WF-07 التذكير والتصعيد اليومي

```mermaid
flowchart TD
  A(["المهمة المجدولة يوميًا 07:30"]) --> B["المهام المفتوحة: assigned / in_progress / returned<br/>غير on_hold، ولها موعد"]
  B --> C["لكل مهمة: حساب أيام العمل حتى/منذ الموعد"]
  C --> D["مطابقة مستويات escalation_rule من لقطة سياسة الجهة"]
  D --> E{"مستوى مستحق لم يُرسل لهذه الفترة؟"}
  E -->|لا| B
  E -->|نعم| F["task_escalations (مفتاح منع التكرار)"]
  F --> G["outbox: إشعار المستلمين حسب المستوى"]
  G --> H{"المستوى ≥ 3 و integrations.sms؟"}
  H -->|نعم| I["رسالة نصية"]
  H -->|لا| J["—"]
  F --> K{"المستوى 4؟"}
  K -->|نعم| L["إدراج في قائمة تصعيد الإدارة العليا"]
  G --> M["تحديث escalation_level"]
```

## WF-08 تفعيل أو تعطيل وحدة اختيارية

```mermaid
sequenceDiagram
  autonumber
  actor A as مدير النظام
  participant ADM as لوحة الإدارة
  participant CFG as الإعدادات المركزية
  participant MOD as الوحدات
  A->>ADM: تعطيل module.esignature
  ADM->>CFG: GET impact
  CFG-->>ADM: 3 طلبات توقيع معلقة، محضران بانتظار الإقرار الإلكتروني
  ADM-->>A: عرض الأثر + طلب السبب
  A->>ADM: تأكيد + السبب
  ADM->>CFG: PUT enabled=false
  CFG->>CFG: feature_flag_history + تدقيق
  CFG-->>MOD: FeatureFlagChanged
  MOD->>MOD: إبطال الذاكرة المؤقتة؛ المنافذ تعود للبديل الافتراضي
  MOD-->>A: إشعار أمناء السر للمحاضر المتأثرة: «أعد الطلب بالبديل»
  Note over MOD: لا حذف لأي صف في sig.*؛ إعادة التفعيل تستأنف المزامنة
```

## WF-09 مسودة محضر بمساعدة الذكاء الاصطناعي (اختيارية)

```mermaid
sequenceDiagram
  autonumber
  actor S as أمين السر
  participant MIN as وحدة المحاضر
  participant AI as وحدة الذكاء الاصطناعي
  participant LLM as خادم النموذج (مضيف مسموح)
  S->>AI: طلب مسودة (ai.minutes_draft)
  AI->>AI: فحص المفتاح للجهة + التصنيف ≤ ai.max_classification + الحصة
  alt مرفوض
    AI-->>S: سبب الرفض (معطل/تصنيف/حصة)
  else مسموح
    AI->>MIN: قراءة بيانات الاجتماع عبر العقد (الحضور، البنود، الملاحظات، الأصوات)
    AI->>LLM: قالب معتمد + البيانات
    LLM-->>AI: مسودة JSON وفق مخطط الإخراج
    AI->>AI: تحقق المخطط + ai_jobs + ai_suggestions (pending_review)
    AI-->>S: عرض المسودة الآلية بجانب المسودة اليدوية مع الفروقات
    S->>AI: قبول أقسام محددة مع تعديلات
    AI->>MIN: أمر تحديث المسودة (source=ai_assisted) بصلاحية أمين السر
    AI->>AI: الاقتراح accepted/partially_accepted + تدقيق
  end
  Note over S,MIN: المحضر يكمل WF-05 كأي محضر؛ لا شيء يصبح رسميًا دون الاعتماد
```

## WF-10 إسناد مهمة لجهة خارجية دون موصل

```mermaid
flowchart LR
  A["قرار يتطلب إجراء من أمانة المنطقة"] --> B["مهمة مسودة: assignee_type=external_entity<br/>المتابِع الداخلي إلزامي"]
  B --> C["اعتماد المحضر ← assigned"]
  C --> D["المتابِع يصدر خطابًا عبر نظام المراسلات خارج CCMS"]
  D --> E["إدخال رقم الخطاب وتاريخه في المهمة"]
  E --> F["رد الجهة يصل بخطاب ← المتابِع يرفعه دليلًا ويحدّث التقدم"]
  F --> G["submit-for-verification ← أمين السر يتحقق"]
```
