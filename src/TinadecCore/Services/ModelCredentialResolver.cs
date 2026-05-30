using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;

namespace TinadecCore.Services;

public sealed class ModelCredentialResolver(SecretProtector protector) : IModelCredentialResolver
{
    public string? ResolveApiKey(ResolvedModelInvocationContextDto context)
    {
        return string.IsNullOrWhiteSpace(context.EncryptedApiKey)
            ? null
            : protector.Unprotect(context.EncryptedApiKey);
    }
}
