using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LarkControl.Windows;

public interface IDesktopIntegration : IDisposable
{
    StartupRegistrationSnapshot CaptureStartup();
    void SetStartup(bool enabled);
    void RestoreStartup(StartupRegistrationSnapshot snapshot);
    void ShowTray(Window owner, Action show, Action settings, Action refresh, Action exit);
    void ConfigureTrayControls(Func<TrayControlSnapshot> readSnapshot, Func<int, Task> adjustGain,
        Func<int, Task> toggleMute) { }
    bool ShowNotification(string title, string message, bool critical) => false;
    void HideTray();
}

public sealed record TrayTransmitterSnapshot(int Index, bool IsConnected, bool IsMuted, bool CanToggleMute);

public sealed record TrayControlSnapshot(bool CanRefresh, int? ConfirmedGain, bool CanAdjustGain,
    IReadOnlyList<TrayTransmitterSnapshot> Transmitters);

internal enum TrayCommand : uint
{
    None, Show, Settings, Refresh, Exit, DecreaseGain, IncreaseGain,
    ToggleMute1 = 10, ToggleMute2, ToggleMute3, ToggleMute4
}

internal sealed record TrayMenuEntry(TrayCommand Command, string Label, bool Enabled = true,
    bool Checked = false, bool IsSeparator = false)
{
    internal uint NativeFlags => IsSeparator ? 0x800u : (Enabled ? 0u : 0x3u) | (Checked ? 0x8u : 0u);
}

public sealed record StartupRegistrationSnapshot
{
    private readonly object? rawValue;

    public static StartupRegistrationSnapshot Missing { get; } = new(false, null, RegistryValueKind.Unknown);
    public bool Exists { get; }
    public object? RawValue => CloneValue(rawValue);
    public RegistryValueKind ValueKind { get; }

    public StartupRegistrationSnapshot(bool exists, object? rawValue, RegistryValueKind valueKind)
    {
        if (!exists)
        {
            if (rawValue is not null || valueKind != RegistryValueKind.Unknown)
                throw new ArgumentException(Localization.Text("Desktop.MissingStartupValueInvalid"));
        }
        else
        {
            bool valid = valueKind switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString => rawValue is string,
                RegistryValueKind.Binary or RegistryValueKind.None => rawValue is byte[],
                RegistryValueKind.MultiString => rawValue is string[] values && values.All(value => value is not null),
                RegistryValueKind.DWord => rawValue is int,
                RegistryValueKind.QWord => rawValue is long,
                _ => throw new NotSupportedException(Localization.Text("Desktop.UnsupportedStartupValueKind", valueKind))
            };
            if (!valid)
                throw new ArgumentException(Localization.Text("Desktop.StartupValueKindMismatch", valueKind), nameof(rawValue));
        }
        Exists = exists;
        this.rawValue = CloneValue(rawValue);
        ValueKind = valueKind;
    }

    public bool Equals(StartupRegistrationSnapshot? other) => other is not null
        && Exists == other.Exists && ValueKind == other.ValueKind
        && (rawValue switch
        {
            byte[] bytes => other.rawValue is byte[] otherBytes && bytes.SequenceEqual(otherBytes),
            string[] strings => other.rawValue is string[] otherStrings && strings.SequenceEqual(otherStrings),
            _ => Equals(rawValue, other.rawValue)
        });

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Exists);
        hash.Add(ValueKind);
        if (rawValue is Array values)
            foreach (object value in values)
                hash.Add(value);
        else
            hash.Add(rawValue);
        return hash.ToHashCode();
    }

    private static object? CloneValue(object? value) => value is Array array ? array.Clone() : value;
}

internal interface IStartupRegistrationStore
{
    StartupRegistrationSnapshot Read();
    void Write(object rawValue, RegistryValueKind valueKind);
    void Delete();
}

public sealed class DesktopIntegration : IDesktopIntegration
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string StartupValueName = "Hollylark";
    private const int TrayCallbackMessage = 0x8001;
    private const uint TrayIconId = 1;
    private const int ContextMenuMessage = 0x7b;
    private const int DoubleClickMessage = 0x203;
    private const int IconSelectNotification = 0x400;
    private const int IconKeyboardSelectNotification = 0x401;
    private readonly string? executablePath;
    private readonly IStartupRegistrationStore startupStore;
    private readonly Func<string, FileAttributes> readFileAttributes;
    private HwndSource? traySource;
    private Window? trayOwner;
    private Action? showAction;
    private Action? settingsAction;
    private Action? refreshAction;
    private Action? exitAction;
    private Func<TrayControlSnapshot>? readTraySnapshot;
    private Func<int, Task>? adjustTrayGain;
    private Func<int, Task>? toggleTrayMute;
    private bool trayControlPending;
    private IntPtr icon;
    private uint taskbarCreatedMessage;
    private bool iconAdded;
    private bool menuOpen;
    private bool disposed;

    public event Action<Exception>? Failed;

    public DesktopIntegration() : this(Environment.ProcessPath, new RunStartupRegistrationStore()) { }

    internal DesktopIntegration(string? executablePath, IStartupRegistrationStore startupStore,
        Func<string, FileAttributes>? readFileAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(startupStore);
        this.executablePath = executablePath;
        this.startupStore = startupStore;
        this.readFileAttributes = readFileAttributes ?? File.GetAttributes;
    }

    public StartupRegistrationSnapshot CaptureStartup()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return startupStore.Read();
    }

    public void SetStartup(bool enabled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RestoreStartup(enabled
            ? new StartupRegistrationSnapshot(true, GetStartupCommand(), RegistryValueKind.String)
            : StartupRegistrationSnapshot.Missing);
    }

    public void RestoreStartup(StartupRegistrationSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (startupStore.Read() == snapshot)
            return;
        if (snapshot.Exists)
            startupStore.Write(snapshot.RawValue!, snapshot.ValueKind);
        else
            startupStore.Delete();
    }

    private string GetStartupCommand()
    {
        string? path = executablePath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.Any(character => char.IsControl(character) || character is '"' or '<' or '>' or '|' or '*' or '?' or '\u2028' or '\u2029'))
            throw new ArgumentException(Localization.Text("Desktop.StartupPathInvalid"));

        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)!;
        if (fullPath[root.Length..].Contains(':')
            || !string.Equals(Path.GetFileName(fullPath), "Hollylark.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(Localization.Text("Desktop.StartupApphostRequired"));
        string command = $"\"{fullPath}\"";
        if (command.Length > 260)
            throw new ArgumentException(Localization.Text("Desktop.StartupCommandTooLong"));
        if ((readFileAttributes(fullPath) & FileAttributes.Directory) != 0)
            throw new IOException(Localization.Text("Desktop.StartupPathIsDirectory", fullPath));
        return command;
    }

    public void ConfigureTrayControls(Func<TrayControlSnapshot> readSnapshot, Func<int, Task> adjustGain,
        Func<int, Task> toggleMute)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(adjustGain);
        ArgumentNullException.ThrowIfNull(toggleMute);
        traySource?.Dispatcher.VerifyAccess();
        readTraySnapshot = readSnapshot;
        adjustTrayGain = adjustGain;
        toggleTrayMute = toggleMute;
    }

    internal static IReadOnlyList<TrayMenuEntry> CreateTrayMenu(TrayControlSnapshot? snapshot, bool pending = false)
    {
        var entries = new List<TrayMenuEntry>
        {
            new(TrayCommand.Show, Localization.Text("Desktop.TrayShow")),
            new(TrayCommand.Settings, Localization.Text("Desktop.TraySettings")),
            new(TrayCommand.Refresh, Localization.Text("Desktop.TrayRefresh"), !pending && (snapshot?.CanRefresh ?? true))
        };
        if (snapshot is not null)
        {
            ArgumentNullException.ThrowIfNull(snapshot.Transmitters);
            if (snapshot.Transmitters.Any(transmitter => transmitter is null || transmitter.Index is < 0 or > 3)
                || snapshot.Transmitters.Select(transmitter => transmitter.Index).Distinct().Count() != snapshot.Transmitters.Count)
                throw new ArgumentException(Localization.Text("Desktop.TrayIndexesInvalid"), nameof(snapshot));
            bool validGain = snapshot.ConfirmedGain is >= 0 and <= 5;
            bool canAdjust = !pending && snapshot.CanAdjustGain && validGain;
            string gain = validGain ? Localization.Text("Desktop.GainLevel", snapshot.ConfirmedGain + 1) : Localization.Text("Desktop.NoGainReading");
            entries.Add(new(TrayCommand.None, string.Empty, IsSeparator: true));
            entries.Add(new(TrayCommand.DecreaseGain, Localization.Text("Desktop.DecreaseGain", gain), canAdjust && snapshot.ConfirmedGain > 0));
            entries.Add(new(TrayCommand.IncreaseGain, Localization.Text("Desktop.IncreaseGain", gain), canAdjust && snapshot.ConfirmedGain < 5));
            foreach (TrayTransmitterSnapshot transmitter in snapshot.Transmitters.OrderBy(transmitter => transmitter.Index))
            {
                string state = !transmitter.IsConnected ? Localization.Text("Desktop.TransmitterDisconnected")
                    : transmitter.IsMuted ? Localization.Text("Desktop.TransmitterMuted") : Localization.Text("Desktop.TransmitterUnmuted");
                entries.Add(new((TrayCommand)((int)TrayCommand.ToggleMute1 + transmitter.Index),
                    Localization.Text("Desktop.ToggleMute", transmitter.Index + 1, state),
                    !pending && transmitter.IsConnected && transmitter.CanToggleMute,
                    transmitter.IsConnected && transmitter.IsMuted));
            }
        }
        entries.Add(new(TrayCommand.None, string.Empty, IsSeparator: true));
        entries.Add(new(TrayCommand.Exit, Localization.Text("Desktop.TrayExit")));
        return entries.AsReadOnly();
    }

    internal IReadOnlyList<TrayMenuEntry> GetTrayMenuEntries() => CreateTrayMenu(readTraySnapshot?.Invoke(), trayControlPending);

    internal async Task<bool> ExecuteTrayControlAsync(TrayCommand command)
    {
        if (disposed || trayControlPending || readTraySnapshot is null)
            return false;
        TrayMenuEntry? entry = GetTrayMenuEntries().SingleOrDefault(item => item.Command == command && !item.IsSeparator);
        if (entry is not { Enabled: true })
            return false;
        Func<Task>? action = command switch
        {
            TrayCommand.DecreaseGain => () => adjustTrayGain!(-1),
            TrayCommand.IncreaseGain => () => adjustTrayGain!(1),
            TrayCommand.ToggleMute1 or TrayCommand.ToggleMute2 or TrayCommand.ToggleMute3 or TrayCommand.ToggleMute4 =>
                () => toggleTrayMute!((int)command - (int)TrayCommand.ToggleMute1),
            _ => null
        };
        if (action is null)
            return false;
        trayControlPending = true;
        try
        {
            await action();
            return true;
        }
        finally { trayControlPending = false; }
    }

    private async void InvokeTrayControl(TrayCommand command)
    {
        Window? owner = trayOwner;
        Action? restore = showAction;
        try { await ExecuteTrayControlAsync(command); }
        catch (Exception exception)
        {
            if (!disposed)
                ExecuteTrayOperation(() => throw exception, restore, Failed, failure => ShowTrayFailure(owner, failure));
        }
    }

    public void ShowTray(Window owner, Action show, Action settings, Action refresh, Action exit)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(exit);
        owner.Dispatcher.VerifyAccess();
        if (owner.Dispatcher.HasShutdownStarted || owner.Dispatcher.HasShutdownFinished)
            throw new InvalidOperationException(Localization.Text("Desktop.TrayDispatcherShutdown"));
        if (trayOwner is not null && trayOwner != owner)
            throw new InvalidOperationException(Localization.Text("Desktop.TrayOwnerChange"));
        showAction = show;
        settingsAction = settings;
        refreshAction = refresh;
        exitAction = exit;
        if (traySource is not null)
            return;

        try
        {
            taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            if (taskbarCreatedMessage == 0)
                throw NativeError("Desktop.RegisterTaskbarNotificationFailed");
            traySource = new HwndSource(new HwndSourceParameters("Hollylark tray callback")
            {
                WindowStyle = unchecked((int)0x80000000),
                ExtendedWindowStyle = 0x80,
                Width = 0,
                Height = 0,
                HwndSourceHook = WindowMessage
            });
            IntPtr sharedIcon = NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(32512));
            if (sharedIcon == IntPtr.Zero)
                throw NativeError("Desktop.LoadApplicationIconFailed");
            icon = NativeMethods.CopyIcon(sharedIcon);
            if (icon == IntPtr.Zero)
                throw NativeError("Desktop.CopyTrayIconFailed");
            AddTrayIcon();
            trayOwner = owner;
            owner.Closed += OwnerClosed;
        }
        catch (Exception exception)
        {
            try { CleanupTray(); }
            catch (Exception cleanupError) { throw new AggregateException(Localization.Text("Desktop.TrayInitializationCleanupFailed"), exception, cleanupError); }
            throw;
        }
    }

    public bool ShowNotification(string title, string message, bool critical)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        trayOwner?.Dispatcher.VerifyAccess();
        HwndSource? source = traySource;
        if (!iconAdded || source is null || trayOwner is null)
            return false;
        source.Dispatcher.VerifyAccess();
        if (source.IsDisposed || source.Dispatcher.HasShutdownStarted || source.Dispatcher.HasShutdownFinished)
            return false;
        if (CreateNotificationData(source.Handle, title, message, critical) is not { } data)
            return false;
        return NativeMethods.ShellNotifyIcon(IconOperation.Modify, ref data);
    }

    private static NativeMethods.NotifyIconData? CreateNotificationData(IntPtr window, string title, string message, bool critical)
    {
        if (window == IntPtr.Zero || !IsNotificationTextValid(title, 63) || !IsNotificationTextValid(message, 255))
            return null;
        return new NativeMethods.NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            Window = window,
            Id = TrayIconId,
            Flags = 0x10 | 0x40,
            Tip = string.Empty,
            Info = message,
            InfoTitle = title,
            InfoFlags = (critical ? 0x3u : 0x2u) | 0x10u
        };
    }

    private static bool IsNotificationTextValid(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
            return false;
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character == '\0')
                return false;
            if (!char.IsSurrogate(character))
                continue;
            if (!char.IsHighSurrogate(character) || index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                return false;
            index++;
        }
        return true;
    }

    public void HideTray()
    {
        if (disposed)
            return;
        CleanupTray();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        traySource?.Dispatcher.VerifyAccess();
        try { CleanupTray(); }
        finally
        {
            disposed = true;
            readTraySnapshot = null;
            adjustTrayGain = toggleTrayMute = null;
        }
    }

    private void OwnerClosed(object? sender, EventArgs arguments) => ExecuteTrayOperation(
        Dispose, null, Failed, exception => ShowTrayFailure(sender as Window, exception));

    private void AddTrayIcon()
    {
        NativeMethods.NotifyIconData data = CreateIconData();
        if (!NativeMethods.ShellNotifyIcon(IconOperation.Add, ref data))
            throw new InvalidOperationException(Localization.Text("Desktop.TrayAddFailed"));
        iconAdded = true;
        data.Version = 4;
        if (!NativeMethods.ShellNotifyIcon(IconOperation.SetVersion, ref data))
            throw new InvalidOperationException(Localization.Text("Desktop.TrayKeyboardNotificationsFailed"));
    }

    private NativeMethods.NotifyIconData CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        Window = traySource!.Handle,
        Id = TrayIconId,
        Flags = 0x1 | 0x2 | 0x4 | 0x80,
        CallbackMessage = TrayCallbackMessage,
        Icon = icon,
        Tip = Localization.Text("Desktop.TrayTip"),
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wordParameter, IntPtr longParameter, ref bool handled)
    {
        if (message == taskbarCreatedMessage && taskbarCreatedMessage != 0)
        {
            iconAdded = false;
            QueueTrayOperation(RecoverTrayIcon, restoreOnFailure: false);
        }
        else if (message == TrayCallbackMessage && ((longParameter.ToInt64() >> 16) & 0xffff) == TrayIconId)
        {
            handled = true;
            int notification = (int)(longParameter.ToInt64() & 0xffff);
            if (IsRestoreNotification(notification))
                QueueTrayOperation(() => showAction!());
            else if (notification == ContextMenuMessage)
            {
                int positionX = unchecked((short)(wordParameter.ToInt64() & 0xffff));
                int positionY = unchecked((short)((wordParameter.ToInt64() >> 16) & 0xffff));
                QueueTrayOperation(() => ShowContextMenu(positionX, positionY));
            }
        }
        return IntPtr.Zero;
    }

    internal static bool IsRestoreNotification(int notification) => notification
        is IconSelectNotification or IconKeyboardSelectNotification or DoubleClickMessage;

    private void QueueTrayOperation(Action operation, bool restoreOnFailure = true)
    {
        HwndSource? source = traySource;
        if (source is null || disposed)
            return;
        Window? owner = trayOwner;
        Action? restore = restoreOnFailure ? showAction : null;
        Action<Exception> fallback = exception => ShowTrayFailure(owner, exception);
        ExecuteTrayOperation(() => source.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!disposed && traySource == source)
                ExecuteTrayOperation(operation, restore, Failed, fallback);
        })), restore, Failed, fallback);
    }

    internal static void ExecuteTrayOperation(Action operation, Action? restore,
        Action<Exception>? failed, Action<Exception> fallback)
    {
        try { operation(); }
        catch (Exception exception)
        {
            Exception failure = exception;
            if (restore is not null)
            {
                try { restore(); }
                catch (Exception restoreError)
                {
                    failure = new AggregateException(Localization.Text("Desktop.TrayOperationRestoreFailed"), failure, restoreError);
                }
            }
            if (failed is not null)
            {
                try { failed(failure); return; }
                catch (Exception notificationError)
                {
                    failure = new AggregateException(Localization.Text("Desktop.TrayErrorReportingFailed"), failure, notificationError);
                }
            }
            fallback(failure);
        }
    }

    private static void ShowTrayFailure(Window? owner, Exception exception)
    {
        string title = Localization.Text("Desktop.TrayFailureTitle");
        string message = Localization.Text("Desktop.TrayFailureMessage");
        if (owner is { IsVisible: true })
        {
            try
            {
                MessageBox.Show(owner, message + exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            catch (Exception dialogError)
            {
                exception = new AggregateException(Localization.Text("Desktop.TrayOwnerDialogFailed"), exception, dialogError);
            }
        }
        MessageBox.Show(message + exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void RecoverTrayIcon()
    {
        if (iconAdded)
            return;
        try { AddTrayIcon(); }
        catch (Exception exception)
        {
            var errors = new List<Exception> { exception };
            AttemptCleanup(CleanupTray, errors);
            throw new AggregateException(Localization.Text("Desktop.TrayRecoveryFailed"), errors);
        }
    }

    private void ShowContextMenu(int positionX, int positionY)
    {
        if (menuOpen)
            return;
        HwndSource source = traySource!;
        if (positionX == -1 && positionY == -1)
        {
            var identifier = new NativeMethods.NotifyIconIdentifier
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconIdentifier>(), Window = source.Handle, Id = TrayIconId
            };
            int result = NativeMethods.ShellNotifyIconGetRect(ref identifier, out NativeMethods.NativeRect rectangle);
            if (result < 0)
                throw new ExternalException(Localization.Text("Desktop.TrayKeyboardMenuLocationFailed"), result);
            positionX = rectangle.Left;
            positionY = rectangle.Bottom;
        }
        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
            throw NativeError("Desktop.CreateTrayMenuFailed");
        uint selected = 0;
        var errors = new List<Exception>();
        try
        {
            foreach (TrayMenuEntry entry in GetTrayMenuEntries())
                if (!NativeMethods.AppendMenu(menu, entry.NativeFlags, (UIntPtr)(uint)entry.Command, entry.Label))
                    throw NativeError("Desktop.AppendTrayMenuItemFailed");
            if (!NativeMethods.SetMenuDefaultItem(menu, 1, false))
                throw NativeError("Desktop.SetDefaultTrayMenuItemFailed");
            if (!NativeMethods.SetForegroundWindow(source.Handle))
                throw new InvalidOperationException(Localization.Text("Desktop.TrayMenuFocusFailed"));
            menuOpen = true;
            selected = NativeMethods.TrackPopupMenuEx(menu, 0x100 | 0x80 | 0x2, positionX, positionY, source.Handle, IntPtr.Zero);
            int error = Marshal.GetLastPInvokeError();
            if (selected == 0 && error != 0)
                throw new Win32Exception(error, Localization.Text("Desktop.TrayMenuDisplayFailed"));
        }
        catch (Exception exception) { errors.Add(exception); }
        finally
        {
            menuOpen = false;
            AttemptCleanup(() =>
            {
                if (!NativeMethods.DestroyMenu(menu))
                    throw NativeError("Desktop.DestroyTrayMenuFailed");
            }, errors);
            if (traySource == source)
            {
                AttemptCleanup(() =>
                {
                    if (!NativeMethods.PostMessage(source.Handle, 0, IntPtr.Zero, IntPtr.Zero))
                        throw NativeError("Desktop.DismissTrayMenuFailed");
                    NativeMethods.NotifyIconData data = CreateIconData();
                    if (iconAdded && !NativeMethods.ShellNotifyIcon(IconOperation.SetFocus, ref data))
                        throw new InvalidOperationException(Localization.Text("Desktop.TrayFocusRestoreFailed"));
                }, errors);
            }
        }
        if (errors.Count > 0)
            throw new AggregateException(Localization.Text("Desktop.TrayMenuFailed"), errors);
        if (traySource != source || disposed)
            return;
        if ((TrayCommand)selected is TrayCommand.DecreaseGain or TrayCommand.IncreaseGain
            or TrayCommand.ToggleMute1 or TrayCommand.ToggleMute2 or TrayCommand.ToggleMute3 or TrayCommand.ToggleMute4)
        {
            InvokeTrayControl((TrayCommand)selected);
            return;
        }
        if (selected == (uint)TrayCommand.Refresh
            && !GetTrayMenuEntries().Single(entry => entry.Command == TrayCommand.Refresh).Enabled)
            return;
        Action? action = selected switch
        {
            0 => null, 1 => showAction, 2 => settingsAction, 3 => refreshAction, 4 => exitAction,
            _ => throw new InvalidOperationException(Localization.Text("Desktop.UnexpectedTrayCommand", selected))
        };
        action?.Invoke();
    }

    private void CleanupTray()
    {
        traySource?.Dispatcher.VerifyAccess();
        var errors = new List<Exception>();
        if (trayOwner is not null)
            trayOwner.Closed -= OwnerClosed;
        if (menuOpen)
            AttemptCleanup(() =>
            {
                if (!NativeMethods.EndMenu())
                    throw NativeError("Desktop.CloseActiveTrayMenuFailed");
            }, errors);
        if (iconAdded && traySource is not null)
            AttemptCleanup(() =>
            {
                NativeMethods.NotifyIconData data = CreateIconData();
                if (!NativeMethods.ShellNotifyIcon(IconOperation.Delete, ref data))
                    throw new InvalidOperationException(Localization.Text("Desktop.TrayRemoveFailed"));
            }, errors);
        iconAdded = false;
        if (traySource is not null)
            AttemptCleanup(traySource.Dispose, errors);
        traySource = null;
        if (icon != IntPtr.Zero)
            AttemptCleanup(() =>
            {
                if (!NativeMethods.DestroyIcon(icon))
                    throw NativeError("Desktop.ReleaseTrayIconFailed");
            }, errors);
        icon = IntPtr.Zero;
        trayOwner = null;
        showAction = settingsAction = refreshAction = exitAction = null;
        taskbarCreatedMessage = 0;
        if (errors.Count > 0)
            throw new AggregateException(Localization.Text("Desktop.CleanupFailed"), errors);
    }

    private static void AttemptCleanup(Action cleanup, List<Exception> errors)
    {
        try { cleanup(); }
        catch (Exception exception) { errors.Add(exception); }
    }

    private static Win32Exception NativeError(string key) => new(Marshal.GetLastPInvokeError(), Localization.Text(key));

    private enum IconOperation : uint
    {
        Add = 0,
        Modify = 1,
        Delete = 2,
        SetFocus = 3,
        SetVersion = 4
    }

    private sealed class RunStartupRegistrationStore : IStartupRegistrationStore
    {
        public StartupRegistrationSnapshot Read()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key is null || !key.GetValueNames().Contains(StartupValueName, StringComparer.OrdinalIgnoreCase))
                return StartupRegistrationSnapshot.Missing;
            RegistryValueKind kind = key.GetValueKind(StartupValueName);
            object? value = key.GetValue(StartupValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return new StartupRegistrationSnapshot(true, value, kind);
        }

        public void Write(object rawValue, RegistryValueKind valueKind)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new IOException(Localization.Text("Desktop.StartupRegistryOpenFailed", RunKeyPath, StartupValueName));
            key.SetValue(StartupValueName, rawValue, valueKind);
        }

        public void Delete()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NotifyIconData
        {
            internal uint Size;
            internal IntPtr Window;
            internal uint Id;
            internal uint Flags;
            internal uint CallbackMessage;
            internal IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
            internal uint State;
            internal uint StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
            internal uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
            internal uint InfoFlags;
            internal Guid Guid;
            internal IntPtr BalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NotifyIconIdentifier
        {
            internal uint Size;
            internal IntPtr Window;
            internal uint Id;
            internal Guid Guid;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShellNotifyIcon(IconOperation operation, ref NotifyIconData data);

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
        internal static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect rectangle);

        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
        internal static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CopyIcon(IntPtr source);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr identifier, string text);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetMenuDefaultItem(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int positionX, int positionY, IntPtr owner, IntPtr parameters);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wordParameter, IntPtr longParameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EndMenu();
    }
}
