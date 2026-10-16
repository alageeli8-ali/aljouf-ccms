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
  code: string;
  bodyType: string;
  nameAr: string;
  nameEn?: string | null;
  mandateAr?: string | null;
  formationDecisionNo?: string | null;
  formationDecisionDate?: string | null;
  termStart?: string | null;
  termEnd?: string | null;
  classification: string;
  status: string;
  chairName?: string | null;
  secretaryName?: string | null;
  memberCount: number;
  createdAt: string;
}

export interface Member {
  id: string;
  userId: string;
  displayNameAr: string;
  displayNameEn?: string | null;
  email: string;
  role: string;
  isVoting: boolean;
  entityName?: string | null;
  startDate?: string | null;
  endDate?: string | null;
  status: string;
}

export interface Meeting {
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
  location?: string | null;
  onlineLink?: string | null;
  status: string;
  agendaCount: number;
  minutesId?: string | null;
}

export interface AgendaItem {
  id: string;
  position: number;
  titleAr: string;
  itemType: string;
  presenterText?: string | null;
  estimatedMinutes?: number | null;
  status: string;
  memoText?: string | null;
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
