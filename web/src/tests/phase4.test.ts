import { describe, expect, it } from 'vitest';
import ar from '../i18n/ar';
import en from '../i18n/en';
import { DELAY_CAUSES, OFFICIAL_CHANNELS } from '../pages/tasks/TaskDetail';

const lookup = (dict: unknown, key: string) => key.split('.').reduce<unknown>((o, p) => (o && typeof o === 'object' ? (o as Record<string, unknown>)[p] : undefined), dict);
const each = (prefix: string, values: string[]) => values.map((v) => `${prefix}.${v}`);

describe('المرحلة 4', () => {
  it('كل قيم دورة المهام والتصعيد والبوابة والخطابات مترجمة بالعربية والإنجليزية', () => {
    const keys = [
      // الحالات العشر المطلوبة (منها «متأخرة» المحسوبة)
      ...each('enums.taskStatus', ['new', 'assigned', 'accepted', 'in_progress', 'on_hold', 'pending_review', 'completed', 'overdue', 'rejected', 'cancelled']),
      ...each('enums.assigneeType', ['member', 'employee', 'org_unit', 'external_entity']),
      ...each('enums.docPurpose', ['deliverable', 'assignment_letter']),
      ...each('tasks4.source', ['decision', 'recommendation', 'agenda_item', 'manual']),
      ...each('tasks4.actionHelp', ['assign', 'accept', 'reject', 'start', 'hold', 'resume', 'submit', 'complete', 'verify', 'return', 'reopen', 'cancel']),
      ...each('tasks4.updateType', ['progress', 'status_change', 'comment', 'evidence', 'reassignment', 'due_date_change', 'deliverable', 'review',
        'extension_request', 'extension_decision', 'escalation', 'external_update', 'letter', 'edit']),
      ...each('tasks4.channel', ['system', 'portal', 'recorded']),
      ...each('tasks4.delayCause', DELAY_CAUSES),
      ...each('tasks4.officialChannel', OFFICIAL_CHANNELS),
      ...each('tasks4.deliverableStatus', ['pending', 'submitted', 'accepted', 'rejected', 'removed']),
      ...each('tasks4.extensionStatus', ['pending', 'approved', 'rejected', 'withdrawn']),
      ...each('tasks4.escKind', ['reminder', 'escalation', 'manual', 'review_overdue']),
      ...each('tasks4.review', ['accepted', 'returned']),
      ...each('tasks4.completion', ['self_declared', 'verified']),
      ...each('tasks4.externalKinds', ['acknowledged', 'declined', 'progress', 'deliverable', 'extension_request', 'comment']),
      ...each('tasks4.dash.buckets', ['overdue', 'next_7_days', 'next_30_days', 'later', 'no_due_date', 'past_due_waiting']),
      ...each('tasks4.escalation.triggers', ['due_soon', 'overdue', 'overdue_l2', 'overdue_l3', 'overdue_l4']),
      ...each('tasks4.escalation.recipients', ['assignee', 'accountable', 'verifier', 'secretary', 'chair', 'vice_chair', 'assignee_org_unit_manager', 'governance_officer', 'executive']),
      ...each('tasks4.recipient', ['assignee', 'accountable', 'verifier', 'secretary', 'chair', 'vice_chair', 'assignee_org_unit_manager', 'governance_officer', 'executive', 'manual']),
      ...each('tasks4.workflow.categories', ['new', 'open', 'active', 'waiting', 'review', 'done', 'closed', 'derived']),
      ...each('tasks4.workflow.actorNames', ['executor', 'follow_up', 'verifier', 'manager']),
      ...each('tasks4.workflow.perms', ['task.update', 'task.manage', 'task.verify']),
      ...each('tasks4.letters.status', ['draft', 'approved', 'dispatched_manual', 'dispatched_integration', 'cancelled']),
      ...each('tasks4.letters.channels', ['correspondence_system_manual', 'hand_delivery', 'official_email', 'other', 'integration']),
      ...each('permissions', ['task.comment', 'task.escalate', 'task.record_external', 'task.extension.approve', 'task.letters.manage', 'task.workflow.manage']),
    ];
    for (const k of keys) for (const d of [ar, en]) expect(typeof lookup(d, k), k).toBe('string');
  });

  it('كل مفتاح نصي للمرحلة 4 مذكور حرفيًا (ولو داخل شرط) موجود باللغتين', () => {
    const sources = import.meta.glob(['../pages/**/*.tsx', '../layout/**/*.tsx'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>;
    const missing: string[] = [];
    for (const [file, text] of Object.entries(sources))
      for (const m of text.matchAll(/'((?:tasks4|portal)\.[a-zA-Z0-9_.]+)'/g))
        for (const [name, dict] of [['ar', ar], ['en', en]] as const)
          if (typeof lookup(dict, m[1]) !== 'string') missing.push(`${name}:${m[1]} (${file})`);
    expect(missing).toEqual([]);
  });

  it('نصوص الخطاب لا تدّعي الإرسال قبل تسجيله', () => {
    // «معتمد» ليس «مرسلًا»: الحالة المعتمدة تصرّح بأنه لم يُرسل
    expect(lookup(ar, 'tasks4.letters.status.approved')).toContain('لم يُرسل');
    expect(lookup(en, 'tasks4.letters.status.approved')).toContain('not sent');
  });
});
