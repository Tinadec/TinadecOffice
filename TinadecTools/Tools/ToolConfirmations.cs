namespace TinadecTools.Tools;

// ponytail: 传值不传 args 对象，编译期绑定，AOT 安全；helper 仅做空白校验.
internal static class ToolConfirmations
{
    /// <summary>要求具有非空 confirm_* 值；缺失或空白则抛 InvalidOperationException 通知 gateway. </summary>
    public static void Require(string? confirmValue, string confirmField)
    {
        if (string.IsNullOrWhiteSpace(confirmValue))
            throw new InvalidOperationException($"Confirmation field '{confirmField}' is required for this mutating tool.");
    }
}