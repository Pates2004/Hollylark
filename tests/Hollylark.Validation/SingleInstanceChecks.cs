using System.Diagnostics;
using System.IO;
using LarkControl.Windows;

internal static partial class Program
{
    private static string SingleInstanceTestName(string token) => @"Local\Hollylark.Validation.SingleInstance." + token;

    private static int RunSingleInstanceHelper(string[] arguments)
    {
        if (arguments.Length != 3 || !Guid.TryParseExact(arguments[1], "N", out _)
            || arguments[2] is not ("owner" or "normal" or "cli"))
            throw new ArgumentException("Expected --single-instance-helper <unique GUID> <owner|normal|cli>.");
        using var instance = new SingleInstance(SingleInstanceTestName(arguments[1]));
        if (!instance.IsOwner)
        {
            Console.WriteLine("duplicate");
            if (arguments[2] != "normal") return 1;
            instance.RequestActivation();
            return 0;
        }
        Console.WriteLine("owner");
        if (arguments[2] != "owner") return 0;
        while (Console.ReadLine() is string command)
        {
            switch (command)
            {
                case "listen":
                    Console.WriteLine("listening");
                    instance.Listen(() => Console.WriteLine("activated"));
                    break;
                case "release":
                    return 0;
                case "fail":
                    throw new InvalidOperationException("Simulated startup failure while holding the instance mutex.");
                default:
                    throw new ArgumentException("Unknown single-instance helper command: " + command);
            }
        }
        return 0;
    }

    private static void VerifySingleInstanceChecks()
    {
        string token = Guid.NewGuid().ToString("N");
        using (var owner = new SingleInstanceChild(token, "owner"))
        {
            owner.Expect("owner", "The first owned process acquires the isolated instance mutex");
            using Mutex retainedHandle = Mutex.OpenExisting(SingleInstanceTestName(token) + ".Mutex");
            using (var duplicate = new SingleInstanceChild(token, "normal"))
            {
                duplicate.Expect("duplicate", "A second live process cannot become the owner before listener registration");
                duplicate.ExpectExit(0, "A normal duplicate signals and exits successfully during bootstrap");
            }
            owner.Send("listen");
            owner.Expect("listening", "The owner registers its listener after the duplicate has exited");
            owner.Expect("activated", "The named AutoReset event retains activation before listener registration");
            using (var duplicate = new SingleInstanceChild(token, "cli"))
            {
                duplicate.Expect("duplicate", "A diagnostic or invalid duplicate cannot acquire the live owner's mutex");
                duplicate.ExpectExit(1, "A CLI duplicate exits unsuccessfully without executing startup work");
            }
            owner.ExpectQuiet("A CLI duplicate does not request GUI activation");
            using (var duplicate = new SingleInstanceChild(token, "normal"))
            {
                duplicate.Expect("duplicate", "Contention remains enforced while the activation listener is live");
                duplicate.ExpectExit(0, "A normal duplicate exits successfully with a live listener");
            }
            owner.Expect("activated", "The live listener receives a later activation after the AutoReset event was consumed");
            owner.Send("release");
            owner.ExpectExit(0, "Normal owner shutdown releases the mutex and listener cleanly");
            VerifySingleInstanceReleased(retainedHandle, "Normal shutdown releases ownership without abandoning the mutex");
            VerifySingleInstanceRestart(token, "A new process acquires the same retained mutex after normal release");
        }
        using (var failingOwner = new SingleInstanceChild(token, "owner"))
        {
            failingOwner.Expect("owner", "The startup-failure fixture owns the isolated mutex");
            using Mutex retainedHandle = Mutex.OpenExisting(SingleInstanceTestName(token) + ".Mutex");
            failingOwner.Send("fail");
            failingOwner.ExpectExit(1, "The simulated startup exception unwinds the owning scope");
            VerifySingleInstanceReleased(retainedHandle, "Startup failure releases ownership without abandoning the mutex");
            VerifySingleInstanceRestart(token, "A new process acquires the same retained mutex after startup failure");
        }
        using (var abandonedOwner = new SingleInstanceChild(token, "owner"))
        {
            abandonedOwner.Expect("owner", "The abandonment fixture owns the isolated mutex");
            using Mutex retainedHandle = Mutex.OpenExisting(SingleInstanceTestName(token) + ".Mutex");
            abandonedOwner.Terminate();
            VerifySingleInstanceRestart(token, "A new process handles an abandoned mutex retained by a separate handle");
        }
    }

    private static void VerifySingleInstanceReleased(Mutex retainedHandle, string description)
    {
        bool acquired = false;
        try
        {
            acquired = retainedHandle.WaitOne(0);
            Check(acquired, description);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
            throw new InvalidOperationException("FAIL: " + description + "; the mutex was abandoned.");
        }
        finally
        {
            if (acquired) retainedHandle.ReleaseMutex();
        }
    }

    private static void VerifySingleInstanceRestart(string token, string description)
    {
        using var restarted = new SingleInstanceChild(token, "owner");
        restarted.Expect("owner", description);
        restarted.Send("release");
        restarted.ExpectExit(0, "The restarted owner releases the mutex normally");
    }

    private sealed class SingleInstanceChild : IDisposable
    {
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);
        private readonly Process process;
        private readonly Task<string> errors;
        private Task<string?>? nextLine;

        internal SingleInstanceChild(string token, string mode)
        {
            string executable = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Cannot locate the native validator executable.", executable);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add("--single-instance-helper");
            start.ArgumentList.Add(token);
            start.ArgumentList.Add(mode);
            process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the owned single-instance helper.");
            errors = process.StandardError.ReadToEndAsync();
        }

        internal void Send(string command)
        {
            process.StandardInput.WriteLine(command);
            process.StandardInput.Flush();
        }

        internal void Expect(string expected, string description)
        {
            nextLine ??= process.StandardOutput.ReadLineAsync();
            string? actual = nextLine.WaitAsync(ResponseTimeout).GetAwaiter().GetResult();
            nextLine = null;
            Check(actual == expected, description + $" (expected {expected}, received {actual ?? "EOF"})");
        }

        internal void ExpectQuiet(string description)
        {
            nextLine ??= process.StandardOutput.ReadLineAsync();
            Check(!nextLine.Wait(TimeSpan.FromMilliseconds(250)) && !process.HasExited, description);
        }

        internal void ExpectExit(int expected, string description)
        {
            if (!process.WaitForExit((int)ResponseTimeout.TotalMilliseconds))
                throw new TimeoutException("The owned single-instance helper did not exit: " + description);
            string error = errors.WaitAsync(ResponseTimeout).GetAwaiter().GetResult();
            bool matched = process.ExitCode == expected && (expected != 0 || error.Length == 0);
            Check(matched, matched ? description : description + $"; exit={process.ExitCode}; stderr={error}");
        }

        internal void Terminate()
        {
            process.Kill();
            if (!process.WaitForExit((int)ResponseTimeout.TotalMilliseconds))
                throw new TimeoutException("The abandoned owner helper did not terminate.");
            Check(process.HasExited, "Only the validator-owned mutex owner is terminated to exercise abandonment");
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    if (!process.WaitForExit((int)ResponseTimeout.TotalMilliseconds))
                        throw new TimeoutException("The owned single-instance helper could not be cleaned up.");
                }
            }
            finally { process.Dispose(); }
        }
    }
}
