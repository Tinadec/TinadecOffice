namespace Tinadec.Contracts.Security;

public enum PermissionMode
{
    Observe,
    Approval,
    Trusted
}

public enum ToolRisk
{
    ReadOnly,
    WriteFile,
    Shell,
    GitWrite,
    ExternalUrl
}

public sealed record ApprovalRequirement(bool Required, string Reason);

public static class PermissionPolicy
{
    public static ApprovalRequirement Evaluate(PermissionMode mode, ToolRisk risk)
    {
        if (mode == PermissionMode.Observe)
        {
            return risk == ToolRisk.ReadOnly
                ? new ApprovalRequirement(false, "Read-only tools are allowed in observe mode.")
                : new ApprovalRequirement(true, "Observe mode blocks mutating or external tool use.");
        }

        if (mode == PermissionMode.Trusted)
        {
            return risk == ToolRisk.ExternalUrl
                ? new ApprovalRequirement(true, "External URLs still need a human checkpoint.")
                : new ApprovalRequirement(false, "Trusted mode allows workspace-scoped tool use.");
        }

        return risk == ToolRisk.ReadOnly
            ? new ApprovalRequirement(false, "Read-only tools do not need approval.")
            : new ApprovalRequirement(true, "Approval mode requires a human checkpoint for this tool.");
    }
}
