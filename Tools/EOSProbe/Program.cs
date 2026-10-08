using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.Logging;
using Epic.OnlineServices.Platform;

internal static partial class Program
{
    private static readonly string[] Required = {
        "EOS_PRODUCT_ID", "EOS_SANDBOX_ID", "EOS_DEPLOYMENT_ID", "EOS_CLIENT_ID", "EOS_CLIENT_SECRET"
    };
    private static PlatformInterface platform;
    private static LobbyInterface lobbies;
    private static ProductUserId user;
    private static string ownedLobby;
    private static bool initialized;
    private static bool cleanupOk = true;
    private static bool stopRequested;
    private static bool createInFlight;
    private static string phase = "configuration";
    private static readonly Stopwatch total = Stopwatch.StartNew();
    private static int sdkDiagnosticCount;
    private static readonly string[] DiagnosticTerms = {
        "relay", "turn", "auth", "p2p", "signaling", "permission", "scope", "policy",
        "denied", "forbidden", "unauthorized", "timeout", "connection", "certificate",
        "network", "socket", "udp", "invalid", "failed", "unavailable", "not configured"
    };

    private static int Main(string[] args)
    {
        // Never serialize arbitrary exception messages or SDK logs: they can include credentials.
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopRequested = true; };
        if (args.Length != 0) { Emit("arguments", "UnsupportedArguments"); return 2; }
        if (!ReadRole()) return 2;
        var settings = Required.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        if (settings.Values.Any(string.IsNullOrWhiteSpace)) {
            Emit("configuration", "MissingEnvironmentVariables");
            return 2;
        }
        bool passed = false;
        try {
            var init = new InitializeOptions { ProductName = "IDAS3 EOS Availability Probe", ProductVersion = "1.0" };
            Expect("initialize", PlatformInterface.Initialize(ref init));
            initialized = true;
            Expect("loggingCallback", LoggingInterface.SetCallback(OnSdkLog));
            Expect("loggingWarnings", LoggingInterface.SetLogLevel(LogCategory.AllCategories, LogLevel.Warning));
            var options = new Options {
                ProductId = settings["EOS_PRODUCT_ID"], SandboxId = settings["EOS_SANDBOX_ID"],
                DeploymentId = settings["EOS_DEPLOYMENT_ID"],
                ClientCredentials = new ClientCredentials {
                    ClientId = settings["EOS_CLIENT_ID"], ClientSecret = settings["EOS_CLIENT_SECRET"]
                },
                IsServer = false, Flags = PlatformFlags.DisableOverlay | PlatformFlags.DisableSocialOverlay,
                CacheDirectory = Path.Combine(AppContext.BaseDirectory, "probe-cache"),
                TickBudgetInMilliseconds = 5, TaskNetworkTimeoutSeconds = 20
            };
            phase = "platformCreate";
            platform = PlatformInterface.Create(ref options);
            if (platform == null) throw new ProbeFailure("NullPlatform");
            Emit(phase, "Success");
            Authenticate();
            lobbies = platform.GetLobbyInterface();
            if (role == "single") CreateAndFindLobby();
            else RunPeerProbe();
            passed = true;
        } catch (ProbeFailure ex) {
            Emit(phase, ex.Code);
        } catch (Exception ex) {
            Emit(phase, ex.GetType().Name);
        } finally {
            Cleanup();
        }
        Emit("summary", passed && cleanupOk ? "Passed" : "Failed", new {
            hasPuid = user != null, cleanupOk, relayedConnection, reliableReceived,
            unreliableReceived, reliableAcked, unreliableAcked, completionHandshake
        });
        return passed && cleanupOk ? 0 : 1;
    }

    private static void Authenticate()
    {
        var connect = platform.GetConnectInterface();
        bool done = false;
        var result = Result.UnexpectedError;
        var device = new CreateDeviceIdOptions { DeviceModel = "Windows IDAS3 diagnostic probe" };
        phase = "createDeviceId";
        connect.CreateDeviceId(ref device, null, (ref CreateDeviceIdCallbackInfo info) => {
            result = info.ResultCode; done = true;
        });
        Wait(() => done);
        Emit(phase, result.ToString());
        if (result != Result.Success && result != Result.DuplicateNotAllowed) throw new ProbeFailure("DeviceIdFailed");
        // Reuse any existing device identity. Never delete or reset it.
        done = false;
        ContinuanceToken continuation = null;
        var login = new LoginOptions {
            Credentials = new Credentials { Type = ExternalCredentialType.DeviceidAccessToken },
            UserLoginInfo = new UserLoginInfo { DisplayName = "IDAS3 Probe" }
        };
        phase = "connectLogin";
        connect.Login(ref login, null, (ref LoginCallbackInfo info) => {
            result = info.ResultCode; user = info.LocalUserId; continuation = info.ContinuanceToken; done = true;
        });
        Wait(() => done);
        Emit(phase, result.ToString());
        if (result == Result.InvalidUser && continuation != null) {
            done = false;
            var create = new CreateUserOptions { ContinuanceToken = continuation };
            phase = "connectCreateUser";
            connect.CreateUser(ref create, null, (ref CreateUserCallbackInfo info) => {
                result = info.ResultCode; user = info.LocalUserId; done = true;
            });
            Wait(() => done);
            Expect(phase, result);
        } else if (result != Result.Success) throw new ProbeFailure("LoginFailed");
        if (user == null || !user.IsValid()) throw new ProbeFailure("MissingPuid");
        Emit("identity", "Success", new { hasPuid = true });
    }

    private static void CreateAndFindLobby()
    {
        var create = new CreateLobbyOptions {
            LocalUserId = user, MaxLobbyMembers = 2, PermissionLevel = LobbyPermissionLevel.Publicadvertised,
            PresenceEnabled = false, AllowInvites = false, BucketId = "idas3-eos-probe-v1",
            DisableHostMigration = true, EnableRTCRoom = false, CrossplayOptOut = false
        };
        bool done = false;
        Result result = Result.UnexpectedError;
        phase = "createLobby";
        createInFlight = true;
        lobbies.CreateLobby(ref create, null, (ref CreateLobbyCallbackInfo info) => {
            result = info.ResultCode;
            if (result == Result.Success) ownedLobby = info.LobbyId;
            createInFlight = false;
            done = true;
        });
        Wait(() => done);
        Expect(phase, result);
        if (string.IsNullOrEmpty(ownedLobby)) throw new ProbeFailure("MissingLobbyId");

        string marker = role == "single" ? Guid.NewGuid().ToString("N") : runId;
        var modificationOptions = new UpdateLobbyModificationOptions { LocalUserId = user, LobbyId = ownedLobby };
        Expect("updateLobbyModification", lobbies.UpdateLobbyModification(ref modificationOptions, out var modification));
        try {
            var add = new LobbyModificationAddAttributeOptions {
                Attribute = new AttributeData { Key = "IDAS3_PROBE", Value = marker },
                Visibility = LobbyAttributeVisibility.Public
            };
            Expect("addAttribute", modification.AddAttribute(ref add));
            done = false;
            var update = new UpdateLobbyOptions { LobbyModificationHandle = modification };
            phase = "updateLobby";
            lobbies.UpdateLobby(ref update, null, (ref UpdateLobbyCallbackInfo info) => { result = info.ResultCode; done = true; });
            Wait(() => done);
            Expect(phase, result);
        } finally { modification.Release(); }

        if (role == "host") { Emit("hostReady", "Success"); return; }

        // Search indexing is asynchronous. Give it a bounded interval to become visible.
        var deadline = Stopwatch.StartNew();
        bool found = false;
        while (!found && deadline.Elapsed < TimeSpan.FromSeconds(30)) {
            var searchOptions = new CreateLobbySearchOptions { MaxResults = 4 };
            Expect("createLobbySearch", lobbies.CreateLobbySearch(ref searchOptions, out var search));
            try {
                var parameter = new LobbySearchSetParameterOptions {
                    Parameter = new AttributeData { Key = "IDAS3_PROBE", Value = marker }, ComparisonOp = ComparisonOp.Equal
                };
                Expect("setSearchParameter", search.SetParameter(ref parameter));
                done = false;
                var find = new LobbySearchFindOptions { LocalUserId = user };
                phase = "findLobby";
                search.Find(ref find, null, (ref LobbySearchFindCallbackInfo info) => { result = info.ResultCode; done = true; });
                Wait(() => done);
                Expect(phase, result);
                var countOptions = new LobbySearchGetSearchResultCountOptions();
                uint count = search.GetSearchResultCount(ref countOptions);
                for (uint i = 0; i < count; i++) {
                    var copy = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = i };
                    Expect("copySearchResult", search.CopySearchResultByIndex(ref copy, out var details));
                    try {
                        var infoOptions = new LobbyDetailsCopyInfoOptions();
                        Expect("readLobby", details.CopyInfo(ref infoOptions, out var info));
                        found = info.HasValue && (string)info.Value.LobbyId == ownedLobby && info.Value.MaxMembers == 2;
                        if (found) break;
                    } finally { details.Release(); }
                }
            } finally { search.Release(); }
            if (!found) PumpFor(1000);
        }
        phase = "verifyOwnLobbySearch";
        if (!found) throw new ProbeFailure("OwnLobbyNotFound");
        Emit(phase, "Success");
    }

    private static void Cleanup()
    {
        CleanupPeer();
        // A successful create arriving after cancellation still owns a remote lobby.
        if (platform != null && createInFlight) {
            try {
                phase = "settlePendingLobbyCreate";
                Wait(() => !createInFlight, 30, cleanup: true);
            } catch (Exception ex) {
                cleanupOk = false;
                Emit(phase, ex is ProbeFailure failure ? failure.Code : ex.GetType().Name);
            }
        }
        if (platform != null && lobbies != null && !string.IsNullOrEmpty(ownedLobby)) {
            try {
                bool done = false;
                var result = Result.UnexpectedError;
                var destroy = new DestroyLobbyOptions { LocalUserId = user, LobbyId = ownedLobby };
                lobbies.DestroyLobby(ref destroy, null, (ref DestroyLobbyCallbackInfo info) => {
                    result = info.ResultCode; done = true;
                });
                phase = "destroyLobby";
                Wait(() => done, 15, cleanup: true);
                Emit(phase, result.ToString());
                cleanupOk &= result == Result.Success || result == Result.NotFound;
            } catch (Exception ex) {
                cleanupOk = false;
                Emit("destroyLobby", ex is ProbeFailure failure ? failure.Code : ex.GetType().Name);
            }
        }
        try { platform?.Release(); platform = null; }
        catch (Exception ex) { cleanupOk = false; Emit("platformRelease", ex.GetType().Name); }
        if (initialized) {
            var result = PlatformInterface.Shutdown();
            Emit("shutdown", result.ToString());
            cleanupOk &= result == Result.Success;
        }
    }

    private static void Wait(Func<bool> complete, int seconds = 30, bool cleanup = false)
    {
        var timer = Stopwatch.StartNew();
        while (!complete()) {
            if ((!cleanup && stopRequested) || timer.Elapsed > TimeSpan.FromSeconds(seconds))
                throw new ProbeFailure(stopRequested ? "Cancelled" : "TimedOut");
            platform.Tick();
            Thread.Sleep(10);
        }
    }

    private static void PumpFor(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { platform.Tick(); Thread.Sleep(10); }
    }

    private static void Expect(string step, Result result)
    {
        phase = step;
        Emit(step, result.ToString());
        if (result != Result.Success) throw new ProbeFailure("OperationFailed");
    }

    private static void OnSdkLog(ref LogMessage message)
    {
        // Extract error identifiers and fixed diagnostic terms, never the SDK's
        // original text: OAuth/P2P logs may contain credentials or player IDs.
        if (Interlocked.Increment(ref sdkDiagnosticCount) > 80) return;
        string text = message.Message ?? "";
        string category = message.Category ?? "";
        if (!Regex.IsMatch(category, "^Log[A-Za-z0-9_]{1,40}$")) category = "SDK";
        var codes = Regex.Matches(text, @"\b(?:EOS_[A-Za-z0-9_]+|errors\.com\.epicgames\.[a-z0-9_.]+)\b")
            .Select(match => match.Value).Distinct().Take(8).ToArray();
        var http = Regex.Matches(text, @"(?i)(?:http|status(?:code| code)?|response code)[^0-9\r\n]{0,16}(?<code>[1-5][0-9]{2})\b")
            .Select(match => match.Groups["code"].Value).Distinct().ToArray();
        var terms = DiagnosticTerms.Where(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        Emit("sdkDiagnostic", message.Level.ToString(), new { category, codes, http, terms });
    }

    private static void Emit(string step, string result, object detail = null) =>
        Console.WriteLine(JsonSerializer.Serialize(new { phase = step, result, role, elapsedMs = total.ElapsedMilliseconds, detail }));

    private sealed class ProbeFailure(string code) : Exception { public string Code { get; } = code; }
}
