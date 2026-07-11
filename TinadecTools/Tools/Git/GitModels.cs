using System.Text.Json.Serialization;

namespace TinadecTools.Tools.Git;

// ── refs ──────────────────────────────────────────────────────────────────────

public sealed class GitRef
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>"head" | "branch" | "tag" | "remote"</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "branch";

    [JsonPropertyName("is_head")] public bool IsHead { get; set; }
}

// ── commit summary ────────────────────────────────────────────────────────────

public sealed class GitCommitSummary
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("short_hash")] public string ShortHash { get; set; } = string.Empty;

    [JsonPropertyName("parents")] public List<string> Parents { get; set; } = new();

    [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;

    [JsonPropertyName("author_email")] public string AuthorEmail { get; set; } = string.Empty;

    [JsonPropertyName("author_date")] public string AuthorDate { get; set; } = string.Empty;

    [JsonPropertyName("committer_date")] public string CommitterDate { get; set; } = string.Empty;

    [JsonPropertyName("subject")] public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("refs")] public List<GitRef> Refs { get; set; } = new();

    /// <summary>该 commit 在 graph 中所在的 lane 索引</summary>
    [JsonPropertyName("lane_index")] public int LaneIndex { get; set; }

    /// <summary>从该 commit 指向各 parent 的 lane 连线</summary>
    [JsonPropertyName("edges")] public List<GitGraphEdge> Edges { get; set; } = new();
}

public sealed class GitGraphEdge
{
    [JsonPropertyName("parent_hash")] public string ParentHash { get; set; } = string.Empty;

    [JsonPropertyName("from_lane")] public int FromLane { get; set; }

    [JsonPropertyName("to_lane")] public int ToLane { get; set; }
}

// ── file change / patch ───────────────────────────────────────────────────────

/// <summary>单文件变更元数据（status/rename/统计），不含 patch 内容</summary>
public sealed class GitFileChange
{
    /// <summary>"A" | "D" | "M" | "R" | "C" | "T" | "U"</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;

    /// <summary>R/C 相似度分数，如 100；其余为 0</summary>
    [JsonPropertyName("score")] public int Score { get; set; }

    [JsonPropertyName("old_path")] public string? OldPath { get; set; }

    [JsonPropertyName("new_path")] public string NewPath { get; set; } = string.Empty;

    [JsonPropertyName("additions")] public int Additions { get; set; }

    [JsonPropertyName("deletions")] public int Deletions { get; set; }

    [JsonPropertyName("is_binary")] public bool IsBinary { get; set; }

    [JsonPropertyName("blob_hash")] public string? BlobHash { get; set; }

    [JsonPropertyName("byte_size")] public long? ByteSize { get; set; }
}

public sealed class GitDiffHunk
{
    [JsonPropertyName("old_start")] public int OldStart { get; set; }

    [JsonPropertyName("old_count")] public int OldCount { get; set; }

    [JsonPropertyName("new_start")] public int NewStart { get; set; }

    [JsonPropertyName("new_count")] public int NewCount { get; set; }

    [JsonPropertyName("lines")] public List<GitDiffLine> Lines { get; set; } = new();
}

public sealed class GitDiffLine
{
    /// <summary>"context" | "add" | "delete"</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "context";

    [JsonPropertyName("old_line_number")] public int? OldLineNumber { get; set; }

    [JsonPropertyName("new_line_number")] public int? NewLineNumber { get; set; }

    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}

/// <summary>单个文件的完整 patch（含 hunks）；二进制文件 hunks 为空</summary>
public sealed class GitPatchFile
{
    [JsonPropertyName("old_path")] public string OldPath { get; set; } = string.Empty;

    [JsonPropertyName("new_path")] public string NewPath { get; set; } = string.Empty;

    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;

    [JsonPropertyName("is_binary")] public bool IsBinary { get; set; }

    [JsonPropertyName("blob_hash")] public string? BlobHash { get; set; }

    [JsonPropertyName("byte_size")] public long? ByteSize { get; set; }

    [JsonPropertyName("hunks")] public List<GitDiffHunk> Hunks { get; set; } = new();
}
