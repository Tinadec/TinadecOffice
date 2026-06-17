namespace TinadecModel.Providers;

public static class ProviderTemplateRules
{
    private static readonly HashSet<string> OpenAiCompatibleDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai-compatible",
        "deepseek",
        "openrouter",
        "pollinations",
        "groq",
        "togetherai",
        "fireworks",
        "ollama",
        "vllm",
        "sglang",
        "lmstudio",
        "llamacpp"
    };

    private static readonly HashSet<string> LocalOpenAiCompatibleDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ollama",
        "vllm",
        "sglang",
        "lmstudio",
        "llamacpp"
    };

    public static bool IsOpenAiCompatibleDriver(string? driver)
    {
        return !string.IsNullOrWhiteSpace(driver) && OpenAiCompatibleDrivers.Contains(driver);
    }

    public static bool IsLocalOpenAiCompatibleDriver(string? driver)
    {
        return !string.IsNullOrWhiteSpace(driver) && LocalOpenAiCompatibleDrivers.Contains(driver);
    }

    public static bool RequiresApiKey(string? driver, string? connectionKind, IReadOnlyList<string>? capabilities = null)
    {
        if (capabilities?.Any(capability => capability.Equals("no-api-key", StringComparison.OrdinalIgnoreCase)) is true)
        {
            return false;
        }

        if (capabilities?.Any(capability => capability.Equals("api-key", StringComparison.OrdinalIgnoreCase)
            || capability.Equals("credential:api-key", StringComparison.OrdinalIgnoreCase)
            || capability.Equals("credential:api_key", StringComparison.OrdinalIgnoreCase)) is true)
        {
            return true;
        }

        var template = string.IsNullOrWhiteSpace(driver) ? null : ModelProviderCatalog.FindTemplate(driver);
        if (template is not null)
        {
            return string.Equals(template.CredentialKind, "api_key", StringComparison.OrdinalIgnoreCase)
                || string.Equals(template.CredentialKind, "api-key", StringComparison.OrdinalIgnoreCase)
                || string.Equals(template.Capabilities.CredentialKind, "api_key", StringComparison.OrdinalIgnoreCase)
                || string.Equals(template.Capabilities.CredentialKind, "api-key", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
