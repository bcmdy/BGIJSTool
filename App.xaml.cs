using System.Configuration;
using System.Data;
using System.Text;
using System.Windows;

namespace BGIJSTool;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        InitializeComponent();
    }
}


