using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Leaf.Plugins.LeafCommons;

public sealed record ShareableExclusion(string Source, string Reason);
public sealed record ShareableBuildResult(int Copied, IReadOnlyList<ShareableExclusion> Excluded);

public sealed class ShareableSetBuilder(SecretScanner scanner, long maxFileBytes = 128 * 1024)
{
    public async Task<ShareableBuildResult> BuildAsync(IEnumerable<string> selectedFolders, string destination, CancellationToken ct = default)
    {
        var staging = destination + ".building-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        var excluded = new List<ShareableExclusion>();
        var copied = 0;
        try
        {
            foreach (var rawRoot in selectedFolders)
            {
                var root = Path.GetFullPath(rawRoot);
                if (!Directory.Exists(root)) { excluded.Add(new(root, "missing")); continue; }
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) { excluded.Add(new(root, "symlink")); continue; }
                var ignored = await ReadGitIgnoredAsync(root, ct);
                var rootName = SafeSegment(new DirectoryInfo(root).Name);
                foreach (var file in EnumerateSafeFiles(root, excluded))
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (relative.Split('/').Contains(".git", StringComparer.OrdinalIgnoreCase)) { excluded.Add(new(file, "git-metadata")); continue; }
                    if (ignored.Contains(relative)) { excluded.Add(new(file, "git-ignored")); continue; }
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { excluded.Add(new(file, "symlink")); continue; }
                    if (info.Length > maxFileBytes) { excluded.Add(new(file, "too-large")); continue; }
                    var bytes = await File.ReadAllBytesAsync(file, ct);
                    if (IsBinary(bytes)) { excluded.Add(new(file, "binary")); continue; }
                    var text = Encoding.UTF8.GetString(bytes);
                    var hits = scanner.Scan(text);
                    if (hits.Count > 0) { excluded.Add(new(file, "secret:" + hits[0].Rule)); continue; }
                    var target = Path.Combine(staging, rootName, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllTextAsync(target, text, new UTF8Encoding(false), ct);
                    copied++;
                }
            }
            if (Directory.Exists(destination)) Directory.Move(destination, destination + ".old-" + Guid.NewGuid().ToString("N"));
            Directory.Move(staging, destination);
            foreach (var old in Directory.GetDirectories(Path.GetDirectoryName(destination)!, Path.GetFileName(destination) + ".old-*")) Directory.Delete(old, true);
            await File.WriteAllTextAsync(destination + ".manifest.json", JsonSerializer.Serialize(new { copied, excluded }), new UTF8Encoding(false), ct);
            return new(copied, excluded);
        }
        catch { if (Directory.Exists(staging)) Directory.Delete(staging, true); throw; }
    }

    private static bool IsBinary(byte[] data) => data.Take(Math.Min(data.Length, 8192)).Any(b => b == 0) || !IsUtf8(data);
    private static bool IsUtf8(byte[] data) { try { _ = new UTF8Encoding(false, true).GetString(data); return true; } catch (DecoderFallbackException) { return false; } }
    private static string SafeSegment(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static IEnumerable<string> EnumerateSafeFiles(string root, List<ShareableExclusion> excluded)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) excluded.Add(new(child, "symlink"));
                else pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory)) yield return file;
        }
    }

    private static async Task<HashSet<string>> ReadGitIgnoredAsync(string root, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(Path.Combine(root, ".git"))) return result;
        foreach (var file in EnumerateSafeFiles(root, []))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Split('/').Contains(".git", StringComparer.OrdinalIgnoreCase)) continue;
            var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("check-ignore"); psi.ArgumentList.Add("--quiet"); psi.ArgumentList.Add("--no-index"); psi.ArgumentList.Add("--"); psi.ArgumentList.Add(rel);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git check-ignore");
            await process.WaitForExitAsync(ct);
            if (process.ExitCode == 0) result.Add(rel);
            else if (process.ExitCode != 1) throw new InvalidOperationException("git check-ignore failed: " + await process.StandardError.ReadToEndAsync(ct));
        }
        return result;
    }
}

public sealed class ShareableStore
{
    private readonly string root;
    public ShareableStore(string root) => this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    public string RootPath => root.TrimEnd(Path.DirectorySeparatorChar);

    public async Task<string> ReadAsync(string relativePath, CancellationToken ct = default)
    {
        var full = Resolve(relativePath);
        if (!File.Exists(full)) throw new FileNotFoundException("Shareable file not found");
        RejectReparseChain(full);
        if (new FileInfo(full).Length > 128 * 1024) throw new InvalidDataException("Shareable file exceeds the read limit");
        return await File.ReadAllTextAsync(full, ct);
    }

    public async Task<IReadOnlyList<string>> SearchAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new ArgumentException("Invalid query");
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        foreach (var file in EnumerateFilesWithoutReparse(root))
        {
            RejectReparseChain(file);
            var text = await File.ReadAllTextAsync(file, ct);
            if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) found.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            if (found.Count >= limit) break;
        }
        return found;
    }

    private static IEnumerable<string> EnumerateFilesWithoutReparse(string start)
    {
        var pending = new Stack<string>(); pending.Push(start);
        while (pending.TryPop(out var directory))
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            foreach (var file in Directory.EnumerateFiles(directory))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
        }
    }

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new UnauthorizedAccessException("Absolute paths are forbidden");
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(x => x == "..")) throw new UnauthorizedAccessException("Path traversal is forbidden");
        var full = Path.GetFullPath(Path.Combine(root, normalized));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Path escapes shareable copy");
        return full;
    }

    private void RejectReparseChain(string full)
    {
        for (var current = full; current.StartsWith(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current) ?? "")
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Symlinks are forbidden");
            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
        }
    }
}
