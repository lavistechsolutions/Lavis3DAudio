using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace Lavis3DAudio;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version?.ToString() ?? "1.0.0.51";

        FeedbackLink.MouseLeftButtonUp += (_, _) => Open("https://lavistech.com/contact");
        SteamLicenseLink.MouseLeftButtonUp += (_, _) => OpenLicense("SteamAudio-Apache-2.0.pdf");
        NAudioLicenseLink.MouseLeftButtonUp += (_, _) => OpenLicense("NAudio-MIT.pdf");
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    void OpenLicense(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "docs", fileName);
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "That license PDF is not in the docs folder next to the application.",
                "Lavis 3D Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Open(path);
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lavis 3D Audio", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
