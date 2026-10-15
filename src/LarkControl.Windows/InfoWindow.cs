using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LarkControl.Windows;

internal sealed class InfoWindow : AuxiliaryWindow
{
    private readonly TextBox summary;
    private readonly TextBox log;
    private readonly Button readIdentity;
    private string pendingSummary = string.Empty;
    private string pendingLog = string.Empty;

    internal InfoWindow(Func<string> saveDiagnostics, Func<Task>? readIdentities = null) : base(Localization.Text("Info.Title"), "InfoWindow")
    {
        var layout = new StackPanel();
        var hint = new TextBlock
        {
            Style = (Style)FindResource("HintStyle"),
            Margin = new Thickness(0, 0, 0, 12),
            Text = Localization.Text("Info.LogHint")
        };
        layout.Children.Add(hint);
        summary = Readout("DeviceInformationReadout", Localization.Text("Info.SummaryName"));
        summary.MinHeight = 90;
        summary.MaxHeight = 240;
        layout.Children.Add(Label(Localization.Text("Info.SummaryLabel"), summary));
        layout.Children.Add(summary);
        readIdentity = Button("ReadIdentityButton", Localization.Text("Info.ReadIdentity"), Localization.Text("Info.ReadIdentityName"));
        readIdentity.HorizontalAlignment = HorizontalAlignment.Left;
        readIdentity.IsEnabled = false;
        readIdentity.Click += async (_, _) =>
        {
            if (readIdentities is null) return;
            readIdentity.IsEnabled = false;
            try { await readIdentities(); }
            catch (Exception exception) { ShowResult(Localization.Text("Info.ReadIdentityFailed", exception.Message)); }
        };
        layout.Children.Add(readIdentity);
        log = Readout("DiagnosticLogReadout", Localization.Text("Info.LogName"));
        log.TextWrapping = TextWrapping.NoWrap;
        log.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        log.MinHeight = 90;
        log.MaxHeight = 240;
        Label logLabel = Label(Localization.Text("Info.LogLabel"), log);
        layout.Children.Add(logLabel);
        layout.Children.Add(log);
        Button save = Button("SaveDiagnosticsButton", Localization.Text("Info.Save"), Localization.Text("Info.SaveName"));
        save.HorizontalAlignment = HorizontalAlignment.Left;
        save.Click += (_, _) => ShowResult(saveDiagnostics());
        layout.Children.Add(save);
        Body.Children.Add(Named(new ScrollViewer
        {
            Content = layout, Padding = new Thickness(12), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false
        }, "DiagnosticsScroll", Localization.Text("Info.ScrollName")));
        summary.LostKeyboardFocus += (_, _) => QueueUpdate();
        log.LostKeyboardFocus += (_, _) => QueueUpdate();
        InitialFocus = summary;
    }

    internal void UpdateContent(string summaryText, string logText, bool? canReadIdentity = null)
    {
        pendingSummary = summaryText;
        pendingLog = logText;
        if (canReadIdentity is bool allowed) readIdentity.IsEnabled = allowed;
        UiSupport.UpdateReadout(summary, pendingSummary);
        if (!log.IsKeyboardFocusWithin) UiSupport.UpdateReadout(log, pendingLog);
    }

    private void QueueUpdate() => Dispatcher.BeginInvoke(DispatcherPriority.Background,
        new Action(() => { if (IsVisible) UpdateContent(pendingSummary, pendingLog); }));
}
