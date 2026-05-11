namespace Kusto.Cli;

internal static class ChartStyle
{
    public const int MinDimension = 200;
    public const int MaxDimension = 8192;
    public const int DefaultWidth = 1200;
    public const int DefaultHeight = 675;

    // Tableau-inspired categorical palette (R, G, B)
    private static readonly (byte R, byte G, byte B)[] Palette =
    [
        (0x1F, 0x77, 0xB4),
        (0xD6, 0x27, 0x28),
        (0x2C, 0xA0, 0x2C),
        (0x94, 0x67, 0xBD),
        (0xFF, 0x7F, 0x0E),
        (0x17, 0xBE, 0xCF),
        (0x8C, 0x56, 0x4B),
        (0x7F, 0x7F, 0x7F),
        (0xBC, 0xBD, 0x22),
        (0xE3, 0x77, 0xC2)
    ];

    public static (byte R, byte G, byte B) SeriesColorRgb(int index)
    {
        return Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];
    }
}
