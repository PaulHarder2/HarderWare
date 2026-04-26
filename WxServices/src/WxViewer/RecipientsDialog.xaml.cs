using System.Windows;

namespace WxViewer;

public partial class RecipientsDialog : Window
{
    public RecipientsDialog(MeteogramItem item)
    {
        InitializeComponent();
        Title = $"Recipients — {item.Icao}";
        RecipientGrid.ItemsSource = item.Recipients;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}