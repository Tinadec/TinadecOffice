using System.Text;

namespace TinadecTools.Tools.Git;

internal sealed record GitPatchLoadResult(
    List<GitPatchFile>? Patches,
    bool Truncated,
    string? TruncationReason,
    int CapturedBytes);

internal static class GitPatchLoader
{
    public static async Task<GitPatchLoadResult> LoadAsync(
        string repo,
        IReadOnlyList<string> gitArguments,
        int maxPatchBytes,
        CancellationToken cancellationToken)
    {
        var output = await GitCli.RunAsync(
            repo,
            gitArguments,
            stdin: null,
            cancellationToken,
            timeoutMs: 30_000,
            // UTF-8 output uses at least one byte per character, so this cap cannot exceed
            // the requested byte budget before the exact byte count is checked below.
            maxOutputChars: maxPatchBytes).ConfigureAwait(false);

        if (output.Truncated)
            return new GitPatchLoadResult(null, true, "patch_output_limit", Encoding.UTF8.GetByteCount(output.Stdout));

        if (!output.Ok)
            return new GitPatchLoadResult(null, false, null, 0);

        var capturedBytes = Encoding.UTF8.GetByteCount(output.Stdout);
        if (capturedBytes > maxPatchBytes)
            return new GitPatchLoadResult(null, true, "patch_output_limit", capturedBytes);

        return new GitPatchLoadResult(DiffParser.ParsePatch(output.Stdout), false, null, capturedBytes);
    }
}
