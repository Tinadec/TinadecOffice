namespace TinadecTools.Tools.Git;

// ── graph lane 分配 ───────────────────────────────────────────────────────────
// 算法: 按 display order（新→旧）遍历 commits。维护 lanes 数组（hash 或 null 空槽）。
//   每个 commit 取它当前所在 lane；不在则占首个空槽或追加。
//   移除该 commit 后，第一个 parent 继承它的 lane，其余 parent 找空槽。
// 结构正确即可，不追求 JetBrains 像素级 lane 布局。

internal static class LaneAssigner
{
    public static void Assign(List<GitCommitSummary> commits)
    {
        var lanes = new List<string?>(); // hash 或 null（空槽）
        var hashToCommits = commits.ToDictionary(c => c.Hash);

        foreach (var c in commits)
        {
            var idx = IndexOf(lanes, c.Hash);
            if (idx < 0)
            {
                idx = FirstFree(lanes);
                if (idx < 0)
                {
                    idx = lanes.Count;
                    lanes.Add(null);
                }
            }

            lanes[idx] = null; // 释放该 commit 的槽
            c.LaneIndex = idx;

            // 分配 parents
            for (var i = 0; i < c.Parents.Count; i++)
            {
                var parent = c.Parents[i];
                var parentLane = IndexOf(lanes, parent);
                if (parentLane >= 0)
                {
                    // parent 已在某 lane，沿用
                }
                else if (i == 0 && lanes[idx] is null)
                {
                    // 第一个 parent 继承当前 commit 的 lane
                    lanes[idx] = parent;
                    parentLane = idx;
                }
                else
                {
                    parentLane = FirstFree(lanes);
                    if (parentLane < 0)
                    {
                        parentLane = lanes.Count;
                        lanes.Add(null);
                    }
                    lanes[parentLane] = parent;
                }

                c.Edges.Add(new GitGraphEdge
                {
                    ParentHash = parent,
                    FromLane = idx,
                    ToLane = parentLane
                });
            }
        }
    }

    private static int IndexOf(List<string?> lanes, string hash)
    {
        for (var i = 0; i < lanes.Count; i++)
            if (lanes[i] == hash)
                return i;
        return -1;
    }

    private static int FirstFree(List<string?> lanes)
    {
        for (var i = 0; i < lanes.Count; i++)
            if (lanes[i] is null)
                return i;
        return -1;
    }
}
