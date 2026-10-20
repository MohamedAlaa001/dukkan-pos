namespace Dukkan.UI;

/// <summary>The version compiled from the UI project's Version property.</summary>
public static class ProductVersion
{
    public static string Current => typeof(ProductVersion).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    public static string Display => $"v{Current}";
}
