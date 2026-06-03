using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using DnsClient;
using Microsoft.Win32;

namespace DnsManager;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (!OperatingSystem.IsWindows())
        {
            Ui.Error("This tool only supports Windows.");
            return 1;
        }

        if (args.Length == 0 || HasAny(args, "--help", "-h", "/?"))
        {
            ShowHelp();
            return 0;
        }

        try
        {
            var adapterName = GetOptionValue(args, "--adapter");
            var profileNameOpt = GetOptionValue(args, "--name");
            var noSave = HasAny(args, "--no-save");

            if (Has(args, "--set"))
            {
                var pos = IndexOf(args, "--set");
                if (pos < 0 || args.Length <= pos + 1)
                {
                    Ui.Error("Usage: dns.exe --set <primaryDNS> [secondaryDNS]");
                    return 2;
                }
                var primary = args[pos + 1];
                string? secondary = (args.Length > pos + 2 && !args[pos + 2].StartsWith("--", StringComparison.OrdinalIgnoreCase)) ? args[pos + 2] : null;

                // اعتبارسنجی IP
                if (!IsValidIp(primary) || (secondary != null && !IsValidIp(secondary)))
                {
                    Ui.Error("Invalid IP address format.");
                    return 5;
                }

                Admin.EnsureElevatedOrRelaunch();
                var nic = NetworkAdapterFinder.GetActive(adapterName);
                if (nic == null)
                {
                    Ui.Error("No active network adapter found. Use --adapter \"<Name>\" to specify one.");
                    return 3;
                }

                Ui.Action($"Setting DNS on adapter \"{nic.Name}\" …");
                DnsConfigurator.SetDns(nic, primary, secondary);

                string profileName = profileNameOpt ?? PromptProfileName();
                if (!noSave && !string.IsNullOrWhiteSpace(profileName))
                {
                    var profile = new DnsProfile
                    {
                        Id = RegistryStore.GetNextId(),
                        Name = profileName.Trim(),
                        PrimaryDns = primary,
                        SecondaryDns = secondary,
                        CreatedAtUtc = DateTime.UtcNow,
                        LastUsedAtUtc = DateTime.UtcNow
                    };
                    RegistryStore.SaveProfile(profile);
                    Ui.Success($"Profile saved as \"{profile.Name}\" (id: {profile.Id}).");
                }

                // تست اتصال جدید
                Ui.Action("Testing DNS resolution...");
                var resolver = new DnsResolver();
                var testResult = await resolver.TestDnsAsync(primary);
                if (testResult)
                    Ui.Success("DNS resolution test passed.");
                else
                    Ui.Error("DNS resolution test failed. Server may not be reachable.");

                Ui.Success("DNS updated.");
                return 0;
            }

            if (Has(args, "--clear"))
            {
                Admin.EnsureElevatedOrRelaunch();
                var nic = NetworkAdapterFinder.GetActive(adapterName);
                if (nic == null)
                {
                    Ui.Error("No active network adapter found. Use --adapter \"<Name>\" to specify one.");
                    return 3;
                }
                Ui.Action($"Clearing DNS (DHCP) on adapter \"{nic.Name}\" …");
                DnsConfigurator.SetDhcp(nic);
                Ui.Success("DNS cleared (DHCP).");
                return 0;
            }

            if (Has(args, "--show"))
            {
                var nic = NetworkAdapterFinder.GetActive(adapterName);
                if (nic == null)
                {
                    Ui.Error("No active network adapter found. Use --adapter \"<Name>\" to specify one.");
                    return 3;
                }

                Ui.Header($"Adapter: {nic.Name}");
                var dnses = NetworkAdapterFinder.GetDnsServers(nic);
                if (dnses.Count == 0) Ui.Info("DNS Servers: (none / DHCP)");
                else
                {
                    Ui.Info("DNS Servers:");
                    int i = 1;
                    foreach (var ip in dnses)
                        Console.WriteLine($"   {Ui.IconBullet} [{i++}] {ip}");
                }

                // نمایش تست سرعت DNS
                if (dnses.Count > 0)
                {
                    Ui.Action("Testing DNS speed...");
                    var resolver = new DnsResolver();
                    foreach (var dns in dnses)
                    {
                        var latency = await resolver.MeasureLatencyAsync(dns);
                        if (latency >= 0)
                            Console.WriteLine($"   {Ui.IconBullet} {dns}: {latency:F1}ms");
                        else
                            Console.WriteLine($"   {Ui.IconBullet} {dns}: timeout/unreachable");
                    }
                }
                return 0;
            }

            if (Has(args, "--profiles"))
            {
                var profiles = RegistryStore.LoadAll();
                if (profiles.Count == 0)
                {
                    Ui.Info("No saved DNS profiles.");
                    Ui.Info($"Registry path: {RegistryStore.RegistryPathDisplay}");
                    return 0;
                }

                Ui.Header("Saved DNS Profiles");
                foreach (var p in profiles.OrderBy(p => p.Id))
                {
                    Console.WriteLine($"{Ui.IconBullet} ID: {p.Id} | Name: {p.Name} | Primary: {p.PrimaryDns} | Secondary: {p.SecondaryDns ?? "-"}");
                }
                Ui.Info($"Registry path: {RegistryStore.RegistryPathDisplay}");
                return 0;
            }

            if (Has(args, "--set-profile"))
            {
                var pos = IndexOf(args, "--set-profile");
                if (pos < 0 || args.Length <= pos + 1 || !int.TryParse(args[pos + 1], out var id))
                {
                    Ui.Error("Usage: dns.exe --set-profile <id>");
                    return 2;
                }

                var profile = RegistryStore.FindById(id);
                if (profile == null)
                {
                    Ui.Error($"Profile id {id} not found.");
                    return 4;
                }

                Admin.EnsureElevatedOrRelaunch();
                var nic = NetworkAdapterFinder.GetActive(adapterName);
                if (nic == null)
                {
                    Ui.Error("No active network adapter found. Use --adapter \"<Name>\" to specify one.");
                    return 3;
                }

                Ui.Action($"Applying profile \"{profile.Name}\" (id: {profile.Id}) to adapter \"{nic.Name}\" …");
                DnsConfigurator.SetDns(nic, profile.PrimaryDns!, profile.SecondaryDns);
                profile.LastUsedAtUtc = DateTime.UtcNow;
                RegistryStore.SaveProfile(profile);
                Ui.Success("DNS updated from profile.");
                return 0;
            }

            if (Has(args, "--delete-profile"))
            {
                var pos = IndexOf(args, "--delete-profile");
                if (pos < 0 || args.Length <= pos + 1 || !int.TryParse(args[pos + 1], out var id))
                {
                    Ui.Error("Usage: dns.exe --delete-profile <id>");
                    return 2;
                }
                if (RegistryStore.DeleteProfile(id))
                {
                    Ui.Success($"Profile {id} deleted.");
                    return 0;
                }
                Ui.Error($"Profile id {id} not found.");
                return 4;
            }

            if (Has(args, "--rename-profile"))
            {
                var pos = IndexOf(args, "--rename-profile");
                if (pos < 0 || args.Length <= pos + 2 || !int.TryParse(args[pos + 1], out var id))
                {
                    Ui.Error("Usage: dns.exe --rename-profile <id> <newName>");
                    return 2;
                }
                var newName = args[pos + 2];
                if (RegistryStore.RenameProfile(id, newName))
                {
                    Ui.Success($"Profile {id} renamed to \"{newName}\".");
                    return 0;
                }
                Ui.Error($"Profile id {id} not found.");
                return 4;
            }

            if (Has(args, "--export"))
            {
                var pos = IndexOf(args, "--export");
                string? filePath;

                if (pos >= 0 && args.Length > pos + 1 && !args[pos + 1].StartsWith("--", StringComparison.OrdinalIgnoreCase))
                {
                    filePath = args[pos + 1];
                }
                else
                {
                    filePath = "dns-profiles.json";
                }

                // اگه --all نباشه، فقط آیدی خاص export میشه
                if (Has(args, "--id"))
                {
                    var idPos = IndexOf(args, "--id");
                    if (idPos < 0 || args.Length <= idPos + 1 || !int.TryParse(args[idPos + 1], out var id))
                    {
                        Ui.Error("Usage: dns.exe --export [filePath] --id <profileId>");
                        return 2;
                    }

                    var profile = RegistryStore.FindById(id);
                    if (profile == null)
                    {
                        Ui.Error($"Profile id {id} not found.");
                        return 4;
                    }

                    ProfileExporter.ExportToFile(new[] { profile }, filePath);
                    Ui.Success($"Profile \"{profile.Name}\" (id: {profile.Id}) exported to: {Path.GetFullPath(filePath)}");
                }
                else
                {
                    // Export همه پروفایل‌ها
                    var profiles = RegistryStore.LoadAll();
                    if (profiles.Count == 0)
                    {
                        Ui.Error("No profiles to export.");
                        return 4;
                    }

                    ProfileExporter.ExportToFile(profiles, filePath);
                    Ui.Success($"{profiles.Count} profile(s) exported to: {Path.GetFullPath(filePath)}");
                }
                return 0;
            }

            if (Has(args, "--import"))
            {
                var pos = IndexOf(args, "--import");
                string? filePath;

                if (pos >= 0 && args.Length > pos + 1 && !args[pos + 1].StartsWith("--", StringComparison.OrdinalIgnoreCase))
                {
                    filePath = args[pos + 1];
                }
                else
                {
                    filePath = "dns-profiles.json";
                }

                if (!File.Exists(filePath))
                {
                    Ui.Error($"File not found: {Path.GetFullPath(filePath)}");
                    return 6;
                }

                var mergeMode = Has(args, "--merge");
                var profiles = ProfileExporter.ImportFromFile(filePath);

                if (profiles.Count == 0)
                {
                    Ui.Error("No valid profiles found in file.");
                    return 4;
                }

                if (!mergeMode)
                {
                    // حذف همه پروفایل‌های قبلی
                    var existingProfiles = RegistryStore.LoadAll();
                    foreach (var p in existingProfiles)
                    {
                        RegistryStore.DeleteProfile(p.Id);
                    }
                }

                // Import پروفایل‌ها
                int imported = 0;
                int skipped = 0;
                foreach (var profile in profiles)
                {
                    // اگر merge mode باشه، پروفایل‌های تکراری رو skip کن
                    if (mergeMode)
                    {
                        var existing = RegistryStore.FindById(profile.Id);
                        if (existing != null)
                        {
                            skipped++;
                            continue;
                        }
                    }

                    // Assign new ID
                    profile.Id = RegistryStore.GetNextId();
                    profile.CreatedAtUtc = DateTime.UtcNow;
                    profile.LastUsedAtUtc = null;
                    RegistryStore.SaveProfile(profile);
                    imported++;
                }

                if (mergeMode)
                    Ui.Success($"Imported {imported} profile(s), skipped {skipped} duplicate(s).");
                else
                    Ui.Success($"Imported {imported} profile(s) from: {Path.GetFullPath(filePath)}");

                return 0;
            }

            if (Has(args, "--benchmark"))
            {
                Ui.Header("DNS Speed Benchmark");
                var commonDns = new Dictionary<string, string>
                {
                    ["Google"] = "8.8.8.8",
                    ["Cloudflare"] = "1.1.1.1",
                    ["OpenDNS"] = "208.67.222.222",
                    ["Quad9"] = "9.9.9.9",
                    ["AdGuard"] = "94.140.14.14",
                    ["CleanBrowsing"] = "185.228.168.9"
                };

                var resolver = new DnsResolver();
                foreach (var kvp in commonDns)
                {
                    var latency = await resolver.MeasureLatencyAsync(kvp.Value);
                    if (latency >= 0)
                        Console.WriteLine($"{Ui.IconBullet} {kvp.Key,-15} ({kvp.Value,-16}): {latency,6:F1}ms");
                    else
                        Console.WriteLine($"{Ui.IconBullet} {kvp.Key,-15} ({kvp.Value,-16}): timeout");
                }
                return 0;
            }

            if (Has(args, "--about"))
            {
                ShowAbout();
                return 0;
            }

            if (Has(args, "--resolve"))
            {
                var pos = IndexOf(args, "--resolve");
                if (pos < 0 || args.Length <= pos + 1)
                {
                    Ui.Error("Usage: dns.exe --resolve <domain> [--dns <server>]");
                    return 2;
                }
                var domain = args[pos + 1];
                var dnsServer = GetOptionValue(args, "--dns");

                Ui.Header($"Resolving: {domain}");
                var resolver = new DnsResolver();
                var ips = await resolver.ResolveAsync(domain, dnsServer);

                if (ips.Count == 0)
                {
                    Ui.Error($"Could not resolve {domain}");
                }
                else
                {
                    Ui.Info($"Found {ips.Count} address(es):");
                    foreach (var ip in ips)
                    {
                        Console.WriteLine($"   {Ui.IconBullet} {ip}");
                    }
                }
                return 0;
            }

            Ui.Error("Unknown command. Use: dns.exe --help");
            return 2;
        }
        catch (Admin.UserCancelledException)
        {
            Ui.Error("Operation cancelled by user.");
            return 1223;
        }
        catch (UnauthorizedAccessException)
        {
            Ui.Error("Administrator privileges required. Please run as Administrator.");
            return 1223;
        }
        catch (Exception ex)
        {
            Ui.Error($"Unexpected error: {ex.Message}");
            return 1;
        }
    }

    private static bool IsValidIp(string ip)
    {
        return IPAddress.TryParse(ip, out _);
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    private static bool HasAny(string[] args, params string[] flags) =>
        args.Any(a => flags.Any(f => string.Equals(a, f, StringComparison.OrdinalIgnoreCase)));
    private static int IndexOf(string[] args, string flag) =>
        Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    private static string? GetOptionValue(string[] args, string option)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
    private static string PromptProfileName()
    {
        Console.Write($"{Ui.IconSave} Enter a profile name to save: ");
        var name = Console.ReadLine() ?? string.Empty;
        return name.Trim();
    }
    private static void ShowAbout()
    {
        Console.WriteLine();
        var prev = Console.ForegroundColor;

        // Banner art
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("  ╔══════════════════════════════════════════════════════════╗");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ║                                                          ║");
            Console.WriteLine("  ║     ██████╗  ██████╗ ███╗   ██╗███╗   ██╗███████╗██████╗  ║");
            Console.WriteLine("  ║    ██╔════╝ ██╔═══██╗████╗  ██║████╗  ██║██╔════╝██╔══██╗ ║");
            Console.WriteLine("  ║    ██║  ███╗██║   ██║██╔██╗ ██║██╔██╗ ██║█████╗  ██████╔╝ ║");
            Console.WriteLine("  ║    ██║   ██║██║   ██║██║╚██╗██║██║╚██╗██║██╔══╝  ██╔══██╗ ║");
            Console.WriteLine("  ║    ╚██████╔╝╚██████╔╝██║ ╚████║██║ ╚████║███████╗██║  ██║ ║");
            Console.WriteLine("  ║     ╚═════╝  ╚═════╝ ╚═╝  ╚═══╝╚═╝  ╚═══╝╚══════╝╚═╝  ╚═╝ ║");
            Console.WriteLine("  ║                                                          ║");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("  ╚══════════════════════════════════════════════════════════╝");
        }
        finally { Console.ForegroundColor = prev; }

        Console.WriteLine();

        // Developer identity - Kali-style terminal prompt
        try
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  ┌──( " + "RoOt㉿zErO" + " )-[~]");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("  │");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine();
        }
        finally { Console.ForegroundColor = prev; }

        // Info block inside the terminal frame
        try
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("  │ ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("🌐 Website ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(" : ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("mrrezanemati.ir");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("  │ ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("✈️ Telegram");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(" : ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("@webs7");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("  │ ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("💻 Project ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(" : ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("DNS Manager CLI");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("  │ ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("🔧 Tech    ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(" : ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("C# / .NET 10");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("  │");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine();
            Console.Write("  └");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("───────────────────────────────────");
        }
        finally { Console.ForegroundColor = prev; }

        Console.WriteLine();

        // Footer
        Ui.Info("DNS Manager CLI - Designed & Developed with ❤️");
        Console.WriteLine();
    }

    private static void ShowHelp()
    {
        Ui.Header("DNS Manager CLI (dns.exe)");
        Console.WriteLine(@$"
{Ui.IconCmd} Usage:
  dns.exe --set <primaryDNS> [secondaryDNS] [--adapter ""Wi-Fi""] [--name <profileName>] [--no-save]
  dns.exe --clear [--adapter ""Wi-Fi""]
  dns.exe --show [--adapter ""Wi-Fi""]
  dns.exe --resolve <domain> [--dns <server>]
  dns.exe --profiles
  dns.exe --set-profile <id> [--adapter ""Wi-Fi""]
  dns.exe --delete-profile <id>
  dns.exe --rename-profile <id> <newName>
  dns.exe --export [filePath] [--id <profileId>]
  dns.exe --import [filePath] [--merge]
  dns.exe --benchmark
  dns.exe --about
  dns.exe --help

{Ui.IconInfo} Features:
  • Direct WMI management (no netsh dependency)
  • DNS speed testing with DnsClient library
  • DNS resolution testing
  • Built-in IP validation
  • Profile management with Registry storage
  • Import/Export profiles to JSON format
  • Admin elevation when needed
  • Benchmark common DNS servers
  • Developer info & credits

{Ui.IconInfo} Examples:
  dns.exe --set 1.1.1.1 8.8.8.8 --name ""Cloudflare+Google""
  dns.exe --show
  dns.exe --resolve github.com --dns 1.1.1.1
  dns.exe --benchmark
  dns.exe --export dns-backup.json
  dns.exe --export my-profile.json --id 1
  dns.exe --import dns-backup.json
  dns.exe --import shared.json --merge
  dns.exe --about

{Ui.IconInfo} Notes:
  • Automatically picks the active adapter (with an IPv4 gateway). Use --adapter to override.
  • Profiles are stored in the Windows Registry under: {RegistryStore.RegistryPathDisplay}
  • Requires Administrator only for: --set, --clear, --set-profile
  • --export without --id exports all profiles
  • --import with --merge keeps existing profiles and adds new ones
  • Default export/import file is ""dns-profiles.json"" in current directory
");
    }
}

// ================= UI =================
internal static class Ui
{
    public static string IconOk => "✅";
    public static string IconErr => "❌";
    public static string IconInfo => "ℹ️";
    public static string IconAct => "🔧";
    public static string IconNet => "🌐";
    public static string IconSave => "💾";
    public static string IconList => "📄";
    public static string IconCmd => "🖥️";
    public static string IconIdeas => "💡";
    public static string IconBullet => "•";

    public static void Header(string text)
    {
        Console.WriteLine($"\n{IconNet} {text}");
        Console.WriteLine(new string('─', Math.Max(12, text.Length + 2)));
    }

    public static void Success(string text) => WriteLine(IconOk, text, ConsoleColor.Green);
    public static void Error(string text) => WriteLine(IconErr, text, ConsoleColor.Red);
    public static void Info(string text) => WriteLine(IconInfo, text, ConsoleColor.Cyan);
    public static void Action(string text) => WriteLine(IconAct, text, ConsoleColor.Yellow);

    private static void WriteLine(string icon, string text, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"{icon} {text}");
        }
        finally { Console.ForegroundColor = prev; }
    }
}

// ================= Admin Elevation =================
internal static class Admin
{
    public sealed class UserCancelledException : Exception { }

    public static void EnsureElevatedOrRelaunch()
    {
        if (IsElevated()) return;

        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe))
            throw new InvalidOperationException("Unable to locate current executable path.");

        var args = Environment.GetCommandLineArgs().Skip(1).Select(QuoteIfNeeded);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(" ", args),
            Verb = "runas",
            UseShellExecute = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        try
        {
            var p = Process.Start(psi);
            if (p == null) throw new UserCancelledException();
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new UserCancelledException();
        }
        Environment.Exit(0);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string QuoteIfNeeded(string s)
    {
        if (s.Contains('\"')) s = s.Replace("\"", "\\\"");
        if (s.Contains(' ') || s.Contains('\t') || s.Contains(';')) return $"\"{s}\"";
        return s;
    }
}

// ================= Profile Import/Export =================
internal static class ProfileExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void ExportToFile(IEnumerable<DnsProfile> profiles, string filePath)
    {
        var exportData = new DnsProfilesExport
        {
            ExportedAtUtc = DateTime.UtcNow,
            Version = "1.0",
            Profiles = profiles.Select(p => new DnsProfileExport
            {
                Id = p.Id,
                Name = p.Name,
                PrimaryDns = p.PrimaryDns,
                SecondaryDns = p.SecondaryDns,
                CreatedAtUtc = p.CreatedAtUtc,
                LastUsedAtUtc = p.LastUsedAtUtc
            }).ToList()
        };

        var json = JsonSerializer.Serialize(exportData, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public static List<DnsProfile> ImportFromFile(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var importData = JsonSerializer.Deserialize<DnsProfilesExport>(json, JsonOptions);

        if (importData?.Profiles == null || importData.Profiles.Count == 0)
            return new List<DnsProfile>();

        return importData.Profiles
            .Where(p => !string.IsNullOrWhiteSpace(p.PrimaryDns) && IsValidIp(p.PrimaryDns))
            .Select(p => new DnsProfile
            {
                Id = p.Id,
                Name = p.Name ?? "Imported Profile",
                PrimaryDns = p.PrimaryDns,
                SecondaryDns = string.IsNullOrWhiteSpace(p.SecondaryDns) ? null : p.SecondaryDns,
                CreatedAtUtc = p.CreatedAtUtc,
                LastUsedAtUtc = p.LastUsedAtUtc
            })
            .ToList();
    }

    private static bool IsValidIp(string ip)
    {
        return IPAddress.TryParse(ip, out _);
    }
}

// ================= JSON Models =================
internal class DnsProfilesExport
{
    public DateTime ExportedAtUtc { get; set; }
    public string Version { get; set; } = "1.0";
    public List<DnsProfileExport> Profiles { get; set; } = new();
}

internal class DnsProfileExport
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PrimaryDns { get; set; }
    public string? SecondaryDns { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
}

// ================= DNS Resolver with DnsClient =================
internal class DnsResolver
{
    public async Task<bool> TestDnsAsync(string dnsServer, string testDomain = "google.com")
    {
        try
        {
            var options = new LookupClientOptions(IPAddress.Parse(dnsServer))
            {
                Timeout = TimeSpan.FromSeconds(5),
                UseCache = false,
                Retries = 1
            };

            var client = new LookupClient(options);
            var result = await client.QueryAsync(testDomain, QueryType.A);

            return !result.HasError && result.Answers.Any();
        }
        catch
        {
            return false;
        }
    }

    public async Task<double> MeasureLatencyAsync(string dnsServer, string testDomain = "google.com")
    {
        try
        {
            var options = new LookupClientOptions(IPAddress.Parse(dnsServer))
            {
                Timeout = TimeSpan.FromSeconds(5),
                UseCache = false,
                Retries = 0
            };

            var client = new LookupClient(options);
            var sw = Stopwatch.StartNew();
            var result = await client.QueryAsync(testDomain, QueryType.A);
            sw.Stop();

            return result.HasError ? -1 : sw.Elapsed.TotalMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    public async Task<List<IPAddress>> ResolveAsync(string domain, string? dnsServer = null)
    {
        try
        {
            LookupClient client;
            if (dnsServer != null)
            {
                var options = new LookupClientOptions(IPAddress.Parse(dnsServer))
                {
                    Timeout = TimeSpan.FromSeconds(5),
                    UseCache = false
                };
                client = new LookupClient(options);
            }
            else
            {
                client = new LookupClient();
            }

            var result = await client.QueryAsync(domain, QueryType.A);
            return result.Answers.ARecords().Select(r => r.Address).ToList();
        }
        catch
        {
            return new List<IPAddress>();
        }
    }
}

// ================= DNS Configuration (WMI) =================
internal static class DnsConfigurator
{
    public static void SetDns(NetworkInterface nic, string primary, string? secondary)
    {
        var config = GetNetworkAdapterConfig(nic.Description);
        if (config == null)
            throw new Exception($"Could not find WMI configuration for adapter: {nic.Name}");

        try
        {
            var dnsServers = secondary != null
                ? new[] { primary, secondary }
                : new[] { primary };

            using var mo = new ManagementObject(config["__PATH"].ToString());
            var inParams = mo.GetMethodParameters("SetDNSServerSearchOrder");
            inParams["DNSServerSearchOrder"] = dnsServers;

            var outParams = mo.InvokeMethod("SetDNSServerSearchOrder", inParams, null);
            var returnValue = (uint)outParams["ReturnValue"];

            if (returnValue != 0 && returnValue != 1)
                throw new Exception($"Failed to set DNS. WMI return code: {returnValue}");

            FlushDns();
        }
        catch (Exception ex)
        {
            throw new Exception($"Error setting DNS: {ex.Message}", ex);
        }
    }

    public static void SetDhcp(NetworkInterface nic)
    {
        var config = GetNetworkAdapterConfig(nic.Description);
        if (config == null)
            throw new Exception($"Could not find WMI configuration for adapter: {nic.Name}");

        try
        {
            using var mo = new ManagementObject(config["__PATH"].ToString());
            var inParams = mo.GetMethodParameters("SetDNSServerSearchOrder");
            inParams["DNSServerSearchOrder"] = null;

            var outParams = mo.InvokeMethod("SetDNSServerSearchOrder", inParams, null);
            var returnValue = (uint)outParams["ReturnValue"];

            if (returnValue != 0 && returnValue != 1)
                throw new Exception($"Failed to set DHCP DNS. WMI return code: {returnValue}");

            FlushDns();
        }
        catch (Exception ex)
        {
            throw new Exception($"Error setting DHCP: {ex.Message}", ex);
        }
    }

    private static ManagementObject? GetNetworkAdapterConfig(string adapterDescription)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");

        foreach (ManagementObject obj in searcher.Get())
        {
            var description = obj["Description"]?.ToString();

            if (description?.Equals(adapterDescription, StringComparison.OrdinalIgnoreCase) == true)
                return obj;
        }

        return null;
    }

    private static void FlushDns()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            process?.WaitForExit(2000);
        }
        catch { /* ignore */ }
    }
}

// ================= Network / DNS Ops =================
internal static class NetworkAdapterFinder
{
    public static NetworkInterface? GetActive(string? preferredName = null)
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                !n.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase) &&
                !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var exact = nics.FirstOrDefault(n =>
                string.Equals(n.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        // Prefer NICs with an IPv4 default gateway
        NetworkInterface? best = null;
        foreach (var nic in nics)
        {
            var props = nic.GetIPProperties();
            bool hasIpv4 = nic.Supports(NetworkInterfaceComponent.IPv4);
            bool hasGw = props.GatewayAddresses.Any(g =>
                g?.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork);
            if (hasIpv4 && hasGw)
                return nic;
            if (best == null && hasIpv4) best = nic;
        }
        return best ?? nics.FirstOrDefault();
    }

    public static List<string> GetDnsServers(NetworkInterface nic)
    {   
        var props = nic.GetIPProperties();
        return props.DnsAddresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork ||
                       a.AddressFamily == AddressFamily.InterNetworkV6)
            .Select(a => a.ToString())
            .ToList();
    }
}

// ================= Profiles (Registry) =================
internal sealed class DnsProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "unnamed";
    public string? PrimaryDns { get; set; }
    public string? SecondaryDns { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
}

internal static class RegistryStore
{
    public const string BaseKeyPath = @"Software\DnsManager\Profiles";
    public static string RegistryPathDisplay => @"HKCU\" + BaseKeyPath;

    public static int GetNextId()
    {
        using var baseKey = Registry.CurrentUser.CreateSubKey(BaseKeyPath, true)!;
        var ids = baseKey.GetSubKeyNames()
            .Select(n => int.TryParse(n, out var i) ? i : 0)
            .Where(i => i > 0)
            .ToList();
        return ids.Count == 0 ? 1 : ids.Max() + 1;
    }

    public static List<DnsProfile> LoadAll()
    {
        using var baseKey = Registry.CurrentUser.CreateSubKey(BaseKeyPath, true)!;
        var list = new List<DnsProfile>();
        foreach (var name in baseKey.GetSubKeyNames())
        {
            if (!int.TryParse(name, out var id)) continue;
            var p = ReadProfile(baseKey, id);
            if (p != null) list.Add(p);
        }
        return list;
    }

    public static DnsProfile? FindById(int id)
    {
        using var baseKey = Registry.CurrentUser.CreateSubKey(BaseKeyPath, true)!;
        return ReadProfile(baseKey, id);
    }

    public static void SaveProfile(DnsProfile profile)
    {
        using var baseKey = Registry.CurrentUser.CreateSubKey(BaseKeyPath, true)!;
        using var sub = baseKey.CreateSubKey(profile.Id.ToString(), true)!;
        sub.SetValue("Id", profile.Id, RegistryValueKind.DWord);
        sub.SetValue("Name", profile.Name ?? "unnamed", RegistryValueKind.String);
        sub.SetValue("PrimaryDns", profile.PrimaryDns ?? string.Empty, RegistryValueKind.String);
        sub.SetValue("SecondaryDns", profile.SecondaryDns ?? string.Empty, RegistryValueKind.String);
        sub.SetValue("CreatedAtUtc", profile.CreatedAtUtc.ToString("o"), RegistryValueKind.String);
        sub.SetValue("LastUsedAtUtc", profile.LastUsedAtUtc?.ToString("o") ?? string.Empty, RegistryValueKind.String);
    }

    public static bool DeleteProfile(int id)
    {
        using var baseKey = Registry.CurrentUser.CreateSubKey(BaseKeyPath, true)!;
        if (baseKey.GetSubKeyNames().Contains(id.ToString()))
        {
            baseKey.DeleteSubKeyTree(id.ToString(), false);
            return true;
        }
        return false;
    }

    public static bool RenameProfile(int id, string newName)
    {
        using var baseKey = Registry.CurrentUser.CreateSubKey(BaseKeyPath, true)!;
        using var sub = baseKey.OpenSubKey(id.ToString(), true);
        if (sub == null) return false;
        sub.SetValue("Name", newName, RegistryValueKind.String);
        return true;
    }

    private static DnsProfile? ReadProfile(RegistryKey baseKey, int id)
    {
        using var sub = baseKey.OpenSubKey(id.ToString(), false);
        if (sub == null) return null;
        var p = new DnsProfile
        {
            Id = id,
            Name = (string?)sub.GetValue("Name") ?? "unnamed",
            PrimaryDns = ((string?)sub.GetValue("PrimaryDns"))?.Trim(),
            SecondaryDns = string.IsNullOrWhiteSpace((string?)sub.GetValue("SecondaryDns")) ? null : ((string)sub.GetValue("SecondaryDns")).Trim(),
            CreatedAtUtc = ParseOrDefault((string?)sub.GetValue("CreatedAtUtc")),
            LastUsedAtUtc = ParseNullable((string?)sub.GetValue("LastUsedAtUtc"))
        };
        return p;

        static DateTime ParseOrDefault(string? s) =>
            DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.MinValue;

        static DateTime? ParseNullable(string? s) =>
            DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
    }
}