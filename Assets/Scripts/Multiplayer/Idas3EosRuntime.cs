#if IDAS3_EOS && UNITY_ANDROID
using System;
using System.IO;
using System.Threading.Tasks;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Platform;
using UnityEngine;

namespace Idas3.Multiplayer
{
    // One platform handle owns the device identity. It keeps ticking while a
    // cancelled room's outstanding SDK requests finish and clean themselves up.
    internal sealed class Idas3EosRuntime : MonoBehaviour
    {
        [Serializable] internal sealed class Config
        {
            public string productId, sandboxId, deploymentId, clientId, clientSecret;
            internal bool Valid => !string.IsNullOrWhiteSpace(productId) && !string.IsNullOrWhiteSpace(sandboxId) &&
                !string.IsNullOrWhiteSpace(deploymentId) && !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);
        }
        static Idas3EosRuntime instance;
        Task login;
        ulong expirationNotification;
        internal PlatformInterface Platform { get; private set; }
        internal ProductUserId User { get; private set; }
        internal static bool Configured
        {
            get {
                if (Application.platform != RuntimePlatform.Android) return false;
                var asset = Resources.Load<TextAsset>("Idas3EOS");
                if (asset == null) return false;
                try { return JsonUtility.FromJson<Config>(asset.text)?.Valid == true; }
                catch { return false; }
            }
        }

        internal static Idas3EosRuntime Get()
        {
            if (instance != null) return instance;
            var go = new GameObject("IDAS3 Internet Services");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<Idas3EosRuntime>();
            try { instance.Initialize(); }
            catch { Destroy(go); instance = null; throw; }
            return instance;
        }

        void Initialize()
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var eos = new AndroidJavaClass("com.epicgames.mobile.eossdk.EOSSDK"))
                eos.CallStatic("init", activity);
            var init = new AndroidInitializeOptions {
                ProductName = "Initial D Reimagined", ProductVersion = "1.0",
                SystemInitializeOptions = new AndroidInitializeOptionsSystemInitializeOptions {
                    OptionalInternalDirectory = Application.persistentDataPath,
                    OptionalExternalDirectory = Application.temporaryCachePath
                }
            };
            Check(PlatformInterface.Initialize(ref init), "initialize");
            var config = JsonUtility.FromJson<Config>(Resources.Load<TextAsset>("Idas3EOS").text);
            string cache = Path.Combine(Application.persistentDataPath, "eos-cache");
            Directory.CreateDirectory(cache);
            var options = new Epic.OnlineServices.Platform.Options {
                ProductId = config.productId, SandboxId = config.sandboxId, DeploymentId = config.deploymentId,
                ClientCredentials = new ClientCredentials { ClientId = config.clientId, ClientSecret = config.clientSecret },
                CacheDirectory = cache, IsServer = false,
                Flags = PlatformFlags.DisableOverlay | PlatformFlags.DisableSocialOverlay,
                TickBudgetInMilliseconds = 3, TaskNetworkTimeoutSeconds = 20
            };
            Platform = PlatformInterface.Create(ref options);
            if (Platform == null) { PlatformInterface.Shutdown(); throw new InvalidOperationException("EOS platform unavailable."); }
            var notify = new AddNotifyAuthExpirationOptions();
            expirationNotification = Platform.GetConnectInterface().AddNotifyAuthExpiration(ref notify, null,
                (ref AuthExpirationCallbackInfo info) => { User = null; login = Login(); });
        }

        internal Task Authenticate()
        {
            if (User != null && User.IsValid() && Platform.GetConnectInterface().GetLoginStatus(User) == LoginStatus.LoggedIn)
                return Task.CompletedTask;
            if (login == null || login.IsCompleted) login = Login();
            return login;
        }

        async Task Login()
        {
            var connect = Platform.GetConnectInterface();
            var device = new CreateDeviceIdOptions { DeviceModel = "IDAS3 Android" };
            var made = await Callback<CreateDeviceIdCallbackInfo>(done => connect.CreateDeviceId(ref device, null,
                (ref CreateDeviceIdCallbackInfo info) => done(info)));
            if (made.ResultCode != Result.DuplicateNotAllowed) Check(made.ResultCode, "device identity");
            var options = new LoginOptions {
                Credentials = new Credentials { Type = ExternalCredentialType.DeviceidAccessToken },
                UserLoginInfo = new UserLoginInfo { DisplayName = "Driver" }
            };
            var signedIn = await Callback<LoginCallbackInfo>(done => connect.Login(ref options, null,
                (ref LoginCallbackInfo info) => done(info)));
            if (signedIn.ResultCode == Result.InvalidUser && signedIn.ContinuanceToken != null) {
                var create = new CreateUserOptions { ContinuanceToken = signedIn.ContinuanceToken };
                var created = await Callback<CreateUserCallbackInfo>(done => connect.CreateUser(ref create, null,
                    (ref CreateUserCallbackInfo info) => done(info)));
                Check(created.ResultCode, "create player"); User = created.LocalUserId;
            } else { Check(signedIn.ResultCode, "sign in"); User = signedIn.LocalUserId; }
            if (User == null || !User.IsValid()) throw new InvalidOperationException("EOS sign-in returned no player.");
            Debug.Log("IDAS3_EOS sign-in succeeded");
        }

        internal static async Task<T> Callback<T>(Action<Action<T>> start)
        {
            var completion = new TaskCompletionSource<T>();
            start(value => completion.TrySetResult(value));
            if (await Task.WhenAny(completion.Task, Task.Delay(25000)) != completion.Task)
                throw new TimeoutException("EOS request timed out.");
            return await completion.Task;
        }

        internal static void Check(Result result, string operation)
        {
            // Error codes are useful on-device; never include raw SDK logs or tokens.
            if (result != Result.Success) throw new InvalidOperationException("EOS " + operation + ": " + result);
        }

        void Update() { Platform?.Tick(); }
        void OnApplicationPause(bool paused)
        {
            Platform?.SetApplicationStatus(paused ? ApplicationStatus.BackgroundConstrained : ApplicationStatus.Foreground);
        }
        void OnDestroy()
        {
            if (Platform == null) return;
            if (expirationNotification != 0) Platform.GetConnectInterface().RemoveNotifyAuthExpiration(expirationNotification);
            Platform.Release(); Platform = null; PlatformInterface.Shutdown();
            if (instance == this) instance = null;
        }
    }
}
#endif
