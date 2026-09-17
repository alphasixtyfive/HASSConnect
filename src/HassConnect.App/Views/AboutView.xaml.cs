using HassConnect.Core;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App.Views;

public sealed partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        ProductName.Text = ProductInfo.Name;
        VersionText.Text = $"Version {ProductInfo.Version}";
        DeveloperName.Text = ProductInfo.DeveloperName;
        DeveloperLink.NavigateUri = ProductInfo.DeveloperPage;
        RepositoryLink.NavigateUri = ProductInfo.RepositoryPage;
        ReleasesLink.NavigateUri = ProductInfo.ReleasesPage;
        IssuesLink.NavigateUri = ProductInfo.IssuesPage;
        LicenseLink.NavigateUri = ProductInfo.LicensePage;
    }
}
