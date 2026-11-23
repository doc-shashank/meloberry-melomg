using System.Reflection;

namespace Melomg.Domain.Common;

public static class MelomgVersion
{
    public static string Current => Resolve(typeof(MelomgVersion).Assembly);

    public static string Resolve(Assembly assembly)
    {
        string? infoVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            int plusIdx = infoVersion.IndexOf('+');
            return (plusIdx >= 0 ? infoVersion[..plusIdx] : infoVersion).Trim();
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
