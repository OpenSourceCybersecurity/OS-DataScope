using System.Reflection;

namespace DataScope.Api.Hosting;

internal static class ProductVersion
{
    /// <summary>The build's version, such as "0.2.0-lab.14", without the source revision suffix.</summary>
    public static string Current { get; } =
        (typeof(ProductVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown")
        .Split('+')[0];
}
