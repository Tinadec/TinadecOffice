using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using TinadecCore.Contracts.Dtos;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Api.Tests;

public sealed class UiPreferencesTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("ui-preferences");
    private readonly string _token = Guid.NewGuid().ToString("N");
    private string? _previousToken;
    private ManagedPreferencesFactory? _factory;

    public Task InitializeAsync()
    {
        _previousToken = Environment.GetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN");
        Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", _token);
        Directory.CreateDirectory(_root);
        _factory = new ManagedPreferencesFactory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_factory is not null)
                await _factory.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", _previousToken);
        }
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tinadec-Host-Control", _token);
        return client;
    }

    [Fact]
    public async Task GetPutPatch_TomlRoundTrip_Aliases_ResetDefaults_AndAtomicWrite()
    {
        using var client = CreateAuthorizedClient();
        var expectedPath = Path.Combine(_root, "preferences.toml");

        // 1. Initial GET returns canonical defaults without mutating disk.
        var initialResp = await client.GetAsync("/api/v1/preferences");
        Assert.Equal(HttpStatusCode.OK, initialResp.StatusCode);
        var initial = await initialResp.Content.ReadFromJsonAsync<UiPreferencesDocumentDto>();
        Assert.NotNull(initial);
        Assert.Equal(1, initial!.Version);
        Assert.Equal(expectedPath, initial.FilePath);
        Assert.Matches("^sha256:[0-9a-f]{64}$", initial.ContentHash);
        Assert.Equal("dark", initial.Appearance.Theme);
        Assert.Equal("monochrome", initial.Appearance.Accent);
        Assert.Equal("#18181b", initial.Appearance.CustomAccentHex);
        Assert.Equal("translucent", initial.Appearance.PanelEffect);
        Assert.Equal("translucent", initial.Appearance.PanelPreset);
        Assert.Equal(76, initial.Appearance.PanelOpacity);
        Assert.Equal(0, initial.Appearance.PanelBlur);
        Assert.Equal(100, initial.Appearance.PanelSaturation);
        Assert.Equal("grid", initial.Appearance.WorkspaceView);
        Assert.Equal("amber", initial.Appearance.WorkspaceFolderTone);
        Assert.Equal("link", initial.Interaction.WorkspaceDropAction);
        Assert.Equal("merge", initial.Interaction.SpatialPromoteMode);
        Assert.Equal("system", initial.Interaction.ReducedMotion);
        Assert.False(File.Exists(expectedPath));

        // 2. PUT normalizes aliases ("opaque" -> "solid", "parent_with_children" -> "parent_children", uppercase hex -> lowercase),
        //    derives canonical preset/material parameters when legacy callers omit the 4 new fields,
        //    and writes deterministic TOML atomically with no leftover temp files.
        var putResp = await client.PutAsJsonAsync("/api/v1/preferences", new
        {
            expected_content_hash = initial.ContentHash,
            appearance = new
            {
                theme = "light",
                accent = "custom",
                custom_accent_hex = "#FF5500",
                panel_effect = "opaque",
                workspace_view = "list",
                workspace_folder_tone = "teal",
            },
            interaction = new
            {
                workspace_drop_action = "ask",
                spatial_promote_mode = "parent_with_children",
                reduced_motion = "on",
            },
        });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var afterPut = await putResp.Content.ReadFromJsonAsync<UiPreferencesDocumentDto>();
        Assert.NotNull(afterPut);
        Assert.Equal("#ff5500", afterPut!.Appearance.CustomAccentHex);
        Assert.Equal("solid", afterPut.Appearance.PanelEffect);
        Assert.Equal("solid", afterPut.Appearance.PanelPreset);
        Assert.Equal(100, afterPut.Appearance.PanelOpacity);
        Assert.Equal(0, afterPut.Appearance.PanelBlur);
        Assert.Equal(100, afterPut.Appearance.PanelSaturation);
        Assert.Equal("parent_children", afterPut.Interaction.SpatialPromoteMode);
        Assert.NotEqual(initial.ContentHash, afterPut.ContentHash);
        Assert.True(File.Exists(expectedPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));

        var tomlText = await File.ReadAllTextAsync(expectedPath, Encoding.UTF8);
        var parsedToml = TomlSerializer.Deserialize<TomlTable>(tomlText)!;
        Assert.Equal(1L, parsedToml["version"]);
        var appTable = Assert.IsType<TomlTable>(parsedToml["appearance"]);
        var intTable = Assert.IsType<TomlTable>(parsedToml["interaction"]);
        Assert.Equal("light", appTable["theme"]);
        Assert.Equal("#ff5500", appTable["custom_accent_hex"]);
        Assert.Equal("solid", appTable["panel_effect"]);
        Assert.Equal("solid", appTable["panel_preset"]);
        Assert.Equal(100L, appTable["panel_opacity"]);
        Assert.Equal(0L, appTable["panel_blur"]);
        Assert.Equal(100L, appTable["panel_saturation"]);
        Assert.Equal("teal", appTable["workspace_folder_tone"]);
        Assert.Equal("ask", intTable["workspace_drop_action"]);
        Assert.Equal("parent_children", intTable["spatial_promote_mode"]);
        Assert.Equal("on", intTable["reduced_motion"]);

        // 3. PATCH merges partial fields (including preset expansion and custom material sliders) while preserving untouched values.
        using var patchReq = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/preferences")
        {
            Content = JsonContent.Create(new
            {
                expected_content_hash = afterPut.ContentHash,
                appearance = new { workspace_folder_tone = "coral", panel_preset = "frosted" },
                interaction = new { spatial_promote_mode = "split_many" },
            }),
        };
        var patchResp = await client.SendAsync(patchReq);
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var afterPatch = await patchResp.Content.ReadFromJsonAsync<UiPreferencesDocumentDto>();
        Assert.NotNull(afterPatch);
        Assert.Equal("light", afterPatch!.Appearance.Theme);
        Assert.Equal("#ff5500", afterPatch.Appearance.CustomAccentHex);
        Assert.Equal("blur", afterPatch.Appearance.PanelEffect);
        Assert.Equal("frosted", afterPatch.Appearance.PanelPreset);
        Assert.Equal(68, afterPatch.Appearance.PanelOpacity);
        Assert.Equal(18, afterPatch.Appearance.PanelBlur);
        Assert.Equal(160, afterPatch.Appearance.PanelSaturation);
        Assert.Equal("coral", afterPatch.Appearance.WorkspaceFolderTone);
        Assert.Equal("ask", afterPatch.Interaction.WorkspaceDropAction);
        Assert.Equal("split", afterPatch.Interaction.SpatialPromoteMode);

        // 3b. Full PUT with explicit custom material parameters (opacity 0..100, blur 0..32, saturation 100..200) round-trips cleanly.
        var customPutResp = await client.PutAsJsonAsync("/api/v1/preferences", new
        {
            expected_content_hash = afterPatch.ContentHash,
            appearance = new
            {
                theme = "dark",
                accent = "monochrome",
                custom_accent_hex = "#18181b",
                panel_effect = "blur",
                panel_preset = "custom",
                panel_opacity = 42,
                panel_blur = 32,
                panel_saturation = 195,
                workspace_view = "grid",
                workspace_folder_tone = "sky",
            },
            interaction = new
            {
                workspace_drop_action = "link",
                spatial_promote_mode = "merge",
                reduced_motion = "system",
            },
        });
        Assert.Equal(HttpStatusCode.OK, customPutResp.StatusCode);
        var afterCustomPut = await customPutResp.Content.ReadFromJsonAsync<UiPreferencesDocumentDto>();
        Assert.NotNull(afterCustomPut);
        Assert.Equal("custom", afterCustomPut!.Appearance.PanelPreset);
        Assert.Equal("blur", afterCustomPut.Appearance.PanelEffect);
        Assert.Equal(42, afterCustomPut.Appearance.PanelOpacity);
        Assert.Equal(32, afterCustomPut.Appearance.PanelBlur);
        Assert.Equal(195, afterCustomPut.Appearance.PanelSaturation);

        // 4. PUT reset_defaults restores canonical defaults and deterministic hash.
        var resetResp = await client.PutAsJsonAsync("/api/v1/preferences", new
        {
            expected_content_hash = afterCustomPut.ContentHash,
            reset_defaults = true,
        });
        Assert.Equal(HttpStatusCode.OK, resetResp.StatusCode);
        var afterReset = await resetResp.Content.ReadFromJsonAsync<UiPreferencesDocumentDto>();
        Assert.NotNull(afterReset);
        Assert.Equal(initial.ContentHash, afterReset!.ContentHash);
        Assert.Equal("dark", afterReset.Appearance.Theme);
        Assert.Equal("amber", afterReset.Appearance.WorkspaceFolderTone);
        Assert.Equal("link", afterReset.Interaction.WorkspaceDropAction);
        Assert.Equal("merge", afterReset.Interaction.SpatialPromoteMode);
    }

    [Fact]
    public async Task PutAndPatch_FailClosedOnHashConflict_MissingSections_UnknownKeys_AndInvalidEnums()
    {
        using var client = CreateAuthorizedClient();
        var current = await client.GetFromJsonAsync<UiPreferencesDocumentDto>("/api/v1/preferences");
        Assert.NotNull(current);

        // Hash conflict -> 412 preferences_conflict
        var conflictPut = await client.PutAsJsonAsync("/api/v1/preferences", new
        {
            expected_content_hash = "sha256:" + new string('0', 64),
            reset_defaults = true,
        });
        Assert.Equal(HttpStatusCode.PreconditionFailed, conflictPut.StatusCode);
        var conflictBody = await conflictPut.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("preferences_conflict", conflictBody.GetProperty("code").GetString());

        using var conflictPatchReq = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/preferences")
        {
            Content = JsonContent.Create(new
            {
                expected_content_hash = "sha256:" + new string('f', 64),
                appearance = new { theme = "light" },
            }),
        };
        var conflictPatch = await client.SendAsync(conflictPatchReq);
        Assert.Equal(HttpStatusCode.PreconditionFailed, conflictPatch.StatusCode);

        // Missing interaction section on PUT without reset_defaults -> 400
        var missingSectionResp = await client.PutAsJsonAsync("/api/v1/preferences", new
        {
            appearance = new
            {
                theme = "dark",
                accent = "monochrome",
                custom_accent_hex = "#18181b",
                panel_effect = "translucent",
                workspace_view = "grid",
                workspace_folder_tone = "amber",
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingSectionResp.StatusCode);

        // Unknown root property -> 400
        using var unknownRootReq = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/preferences")
        {
            Content = new StringContent("{\"appearance\":{\"theme\":\"light\"},\"unexpected\":true}", Encoding.UTF8, "application/json"),
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(unknownRootReq)).StatusCode);

        // Unknown nested property -> 400
        using var unknownNestedReq = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/preferences")
        {
            Content = new StringContent("{\"appearance\":{\"theme\":\"light\",\"extra_field\":\"x\"}}", Encoding.UTF8, "application/json"),
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(unknownNestedReq)).StatusCode);

        // Invalid enum & invalid hex -> 400
        foreach (var badPayload in new[]
        {
            "{\"appearance\":{\"theme\":\"neon\"}}",
            "{\"appearance\":{\"custom_accent_hex\":\"18181b\"}}",
            "{\"appearance\":{\"custom_accent_hex\":\"#fff\"}}",
            "{\"appearance\":{\"panel_preset\":\"glass\"}}",
            "{\"appearance\":{\"panel_opacity\":-1}}",
            "{\"appearance\":{\"panel_opacity\":101}}",
            "{\"appearance\":{\"panel_blur\":-1}}",
            "{\"appearance\":{\"panel_blur\":33}}",
            "{\"appearance\":{\"panel_saturation\":99}}",
            "{\"appearance\":{\"panel_saturation\":201}}",
            "{\"appearance\":{\"workspace_folder_tone\":\"gold\"}}",
            "{\"interaction\":{\"workspace_drop_action\":\"move\"}}",
            "{\"interaction\":{\"spatial_promote_mode\":\"invalid_mode\"}}",
            "{\"interaction\":{\"reduced_motion\":\"sometimes\"}}",
        })
        {
            using var req = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/preferences")
            {
                Content = new StringContent(badPayload, Encoding.UTF8, "application/json"),
            };
            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
    }

    [Fact]
    public async Task SseStream_EmitsImmediateSnapshot_ApiUpdates_AndExternalTomlHotReload_IgnoringMalformedEdits()
    {
        using var sseClient = CreateAuthorizedClient();
        using var apiClient = CreateAuthorizedClient();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var sseReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/preferences/events");
        using var sseResp = await sseClient.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);
        Assert.Equal("text/event-stream", sseResp.Content.Headers.ContentType?.MediaType);

        await using var stream = await sseResp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // 1. Immediate snapshot on connect
        var firstEvent = await ReadNextSseEventAsync(reader, cts.Token);
        Assert.Equal("preferences.updated", firstEvent.EventName);
        Assert.Equal("dark", firstEvent.Payload.Appearance.Theme);
        Assert.Equal("amber", firstEvent.Payload.Appearance.WorkspaceFolderTone);

        // 2. PATCH pushes live update over SSE
        using var patchReq = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/preferences")
        {
            Content = JsonContent.Create(new
            {
                appearance = new { workspace_folder_tone = "sky", workspace_view = "list" },
            }),
        };
        var patchResp = await apiClient.SendAsync(patchReq, cts.Token);
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);

        var secondEvent = await ReadNextSseEventAsync(reader, cts.Token);
        Assert.Equal("preferences.updated", secondEvent.EventName);
        Assert.Equal("sky", secondEvent.Payload.Appearance.WorkspaceFolderTone);
        Assert.Equal("list", secondEvent.Payload.Appearance.WorkspaceView);

        // 3. Transient malformed external edit is ignored without crashing or emitting corrupt state;
        //    subsequent valid external TOML edit triggers hot-reload SSE broadcast.
        var filePath = Path.Combine(_root, "preferences.toml");
        await File.WriteAllTextAsync(filePath, "version = 1\n[appearance\ntheme = \"broken", Encoding.UTF8, cts.Token);
        await Task.Delay(220, cts.Token);

        var stillValid = await apiClient.GetFromJsonAsync<UiPreferencesDocumentDto>("/api/v1/preferences", cts.Token);
        Assert.NotNull(stillValid);
        Assert.Equal("sky", stillValid!.Appearance.WorkspaceFolderTone);

        var validExternalToml = """
            version = 1

            [appearance]
            theme = "light"
            accent = "emerald_invalid_check"
            """;
        await File.WriteAllTextAsync(filePath, validExternalToml, Encoding.UTF8, cts.Token);
        await Task.Delay(200, cts.Token);

        var finalExternalToml = """
            version = 1

            [appearance]
            theme = "light"
            accent = "green"
            custom_accent_hex = "#18181b"
            panel_effect = "blur"
            workspace_view = "grid"
            workspace_folder_tone = "frost"

            [interaction]
            workspace_drop_action = "copy"
            spatial_promote_mode = "merge_single"
            reduced_motion = "off"
            """;
        await File.WriteAllTextAsync(filePath, finalExternalToml, Encoding.UTF8, cts.Token);

        var thirdEvent = await ReadNextSseEventAsync(reader, cts.Token);
        Assert.Equal("preferences.updated", thirdEvent.EventName);
        Assert.Equal("light", thirdEvent.Payload.Appearance.Theme);
        Assert.Equal("green", thirdEvent.Payload.Appearance.Accent);
        Assert.Equal("blur", thirdEvent.Payload.Appearance.PanelEffect);
        Assert.Equal("frosted", thirdEvent.Payload.Appearance.PanelPreset);
        Assert.Equal(68, thirdEvent.Payload.Appearance.PanelOpacity);
        Assert.Equal(18, thirdEvent.Payload.Appearance.PanelBlur);
        Assert.Equal(160, thirdEvent.Payload.Appearance.PanelSaturation);
        Assert.Equal("frost", thirdEvent.Payload.Appearance.WorkspaceFolderTone);
        Assert.Equal("copy", thirdEvent.Payload.Interaction.WorkspaceDropAction);
        Assert.Equal("merge", thirdEvent.Payload.Interaction.SpatialPromoteMode);
        Assert.Equal("off", thirdEvent.Payload.Interaction.ReducedMotion);
    }

    [Fact]
    public async Task McpServer_ExposesUiPreferencesResource_InspectTarget_AndStrictSelfConfigTool()
    {
        using var client = CreateAuthorizedClient();

        // 1. resources/list includes tinadec://ui-preferences
        var listResResp = await PostMcpAsync(client, "resources/list", new { });
        var resources = listResResp.GetProperty("result").GetProperty("resources").EnumerateArray().ToArray();
        Assert.Contains(resources, r => r.GetProperty("uri").GetString() == "tinadec://ui-preferences");

        // 2. resources/read reads tinadec://ui-preferences
        var readResResp = await PostMcpAsync(client, "resources/read", new { uri = "tinadec://ui-preferences" });
        var readText = readResResp.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!;
        var initialDoc = JsonSerializer.Deserialize<UiPreferencesDocumentDto>(readText)!;
        Assert.Equal(1, initialDoc.Version);
        Assert.Equal("amber", initialDoc.Appearance.WorkspaceFolderTone);

        // 3. tools/list includes tinadec_update_ui_preferences with additionalProperties=false on root, appearance, interaction
        var listToolsResp = await PostMcpAsync(client, "tools/list", new { });
        var updateTool = listToolsResp.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "tinadec_update_ui_preferences");
        var schema = updateTool.GetProperty("inputSchema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(schema.GetProperty("properties").GetProperty("appearance").GetProperty("additionalProperties").GetBoolean());
        Assert.False(schema.GetProperty("properties").GetProperty("interaction").GetProperty("additionalProperties").GetBoolean());

        // 4. tools/call tinadec_update_ui_preferences applies valid partial update and returns UiPreferencesDocumentDto
        var callOkResp = await PostMcpAsync(client, "tools/call", new
        {
            name = "tinadec_update_ui_preferences",
            arguments = new
            {
                expected_content_hash = initialDoc.ContentHash,
                appearance = new
                {
                    workspace_folder_tone = "teal",
                    accent = "cyan",
                    panel_preset = "crystal",
                },
                interaction = new { workspace_drop_action = "copy", spatial_promote_mode = "split_many" },
            },
        });
        var callOkResult = callOkResp.GetProperty("result");
        Assert.False(callOkResult.GetProperty("isError").GetBoolean());
        var updatedDoc = callOkResult.GetProperty("structuredContent").Deserialize<UiPreferencesDocumentDto>()!;
        Assert.Equal("teal", updatedDoc.Appearance.WorkspaceFolderTone);
        Assert.Equal("cyan", updatedDoc.Appearance.Accent);
        Assert.Equal("blur", updatedDoc.Appearance.PanelEffect);
        Assert.Equal("crystal", updatedDoc.Appearance.PanelPreset);
        Assert.Equal(48, updatedDoc.Appearance.PanelOpacity);
        Assert.Equal(24, updatedDoc.Appearance.PanelBlur);
        Assert.Equal(180, updatedDoc.Appearance.PanelSaturation);
        Assert.Equal("copy", updatedDoc.Interaction.WorkspaceDropAction);
        Assert.Equal("split", updatedDoc.Interaction.SpatialPromoteMode);

        // 4b. tools/call tinadec_update_ui_preferences supports custom integer sliders (opacity, blur, saturation)
        var callCustomResp = await PostMcpAsync(client, "tools/call", new
        {
            name = "tinadec_update_ui_preferences",
            arguments = new
            {
                expected_content_hash = updatedDoc.ContentHash,
                appearance = new
                {
                    panel_preset = "custom",
                    panel_effect = "blur",
                    panel_opacity = 55,
                    panel_blur = 28,
                    panel_saturation = 175,
                },
            },
        });
        Assert.False(callCustomResp.GetProperty("result").GetProperty("isError").GetBoolean());
        updatedDoc = callCustomResp.GetProperty("result").GetProperty("structuredContent").Deserialize<UiPreferencesDocumentDto>()!;
        Assert.Equal("custom", updatedDoc.Appearance.PanelPreset);
        Assert.Equal(55, updatedDoc.Appearance.PanelOpacity);
        Assert.Equal(28, updatedDoc.Appearance.PanelBlur);
        Assert.Equal(175, updatedDoc.Appearance.PanelSaturation);

        // 5. tinadec_inspect target=ui_preferences returns the updated document
        var inspectResp = await PostMcpAsync(client, "tools/call", new
        {
            name = "tinadec_inspect",
            arguments = new { target = "ui_preferences" },
        });
        Assert.False(inspectResp.GetProperty("result").GetProperty("isError").GetBoolean());
        var inspectedDoc = inspectResp.GetProperty("result").GetProperty("structuredContent").Deserialize<UiPreferencesDocumentDto>()!;
        Assert.Equal(updatedDoc.ContentHash, inspectedDoc.ContentHash);
        Assert.Equal("teal", inspectedDoc.Appearance.WorkspaceFolderTone);
        Assert.Equal("custom", inspectedDoc.Appearance.PanelPreset);
        Assert.Equal(28, inspectedDoc.Appearance.PanelBlur);

        // 6. Fail-closed on unknown keys, invalid enums, out-of-range integers, and stale expected_content_hash
        foreach (var invalidArgs in new object[]
        {
            new { unknown_root = 1 },
            new { appearance = new { bogus_key = "dark" } },
            new { appearance = new { theme = "neon" } },
            new { appearance = new { panel_preset = "invalid_preset" } },
            new { appearance = new { panel_opacity = 101 } },
            new { appearance = new { panel_blur = 33 } },
            new { appearance = new { panel_saturation = 99 } },
            new { appearance = new { panel_blur = "18" } },
            new { interaction = new { workspace_drop_action = "teleport" } },
            new { expected_content_hash = initialDoc.ContentHash, appearance = new { theme = "light" } },
        })
        {
            var errResp = await PostMcpAsync(client, "tools/call", new
            {
                name = "tinadec_update_ui_preferences",
                arguments = invalidArgs,
            });
            Assert.True(errResp.GetProperty("result").GetProperty("isError").GetBoolean());
        }
    }

    [Fact]
    public async Task ScopelessFallbackHost_WorksCleanlyWithoutStorageScopeRegistry()
    {
        var fallbackRoot = ApiTestStorage.CreateRoot("ui-prefs-scopeless");
        try
        {
            await using var factory = new ScopelessPreferencesFactory(fallbackRoot);
            using var client = factory.CreateClient();

            var initial = await client.GetFromJsonAsync<UiPreferencesDocumentDto>("/api/v1/preferences");
            Assert.NotNull(initial);
            Assert.Equal(Path.Combine(fallbackRoot, "preferences.toml"), initial!.FilePath);

            using var patchReq = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/preferences")
            {
                Content = JsonContent.Create(new { appearance = new { workspace_folder_tone = "coral" } }),
            };
            var patchResp = await client.SendAsync(patchReq);
            Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
            Assert.True(File.Exists(Path.Combine(fallbackRoot, "preferences.toml")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(fallbackRoot))
                Directory.Delete(fallbackRoot, recursive: true);
        }
    }

    private static async Task<JsonElement> PostMcpAsync(HttpClient client, string method, object @params)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/mcp/tinadec", new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params,
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<(string EventName, UiPreferencesDocumentDto Payload)> ReadNextSseEventAsync(
        StreamReader reader,
        CancellationToken ct)
    {
        string? eventName = null;
        var dataBuilder = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            Assert.NotNull(line);

            if (line!.StartsWith(':'))
                continue;

            if (line.Length == 0)
            {
                if (dataBuilder.Length == 0)
                    continue;

                var payload = JsonSerializer.Deserialize<UiPreferencesDocumentDto>(dataBuilder.ToString())!;
                return (eventName ?? string.Empty, payload);
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (dataBuilder.Length > 0)
                    dataBuilder.Append('\n');
                dataBuilder.Append(line["data:".Length..].TrimStart());
            }
        }
    }

    private sealed class ManagedPreferencesFactory(string root) : IsolatedApiFactory
    {
        protected override bool UsesManagedStorage => true;
        protected override string? ManagedUserRoot => root;
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Warning",
            }));
    }

    private sealed class ScopelessPreferencesFactory(string root) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(root, "workspace"),
                ["Logging:LogLevel:Default"] = "Warning",
            }));
    }
}
