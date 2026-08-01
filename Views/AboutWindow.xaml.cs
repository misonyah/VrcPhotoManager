using System.Windows;

namespace VrcPhotoManager.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DialogWindowBehavior.HideMinimizeAndMaximizeButtons(this);
        DialogWindowBehavior.CloseOnDeactivated(this);
        DialogWindowBehavior.OpenNearCursor(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
