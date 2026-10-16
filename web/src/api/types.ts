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
