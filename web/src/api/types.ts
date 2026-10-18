export interface Me {
  user: {
    id: string;
    email: string;
    displayNameAr: string;
    displayNameEn?: string | null;
    language: 'ar' | 'en';
    calendar: 'gregorian' | 'hijri';
    externalEntityId?: string | null;
    externalEntityName?: string | null;
    userType?: string;
    mustChangePassword?: boolean;
  };
  systemRoles: string[];
  bodyRoles: { bodyId: string; role: string }[];
  permissions: string[];
  /** ممثل جهة خارجية بلا عضوية داخلية: تُعرض له واجهة البوابة فقط */
  portalUser?: boolean;
  portalEnabled?: boolean;
}

export type FeatureMap = Record<string, { enabled: boolean; value: string }>;

export interface Body {
  id: string;
  referenceNo: string;
  code: string;
  bodyType: string;
  nameAr: string;
  nameEn?: string | null;
  parentBodyId?: string | null;
  parentName?: string | null;
  objectiveAr?: string | null;
  mandateAr?: string | null;
  authorityRef?: string | null;
  formationDecisionNo?: string | null;
  formationDecisionDate?: string | null;
  termStart?: string | null;
  termEnd?: string | null;
  meetingFrequency: string;
  classification: string;
  ownerOrgUnitName?: string | null;
  status: string;
  statusReason?: string | null;
  chairName?: string | null;
  viceChairName?: string | null;
  secretaryName?: string | null;
  memberCount: number;
  lastMeetingAt?: string | null;
  nextMeetingAt?: string | null;
  createdAt: string;
}

export interface BodyTerm {
  changeType: 'initial' | 'renewal' | 'extension';
  termStart?: string | null;
  termEnd?: string | null;
  decisionNo?: string | null;
  decisionDate?: string | null;
  reason?: string | null;
  createdAt: string;
  createdBy?: string | null;
}

export interface BodyDetail {
  body: Body;
  partners: { id: string; nameAr: string; nameEn?: string | null }[];
  children: { id: string; referenceNo: string; nameAr: string; bodyType: string; status: string }[];
  terms: BodyTerm[];
  permissions: string[];
}

export interface Member {
  id: string;
  bodyId: string;
  userId: string;
  displayNameAr: string;
  displayNameEn?: string | null;
  email: string;
  workPhone?: string | null;
  jobTitle?: string | null;
  orgUnitName?: string | null;
  entityName?: string | null;
  verifiedIdentity: boolean;
  role: string;
  isVoting: boolean;
  alternateForMemberId?: string | null;
  alternateForName?: string | null;
  startDate?: string | null;
  endDate?: string | null;
  status: string;
  endReason?: string | null;
  appointmentRef?: string | null;
  replacedByMemberId?: string | null;
  createdAt: string;
}

export interface MembershipRow {
  id: string;
  bodyId: string;
  bodyName: string;
  bodyReference: string;
  userId: string;
  displayNameAr: string;
  email: string;
  jobTitle?: string | null;
  role: string;
  isVoting: boolean;
  startDate?: string | null;
  endDate?: string | null;
  status: string;
  endReason?: string | null;
}

export interface QuorumRule {
  type: 'majority' | 'fraction' | 'fixed';
  numerator: number;
  denominator: number;
  fixedCount?: number | null;
  chairRequired: boolean;
  countRemote: boolean;
  countProxies: boolean;
  allowOverride: boolean;
}

export interface BodyPolicy {
  id: string;
  version: number;
  effectiveFrom: string;
  quorum: QuorumRule;
  voting: { threshold: string; abstentions: string; tieBreaker: string; secretAllowed: boolean };
  signoffRequired: boolean;
  minutesReviewDays: number;
  extensionApprover: string;
  allowProxy: boolean;
  maxProxiesPerMember: number;
  allowRemoteAttendance: boolean;
  allowCirculation: boolean;
  createdAt: string;
  createdByName?: string | null;
  minutesFlow?: MinutesWorkflow;
  escalation?: EscalationRule | null;
  escalationFlow?: EscalationRule;
}

export interface EscalationLevel { level: number; trigger: string; offsetDays: number; notify: string[] }
export interface EscalationRule { workingDays: boolean; repeatOverdueEveryDays: number; reviewOverdueDays: number; levels: EscalationLevel[] }

export interface Delegation {
  id: string;
  scope: 'chair_authority' | 'attendance_proxy';
  delegatorUserId: string;
  delegatorName: string;
  delegateUserId: string;
  delegateName: string;
  validFrom: string;
  validTo: string;
  reason: string;
  status: string;
  effective: boolean;
  createdAt: string;
}

export interface ActivityEntry {
  seq: number;
  occurredAt: string;
  actorUserId?: string | null;
  actorName?: string | null;
  module: string;
  action: string;
  entityType: string;
  entityId?: string | null;
  outcome: string;
  details?: string | null;
}

export interface Doc {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  sha256: string;
  classification: string;
  scanStatus: string;
  uploadedAt: string;
  uploadedBy: string;
  purpose: string;
  versionNo?: number;
  locked?: boolean;
  lifecycleStatus?: string;
  recordsClass?: string | null;
  legalHold?: boolean;
}

export interface Meeting {
  id: string;
  bodyId: string;
  bodyName: string;
  bodyCode: string;
  referenceNo?: string | null;
  titleAr: string;
  titleEn?: string | null;
  meetingType: string;
  mode: string;
  startsAt: string;
  plannedEndAt?: string | null;
  location?: string | null;
  onlineLink?: string | null;
  status: string;
  statusReason?: string | null;
  circulationDeadline?: string | null;
  invitationsSentAt?: string | null;
  actualStartedAt?: string | null;
  actualEndedAt?: string | null;
  chairName?: string | null;
  secretaryName?: string | null;
  agendaCount: number;
  invitedCount: number;
  acceptedCount: number;
  minutesId?: string | null;
  previousMeetingId?: string | null;
  previousReference?: string | null;
  previousTitle?: string | null;
  rescheduledFromMeetingId?: string | null;
  rescheduledFromReference?: string | null;
  rescheduledToId?: string | null;
  nextMeetingId?: string | null;
  classification: string;
}

export interface CalendarMeeting {
  id: string;
  bodyId: string;
  bodyName: string;
  bodyCode: string;
  referenceNo?: string | null;
  titleAr: string;
  meetingType: string;
  mode: string;
  startsAt: string;
  plannedEndAt?: string | null;
  circulationDeadline?: string | null;
  location?: string | null;
  status: string;
  myResponse?: string | null;
}

export interface AgendaItem {
  id: string;
  meetingId: string;
  position: number;
  titleAr: string;
  titleEn?: string | null;
  descriptionAr?: string | null;
  itemType: string;
  presenterText?: string | null;
  ownerOrgUnitId?: string | null;
  ownerOrgUnitName?: string | null;
  ownerText?: string | null;
  estimatedMinutes?: number | null;
  status: string;
  statusReason?: string | null;
  memoText?: string | null;
  discussionSummary?: string | null;
  recommendationText?: string | null;
  carriedFromItemId?: string | null;
  carriedFromMeetingId?: string | null;
  carriedFromReference?: string | null;
  attachmentCount: number;
  resolutionCount: number;
  taskCount: number;
  forwardedToItemId?: string | null;
  forwardedToMeetingId?: string | null;
  forwardedToReference?: string | null;
  pendingBodyName?: string | null;
  pendingStatus?: string | null;
}

export interface MeetingDetail {
  meeting: Meeting;
  agenda: AgendaItem[];
  permissions: string[];
}

export interface Invitee {
  userId: string;
  name: string;
  nameEn?: string | null;
  jobTitle?: string | null;
  orgUnit?: string | null;
  memberId?: string | null;
  role: string;
  isVoting: boolean;
  alternateForName?: string | null;
  invitationId?: string | null;
  inviteeKind?: string | null;
  sentAt?: string | null;
  response?: string | null;
  responseNote?: string | null;
  respondedAt?: string | null;
  lastReminderAt?: string | null;
  agendaItemId?: string | null;
  agendaItemTitle?: string | null;
  attendanceStatus?: string | null;
  attendanceMode?: string | null;
  proxyHolderUserId?: string | null;
  proxyHolderName?: string | null;
  arrivedAt?: string | null;
  leftAt?: string | null;
  attendanceNote?: string | null;
}

export interface QuorumLine {
  userId: string;
  name: string;
  role: string;
  principal: boolean;
  counted: boolean;
  reason: string;
}

export interface QuorumResult {
  eligible: number;
  counted: number;
  required: number;
  chairRequired: boolean;
  chairPresent: boolean;
  isMet: boolean;
  basis: string;
  lines: QuorumLine[];
}

export interface QuorumView {
  mode: 'actual' | 'forecast' | 'circulation';
  result: QuorumResult;
  policy: { version: number; quorum: QuorumRule; allowProxy: boolean; maxProxiesPerMember: number; allowRemoteAttendance: boolean; allowCirculation: boolean };
  history: { id: string; context: string; eligible: number; counted: number; required: number; chairPresent: boolean; isMet: boolean; overrideReason?: string | null; checkedAt: string; checkedBy?: string | null }[];
}

export interface PendingItem {
  id: string;
  kind: 'deferred' | 'referred';
  titleAr: string;
  descriptionAr?: string | null;
  reason: string;
  status: 'pending' | 'scheduled' | 'dropped';
  sourceItemId: string;
  sourceBodyId: string;
  sourceBodyName: string;
  sourceMeetingId: string;
  sourceMeetingReference?: string | null;
  sourceMeetingTitle: string;
  scheduledItemId?: string | null;
  scheduledMeetingId?: string | null;
  scheduledMeetingReference?: string | null;
  createdAt: string;
  createdByName?: string | null;
}

export interface MinutesSection {
  key: string;
  title: string;
  body?: string | null;
}

export interface Minutes {
  id: string;
  meetingId: string;
  status: string;
  versionNo: number;
  content: { sections: MinutesSection[] };
  meetingReference?: string | null;
  meetingTitle: string;
  meetingStartsAt: string;
  bodyId: string;
  bodyName: string;
  contentHash?: string | null;
  updatedAt: string;
}

export interface SignOff {
  id: string;
  userId: string;
  name: string;
  method: string;
  status: string;
  reservation?: string | null;
  requestedAt: string;
  signedAt?: string | null;
  versionNo: number;
}

export interface Resolution {
  id: string;
  bodyId: string;
  bodyName: string;
  meetingReference?: string | null;
  resolutionType: string;
  number?: string | null;
  titleAr: string;
  textAr: string;
  status: string;
  createdAt: string;
  taskCount: number;
}

export interface Task {
  id: string;
  referenceNo?: string | null;
  bodyId: string;
  bodyName: string;
  resolutionNumber?: string | null;
  titleAr: string;
  description?: string | null;
  assigneeType: string;
  assigneeName?: string | null;
  accountableName?: string | null;
  priority: string;
  dueDate?: string | null;
  status: string;
  progressPct: number;
  escalationLevel: number;
  isOverdue: boolean;
  createdAt: string;
}

export interface TaskUpdate {
  id: string;
  updateType: string;
  progressPct?: number | null;
  note?: string | null;
  createdAt: string;
  author: string;
}

export interface UserRow {
  id: string;
  email: string;
  displayNameAr: string;
  displayNameEn?: string | null;
  jobTitle?: string | null;
  userType: string;
  orgUnitName?: string | null;
  externalEntityId?: string | null;
  externalEntityName?: string | null;
  clearance: string;
  status: string;
  lastLoginAt?: string | null;
  hasLocalPassword: boolean;
  systemRoles: string[];
}

export interface DirectoryUser {
  id: string;
  email: string;
  displayNameAr: string;
  displayNameEn?: string | null;
}

export interface Notification {
  id: string;
  eventType: string;
  titleAr: string;
  titleEn: string;
  link?: string | null;
  severity: string;
  isRead: boolean;
  createdAt: string;
}

export interface Connector {
  code: string;
  nameAr: string;
  nameEn: string;
  status: string;
  detail?: string | null;
  latencyMs?: number | null;
  checkedAt?: string | null;
}

export interface FeatureFlag {
  key: string;
  category: 'ai' | 'esignature' | 'integration' | 'platform';
  valueType: 'boolean' | 'enum' | 'integer' | 'text';
  value: string;
  storedValue: string;
  defaultValue: string;
  allowedValues?: string[] | null;
  minValue?: number | null;
  maxValue?: number | null;
  maxLength?: number | null;
  parentKey?: string | null;
  effectiveEnabled: boolean;
  blockedBy?: string | null;
  locked: boolean;
  connector?: Connector | null;
  nameAr: string;
  nameEn: string;
  descriptionAr: string;
  descriptionEn: string;
  updatedAt: string;
  updatedBy?: string | null;
}

export interface ImpactItem {
  code: string;
  descriptionAr: string;
  descriptionEn: string;
  count: number;
}

export interface FlagHistory {
  id: string;
  key: string;
  oldValue?: string | null;
  newValue?: string | null;
  changedAt: string;
  actor: string;
  reason?: string | null;
  result: string;
  resultDetail?: string | null;
  impact?: string | null;
}

export interface Dashboard {
  counters: {
    activeBodies: number;
    upcomingMeetings: number;
    minutesInProgress: number;
    openResolutions: number;
    openTasks: number;
    overdueTasks: number;
    completedTasks: number;
    myOpenTasks: number;
    myPendingSignoffs: number;
  };
  upcomingMeetings: { id: string; referenceNo?: string | null; titleAr: string; startsAt: string; bodyName: string }[];
}

export interface OrgUnit {
  id: string;
  code: string;
  nameAr: string;
}

export interface ExternalEntity {
  id: string;
  nameAr: string;
  liaisonName: string;
}

// ---------------- المرحلة 3: المحاضر والقرارات والتصويت ----------------

export interface MinutesWorkflow {
  secretaryReview: boolean;
  chairReview: boolean;
  memberReview: boolean;
  memberReviewDays: number;
  signatures: 'none' | 'present_members' | 'present_members_and_chair' | 'all_members';
  signatureFallback: 'attestation' | 'wet_signature';
  finalApproval: 'chair' | 'recorded_external';
  amendmentReapproval: 'full' | 'final_only';
  templateCode: string;
}

export interface MinutesRecord {
  id: string;
  meetingId: string;
  status: string;
  versionNo: number;
  content: { sections: MinutesSection[]; items?: { agendaItemId: string; discussion?: string | null; recommendation?: string | null }[] };
  updatedAt: string;
  approvedAt?: string | null;
  approvedByName?: string | null;
  approvedVersionNo?: number | null;
  workflow: MinutesWorkflow;
  templateId: string;
  amendmentNo: number;
  amendmentReason?: string | null;
  reviewDeadline?: string | null;
  statusReason?: string | null;
  statusChangedAt: string;
  verificationCode?: string | null;
  pdfDocumentId?: string | null;
  pdfSha256?: string | null;
  classification: string;
  meetingReference?: string | null;
  meetingTitle: string;
  meetingStartsAt: string;
  meetingStatus: string;
  meetingType: string;
  bodyId: string;
  bodyName: string;
  bodyCode: string;
  contentHash?: string | null;
  resolutionCount: number;
  openComments: number;
}

export interface TemplateSection {
  key: string;
  titleAr: string;
  titleEn?: string | null;
  kind: 'auto' | 'text';
  source?: string | null;
  defaultBody?: string | null;
}

export interface MinutesTemplate {
  id: string;
  code: string;
  nameAr: string;
  nameEn?: string | null;
  sections: TemplateSection[];
  isDefault: boolean;
  status: string;
  version: number;
  updatedAt: string;
  usage: number;
}

export interface SnapVote { method: string; result: string; for: number; against: number; abstain: number; eligible: number; chairCasting: boolean; round: number }

export interface MinutesSnapshot {
  meeting: {
    id: string; referenceNo?: string | null; titleAr: string; bodyName: string; bodyCode: string; bodyReference: string; meetingType: string; mode: string;
    startsAt: string; actualStartedAt?: string | null; actualEndedAt?: string | null; location?: string | null; onlineLink?: string | null;
    chairName?: string | null; secretaryName?: string | null; previousReference?: string | null; classification: string;
  };
  attendance: { userId: string; name: string; role: string; status?: string | null; mode?: string | null; proxyHolder?: string | null; note?: string | null; leftAt?: string | null }[];
  guests: { name: string; response: string; itemTitle?: string | null }[];
  quorum?: { context: string; eligible: number; counted: number; required: number; isMet: boolean; chairPresent: boolean; overrideReason?: string | null; checkedAt: string } | null;
  agenda: { id: string; position: number; titleAr: string; presenter?: string | null; owner?: string | null; itemType: string; status: string; statusReason?: string | null; discussion?: string | null; recommendation?: string | null }[];
  resolutions: {
    id: string; position: number; type: string; category: string; titleAr: string; textAr: string; textEn?: string | null; agendaItemId?: string | null; agendaPosition?: number | null;
    status: string; number?: string | null; authorityText?: string | null; targetBody?: string | null; responsible?: string | null; supersedes?: string | null; supersedeKind?: string | null;
    adoptionMethod: string; vote?: SnapVote | null;
  }[];
  reservations: { name: string; resolutionTitle: string; kind: string; text: string }[];
  tasks: { id: string; titleAr: string; referenceNo?: string | null; assignee?: string | null; dueDate?: string | null; resolutionTitle?: string | null; status: string }[];
  attachments: { fileName: string; ownerType: string; purpose: string; sha256: string }[];
}

export interface MinutesSignoff {
  id: string; userId: string; name: string; signerRole: string; method: string; status: string; reservation?: string | null; requestedAt: string;
  signedAt?: string | null; methodReason?: string | null; evidenceDocumentId?: string | null; recordedBy?: string | null; invalidatedReason?: string | null; versionNo: number;
}

export interface MinutesApproval {
  id: string; step: string; decision: string; actor: string; capacity?: string | null; onBehalfOf?: string | null; method?: string | null; note?: string | null;
  externalApprover?: string | null; externalReference?: string | null; externalDate?: string | null; evidenceDocumentId?: string | null; contentHash?: string | null;
  versionNo?: number | null; createdAt: string;
}

export interface MinutesActions {
  canEdit: boolean; canManageResolutions: boolean; canSubmit: boolean; submitBlockedReason?: string | null; canApproveStep: boolean; canReturn: boolean;
  canSign: boolean; mySignoffMethod?: string | null; canRecordWet: boolean; canApprove: boolean; canRecordExternal: boolean; canAmend: boolean;
  canComment: boolean; canResolveComments: boolean;
}

export interface MinutesDetail {
  minutes: MinutesRecord;
  template: TemplateSection[];
  snapshot: MinutesSnapshot;
  signoffs: MinutesSignoff[];
  approvals: MinutesApproval[];
  actions: MinutesActions;
  steps: string[];
  currentVersion?: { no: number; hash: string; createdAt: string; createdBy?: string | null; changeSummary?: string | null } | null;
  signatureMethod: { method: string; reason: string };
  permissions: string[];
}

export interface MinutesVersion {
  id: string; versionNo: number; contentHash: string; createdAt: string; createdBy?: string | null; changeSummary?: string | null; amendmentNo: number;
  createdInStatus?: string | null; source: string; resolutionsIssued: number; approved: boolean; current: boolean;
}

export interface MinutesComment {
  id: string; anchor: string; text: string; status: string; resolutionNote?: string | null; createdAt: string; resolvedAt?: string | null;
  author: string; resolver?: string | null; versionNo?: number | null; authorUserId: string;
}

export interface ResolutionFull {
  id: string; bodyId: string; bodyName: string; bodyCode: string; minutesId?: string | null; minutesStatus?: string | null; minutesVersionNo?: number | null;
  meetingId?: string | null; meetingReference?: string | null; meetingTitle?: string | null; meetingStartsAt?: string | null;
  agendaItemId?: string | null; agendaItemTitle?: string | null; agendaItemPosition?: number | null;
  resolutionType: string; category: string; position: number; number?: string | null; titleAr: string; textAr: string; textEn?: string | null;
  status: string; statusReason?: string | null; issuedAt?: string | null; approvedAt?: string | null; requiresFollowUp: boolean; classification: string; source: string;
  authorityText?: string | null; targetBodyId?: string | null; targetBodyName?: string | null; targetAuthorityText?: string | null;
  referralOutcome?: string | null; referralNote?: string | null; referralDecidedAt?: string | null;
  responsibleOrgUnitId?: string | null; responsibleOrgUnitName?: string | null; responsibleExternalEntityId?: string | null; responsibleEntityName?: string | null;
  responsibleText?: string | null; supersedesResolutionId?: string | null; supersedeKind?: string | null; supersedesNumber?: string | null; supersedesTitle?: string | null;
  adoptionMethod: string; voteResult?: string | null;
  voteSummary?: { method: string; for: number; against: number; abstain: number; cast: number; eligible: number; present: number; members: number; chairCasting: boolean; result: string; threshold: string } | null;
  openMotions: number; createdAt: string; updatedAt: string; taskCount: number; tasksCompleted: number;
}

export interface ResolutionDetail {
  resolution: ResolutionFull;
  tasks: { id: string; referenceNo?: string | null; titleAr: string; status: string; dueDate?: string | null; progressPct: number; assigneeType: string; assigneeName?: string | null }[];
  history: { fromStatus?: string | null; toStatus: string; reason?: string | null; reference?: string | null; changedAt: string; changedBy?: string | null }[];
  supersededBy: { id: string; number?: string | null; titleAr: string; kind: string; status: string }[];
  reservations: { name: string; kind: string; text: string; createdAt: string }[];
  documentCount: number;
  permissions: string[];
}

export interface Motion {
  id: string; meetingId: string; resolutionId: string; resolutionTitle: string; resolutionNumber?: string | null; agendaItemId?: string | null; round: number;
  method: string; status: string; openedAt: string; deadlineAt?: string | null; closedAt?: string | null; result?: string | null; closeReason?: string | null;
  tally?: { method: string; for: number; against: number; abstain: number; cast: number; eligible: number; present: number; members: number; chairCasting: boolean; quorumMet: boolean; result: string; threshold: string } | null;
  eligibleCount: number; votedCount: number;
  eligible: { principalUserId: string; name: string; role: string; capacity: string; castByUserId: string; voted: boolean }[];
  myBallots: { principalUserId: string; name: string; capacity: string; voted: boolean; choice?: string | null }[];
  votes?: { principalUserId?: string | null; name?: string | null; choice: string; capacity?: string | null; castBy?: string | null; castAt: string }[] | null;
  reservations: { name: string; kind: string; text: string; createdAt: string }[];
}

export interface MotionList { canManage: boolean; canCast: boolean; seeIndividual: boolean; items: Motion[] }

// ---------------- المرحلة 4: المهام والتكليفات ----------------

export interface TaskItem {
  id: string; referenceNo?: string | null; bodyId: string; bodyName: string; bodyCode: string;
  resolutionId?: string | null; resolutionNumber?: string | null; resolutionTitle?: string | null; resolutionType?: string | null; resolutionStatus?: string | null;
  meetingId?: string | null; meetingReference?: string | null; agendaItemId?: string | null; agendaItemTitle?: string | null; sourceType: string;
  titleAr: string; description?: string | null; assigneeType: string; assigneeUserId?: string | null; assigneeOrgUnitId?: string | null; assigneeExternalEntityId?: string | null;
  assigneeName?: string | null; executorUserId?: string | null; executorName?: string | null; executorContact?: string | null; executingOrgUnitId?: string | null;
  executingUnitName?: string | null; accountableUserId?: string | null; accountableName?: string | null; verifierUserId?: string | null; verifierName?: string | null;
  verificationRequired: boolean; priority: string; dueDate?: string | null; originalDueDate?: string | null; status: string; statusCategory: string;
  statusNameAr: string; statusNameEn: string; statusColor: string; statusReason?: string | null; progressPct: number; escalationLevel: number;
  isOverdue: boolean; daysOverdue?: number | null; pendingExtension: boolean; deliverablesTotal: number; deliverablesAccepted: number;
  acceptanceCriteria?: string | null; externalBrief?: string | null; shareResolutionWithEntity: boolean; delayReason?: string | null;
  correspondenceNo?: string | null; correspondenceDate?: string | null; assignedAt?: string | null; acceptedAt?: string | null; startedAt?: string | null;
  submittedAt?: string | null; completedAt?: string | null; verifiedAt?: string | null; verifiedByName?: string | null; completionMethod?: string | null;
  cancelledAt?: string | null; createdAt: string; updatedAt: string;
}

export interface TaskDeliverable {
  id: string; position: number; title: string; description?: string | null; acceptanceCriteria?: string | null; required: boolean; status: string;
  reviewNote?: string | null; reviewedAt?: string | null; reviewedBy?: string | null;
  submissions: { id: string; note?: string | null; channel: string; createdAt: string; submittedBy: string; documents: { id: string; fileName: string; sha256: string }[] }[];
}

export interface TaskUpdateEntry {
  id: string; updateType: string; progressPct?: number | null; note?: string | null; createdAt: string; author?: string | null; authorIsExternal: boolean;
  channel: string; visibility: string; officialChannel?: string | null; reference?: string | null; receivedOn?: string | null;
  newValue?: Record<string, unknown> | null; oldValue?: Record<string, unknown> | null; delayReason?: string | null; recordedForEntity?: string | null; documentIds: string[];
}

export interface TaskExtension {
  id: string; status: string; currentDueDate: string; requestedDueDate: string; approvedDueDate?: string | null; delayCause: string; reason: string;
  channel: string; reference?: string | null; createdAt: string; requestedBy: string; decidedAt?: string | null; decidedBy?: string | null; decisionNote?: string | null;
}

export interface TaskEscalation {
  id: string; kind: string; level: number; trigger: string; reason?: string | null; createdAt: string; daysOverdue?: number | null; createdBy?: string | null;
  recipients: { userId: string; name: string; role: string; unit?: string | null }[];
  acknowledgedAt?: string | null; acknowledgedBy?: string | null; ackNote?: string | null; canAcknowledge: boolean;
}

export interface TaskAction { action: string; to?: string; nameAr: string; nameEn: string; requiresReason: boolean }

export interface TaskCard {
  task: TaskItem; deliverables: TaskDeliverable[]; updates: TaskUpdateEntry[]; extensions: TaskExtension[]; escalations: TaskEscalation[];
  reviews: { id: string; decision: string; note?: string | null; reviewer: string; createdAt: string; deliverableDecisions: { deliverableId: string; decision: string; note?: string | null }[] }[];
  letters: { id: string; referenceNo: string; subject: string; status: string; outgoingNo?: string | null; outgoingDate?: string | null; createdAt: string }[];
  actions: TaskAction[];
  capabilities: {
    canManage: boolean; canProgress: boolean; canSubmitDeliverables: boolean; canComment: boolean; canRequestExtension: boolean; canDecideExtension: boolean;
    canRecordExternal: boolean; canEscalate: boolean; canLetters: boolean; canShareDocuments: boolean; isExecutor: boolean; isFollowUp: boolean; isVerifier: boolean;
    portalEnabled: boolean;
  };
  permissions: string[];
}

export interface WorkflowStatus { code: string; nameAr: string; nameEn: string; category: string; color: string; sortOrder: number; isSystem: boolean; active: boolean; tasks: number }
export interface WorkflowTransition {
  id: string; action: string; from: string; to: string; nameAr: string; nameEn: string; permission: string; actors: string[]; requiresReason: boolean;
  isSystem: boolean; active: boolean; sortOrder: number;
}
export interface TaskWorkflow { statuses: WorkflowStatus[]; transitions: WorkflowTransition[]; actors: string[]; permissions: string[]; canManage: boolean }

export interface TaskGroupRow { key?: string | null; name?: string | null; kind?: string | null; total: number; open: number; completed: number; overdue: number; avgProgress: number; completionRate: number }
export interface TaskDashboard {
  kpis: {
    total: number; open: number; completed: number; cancelled: number; overdue: number; dueSoon: number; pendingReview: number; onHold: number; rejected: number;
    completedOnTime: number; avgProgress: number; escalated: number; pendingExtensions: number; completionRate: number; onTimeRate: number; overdueRate: number;
  };
  byStatus: { status: string; nameAr: string; nameEn: string; color: string; category: string; count: number }[];
  byPriority: TaskGroupRow[]; byBody: TaskGroupRow[]; byExecutor: TaskGroupRow[];
  byDue: { bucket: string; count: number }[];
  topOverdue: { id: string; referenceNo?: string | null; titleAr: string; executorName?: string | null; dueDate?: string | null; daysOverdue: number; escalationLevel: number; bodyName: string; priority: string }[];
  executive: { id: string; referenceNo?: string | null; titleAr: string; executorName?: string | null; dueDate?: string | null; escalationLevel: number; bodyName: string; lastEscalated?: string | null }[];
  generatedAt: string;
}

export interface BoardColumn { status: string; nameAr: string; nameEn: string; category: string; color: string; items: TaskItem[] }

export interface PortalTask {
  id: string; referenceNo?: string | null; titleAr: string; description?: string | null; externalBrief?: string | null; acceptanceCriteria?: string | null;
  priority: string; dueDate?: string | null; originalDueDate?: string | null; status: string; statusNameAr: string; statusNameEn: string; statusCategory: string;
  statusColor: string; progressPct: number; assignedAt?: string | null; acceptedAt?: string | null; submittedAt?: string | null; completedAt?: string | null;
  assigningBody: string; followUpName?: string | null; followUpEmail?: string | null; resolutionNumber?: string | null; resolutionTitle?: string | null;
  resolutionText?: string | null; isOverdue: boolean; pendingExtension: boolean; statusReason?: string | null; correspondenceNo?: string | null; correspondenceDate?: string | null;
}

export interface PortalCard {
  task: PortalTask; actions: TaskAction[]; deliverables: TaskDeliverable[]; updates: TaskUpdateEntry[]; extensions: TaskExtension[];
  documents: { id: string; fileName: string; sizeBytes: number; uploadedAt: string; purpose: string; uploadedBy: string; fromEntity: boolean }[];
  letters: { id: string; referenceNo: string; subject: string; outgoingNo?: string | null; outgoingDate?: string | null }[];
  capabilities: { canProgress: boolean; canSubmitDeliverables: boolean; canRequestExtension: boolean; canComment: boolean };
}

export interface PortalMe { entityId: string; entityNameAr: string; entityNameEn?: string | null; open: number; awaitingAcknowledgement: number; overdue: number; completed: number }

export interface AssignmentLetter {
  id: string; referenceNo: string; bodyId: string; resolutionId?: string | null; recipientType: string; recipientName: string; recipientAttention?: string | null;
  subject: string; bodyText: string; dueDate?: string | null; taskIds: string[]; attachmentIds: string[]; shareWithEntity: boolean; status: string;
  outgoingNo?: string | null; outgoingDate?: string | null; pdfDocumentId?: string | null; createdAt: string; createdByName?: string | null; approvedAt?: string | null;
  approvedByName?: string | null; dispatchChannel?: string | null; dispatchedAt?: string | null; cancelReason?: string | null; integrationRef?: string | null; officiallySent: boolean;
}

// ============ وحدة التوقيع الإلكتروني ============
export interface ESignModuleState {
  enabled: boolean; ready: boolean; reason: string; notice: string;
  settings: { method: string; methodLabel: string; provider: string; providerName?: string | null; productionGrade: boolean; reauthRequired: boolean;
    sequential: boolean; pdfOutput: boolean; memberRequired: boolean; chairRequired: boolean; secretaryRequired: boolean };
}
export interface ESignSigner {
  id: string; userId: string; name: string; role: string; step: number; position: number; status: string; signedAt?: string | null; actedBy?: string | null;
  onBehalf: boolean; declineReason?: string | null; allowDelegation: boolean; signedDocumentSha256?: string | null;
  certificate?: { subject: string; issuer: string; serial: string; thumbprint: string; notAfter: string; chain: string } | null;
  reauth?: { method: string; at: string } | null;
}
export interface ESignEvent { kind: string; source: string; actor?: string | null; at: string; data: Record<string, unknown> }
export interface ESignCallback { eventId: string; signatureValid: boolean; outcome: string; detail?: string | null; at: string; event?: string | null }
export interface ESignRequest {
  id: string; reference: string; minutesId: string; versionNo: number; contentHash: string; type: string; typeLabel: string; typeNotice: string;
  provider: string; providerName: string; productionGrade: boolean; status: string; statusReason?: string | null; sequential: boolean;
  route: { nameAr: string; source: string; steps: { order: number; role: string; allowDelegation: boolean }[]; expiryDays: number };
  documentId: string; documentSha256: string; documentSize: number; currentDocumentId?: string | null; currentDocumentSha256?: string | null;
  signedDocumentId?: string | null; signedDocumentSha256?: string | null; expiresAt: string; createdAt: string; completedAt?: string | null;
  dispatchAttempts: number; lastError?: string | null; nextAttemptAt?: string | null; providerRef?: string | null;
  verification?: ESignReport | null;
  signers: ESignSigner[];
  my: { signerId?: string | null; myStatus?: string | null; canSign: boolean; canOpenProviderSession: boolean; canDecline: boolean; notYourTurn: boolean;
    reauthRequired: boolean; onBehalf: { signerId: string; userId: string; name: string }[] };
  manage: { canFallback: boolean; canHold: boolean; canResume: boolean; moduleDisabled: boolean; actionRequired: boolean };
  events: ESignEvent[]; callbacks: ESignCallback[];
}
export interface ESignMinutes {
  module: ESignModuleState; minutesStatus: string; currentVersionNo: number; canStart: boolean; startBlockedReason?: string | null; requests: ESignRequest[];
}
export interface PdfSignatureCheck {
  index: number; signerName?: string | null; certificateSubject?: string | null; certificateIssuer?: string | null; certificateSerial?: string | null;
  certificateNotBefore?: string | null; certificateNotAfter?: string | null; signingTime?: string | null; byteRangeValid: boolean; digestMatches: boolean;
  signatureValid: boolean; certificateValidAtSigning: boolean; chain: string; chainDetail?: string | null; coversWholeDocument: boolean; subsequentChanges: string;
  revocation: string; timestamp: string; revisionSha256: string; error?: string | null; cryptographicallyValid: boolean;
}
export interface ESignReport {
  checkedAt: string; overall: string; notes: string[]; type?: string; typeLabel?: string; request?: string; legalNotice?: string;
  document?: { expectedSha256: string; actualSha256?: string | null; intact: boolean; versionNo: number; contentHash: string };
  signedDocument?: { sha256: string; expectedSha256?: string | null; matchesRecord: boolean; originalPreserved: boolean; modifiedAfterLastSignature: boolean };
  pdfSignatures?: PdfSignatureCheck[]; signatures?: PdfSignatureCheck[];
  approvals?: { signer: string; role: string; status: string; at?: string | null; actedBy?: string | null; onBehalf: boolean; documentSha256?: string | null }[];
  sha256?: string; length?: number; matchesRecord?: { reference: string; type: string; versionNo: number } | null; modifiedAfterLastSignature?: boolean;
}
export interface ESignRoute {
  id: string; bodyId?: string | null; bodyName?: string | null; meetingType?: string | null; nameAr: string; nameEn?: string | null;
  steps: { order: number; role: string; allowDelegation: boolean }[]; sequential?: boolean | null; signatureType?: string | null; expiryDays: number;
  active: boolean; version: number; updatedBy?: string | null; updatedAt: string;
}

// ---------------- وحدة الذكاء الاصطناعي (ADR-021) ----------------
export interface AiStatus {
  enabled: boolean;
  functions: Record<string, boolean>;
  provider: { code: string; name?: string | null; nameEn?: string | null; testOnly: boolean; ready: boolean; reason: string; reasonText: string; transcriptionReady: boolean; transcriptionReason: string };
  usage: { myToday: number; dailyLimit: number };
  policy: { maxClassification: string; hosting: string };
}
export interface AiCitation { ref: string; type: string; id?: string | null; label: string; link?: string | null }
export interface AiStatement { text: string; refs: string[] }
export interface AiResult<C = Record<string, unknown>> {
  outputId?: string | null; generated: true; draft: true; testOnly: boolean; provider: string; model: string;
  content: C; citations: AiCitation[]; warnings: string[]; excludedSources?: number; redactions?: number; noSources?: boolean; extra?: Record<string, unknown> | null;
}
export interface AiOutput {
  id: string; function: string; subjectType: string; subjectId?: string | null; bodyId?: string | null; requestedBy: string; requestedByName: string;
  status: 'draft' | 'saved' | 'applied' | 'discarded' | 'expired' | 'held'; generated: true; testOnly: boolean; provider: string; model: string; promptVersion: string;
  classification: string; sourceCount: number; heldReason?: string | null; reviewedBy?: string | null; reviewedAt?: string | null; reviewNote?: string | null;
  purgedAt?: string | null; createdAt: string; content?: Record<string, unknown> | null; citations?: AiCitation[] | null; warnings: string[];
  appliedRefs: { recordType: string; recordId: string; index?: number | null }[];
  can: { save: boolean; discard: boolean; applied: boolean; release: boolean }; mine: boolean;
}
export interface AiJob {
  id: string; status: 'queued' | 'running' | 'completed' | 'failed' | 'held' | 'cancelled'; attempts: number; lastError?: string | null; heldReason?: string | null;
  outputId?: string | null; consent: { reference: string; note?: string; confirmedByName?: string; confirmedAt?: string }; createdAt: string; updatedAt: string;
  fileName: string; documentId: string; requestedBy: string; closeReason?: string | null; can: { resume: boolean; cancel: boolean };
}
export interface AiFact { ref: string; kind: string; title: string; text: string; link?: string | null; type?: string | null; id?: string | null; label?: string | null }
export interface SearchHitView {
  type: string; id: string; title: string; reference?: string | null; bodyId: string; bodyName: string; status?: string | null; date?: string | null;
  snippet: string; link: string; dueDate?: string | null; aiProcessable?: boolean; classification?: string;
}
export interface AiOverview {
  settings: {
    enabled: boolean; provider: string; providerName?: string | null; providerNameEn?: string | null; testOnly: boolean; hosting: string; configuredMaxClassification: string;
    effectiveMaxClassification: string; baseUrl: string; model: string; transcriptionModel: string; timeoutSeconds: number; dailyPerUser: number;
    monthlyRequests: number; monthlyTokens: number; alertPercent: number; retentionDays: number;
  };
  functions: { code: string; flag: string; enabled: boolean }[];
  readiness: { ready: boolean; reason: string; text: string; transcriptionReady: boolean; transcriptionReason: string; transcriptionText: string };
  connection?: { status: string; detail: string; latencyMs?: number | null } | null;
  secret: { name: string; source: string; canStore: boolean; updatedAt?: string | null; updatedBy?: string | null; fingerprint?: string | null };
  allowedHosts: string[];
  usage: { period: string; todayRequests: number; monthRequests: number; monthTokens: number; requestsPct: number; tokensPct: number; alertPercent: number };
  alerts: { metric: string; threshold: number; value: number; limit: number; at: string }[];
  byFunction: { function: string; status: string; count: number; tokens: number; avgMs: number }[];
  outputs: { held: number; kept: number; drafts: number };
  jobs: { active: number; held: number; failed: number };
  inflight: number;
  mock?: { mode: string; delayMs: number; remaining: number } | null;
}
export interface AiRequestLog {
  id: string; at: string; function: string; user?: string | null; status: string; errorCode?: string | null; durationMs?: number | null; inputTokens?: number | null;
  outputTokens?: number | null; usageEstimated: boolean; provider?: string | null; model?: string | null; sources?: number | null; excluded: number; redactions: number;
  classification?: string | null; counted: boolean; outputId?: string | null; subjectType?: string | null; body?: string | null;
}
