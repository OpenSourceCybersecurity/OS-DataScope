using System.Text;

namespace DataScope.Scanner.Content;

/// <summary>Collects extracted text up to a character limit, then ignores the rest and remembers that it did.</summary>
internal sealed class TextBudget(int maxChars)
{
    private readonly StringBuilder text = new();

    public bool Full { get; private set; }

    public int Length => text.Length;

    public void Append(ReadOnlySpan<char> value)
    {
        if (Full)
        {
            return;
        }
        var room = maxChars - text.Length;
        if (value.Length > room)
        {
            text.Append(value[..room]);
            Full = true;
            return;
        }
        text.Append(value);
    }

    public void Append(char c)
    {
        if (text.Length >= maxChars)
        {
            Full = true;
            return;
        }
        text.Append(c);
    }

    public override string ToString() => text.ToString();
}
