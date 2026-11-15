using System.Security.AccessControl;
using System.Security.Principal;
namespace EchoType.Windows;

public sealed class AgentPaths
{
    public string Root { get; }
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoType");
    public static string LegacyRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopAgent");
    public AgentPaths(string? root = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? DefaultRoot));
        var relative = Path.GetRelativePath(LegacyRoot, Root);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith("..\\", StringComparison.Ordinal))
            throw new InvalidOperationException("EchoType の保存先に DesktopAgent のフォルダーを指定できません。");
        var parent = Root;
        while (!Directory.Exists(parent) && !File.Exists(parent))
            parent = Path.GetDirectoryName(parent) ?? throw new DirectoryNotFoundException();
        RequireLocalPath(parent, true);
        Directory.CreateDirectory(Root);
        RequireLocalPath(Root, true);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(security);
        foreach (var child in new[] { "data", "control", "cache" })
        {
            var folder = Path.Combine(Root, child);
            Directory.CreateDirectory(folder);
            RequireLocalPath(folder, true);
        }
    }
    public static void RequireLocalPath(string path, bool directory = false)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
            throw new InvalidOperationException("ローカルの絶対パスを指定してください。");
        if (directory ? !Directory.Exists(path) : !File.Exists(path)) throw new FileNotFoundException("登録した対象が見つかりません。", path);
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("リンク経由の実行先は登録できません。");
            current = Path.GetDirectoryName(current);
        }
    }
}
