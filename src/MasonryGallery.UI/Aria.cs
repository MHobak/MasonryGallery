namespace MasonryGallery.UI;

/// <summary>
/// ARIA state values. Blazor renders a bool attribute as present-and-empty when true (aria-pressed=""),
/// which neither assistive tech nor [aria-pressed="true"] selectors understand; ARIA wants the literal strings.
/// </summary>
public static class Aria
{
    public static string Bool(bool value) => value ? "true" : "false";
}
