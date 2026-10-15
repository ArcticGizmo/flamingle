namespace Flamingle.Core.Saved;

public static class CommandLine
{
    /// <summary>
    /// A process's command line minus its first token (the program), parsed the way <c>CommandLineToArgvW</c>
    /// reads argv[0]: up to the closing quote if it starts with one, else up to the first space or tab. The rest is
    /// returned as-is (leading whitespace trimmed) so it can be handed straight back to the app.
    /// </summary>
    public static string Arguments(string commandLine)
    {
        var s = commandLine.TrimStart();
        if (s.Length == 0) return string.Empty;

        int end;
        if (s[0] == '"')
        {
            var close = s.IndexOf('"', 1);
            end = close < 0 ? s.Length : close + 1;
        }
        else
        {
            end = s.IndexOfAny([' ', '\t']);
            if (end < 0) end = s.Length;
        }
        return s[end..].TrimStart(' ', '\t');
    }
}
