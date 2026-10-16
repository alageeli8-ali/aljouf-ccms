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
  };
  systemRoles: string[];
  bodyRoles: { bodyId: string; role: string }[];
  permissions: string[];
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
}

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
  valueType: 'boolean' | 'enum';
  value: string;
  storedValue: string;
  defaultValue: string;
  allowedValues?: string[] | null;
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
