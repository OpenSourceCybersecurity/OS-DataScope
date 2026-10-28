// Builds a synthetic "file server" for development: lab-data/<host>/<share>/...
// Every name and byte here is made up. SSNs are random numbers in valid ranges and card numbers are published
// test numbers, so the detectors have something to find. Run: node scripts/make-lab.mjs [folder] [host]
import { mkdirSync, writeFileSync, symlinkSync, existsSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';

const root = process.argv[2] ?? 'lab-data';
const host = process.argv[3] ?? 'nw-fs01';
const server = join(root, host);
if (existsSync(server)) rmSync(server, { recursive: true, force: true });

const write = (share, path, data) => {
  const full = join(server, share, ...path.split('/'));
  mkdirSync(dirname(full), { recursive: true });
  writeFileSync(full, data);
};
const filler = (share, path, bytes = 64) => write(share, path, 'synthetic test data\n'.repeat(Math.max(1, Math.ceil(bytes / 20))).slice(0, bytes));

// ---------- tiny writers for real Office and PDF files: an uncompressed zip and a one-page PDF ----------
const CRC = new Uint32Array(256).map((_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
const crc32 = (buf) => {
  let c = 0xffffffff;
  for (const b of buf) c = CRC[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};

function zip(entries) {
  const parts = [];
  const central = [];
  let offset = 0;
  for (const [name, text] of entries) {
    const data = Buffer.from(text, 'utf8');
    const nameBuf = Buffer.from(name, 'utf8');
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBuf.length, 26);
    const header = Buffer.alloc(46);
    header.writeUInt32LE(0x02014b50, 0);
    header.writeUInt16LE(20, 4);
    header.writeUInt16LE(20, 6);
    header.writeUInt32LE(crc, 16);
    header.writeUInt32LE(data.length, 20);
    header.writeUInt32LE(data.length, 24);
    header.writeUInt16LE(nameBuf.length, 28);
    header.writeUInt32LE(offset, 42);
    parts.push(local, nameBuf, data);
    central.push(header, nameBuf);
    offset += 30 + nameBuf.length + data.length;
  }
  const directory = Buffer.concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(directory.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...parts, directory, end]);
}

const esc = (t) => t.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const TYPES = ['[Content_Types].xml', '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>'];

const docx = (paragraphs) =>
  zip([
    TYPES,
    [
      'word/document.xml',
      `<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>${paragraphs
        .map((p) => `<w:p><w:r><w:t xml:space="preserve">${esc(p)}</w:t></w:r></w:p>`)
        .join('')}</w:body></w:document>`,
    ],
  ]);

const xlsx = (rows) => {
  const strings = [];
  const cell = (v) => (typeof v === 'number' ? `<c><v>${v}</v></c>` : `<c t="s"><v>${strings.push(String(v)) - 1}</v></c>`);
  const sheet = rows.map((r) => `<row>${r.map(cell).join('')}</row>`).join('');
  return zip([
    TYPES,
    ['xl/worksheets/sheet1.xml', `<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>${sheet}</sheetData></worksheet>`],
    ['xl/sharedStrings.xml', `<?xml version="1.0"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">${strings.map((t) => `<si><t>${esc(t)}</t></si>`).join('')}</sst>`],
  ]);
};

function pdf(lines) {
  const body = lines.map((l, i) => `BT /F1 11 Tf 72 ${720 - i * 16} Td (${l.replace(/[()\\]/g, '\\$&')}) Tj ET`).join('\n');
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>',
    `<< /Length ${Buffer.byteLength(body, 'latin1')} >>\nstream\n${body}\nendstream`,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ];
  let out = '%PDF-1.4\n';
  const offsets = objects.map((o, i) => {
    const at = Buffer.byteLength(out, 'latin1');
    out += `${i + 1} 0 obj\n${o}\nendobj\n`;
    return at;
  });
  const xref = Buffer.byteLength(out, 'latin1');
  out += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('')}`;
  out += `trailer << /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(out, 'latin1');
}

// ---------- synthetic people (fixed seed, so every run makes the same lab) ----------
let seed = 20261009;
const rand = (n) => {
  seed = (seed * 1103515245 + 12345) % 2147483648;
  return seed % n;
};
const FIRST = ['Avery', 'Jordan', 'Riley', 'Morgan', 'Casey', 'Quinn', 'Harper', 'Rowan', 'Emerson', 'Sasha'];
const LAST = ['Okafor', 'Lindqvist', 'Tanaka', 'Moreau', 'Silva', 'Novak', 'Haddad', 'Byrne', 'Petrov', 'Mensah'];
const person = () => `${FIRST[rand(FIRST.length)]} ${LAST[rand(LAST.length)]}`;
const ssn = () => `${String(100 + rand(565)).padStart(3, '0')}-${10 + rand(89)}-${1000 + rand(8999)}`;
const dob = () => `${1 + rand(12)}/${1 + rand(28)}/19${60 + rand(39)}`;

// ---------- HR ----------
const months = ['01-jan', '02-feb', '03-mar', '04-apr', '05-may', '06-jun', '07-jul', '08-aug', '09-sep', '10-oct', '11-nov', '12-dec'];
for (const year of [2023, 2024, 2025]) {
  for (const m of months) {
    const rows = [['Employee', 'SSN', 'Gross pay']];
    for (let i = 0; i < 8; i++) rows.push([person(), ssn(), 3000 + rand(4000)]);
    write('HR', `Payroll/${year}/${m}/payroll-register.xlsx`, xlsx(rows));
  }
}
for (let i = 1; i <= 40; i++) {
  write('HR', `Employees/E${1000 + i}/offer-letter.docx`, docx([`Dear ${person()},`, 'We are pleased to offer you the position of Analyst.', `Employee date of birth: ${dob()}`]));
}
write('HR', 'Benefits/fmla-request.docx', docx(['FMLA request', `Employee: ${person()}  SSN: ${ssn()}`, 'Physician certification attached. Diagnosis: M54.5. Treatment plan: physical therapy.']));
write('HR', 'Policies/employee-handbook.pdf', pdf(['Northwind Traders employee handbook', 'Our clinic partner offers flu shots every October.', 'Questions? Ask HR.']));
write('HR', 'Policies/remote-work.docx', docx(['Remote work policy', 'Staff may work from home up to three days a week.']));

// ---------- Finance ----------
for (let i = 1; i <= 120; i++) {
  write('Finance', `AP/Invoices/2025/INV-${4000 + i}.pdf`, pdf([`Invoice INV-${4000 + i}`, `Order ${100000000 + rand(899999999)} shipped.`, `Total due: $${100 + rand(9000)}.00`]));
}
write('Finance', 'AP/vendor-banking.csv', `Vendor,Bank details\nContoso Supplies,routing 011000015 account 4400${1000 + rand(8999)}\n`);
write('Finance', 'AR/card-payments.txt', 'Customer paid by Visa 4111 1111 1111 1111 exp 04/28\nRefund to Amex 3782 822463 10005\n');
write('Finance', 'Budget/FY26-budget.xlsx', xlsx([['Department', 'Budget'], ['Operations', 1200000], ['HR', 450000]]));
filler('Finance', 'Audit/2024/workpapers.zip', 4_500_000);

// ---------- Operations ----------
for (let i = 1; i <= 25; i++) {
  write('Operations', `Projects/P-${100 + i}/notes.txt`, `Project P-${100 + i} weekly notes.\nPart# ${100 + rand(800)}-${10 + rand(89)}-${1000 + rand(8999)} on back order.\n`);
}
write('Operations', 'IT/deploy.ps1', '# Deployment script (synthetic)\n$server = "app01"\n$password = "Winter2026!"\n');
write('Operations', 'IT/old-key.pem', '-----BEGIN RSA PRIVATE KEY-----\nNOT-A-REAL-KEY\n-----END RSA PRIVATE KEY-----\n');

mkdirSync(join(server, 'Public'), { recursive: true });
write('Public', 'Menus/cafeteria.pdf', pdf(['Cafeteria menu', 'Monday: soup and salad']));
filler('C$', 'Windows/system.ini', 512);

// A junction-style loop: the scanner must report it and not follow it.
try {
  symlinkSync(join(process.cwd(), server, 'Operations'), join(server, 'Operations', 'Projects', 'shortcut-to-ops'), 'junction');
} catch {
  console.log('(Could not create the loop link; on Windows this needs Developer Mode or an admin prompt. Skipped.)');
}
console.log(`Synthetic server written to ${server}`);

// ---------- a synthetic domain (NORTHWIND.TEST) and the file server's permissions ----------
// Read by the Development-only LabDirectoryReader and LabSecurityReader. Every account and SID is made up.
const DOMAIN = 'S-1-5-21-3623811015-3361044348-30300820';
const sid = (rid) => `${DOMAIN}-${rid}`;
const principals = [];
const add = (p) => (principals.push({ enabled: true, security: true, members: [], ...p }), p.sid);
const DOMAIN_ADMINS = add({ sid: sid(512), kind: 'Group', name: 'Domain Admins' });
const DOMAIN_USERS = add({ sid: sid(513), kind: 'Group', name: 'Domain Users' });
add({ sid: sid(514), kind: 'Group', name: 'Domain Guests' });

let rid = 1100;
const user = (name, department, title, extra = {}) =>
  add({ sid: sid(rid++), kind: 'User', name, displayName: name.replace('.', ' ').replace(/\b\w/g, (c) => c.toUpperCase()), department, title, mail: `${name}@northwind.test`, ...extra });
const hr = ['avery.okafor', 'jordan.lindqvist', 'riley.tanaka'].map((n) => user(n, 'HR', 'HR Generalist'));
const hrManager = user('morgan.moreau', 'HR', 'HR Manager');
const contractor = user('casey.silva', 'HR', 'Contractor');
const ap = ['quinn.novak', 'harper.haddad'].map((n) => user(n, 'Finance', 'AP Clerk'));
const finance = ['rowan.byrne', 'emerson.petrov'].map((n) => user(n, 'Finance', 'Accountant'));
const ops = ['sasha.mensah', 'avery.tanaka', 'jordan.silva', 'riley.novak', 'morgan.haddad', 'casey.byrne'].map((n) => user(n, 'Operations', 'Coordinator'));
const it = ['quinn.petrov', 'harper.mensah'].map((n) => user(n, 'IT', 'Systems Administrator'));
const admin = user('admin.rowan', 'IT', 'Domain administrator');
const leaver = user('emerson.okafor', 'Finance', 'Former Controller', { enabled: false });
const svc = user('svc-backup', 'IT', 'Service account');
const exec = user('sasha.lindqvist', 'Executive', 'Chief Executive');

// Reporting lines (the AD "manager" attribute): Data Scope suggests owners from them.
const reportsTo = (manager, ...people) => people.flat().forEach((s) => (principals.find((p) => p.sid === s).manager = manager));
reportsTo(exec, hrManager, finance[1], ops[0], admin);
reportsTo(hrManager, hr, contractor);
reportsTo(finance[1], finance[0]);
reportsTo(finance[0], ap);
reportsTo(ops[0], ops.slice(1));
reportsTo(admin, it, svc);

const group = (name, members, extra = {}) => add({ sid: sid(rid++), kind: 'Group', name, members, ...extra });
principals.find((p) => p.sid === DOMAIN_ADMINS).members.push(admin);
const GG_HR_STAFF = group('GG_HR_Staff', [...hr, hrManager, contractor]);
const GG_HR_MANAGERS = group('GG_HR_Managers', [hrManager, exec]);
const GG_FIN_AP = group('GG_Finance_AP', ap);
const GG_FIN_ALL = group('GG_Finance_All', [GG_FIN_AP, ...finance, leaver]);
const GG_OPS = group('GG_Operations', ops);
const GG_IT = group('GG_IT_Admins', it);
const DL_HR_READ = group('DL_HR_Read', [GG_HR_STAFF]);
const DL_HR_MODIFY = group('DL_HR_Modify', [GG_HR_MANAGERS]);
const DL_FIN_MODIFY = group('DL_Finance_Modify', [GG_FIN_ALL, exec]);
// Two groups that contain each other: a loop Data Scope must report and survive.
const PROJECTS_A = sid(rid++);
const PROJECTS_B = sid(rid++);
add({ sid: PROJECTS_A, kind: 'Group', name: 'GG_Projects_A', members: [PROJECTS_B, ops[0], ops[1]] });
add({ sid: PROJECTS_B, kind: 'Group', name: 'GG_Projects_B', members: [PROJECTS_A, it[0]] });
// A member from another forest that this domain cannot resolve: access through this group is uncertain.
const GG_PARTNERS = group('GG_Partners', [exec, 'S-1-5-21-9000-9000-9000-4001']);
group('Newsletter', [...hr, ...ops], { security: false });
add({ sid: sid(rid++), kind: 'Computer', name: 'NW-FS01$', security: false });
const ORPHAN = sid(1999); // a deleted account still named in a permission list

writeFileSync(join(root, 'northwind.test.directory.json'), JSON.stringify({ domain: 'northwind.test', domainSid: DOMAIN, principals }, null, 1));

const SYSTEM = 'S-1-5-18', ADMINS = 'S-1-5-32-544', USERS = 'S-1-5-32-545', EVERYONE = 'S-1-1-0', AUTH = 'S-1-5-11', CREATOR = 'S-1-3-0';
const base = [
  { sid: SYSTEM, rights: 'Full' },
  { sid: ADMINS, rights: 'Full' },
  { sid: CREATOR, rights: 'Full', applies: 'subfolders-and-files' },
];
const security = {
  localGroups: [
    { sid: ADMINS, name: 'BUILTIN\\Administrators', members: [{ sid: DOMAIN_ADMINS, kind: 'Group' }, { sid: 'S-1-5-21-1111-2222-3333-500', kind: 'User' }] },
    { sid: USERS, name: 'BUILTIN\\Users', members: [{ sid: DOMAIN_USERS, kind: 'Group' }, { sid: AUTH, kind: 'WellKnownGroup' }, { sid: 'S-1-5-4', kind: 'WellKnownGroup' }] },
    { sid: 'S-1-5-32-551', name: 'BUILTIN\\Backup Operators', members: [{ sid: svc, kind: 'User' }] },
  ],
  shares: {
    HR: [{ sid: AUTH, rights: 'Change' }, { sid: ADMINS, rights: 'Full' }],
    Finance: null, // share permissions the scan account may not read
    Operations: [{ sid: EVERYONE, rights: 'Full' }],
    Public: [{ sid: EVERYONE, rights: 'Read' }, { sid: GG_IT, rights: 'Full' }],
  },
  folders: {
    HR: { owner: ADMINS, aces: [...base, { sid: DL_HR_READ, rights: 'Read' }, { sid: DL_HR_MODIFY, rights: 'Modify' }] },
    'HR/Payroll': { owner: ADMINS, protected: true, aces: [...base, { sid: GG_HR_MANAGERS, rights: 'Modify' }, { sid: GG_FIN_AP, rights: 'Read' }, { sid: ORPHAN, rights: 'Read' }] },
    'HR/Employees': { aces: [{ sid: contractor, rights: 'Read', deny: true }] },
    // A mistake worth finding: medical paperwork opened to everyone.
    'HR/Benefits': { aces: [{ sid: EVERYONE, rights: 'Read' }] },
    Finance: { owner: ADMINS, aces: [...base, { sid: DL_FIN_MODIFY, rights: 'Modify' }, { sid: GG_IT, rights: 'Full' }, { sid: USERS, rights: 'Read' }] },
    'Finance/AR': { protected: true, aces: [...base, { sid: GG_FIN_AP, rights: 'Modify' }, { sid: GG_PARTNERS, rights: 'Read' }] },
    Operations: { owner: ADMINS, aces: [...base, { sid: GG_OPS, rights: 'Modify' }, { sid: DOMAIN_USERS, rights: 'Read' }] },
    'Operations/Projects': { aces: [{ sid: PROJECTS_A, rights: 'Modify' }] },
    Public: { owner: ADMINS, aces: [...base, { sid: EVERYONE, rights: 'Read' }, { sid: AUTH, rights: 'Modify', applies: 'subfolders-and-files' }] },
  },
  denied: ['Operations/IT'], // folder permissions the scan account may not read
};
writeFileSync(join(root, `${host}.security.json`), JSON.stringify(security, null, 1));
console.log(`Synthetic domain written to ${join(root, 'northwind.test.directory.json')} and permissions to ${join(root, `${host}.security.json`)}`);

// ---------- a week of synthetic file activity: the file server's Security log ----------
// Read by the Development-only LabSecurityEventSource, in the format `wevtutil qe Security /f:xml /e:Events` writes.
// Share folders are D:\Shares\<share>; the 5140 events teach Data Scope that mapping. Every event is made up.
const nameOf = Object.fromEntries(principals.map((p) => [p.sid, p.name]));
const xmlText = (v) => String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/'/g, '&apos;').replace(/"/g, '&quot;');
const events = [];
let record = 48_000;
const nowMs = Date.now();
const at = (daysAgo, hour, minute = 0) => {
  const d = new Date(nowMs - daysAgo * 864e5);
  d.setUTCHours(hour, minute, rand(60), rand(1000));
  return Math.min(d.getTime(), nowMs - 60_000);
};
const logons = {};
const logon = (who) => (logons[who] ??= `0x${(0x3e000 + Object.keys(logons).length * 0x1d3).toString(16)}`);
const event = (id, time, data, { failure = false, provider = 'Microsoft-Windows-Security-Auditing' } = {}) => {
  record += 1 + rand(40); // other events in the log sit between these
  const stamp = new Date(time).toISOString().replace(/\.(\d{3})Z$/, '.$10000Z');
  const keywords = failure ? '0x8010000000000000' : '0x8020000000000000';
  const body = Object.entries(data).map(([k, v]) => `<Data Name='${k}'>${xmlText(v)}</Data>`).join('');
  events.push({ time, xml: `<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='${provider}' Guid='{54849625-5478-4994-a5ba-3e3b0328c30d}'/><EventID>${id}</EventID><Version>1</Version><Level>0</Level><Task>12800</Task><Opcode>0</Opcode><Keywords>${keywords}</Keywords><TimeCreated SystemTime='${stamp}'/><EventRecordID>${record}</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='${200 + rand(4000)}'/><Channel>Security</Channel><Computer>${host}.northwind.test</Computer><Security/></System><EventData>${body}</EventData></Event>` });
};
const subject = (who) => ({ SubjectUserSid: who, SubjectUserName: nameOf[who] ?? 'unknown', SubjectDomainName: 'NORTHWIND', SubjectLogonId: logon(who) });
const local = (share, path = '') => `D:\\Shares\\${share}${path ? '\\' + path.replace(/\//g, '\\') : ''}`;
let handle = 0x400;
const access = (time, who, share, path, mask) => {
  handle += 4;
  event(4663, time, { ...subject(who), ObjectServer: 'Security', ObjectType: 'File', ObjectName: local(share, path), HandleId: `0x${handle.toString(16)}`, AccessList: '%%4416', AccessMask: `0x${mask.toString(16)}`, ProcessId: '0x4', ProcessName: '', ResourceAttributes: 'S:AI' });
  return handle;
};
const remove = (time, who, share, path) => {
  const h = access(time, who, share, path, 0x10000);
  event(4660, time + 5, { ...subject(who), ObjectServer: 'Security', HandleId: `0x${h.toString(16)}`, ProcessId: '0x4', ProcessName: '', TransactionId: '{00000000-0000-0000-0000-000000000000}' });
};
const connect = (time, who, share) =>
  event(5140, time, { ...subject(who), ObjectType: 'File', IpAddress: `10.20.0.${20 + rand(200)}`, IpPort: String(49152 + rand(16000)), ShareName: `\\\\*\\${share}`, ShareLocalPath: `\\??\\${local(share)}`, AccessMask: '0x1', AccessList: '%%4416' });
const check = (time, who, share, path, mask, failure = false) =>
  event(5145, time, { ...subject(who), ObjectType: 'File', IpAddress: `10.20.0.${20 + rand(200)}`, IpPort: String(49152 + rand(16000)), ShareName: `\\\\*\\${share}`, ShareLocalPath: `\\??\\${local(share)}`, RelativeTargetName: path.replace(/\//g, '\\'), AccessMask: `0x${mask.toString(16)}`, AccessList: '%%4416', AccessReason: '-' }, { failure });

for (let day = 6; day >= 0; day--) {
  for (const who of [...ap, ...finance]) {
    connect(at(day, 13), who, 'Finance');
    for (let i = 0; i < 3 + rand(4); i++) access(at(day, 13 + rand(8), rand(60)), who, 'Finance', `AP/Invoices/2025/INV-${4001 + rand(120)}.pdf`, 0x1);
  }
  access(at(day, 15, 30), finance[0], 'Finance', 'Budget/FY26-budget.xlsx', 0x1);
  if (day % 2 === 0) access(at(day, 16, 10), finance[0], 'Finance', 'Budget/FY26-budget.xlsx', 0x2);
  for (const who of hr) {
    connect(at(day, 14), who, 'HR');
    access(at(day, 14 + rand(6), rand(60)), who, 'HR', `Employees/E${1001 + rand(40)}/offer-letter.docx`, 0x1);
  }
  connect(at(day, 8, 55), ops[0], 'Operations');
  access(at(day, 9, rand(60)), ops[0], 'Operations', 'Projects', 0x1);
}
// A contractor tries the payroll folder and is refused, at the share and at the folder.
check(at(2, 18, 12), contractor, 'HR', 'Payroll/2025/09-sep/payroll-register.xlsx', 0x1, true);
event(4656, at(2, 18, 12), { ...subject(contractor), ObjectServer: 'Security', ObjectType: 'File', ObjectName: local('HR', 'Payroll/2025/09-sep/payroll-register.xlsx'), HandleId: '0x0', TransactionId: '{00000000-0000-0000-0000-000000000000}', AccessList: '%%4416', AccessReason: '-', AccessMask: '0x1', PrivilegeList: '-', RestrictedSidCount: '0', ProcessId: '0x4', ProcessName: '' }, { failure: true });
// An administrator opens the benefits folder to everyone (the mistake the permission rules find).
event(4670, at(3, 11, 5), { ...subject(admin), ObjectServer: 'Security', ObjectType: 'File', ObjectName: local('HR', 'Benefits'), HandleId: '0x9c4', OldSd: `D:AI(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)`, NewSd: `D:AI(A;OICI;0x1200a9;;;WD)(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)`, ProcessId: '0x1a2c', ProcessName: 'C:\\Windows\\explorer.exe' });
// Someone in Operations clears out old project folders this morning: 60 deletes in a few minutes, enough for the
// mass delete alert (50 in 10 minutes by default). The contractor, permission change and cleared log raise the others.
connect(at(0, 8, 39), ops[2], 'Operations');
for (let i = 101; i <= 160; i++) remove(at(0, 8, 40 + (i % 6)), ops[2], 'Operations', `Projects/P-${i}/notes.txt`);
// A backup agent reading outside any share, which Data Scope cannot match to a share.
access(at(1, 2, 30), svc, 'C$', '', 0x1);
events.at(-1).xml = events.at(-1).xml.replace(`<Data Name='ObjectName'>D:\\Shares\\C$</Data>`, `<Data Name='ObjectName'>C:\\ProgramData\\BackupAgent\\catalog.db</Data>`);
// Someone clears the Security log: events before it may be missing.
record += 3;
events.push({ time: at(4, 22, 0), xml: `<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Eventlog' Guid='{fc65ddd8-d6ef-4962-83d5-6e5cfe9ce148}'/><EventID>1102</EventID><Version>0</Version><Level>4</Level><Task>104</Task><Opcode>0</Opcode><Keywords>0x4020000000000000</Keywords><TimeCreated SystemTime='${new Date(at(4, 22, 0)).toISOString().replace(/\.(\d{3})Z$/, '.$10000Z')}'/><EventRecordID>${record}</EventRecordID><Correlation/><Execution ProcessID='1060' ThreadID='4520'/><Channel>Security</Channel><Computer>${host}.northwind.test</Computer><Security/></System><UserData><LogFileCleared xmlns='http://manifests.microsoft.com/win/2004/08/windows/eventlog'><SubjectUserSid>${admin}</SubjectUserSid><SubjectUserName>admin.rowan</SubjectUserName><SubjectDomainName>NORTHWIND</SubjectDomainName><SubjectLogonId>${logon(admin)}</SubjectLogonId></LogFileCleared></UserData></Event>` });

// Windows numbers records in the order it writes them: renumber by time.
events.sort((a, b) => a.time - b.time);
let renumber = 48_000;
const log = events.map((e) => e.xml.replace(/<EventRecordID>\d+<\/EventRecordID>/, () => `<EventRecordID>${(renumber += 1 + rand(30))}</EventRecordID>`));
writeFileSync(join(root, `${host}.security-events.xml`), `<Events>\n${log.join('\n')}\n</Events>\n`);
console.log(`Synthetic Security log (${log.length} file events) written to ${join(root, `${host}.security-events.xml`)}`);
