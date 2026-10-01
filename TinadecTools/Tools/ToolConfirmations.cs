namespace TinadecTools.Tools;

// ponytail: 传值不传 args 对象，编译期绑定，AOT 安全；helper 仅做空白校验.
internal static class ToolConfirmations
{
    /// <summary>要求具有非空 confirm_* 值；缺失或空白则抛 InvalidOperationException 通知 gateway. </summary>
    /// <remarks>
    /// 调用方传 <c>nameof(args.ConfirmCommit)</c>；报错必须用线上的 snake_case 名（confirm_commit），
    /// 否则模型被要求去填一个 schema 里不存在的字段。
    /// </remarks>
    public static void Require(string? confirmValue, string confirmField)
    {
        if (string.IsNullOrWhiteSpace(confirmValue))
        {
            var wireName = ToWireName(confirmField);
            throw new InvalidOperationException(
                $"'{wireName}' is required: set it to a short non-empty note restating what this call will do, then call again. The user still approves the call.");
        }
    }

    internal static string ToWireName(string name)
    {
        if (name.Contains('_')) return name;
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var ch = name[index];
            if (char.IsUpper(ch) && index > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }
}