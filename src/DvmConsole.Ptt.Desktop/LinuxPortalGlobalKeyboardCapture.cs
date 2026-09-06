// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using Tmds.DBus.Protocol;

namespace DvmConsole.Ptt;

internal static class LinuxDesktopSession
{
    public static bool IsWayland
        => string.Equals(
                Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
                "wayland",
                StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
}

/// <summary>
/// Uses the XDG GlobalShortcuts portal so Wayland compositors can grant PTT
/// capture without weakening their input-isolation model.
/// </summary>
internal sealed class LinuxPortalGlobalKeyboardCapture : IGlobalKeyboardCapture
{
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string HostRegistryInterface = "org.freedesktop.host.portal.Registry";
    private const string ShortcutsInterface = "org.freedesktop.portal.GlobalShortcuts";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private const string ShortcutId = "push_to_talk";
    internal const string ApplicationId = "org.dvmproject.dvmconsole";
    private static readonly TimeSpan PortalResponseTimeout = TimeSpan.FromMinutes(2);

    private readonly KeyboardPttKey activationKey;
    private Connection? connection;
    private IDisposable? activatedObserver;
    private IDisposable? deactivatedObserver;
    private IDisposable? closedObserver;
    private IDisposable? ownerObserver;
    private bool started;
    private bool disposed;

    public LinuxPortalGlobalKeyboardCapture(KeyboardPttKey activationKey)
    {
        if (activationKey == KeyboardPttKey.None)
            throw new ArgumentOutOfRangeException(nameof(activationKey));
        this.activationKey = activationKey;
    }

    public event Action<KeyboardPttKey, bool>? KeyChanged;
    public event Action<Exception>? Terminated;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || !LinuxDesktopSession.IsWayland)
            throw new PlatformNotSupportedException("The Wayland global-shortcuts portal is unavailable in this session.");
        if (started)
            return;

        var nextConnection = new Connection(
            Address.Session ?? throw new InvalidOperationException("The session D-Bus address is unavailable."));
        try
        {
            await nextConnection.ConnectAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await RegisterHostApplicationAsync(nextConnection, cancellationToken).ConfigureAwait(false);

            string sessionToken = CreateToken("dvmconsole_session_");
            PortalResponse createResponse = await ExecutePortalRequestAsync(
                nextConnection,
                CreateToken("dvmconsole_create_"),
                (dbus, requestToken) => CreateSessionRequest(
                    dbus,
                    requestToken,
                    sessionToken),
                cancellationToken).ConfigureAwait(false);
            EnsurePortalSuccess(createResponse, "create the Wayland global-shortcut session");
            if (!createResponse.Results.TryGetValue("session_handle", out VariantValue sessionValue))
                throw new InvalidOperationException("The global-shortcuts portal did not return a session handle.");
            string nextSessionPath = sessionValue.GetString();

            IDisposable nextActivatedObserver = await WatchShortcutAsync(
                nextConnection,
                nextSessionPath,
                "Activated",
                isDown: true).ConfigureAwait(false);
            IDisposable nextDeactivatedObserver;
            try
            {
                nextDeactivatedObserver = await WatchShortcutAsync(
                    nextConnection,
                    nextSessionPath,
                    "Deactivated",
                    isDown: false).ConfigureAwait(false);
            }
            catch
            {
                nextActivatedObserver.Dispose();
                throw;
            }

            try
            {
                PortalResponse bindResponse = await ExecutePortalRequestAsync(
                    nextConnection,
                    CreateToken("dvmconsole_bind_"),
                    (dbus, requestToken) => CreateBindShortcutsRequest(
                        dbus,
                        requestToken,
                        nextSessionPath,
                        activationKey),
                    cancellationToken).ConfigureAwait(false);
                EnsurePortalSuccess(bindResponse, "bind the Wayland global PTT shortcut");
            }
            catch
            {
                nextActivatedObserver.Dispose();
                nextDeactivatedObserver.Dispose();
                throw;
            }

            activatedObserver = nextActivatedObserver;
            deactivatedObserver = nextDeactivatedObserver;
            closedObserver = await WatchTerminationAsync(nextConnection, new MatchRule
            {
                Type = MessageType.Signal,
                Sender = PortalService,
                Path = nextSessionPath,
                Interface = "org.freedesktop.portal.Session",
                Member = "Closed"
            }).ConfigureAwait(false);
            ownerObserver = await WatchTerminationAsync(nextConnection, new MatchRule
            {
                Type = MessageType.Signal,
                Sender = "org.freedesktop.DBus",
                Interface = "org.freedesktop.DBus",
                Member = "NameOwnerChanged",
                Arg0 = PortalService
            }).ConfigureAwait(false);
            connection = nextConnection;
            activatedObserver = nextActivatedObserver;
            deactivatedObserver = nextDeactivatedObserver;
            started = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DisposeObservers();
            nextConnection.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            DisposeObservers();
            nextConnection.Dispose();
            throw new InvalidOperationException(
                "Wayland global PTT could not be registered. Focused-window PTT remains available.",
                exception);
        }
    }

    public void Stop()
    {
        if (!started && connection is null)
            return;
        started = false;
        KeyChanged?.Invoke(activationKey, false);
        DisposeObservers();
        connection?.Dispose();
        connection = null;
    }

    private void DisposeObservers()
    {
        closedObserver?.Dispose();
        ownerObserver?.Dispose();
        closedObserver = null;
        ownerObserver = null;
        activatedObserver?.Dispose();
        deactivatedObserver?.Dispose();
        activatedObserver = null;
        deactivatedObserver = null;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        Stop();
        disposed = true;
    }

    private async Task<IDisposable> WatchShortcutAsync(
        Connection dbus,
        string expectedSessionPath,
        string member,
        bool isDown)
        => await dbus.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Sender = PortalService,
                Path = PortalPath,
                Interface = ShortcutsInterface,
                Member = member
            },
            static (message, _) =>
            {
                Reader reader = message.GetBodyReader();
                return new ShortcutSignal(
                    reader.ReadObjectPathAsString(),
                    reader.ReadString());
            },
            (exception, notification, _, _) =>
            {
                if (exception is not null)
                    Terminated?.Invoke(exception);
                else if (
                    notification.SessionPath == expectedSessionPath &&
                    notification.ShortcutId == ShortcutId)
                {
                    KeyChanged?.Invoke(activationKey, isDown);
                }
            },
            ObserverFlags.EmitOnConnectionDispose,
            null,
            null,
            false).ConfigureAwait(false);

    private async Task<IDisposable> WatchTerminationAsync(Connection dbus, MatchRule rule)
        => await dbus.AddMatchAsync(
            rule,
            static (_, _) => true,
            (exception, _, _, _) => Terminated?.Invoke(exception ??
                new IOException("The Wayland global PTT session or portal owner was lost.")),
            ObserverFlags.EmitOnConnectionDispose, null, null, false).ConfigureAwait(false);

    private static async Task<PortalResponse> ExecutePortalRequestAsync(
        Connection dbus,
        string requestToken,
        Func<Connection, string, MessageBuffer> createRequest,
        CancellationToken cancellationToken)
    {
        string expectedPath = BuildRequestPath(dbus, requestToken);
        var completion = new TaskCompletionSource<PortalResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable responseObserver = await dbus.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Sender = PortalService,
                Path = expectedPath,
                Interface = RequestInterface,
                Member = "Response"
            },
            ReadPortalResponse,
            (exception, response, _, _) =>
            {
                if (exception is null)
                    completion.TrySetResult(response);
                else
                    completion.TrySetException(exception);
            },
            ObserverFlags.EmitOnConnectionDispose,
            null,
            null,
            false).ConfigureAwait(false);

        MessageBuffer requestMessage = createRequest(dbus, requestToken);
        ObjectPath returnedHandle = await dbus.CallMethodAsync(
            requestMessage,
            static (message, _) => message.GetBodyReader().ReadObjectPath(),
            readerState: null).ConfigureAwait(false);
        if (!string.Equals(returnedHandle.ToString(), expectedPath, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "The desktop portal returned a legacy request handle that cannot be observed without a response race.");
        }

        return await completion.Task.WaitAsync(
            PortalResponseTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RegisterHostApplicationAsync(
        Connection dbus,
        CancellationToken cancellationToken)
    {
        if (!ShouldRegisterHostApplication(
                Environment.GetEnvironmentVariable("FLATPAK_ID"),
                Environment.GetEnvironmentVariable("SNAP"),
                File.Exists("/.flatpak-info")))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        MessageBuffer registrationRequest;
        {
            using MessageWriter writer = dbus.GetMessageWriter();
            writer.WriteMethodCallHeader(
                PortalService,
                PortalPath,
                HostRegistryInterface,
                "Register",
                "sa{sv}",
                MessageFlags.None);
            writer.WriteString(ApplicationId);
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
            registrationRequest = writer.CreateMessage();
        }
        try
        {
            await dbus.CallMethodAsync(registrationRequest).ConfigureAwait(false);
        }
        catch (DBusException exception) when (
            exception.ErrorName.ToString() is "org.freedesktop.DBus.Error.UnknownInterface" or
                "org.freedesktop.DBus.Error.UnknownMethod")
        {
            // Portals before the host registry was introduced infer host app
            // identity themselves. Continue to GlobalShortcuts on those hosts.
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static bool ShouldRegisterHostApplication(
        string? flatpakId,
        string? snapPath,
        bool flatpakInfoExists)
        => string.IsNullOrWhiteSpace(flatpakId) &&
            string.IsNullOrWhiteSpace(snapPath) &&
            !flatpakInfoExists;

    private static PortalResponse ReadPortalResponse(Message message, object? state)
    {
        _ = state;
        Reader reader = message.GetBodyReader();
        uint response = reader.ReadUInt32();
        var results = new Dictionary<string, VariantValue>(StringComparer.Ordinal);
        ArrayEnd dictionaryEnd = reader.ReadDictionaryStart();
        while (reader.HasNext(dictionaryEnd))
        {
            string key = reader.ReadString();
            results[key] = reader.ReadVariantValue();
        }
        return new PortalResponse(response, results);
    }

    private static MessageBuffer CreateSessionRequest(
        Connection dbus,
        string requestToken,
        string sessionToken)
    {
        using MessageWriter writer = dbus.GetMessageWriter();
        writer.WriteMethodCallHeader(
            PortalService,
            PortalPath,
            ShortcutsInterface,
            "CreateSession",
            "a{sv}",
            MessageFlags.None);
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["handle_token"] = requestToken,
            ["session_handle_token"] = sessionToken
        });
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateBindShortcutsRequest(
        Connection dbus,
        string requestToken,
        string targetSessionPath,
        KeyboardPttKey key)
    {
        using MessageWriter writer = dbus.GetMessageWriter();
        writer.WriteMethodCallHeader(
            PortalService,
            PortalPath,
            ShortcutsInterface,
            "BindShortcuts",
            "oa(sa{sv})sa{sv}",
            MessageFlags.None);
        writer.WriteObjectPath(targetSessionPath);
        ArrayStart shortcuts = writer.WriteArrayStart(DBusType.Struct);
        writer.WriteStructureStart();
        writer.WriteString(ShortcutId);
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["description"] = "DVM Console push-to-talk",
            ["preferred_trigger"] = ToShortcutTrigger(key)
        });
        writer.WriteArrayEnd(shortcuts);
        writer.WriteString(string.Empty);
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["handle_token"] = requestToken
        });
        return writer.CreateMessage();
    }

    private static string BuildRequestPath(Connection dbus, string token)
    {
        string sender = dbus.UniqueName ??
            throw new InvalidOperationException("The D-Bus session connection has no unique name.");
        return $"/org/freedesktop/portal/desktop/request/{sender.TrimStart(':').Replace('.', '_')}/{token}";
    }

    private static string CreateToken(string prefix)
        => prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    internal static string ToShortcutTrigger(KeyboardPttKey key)
        => key == KeyboardPttKey.Space ? "space" : key.ToString();

    private static void EnsurePortalSuccess(PortalResponse response, string operation)
    {
        if (response.Response != 0)
        {
            throw new InvalidOperationException(
                $"The desktop portal declined the request to {operation} (response {response.Response}).");
        }
    }

    private sealed record PortalResponse(
        uint Response,
        IReadOnlyDictionary<string, VariantValue> Results);

    private sealed record ShortcutSignal(string SessionPath, string ShortcutId);
}
