using System.Reflection;

namespace Multiloud.App;
public static class AppVersion
{
    public static string Number { get; } = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public static string Display => $"v{Number}";
}
