using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

Console.WriteLine("Gesture Companion Bridge Injector");

var options = Options.Parse(args);
if (options.ShowHelp)
{
    Options.PrintHelp();
    return 1;
}

var target = FindTarget(options.TargetName, options.TargetProcessId);
if (target is null)
{
    Console.Error.WriteLine($"Could not find target process '{options.TargetName}'. Start the drawing app first.");
    return 2;
}

var originalPayloadPath = Path.GetFullPath(options.PayloadPath);
if (!File.Exists(originalPayloadPath))
{
    Console.Error.WriteLine($"Payload DLL not found: {originalPayloadPath}");
    return 3;
}

var payloadPath = MakeTemporaryPayloadCopy(originalPayloadPath, target.Id);

var injectorIs64 = Environment.Is64BitProcess;
var targetIs64 = Native.IsProcess64Bit(target);
Console.WriteLine($"Target: {target.ProcessName}.exe pid={target.Id}");
Console.WriteLine($"Injector: {(injectorIs64 ? "x64" : "x86")}");
Console.WriteLine($"Target: {(targetIs64 ? "x64" : "x86")}");

if (injectorIs64 != targetIs64)
{
    Console.Error.WriteLine("Architecture mismatch. Rebuild/run injector and payload for the same architecture as the target.");
    return 4;
}

Console.WriteLine("Skipping local LoadLibrary test for threaded payload.");

var pipeName = $"GestureCompanionBridge-{target.Id}";
using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

Console.WriteLine($"Injecting payload: {payloadPath}");
Native.InjectDll(target.Id, payloadPath);

Console.WriteLine("Waiting for payload handshake...");
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.HandshakeSeconds));
try
{
    await pipe.WaitForConnectionAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Timed out waiting for payload handshake.");
    Console.Error.WriteLine("Close and reopen SAI/SAI2 if an older payload is already loaded in this process.");
    return 5;
}

using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

var hello = await reader.ReadLineAsync();
Console.WriteLine($"Payload says: {hello}");
var payloadReady = IsSuccessfulPayloadHandshake(hello);

if (!string.IsNullOrWhiteSpace(options.ControlPipeName))
{
    await ServeControlPipeAsync(options.ControlPipeName, reader, writer, hello, payloadReady);
    if (!payloadReady)
    {
        return 6;
    }
}
else if (!payloadReady)
{
    Console.Error.WriteLine("Payload did not confirm that the native pointer hook was installed.");
    return 6;
}
else
{
    Console.WriteLine("Type a command: pinch-open, pinch-close, rotate-cw, rotate-ccw, undo, redo, quit");
    while (true)
    {
        Console.Write("> ");
        var command = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(command))
        {
            continue;
        }

        await writer.WriteLineAsync(command.Trim());
        var response = await reader.ReadLineAsync();
        Console.WriteLine(response ?? "Payload disconnected.");

        if (command.Equals("quit", StringComparison.OrdinalIgnoreCase) || response is null)
        {
            break;
        }
    }
}

return 0;

static bool IsSuccessfulPayloadHandshake(string? handshake)
{
    if (string.IsNullOrWhiteSpace(handshake))
    {
        return false;
    }

    try
    {
        using var document = System.Text.Json.JsonDocument.Parse(handshake);
        var root = document.RootElement;
        return root.TryGetProperty("state", out var state) &&
               state.GetString()?.Equals("ok", StringComparison.OrdinalIgnoreCase) == true &&
               root.TryGetProperty("detail", out var detail) &&
               detail.GetString()?.Equals("native-pointer-hook-installed", StringComparison.Ordinal) == true;
    }
    catch (System.Text.Json.JsonException)
    {
        return false;
    }
}
static async Task ServeControlPipeAsync(string controlPipeName, StreamReader payloadReader, StreamWriter payloadWriter, string? payloadHandshake, bool payloadReady)
{
    Console.WriteLine($"Opening control pipe: {controlPipeName}");
    using var controlPipe = new NamedPipeServerStream(controlPipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    await controlPipe.WaitForConnectionAsync();
    using var controlReader = new StreamReader(controlPipe, Encoding.UTF8, leaveOpen: true);
    using var controlWriter = new StreamWriter(controlPipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    await controlWriter.WriteLineAsync(payloadHandshake ?? "{\"state\":\"error\",\"detail\":\"missing-payload-handshake\"}");
    if (!payloadReady)
    {
        return;
    }

    while (await controlReader.ReadLineAsync() is { } command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            continue;
        }

        await payloadWriter.WriteLineAsync(command.Trim());
        var response = await payloadReader.ReadLineAsync() ?? "Payload disconnected.";
        await controlWriter.WriteLineAsync(response);
        Console.WriteLine($"control {command} -> {response}");

        if (command.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }
    }
}
static Process? FindTarget(string targetName, int? targetProcessId)
{
    if (targetProcessId is int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            var expectedName = Path.GetFileNameWithoutExtension(targetName);
            if (process.ProcessName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return process;
            }

            process.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }
    var trimmed = Path.GetFileNameWithoutExtension(targetName);
    return Process.GetProcessesByName(trimmed)
        .OrderByDescending(p =>
        {
            try { return p.StartTime; }
            catch { return DateTime.MinValue; }
        })
        .FirstOrDefault();
}
static string MakeTemporaryPayloadCopy(string payloadPath, int targetProcessId)
{
    var tempDirectory = Path.Combine(Path.GetTempPath(), "GestureCompanionBridge");
    Directory.CreateDirectory(tempDirectory);

    var tempPayloadPath = Path.Combine(
        tempDirectory,
        $"GestureCompanionBridge.NativePayload.{targetProcessId}.{Guid.NewGuid():N}.dll");

    File.Copy(payloadPath, tempPayloadPath, overwrite: true);
    return tempPayloadPath;
}
sealed record Options(string TargetName, int? TargetProcessId, string PayloadPath, int HandshakeSeconds, string? ControlPipeName, bool ShowHelp)
{
    public static Options Parse(string[] args)
    {
        var target = "sai2";
        int? targetProcessId = null;
        var payload = "GestureCompanionBridge.NativePayload.dll";
        var handshake = 10;
        string? controlPipe = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--target" when i + 1 < args.Length:
                    target = args[++i];
                    break;
                case "--pid" when i + 1 < args.Length && int.TryParse(args[++i], out var parsedProcessId):
                    targetProcessId = parsedProcessId;
                    break;
                case "--payload" when i + 1 < args.Length:
                    payload = args[++i];
                    break;
                case "--handshake-seconds" when i + 1 < args.Length && int.TryParse(args[++i], out var parsed):
                    handshake = parsed;
                    break;
                case "--control-pipe" when i + 1 < args.Length:
                    controlPipe = args[++i];
                    break;
                case "-h":
                case "--help":
                    return new Options(target, targetProcessId, payload, handshake, controlPipe, true);
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        payload = args[i];
                    }
                    break;
            }
        }

        return new Options(target, targetProcessId, payload, handshake, controlPipe, false);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  GestureCompanionBridge.Injector.exe --target sai2 [--pid 1234] --payload C:\\path\\payload.dll");
    }
}
static partial class Native
{
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVirtualMemoryOperation = 0x0008;
    private const uint ProcessVirtualMemoryWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0x00000000;
    private const uint Infinite = 0xFFFFFFFF;

    public static void InjectDll(int processId, string dllPath)
    {
        var dllBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
        var process = OpenProcess(ProcessCreateThread | ProcessQueryInformation | ProcessVirtualMemoryOperation | ProcessVirtualMemoryWrite, false, processId);
        if (process == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess failed.");
        }

        try
        {
            var remoteMemory = VirtualAllocEx(process, IntPtr.Zero, (nuint)dllBytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remoteMemory == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx failed.");
            }

            if (!WriteProcessMemory(process, remoteMemory, dllBytes, (nuint)dllBytes.Length, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory failed.");
            }

            var kernel32 = GetModuleHandle("kernel32.dll");
            var loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcAddress(LoadLibraryW) failed.");
            }

            var thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibrary, remoteMemory, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread failed.");
            }

            try
            {
                if (WaitForSingleObject(thread, Infinite) != WaitObject0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject failed.");
                }

                if (!GetExitCodeThread(thread, out var moduleHandle) || moduleHandle == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Remote LoadLibraryW returned NULL.");
                }

                Console.WriteLine($"Remote module handle: 0x{moduleHandle:X}");
            }
            finally
            {
                CloseHandle(thread);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static void VerifyLocalLoad(string dllPath)
    {
        var module = LoadLibrary(dllPath);
        if (module == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Local LoadLibrary test failed.");
        }

        FreeLibrary(module);
        Console.WriteLine("Local LoadLibrary test OK.");
    }

    public static bool IsProcess64Bit(Process process)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return false;
        }

        if (!IsWow64Process(process.Handle, out var isWow64))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "IsWow64Process failed.");
        }

        return !isWow64;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, nuint size, out nuint bytesWritten);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string moduleName);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetProcAddress(IntPtr module, string procName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes, nuint stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, IntPtr threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadLibrary(string fileName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(IntPtr module);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);
}









