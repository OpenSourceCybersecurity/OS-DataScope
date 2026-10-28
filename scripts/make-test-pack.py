#!/usr/bin/env python3
"""Makes the detection test pack (D138): synthetic files with known contents, in every format Data Scope reads,
and expected.csv, which says what Data Scope should report for each one. Copy the pack's files to a lab share,
scan it, and Test-DataScopeTestPack.ps1 compares the results with expected.csv. CI reads the same files
(TestPackTests), so a change that stops Data Scope finding something fails the build.

Every name, number and address here is made up. 512-48-33xx are the test SSNs used across the test suite (each file
has its own last four digits, so the masked evidence shows which file a match came from). Card numbers are the
issuers' published test numbers, IBANs are the published examples, and routing numbers are public bank routing
numbers with made-up accounts.

Needs LibreOffice (Writer, Calc, Impress), py7zr (python3 -m pip install py7zr) and zip.

    python3 scripts/make-test-pack.py
    DATASCOPE_WRITE_TEST_PACK=1 dotnet test tests/DataScope.Scanner.Tests --filter TestPackTests

The second command writes the Outlook files and protected Office files, which only the C# test builders can make.
Writes tests/test-pack/. The files are committed; run this again only to change them.
"""
import base64
import csv
import gzip
import io
import os
import shutil
import subprocess
import tempfile
import zipfile

import py7zr

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, '..'))
PACK = os.path.join(REPO, 'tests', 'test-pack')
FILES = os.path.join(PACK, 'files')
OCR = os.path.join(REPO, 'tests', 'DataScope.Scanner.Tests', 'Content', 'Fixtures', 'ocr')
LEGACY = os.path.join(REPO, 'tests', 'DataScope.Scanner.Tests', 'Content', 'Fixtures', 'legacy')
work = tempfile.mkdtemp(prefix='ds-pack-')
shutil.rmtree(FILES, ignore_errors=True)

# Files the C# builders write (TestPackTests.WriteBuiltFiles); listed here so expected.csv covers them.
BUILT_BY_TESTS = set()
cases = []


def expect(path, label, read='Scanned', find='', not_find='', tests=''):
    """One row of expected.csv. find: categories that must be found at Medium or High (Category or Category@High).
    not_find: categories that must not be found at Medium or High ('any' for none at all)."""
    cases.append({'File': path, 'Label': label, 'Read': read, 'MustFind': find, 'MustNotFind': not_find, 'Tests': tests})


def out(path):
    full = os.path.join(FILES, *path.split('/'))
    os.makedirs(os.path.dirname(full), exist_ok=True)
    return full


def write(path, data, encoding='utf-8'):
    with open(out(path), 'wb') as f:
        f.write(data if isinstance(data, bytes) else data.encode(encoding))


def convert(source_name, source_text, fmt, path):
    """Saves source as fmt with LibreOffice, in a profile of its own so no user settings apply."""
    src = os.path.join(work, source_name)
    with open(src, 'w', encoding='utf-8') as f:
        f.write(source_text)
    args = ['soffice', '-env:UserInstallation=file://' + os.path.join(work, 'profile'), '--headless']
    if source_name.endswith('.csv'):
        args += ['--infilter=CSV:44,34,76,1']
    subprocess.run(args + ['--convert-to', fmt, '--outdir', work, src], check=True, capture_output=True)
    built = os.path.splitext(src)[0] + '.' + fmt.split(':')[0]
    shutil.move(built, out(path))


def html(title, *paragraphs, table=None):
    rows = ''
    if table:
        rows = '<table border="1">' + ''.join('<tr>' + ''.join(f'<td>{c}</td>' for c in r) + '</tr>' for r in table) + '</table>'
    return (f'<html><head><meta charset="utf-8"><title>{title}</title></head><body><h1>{title}</h1>'
            + ''.join(f'<p>{p}</p>' for p in paragraphs) + rows + '</body></html>')


def presentation(title, lines, note=None):
    notes = ''
    if note:
        notes = (f'<presentation:notes><draw:frame svg:x="2cm" svg:y="2cm" svg:width="16cm" svg:height="10cm"><draw:text-box>'
                 f'<text:p>{note}</text:p></draw:text-box></draw:frame></presentation:notes>')
    body = ''.join(f'<text:p>{l}</text:p>' for l in lines)
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
 xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
 xmlns:presentation="urn:oasis:names:tc:opendocument:xmlns:presentation:1.0" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0"
 office:version="1.3" office:mimetype="application/vnd.oasis.opendocument.presentation">
 <office:body><office:presentation>
  <draw:page draw:name="Slide1">
   <draw:frame svg:x="2cm" svg:y="2cm" svg:width="20cm" svg:height="3cm"><draw:text-box><text:p>{title}</text:p></draw:text-box></draw:frame>
   <draw:frame svg:x="2cm" svg:y="6cm" svg:width="20cm" svg:height="6cm"><draw:text-box>{body}</draw:text-box></draw:frame>
   {notes}
  </draw:page>
 </office:presentation></office:body>
</office:document>"""


def zip_of(entries, path=None, compress=zipfile.ZIP_DEFLATED):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w', compress) as z:
        for name, data in entries:
            info = zipfile.ZipInfo(name, date_time=(2026, 10, 1, 9, 0, 0))
            info.compress_type = compress
            z.writestr(info, data)
    if path:
        write(path, buffer.getvalue())
    return buffer.getvalue()


def copy(source, path):
    shutil.copyfile(source, out(path))


# ---------------------------------------------------------------------------------------------------------------
# 1. Each kind of data Data Scope looks for, in a plain text file, so a miss here is the detector, not the reader.
# ---------------------------------------------------------------------------------------------------------------
K = '1-Each-kind-of-data'
write(f'{K}/ssn-with-label.txt', 'Payroll correction for Jordan Sample.\nSocial Security Number: 512-48-3301\nApproved by HR.\n')
expect(f'{K}/ssn-with-label.txt', 'Restricted', find='US SSN@High', tests='A US Social Security number next to its label')

write(f'{K}/ssn-column.csv', 'Employee,SSN,Start date\nAvery Example,512483302,2024-03-01\nJordan Sample,512-48-3303,2023-11-15\n')
expect(f'{K}/ssn-column.csv', 'Restricted', find='US SSN@High', tests='SSNs under an SSN column header, with and without dashes')

write(f'{K}/itin.txt', 'Contractor tax form on file.\nITIN: 912-73-4821\n')
expect(f'{K}/itin.txt', 'Restricted', find='US ITIN@High', not_find='US SSN', tests='A US taxpayer ID (ITIN), reported as an ITIN and not as an SSN')

write(f'{K}/card-numbers.txt', 'Card payments to refund:\nVisa 4111 1111 1111 1111 exp 04/28\nMastercard 5555-5555-5555-4444\n'
      'Amex 3782 822463 10005\nDiscover 6011111111111117\n')
expect(f'{K}/card-numbers.txt', 'Restricted', find='Payment card@High', tests='Payment card numbers from four issuers, spaced, dashed and run together')

write(f'{K}/bank-routing-and-account.txt', 'Direct deposit form\nRouting number: 011000015\nAccount number: 4400918273\n')
expect(f'{K}/bank-routing-and-account.txt', 'Restricted', find='Bank account@High', tests='A US bank routing number with an account number next to it')

write(f'{K}/iban.txt', 'Please pay the supplier by bank transfer to IBAN GB82 WEST 1234 5698 7654 32 before Friday.\n')
expect(f'{K}/iban.txt', 'Restricted', find='Bank account@High', tests='An international bank account number (IBAN) next to a bank word: High')

write(f'{K}/iban-on-its-own.txt', 'Reference DE89 3704 0044 0532 0130 00 from the old system.\n')
expect(f'{K}/iban-on-its-own.txt', 'Confidential', find='Bank account', tests='An IBAN with no bank word near it: Medium')

write(f'{K}/private-key.pem', '-----BEGIN RSA PRIVATE KEY-----\n'
      'MIIEowIBAAKCAQEAsyntheticTESTkeyNOTrealQ0FBQUFBQUFBQUFBQUFBQUFBQUFB\n'
      'QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFB\n'
      '-----END RSA PRIVATE KEY-----\n')
expect(f'{K}/private-key.pem', 'Restricted', find='Credentials@High', tests='A private key block (made up, not a working key)')

write(f'{K}/app-settings.config', '<configuration>\n  <connectionStrings>\n    <add name="Payroll" connectionString="Server=sql01;Database=Payroll;'
      'User Id=payroll_app;Password=Pl4ceh0lder-Synthetic!;" />\n  </connectionStrings>\n</configuration>\n')
expect(f'{K}/app-settings.config', 'Restricted', find='Credentials', tests='A password in a database connection string')

write(f'{K}/deploy-notes.txt', 'Deployment notes\nadmin password: Winter2026-Synthetic!\nAWS key AKIAIOSFODNN7EXAMPLE for the backup bucket (the example key from AWS documentation)\n')
expect(f'{K}/deploy-notes.txt', 'Restricted', find='Credentials@High', tests='A password and a cloud access key written in notes')

write(f'{K}/health-record.txt', 'Patient: Riley Placeholder  MRN 4471-2290\nDiagnosis: E11.9 type 2 diabetes mellitus. '
      'Prescribed metformin 500 mg. Follow-up A1C in three months.\n')
expect(f'{K}/health-record.txt', 'Restricted', find='Health information', tests='Health information (diagnosis code, condition, medication) about a named patient')

write(f'{K}/date-of-birth.txt', 'Emergency contact form\nName: Casey Demo\nDate of birth: 03/14/1985\n')
expect(f'{K}/date-of-birth.txt', 'Confidential', find='Date of birth', tests='A date of birth next to its label')

write(f'{K}/drivers-licence.txt', "Rental car booking for Morgan Sample. Driver's license: D4821937 (CA).\n")
expect(f'{K}/drivers-licence.txt', 'Confidential', find="Driver's licence", tests="A driver's licence number next to its label")

write(f'{K}/drivers-licence-column.csv', 'Name,DL Number,State\nCasey Byrne,D4821937,CA\nDana Shah,S7046213,WA\nEli Ward,W8301927,NY\n')
expect(f'{K}/drivers-licence-column.csv', 'Restricted', find="Driver's licence@High", tests="Driver's licence numbers in a licence column")

write(f'{K}/passport.txt', 'Travel request: Lisbon offsite for Eli Ward. Passport no: 548120934, returning Friday.\n')
expect(f'{K}/passport.txt', 'Confidential', find='Passport', tests='A passport number next to its label')

write(f'{K}/passport-column.csv', 'Traveller,Passport number,Nationality\nEli Ward,548120934,US\nDana Shah,X7319462,GB\nCasey Byrne,620417385,US\n')
expect(f'{K}/passport-column.csv', 'Restricted', find='Passport@High', tests='Passport numbers in a passport column')

write(f'{K}/mailing-labels.txt', 'Holiday card list\n\nAvery Example\n4021 N Elm Ave, Apt 4\nSpringfield, IL 62704\n\n'
      'Jordan Sample\n77 Harbor Road\nPortland, ME 04101\n')
expect(f'{K}/mailing-labels.txt', 'Confidential', find='Personal details', tests="People's names above their home addresses")

rows = [('First name', 'Last name', 'Home address', 'Date of birth'),
        ('Avery', 'Example', '12 Oak St', '04/12/1986'), ('Jordan', 'Sample', '77 Harbor Road', '09/30/1979'),
        ('Casey', 'Demo', '"4021 N Elm Ave, Apt 4"', '01/02/1990'), ('Riley', 'Placeholder', '9 Birch Lane', '12/11/1968'),
        ('Morgan', 'Sample', '310 Pine Court', '07/07/1995'), ('Dana', 'Shah', '58 Cedar Blvd', '02/28/1983')]
write(f'{K}/customer-list.csv', '\n'.join(','.join(r) for r in rows) + '\n')
expect(f'{K}/customer-list.csv', 'Confidential', find='Personal details@High',
       tests='A table of six people with home addresses and dates of birth (High, but names and addresses alone are never Restricted)')

write(f'{K}/patient-intake.txt', 'Patient intake (synthetic)\nName: Riley Placeholder\nDOB: 12/11/1968\nSSN: 512-48-3304\n'
      'Reason for visit: hypertension (I10), taking lisinopril.\nCard on file: 4012 8888 8888 1881\n')
expect(f'{K}/patient-intake.txt', 'Restricted', find='US SSN@High;Date of birth;Payment card@High',
       tests='Several kinds in one file: each is reported. Known gap: health information in plain words (hypertension, '
             'code I10 without a decimal, a medicine name) is not recognised; see DETECTION-LIMITATIONS.md')

lines = ['Employee ID,SSN,Department'] + [f'E{1000 + i},512-48-{3400 + i},Operations' for i in range(60)]
write(f'{K}/ssn-sixty-rows.csv', '\n'.join(lines) + '\n')
expect(f'{K}/ssn-sixty-rows.csv', 'Restricted', find='US SSN@High', tests='Sixty SSNs in one file: every one is counted')

# ---------------------------------------------------------------------------------------------------------------
# 2. Look-alikes: numbers and words that resemble sensitive data but are not. These must not be labelled
#    Confidential or Restricted, except the one known false positive, which is labelled as the docs say.
# ---------------------------------------------------------------------------------------------------------------
L = '2-Look-alikes'
write(f'{L}/invalid-ssns.txt', 'These are not valid SSNs: 000-12-3456, 666-12-3456, 512-00-3456, 512-48-0000.\n'
      'SSN field left blank on purpose.\n')
expect(f'{L}/invalid-ssns.txt', 'Internal', not_find='any', tests='Numbers shaped like SSNs that the SSA never issues')

write(f'{L}/bad-card-checksums.txt', 'Order references: 4111 1111 1111 1112, 5555 5555 5555 4445, 1234 5678 9012 3456.\n')
expect(f'{L}/bad-card-checksums.txt', 'Internal', not_find='any', tests='16-digit numbers that fail the card checksum')

write(f'{L}/phone-numbers.txt', 'Front desk (555) 201-4477, fax 555-201-4478, toll free 1-800-555-0199.\n')
expect(f'{L}/phone-numbers.txt', 'Internal', not_find='any', tests='Phone and fax numbers')

write(f'{L}/dates-without-birth.txt', 'Project kickoff 03/14/1985 was a long time ago. Invoice dated 04/12/2026, due 05/12/2026.\n')
expect(f'{L}/dates-without-birth.txt', 'Internal', not_find='any', tests='Dates with no birth label')

write(f'{L}/iban-bad-checksum.txt', 'Old account reference GB00 WEST 1234 5698 7654 32 (closed).\n')
expect(f'{L}/iban-bad-checksum.txt', 'Internal', not_find='Bank account', tests='An IBAN-shaped value with wrong check digits')

write(f'{L}/password-placeholders.config', '# Fill these in on the server\npassword=<your password here>\npwd: ********\n'
      'Password=CHANGEME\n')
expect(f'{L}/password-placeholders.config', 'Internal', not_find='Credentials', tests='Password settings with placeholders, not real values')

write(f'{L}/wellness-newsletter.txt', 'Wellness newsletter: flu shots are available at the clinic every October. Stay hydrated!\n')
expect(f'{L}/wellness-newsletter.txt', 'Internal', not_find='Health information', tests='Health words with no patient')

write(f'{L}/business-addresses.txt', 'Suppliers\n\nContoso Supplies LLC\n100 Industrial Way\nDayton, OH 45402\n\n'
      'Fabrikam Clinic\n22 Market Street\nAustin, TX 78701\n')
expect(f'{L}/business-addresses.txt', 'Internal', not_find='Personal details', tests='Business names above their addresses')

write(f'{L}/passport-reminder.txt', 'Reminder: bring your passport to the Lisbon offsite. Flight number 4821937.\n')
expect(f'{L}/passport-reminder.txt', 'Internal', not_find='Passport', tests='The word passport with no passport number')

write(f'{L}/order-numbers.csv', 'Order,Quantity\n512483307,4\n498112045,10\n')
expect(f'{L}/order-numbers.csv', 'Internal', not_find='any', tests='Nine-digit order numbers with no SSN header')

write(f'{L}/routing-number-only.txt', 'Our bank: First Example Bank, routing 011000015.\n')
expect(f'{L}/routing-number-only.txt', 'Internal', not_find='Bank account', tests='A bank routing number with no account number (a possible match at Low only)')

write(f'{L}/part-numbers.txt', 'Part# 512-48-3305 on back order.\n')
expect(f'{L}/part-numbers.txt', 'Confidential', find='US SSN',
       tests='Known false positive: a part number formatted exactly like an SSN is reported at Medium (see DETECTION-LIMITATIONS.md)')

# ---------------------------------------------------------------------------------------------------------------
# 3. File formats: the same kind of value in every format Data Scope reads. A miss here is the reader.
# ---------------------------------------------------------------------------------------------------------------
F = '3-File-formats'
serial = iter(range(3310, 3400))


def ssn_line():
    return f'Social Security Number: 512-48-{next(serial)}'


def text_format(name, make, tests):
    write(f'{F}/{name}', make(ssn_line()))
    expect(f'{F}/{name}', 'Restricted', find='US SSN@High', tests=tests)


text_format('plain.txt', lambda s: f'New hire note\n{s}\n', 'Plain text')
text_format('unicode-utf16.txt', lambda s: ('\ufeff' + f'New hire note (saved as Unicode by Notepad)\r\n{s}\r\n').encode('utf-16-le'),
            'Text saved as UTF-16 (Notepad "Unicode")')
text_format('utf8-bom.txt', lambda s: ('\ufeff' + f'Résumé note for Zoë Example\n{s}\n').encode('utf-8'), 'UTF-8 text with a byte order mark and accents')
text_format('latin1.txt', lambda s: f'Résumé note for Zoë Example\n{s}\n'.encode('latin-1'), 'Western European (Latin-1) text')
text_format('table.csv', lambda s: f'Name,Note\nAvery Example,"{s}"\n', 'CSV')
text_format('table.tsv', lambda s: f'Name\tNote\nAvery Example\t{s}\n', 'Tab-separated')
text_format('export.json', lambda s: '{"employee": "Avery Example", "note": "' + s + '"}\n', 'JSON')
text_format('export.xml', lambda s: f'<?xml version="1.0"?>\n<employee><name>Avery Example</name><note>{s}</note></employee>\n', 'XML')
text_format('export.yaml', lambda s: f'employee: Avery Example\nnote: "{s}"\n', 'YAML')
text_format('page.html', lambda s: f'<html><body><h1>Employee</h1><p>Avery Example</p><p>{s}</p></body></html>\n', 'Web page (HTML)')
text_format('notes.md', lambda s: f'# Employee\n\n- Avery Example\n- {s}\n', 'Markdown')
text_format('server.log', lambda s: f'2026-10-01 09:00:01 INFO import started\n2026-10-01 09:00:02 WARN row 7: {s}\n', 'Log file')
text_format('query.sql', lambda s: f"-- {s}\nSELECT name FROM employees WHERE id = 7;\n", 'SQL script (read as text, never run)')
text_format('cleanup.ps1', lambda s: f"# {s}\nWrite-Output 'never run by Data Scope'\n", 'PowerShell script (read as text, never run)')
text_format('letter.rtf', lambda s: '{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Times New Roman;}}\\f0\\fs24 New hire letter\\par ' + s + '\\par}\n', 'Rich text (RTF)')

# An email with the value in the body and a card number in an attached text file.
s = ssn_line()
attachment = 'Card on file: 4111 1111 1111 1111\r\n'
eml = ('From: hr@example.test\r\nTo: payroll@example.test\r\nSubject: New hire paperwork\r\nDate: Thu, 1 Oct 2026 09:00:00 +0000\r\n'
       'MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary="b1"\r\n\r\n--b1\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n'
       f'Please set up payroll.\r\n{s}\r\n--b1\r\nContent-Type: text/plain; name="card.txt"\r\nContent-Disposition: attachment; filename="card.txt"\r\n'
       f'Content-Transfer-Encoding: base64\r\n\r\n{base64.b64encode(attachment.encode()).decode()}\r\n--b1--\r\n')
write(f'{F}/email.eml', eml)
expect(f'{F}/email.eml', 'Restricted', find='US SSN@High;Payment card@High', tests='Email (.eml): the body and an attachment')

s = ssn_line()
convert('word.html', html('New hire form', 'Employee: Avery Example', s), 'docx:MS Word 2007 XML', f'{F}/word.docx')
expect(f'{F}/word.docx', 'Restricted', find='US SSN@High', tests='Word (.docx) saved by an office suite')
s = ssn_line()
convert('word97.html', html('New hire form', 'Employee: Avery Example', s), 'doc:MS Word 97', f'{F}/word-97-2003.doc')
expect(f'{F}/word-97-2003.doc', 'Restricted', find='US SSN@High', tests='Word 97-2003 (.doc)')
s = ssn_line()
convert('writer.html', html('New hire form', 'Employee: Avery Example', s), 'odt', f'{F}/opendocument.odt')
expect(f'{F}/opendocument.odt', 'Restricted', find='US SSN@High', tests='OpenDocument text (.odt), read by its contents')
s = ssn_line()
convert('richtext.html', html('New hire form', 'Employee: Avery Example', s), 'rtf:Rich Text Format', f'{F}/word-saved-as.rtf')
expect(f'{F}/word-saved-as.rtf', 'Restricted', find='US SSN@High', tests='Rich text saved by an office suite')
s = ssn_line()
convert('pdf.html', html('New hire form', 'Employee: Avery Example', s), 'pdf:writer_pdf_Export', f'{F}/document.pdf')
expect(f'{F}/document.pdf', 'Restricted', find='US SSN@High', tests='PDF with a text layer')

sheet = f'Employee,SSN,Card\nAvery Example,512-48-{next(serial)},4111111111111111\nJordan Sample,512-48-{next(serial)},\n'
convert('sheet.csv', sheet, 'xlsx:Calc MS Excel 2007 XML', f'{F}/spreadsheet.xlsx')
expect(f'{F}/spreadsheet.xlsx', 'Restricted', find='US SSN@High;Payment card@High', tests='Excel (.xlsx)')
sheet = f'Employee,SSN\nAvery Example,512-48-{next(serial)}\n'
convert('sheet97.csv', sheet, 'xls:MS Excel 97', f'{F}/spreadsheet-97-2003.xls')
expect(f'{F}/spreadsheet-97-2003.xls', 'Restricted', find='US SSN@High', tests='Excel 97-2003 (.xls)')
sheet = f'Employee,SSN\nAvery Example,512-48-{next(serial)}\n'
convert('calc.csv', sheet, 'ods', f'{F}/opendocument.ods')
expect(f'{F}/opendocument.ods', 'Restricted', find='US SSN@High', tests='OpenDocument spreadsheet (.ods), read by its contents')

s = ssn_line()
convert('slides.fodp', presentation('Benefits review', ['Case for Casey Demo', s], note='Card on file 5105 1051 0510 5100'),
        'pptx:Impress MS PowerPoint 2007 XML', f'{F}/slides.pptx')
expect(f'{F}/slides.pptx', 'Restricted', find='US SSN@High;Payment card@High', tests='PowerPoint (.pptx): a slide and the speaker notes')
s = ssn_line()
convert('slides97.fodp', presentation('Benefits review', ['Case for Casey Demo', s]), 'ppt:MS PowerPoint 97', f'{F}/slides-97-2003.ppt')
expect(f'{F}/slides-97-2003.ppt', 'Restricted', find='US SSN@High', tests='PowerPoint 97-2003 (.ppt)')

# Archives. Each holds a text file with the value and a clean one.
s = ssn_line()
zip_of([('hr/new-hire.txt', f'New hire Avery Example\n{s}\n'), ('hr/readme.txt', 'Nothing sensitive here.\n')], f'{F}/archive.zip')
expect(f'{F}/archive.zip', 'Restricted', find='US SSN@High', tests='Zip archive')
s = ssn_line()
inner = zip_of([('new-hire.txt', f'New hire Avery Example\n{s}\n')])
zip_of([('2025/inner.zip', inner), ('readme.txt', 'Archive of archives.\n')], f'{F}/zip-inside-zip.zip')
expect(f'{F}/zip-inside-zip.zip', 'Restricted', find='US SSN@High', tests='A zip inside a zip')
s = ssn_line()
docx_bytes = open(out(f'{F}/word.docx'), 'rb').read()
zip_of([('forms/new-hire.txt', f'{s}\n'), ('forms/form.docx', docx_bytes)], f'{F}/office-files-in.zip')
expect(f'{F}/office-files-in.zip', 'Restricted', find='US SSN@High', tests='Office documents inside a zip')
s = ssn_line()
with py7zr.SevenZipFile(out(f'{F}/archive.7z'), 'w') as z:
    z.writestr(f'New hire Avery Example\n{s}\n'.encode(), 'hr/new-hire.txt')
    z.writestr(b'Nothing sensitive here.\n', 'hr/readme.txt')
expect(f'{F}/archive.7z', 'Restricted', find='US SSN@High', tests='7-Zip archive')
copy(os.path.join(LEGACY, 'records.rar'), f'{F}/archive.rar')
expect(f'{F}/archive.rar', 'Restricted', read='Partial', find='US SSN@High', tests='RAR archive; the program inside is not opened, so it is Partial')

# Outlook files, written by the C# test builders (TestPackTests).
for name, tests, find in [
        ('message.msg', 'Outlook message (.msg) with an attached file', 'US SSN@High;Payment card@High'),
        ('mailbox.pst', 'Outlook data file (.pst): messages, an attachment and an attached message', 'US SSN@High;Payment card@High'),
        ('offline-mailbox.ost', 'Outlook offline mailbox (.ost, compressed as Outlook 2013 and later save it)', 'US SSN@High;Payment card@High')]:
    BUILT_BY_TESTS.add(f'{F}/{name}')
    expect(f'{F}/{name}', 'Restricted', find=find, tests=tests)

# ---------------------------------------------------------------------------------------------------------------
# 4. Scans and pictures, read by text recognition (OCR). Made by scripts/make-ocr-fixtures.py.
# ---------------------------------------------------------------------------------------------------------------
P = '4-Scans-and-pictures'
for source, name, tests in [
        ('form-scan.png', 'scanned-form.png', 'A scanned form (PNG)'),
        ('form-scan.jpg', 'scanned-form.jpg', 'A scanned form (JPEG)'),
        ('form-sideways.png', 'scanned-sideways.png', 'A form scanned sideways'),
        ('form-upside-down.png', 'scanned-upside-down.png', 'A form scanned upside down'),
        ('form-tilted.jpg', 'photo-of-form-tilted.jpg', 'A photo of a form, tilted'),
        ('screenshot.png', 'screenshot.png', 'A screenshot with small text'),
        ('fax.tif', 'fax-two-pages.tif', 'A two-page fax (TIFF); the SSN is on page 2'),
        ('badge.gif', 'badge.gif', 'A GIF picture'),
        ('scanned.pdf', 'scanned-no-text-layer.pdf', 'A scanned PDF with no text layer'),
        ('scanned-fax.pdf', 'faxed-pdf.pdf', 'A faxed (1-bit) PDF page'),
        ('scan-pasted.docx', 'scan-pasted-in-word.docx', 'A scan pasted into a Word document'),
        ('scan-pasted.xlsx', 'scan-pasted-in-excel.xlsx', 'A scan pasted into an Excel workbook'),
        ('scan-pasted.pptx', 'scan-pasted-in-powerpoint.pptx', 'A scan pasted into a PowerPoint slide')]:
    copy(os.path.join(OCR, source), f'{P}/{name}')
    expect(f'{P}/{name}', 'Restricted', find='US SSN@High', tests=tests)
copy(os.path.join(OCR, 'no-text.png'), f'{P}/picture-no-text.png')
expect(f'{P}/picture-no-text.png', 'Internal', not_find='any', tests='A picture with no text')

# ---------------------------------------------------------------------------------------------------------------
# 5. Wrong or no extension: Data Scope reads files by what they contain, not by their names.
# ---------------------------------------------------------------------------------------------------------------
N = '5-Wrong-or-no-extension'
s = ssn_line()
convert('renamed.csv', f'Employee,SSN\nAvery Example,512-48-{next(serial)}\n', 'xlsx:Calc MS Excel 2007 XML', f'{N}/spreadsheet-named-as-photo.jpg')
expect(f'{N}/spreadsheet-named-as-photo.jpg', 'Restricted', find='US SSN@High', tests='An Excel workbook renamed to .jpg')
s = ssn_line()
convert('noext.html', html('New hire form', s), 'docx:MS Word 2007 XML', f'{N}/word-document-no-extension')
expect(f'{N}/word-document-no-extension', 'Restricted', find='US SSN@High', tests='A Word document with no extension')
s = ssn_line()
convert('tmp.html', html('New hire form', s), 'pdf:writer_pdf_Export', f'{N}/pdf-named.tmp')
expect(f'{N}/pdf-named.tmp', 'Restricted', find='US SSN@High', tests='A PDF with a .tmp name')
s = ssn_line()
write(f'{N}/export.dat', f'Employee,Note\nAvery Example,{s}\n')
expect(f'{N}/export.dat', 'Restricted', find='US SSN@High', tests='Text in a file with an unknown extension')
s = ssn_line()
write(f'{N}/payroll.csv.bak', f'Employee,Note\nAvery Example,{s}\n')
expect(f'{N}/payroll.csv.bak', 'Restricted', find='US SSN@High', tests='A backup copy (.bak)')

# ---------------------------------------------------------------------------------------------------------------
# 6. Files Data Scope can't or won't read. Each must be reported as not read, with a reason, and never as clean.
# ---------------------------------------------------------------------------------------------------------------
R = '6-Reported-as-not-read'
subprocess.run(['zip', '-q', '-j', '-P', 'synthetic-test-only', os.path.join(work, 'locked.zip'),
                out(f'{F}/plain.txt')], check=True)
shutil.move(os.path.join(work, 'locked.zip'), out(f'{R}/password-protected.zip'))
expect(f'{R}/password-protected.zip', 'Unknown', read='Encrypted', tests='A password-protected zip')
copy(os.path.join(LEGACY, 'locked-files.7z'), f'{R}/password-protected.7z')
expect(f'{R}/password-protected.7z', 'Unknown', read='Unreadable',
       tests='A 7-Zip archive with encrypted files and a program: nothing could be read, and the note counts both reasons')
copy(os.path.join(LEGACY, 'locked-names.7z'), f'{R}/password-protected-names.7z')
expect(f'{R}/password-protected-names.7z', 'Unknown', read='Encrypted', tests='A 7-Zip archive whose file names are encrypted too')
for name, tests in [('password-protected.docx', 'A password-protected Word document'),
                    ('password-protected.xlsx', 'A password-protected Excel workbook')]:
    BUILT_BY_TESTS.add(f'{R}/{name}')
    expect(f'{R}/{name}', 'Unknown', read='Encrypted', tests=tests)

docx_bytes = open(out(f'{F}/word.docx'), 'rb').read()
write(f'{R}/damaged.docx', docx_bytes[:len(docx_bytes) // 3])
expect(f'{R}/damaged.docx', 'Unknown', read='Unreadable', tests='A Word document cut short (as after a failed copy)')
pdf_bytes = open(out(f'{F}/document.pdf'), 'rb').read()
write(f'{R}/damaged.pdf', b'%PDF-1.4\n' + bytes((b * 7 + 3) & 0xFF for b in pdf_bytes[9:400]))
expect(f'{R}/damaged.pdf', 'Unknown', read='Unreadable', tests='A damaged PDF')

# A small zip that unpacks to 1 GB of text: only the start is read, within the memory and time limits.
bomb = io.BytesIO()
with zipfile.ZipFile(bomb, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    info = zipfile.ZipInfo('zeros.txt', date_time=(2026, 10, 1, 9, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    with z.open(info, 'w', force_zip64=True) as f:
        chunk = b'A' * (1 << 20)
        for _ in range(1024):
            f.write(chunk)
write(f'{R}/zip-bomb.zip', bomb.getvalue())
expect(f'{R}/zip-bomb.zip', 'Internal', read='Partial', tests='A zip bomb: 1 MB that would unpack to 1 GB. Only the start is read, and the scan says so')

# Zips nested six deep: the first level is read, the deepest is past the nesting limit (3).
nested = zip_of([('level-6.txt', f'{ssn_line()}\n')])
for level in range(5, 0, -1):
    nested = zip_of([(f'level-{level + 1}.zip', nested)] + ([('level-1.txt', f'{ssn_line()}\n')] if level == 1 else []))
write(f'{R}/zips-nested-six-deep.zip', nested)
expect(f'{R}/zips-nested-six-deep.zip', 'Restricted', read='Partial', find='US SSN@High',
       tests='Zips nested six deep: the outer levels are read and the scan says the deepest were not')

write(f'{R}/setup.exe', b'MZ' + bytes(62) + b'Synthetic stand-in for a program. SSN 512-48-3399 must not be read from programs.')
expect(f'{R}/setup.exe', 'Unknown', read='Unsupported', tests='A program: never opened')
write(f'{R}/backup.iso', b'CD001' + bytes(2043) + b'Synthetic stand-in for a disk image.')
expect(f'{R}/backup.iso', 'Unknown', read='Unsupported', tests='A disk image: never opened')
write(f'{R}/training-video.mp4', b'\x00\x00\x00\x18ftypmp42' + bytes(64))
expect(f'{R}/training-video.mp4', 'Unknown', read='Unsupported', tests='A video: never opened')
write(f'{R}/export.csv.gz', gzip.compress(f'Employee,Note\nAvery Example,{ssn_line()}\n'.encode(), mtime=0))
expect(f'{R}/export.csv.gz', 'Unknown', read='Unsupported', tests='A gzip file: named, not unpacked yet')
write(f'{R}/database.sqlite', b'SQLite format 3\x00' + bytes(84) + b'synthetic')
expect(f'{R}/database.sqlite', 'Unknown', read='Unsupported', tests='A database file: named, not read')
write(f'{R}/empty.txt', b'')
expect(f'{R}/empty.txt', 'Internal', not_find='any', tests='An empty file: read, nothing found')

# ---------------------------------------------------------------------------------------------------------------
# expected.csv
# ---------------------------------------------------------------------------------------------------------------
missing = [c['File'] for c in cases if c['File'] not in BUILT_BY_TESTS and not os.path.exists(out(c['File']))]
assert not missing, missing
with open(os.path.join(PACK, 'expected.csv'), 'w', newline='', encoding='utf-8') as f:
    w = csv.DictWriter(f, fieldnames=['File', 'Label', 'Read', 'MustFind', 'MustNotFind', 'Tests'], lineterminator='\r\n')
    w.writeheader()
    w.writerows(cases)
print(f'{len(cases)} files in {PACK} ({len(BUILT_BY_TESTS)} written by TestPackTests)')
shutil.rmtree(work)
