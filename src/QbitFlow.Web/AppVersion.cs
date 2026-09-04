using System.Reflection;

namespace QbitFlow.Web;

/// <summary>
/// The app version rendered in the site header. Sourced from the assembly's informational
/// version (generated from <c>&lt;Version&gt;</c> in <c>QbitFlow.Web.csproj</c>); any
/// <c>+&lt;commit&gt;</c> build metadata is trimmed for display.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var asm = typeof(AppVersion).Assembly;

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
