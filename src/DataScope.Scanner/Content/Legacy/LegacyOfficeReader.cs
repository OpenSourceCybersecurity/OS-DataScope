using DataScope.Core.Classification;

namespace DataScope.Scanner.Content.Legacy;

/// <summary>
/// Reads files in the OLE compound file format by what they hold, whatever their names say (D132): Word, Excel and
/// PowerPoint 97-2003 files, Outlook messages, and password-protected Word, Excel and PowerPoint 2007+ files (which
/// Office saves in this wrapper). Other compound files, such as Visio or Publisher files, are named and not read.
/// </summary>
internal static class LegacyOfficeReader
{
    private const string Damaged = "The file is damaged or not in the format its name suggests.";

    public static ContentExtraction Read(Stream stream, ContentLimits limits, int depth, CancellationToken ct)
    {
        try
        {
            var file = CompoundFile.Open(stream);
            var root = file.Root;
            if (file.Find(root, "EncryptedPackage") is not null)
            {
                return ContentExtraction.Of(ContentState.Encrypted, "The file is password-protected.");
            }
            if (file.Find(root, "WordDocument") is { IsStorage: false } word)
            {
                return WordBinaryReader.Read(file, word, limits, ct);
            }
            if (file.Find(root, "Workbook") is { IsStorage: false } workbook)
            {
                return ExcelBinaryReader.Read(file, workbook, limits, ct);
            }
            if (file.Find(root, "Book") is { IsStorage: false })
            {
                return ContentExtraction.Of(ContentState.Unsupported, "Excel workbooks from Excel 95 and earlier are not read.");
            }
            if (file.Find(root, "PowerPoint Document") is { IsStorage: false } slides)
            {
                return PowerPointBinaryReader.Read(file, slides, limits, ct);
            }
            if (OutlookMessageReader.IsMessage(file, root))
            {
                return OutlookMessageReader.Read(file, limits, depth, ct);
            }
            return ContentExtraction.Of(ContentState.Unsupported,
                "The contents are in a Microsoft compound file format Data Scope does not read, for example a Visio, Publisher or Works file.");
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            // A length or offset inside the file points somewhere it can't: the file is damaged. Every read is bounds
            // checked by the runtime, so this is where a damaged structure the readers did not foresee ends up.
            return ContentExtraction.Of(ContentState.Unreadable, Damaged);
        }
    }
}
