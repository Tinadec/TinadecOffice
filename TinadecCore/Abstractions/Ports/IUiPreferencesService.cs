using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

public interface IUiPreferencesService
{
    Task<UiPreferencesDocumentDto> GetAsync(CancellationToken ct = default);
    Task<UiPreferencesDocumentDto> PutAsync(UiPreferencesPutRequestDto request, CancellationToken ct = default);
    Task<UiPreferencesDocumentDto> PatchAsync(UiPreferencesPatchRequestDto request, CancellationToken ct = default);
    IAsyncEnumerable<UiPreferencesDocumentDto> Subscribe(CancellationToken ct = default);
}
