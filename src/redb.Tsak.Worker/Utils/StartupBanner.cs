using System.Reflection;
using System.Text;

namespace redb.Tsak.Worker.Utils;

/// <summary>
/// ASCII startup banner for redb.Tsak container.
/// </summary>
public static class StartupBanner
{
    public static void Print(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetExecutingAssembly();
        try
        {
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion?.Split('+')[0]
                ?? assembly.GetName().Version?.ToString()
                ?? "0.0.0";

            Console.Write(Build(version));
        }
        catch
        {
            Console.WriteLine($"=== REDB TSAK v{assembly.GetName().Version?.ToString() ?? "?"} ===");
        }
    }

    private static string Build(string version)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("   ██████╗ ███████╗██████╗ ██████╗    ████████╗███████╗ █████╗ ██╗  ██╗");
        sb.AppendLine("   ██╔══██╗██╔════╝██╔══██╗██╔══██╗   ╚══██╔══╝██╔════╝██╔══██╗██║ ██╔╝");
        sb.AppendLine("   ██████╔╝█████╗  ██║  ██║██████╔╝      ██║   ███████╗███████║█████╔╝ ");
        sb.AppendLine("   ██╔══██╗██╔══╝  ██║  ██║██╔══██╗      ██║   ╚════██║██╔══██║██╔═██╗ ");
        sb.AppendLine("   ██║  ██║███████╗██████╔╝██████╔╝      ██║   ███████║██║  ██║██║  ██╗");
        sb.AppendLine("   ╚═╝  ╚═╝╚══════╝╚═════╝ ╚═════╝       ╚═╝   ╚══════╝╚═╝  ╚═╝╚═╝  ╚═╝");
        sb.AppendLine();
        sb.AppendLine("                     ROUTE CONTAINER SYSTEM");
        sb.AppendLine();
        sb.AppendLine(FmtLine("Version:", version));
        sb.AppendLine(FmtLine("Runtime:", Environment.Version.ToString()));
        sb.AppendLine(FmtLine("OS:", Environment.OSVersion.VersionString));
        sb.AppendLine(FmtLine("Started:", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine();
        sb.AppendLine("                                        by relikt");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string FmtLine(string label, string value)
        => $"   {label,-12} {value}";
}
