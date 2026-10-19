using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Leaf.Plugins.LeafCommons;

public sealed record ShareableExclusion(string Source, string Reason);
public sealed record ShareableBuildResult(int Copied, IReadOnlyList<ShareableExclusion> Excluded);
public sealed record ProcessExecutionResult(int ExitCode, byte[] StandardOutput, string StandardError);

public interface IProcessLauncher
{
    Task<ProcessExecutionResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        string? workingDirectory = null, CancellationToken ct = default);
}

public sealed class ProcessLauncher : IProcessLauncher
{
    public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    public async Task<ProcessExecutionResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        string? workingDirectory = null, CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(fileName, arguments, workingDirectory) };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {fileName}");

        await using var stdout = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        return new(process.ExitCode, stdout.ToArray(), await stderrTask);
    }
}

public interface IFileTreeEnumerator
{
    IEnumerable<string> EnumerateDirectories(string path);
    IEnumerable<string> EnumerateFiles(string path);
}

public sealed class FileTreeEnumerator : IFileTreeEnumerator
{
    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);
    public IEnumerable<string> EnumerateFiles(string path) => Directory.EnumerateFiles(path);
}

public sealed class ShareableSetBuilder
{
    private static readonly HashSet<string> FallbackSkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "dist", ".pnpm"
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SecretScanner scanner;
    private readonly long maxFileBytes;
    private readonly IProcessLauncher processLauncher;
    private readonly IFileTreeEnumerator fileTree;
    private readonly int fallbackFileLimit;

    public ShareableSetBuilder(SecretScanner scanner, long maxFileBytes = 128 * 1024,
        IProcessLauncher? processLauncher = null, IFileTreeEnumerator? fileTree = null,
        int fallbackFileLimit = 20_000)
    {
        if (maxFileBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        if (fallbackFileLimit < 1) throw new ArgumentOutOfRangeException(nameof(fallbackFileLimit));
        this.scanner = scanner;
        this.maxFileBytes = maxFileBytes;
        this.processLauncher = processLauncher ?? new ProcessLauncher();
        this.fileTree = fileTree ?? new FileTreeEnumerator();
        this.fallbackFileLimit = fallbackFileLimit;
    }

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
                var rootName = SafeSegment(new DirectoryInfo(root).Name);
                var files = await CandidateFilesAsync(root, excluded, ct);
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (relative.Split('/').Contains(".git", StringComparer.OrdinalIgnoreCase)) { excluded.Add(new(file, "git-metadata")); continue; }
                    if (!File.Exists(file)) { excluded.Add(new(file, "missing")); continue; }
                    var info = new FileInfo(file);
                    if (HasReparsePoint(root, file)) { excluded.Add(new(file, "symlink")); continue; }
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

    private async Task<IReadOnlyList<string>> CandidateFilesAsync(string root,
        List<ShareableExclusion> excluded, CancellationToken ct)
    {
        var gitMarker = Path.Combine(root, ".git");
        return Directory.Exists(gitMarker) || File.Exists(gitMarker)
            ? await GitCandidateFilesAsync(root, ct)
            : EnumerateFallbackFiles(root, excluded, ct);
    }

    private async Task<IReadOnlyList<string>> GitCandidateFilesAsync(string root, CancellationToken ct)
    {
        var result = await processLauncher.RunAsync("git",
            ["-C", root, "ls-files", "-z", "--cached", "--others", "--exclude-standard"], ct: ct);
        if (result.ExitCode != 0)
        {
            var detail = result.StandardError.Trim();
            if (detail.Length > 500) detail = detail[..500];
            throw new InvalidOperationException($"git ls-files failed for shareable root '{root}' " +
                $"with exit code {result.ExitCode}" + (detail.Length == 0 ? "." : $": {detail}"));
        }

        string output;
        try { output = StrictUtf8.GetString(result.StandardOutput); }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException($"git ls-files returned invalid UTF-8 for shareable root '{root}'.", ex);
        }

        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => ResolveCandidate(root, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<string> EnumerateFallbackFiles(string root,
        List<ShareableExclusion> excluded, CancellationToken ct)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        var visited = 0;
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var child in fileTree.EnumerateDirectories(directory))
            {
                if (FallbackSkippedDirectories.Contains(Path.GetFileName(child))) continue;
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    excluded.Add(new(child, "symlink"));
                else pending.Push(child);
            }
            foreach (var file in fileTree.EnumerateFiles(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > fallbackFileLimit)
                    throw new InvalidOperationException($"Shareable fallback file cap of {fallbackFileLimit} " +
                        $"was exceeded for non-git root '{root}'.");
                files.Add(ResolveCandidate(root, Path.GetRelativePath(root, file)));
            }
        }
        return files;
    }

    private static string ResolveCandidate(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Invalid shareable candidate path '{relativePath}'.");
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException($"Invalid shareable candidate path '{relativePath}'.");
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Shareable candidate escapes root: '{relativePath}'.");
        return full;
    }

    private static bool HasReparsePoint(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        var current = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
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
