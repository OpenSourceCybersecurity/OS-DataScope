// Synthetic lab environment: one AD domain, two Windows file servers, eight
// shares. Every name, number and document here is made up. Card numbers are
// public issuer test numbers; SSNs are randomly generated.

import type {
  Ace,
  ActivityAlert,
  AuditEvent,
  FileItem,
  ManagedServer,
  OwnerAssignment,
  Principal,
  ReviewCampaign,
  Share,
} from '../domain/types';
import { WELL_KNOWN } from '../domain/access';

export const NOW = new Date('2026-10-09T17:00:00Z');
const daysAgo = (d: number, h = 0) => new Date(NOW.getTime() - d * 864e5 - h * 36e5).toISOString();
const daysAhead = (d: number) => new Date(NOW.getTime() + d * 864e5).toISOString();

// Deterministic PRNG so the demo looks the same on every load.
function mulberry32(seed: number) {
  return () => {
    seed |= 0;
    seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const rand = mulberry32(20261009);
const pick = <T,>(xs: T[]) => xs[Math.floor(rand() * xs.length)];
const int = (lo: number, hi: number) => lo + Math.floor(rand() * (hi - lo + 1));

export const DOMAIN = 'NORTHWIND';
const D = 'S-1-5-21-3623811015-3361044348-30300820';
const sid = (rid: number) => `${D}-${rid}`;
export const DOMAIN_USERS = sid(513);
const DOMAIN_ADMINS = sid(512);

// ---------- Directory ----------
type U = [rid: number, sam: string, name: string, dept: string, title: string, groups: string[], enabled?: boolean, lastLogonDays?: number];
const G = {
  HR_Read: sid(1201),
  HR_Write: sid(1202),
  Finance_Read: sid(1203),
  Finance_Write: sid(1204),
  Finance_Reviewers: sid(1205),
  Legal_Team: sid(1206),
  Ops_Team: sid(1207),
  Eng_Team: sid(1208),
  Sales_Team: sid(1209),
  IT_Admins: sid(1210),
  AllStaff: sid(1211),
  Helpdesk: sid(1212),
  Former_Contractors: sid(1213),
  Atlas_Core: sid(1214),
  Atlas_Members: sid(1215),
};
export const GROUPS = G;
const ORPHAN_SID = sid(1187); // deleted account still on an ACL

const usersSpec: U[] = [
  [1101, 'rdelgado', 'Rosa Delgado', 'Human Resources', 'HR Director', [G.HR_Write]],
  [1102, 'kosei', 'Kevin Osei', 'Human Resources', 'HR Generalist', [G.HR_Write]],
  [1103, 'tbrooks', 'Tanya Brooks', 'Human Resources', 'Payroll Specialist', [G.HR_Write, G.Finance_Read]],
  [1104, 'lcarter', 'Liam Carter', 'Human Resources', 'Recruiter', [G.HR_Read]],
  [1105, 'mlee', 'Marcus Lee', 'Finance', 'Controller', [G.Finance_Write]],
  [1106, 'pnair', 'Priya Nair', 'Finance', 'AP Specialist', [G.Finance_Write]],
  [1107, 'dkim', 'Daniel Kim', 'Finance', 'Staff Accountant', [G.Finance_Write]],
  [1108, 'gholloway', 'Grace Holloway', 'Finance', 'Financial Analyst', [G.Finance_Read]],
  [1109, 'ohaddad', 'Omar Haddad', 'Finance', 'Internal Auditor', [G.Finance_Reviewers]],
  [1110, 'epetrova', 'Elena Petrova', 'Legal', 'General Counsel', [G.Legal_Team]],
  [1111, 'swhitaker', 'Sam Whitaker', 'Legal', 'Paralegal', [G.Legal_Team]],
  [1112, 'jturner', 'Jamal Turner', 'Operations', 'Operations Manager', [G.Ops_Team]],
  [1113, 'hmoore', 'Hannah Moore', 'Operations', 'Logistics Coordinator', [G.Ops_Team]],
  [1114, 'valvarez', 'Victor Alvarez', 'Operations', 'Warehouse Lead', [G.Ops_Team]],
  [1115, 'mchen', 'Mei Chen', 'Operations', 'Purchasing Agent', [G.Ops_Team, G.Finance_Read]],
  [1116, 'arahman', 'Aisha Rahman', 'Engineering', 'Engineering Lead', [G.Eng_Team, G.Atlas_Core]],
  [1117, 'tbecker', 'Tom Becker', 'Engineering', 'Software Engineer', [G.Eng_Team, G.Atlas_Members]],
  [1118, 'nkowalski', 'Nina Kowalski', 'Engineering', 'QA Engineer', [G.Eng_Team]],
  [1119, 'rpatel', 'Raj Patel', 'Engineering', 'DevOps Engineer', [G.Eng_Team, G.Atlas_Members]],
  [1120, 'cdonovan', 'Chris Donovan', 'Sales', 'Sales Director', [G.Sales_Team]],
  [1121, 'lfitch', 'Lauren Fitch', 'Sales', 'Account Executive', [G.Sales_Team]],
  [1122, 'byoder', 'Ben Yoder', 'Sales', 'Sales Rep', [G.Sales_Team]],
  [1123, 'dwhitfield', 'Dana Whitfield', 'IT', 'IT Manager', [G.IT_Admins]],
  [1124, 'aromero', 'Alex Romero', 'IT', 'Systems Administrator', [G.IT_Admins]],
  [1125, 'ifrost', 'Ian Frost', 'IT', 'Helpdesk Technician', [G.Helpdesk]],
  [1126, 'gpalmer', 'Greg Palmer', 'Finance', 'Former AP Clerk', [G.Finance_Write], false, 212],
  [1127, 'jstone', 'Julia Stone', 'Engineering', 'Former Contractor', [G.Former_Contractors, G.Eng_Team], false, 160],
  [1128, 'svc_datascope', 'Data Scope Scanner', 'IT', 'Service account (read-only)', [], true, 0],
  [1129, 'wnguyen', 'Wendy Nguyen', 'Finance', 'Payroll Coordinator', [G.Finance_Read, G.HR_Read]],
  [1130, 'pgrant', 'Paul Grant', 'Executive', 'Chief Operating Officer', [G.Finance_Read, G.Ops_Team]],
];

const groupSpec: [string, string, string, string[]][] = [
  [G.HR_Read, 'HR_Read', 'Read HR share', [G.HR_Write]],
  [G.HR_Write, 'HR_Write', 'Modify HR share', []],
  [G.Finance_Read, 'Finance_Read', 'Read Finance share', []],
  [G.Finance_Write, 'Finance_Write', 'Modify Finance share', []],
  [G.Finance_Reviewers, 'Finance_Reviewers', 'Auditors who review Finance', [G.Finance_Read]],
  [G.Legal_Team, 'Legal_Team', 'Legal department', [G.AllStaff]],
  [G.Ops_Team, 'Ops_Team', 'Operations department', [G.AllStaff]],
  [G.Eng_Team, 'Eng_Team', 'Engineering department', [G.AllStaff]],
  [G.Sales_Team, 'Sales_Team', 'Sales department', [G.AllStaff]],
  [G.IT_Admins, 'IT_Admins', 'File server administrators', []],
  [G.AllStaff, 'AllStaff', 'Every department group', []],
  [G.Helpdesk, 'Helpdesk', 'Helpdesk technicians', [G.Finance_Write]],
  [G.Former_Contractors, 'Former_Contractors', 'Offboarded contractors', []],
  [G.Atlas_Core, 'ProjectAtlas_Core', 'Project Atlas core team', [G.Atlas_Members]],
  [G.Atlas_Members, 'ProjectAtlas_Members', 'Project Atlas members', [G.Atlas_Core]],
];

export function buildDirectory(): Principal[] {
  const out: Principal[] = [
    { sid: WELL_KNOWN.everyone, sam: 'Everyone', displayName: 'Everyone', type: 'wellknown', enabled: true, memberOf: [] },
    { sid: WELL_KNOWN.authenticatedUsers, sam: 'Authenticated Users', displayName: 'Authenticated Users', type: 'wellknown', enabled: true, memberOf: [] },
    { sid: WELL_KNOWN.builtinAdmins, sam: 'BUILTIN\\Administrators', displayName: 'Administrators (local)', type: 'wellknown', enabled: true, memberOf: [] },
    { sid: WELL_KNOWN.system, sam: 'SYSTEM', displayName: 'SYSTEM', type: 'wellknown', enabled: true, memberOf: [] },
    { sid: DOMAIN_USERS, sam: 'Domain Users', displayName: 'Domain Users', type: 'group', enabled: true, memberOf: [] },
    { sid: DOMAIN_ADMINS, sam: 'Domain Admins', displayName: 'Domain Admins', type: 'group', enabled: true, memberOf: [WELL_KNOWN.builtinAdmins] },
  ];
  for (const [s, sam, desc, memberOf] of groupSpec) {
    out.push({ sid: s, sam, displayName: sam, title: desc, type: 'group', enabled: true, memberOf });
  }
  for (const [rid, sam, name, dept, title, groups, enabled = true, lastLogon] of usersSpec) {
    const memberOf = [DOMAIN_USERS, ...groups];
    if (sam === 'dwhitfield' || sam === 'aromero') memberOf.push(DOMAIN_ADMINS);
    out.push({
      sid: sid(rid),
      sam,
      displayName: name,
      type: 'user',
      enabled,
      department: dept,
      title,
      email: `${sam}@northwind.example`,
      memberOf,
      lastLogon: daysAgo(lastLogon ?? int(0, 6), int(1, 9)),
    });
  }
  return out;
}

export const userSid = (sam: string) => sid(usersSpec.find((u) => u[1] === sam)![0]);

// ---------- Servers & shares ----------
export const SERVERS: ManagedServer[] = [
  { id: 'srv-fs01', hostname: 'NW-FS01', fqdn: 'nw-fs01.northwind.local', os: 'Windows Server 2022 Standard', state: 'enrolled', lastSeen: daysAgo(0, 0.2), ip: '10.20.1.21' },
  { id: 'srv-fs02', hostname: 'NW-FS02', fqdn: 'nw-fs02.northwind.local', os: 'Windows Server 2019 Standard', state: 'enrolled', lastSeen: daysAgo(0, 0.3), ip: '10.20.1.22' },
];

const ace = (s: string, rights: Ace['rights'], type: Ace['type'] = 'allow', inherited = false): Ace => ({
  sid: s,
  rights,
  type,
  inherited,
  mask: { None: '0x0', Read: '0x1200A9', Modify: '0x1301BF', FullControl: '0x1F01FF' }[rights],
});
const adminAces = [ace(G.IT_Admins, 'FullControl'), ace(WELL_KNOWN.system, 'FullControl'), ace(WELL_KNOWN.builtinAdmins, 'FullControl')];

export function buildShares(): Share[] {
  const scan = (d: number) => ({ state: 'complete' as const, lastScan: daysAgo(d, 3), progress: 1 });
  return [
    {
      id: 'sh-hr', serverId: 'srv-fs01', name: 'HR', unc: '\\\\NW-FS01\\HR', description: 'Personnel files, payroll and benefits',
      shareAcl: [ace(WELL_KNOWN.authenticatedUsers, 'Modify')],
      ntfsAcl: [ace(G.HR_Read, 'Read'), ace(G.HR_Write, 'Modify'), ...adminAces],
      folders: [
        { path: 'Benefits', inheritanceBroken: true, ntfsAcl: [ace(G.HR_Write, 'Modify'), ace(DOMAIN_USERS, 'Read'), ...adminAces] },
        { path: 'Employee Files', inheritanceBroken: false },
        { path: 'Payroll', inheritanceBroken: true, ntfsAcl: [ace(G.HR_Write, 'Modify'), ace(G.Finance_Read, 'Read'), ...adminAces] },
        { path: 'Recruiting', inheritanceBroken: false },
      ],
      scan: scan(1),
    },
    {
      id: 'sh-fin', serverId: 'srv-fs01', name: 'Finance', unc: '\\\\NW-FS01\\Finance', description: 'Accounting, AP/AR, banking and audit workpapers',
      shareAcl: [ace(WELL_KNOWN.everyone, 'FullControl')],
      ntfsAcl: [
        ace(G.Finance_Read, 'Read'), ace(G.Finance_Write, 'Modify'), ...adminAces,
        ace(userSid('gpalmer'), 'Modify'), ace(ORPHAN_SID, 'Read'),
      ],
      folders: [
        { path: 'Accounts Payable', inheritanceBroken: false },
        { path: 'Banking', inheritanceBroken: false },
        { path: 'Audit 2026', inheritanceBroken: false },
        { path: 'Budgets', inheritanceBroken: false },
      ],
      scan: scan(1),
    },
    {
      id: 'sh-legal', serverId: 'srv-fs01', name: 'Legal', unc: '\\\\NW-FS01\\Legal', description: 'Contracts, litigation and board materials',
      shareAcl: [ace(WELL_KNOWN.authenticatedUsers, 'Modify')],
      ntfsAcl: [ace(G.Legal_Team, 'Modify'), ace(userSid('epetrova'), 'FullControl'), ace(G.Former_Contractors, 'FullControl', 'deny'), ...adminAces],
      folders: [
        { path: 'Contracts', inheritanceBroken: false },
        { path: 'Litigation', inheritanceBroken: false },
        { path: 'Board', inheritanceBroken: false },
      ],
      scan: scan(2),
    },
    {
      id: 'sh-public', serverId: 'srv-fs01', name: 'Public', unc: '\\\\NW-FS01\\Public', description: 'Company-wide drop folder', intendedPublic: true,
      shareAcl: [ace(WELL_KNOWN.everyone, 'FullControl')],
      ntfsAcl: [ace(WELL_KNOWN.everyone, 'Modify'), ...adminAces],
      folders: [{ path: 'Templates', inheritanceBroken: false }, { path: 'Transfer', inheritanceBroken: false }],
      scan: scan(1),
    },
    {
      id: 'sh-ops', serverId: 'srv-fs02', name: 'Operations', unc: '\\\\NW-FS02\\Operations', description: 'Logistics, purchasing and site documentation',
      shareAcl: [ace(WELL_KNOWN.everyone, 'FullControl')],
      ntfsAcl: [ace(G.Ops_Team, 'Modify'), ace(G.AllStaff, 'Read'), ...adminAces],
      folders: [{ path: 'Purchasing', inheritanceBroken: false }, { path: 'Sites', inheritanceBroken: false }, { path: 'IT Handover', inheritanceBroken: false }],
      scan: scan(1),
    },
    {
      id: 'sh-eng', serverId: 'srv-fs02', name: 'Engineering', unc: '\\\\NW-FS02\\Engineering', description: 'Source exports, build configs and Project Atlas',
      shareAcl: [ace(WELL_KNOWN.authenticatedUsers, 'Modify')],
      ntfsAcl: [ace(G.Eng_Team, 'Modify'), ace(G.Atlas_Core, 'Read'), ...adminAces],
      folders: [{ path: 'Atlas', inheritanceBroken: false }, { path: 'Builds', inheritanceBroken: false }, { path: 'Docs', inheritanceBroken: false }],
      scan: scan(1),
    },
    {
      id: 'sh-sales', serverId: 'srv-fs02', name: 'Sales', unc: '\\\\NW-FS02\\Sales', description: 'Quotes, customer orders and commissions',
      shareAcl: [ace(G.Sales_Team, 'Modify'), ...adminAces.slice(0, 1)],
      ntfsAcl: [ace(G.Sales_Team, 'Modify'), ace(G.Finance_Read, 'Read'), ...adminAces],
      folders: [{ path: 'Orders', inheritanceBroken: false }, { path: 'Commissions', inheritanceBroken: false }, { path: 'Quotes', inheritanceBroken: false }],
      scan: scan(2),
    },
    {
      id: 'sh-scans', serverId: 'srv-fs02', name: 'Scans', unc: '\\\\NW-FS02\\Scans', description: 'Multifunction printer scan-to-folder target',
      shareAcl: [ace(WELL_KNOWN.everyone, 'FullControl')],
      ntfsAcl: [ace(WELL_KNOWN.everyone, 'FullControl'), ...adminAces.slice(1)],
      folders: [{ path: 'Inbox', inheritanceBroken: false }],
      scan: { state: 'partial', lastScan: daysAgo(3, 2), progress: 1 },
    },
  ];
}

// ---------- Synthetic file contents ----------
const FIRST = ['Ava', 'Noah', 'Mia', 'Ethan', 'Zoe', 'Lucas', 'Ivy', 'Owen', 'Leah', 'Caleb', 'Nora', 'Eli', 'Ruby', 'Jonah', 'Tess', 'Felix'];
const LAST = ['Archer', 'Bell', 'Cruz', 'Dunn', 'Ellis', 'Ford', 'Gray', 'Hale', 'Ingram', 'Jett', 'Keane', 'Lowe', 'Marsh', 'Nash', 'Orr', 'Pike'];
const person = () => `${pick(FIRST)} ${pick(LAST)}`;
function fakeSsn() {
  let a = int(101, 665);
  if (a === 666) a = 667;
  return `${String(a).padStart(3, '0')}-${String(int(10, 99))}-${String(int(1001, 9899))}`;
}
const TEST_CARDS = ['4111 1111 1111 1111', '5555 5555 5555 4444', '3782 822463 10005', '6011 1111 1111 1117', '4012 8888 8888 1881'];

const payrollCsv = (n: number) =>
  ['Employee,SSN,Department,Annual Salary,Direct Deposit Routing,Account']
    .concat(Array.from({ length: n }, () => `${person()},${fakeSsn()},${pick(['Ops', 'Sales', 'Eng', 'HR'])},${int(48, 160)}000,routing 011000015,account ${int(1000000, 9999999)}`))
    .join('\n');
const employeeRecord = () =>
  `EMPLOYEE RECORD\nName: ${person()}\nSSN: ${fakeSsn()}\nDOB: ${int(1, 12)}/${int(1, 28)}/${int(1965, 2001)}\nTitle: ${pick(['Coordinator', 'Analyst', 'Technician'])}\nEmergency contact: ${person()}`;
const benefitsClaim = () =>
  `FMLA / disability claim worksheet\nEmployee: ${person()}  SSN ${fakeSsn()}\nPhysician statement attached. Diagnosis code M54.5 and G43.909.\nTreatment plan: physical therapy 6 weeks. Medication list on file.`;
const cardSheet = () =>
  `Corporate card reconciliation - ${pick(['Q2', 'Q3'])} 2026\nCardholder,Card number,Exp,Amount\n` +
  Array.from({ length: 5 }, () => `${person()},${pick(TEST_CARDS)},${int(1, 12)}/${int(27, 30)},${int(40, 900)}.00`).join('\n');
const vendorBank = () =>
  `Vendor ACH setup form\nVendor: ${pick(['Harbor Freight Lines', 'Cobalt Packaging', 'Lumen Office Supply'])}\nBank: First Example Bank\nRouting number: 011000015\nAccount number: ${int(10000000, 99999999)}\nTax ID on file.`;
const vpnBackup = () =>
  `# NW-FW01 config export (do not share)\nvpn.psk = "Wint3r-Gate-2026!"\nadmin password: N0rthw1nd#Adm\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEAsyntheticfixturekeymaterialonly0000\n-----END RSA PRIVATE KEY-----`;
const envFile = () => `APP_ENV=staging\nDB_HOST=db01.northwind.local\nDB_PASSWORD=atlas-staging-42\nAWS_ACCESS_KEY_ID=AKIAEXAMPLESYNTH7Q2Z\n`;
const partsList = () =>
  `Replacement parts list - Site 3\nPart# 411-22-3333 conveyor bearing qty 4\nPart# 512-08-7741 belt tensioner qty 2\nOrder ref PO-2026-118842\nCall vendor 555-201-8834`;
const benign = () => `Meeting notes ${int(1, 28)} Sept 2026\nAttendees: ${person()}, ${person()}\nInvoice 482913374 paid. Order 7781203345 shipped. Next review in two weeks.`;
const ndaContract = () => `MUTUAL NONDISCLOSURE AGREEMENT between Northwind Traders and ${pick(['Acme Robotics', 'Blue Harbor LLC'])}. Signed by ${person()}. Governing law: Delaware.`;
const settlement = () => `Settlement memo - privileged\nClaimant ${person()}, SSN ${fakeSsn()}, DOB 04/12/1979. Medical records reviewed: diagnosis S83.511A, physician notes and treatment plan.`;
const commission = () => `Commission statement\nRep: ${person()}\nPayout account routing 011000015 account ${int(100000000, 999999999)}\nSSN for 1099: ${fakeSsn()}`;
const customerOrder = () => `Customer order form\nCustomer: ${person()}\nCard: ${pick(TEST_CARDS)}  exp ${int(1, 12)}/28  cvv on phone\nShip to: 12 Example Ave`;
const dobList = () => ['Name,DOB,Start date'].concat(Array.from({ length: 6 }, () => `${person()},DOB ${int(1, 12)}/${int(1, 28)}/${int(1970, 2002)},2026-0${int(1, 9)}-0${int(1, 9)}`)).join('\n');
const exportCsv = () => ['Name,SSN,Hire date'].concat(Array.from({ length: 8 }, () => `${person()},${fakeSsn()},2019-0${int(1, 9)}-1${int(0, 9)}`)).join('\n');

interface FileSpec { path: string; content?: () => string; status?: FileItem['scanStatus']; ageDays?: number; size?: number; owner?: string }

const sensitiveFiles: Record<string, FileSpec[]> = {
  'sh-hr': [
    { path: 'Payroll/2026/payroll-register-2026-09.csv', content: () => payrollCsv(14) },
    { path: 'Payroll/2026/payroll-register-2026-08.csv', content: () => payrollCsv(14) },
    { path: 'Payroll/2025/w2-corrections.xlsx', content: () => payrollCsv(6) },
    { path: 'Employee Files/Archer, Ava/onboarding-packet.pdf', content: employeeRecord },
    { path: 'Employee Files/Cruz, Noah/i9-and-id-copies.pdf', content: employeeRecord },
    { path: 'Employee Files/Hale, Mia/personnel-record.docx', content: employeeRecord },
    { path: 'Employee Files/Dunn, Owen/personnel-record.docx', content: employeeRecord },
    { path: 'Benefits/FMLA/claim-worksheet-0912.docx', content: benefitsClaim },
    { path: 'Benefits/FMLA/claim-worksheet-0824.docx', content: benefitsClaim },
    { path: 'Benefits/Enrollment/open-enrollment-2026.xlsx', content: () => payrollCsv(9) },
    { path: 'Benefits/Disability/std-claims-tracker.xlsx', content: benefitsClaim },
    { path: 'Recruiting/offer-letters/offer-template.docx', content: benign },
    { path: 'Recruiting/background-check-roster.xlsx', content: dobList },
    { path: 'Employee Files/birthday-calendar-2026.xlsx', content: dobList },
    { path: 'Employee Files/terminations-2024.xlsx', status: 'encrypted' },
  ],
  'sh-fin': [
    { path: 'Accounts Payable/vendor-ach-cobalt.pdf', content: vendorBank },
    { path: 'Accounts Payable/vendor-ach-harbor.pdf', content: vendorBank },
    { path: 'Accounts Payable/vendor-ach-lumen.docx', content: vendorBank },
    { path: 'Banking/corporate-card-recon-q3.xlsx', content: cardSheet },
    { path: 'Banking/corporate-card-recon-q2.xlsx', content: cardSheet },
    { path: 'Banking/wire-instructions.txt', content: vendorBank },
    { path: 'Audit 2026/payroll-sample-testing.xlsx', content: () => payrollCsv(5) },
    { path: 'Audit 2026/1099-contractors.csv', content: commission },
    { path: 'Budgets/fy27-budget-draft.xlsx', content: benign },
    { path: 'Banking/bank-statements-2026.pdf', status: 'unsupported' },
    { path: 'Audit 2026/auditor-portal-export.zip', status: 'encrypted' },
  ],
  'sh-legal': [
    { path: 'Litigation/Marsh v Northwind/settlement-memo.docx', content: settlement },
    { path: 'Litigation/Marsh v Northwind/medical-summary.pdf', content: settlement },
    { path: 'Contracts/NDAs/nda-acme-robotics.pdf', content: ndaContract },
    { path: 'Contracts/NDAs/nda-blue-harbor.pdf', content: ndaContract },
    { path: 'Board/minutes-2026-q3.docx', content: benign },
    { path: 'Board/equity-grants.xlsx', status: 'encrypted' },
  ],
  'sh-public': [
    { path: 'Transfer/old_employee_export.csv', content: exportCsv, ageDays: 410 },
    { path: 'Transfer/for-tanya-payroll-fix.xlsx', content: () => payrollCsv(4), ageDays: 38 },
    { path: 'Transfer/customer-cards-to-key-in.txt', content: customerOrder, ageDays: 12 },
    { path: 'Templates/expense-report.xlsx', content: benign },
    { path: 'Templates/letterhead.docx', content: benign },
  ],
  'sh-ops': [
    { path: 'IT Handover/fw01-config-backup.txt', content: vpnBackup, ageDays: 95 },
    { path: 'Purchasing/site3-parts-list.xlsx', content: partsList },
    { path: 'Purchasing/vendor-setup-cobalt.pdf', content: vendorBank },
    { path: 'Sites/forklift-certifications.xlsx', content: dobList },
    { path: 'Sites/site3-safety-incident.docx', content: () => `Incident report. Employee ${person()} treated at clinic, physician note and medication list attached. Diagnosis S61.012A.` },
  ],
  'sh-eng': [
    { path: 'Atlas/deploy/.env.staging', content: envFile },
    { path: 'Builds/ci/secrets.backup.json', content: () => `{"api_key": "sk_synthetic_5f2a91c0", "password": "BuildBot2026"}` },
    { path: 'Docs/architecture-overview.docx', content: benign },
  ],
  'sh-sales': [
    { path: 'Orders/phone-orders-sept.xlsx', content: customerOrder },
    { path: 'Orders/phone-orders-aug.xlsx', content: customerOrder },
    { path: 'Commissions/commission-statements-q3.pdf', content: commission },
    { path: 'Quotes/quote-template.docx', content: benign },
    { path: 'Orders/customer-age-verification.csv', content: dobList },
  ],
  'sh-scans': [
    { path: 'Inbox/scan_20260921_0912.pdf', status: 'unsupported' },
    { path: 'Inbox/scan_20260930_1418.pdf', status: 'unsupported' },
    { path: 'Inbox/scan_20261002_0833.pdf', status: 'unsupported' },
    { path: 'Inbox/scan_20261007_1602.pdf', content: employeeRecord },
  ],
};

const fillerNames: Record<string, string[]> = {
  'sh-hr': ['Recruiting/job-description', 'Recruiting/interview-guide', 'Employee Files/handbook-ack', 'Benefits/plan-summary', 'Recruiting/careers-page-copy'],
  'sh-fin': ['Budgets/department-forecast', 'Accounts Payable/invoice', 'Audit 2026/workpaper', 'Budgets/capex-request', 'Banking/cash-position'],
  'sh-legal': ['Contracts/msa-draft', 'Contracts/sow', 'Board/agenda', 'Litigation/correspondence'],
  'sh-public': ['Templates/form', 'Transfer/photo', 'Templates/presentation', 'Transfer/notes'],
  'sh-ops': ['Sites/floor-plan', 'Purchasing/po', 'Sites/inspection', 'Purchasing/quote', 'Sites/sop'],
  'sh-eng': ['Atlas/spec', 'Builds/build-log', 'Docs/runbook', 'Atlas/design', 'Docs/release-notes'],
  'sh-sales': ['Quotes/quote', 'Orders/order-confirmation', 'Commissions/plan', 'Quotes/proposal'],
  'sh-scans': ['Inbox/scan'],
};
const fillerCounts: Record<string, number> = { 'sh-hr': 140, 'sh-fin': 220, 'sh-legal': 90, 'sh-public': 75, 'sh-ops': 180, 'sh-eng': 260, 'sh-sales': 120, 'sh-scans': 40 };
const EXTS = ['docx', 'docx', 'docx', 'xlsx', 'xlsx', 'xlsx', 'pdf', 'pdf', 'txt', 'csv', 'pptx', 'png', 'msg'];
const SUPPORTED = new Set(['docx', 'xlsx', 'pdf', 'txt', 'csv', 'json', 'xml']);

const shareOwnerSam: Record<string, string[]> = {
  'sh-hr': ['rdelgado', 'kosei', 'tbrooks'], 'sh-fin': ['mlee', 'pnair', 'dkim', 'gpalmer'], 'sh-legal': ['epetrova', 'swhitaker'],
  'sh-public': ['lfitch', 'hmoore', 'tbrooks', 'byoder'], 'sh-ops': ['jturner', 'hmoore', 'aromero'], 'sh-eng': ['arahman', 'tbecker', 'rpatel', 'jstone'],
  'sh-sales': ['cdonovan', 'lfitch', 'byoder'], 'sh-scans': ['svc_datascope'],
};

export function buildFiles(): FileItem[] {
  const files: FileItem[] = [];
  let n = 0;
  const push = (shareId: string, spec: FileSpec) => {
    const name = spec.path.split('/').pop()!;
    const ext = name.includes('.') ? name.split('.').pop()!.toLowerCase() : '';
    const age = spec.ageDays ?? int(2, 900);
    const status: FileItem['scanStatus'] = spec.status ?? (SUPPORTED.has(ext) || name.startsWith('.env') ? 'scanned' : 'unsupported');
    files.push({
      id: `f${++n}`,
      shareId,
      path: spec.path,
      name,
      ext: name.startsWith('.env') ? 'env' : ext,
      size: spec.size ?? int(8, 2400) * 1024,
      modified: daysAgo(age, int(0, 20)),
      created: daysAgo(age + int(0, 400)),
      ownerSid: userSid(pick(shareOwnerSam[shareId])),
      scanStatus: status,
      findings: [],
      policyClassification: status === 'scanned' ? 'Internal' : 'Unknown',
      content: spec.content?.(),
    });
  };
  for (const [shareId, list] of Object.entries(sensitiveFiles)) list.forEach((s) => push(shareId, s));
  for (const [shareId, names] of Object.entries(fillerNames)) {
    for (let i = 0; i < fillerCounts[shareId]; i++) {
      const ext = shareId === 'sh-scans' ? 'pdf' : pick(EXTS);
      const base = pick(names);
      const spec: FileSpec = { path: `${base}-${String(int(1, 999)).padStart(3, '0')}.${ext}` };
      if (shareId === 'sh-scans') spec.status = 'unsupported';
      else if (rand() < 0.012) spec.status = 'corrupt';
      else if (shareId === 'sh-legal' && base.startsWith('Litigation') && rand() < 0.3) spec.status = 'denied';
      push(shareId, spec);
    }
  }
  // Old files and copies for Stale & duplicates (D119). Fixed values and no random draws, so the rest of the demo stays the same.
  const archive: [shareId: string, path: string, ageDays: number, kb: number, content?: string][] = [
    ['sh-hr', 'Archive/2018/payroll-2018.csv', 2900, 412, 'Name,SSN,Salary\nAvery Okafor,512-48-3307,61000\nJordan Lee,512-48-3308,58000'],
    ['sh-hr', 'Archive/2018/benefits-enrollment-2018.xlsx', 2850, 1180],
    ['sh-hr', 'Archive/2019/terminations-2019.docx', 2500, 96],
    ['sh-fin', 'Closed Years/FY2019/general-ledger-fy2019.xlsx', 2400, 8800],
    ['sh-fin', 'Closed Years/FY2019/vendor-bank-details.csv', 2380, 64, 'Vendor,Routing,Account\nAcme Paper,routing 021000021 account 123456789'],
    ['sh-fin', 'Closed Years/FY2020/audit-workpapers-fy2020.pdf', 2050, 24600],
    ['sh-ops', 'Old Projects/warehouse-move-2017.pptx', 3300, 41200],
    ['sh-ops', 'Old Projects/forklift-training-2018.mp4', 2950, 612000],
    ['sh-eng', 'Releases/build-2.4-installer.zip', 1900, 298000],
    ['sh-public', 'Company/Employee-Handbook-2024.pdf', 700, 3400],
    ['sh-hr', 'Policies/Employee-Handbook-2024.pdf', 700, 3400],
    ['sh-sales', 'Collateral/Employee-Handbook-2024.pdf', 700, 3400],
    ['sh-sales', 'Collateral/price-list-2025.xlsx', 300, 220],
    ['sh-fin', 'Pricing/price-list-2025.xlsx', 300, 220],
  ];
  for (const [shareId, path, ageDays, kb, content] of archive) {
    const name = path.split('/').pop()!;
    const ext = name.split('.').pop()!.toLowerCase();
    files.push({
      id: `f${++n}`,
      shareId,
      path,
      name,
      ext,
      size: kb * 1024,
      modified: daysAgo(ageDays),
      created: daysAgo(ageDays),
      ownerSid: userSid(shareOwnerSam[shareId][0]),
      scanStatus: SUPPORTED.has(ext) ? 'scanned' : 'unsupported',
      findings: [],
      policyClassification: SUPPORTED.has(ext) ? 'Internal' : 'Unknown',
      content,
    });
  }
  return files;
}

// ---------- Ownership ----------
export function buildOwners(): OwnerAssignment[] {
  return [
    { shareId: 'sh-hr', ownerSid: userSid('rdelgado'), backupOwnerSid: userSid('kosei'), department: 'Human Resources', purpose: 'Personnel, payroll and benefits records', cadence: 'Quarterly', confirmed: true },
    { shareId: 'sh-fin', ownerSid: userSid('mlee'), backupOwnerSid: userSid('dkim'), department: 'Finance', purpose: 'Accounting and banking', cadence: 'Quarterly', confirmed: true },
    { shareId: 'sh-legal', ownerSid: userSid('epetrova'), backupOwnerSid: userSid('swhitaker'), department: 'Legal', purpose: 'Contracts and litigation', cadence: 'Semiannual', confirmed: true },
    { shareId: 'sh-eng', ownerSid: userSid('arahman'), department: 'Engineering', purpose: 'Engineering documentation and build configs', cadence: 'Semiannual', confirmed: true },
    { shareId: 'sh-ops', cadence: 'Semiannual', confirmed: false, suggestedOwnerSid: userSid('jturner'), suggestionReason: 'Ops_Team holds Modify and Jamal Turner is the department manager in AD.' },
    { shareId: 'sh-sales', cadence: 'Semiannual', confirmed: false, suggestedOwnerSid: userSid('cdonovan'), suggestionReason: 'Sales_Team holds Modify and Chris Donovan created 41% of files.' },
    { shareId: 'sh-public', cadence: 'Annual', confirmed: false },
    { shareId: 'sh-scans', cadence: 'Annual', confirmed: false },
  ];
}

// ---------- Campaigns (items are generated from snapshots in the store) ----------
export function buildCampaigns(): ReviewCampaign[] {
  return [
    { id: 'cmp-fin-q4', name: 'Finance access review, Q4 2026', shareIds: ['sh-fin'], reviewerSid: userSid('mlee'), backupReviewerSid: userSid('dkim'), createdBy: userSid('dwhitfield'), createdAt: daysAgo(5), snapshotAt: daysAgo(5, 1), dueAt: daysAhead(9), status: 'Active', revision: 1, emailSentAt: daysAgo(5) },
    { id: 'cmp-legal-h2', name: 'Legal access review, H2 2026', shareIds: ['sh-legal'], reviewerSid: userSid('epetrova'), backupReviewerSid: userSid('swhitaker'), createdBy: userSid('dwhitfield'), createdAt: daysAgo(24), snapshotAt: daysAgo(24, 1), dueAt: daysAgo(3), status: 'Overdue', revision: 1, emailSentAt: daysAgo(24) },
    { id: 'cmp-hr-q3', name: 'HR access review, Q3 2026', shareIds: ['sh-hr'], reviewerSid: userSid('rdelgado'), createdBy: userSid('dwhitfield'), createdAt: daysAgo(70), snapshotAt: daysAgo(70, 1), dueAt: daysAgo(55), status: 'Completed', revision: 1, emailSentAt: daysAgo(70) },
  ];
}

// ---------- File activity (V1.5 collector preview) ----------
export function buildActivity(shares: Share[], files: FileItem[]): { events: AuditEvent[]; alerts: ActivityAlert[] } {
  const events: AuditEvent[] = [];
  let rec = 884_210;
  const push = (e: Omit<AuditEvent, 'id' | 'recordId'>) => events.push({ ...e, id: `ev${rec}`, recordId: rec++ });
  const byShare = (id: string) => files.filter((f) => f.shareId === id);
  const actorsByShare: Record<string, string[]> = {
    'sh-hr': ['rdelgado', 'kosei', 'tbrooks', 'wnguyen', 'lcarter'], 'sh-fin': ['mlee', 'pnair', 'dkim', 'gholloway', 'ohaddad', 'tbrooks'],
    'sh-legal': ['epetrova', 'swhitaker'], 'sh-public': ['lfitch', 'hmoore', 'byoder', 'kosei', 'tbecker'], 'sh-ops': ['jturner', 'hmoore', 'valvarez', 'mchen'],
    'sh-eng': ['arahman', 'tbecker', 'nkowalski', 'rpatel'], 'sh-sales': ['cdonovan', 'lfitch', 'byoder'], 'sh-scans': ['kosei', 'pnair'],
  };
  for (let i = 0; i < 420; i++) {
    const share = pick(shares);
    const f = pick(byShare(share.id));
    const who = userSid(pick(actorsByShare[share.id]));
    const t = new Date(NOW.getTime() - rand() * 7 * 864e5).toISOString();
    const corr = `c${i}`;
    push({ eventId: 5145, time: t, serverId: share.serverId, shareId: share.id, path: f.path, principalSid: who, operation: 'Share access check', certainty: 'access-check', outcome: 'success', accessMask: '0x1', correlationId: corr });
    const op = rand();
    if (op < 0.62) push({ eventId: 4663, time: t, serverId: share.serverId, shareId: share.id, path: f.path, principalSid: who, operation: 'Read', certainty: 'confirmed', outcome: 'success', accessMask: '0x1 (ReadData)', correlationId: corr });
    else if (op < 0.9) push({ eventId: 4663, time: t, serverId: share.serverId, shareId: share.id, path: f.path, principalSid: who, operation: 'Write', certainty: 'confirmed', outcome: 'success', accessMask: '0x2 (WriteData)', correlationId: corr });
  }
  // A Sales rep probing Finance: failed handle requests.
  for (let i = 0; i < 6; i++) {
    const t = daysAgo(1, 3 + i * 0.05);
    push({ eventId: 4656, time: t, serverId: 'srv-fs01', shareId: 'sh-fin', path: pick(byShare('sh-fin')).path, principalSid: userSid('byoder'), operation: 'Handle request', certainty: 'attempted', outcome: 'failure', accessMask: '0x1 (ReadData)' });
  }
  // Permission change that broke HR\Benefits.
  push({ eventId: 4670, time: daysAgo(34, 5), serverId: 'srv-fs01', shareId: 'sh-hr', path: 'Benefits', principalSid: userSid('aromero'), operation: 'Permission change', certainty: 'confirmed', outcome: 'success', accessMask: 'WRITE_DAC' });
  // Mass delete burst on Operations.
  const burstStart = NOW.getTime() - 0.6 * 864e5;
  for (let i = 0; i < 64; i++) {
    const t = new Date(burstStart + i * 5200).toISOString();
    const f = pick(byShare('sh-ops'));
    push({ eventId: 4663, time: t, serverId: 'srv-fs02', shareId: 'sh-ops', path: f.path, principalSid: userSid('valvarez'), operation: 'Delete', certainty: 'confirmed', outcome: 'success', accessMask: '0x10000 (DELETE)', correlationId: `del${i}` });
    push({ eventId: 4660, time: t, serverId: 'srv-fs02', shareId: 'sh-ops', principalSid: userSid('valvarez'), operation: 'Delete', certainty: 'confirmed', outcome: 'success', correlationId: `del${i}` });
  }
  events.sort((a, b) => b.time.localeCompare(a.time));
  const alerts: ActivityAlert[] = [
    { id: 'al-1', rule: 'Mass deletion', severity: 'High', time: new Date(burstStart).toISOString(), principalSid: userSid('valvarez'), shareId: 'sh-ops', summary: '64 files deleted on Operations in 5.5 minutes (threshold: 50 in 10 minutes).', eventCount: 128, state: 'Open' },
    { id: 'al-2', rule: 'Repeated access denied', severity: 'Medium', time: daysAgo(1, 3), principalSid: userSid('byoder'), shareId: 'sh-fin', summary: '6 failed attempts to open Finance files by a Sales account in 20 minutes.', eventCount: 6, state: 'Open' },
    { id: 'al-3', rule: 'Permission change on sensitive folder', severity: 'High', time: daysAgo(34, 5), principalSid: userSid('aromero'), shareId: 'sh-hr', summary: 'NTFS permissions changed on HR\\Benefits. Domain Users gained Read.', eventCount: 1, state: 'Acknowledged' },
  ];
  return { events, alerts };
}
