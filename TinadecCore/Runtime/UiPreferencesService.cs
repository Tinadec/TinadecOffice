using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Runtime;

/// <summary>
/// Manages <c>&lt;UserStorageRoot&gt;/preferences.toml</c> with deterministic Tomlyn serialization,
/// atomic write-through persistence, optimistic <c>content_hash</c> concurrency, and debounced
/// <see cref="FileSystemWatcher"/> hot-reload broadcast.
/// </summary>
public sealed class UiPreferencesService : IUiPreferencesService, IDisposable, IAsyncDisposable
{
    public const string FileName = "preferences.toml";
    public const int CurrentVersion = 1;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan WatcherDebounce = TimeSpan.FromMilliseconds(120);
    private static readonly Regex HexColorRegex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedThemes = new(StringComparer.Ordinal)
    {
        "dark", "light", "system",
    };

    private static readonly HashSet<string> AllowedAccents = new(StringComparer.Ordinal)
    {
        "monochrome", "warm", "dynamic", "custom",
        "blue", "green", "orange", "purple", "pink", "red", "cyan", "yellow",
    };

    private static readonly HashSet<string> AllowedPanelEffects = new(StringComparer.Ordinal)
    {
        "solid", "translucent", "blur",
    };

    private static readonly HashSet<string> AllowedPanelPresets = new(StringComparer.Ordinal)
    {
        "solid", "frosted", "crystal", "mica", "translucent", "custom",
    };

    private readonly record struct PanelPresetSpec(string Preset, string Effect, int Opacity, int Blur, int Saturation);

    private static readonly IReadOnlyDictionary<string, PanelPresetSpec> NamedPanelPresets =
        new Dictionary<string, PanelPresetSpec>(StringComparer.Ordinal)
        {
            ["solid"] = new("solid", "solid", 100, 0, 100),
            ["frosted"] = new("frosted", "blur", 68, 18, 160),
            ["crystal"] = new("crystal", "blur", 48, 24, 180),
            ["mica"] = new("mica", "blur", 82, 14, 130),
            ["translucent"] = new("translucent", "translucent", 76, 0, 100),
        };

    private static readonly HashSet<string> AllowedWorkspaceViews = new(StringComparer.Ordinal)
    {
        "grid", "list",
    };

    private static readonly HashSet<string> AllowedFolderTones = new(StringComparer.Ordinal)
    {
        "amber", "teal", "sky", "coral", "frost",
    };

    private static readonly HashSet<string> AllowedDropActions = new(StringComparer.Ordinal)
    {
        "link", "copy", "ask",
    };

    private static readonly HashSet<string> AllowedSpatialPromoteModes = new(StringComparer.Ordinal)
    {
        "merge", "split", "parent_children",
    };

    private static readonly HashSet<string> AllowedReducedMotions = new(StringComparer.Ordinal)
    {
        "system", "on", "off",
    };

    private static readonly HashSet<string> AllowedRootTomlKeys = new(StringComparer.Ordinal)
    {
        "version", "appearance", "interaction",
    };

    private static readonly HashSet<string> AllowedAppearanceTomlKeys = new(StringComparer.Ordinal)
    {
        "theme", "accent", "custom_accent_hex",
        "panel_effect", "panel_preset", "panel_opacity", "panel_blur", "panel_saturation",
        "workspace_view", "workspace_folder_tone",
    };

    private static readonly HashSet<string> AllowedInteractionTomlKeys = new(StringComparer.Ordinal)
    {
        "workspace_drop_action", "spatial_promote_mode", "reduced_motion",
    };

    private static readonly string DefaultTomlText = SerializeToml(
        UiPreferencesAppearanceDto.CreateDefaults(),
        UiPreferencesInteractionDto.CreateDefaults());

    private static readonly string DefaultContentHash = ComputeContentHash(Utf8NoBom.GetBytes(DefaultTomlText));

    private readonly IServiceProvider? _services;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<UiPreferencesService>? _logger;
    private readonly Lazy<string> _userStorageRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ChannelWriter<UiPreferencesDocumentDto>> _subscribers = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly DateTimeOffset _initializedAtUtc = DateTimeOffset.UtcNow;

    private readonly object _watcherLock = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private string? _ownedFallbackTempRoot;
    private UiPreferencesDocumentDto? _cachedValidDocument;
    private string? _lastBroadcastHash;
    private int _disposed;

    [ActivatorUtilitiesConstructor]
    public UiPreferencesService(
        IServiceProvider services,
        IConfiguration? configuration = null,
        ILogger<UiPreferencesService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _configuration = configuration;
        _logger = logger;
        _userStorageRoot = new Lazy<string>(ResolveUserStorageRoot, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public UiPreferencesService(string userStorageRoot, ILogger<UiPreferencesService>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(userStorageRoot))
            throw new ArgumentException("User storage root is required.", nameof(userStorageRoot));
        var normalizedRoot = Path.GetFullPath(userStorageRoot);
        _logger = logger;
        _userStorageRoot = new Lazy<string>(() => normalizedRoot, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string UserStorageRoot => _userStorageRoot.Value;
    public string PreferencesFilePath => Path.Combine(UserStorageRoot, FileName);

    public async Task<UiPreferencesDocumentDto> GetAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await ReadCurrentUnderLockAsync(ct).ConfigureAwait(false);
            _lastBroadcastHash ??= doc.ContentHash;
            return doc;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UiPreferencesDocumentDto> PutAsync(UiPreferencesPutRequestDto request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (request is null)
            throw InvalidPreferences("missing_body", "Preferences PUT request body is required.");

        var resetDefaults = request.ResetDefaults == true;
        if (!resetDefaults && (request.Appearance is null || request.Interaction is null))
        {
            throw InvalidPreferences(
                "missing_sections",
                "PUT /api/v1/preferences requires both 'appearance' and 'interaction' sections unless reset_defaults is true.");
        }

        var nextAppearance = request.Appearance is not null
            ? ValidateFullAppearance(request.Appearance)
            : UiPreferencesAppearanceDto.CreateDefaults();

        var nextInteraction = request.Interaction is not null
            ? ValidateFullInteraction(request.Interaction)
            : UiPreferencesInteractionDto.CreateDefaults();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await ReadCurrentUnderLockAsync(ct).ConfigureAwait(false);
            AssertExpectedHashMatches(request.ExpectedContentHash, current.ContentHash);
            return await PersistAndBroadcastUnderLockAsync(nextAppearance, nextInteraction, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UiPreferencesDocumentDto> PatchAsync(UiPreferencesPatchRequestDto request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (request is null)
            throw InvalidPreferences("missing_body", "Preferences PATCH request body is required.");

        var validatedAppearancePatch = ValidateAppearancePatch(request.Appearance);
        var validatedInteractionPatch = ValidateInteractionPatch(request.Interaction);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await ReadCurrentUnderLockAsync(ct).ConfigureAwait(false);
            AssertExpectedHashMatches(request.ExpectedContentHash, current.ContentHash);

            var mergedPanel = MergePatchedPanelAppearance(current.Appearance, validatedAppearancePatch);
            var mergedAppearance = new UiPreferencesAppearanceDto
            {
                Theme = validatedAppearancePatch?.Theme ?? current.Appearance.Theme,
                Accent = validatedAppearancePatch?.Accent ?? current.Appearance.Accent,
                CustomAccentHex = validatedAppearancePatch?.CustomAccentHex ?? current.Appearance.CustomAccentHex,
                PanelEffect = mergedPanel.Effect,
                PanelPreset = mergedPanel.Preset,
                PanelOpacity = mergedPanel.Opacity,
                PanelBlur = mergedPanel.Blur,
                PanelSaturation = mergedPanel.Saturation,
                WorkspaceView = validatedAppearancePatch?.WorkspaceView ?? current.Appearance.WorkspaceView,
                WorkspaceFolderTone = validatedAppearancePatch?.WorkspaceFolderTone ?? current.Appearance.WorkspaceFolderTone,
            };

            var mergedInteraction = new UiPreferencesInteractionDto
            {
                WorkspaceDropAction = validatedInteractionPatch?.WorkspaceDropAction ?? current.Interaction.WorkspaceDropAction,
                SpatialPromoteMode = validatedInteractionPatch?.SpatialPromoteMode ?? current.Interaction.SpatialPromoteMode,
                ReducedMotion = validatedInteractionPatch?.ReducedMotion ?? current.Interaction.ReducedMotion,
            };

            return await PersistAndBroadcastUnderLockAsync(mergedAppearance, mergedInteraction, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<UiPreferencesDocumentDto> Subscribe([EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var subscriberId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<UiPreferencesDocumentDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _subscribers[subscriberId] = channel.Writer;
        EnsureWatcherStarted();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            while (await channel.Reader.WaitToReadAsync(linkedToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var doc))
                {
                    yield return doc;
                }
            }
        }
        finally
        {
            _subscribers.TryRemove(subscriberId, out _);
            channel.Writer.TryComplete();
        }
    }

    public static string SerializeToml(UiPreferencesAppearanceDto appearance, UiPreferencesInteractionDto interaction)
    {
        var panel = ResolveFullPanelAppearance(
            string.IsNullOrWhiteSpace(appearance.PanelEffect) ? null : appearance.PanelEffect,
            appearance.PanelPreset,
            appearance.PanelOpacity,
            appearance.PanelBlur,
            appearance.PanelSaturation,
            defaultEffectWhenMissing: "translucent");

        var table = new TomlTable
        {
            ["version"] = (long)CurrentVersion,
            ["appearance"] = new TomlTable
            {
                ["theme"] = appearance.Theme,
                ["accent"] = appearance.Accent,
                ["custom_accent_hex"] = appearance.CustomAccentHex,
                ["panel_effect"] = panel.Effect,
                ["panel_preset"] = panel.Preset,
                ["panel_opacity"] = (long)panel.Opacity,
                ["panel_blur"] = (long)panel.Blur,
                ["panel_saturation"] = (long)panel.Saturation,
                ["workspace_view"] = appearance.WorkspaceView,
                ["workspace_folder_tone"] = appearance.WorkspaceFolderTone,
            },
            ["interaction"] = new TomlTable
            {
                ["workspace_drop_action"] = interaction.WorkspaceDropAction,
                ["spatial_promote_mode"] = interaction.SpatialPromoteMode,
                ["reduced_motion"] = interaction.ReducedMotion,
            },
        };

        var serialized = TomlSerializer.Serialize(table);
        return serialized.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
    }

    public static string ComputeContentHash(ReadOnlySpan<byte> utf8Bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(utf8Bytes)).ToLowerInvariant();

    private string ResolveUserStorageRoot()
    {
        if (_services?.GetService<IStorageScopeRegistry>() is { } registry
            && !string.IsNullOrWhiteSpace(registry.User.Root))
        {
            return Path.GetFullPath(registry.User.Root);
        }

        if (_services?.GetService<IScopeStorageLocations>() is { } locations
            && !string.IsNullOrWhiteSpace(locations.Root))
        {
            return Path.GetFullPath(locations.Root);
        }

        if (_configuration?["TinadecStorage:UserRoot"] is { Length: > 0 } configuredRoot)
        {
            return Path.GetFullPath(configuredRoot);
        }

        var fallback = Path.Combine(Path.GetTempPath(), "tinadec-ui-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fallback);
        _ownedFallbackTempRoot = fallback;
        return fallback;
    }

    private async Task<UiPreferencesDocumentDto> ReadCurrentUnderLockAsync(CancellationToken ct)
    {
        var root = UserStorageRoot;
        var filePath = PreferencesFilePath;
        if (!File.Exists(filePath))
        {
            var defaultDoc = CreateDefaultDocument(filePath, _initializedAtUtc);
            _cachedValidDocument = defaultDoc;
            return defaultDoc;
        }

        StorageScopePaths.RejectLinks(root, filePath);
        var bytes = await ReadBytesWithRetryAsync(filePath, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            var defaultDoc = CreateDefaultDocument(filePath, _initializedAtUtc);
            _cachedValidDocument = defaultDoc;
            return defaultDoc;
        }

        var text = Utf8NoBom.GetString(bytes);
        if (TryParsePreferencesToml(text, out var appearance, out var interaction, out var diagnosticMessage))
        {
            var doc = new UiPreferencesDocumentDto
            {
                Version = CurrentVersion,
                FilePath = filePath,
                ContentHash = ComputeContentHash(bytes),
                UpdatedAt = GetFileUpdatedAtUtc(filePath),
                Appearance = appearance,
                Interaction = interaction,
            };
            _cachedValidDocument = doc;
            return doc;
        }

        _logger?.LogWarning("Ignoring malformed preferences.toml at {FilePath}: {Diagnostic}", filePath, diagnosticMessage);
        return _cachedValidDocument ?? CreateDefaultDocument(filePath, _initializedAtUtc);
    }

    private async Task<UiPreferencesDocumentDto> PersistAndBroadcastUnderLockAsync(
        UiPreferencesAppearanceDto appearance,
        UiPreferencesInteractionDto interaction,
        CancellationToken ct)
    {
        var root = UserStorageRoot;
        var filePath = PreferencesFilePath;
        Directory.CreateDirectory(root);
        StorageScopePaths.RejectLinks(root, filePath);

        var tomlText = SerializeToml(appearance, interaction);
        var bytes = Utf8NoBom.GetBytes(tomlText);
        var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            StorageScopePaths.RejectLinks(root, tempPath);
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            StorageScopePaths.RejectLinks(root, filePath);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (IOException) { }
            }
        }

        var updated = new UiPreferencesDocumentDto
        {
            Version = CurrentVersion,
            FilePath = filePath,
            ContentHash = ComputeContentHash(bytes),
            UpdatedAt = GetFileUpdatedAtUtc(filePath),
            Appearance = appearance,
            Interaction = interaction,
        };

        _cachedValidDocument = updated;
        EnsureWatcherStarted();

        if (!string.Equals(_lastBroadcastHash, updated.ContentHash, StringComparison.Ordinal))
        {
            _lastBroadcastHash = updated.ContentHash;
            Broadcast(updated);
        }

        return updated;
    }

    private void EnsureWatcherStarted()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_watcherLock)
        {
            if (_watcher is not null || Volatile.Read(ref _disposed) != 0)
                return;

            try
            {
                var root = UserStorageRoot;
                Directory.CreateDirectory(root);
                StorageScopePaths.RejectLinks(root);

                if (_lastBroadcastHash is null)
                {
                    var filePath = PreferencesFilePath;
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            StorageScopePaths.RejectLinks(root, filePath);
                            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            using var ms = new MemoryStream();
                            stream.CopyTo(ms);
                            var bytes = ms.ToArray();
                            if (TryParsePreferencesToml(Utf8NoBom.GetString(bytes), out var app, out var inter, out _))
                            {
                                var hash = ComputeContentHash(bytes);
                                _lastBroadcastHash = hash;
                                _cachedValidDocument ??= new UiPreferencesDocumentDto
                                {
                                    Version = CurrentVersion,
                                    FilePath = filePath,
                                    ContentHash = hash,
                                    UpdatedAt = GetFileUpdatedAtUtc(filePath),
                                    Appearance = app,
                                    Interaction = inter,
                                };
                            }
                            else
                            {
                                _lastBroadcastHash = DefaultContentHash;
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _lastBroadcastHash = DefaultContentHash;
                        }
                    }
                    else
                    {
                        _lastBroadcastHash = DefaultContentHash;
                    }
                }

                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                };

                watcher.Changed += OnWatcherEvent;
                watcher.Created += OnWatcherEvent;
                watcher.Deleted += OnWatcherEvent;
                watcher.Renamed += OnWatcherRenamed;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Could not start FileSystemWatcher for preferences.toml.");
            }
        }
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        if (!IsPreferencesFileName(e.Name))
            return;
        ScheduleWatcherReload();
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsPreferencesFileName(e.Name) && !IsPreferencesFileName(e.OldName))
            return;
        ScheduleWatcherReload();
    }

    private static bool IsPreferencesFileName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFileName(name), FileName, comparison);
    }

    private void ScheduleWatcherReload()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        CancellationToken debounceToken;
        lock (_watcherLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            debounceToken = _debounceCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(WatcherDebounce, debounceToken).ConfigureAwait(false);
                await ReloadFromDiskAfterWatcherAsync(debounceToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by newer file event or service disposal.
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Unexpected error while reloading preferences.toml after file change.");
            }
        }, CancellationToken.None);
    }

    private async Task ReloadFromDiskAfterWatcherAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = UserStorageRoot;
            var filePath = PreferencesFilePath;

            if (!File.Exists(filePath))
            {
                var defaultDoc = CreateDefaultDocument(filePath, DateTimeOffset.UtcNow);
                _cachedValidDocument = defaultDoc;
                if (!string.Equals(_lastBroadcastHash, defaultDoc.ContentHash, StringComparison.Ordinal))
                {
                    _lastBroadcastHash = defaultDoc.ContentHash;
                    Broadcast(defaultDoc);
                }
                return;
            }

            StorageScopePaths.RejectLinks(root, filePath);
            var bytes = await ReadBytesWithRetryAsync(filePath, ct).ConfigureAwait(false);
            if (bytes is null)
                return;

            var text = Utf8NoBom.GetString(bytes);
            if (!TryParsePreferencesToml(text, out var appearance, out var interaction, out var diagnosticMessage))
            {
                _logger?.LogDebug("Ignoring transient/malformed preferences.toml edit: {Diagnostic}", diagnosticMessage);
                return;
            }

            var doc = new UiPreferencesDocumentDto
            {
                Version = CurrentVersion,
                FilePath = filePath,
                ContentHash = ComputeContentHash(bytes),
                UpdatedAt = GetFileUpdatedAtUtc(filePath),
                Appearance = appearance,
                Interaction = interaction,
            };

            _cachedValidDocument = doc;
            if (!string.Equals(_lastBroadcastHash, doc.ContentHash, StringComparison.Ordinal))
            {
                _lastBroadcastHash = doc.ContentHash;
                Broadcast(doc);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Broadcast(UiPreferencesDocumentDto document)
    {
        foreach (var pair in _subscribers)
        {
            if (!pair.Value.TryWrite(document))
            {
                _subscribers.TryRemove(pair.Key, out _);
            }
        }
    }

    private static async Task<byte[]?> ReadBytesWithRetryAsync(string filePath, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(filePath))
                    return null;

                await using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
                return ms.ToArray();
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 6)
            {
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
    }

    private static UiPreferencesDocumentDto CreateDefaultDocument(string filePath, DateTimeOffset updatedAt) => new()
    {
        Version = CurrentVersion,
        FilePath = filePath,
        ContentHash = DefaultContentHash,
        UpdatedAt = updatedAt,
        Appearance = UiPreferencesAppearanceDto.CreateDefaults(),
        Interaction = UiPreferencesInteractionDto.CreateDefaults(),
    };

    private static DateTimeOffset GetFileUpdatedAtUtc(string filePath)
    {
        try
        {
            var utc = File.GetLastWriteTimeUtc(filePath);
            return utc.Year <= 1601 ? DateTimeOffset.UtcNow : new DateTimeOffset(utc, TimeSpan.Zero);
        }
        catch (IOException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static void AssertExpectedHashMatches(string? expectedContentHash, string actualContentHash)
    {
        if (string.IsNullOrWhiteSpace(expectedContentHash))
            return;

        var normalizedExpected = expectedContentHash.Trim().Trim('"');
        if (!string.Equals(normalizedExpected, actualContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigurationDocumentException(
                "preferences_conflict",
                $"Preferences changed since '{normalizedExpected}' was read (current '{actualContentHash}'). Reload before saving.",
                [new ConfigurationDiagnostic("preferences_conflict", "Preferences content_hash did not match the current document.")]);
        }
    }

    internal static bool TryParsePreferencesToml(
        string tomlText,
        out UiPreferencesAppearanceDto appearance,
        out UiPreferencesInteractionDto interaction,
        out string? errorMessage)
    {
        appearance = UiPreferencesAppearanceDto.CreateDefaults();
        interaction = UiPreferencesInteractionDto.CreateDefaults();
        errorMessage = null;

        TomlTable? root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(tomlText);
        }
        catch (TomlException ex)
        {
            errorMessage = ex.Message;
            return false;
        }

        if (root is null)
        {
            errorMessage = "TOML root table is null.";
            return false;
        }

        foreach (var key in root.Keys)
        {
            if (!AllowedRootTomlKeys.Contains(key))
            {
                errorMessage = $"Unknown root key '{key}' in preferences.toml.";
                return false;
            }
        }

        if (root.TryGetValue("version", out var rawVersion))
        {
            if (rawVersion is not long version || version != CurrentVersion)
            {
                errorMessage = $"Unsupported preferences.toml version '{rawVersion}'. Expected {CurrentVersion}.";
                return false;
            }
        }

        var defaultsApp = UiPreferencesAppearanceDto.CreateDefaults();
        var theme = defaultsApp.Theme;
        var accent = defaultsApp.Accent;
        var customHex = defaultsApp.CustomAccentHex;
        string? rawPanelEffect = null;
        string? rawPanelPreset = null;
        int? rawPanelOpacity = null;
        int? rawPanelBlur = null;
        int? rawPanelSaturation = null;
        var workspaceView = defaultsApp.WorkspaceView;
        var folderTone = defaultsApp.WorkspaceFolderTone;

        if (root.TryGetValue("appearance", out var rawAppearance))
        {
            if (rawAppearance is not TomlTable appTable)
            {
                errorMessage = "'appearance' in preferences.toml must be a table.";
                return false;
            }

            foreach (var key in appTable.Keys)
            {
                if (!AllowedAppearanceTomlKeys.Contains(key))
                {
                    errorMessage = $"Unknown appearance key '{key}' in preferences.toml.";
                    return false;
                }
            }

            try
            {
                if (appTable.TryGetValue("theme", out var vTheme))
                    theme = ValidateTheme(RequireTomlString(vTheme, "appearance.theme"));
                if (appTable.TryGetValue("accent", out var vAccent))
                    accent = ValidateAccent(RequireTomlString(vAccent, "appearance.accent"));
                if (appTable.TryGetValue("custom_accent_hex", out var vHex))
                    customHex = ValidateCustomAccentHex(RequireTomlString(vHex, "appearance.custom_accent_hex"));
                if (appTable.TryGetValue("panel_effect", out var vPanel))
                    rawPanelEffect = ValidatePanelEffect(RequireTomlString(vPanel, "appearance.panel_effect"));
                if (appTable.TryGetValue("panel_preset", out var vPreset))
                    rawPanelPreset = ValidatePanelPreset(RequireTomlString(vPreset, "appearance.panel_preset"));
                if (appTable.TryGetValue("panel_opacity", out var vOpacity))
                    rawPanelOpacity = ValidatePanelOpacity(RequireTomlInt(vOpacity, "appearance.panel_opacity"));
                if (appTable.TryGetValue("panel_blur", out var vBlur))
                    rawPanelBlur = ValidatePanelBlur(RequireTomlInt(vBlur, "appearance.panel_blur"));
                if (appTable.TryGetValue("panel_saturation", out var vSaturation))
                    rawPanelSaturation = ValidatePanelSaturation(RequireTomlInt(vSaturation, "appearance.panel_saturation"));
                if (appTable.TryGetValue("workspace_view", out var vView))
                    workspaceView = ValidateWorkspaceView(RequireTomlString(vView, "appearance.workspace_view"));
                if (appTable.TryGetValue("workspace_folder_tone", out var vTone))
                    folderTone = ValidateWorkspaceFolderTone(RequireTomlString(vTone, "appearance.workspace_folder_tone"));
            }
            catch (ConfigurationDocumentException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        var resolvedPanel = ResolveFullPanelAppearance(
            rawPanelEffect,
            rawPanelPreset,
            rawPanelOpacity,
            rawPanelBlur,
            rawPanelSaturation,
            defaultEffectWhenMissing: defaultsApp.PanelEffect);

        var defaultsInt = UiPreferencesInteractionDto.CreateDefaults();
        var dropAction = defaultsInt.WorkspaceDropAction;
        var spatialMode = defaultsInt.SpatialPromoteMode;
        var reducedMotion = defaultsInt.ReducedMotion;

        if (root.TryGetValue("interaction", out var rawInteraction))
        {
            if (rawInteraction is not TomlTable intTable)
            {
                errorMessage = "'interaction' in preferences.toml must be a table.";
                return false;
            }

            foreach (var key in intTable.Keys)
            {
                if (!AllowedInteractionTomlKeys.Contains(key))
                {
                    errorMessage = $"Unknown interaction key '{key}' in preferences.toml.";
                    return false;
                }
            }

            try
            {
                if (intTable.TryGetValue("workspace_drop_action", out var vDrop))
                    dropAction = ValidateWorkspaceDropAction(RequireTomlString(vDrop, "interaction.workspace_drop_action"));
                if (intTable.TryGetValue("spatial_promote_mode", out var vPromote))
                    spatialMode = ValidateSpatialPromoteMode(RequireTomlString(vPromote, "interaction.spatial_promote_mode"));
                if (intTable.TryGetValue("reduced_motion", out var vMotion))
                    reducedMotion = ValidateReducedMotion(RequireTomlString(vMotion, "interaction.reduced_motion"));
            }
            catch (ConfigurationDocumentException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        appearance = new UiPreferencesAppearanceDto
        {
            Theme = theme,
            Accent = accent,
            CustomAccentHex = customHex,
            PanelEffect = resolvedPanel.Effect,
            PanelPreset = resolvedPanel.Preset,
            PanelOpacity = resolvedPanel.Opacity,
            PanelBlur = resolvedPanel.Blur,
            PanelSaturation = resolvedPanel.Saturation,
            WorkspaceView = workspaceView,
            WorkspaceFolderTone = folderTone,
        };

        interaction = new UiPreferencesInteractionDto
        {
            WorkspaceDropAction = dropAction,
            SpatialPromoteMode = spatialMode,
            ReducedMotion = reducedMotion,
        };

        return true;
    }

    private static string RequireTomlString(object? value, string field) =>
        value is string text ? text : throw InvalidPreferences("invalid_type", $"'{field}' must be a string.");

    private static int RequireTomlInt(object? value, string field) => value switch
    {
        int i => i,
        long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
        _ => throw InvalidPreferences("invalid_type", $"'{field}' must be an integer."),
    };

    private static UiPreferencesAppearanceDto ValidateFullAppearance(UiPreferencesAppearanceDto input)
    {
        var validatedPreset = input.PanelPreset is null ? null : ValidatePanelPreset(input.PanelPreset);
        string? validatedEffect;
        if (!string.IsNullOrEmpty(input.PanelEffect))
        {
            validatedEffect = ValidatePanelEffect(input.PanelEffect);
        }
        else if (validatedPreset is not null && NamedPanelPresets.TryGetValue(validatedPreset, out var presetSpec))
        {
            validatedEffect = presetSpec.Effect;
        }
        else
        {
            validatedEffect = ValidatePanelEffect(input.PanelEffect);
        }

        var validatedOpacity = input.PanelOpacity is null ? (int?)null : ValidatePanelOpacity(input.PanelOpacity.Value);
        var validatedBlur = input.PanelBlur is null ? (int?)null : ValidatePanelBlur(input.PanelBlur.Value);
        var validatedSaturation = input.PanelSaturation is null ? (int?)null : ValidatePanelSaturation(input.PanelSaturation.Value);

        var resolvedPanel = ResolveFullPanelAppearance(
            validatedEffect,
            validatedPreset,
            validatedOpacity,
            validatedBlur,
            validatedSaturation,
            defaultEffectWhenMissing: "translucent");

        return new UiPreferencesAppearanceDto
        {
            Theme = ValidateTheme(input.Theme),
            Accent = ValidateAccent(input.Accent),
            CustomAccentHex = ValidateCustomAccentHex(input.CustomAccentHex),
            PanelEffect = resolvedPanel.Effect,
            PanelPreset = resolvedPanel.Preset,
            PanelOpacity = resolvedPanel.Opacity,
            PanelBlur = resolvedPanel.Blur,
            PanelSaturation = resolvedPanel.Saturation,
            WorkspaceView = ValidateWorkspaceView(input.WorkspaceView),
            WorkspaceFolderTone = ValidateWorkspaceFolderTone(input.WorkspaceFolderTone),
        };
    }

    private static UiPreferencesInteractionDto ValidateFullInteraction(UiPreferencesInteractionDto input) => new()
    {
        WorkspaceDropAction = ValidateWorkspaceDropAction(input.WorkspaceDropAction),
        SpatialPromoteMode = ValidateSpatialPromoteMode(input.SpatialPromoteMode),
        ReducedMotion = ValidateReducedMotion(input.ReducedMotion),
    };

    private static UiPreferencesAppearancePatchDto? ValidateAppearancePatch(UiPreferencesAppearancePatchDto? patch)
    {
        if (patch is null)
            return null;

        return new UiPreferencesAppearancePatchDto
        {
            Theme = patch.Theme is null ? null : ValidateTheme(patch.Theme),
            Accent = patch.Accent is null ? null : ValidateAccent(patch.Accent),
            CustomAccentHex = patch.CustomAccentHex is null ? null : ValidateCustomAccentHex(patch.CustomAccentHex),
            PanelEffect = patch.PanelEffect is null ? null : ValidatePanelEffect(patch.PanelEffect),
            PanelPreset = patch.PanelPreset is null ? null : ValidatePanelPreset(patch.PanelPreset),
            PanelOpacity = patch.PanelOpacity is null ? null : ValidatePanelOpacity(patch.PanelOpacity.Value),
            PanelBlur = patch.PanelBlur is null ? null : ValidatePanelBlur(patch.PanelBlur.Value),
            PanelSaturation = patch.PanelSaturation is null ? null : ValidatePanelSaturation(patch.PanelSaturation.Value),
            WorkspaceView = patch.WorkspaceView is null ? null : ValidateWorkspaceView(patch.WorkspaceView),
            WorkspaceFolderTone = patch.WorkspaceFolderTone is null ? null : ValidateWorkspaceFolderTone(patch.WorkspaceFolderTone),
        };
    }

    private static PanelPresetSpec DefaultPresetForEffect(string effect) => effect switch
    {
        "solid" => NamedPanelPresets["solid"],
        "blur" => NamedPanelPresets["frosted"],
        _ => NamedPanelPresets["translucent"],
    };

    private static string? MatchCanonicalPreset(string effect, int opacity, int blur, int saturation)
    {
        foreach (var spec in NamedPanelPresets.Values)
        {
            if (string.Equals(spec.Effect, effect, StringComparison.Ordinal)
                && spec.Opacity == opacity
                && spec.Blur == blur
                && spec.Saturation == saturation)
            {
                return spec.Preset;
            }
        }
        return null;
    }

    private static PanelPresetSpec ResolveFullPanelAppearance(
        string? validatedEffect,
        string? validatedPreset,
        int? validatedOpacity,
        int? validatedBlur,
        int? validatedSaturation,
        string defaultEffectWhenMissing)
    {
        var normalizedEffect = validatedEffect is null
            ? null
            : string.Equals(validatedEffect, "opaque", StringComparison.Ordinal) ? "solid" : validatedEffect;

        if (validatedPreset is not null && NamedPanelPresets.TryGetValue(validatedPreset, out var namedSpec))
        {
            var effect = normalizedEffect ?? namedSpec.Effect;
            var opacity = validatedOpacity ?? namedSpec.Opacity;
            var blur = validatedBlur ?? namedSpec.Blur;
            var saturation = validatedSaturation ?? namedSpec.Saturation;
            return new PanelPresetSpec(validatedPreset, effect, opacity, blur, saturation);
        }

        if (string.Equals(validatedPreset, "custom", StringComparison.Ordinal))
        {
            var effect = normalizedEffect ?? (validatedBlur is > 0 ? "blur" : defaultEffectWhenMissing);
            var fallback = DefaultPresetForEffect(effect);
            var opacity = validatedOpacity ?? fallback.Opacity;
            var blur = validatedBlur ?? fallback.Blur;
            var saturation = validatedSaturation ?? fallback.Saturation;
            return new PanelPresetSpec("custom", effect, opacity, blur, saturation);
        }

        var effectiveEffect = normalizedEffect ?? (validatedBlur is > 0 ? "blur" : defaultEffectWhenMissing);
        var defaultSpec = DefaultPresetForEffect(effectiveEffect);
        var resolvedOpacity = validatedOpacity ?? defaultSpec.Opacity;
        var resolvedBlur = validatedBlur ?? defaultSpec.Blur;
        var resolvedSaturation = validatedSaturation ?? defaultSpec.Saturation;
        var derivedPreset = MatchCanonicalPreset(effectiveEffect, resolvedOpacity, resolvedBlur, resolvedSaturation) ?? "custom";
        return new PanelPresetSpec(derivedPreset, effectiveEffect, resolvedOpacity, resolvedBlur, resolvedSaturation);
    }

    private static PanelPresetSpec MergePatchedPanelAppearance(
        UiPreferencesAppearanceDto current,
        UiPreferencesAppearancePatchDto? patch)
    {
        var basePanel = ResolveFullPanelAppearance(
            current.PanelEffect,
            current.PanelPreset,
            current.PanelOpacity,
            current.PanelBlur,
            current.PanelSaturation,
            defaultEffectWhenMissing: "translucent");

        if (patch is null
            || (patch.PanelEffect is null
                && patch.PanelPreset is null
                && patch.PanelOpacity is null
                && patch.PanelBlur is null
                && patch.PanelSaturation is null))
        {
            return basePanel;
        }

        if (patch.PanelPreset is not null && NamedPanelPresets.TryGetValue(patch.PanelPreset, out var namedSpec))
        {
            return new PanelPresetSpec(
                patch.PanelPreset,
                patch.PanelEffect ?? namedSpec.Effect,
                patch.PanelOpacity ?? namedSpec.Opacity,
                patch.PanelBlur ?? namedSpec.Blur,
                patch.PanelSaturation ?? namedSpec.Saturation);
        }

        if (string.Equals(patch.PanelPreset, "custom", StringComparison.Ordinal))
        {
            var effect = patch.PanelEffect ?? basePanel.Effect;
            var effectChanged = patch.PanelEffect is not null && !string.Equals(patch.PanelEffect, basePanel.Effect, StringComparison.Ordinal);
            var fallback = DefaultPresetForEffect(effect);
            return new PanelPresetSpec(
                "custom",
                effect,
                patch.PanelOpacity ?? (effectChanged ? fallback.Opacity : basePanel.Opacity),
                patch.PanelBlur ?? (effectChanged ? fallback.Blur : basePanel.Blur),
                patch.PanelSaturation ?? (effectChanged ? fallback.Saturation : basePanel.Saturation));
        }

        if (patch.PanelEffect is not null
            && patch.PanelOpacity is null
            && patch.PanelBlur is null
            && patch.PanelSaturation is null)
        {
            return DefaultPresetForEffect(patch.PanelEffect);
        }

        var nextEffect = patch.PanelEffect ?? basePanel.Effect;
        var changedEffect = patch.PanelEffect is not null && !string.Equals(patch.PanelEffect, basePanel.Effect, StringComparison.Ordinal);
        var nextFallback = DefaultPresetForEffect(nextEffect);
        var nextOpacity = patch.PanelOpacity ?? (changedEffect ? nextFallback.Opacity : basePanel.Opacity);
        var nextBlur = patch.PanelBlur ?? (changedEffect ? nextFallback.Blur : basePanel.Blur);
        var nextSaturation = patch.PanelSaturation ?? (changedEffect ? nextFallback.Saturation : basePanel.Saturation);
        var matchedPreset = MatchCanonicalPreset(nextEffect, nextOpacity, nextBlur, nextSaturation) ?? "custom";
        return new PanelPresetSpec(matchedPreset, nextEffect, nextOpacity, nextBlur, nextSaturation);
    }

    private static UiPreferencesInteractionPatchDto? ValidateInteractionPatch(UiPreferencesInteractionPatchDto? patch)
    {
        if (patch is null)
            return null;

        return new UiPreferencesInteractionPatchDto
        {
            WorkspaceDropAction = patch.WorkspaceDropAction is null ? null : ValidateWorkspaceDropAction(patch.WorkspaceDropAction),
            SpatialPromoteMode = patch.SpatialPromoteMode is null ? null : ValidateSpatialPromoteMode(patch.SpatialPromoteMode),
            ReducedMotion = patch.ReducedMotion is null ? null : ValidateReducedMotion(patch.ReducedMotion),
        };
    }

    private static string ValidateTheme(string? value)
    {
        if (value is not null && AllowedThemes.Contains(value))
            return value;
        throw InvalidPreferences("invalid_theme", $"appearance.theme must be one of: dark, light, system (got '{value}').");
    }

    private static string ValidateAccent(string? value)
    {
        if (value is not null && AllowedAccents.Contains(value))
            return value;
        throw InvalidPreferences(
            "invalid_accent",
            $"appearance.accent must be one of: monochrome, warm, dynamic, custom, blue, green, orange, purple, pink, red, cyan, yellow (got '{value}').");
    }

    private static string ValidateCustomAccentHex(string? value)
    {
        if (value is not null && HexColorRegex.IsMatch(value))
            return value.ToLowerInvariant();
        throw InvalidPreferences("invalid_custom_accent_hex", $"appearance.custom_accent_hex must match #RRGGBB (got '{value}').");
    }

    private static string ValidatePanelEffect(string? value)
    {
        if (string.Equals(value, "opaque", StringComparison.Ordinal))
            return "solid";
        if (value is not null && AllowedPanelEffects.Contains(value))
            return value;
        throw InvalidPreferences("invalid_panel_effect", $"appearance.panel_effect must be one of: solid, translucent, blur (or alias opaque) (got '{value}').");
    }

    private static string ValidatePanelPreset(string? value)
    {
        if (value is not null && AllowedPanelPresets.Contains(value))
            return value;
        throw InvalidPreferences(
            "invalid_panel_preset",
            $"appearance.panel_preset must be one of: solid, frosted, crystal, mica, translucent, custom (got '{value}').");
    }

    private static int ValidatePanelOpacity(int value)
    {
        if (value >= 0 && value <= 100)
            return value;
        throw InvalidPreferences("invalid_panel_opacity", $"appearance.panel_opacity must be an integer between 0 and 100 (got '{value}').");
    }

    private static int ValidatePanelBlur(int value)
    {
        if (value >= 0 && value <= 32)
            return value;
        throw InvalidPreferences("invalid_panel_blur", $"appearance.panel_blur must be an integer between 0 and 32 (got '{value}').");
    }

    private static int ValidatePanelSaturation(int value)
    {
        if (value >= 100 && value <= 200)
            return value;
        throw InvalidPreferences("invalid_panel_saturation", $"appearance.panel_saturation must be an integer between 100 and 200 (got '{value}').");
    }

    private static string ValidateWorkspaceView(string? value)
    {
        if (value is not null && AllowedWorkspaceViews.Contains(value))
            return value;
        throw InvalidPreferences("invalid_workspace_view", $"appearance.workspace_view must be one of: grid, list (got '{value}').");
    }

    private static string ValidateWorkspaceFolderTone(string? value)
    {
        if (value is not null && AllowedFolderTones.Contains(value))
            return value;
        throw InvalidPreferences("invalid_workspace_folder_tone", $"appearance.workspace_folder_tone must be one of: amber, teal, sky, coral, frost (got '{value}').");
    }

    private static string ValidateWorkspaceDropAction(string? value)
    {
        if (value is not null && AllowedDropActions.Contains(value))
            return value;
        throw InvalidPreferences("invalid_workspace_drop_action", $"interaction.workspace_drop_action must be one of: link, copy, ask (got '{value}').");
    }

    private static string ValidateSpatialPromoteMode(string? value)
    {
        var normalized = value switch
        {
            "merge_single" => "merge",
            "split_many" => "split",
            "parent_with_children" => "parent_children",
            _ => value,
        };
        if (normalized is not null && AllowedSpatialPromoteModes.Contains(normalized))
            return normalized;
        throw InvalidPreferences(
            "invalid_spatial_promote_mode",
            $"interaction.spatial_promote_mode must be one of: merge, split, parent_children (or aliases merge_single, split_many, parent_with_children) (got '{value}').");
    }

    private static string ValidateReducedMotion(string? value)
    {
        if (value is not null && AllowedReducedMotions.Contains(value))
            return value;
        throw InvalidPreferences("invalid_reduced_motion", $"interaction.reduced_motion must be one of: system, on, off (got '{value}').");
    }

    private static ConfigurationDocumentException InvalidPreferences(string diagnosticCode, string message) =>
        new("preferences_invalid", message, [new ConfigurationDiagnostic(diagnosticCode, message)]);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _disposeCts.Cancel();

        lock (_watcherLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;

            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        foreach (var pair in _subscribers)
        {
            pair.Value.TryComplete();
        }
        _subscribers.Clear();

        _disposeCts.Dispose();

        if (_ownedFallbackTempRoot is { } tempRoot && Directory.Exists(tempRoot))
        {
            try { Directory.Delete(tempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
