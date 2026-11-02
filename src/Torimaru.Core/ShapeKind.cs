namespace Torimaru.Core;

/// <summary>注釈ツールの種類。</summary>
public static class ShapeKind
{
    public const string Arrow = "Arrow";
    public const string Rectangle = "Rectangle";
    public const string Ellipse = "Ellipse";
    public const string Text = "Text";
    public const string Callout = "Callout";
    public const string Step = "Step";
    public const string Highlight = "Highlight";
    public const string Blur = "Blur";

    public static readonly string[] All =
    [
        Arrow, Rectangle, Ellipse, Text, Callout, Step, Highlight, Blur
    ];

    public static bool IsKnown(string? kind) => kind is not null && Array.IndexOf(All, kind) >= 0;
}
