using Tinadec.Contracts.Models;

namespace TinadecModel.Providers;

public static class ModelProviderCatalog
{
    public static IReadOnlyList<ModelProviderTemplateDto> ListTemplates()
    {
        return
        [
            new("openai-compatible", "openai-compatible", "OpenAI Compatible", "http", "api_key", "Generic Chat Completions API.", "Contributor-visible template for OpenAI-compatible HTTP providers.", "https://api.openai.com/v1", "gpt-5.4-mini", 60, new ProviderCapabilityDto(true, true, true, true, null, false, "api_key", ProviderHealthStatus.Unknown)),
            new("anthropic", "anthropic", "Anthropic", "http", "api_key", "Anthropic Messages API.", "Contributor-visible template for Anthropic-compatible HTTP providers.", "https://api.anthropic.com/v1", "claude-sonnet-4-6", 60, new ProviderCapabilityDto(true, true, true, true, null, false, "api_key", ProviderHealthStatus.Unknown)),
            new("local-http", "local-http", "Local HTTP", "http", "none", "Local HTTP model server.", "Generic local HTTP adapter that posts to the configured endpoint without assuming a vendor protocol.", "http://localhost:8080/invoke", "default", 120, new ProviderCapabilityDto(true, false, false, true, null, false, "none", ProviderHealthStatus.Unknown)),
            new("local-http", "local-http-openai-compatible", "Local HTTP (OpenAI Compatible)", "http", "none", "Local OpenAI-compatible HTTP model server.", "Local HTTP adapter that uses OpenAI-compatible chat completions request and response mapping.", "http://localhost:8080/v1", "default", 120, new ProviderCapabilityDto(true, false, true, true, null, false, "none", ProviderHealthStatus.Unknown)),
            new("local-http", "local-http-ollama", "Local HTTP (Ollama-like)", "http", "none", "Local Ollama-like HTTP model server.", "Local HTTP adapter strategy reserved for Ollama-like local protocols without assuming OpenAI compatibility.", "http://localhost:11434/api/generate", "llama3.2", 120, new ProviderCapabilityDto(true, false, false, true, null, false, "none", ProviderHealthStatus.Unknown)),
            new("codex-cli", "codex-cli", "Codex CLI", "cli", "cli", "Codex CLI runtime instance.", "Contributor-visible template for Codex CLI workspace runs.", null, "gpt-5.4", 180, new ProviderCapabilityDto(false, true, false, true, null, true, "cli", ProviderHealthStatus.Unknown)),
            new("deepseek", "deepseek", "DeepSeek", "http", "api_key", "DeepSeek OpenAI-compatible endpoint.", "Contributor-visible template for DeepSeek HTTP providers.", "https://api.deepseek.com/v1", "deepseek-chat", 60, new ProviderCapabilityDto(true, true, true, true, null, false, "api_key", ProviderHealthStatus.Unknown)),
            new("openrouter", "openrouter", "OpenRouter", "http", "api_key", "OpenRouter model gateway.", "Contributor-visible template for OpenRouter routing providers.", "https://openrouter.ai/api/v1", "openai/gpt-5", 60, new ProviderCapabilityDto(true, true, true, true, null, false, "api_key", ProviderHealthStatus.Unknown)),
            new("pollinations", "pollinations", "Pollinations", "public-api", "none", "Public no-login OpenAI-compatible endpoint.", "Built-in unauthenticated public endpoint for free experimental model access; rate limits and availability are provider-controlled.", "https://gen.pollinations.ai/v1", "openai", 90, new ProviderCapabilityDto(true, false, false, true, null, false, "none", ProviderHealthStatus.Unknown)),
            new("groq", "groq", "Groq", "http", "api_key", "Groq OpenAI-compatible endpoint.", "Contributor-visible template for Groq HTTP providers.", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", 60, new ProviderCapabilityDto(true, true, true, true, null, false, "api_key", ProviderHealthStatus.Unknown)),
            new("togetherai", "togetherai", "Together AI", "http", "api_key", "Together OpenAI-compatible endpoint.", "Contributor-visible template for Together AI HTTP providers.", "https://api.together.xyz/v1", "meta-llama/Llama-3.3-70B-Instruct-Turbo", 60, new ProviderCapabilityDto(true, true, true, true, null, false, "api_key", ProviderHealthStatus.Unknown)),
            new("fireworks", "fireworks", "Fireworks AI", "http", "api_key", "Fireworks OpenAI-compatible endpoint.", "Contributor-visible template for Fireworks HTTP providers.", "https://api.fireworks.ai/inference/v1", "accounts/fireworks/models/deepseek-v3", 60, new ProviderCapabilityDto(true, true, false, true, null, false, "api_key", ProviderHealthStatus.Unknown)),
            new("ollama", "ollama", "Ollama", "local-server", "none", "Local OpenAI-compatible Ollama server.", "Contributor-visible template for Ollama local providers.", "http://localhost:11434/v1", "llama3.2", 120, new ProviderCapabilityDto(true, true, false, true, null, false, "none", ProviderHealthStatus.Unknown)),
            new("vllm", "vllm", "vLLM", "local-server", "none", "Local or remote vLLM OpenAI-compatible server.", "Contributor-visible template for vLLM local providers.", "http://localhost:8000/v1", "default", 120, new ProviderCapabilityDto(true, true, false, true, null, false, "none", ProviderHealthStatus.Unknown)),
            new("sglang", "sglang", "SGLang", "local-server", "none", "Local or remote SGLang OpenAI-compatible server.", "Contributor-visible template for SGLang local providers.", "http://localhost:30000/v1", "default", 120, new ProviderCapabilityDto(true, true, false, true, null, false, "none", ProviderHealthStatus.Unknown)),
            new("lmstudio", "lmstudio", "LM Studio", "local-server", "none", "Local OpenAI-compatible LM Studio server.", "Contributor-visible template for LM Studio local providers.", "http://localhost:1234/v1", "default", 120, new ProviderCapabilityDto(true, true, false, true, null, false, "none", ProviderHealthStatus.Unknown)),
            new("llamacpp", "llamacpp", "llama.cpp", "local-server", "none", "Local OpenAI-compatible llama.cpp server.", "Contributor-visible template for llama.cpp local providers.", "http://localhost:8080/v1", "default", 120, new ProviderCapabilityDto(true, true, false, true, null, false, "none", ProviderHealthStatus.Unknown)),
            new("claude-cli", "claude-cli", "Claude CLI", "cli", "cli", "Claude CLI runtime instance.", "Contributor-visible template for Claude CLI workspace runs.", null, "claude-sonnet-4-6", 180, new ProviderCapabilityDto(false, true, false, true, null, true, "cli", ProviderHealthStatus.Unknown)),
            new("cursor-acp", "cursor-acp", "Cursor ACP", "cli", "cli", "Cursor agent ACP runtime.", "Contributor-visible template for Cursor ACP workspace runs.", null, "auto", 180, new ProviderCapabilityDto(false, true, false, true, null, true, "cli", ProviderHealthStatus.Unknown)),
            new("opencode", "opencode", "OpenCode", "cli", "cli", "OpenCode CLI or server runtime.", "Contributor-visible template for OpenCode workspace and server runs.", "http://127.0.0.1:4096", "openai/gpt-5", 180, new ProviderCapabilityDto(true, true, false, true, null, true, "cli", ProviderHealthStatus.Unknown))
        ];
    }

    public static ModelProviderTemplateDto? FindTemplate(string driver)
    {
        return ListTemplates().FirstOrDefault(template => template.Driver.Equals(driver, StringComparison.OrdinalIgnoreCase));
    }
}
