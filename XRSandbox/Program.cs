using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Serilog;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.HTCX;

const string outputTemplate = "[{Timestamp:HH:mm:ss} {Level}] {Message:l}{NewLine}{Exception}";

var localNowStr = DateTime.UtcNow.ToLocalTime()
    .ToString(CultureInfo.InvariantCulture.DateTimeFormat.SortableDateTimePattern, CultureInfo.InvariantCulture);

var sanitizedNowStr = new string(localNowStr
    .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
    .ToArray());
var logFilename = $"xrsandbox-{sanitizedNowStr}.txt";


Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: outputTemplate)
    .WriteTo.File(logFilename, outputTemplate: outputTemplate)
    .MinimumLevel.Debug()
    .CreateLogger();

#region OpenXR

Log.Information("[{LogTag}] Loading OpenXR.", "OpenXR");

var xr = XR.GetApi();

HtcxViveTrackerInteraction htcx;// = new HtcxViveTrackerInteraction(xr.Context);

unsafe {
    ISet<string> supportedExtensions = new HashSet<string>();

    uint instanceExtensionCount = 0;
    xr.EnumerateInstanceExtensionProperties((byte*) IntPtr.Zero, instanceExtensionCount, &instanceExtensionCount, null);
    Span<ExtensionProperties> exts = stackalloc ExtensionProperties[(int) instanceExtensionCount];
    for (var i = 0; i < instanceExtensionCount; i++) {
        exts[i] = new ExtensionProperties(StructureType.TypeExtensionProperties);
    }

    xr.EnumerateInstanceExtensionProperties((string) null, ref instanceExtensionCount, exts);

    foreach (var extensionProp in exts) {
        string name;
        unsafe { name = Marshal.PtrToStringUTF8((IntPtr) extensionProp.ExtensionName); }

        supportedExtensions.Add(name);
        Log.Information("[{LogTag}][Instance] Extension: Name={Name} Version={Version}", "OpenXR", name, extensionProp.ExtensionVersion);
    }

    var ici = new InstanceCreateInfo(StructureType.InstanceCreateInfo) {
        EnabledApiLayerCount = 0,
        EnabledApiLayerNames = null,
    };

    var extensions = new List<string>();
    // if (supportedExtensions.Contains("XR_MND_headless")) {
    //     extensions.Add("XR_MND_headless");
    // } else {
    //     Log.Error("[{LogTag}] XR_MND_headless extension not supported!", "OpenXR");
    // }
    if (supportedExtensions.Contains("XR_HTCX_vive_tracker_interaction")) {
        extensions.Add("XR_HTCX_vive_tracker_interaction");
    } else {
        Log.Error("[{LogTag}] XR_HTCX_vive_tracker_interaction extension not supported!", "OpenXR");
    }

    var instance = new Instance();

    var ansiExtensions = extensions.Select(e => Marshal.StringToHGlobalAnsi(e)).ToArray();
    fixed (IntPtr* fixedAnsiExtensions = ansiExtensions) {
        ici.EnabledExtensionCount = (uint) extensions.Count;
        ici.EnabledExtensionNames = (byte**) fixedAnsiExtensions;

        const string appname = "OpenXR Sandbox";
        ici.ApplicationInfo = new ApplicationInfo() {
            ApiVersion = 1ul << 48,
            ApplicationVersion = 1,
            EngineVersion = 1,
        };
        Marshal.Copy(appname.ToCharArray(), 0, (IntPtr) ici.ApplicationInfo.ApplicationName, appname.Length);
        ici.ApplicationInfo.EngineName[0] = (byte) '\0';

        xr.CreateInstance(ici, ref instance);
        xr.CurrentInstance = instance;

        xr.TryGetInstanceExtension(null, instance, out htcx);
        // htcx.CurrentVTable.Load("xrEnumerateViveTrackerPathsHTCX");
    }

    // INSTANCE
    var instanceProperties = new InstanceProperties(StructureType.InstanceProperties);
    xr.GetInstanceProperties(instance, ref instanceProperties);
    var runtimeName = Marshal.PtrToStringUTF8((IntPtr) instanceProperties.RuntimeName);
    Log.Information("[{LogTag}][Instance] Runtime: Name={Name} Version={Version}", "OpenXR", runtimeName,
        instanceProperties.RuntimeVersion);

    // SYSTEM
    var systemGetInfo = new SystemGetInfo(StructureType.SystemGetInfo) {FormFactor = FormFactor.HeadMountedDisplay};
    ulong systemId = 0;
    xr.GetSystem((Instance) xr.CurrentInstance, &systemGetInfo, (ulong*) &systemId);
    Log.Information("[{LogTag}][System] Id={SystemId}", "OpenXR", systemId);

    var systemProperties = new SystemProperties(StructureType.SystemProperties);
    xr.GetSystemProperties(instance, systemId, ref systemProperties);
    var systemName = Marshal.PtrToStringUTF8((IntPtr) systemProperties.SystemName);
    Log.Information("[{LogTag}][System] Name={Name}", "OpenXR", systemName);
    
    foreach (var ansiExtension in ansiExtensions) { Marshal.FreeHGlobal(ansiExtension); }
}

var inputQueue = new ConcurrentQueue<string>();
SpawnThreadAndWaitForInput();

void SpawnThreadAndWaitForInput() {
    var thread = new Thread(ThreadFunc) {
        Name = "XRSandboxThread",
        Priority = ThreadPriority.BelowNormal,
        IsBackground = true,
    };
    thread.Start();
    while (true) {
        var input = Console.ReadLine();
        if (input == null) break;
        inputQueue.Enqueue(input);
    }
}

void ThreadFunc() {
    while (true) {
        while (inputQueue.TryDequeue(out var input)) {
            Log.Information("[{LogTag}] Input: {Input}", "XRSandbox", input);
            if (input == "exit") {
                Log.Information("[{LogTag}] Exiting.", "XRSandbox");
                return;
            } else if (input == "list") {
                unsafe {
                    var i = xr.CurrentInstance.Value;
                    
                    var span = stackalloc ViveTrackerPathsHTCX[11];
                    uint pathcap = 0; // Normally I'd do the two-step checked-size allocation, but this is a test
                    htcx.EnumerateViveTrackerPathsHtcx(i, 11, ref pathcap, span); 

                    Log.Information("[{LogTag}] Number of Vive Tracker paths: {PathCount}", "XRSandbox", pathcap);
                }
            } else {
                Log.Warning("[{LogTag}] Unknown command: {Input}", "XRSandbox", input);
            }
        }
        Thread.Sleep(0);
    }
}

#endregion
