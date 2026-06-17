using Tinadec.Contracts.Models;

namespace TinadecModel.Storage;

public sealed record StoredModelSettings(
    string BaseUrl,
    string Model,
    string? EncryptedApiKey,
    DateTimeOffset UpdatedAt)
{
    public ModelSettingsDto ToDto()
    {
        return new ModelSettingsDto(BaseUrl, Model, !string.IsNullOrWhiteSpace(EncryptedApiKey), UpdatedAt);
    }
}
