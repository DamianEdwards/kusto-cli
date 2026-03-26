namespace Kusto.Cli;

internal readonly record struct QueryLineRange(int StartLine, int EndLine)
{
    public int LineCount => EndLine - StartLine + 1;
}

internal readonly record struct QueryFileReference(string Path, QueryLineRange? LineRange);
