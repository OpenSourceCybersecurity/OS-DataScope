#!/usr/bin/env python3
"""Makes the synthetic older-format files the reader tests open (D132): Word, Excel and PowerPoint 97-2003 files
saved by LibreOffice, 7-Zip archives made with py7zr, and RAR archives written here byte by byte (RAR's own tools
are not free software, so the archives store their files uncompressed). Every name and number is made up:
512-48-33xx are the test SSNs used across the test suite and 4111 1111 1111 1111 is the standard test card number.

Needs LibreOffice and py7zr (Ubuntu: apt install libreoffice-writer libreoffice-calc libreoffice-impress;
python3 -m pip install py7zr).

    python3 scripts/make-legacy-fixtures.py

Writes tests/DataScope.Scanner.Tests/Content/Fixtures/legacy/. The files are committed; run this again only to change them.
Outlook .msg files are built by the tests themselves (Fixtures.Msg), since no free tool saves them.
"""
import os
import shutil
import struct
import subprocess
import tempfile
import zlib

import py7zr

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'tests', 'DataScope.Scanner.Tests', 'Content', 'Fixtures', 'legacy')
os.makedirs(OUT, exist_ok=True)
work = tempfile.mkdtemp(prefix='ds-legacy-')


def convert(source, fmt, name):
    """Saves source as fmt with LibreOffice, in a profile of its own so no user settings apply."""
    subprocess.run(['soffice', '-env:UserInstallation=file://' + os.path.join(work, 'profile'), '--headless',
                    '--convert-to', fmt, '--outdir', work, source], check=True, capture_output=True)
    built = os.path.splitext(source)[0] + '.' + fmt.split(':')[0]
    shutil.move(built, os.path.join(OUT, name))
    print(f'{name}: {os.path.getsize(os.path.join(OUT, name)):,} bytes')


def write(name, text):
    path = os.path.join(work, name)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(text)
    return path


# A Word 97-2003 document: paragraphs, a table and accented text.
doc = write('payroll-change.html', """<html><head><meta charset="utf-8"><title>Payroll change</title></head><body>
<h1>Payroll change form</h1>
<p>Employee: Avery Example, Zürich office</p>
<p>Social Security Number: 512-48-3307</p>
<table border="1"><tr><td>Name</td><td>SSN</td></tr><tr><td>Jordan Sample</td><td>512-48-3308</td></tr></table>
<p>Approved by the HR team.</p>
</body></html>""")
convert(doc, 'doc:MS Word 97', 'payroll-change.doc')

# An Excel 97-2003 workbook: text, an SSN stored as a number, a card number, a decimal, an empty cell, and a second
# sheet. (LibreOffice saves no calculated result for formulas here, so formulas are tested with records built in the test.)
def cell(value):
    if isinstance(value, (int, float)):
        return f'<table:table-cell office:value-type="float" office:value="{value}"><text:p>{value}</text:p></table:table-cell>'
    if value is None:
        return '<table:table-cell/>'
    return f'<table:table-cell office:value-type="string"><text:p>{value}</text:p></table:table-cell>'


def sheet(name, rows):
    return (f'<table:table table:name="{name}">' + ''.join(
        '<table:table-row>' + ''.join(cell(v) for v in row) + '</table:table-row>' for row in rows) + '</table:table>')


xls = write('staff.fods', """<?xml version="1.0" encoding="UTF-8"?>
<office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"
 xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
 office:version="1.3" office:mimetype="application/vnd.oasis.opendocument.spreadsheet">
 <office:body><office:spreadsheet>""" + sheet('Staff', [
    ['Name', 'SSN', 'Card', 'Rate'],
    ['Avery Example', 512483307, 4111111111111111, 12.5],
    ['Jordan Sample', '512-48-3308', None, 7],
]) + sheet('Notes', [['Reviewed by Casey Demo']]) + """</office:spreadsheet></office:body>
</office:document>""")
convert(xls, 'xls:MS Excel 97', 'staff.xls')

# A PowerPoint 97-2003 presentation: two slides and a speaker note.
ppt = write('review.fodp', """<?xml version="1.0" encoding="UTF-8"?>
<office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
 xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
 xmlns:presentation="urn:oasis:names:tc:opendocument:xmlns:presentation:1.0" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0"
 office:version="1.3" office:mimetype="application/vnd.oasis.opendocument.presentation">
 <office:body><office:presentation>
  <draw:page draw:name="Slide1">
   <draw:frame svg:x="2cm" svg:y="2cm" svg:width="20cm" svg:height="3cm"><draw:text-box><text:p>Quarterly benefits review</text:p></draw:text-box></draw:frame>
   <draw:frame svg:x="2cm" svg:y="6cm" svg:width="20cm" svg:height="6cm"><draw:text-box><text:p>Case for Casey Demo</text:p><text:p>Member SSN 512-48-3307</text:p></draw:text-box></draw:frame>
   <presentation:notes><draw:frame svg:x="2cm" svg:y="2cm" svg:width="16cm" svg:height="10cm"><draw:text-box><text:p>Card on file 4111 1111 1111 1111</text:p></draw:text-box></draw:frame></presentation:notes>
  </draw:page>
  <draw:page draw:name="Slide2">
   <draw:frame svg:x="2cm" svg:y="2cm" svg:width="20cm" svg:height="3cm"><draw:text-box><text:p>Next steps</text:p></draw:text-box></draw:frame>
  </draw:page>
 </office:presentation></office:body>
</office:document>""")
convert(ppt, 'ppt:MS PowerPoint 97', 'review.ppt')

# 7-Zip archives: LZMA2, solid with several files, AES-encrypted contents, and encrypted names too.
files = {
    'hr/new-hire.txt': 'New hire Avery Example, SSN 512-48-3307\n',
    'hr/notes.txt': 'Nothing sensitive here.\n',
    'tools/setup.exe': 'MZ not really a program',
}
def seven(name, password=None, header_encryption=False):
    path = os.path.join(OUT, name)
    with py7zr.SevenZipFile(path, 'w', password=password, header_encryption=header_encryption) as z:
        for inner, text in files.items():
            z.writestr(text.encode(), inner)
    print(f'{name}: {os.path.getsize(path):,} bytes')
seven('records.7z')
seven('locked-files.7z', password='synthetic-test-only')
seven('locked-names.7z', password='synthetic-test-only', header_encryption=True)


# RAR 5 archives, files stored uncompressed (method 0). Layout from RAR's published format description.
def vint(n):
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        out.append(b | (0x80 if n else 0))
        if not n:
            return bytes(out)


def header(body):
    size = vint(len(body))
    return struct.pack('<I', zlib.crc32(size + body)) + size + body


def rar(name, entries):
    out = bytearray(b'Rar!\x1a\x07\x01\x00')
    out += header(vint(1) + vint(0) + vint(0))  # Main archive header: no flags.
    for inner, data in entries:
        encoded = inner.encode()
        fields = (vint(0x0004)                 # File flags: data CRC32 present.
                  + vint(len(data))            # Unpacked size.
                  + vint(0x20)                 # Attributes.
                  + struct.pack('<I', zlib.crc32(data))
                  + vint(0)                    # Compression: version 0, not solid, stored.
                  + vint(0)                    # Host OS: Windows.
                  + vint(len(encoded)) + encoded)
        out += header(vint(2) + vint(0x0002) + vint(len(data)) + fields)  # Type 2, has a data area of that size.
        out += data
    out += header(vint(5) + vint(0) + vint(0))  # End of archive.
    path = os.path.join(OUT, name)
    with open(path, 'wb') as f:
        f.write(out)
    print(f'{name}: {len(out):,} bytes')


rar('records.rar', [(k, v.encode()) for k, v in files.items()])

shutil.rmtree(work)
