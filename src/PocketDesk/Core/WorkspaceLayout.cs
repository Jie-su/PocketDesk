using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PocketDesk.Core;

/// <summary>Pure responsive layout, shared by the application and visual previews.</summary>
public static class WorkspaceLayout
{
    public static void Apply(FrameworkElement view, double logicalWidth)
    {
        if (!double.IsFinite(logicalWidth) || logicalWidth <= 0) return;
        T Find<T>(string name) where T : class => (T)view.FindName(name);
        bool wide = logicalWidth >= 680;
        bool shortWindow = Find<Grid>("PhoneSurface").ActualHeight is > 0 and < 500;
        Find<Grid>("PhoneSurface").Tag = wide && shortWindow;
        Find<ColumnDefinition>("RailColumn").Width = new GridLength(wide ? 76 : 0);
        Find<Border>("SideRail").Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        Find<Border>("BottomNavigation").Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        Find<Grid>("MainPane").Margin = new Thickness(wide ? 24 : 16, shortWindow ? 8 : 20, wide ? 24 : 16, shortWindow ? 8 : 12);
        Find<Grid>("HeaderBar").Margin = new Thickness(0, 0, 0, shortWindow ? 10 : 18);
        var connection = Find<Border>("ConnectionBanner");
        connection.Padding = new Thickness(14, shortWindow ? 0 : 8, 14, shortWindow ? 0 : 8);
        connection.Margin = new Thickness(0, 0, 0, shortWindow ? 10 : 16);
        Find<Border>("SearchCard").Margin = new Thickness(0, 0, 0, shortWindow ? 10 : 18);
        Find<TextBlock>("HeaderSubtitle").Visibility = wide && !shortWindow ? Visibility.Visible : Visibility.Collapsed;

        var list = Find<ItemsControl>("WindowList");
        double available = list.ActualWidth > 0 ? list.ActualWidth : logicalWidth - (wide ? 124 : 32) + 12;
        int columns = wide ? Math.Clamp((int)(available / 272), 1, 3) : 1;
        var panel = (ItemsPanelTemplate)view.FindResource(columns > 1 ? "LandscapeAppsPanel" : "PortraitAppsPanel");
        if (!ReferenceEquals(list.ItemsPanel, panel)) list.ItemsPanel = panel;
        // WPF rounds each item to a device pixel. Round down in that same
        // coordinate system so 150% DPI cannot wrap the last column away.
        double dpi = VisualTreeHelper.GetDpi(list).DpiScaleX;
        double cardWidth = columns > 1 ? Math.Floor(available * dpi / columns - 0.01) / dpi : double.NaN;
        var previous = list.ItemContainerStyle?.Setters.OfType<Setter>()
            .FirstOrDefault(s => s.Property == FrameworkElement.WidthProperty)?.Value as double?;
        if (previous is null || !previous.Value.Equals(cardWidth))
        {
            var style = new Style(typeof(ContentPresenter));
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, cardWidth));
            list.ItemContainerStyle = style;
        }

        Find<ColumnDefinition>("SettingsRightColumn").Width = wide
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        string[] cards = ["ProfileCard", "ConnectionCard", "DisplayCard", "AppearanceCard", "AdvancedCard"];
        for (int i = 0; i < cards.Length; i++)
        {
            var card = Find<Border>(cards[i]);
            Grid.SetRow(card, wide ? i / 2 : i);
            Grid.SetColumn(card, wide ? i % 2 : 0);
            Grid.SetColumnSpan(card, wide && i == 4 ? 2 : 1);
            card.Margin = wide && i < 4
                ? new Thickness(i % 2 == 0 ? 0 : 7, 0, i % 2 == 0 ? 7 : 0, 14)
                : new Thickness(0, 0, 0, 14);
        }
    }
}
