using Tinadec.Contracts.Models;
using TinadecModel.Abstractions;

namespace TinadecModel.Services;

public sealed class ModelCredentialResolver(SecretProtector protector) : IModelCredentialResolver
{
    public string? ResolveApiKey(ResolvedModelInvocationContextDto context)
    {
        return string.IsNullOrWhiteSpace(context.EncryptedApiKey)
            ? null
            : protector.Unprotect(context.EncryptedApiKey);
    }
}
