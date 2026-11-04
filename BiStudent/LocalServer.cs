using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace BiStudent;

internal static class LocalServer
{
    public const string Url = "http://localhost:5080";

    private static readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly object sync = new();
    private static Task? launchTask;
    private static Process? process;
    private static IntPtr jobHandle;
    private static bool launchFailed;

    public static string? ExecutablePath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Server", "BiStudent.Server.exe");
            return File.Exists(path) ? path : null;
        }
    }

    public static bool IsBundled => ExecutablePath is not null;

    // Наличие Server рядом с exe не должно перехватывать подключение к облачному серверу.
    internal static bool IsLocalAddress(string? address) =>
        Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && uri.Port == 5080 &&
        uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0;

    public static bool ShouldStart(string? address) => IsBundled && IsLocalAddress(address);

    public static void Start()
    {
        if (ExecutablePath is not { } executable) return;
        lock (sync)
            launchTask ??= Task.Run(() => LaunchAsync(executable));
    }

    public static async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!IsBundled) return true;
        Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var task = launchTask;
            if (task is not null) await task.WaitAsync(deadline.Token);
            while (!deadline.IsCancellationRequested)
            {
                if (launchFailed || process is { HasExited: true }) return false;
                if (await IsHealthyAsync(TimeSpan.FromSeconds(2), deadline.Token)) return true;
                if (launchFailed || process is { HasExited: true }) return false;
                await Task.Delay(400, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return false;
    }

    private static async Task LaunchAsync(string executable)
    {
        if (await IsHealthyAsync(TimeSpan.FromSeconds(1), CancellationToken.None)) return;
        Process? started = null;
        try
        {
            started = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    Arguments = "--urls http://*:5080",
                    WorkingDirectory = Path.GetDirectoryName(executable)!,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            started.StartInfo.Environment["Demo__Enabled"] = "true";
            if (!started.Start() || !AttachToJob(started))
                throw new InvalidOperationException("Не удалось запустить сервер BiStudent.");
            process = started;
        }
        catch
        {
            launchFailed = true;
            if (started is not null)
            {
                try { if (!started.HasExited) started.Kill(entireProcessTree: true); }
                catch { }
                started.Dispose();
            }
        }
    }

    private static async Task<bool> IsHealthyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Windows отклоняет подключение к закрытому порту только через ~2 с, поэтому сначала смотрим, слушает ли его кто-нибудь.
        var port = new Uri(Url).Port;
        if (!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port))
            return false;
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(timeout);
        try
        {
            using var response = await http.GetAsync($"{Url}/health", HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private static bool AttachToJob(Process started)
    {
        if (jobHandle != IntPtr.Zero) return true;
        var job = IntPtr.Zero;
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return false;
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x2000 }
            };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())
                || !AssignProcessToJobObject(job, started.Handle)) return false;
            jobHandle = job;
            job = IntPtr.Zero;
            return true;
        }
        catch { return false; }
        finally { if (job != IntPtr.Zero) CloseHandle(job); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass,
        ref JobObjectExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
