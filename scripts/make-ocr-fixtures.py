#!/usr/bin/env python3
"""Makes the synthetic images the OCR tests read (D129). Every name and number is made up: 512-48-33xx are
test SSNs used across the test suite and 4111 1111 1111 1111 is the standard test card number. Needs Pillow
with libtiff and the DejaVu fonts (Ubuntu: apt install python3-pil fonts-dejavu-core).

    python3 scripts/make-ocr-fixtures.py

Writes tests/DataScope.Scanner.Tests/Content/Fixtures/ocr/. The files are committed; run this again only to change them.
"""
import io
import os
import random
import zipfile

from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = os.path.join(os.path.dirname(__file__), '..', 'tests', 'DataScope.Scanner.Tests', 'Content', 'Fixtures', 'ocr')
SANS = '/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf'
SERIF = '/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf'
MONO = '/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf'
random.seed(42)


def page(lines, size=(1275, 1650), font=SERIF, pt=34, top=140, mode='L'):
    """A letter page at 150 dpi, black text on white."""
    im = Image.new(mode, size, 255 if mode in ('L', '1') else (255, 255, 255))
    d = ImageDraw.Draw(im)
    f = ImageFont.truetype(font, pt)
    y = top
    for line in lines:
        d.text((110, y), line, font=f, fill=0)
        y += int(pt * 1.9)
    return im


def save(im, name, **kw):
    path = os.path.join(OUT, name)
    im.save(path, **kw)
    print(f'{name}: {os.path.getsize(path):,} bytes')


FORM = [
    'NEW HIRE FORM (SYNTHETIC TEST DATA)',
    'Employee name: Avery Example',
    'Social Security Number: 512-48-3307',
    'Date of birth: 04/12/1986',
    'Department: Accounts Payable',
]

os.makedirs(OUT, exist_ok=True)

# A scanned form: grayscale, a little noise and blur, as from a document scanner.
scan = page(FORM)
noise = Image.effect_noise(scan.size, 18).point(lambda v: 255 if v > 40 else 0)
scan = Image.composite(scan, Image.new('L', scan.size, 235), noise).filter(ImageFilter.GaussianBlur(0.6))
save(scan.quantize(16).convert('L'), 'form-scan.png', optimize=True)

# The same page as a JPEG, as a phone or a copier saves it.
save(scan, 'form-scan.jpg', quality=70)

# A screenshot at screen resolution: small text on a transparent background, which must read as white, not black.
shot = Image.new('RGBA', (760, 170), (0, 0, 0, 0))
d = ImageDraw.Draw(shot)
f = ImageFont.truetype(SANS, 15)
d.rectangle((0, 0, 759, 30), fill=(32, 64, 128, 255))
d.text((12, 7), 'HR Portal - Employee record', font=f, fill=(255, 255, 255, 255))
d.text((12, 50), 'Name: Jordan Sample', font=f, fill=(20, 20, 20, 255))
d.text((12, 80), 'SSN: 512-48-3308', font=f, fill=(20, 20, 20, 255))
d.text((12, 110), 'Payment card: 4111 1111 1111 1111', font=f, fill=(20, 20, 20, 255))
save(shot, 'screenshot.png', optimize=True)

# A two-page fax in Group 4 TIFF: the number is on the second page.
fax1 = page(['FAX COVER SHEET', 'To: Benefits office', 'Pages: 2'], mode='L').point(lambda v: 255 if v > 128 else 0).convert('1')
fax2 = page(['Claim for Riley Placeholder', 'Member SSN 512-48-3309'], mode='L').point(lambda v: 255 if v > 128 else 0).convert('1')
save(fax1, 'fax.tif', compression='group4', save_all=True, append_images=[fax2], dpi=(150, 150))

# A GIF banner.
gif = page(['Badge for Casey Demo', 'SSN 512-48-3307'], size=(900, 260), font=SANS, pt=30, top=40).convert('P')
save(gif, 'badge.gif')

# An animated GIF: the first frame has the text; the second is blank.
blank_frame = Image.new('L', gif.size, 255).convert('P')
save(gif, 'animated.gif', save_all=True, append_images=[blank_frame], duration=500, loop=0)

# A picture with no text: a smooth gradient with shapes.
blank = Image.linear_gradient('L').resize((640, 480))
d = ImageDraw.Draw(blank)
d.ellipse((200, 120, 440, 360), fill=90)
save(blank, 'no-text.png', optimize=True)

# A tiny image: a toolbar glyph.
save(Image.new('L', (16, 16), 128), 'tiny.png')

# Scanned PDFs: one page that is only a JPEG (DCTDecode), and a fax-style page that is only a 1-bit image.
save(scan.convert('RGB'), 'scanned.pdf', resolution=150, quality=70)
save(fax2, 'scanned-fax.pdf', resolution=150)

# Pages that went through the scanner crooked or the wrong way round (D131): turned a quarter either way, upside down,
# tilted a few degrees, and photographed at a slant by a phone.
form = scan.quantize(16).convert('L')
save(form.rotate(-90, expand=True), 'form-sideways.png', optimize=True)
save(form.rotate(90, expand=True), 'form-sideways-left.png', optimize=True)
save(form.rotate(180), 'form-upside-down.png', optimize=True)
save(form.rotate(4, expand=True, fillcolor=255, resample=Image.BICUBIC), 'form-tilted.png', optimize=True)
save(scan.rotate(-7, expand=True, fillcolor=255, resample=Image.BICUBIC), 'form-tilted.jpg', quality=70)

# Word, Excel and PowerPoint files whose only copy of the number is a scanned page pasted in as a picture.
picture = io.BytesIO()
form.save(picture, 'PNG', optimize=True)
CONTENT_TYPES = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                 '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                 '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                 '<Default Extension="xml" ContentType="application/xml"/><Default Extension="png" ContentType="image/png"/>'
                 '<Override PartName="/{main}" ContentType="{type}"/></Types>')
ROOT_RELS = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
             '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
             '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="{main}"/>'
             '</Relationships>')
IMAGE_RELS = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
              '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
              '<Relationship Id="rId9" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="{target}"/>'
              '</Relationships>')
PACKAGES = {
    'scan-pasted.docx': ('word/document.xml', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml',
                         '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>'
                         '<w:p><w:r><w:t>Signed form attached below.</w:t></w:r></w:p></w:body></w:document>',
                         'word/_rels/document.xml.rels', 'media/image1.png', 'word/media/image1.png'),
    'scan-pasted.xlsx': ('xl/workbook.xml', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml',
                         '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"/>',
                         'xl/drawings/_rels/drawing1.xml.rels', '../media/image1.png', 'xl/media/image1.png'),
    'scan-pasted.pptx': ('ppt/presentation.xml', 'application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml',
                         '<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"/>',
                         'ppt/slides/_rels/slide1.xml.rels', '../media/image1.png', 'ppt/media/image1.png'),
}
for name, (main, kind, body, rels, target, media) in PACKAGES.items():
    out = io.BytesIO()
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('[Content_Types].xml', CONTENT_TYPES.format(main=main, type=kind))
        z.writestr('_rels/.rels', ROOT_RELS.format(main=main))
        z.writestr(main, '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' + body)
        z.writestr(rels, IMAGE_RELS.format(target=target))
        z.writestr(media, picture.getvalue(), compress_type=zipfile.ZIP_STORED)
    with open(os.path.join(OUT, name), 'wb') as f:
        f.write(out.getvalue())
    print(f'{name}: {len(out.getvalue()):,} bytes')
