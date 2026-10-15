using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LarkControl.Windows;

public sealed class GuardedSettingComboBox : ComboBox
{
    private static readonly CoerceValueCallback? NativeIndexCoercion = SelectedIndexProperty.GetMetadata(typeof(ComboBox)).CoerceValueCallback;
    private static readonly CoerceValueCallback? NativeItemCoercion = SelectedItemProperty.GetMetadata(typeof(ComboBox)).CoerceValueCallback;
    private static readonly CoerceValueCallback? NativeValueCoercion = SelectedValueProperty.GetMetadata(typeof(ComboBox)).CoerceValueCallback;
    private readonly SettingEditGate editGate = new();
    private readonly HashSet<GuardedSettingComboBoxItem> ownedItems = [];

    static GuardedSettingComboBox()
    {
        IsSynchronizedWithCurrentItemProperty.OverrideMetadata(typeof(GuardedSettingComboBox), new FrameworkPropertyMetadata(false));
        SelectedIndexProperty.OverrideMetadata(typeof(GuardedSettingComboBox), new FrameworkPropertyMetadata(-1, null, CoerceIndex));
        SelectedItemProperty.OverrideMetadata(typeof(GuardedSettingComboBox), new FrameworkPropertyMetadata(null, null, CoerceItem));
        SelectedValueProperty.OverrideMetadata(typeof(GuardedSettingComboBox), new FrameworkPropertyMetadata(null, null, CoerceValue));
    }

    internal bool RequestEdit() => editGate.RequestEdit(this);

    private object? AuthoritativeItem => DataContext is SettingRowViewModel setting
        && setting.SelectedIndex >= 0 && setting.SelectedIndex < Items.Count ? Items[setting.SelectedIndex] : null;

    private static object CoerceIndex(DependencyObject dependency, object value)
    {
        var owner = (GuardedSettingComboBox)dependency;
        return owner.editGate.Coerce(owner, SelectedIndexProperty, value,
            owner.DataContext is SettingRowViewModel setting ? setting.SelectedIndex : value, NativeIndexCoercion);
    }

    private static object CoerceItem(DependencyObject dependency, object value)
    {
        var owner = (GuardedSettingComboBox)dependency;
        return owner.editGate.Coerce(owner, SelectedItemProperty, value, owner.AuthoritativeItem, NativeItemCoercion);
    }

    private static object CoerceValue(DependencyObject dependency, object value)
    {
        var owner = (GuardedSettingComboBox)dependency;
        object? expected = owner.SelectedValuePath.Length == 0 ? owner.AuthoritativeItem : owner.SelectedValue;
        return owner.editGate.Coerce(owner, SelectedValueProperty, value, expected, NativeValueCoercion);
    }

    protected override bool IsItemItsOwnContainerOverride(object item) => item is GuardedSettingComboBoxItem;
    protected override DependencyObject GetContainerForItemOverride() => new GuardedSettingComboBoxItem();

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs eventArgs)
    {
        HashSet<GuardedSettingComboBoxItem> currentItems = Items.OfType<GuardedSettingComboBoxItem>().ToHashSet();
        foreach (GuardedSettingComboBoxItem previous in ownedItems)
            if (!currentItems.Contains(previous) && ReferenceEquals(previous.SettingOwner, this)) previous.SettingOwner = null;
        ownedItems.Clear();
        foreach (GuardedSettingComboBoxItem item in currentItems)
        {
            item.SettingOwner = this;
            ownedItems.Add(item);
        }
        base.OnItemsChanged(eventArgs);
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is GuardedSettingComboBoxItem container) container.SettingOwner = this;
        base.PrepareContainerForItemOverride(element, item);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        base.ClearContainerForItemOverride(element, item);
        if (element is GuardedSettingComboBoxItem container && !ownedItems.Contains(container)) container.SettingOwner = null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GuardedComboBoxAutomationPeer(this);

    protected override void OnPreviewKeyDown(KeyEventArgs eventArgs)
    {
        if (BlockKey(eventArgs)) return;
        base.OnPreviewKeyDown(eventArgs);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (BlockKey(eventArgs)) return;
        base.OnKeyDown(eventArgs);
    }

    private bool BlockKey(KeyEventArgs eventArgs)
    {
        Key key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        bool changesChoice = key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.Space or Key.Enter
            || key == Key.F4 && !eventArgs.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Alt);
        if (!changesChoice || RequestEdit()) return false;
        eventArgs.Handled = true;
        return true;
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs eventArgs)
    {
        if (!RequestEdit()) eventArgs.Handled = true;
        else base.OnPreviewTextInput(eventArgs);
    }

    protected override void OnTextInput(TextCompositionEventArgs eventArgs)
    {
        if (!RequestEdit()) eventArgs.Handled = true;
        else base.OnTextInput(eventArgs);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left && !RequestEdit())
        {
            Focus();
            eventArgs.Handled = true;
        }
        else base.OnPreviewMouseDown(eventArgs);
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left && !RequestEdit()) eventArgs.Handled = true;
        else base.OnPreviewMouseUp(eventArgs);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs eventArgs)
    {
        if (!RequestEdit()) eventArgs.Handled = true;
        else base.OnPreviewMouseWheel(eventArgs);
    }

    private sealed class GuardedComboBoxAutomationPeer : ComboBoxAutomationPeer
    {
        internal GuardedComboBoxAutomationPeer(GuardedSettingComboBox owner) : base(owner) { }

        protected override ItemAutomationPeer CreateItemAutomationPeer(object item) =>
            new GuardedChoiceAutomationPeer(item, this, (GuardedSettingComboBox)Owner);
    }

    private sealed class GuardedChoiceAutomationPeer(object item, SelectorAutomationPeer parent, GuardedSettingComboBox owner)
        : ListBoxItemAutomationPeer(item, parent), ISelectionItemProvider
    {
        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.SelectionItem ? this : base.GetPattern(patternInterface);

        bool ISelectionItemProvider.IsSelected => Equals(owner.SelectedItem, Item);
        IRawElementProviderSimple ISelectionItemProvider.SelectionContainer => ProviderFromPeer(ItemsControlAutomationPeer);

        void ISelectionItemProvider.Select()
        {
            if (!CanSelect()) return;
            owner.SetCurrentValue(SelectedIndexProperty, owner.Items.IndexOf(Item));
        }

        void ISelectionItemProvider.AddToSelection()
        {
            if (!CanSelect()) return;
            if (owner.SelectedItem is not null && !Equals(owner.SelectedItem, Item))
                throw new InvalidOperationException("The control supports a single selected item.");
            owner.SetCurrentValue(SelectedIndexProperty, owner.Items.IndexOf(Item));
        }

        void ISelectionItemProvider.RemoveFromSelection()
        {
            if (CanSelect() && Equals(owner.SelectedItem, Item)) owner.SetCurrentValue(SelectedIndexProperty, -1);
        }

        private bool CanSelect()
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            return owner.RequestEdit();
        }
    }
}

internal sealed class GuardedSettingComboBoxItem : ComboBoxItem
{
    private readonly SettingEditGate editGate = new();
    internal GuardedSettingComboBox? SettingOwner { get; set; }

    static GuardedSettingComboBoxItem()
    {
        IsSelectedProperty.OverrideMetadata(typeof(GuardedSettingComboBoxItem), new FrameworkPropertyMetadata(false, null, CoerceSelection));
    }

    private static object CoerceSelection(DependencyObject dependency, object value)
    {
        var item = (GuardedSettingComboBoxItem)dependency;
        GuardedSettingComboBox? owner = item.SettingOwner ?? ItemsControl.ItemsControlFromItemContainer(item) as GuardedSettingComboBox;
        if (owner?.DataContext is not SettingRowViewModel setting) return value;
        int index = owner.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0) index = owner.Items.IndexOf(item);
        bool expected = index >= 0 && index == setting.SelectedIndex;
        if (Equals(value, expected) || owner.RequestEdit()) return value;
        return item.editGate.Reject(item, IsSelectedProperty);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left && SettingOwner?.RequestEdit() == false) eventArgs.Handled = true;
        else base.OnPreviewMouseDown(eventArgs);
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left && SettingOwner?.RequestEdit() == false) eventArgs.Handled = true;
        else base.OnPreviewMouseUp(eventArgs);
    }
}

public sealed class GuardedSettingSlider : Slider
{
    private static readonly CoerceValueCallback? NativeValueCoercion = ValueProperty.GetMetadata(typeof(Slider)).CoerceValueCallback;
    private readonly SettingEditGate editGate = new();

    static GuardedSettingSlider()
    {
        ValueProperty.OverrideMetadata(typeof(GuardedSettingSlider), new FrameworkPropertyMetadata(0.0, null, CoerceValue));
    }

    private static object CoerceValue(DependencyObject dependency, object value)
    {
        var owner = (GuardedSettingSlider)dependency;
        object normalized = NativeValueCoercion is null ? value : NativeValueCoercion(owner, value);
        if (!owner.IsInitialized && owner.ReadLocalValue(ValueProperty) == DependencyProperty.UnsetValue) return normalized;
        return owner.editGate.Coerce(owner, ValueProperty, normalized,
            owner.DataContext is SettingRowViewModel setting ? setting.SelectedLevel : normalized, null);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GuardedSliderAutomationPeer(this);

    protected override void OnPreviewKeyDown(KeyEventArgs eventArgs)
    {
        if (BlockKey(eventArgs)) return;
        base.OnPreviewKeyDown(eventArgs);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (BlockKey(eventArgs)) return;
        base.OnKeyDown(eventArgs);
    }

    private bool BlockKey(KeyEventArgs eventArgs)
    {
        if (eventArgs.Key is not (Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            || editGate.RequestEdit(this)) return false;
        eventArgs.Handled = true;
        return true;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left && !editGate.RequestEdit(this))
        {
            Focus();
            eventArgs.Handled = true;
        }
        else base.OnPreviewMouseDown(eventArgs);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs eventArgs)
    {
        if (!editGate.RequestEdit(this)) eventArgs.Handled = true;
        else base.OnPreviewMouseWheel(eventArgs);
    }

    private sealed class GuardedSliderAutomationPeer : SliderAutomationPeer, IRangeValueProvider
    {
        internal GuardedSliderAutomationPeer(GuardedSettingSlider owner) : base(owner) { }
        private GuardedSettingSlider SettingOwner => (GuardedSettingSlider)Owner;

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

        bool IRangeValueProvider.IsReadOnly => !SettingOwner.IsEnabled;
        double IRangeValueProvider.Value => SettingOwner.Value;
        double IRangeValueProvider.Minimum => SettingOwner.Minimum;
        double IRangeValueProvider.Maximum => SettingOwner.Maximum;
        double IRangeValueProvider.SmallChange => SettingOwner.SmallChange;
        double IRangeValueProvider.LargeChange => SettingOwner.LargeChange;

        void IRangeValueProvider.SetValue(double value)
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            GuardedSettingSlider owner = SettingOwner;
            if (!owner.editGate.RequestEdit(owner)) return;
            ArgumentOutOfRangeException.ThrowIfLessThan(value, owner.Minimum);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, owner.Maximum);
            owner.SetCurrentValue(ValueProperty, value);
        }
    }
}

internal sealed class SettingEditGate
{
    private readonly Dictionary<DependencyProperty, object> rejectedValues = [];
    private SettingRowViewModel? lastRefusedSetting;
    private string? lastRefusedReason;
    private long lastRefusalTime;

    internal bool RequestEdit(FrameworkElement owner)
    {
        if (owner.DataContext is not SettingRowViewModel setting) return true;
        long now = Environment.TickCount64;
        if (!setting.IsEnabled && ReferenceEquals(setting, lastRefusedSetting)
            && setting.UnavailableReason == lastRefusedReason && now - lastRefusalTime < 400) return false;
        if (setting.RequestEdit())
        {
            lastRefusedSetting = null;
            return true;
        }
        lastRefusedSetting = setting;
        lastRefusedReason = setting.UnavailableReason;
        lastRefusalTime = now;
        return false;
    }

    internal object Coerce(FrameworkElement owner, DependencyProperty property, object proposed, object? authoritative,
        CoerceValueCallback? nativeCoercion)
    {
        if (owner.DataContext is not SettingRowViewModel || Equals(proposed, authoritative) || RequestEdit(owner))
        {
            rejectedValues.Remove(property);
            return nativeCoercion is null ? proposed : nativeCoercion(owner, proposed);
        }
        return Reject(owner, property);
    }

    internal object Reject(FrameworkElement owner, DependencyProperty property)
    {
        object marker = new();
        rejectedValues[property] = marker;
        owner.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            if (!rejectedValues.TryGetValue(property, out object? current) || !ReferenceEquals(marker, current)) return;
            rejectedValues.Remove(property);
            owner.SetCurrentValue(property, owner.GetValue(property));
        }));
        return owner.GetValue(property);
    }
}
