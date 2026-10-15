[CmdletBinding()]
param(
    [string]$Executable = '',
    [string[]]$Arguments = @(),
    [Parameter(Mandatory = $true)][string]$LogPath,
    [ValidateRange(1, 86400)][int]$TimeoutSeconds = 1800
)

. (Join-Path $PSScriptRoot '..\project_materials.ps1')

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'LarkControl.slnx') -PathType Leaf)) {
    throw 'Run this script from the tools directory of a Hollylark checkout.'
}

function Resolve-ValidationPath {
    param([string]$Path, [string]$RequiredRoot)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A non-empty filesystem path is required.' }
    if ($Path -match '^[A-Za-z]:[^\\/]') { throw "Drive-relative paths are not supported: $Path" }
    if (-not [IO.Path]::IsPathRooted($Path)) { $Path = Join-ProjectMaterialPath $projectRoot $Path }
    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootPrefix = $RequiredRoot.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the permitted directory $RequiredRoot : $fullPath"
    }
    foreach ($component in $fullPath.Substring(3).Split('\')) {
        if ($component.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            $component.EndsWith('.') -or $component.EndsWith(' ') -or
            $component -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$') {
            throw "Unsupported Windows path component: $component"
        }
    }
    $ancestor = $fullPath
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            $entry = Get-Item -LiteralPath $ancestor -Force
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse points are not permitted for validation paths: $ancestor"
            }
        }
        if ($ancestor.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $fullPath
}

if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = 'Workspace\Build\bin\Hollylark.Validation\Release\net10.0-windows\Hollylark.Validation.exe'
}
$executablePath = Resolve-ValidationPath -Path $Executable -RequiredRoot (Join-ProjectMaterialPath $projectRoot 'Workspace')
$resolvedLogPath = Resolve-ValidationPath -Path $LogPath -RequiredRoot (Join-ProjectMaterialPath $projectRoot 'Workspace')
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf) -or
    [IO.Path]::GetExtension($executablePath) -ine '.exe') {
    throw "The validator must be an existing executable inside the project: $executablePath"
}
if (Test-Path -LiteralPath $resolvedLogPath) { throw "Refusing to overwrite an existing validation log: $resolvedLogPath" }
foreach ($argument in $Arguments) {
    if ($null -eq $argument -or $argument.IndexOf([char]0) -ge 0) { throw 'Arguments cannot be null or contain NUL.' }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedLogPath)) | Out-Null

$nativeSource = @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Hollylark.ValidationDesktop
{
    public sealed class RunResult
    {
        public string DesktopName { get; internal set; }
        public uint ProcessId { get; internal set; }
        public int ExitCode { get; internal set; }
        public bool TimedOut { get; internal set; }
        public string[] CleanupErrors { get; internal set; }
    }

    public static class Runner
    {
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);
        private const uint WaitObject = 0;
        private const uint WaitTimeout = 258;
        private const uint KillOnJobClose = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved;
            public string Desktop;
            public string Title;
            public uint PositionX;
            public uint PositionY;
            public uint Width;
            public uint Height;
            public uint CharacterWidth;
            public uint CharacterHeight;
            public uint FillAttribute;
            public uint Flags;
            public ushort ShowWindow;
            public ushort ReservedSize;
            public IntPtr ReservedData;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo Startup;
            public IntPtr Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicJobLimits
        {
            public long ProcessTime;
            public long JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet;
            public UIntPtr MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperations;
            public ulong WriteOperations;
            public ulong OtherOperations;
            public ulong ReadBytes;
            public ulong WriteBytes;
            public ulong OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedJobLimits
        {
            public BasicJobLimits Basic;
            public IoCounters Counters;
            public UIntPtr ProcessMemory;
            public UIntPtr JobMemory;
            public UIntPtr PeakProcessMemory;
            public UIntPtr PeakJobMemory;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktopW(string desktop, IntPtr device, IntPtr deviceMode,
            uint flags, uint desiredAccess, IntPtr securityAttributes);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint sharing,
            ref SecurityAttributes security, uint disposition, uint attributes, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(string application, StringBuilder commandLine,
            IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags, IntPtr environment, string directory, ref StartupInfoEx startup,
            out ProcessInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count,
            int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, UIntPtr attribute,
            IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr securityAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass,
            ref ExtendedJobLimits information, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private static Win32Exception NativeFailure(string operation)
        {
            int error = Marshal.GetLastWin32Error();
            return new Win32Exception(error, operation + ": " + new Win32Exception(error).Message);
        }

        private static string QuoteArgument(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid process argument.");
            StringBuilder quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { backslashes++; continue; }
                if (character == '"') quoted.Append('\\', backslashes * 2 + 1);
                else quoted.Append('\\', backslashes);
                quoted.Append(character);
                backslashes = 0;
            }
            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        private static void CloseOwnedHandle(ref IntPtr handle, string name, List<string> errors)
        {
            if (handle == IntPtr.Zero || handle == InvalidHandle) return;
            if (!CloseHandle(handle)) errors.Add(NativeFailure("Close " + name).Message);
            handle = IntPtr.Zero;
        }

        public static RunResult Run(string executable, string[] arguments, string logPath, int timeoutSeconds)
        {
            if (timeoutSeconds < 1 || timeoutSeconds > 86400) throw new ArgumentOutOfRangeException("timeoutSeconds");
            StringBuilder commandLine = new StringBuilder(QuoteArgument(executable));
            foreach (string argument in arguments) commandLine.Append(' ').Append(QuoteArgument(argument));
            if (commandLine.Length >= 32767) throw new ArgumentException("The Windows command line is too long.");
            RunResult result = new RunResult();
            result.DesktopName = "HollylarkValidation_" + Guid.NewGuid().ToString("N");
            List<string> cleanupErrors = new List<string>();
            IntPtr desktop = IntPtr.Zero;
            IntPtr job = IntPtr.Zero;
            IntPtr log = IntPtr.Zero;
            IntPtr input = IntPtr.Zero;
            IntPtr attributes = IntPtr.Zero;
            IntPtr inheritedHandles = IntPtr.Zero;
            bool attributesInitialized = false;
            bool assignedToJob = false;
            ProcessInformation information = new ProcessInformation();
            try
            {
                desktop = CreateDesktopW(result.DesktopName, IntPtr.Zero, IntPtr.Zero, 0, 0x000000CF, IntPtr.Zero);
                if (desktop == IntPtr.Zero) throw NativeFailure("Create private validation desktop");
                job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw NativeFailure("Create validation job");
                ExtendedJobLimits limits = new ExtendedJobLimits();
                limits.Basic.Flags = KillOnJobClose;
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedJobLimits))))
                    throw NativeFailure("Set owned job cleanup policy");
                SecurityAttributes security = new SecurityAttributes();
                security.Length = Marshal.SizeOf(typeof(SecurityAttributes));
                security.InheritHandle = true;
                log = CreateFileW(logPath, 0x40000000, 1, ref security, 1, 0x00000080, IntPtr.Zero);
                if (log == InvalidHandle) throw NativeFailure("Create validation log without overwriting");
                input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
                if (input == InvalidHandle) throw NativeFailure("Open isolated standard input");
                IntPtr attributeSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
                if (attributeSize == IntPtr.Zero) throw NativeFailure("Determine process attribute size");
                attributes = Marshal.AllocHGlobal(attributeSize);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeSize))
                    throw NativeFailure("Initialize inherited handle list");
                attributesInitialized = true;
                inheritedHandles = Marshal.AllocHGlobal(IntPtr.Size * 2);
                Marshal.WriteIntPtr(inheritedHandles, 0, log);
                Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, input);
                if (!UpdateProcThreadAttribute(attributes, 0, new UIntPtr(0x00020002), inheritedHandles,
                    new IntPtr(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero))
                    throw NativeFailure("Restrict inheritance to validation input and log handles");
                StartupInfoEx startup = new StartupInfoEx();
                startup.Startup.Size = Marshal.SizeOf(typeof(StartupInfoEx));
                startup.Startup.Desktop = result.DesktopName;
                startup.Startup.Flags = 0x00000100;
                startup.Startup.StandardInput = input;
                startup.Startup.StandardOutput = log;
                startup.Startup.StandardError = log;
                startup.Attributes = attributes;
                if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    0x08080004, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out information))
                    throw NativeFailure("Create validator on its private desktop");
                result.ProcessId = information.ProcessId;
                if (!AssignProcessToJobObject(job, information.Process)) throw NativeFailure("Assign suspended validator to its owned job");
                assignedToJob = true;
                if (ResumeThread(information.Thread) == UInt32.MaxValue) throw NativeFailure("Resume validator");
                uint wait = WaitForSingleObject(information.Process, checked((uint)timeoutSeconds * 1000));
                if (wait == WaitTimeout)
                {
                    result.TimedOut = true;
                    if (!TerminateJobObject(job, 124)) throw NativeFailure("Stop timed-out validation job");
                    if (WaitForSingleObject(information.Process, 5000) != WaitObject)
                        throw new TimeoutException("The owned validator did not exit within the five-second cleanup allowance.");
                }
                else if (wait != WaitObject) throw NativeFailure("Wait for validator");
                uint processExitCode;
                if (!GetExitCodeProcess(information.Process, out processExitCode)) throw NativeFailure("Read validator exit code");
                result.ExitCode = result.TimedOut ? 124 : unchecked((int)processExitCode);
                return result;
            }
            finally
            {
                if (assignedToJob)
                {
                    if (!TerminateJobObject(job, 125)) cleanupErrors.Add(NativeFailure("Clean up owned validation helpers").Message);
                }
                else if (information.Process != IntPtr.Zero && WaitForSingleObject(information.Process, 0) == WaitTimeout)
                {
                    if (!TerminateProcess(information.Process, 125)) cleanupErrors.Add(NativeFailure("Stop owned suspended validator").Message);
                }
                if (information.Process != IntPtr.Zero && WaitForSingleObject(information.Process, 5000) != WaitObject)
                    cleanupErrors.Add("The owned validation process did not finish during handle cleanup.");
                CloseOwnedHandle(ref information.Thread, "validation thread", cleanupErrors);
                CloseOwnedHandle(ref information.Process, "validation process", cleanupErrors);
                CloseOwnedHandle(ref job, "validation job", cleanupErrors);
                if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
                CloseOwnedHandle(ref input, "validation input", cleanupErrors);
                CloseOwnedHandle(ref log, "validation log", cleanupErrors);
                if (desktop != IntPtr.Zero && !CloseDesktop(desktop)) cleanupErrors.Add(NativeFailure("Close private validation desktop").Message);
                result.CleanupErrors = cleanupErrors.ToArray();
                foreach (string error in cleanupErrors) Console.Error.WriteLine("Validation cleanup: " + error);
            }
        }
    }
}
'@

if (-not ('Hollylark.ValidationDesktop.Runner' -as [type])) {
    Add-Type -TypeDefinition $nativeSource -Language CSharp
}
$result = [Hollylark.ValidationDesktop.Runner]::Run($executablePath, $Arguments, $resolvedLogPath, $TimeoutSeconds)
[pscustomobject]@{
    Executable = $executablePath
    Arguments = $Arguments
    LogPath = $resolvedLogPath
    DesktopName = $result.DesktopName
    ProcessId = $result.ProcessId
    ExitCode = $result.ExitCode
    TimedOut = $result.TimedOut
    CleanupErrors = $result.CleanupErrors
}
if ($result.TimedOut) { Write-Warning 'Validation timed out; only the owned validation job was terminated.' }
if ($result.CleanupErrors.Count -gt 0 -and $result.ExitCode -eq 0) { exit 125 }
exit $result.ExitCode
