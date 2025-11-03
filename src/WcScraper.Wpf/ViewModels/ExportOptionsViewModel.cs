using System.ComponentModel;

namespace WcScraper.Wpf.ViewModels;

public sealed class ExportOptionsViewModel : MainViewModelFeatureBase
{
    public ExportOptionsViewModel(MainViewModel owner)
        : base(owner)
    {
    }

    public bool ExportCsv
    {
        get => Owner.ExportCsv;
        set => Owner.ExportCsv = value;
    }

    public bool ExportShopify
    {
        get => Owner.ExportShopify;
        set => Owner.ExportShopify = value;
    }

    public bool ExportWoo
    {
        get => Owner.ExportWoo;
        set => Owner.ExportWoo = value;
    }

    public bool ExportReviews
    {
        get => Owner.ExportReviews;
        set => Owner.ExportReviews = value;
    }

    public bool ExportXlsx
    {
        get => Owner.ExportXlsx;
        set => Owner.ExportXlsx = value;
    }

    public bool ExportJsonl
    {
        get => Owner.ExportJsonl;
        set => Owner.ExportJsonl = value;
    }

    public bool ExportPluginsCsv
    {
        get => Owner.ExportPluginsCsv;
        set => Owner.ExportPluginsCsv = value;
    }

    public bool ExportPluginsJsonl
    {
        get => Owner.ExportPluginsJsonl;
        set => Owner.ExportPluginsJsonl = value;
    }

    public bool ExportThemesCsv
    {
        get => Owner.ExportThemesCsv;
        set => Owner.ExportThemesCsv = value;
    }

    public bool ExportThemesJsonl
    {
        get => Owner.ExportThemesJsonl;
        set => Owner.ExportThemesJsonl = value;
    }

    public bool ExportPublicExtensionFootprints
    {
        get => Owner.ExportPublicExtensionFootprints;
        set => Owner.ExportPublicExtensionFootprints = value;
    }

    public string AdditionalPublicExtensionPages
    {
        get => Owner.AdditionalPublicExtensionPages;
        set => Owner.AdditionalPublicExtensionPages = value;
    }

    public string PublicExtensionMaxPages
    {
        get => Owner.PublicExtensionMaxPages;
        set => Owner.PublicExtensionMaxPages = value;
    }

    public string PublicExtensionMaxBytes
    {
        get => Owner.PublicExtensionMaxBytes;
        set => Owner.PublicExtensionMaxBytes = value;
    }

    public bool ExportPublicDesignSnapshot
    {
        get => Owner.ExportPublicDesignSnapshot;
        set => Owner.ExportPublicDesignSnapshot = value;
    }

    public string AdditionalDesignSnapshotPages
    {
        get => Owner.AdditionalDesignSnapshotPages;
        set => Owner.AdditionalDesignSnapshotPages = value;
    }

    public bool ExportPublicDesignScreenshots
    {
        get => Owner.ExportPublicDesignScreenshots;
        set => Owner.ExportPublicDesignScreenshots = value;
    }

    public string DesignScreenshotBreakpointsText
    {
        get => Owner.DesignScreenshotBreakpointsText;
        set => Owner.DesignScreenshotBreakpointsText = value;
    }

    public bool ExportStoreConfiguration
    {
        get => Owner.ExportStoreConfiguration;
        set => Owner.ExportStoreConfiguration = value;
    }

    public bool IsWooCommerce => Owner.IsWooCommerce;

    public bool IsShopify => Owner.IsShopify;

    public bool HasWordPressCredentials => Owner.HasWordPressCredentials;

    public bool CanExportExtensions => Owner.CanExportExtensions;

    public bool CanExportPublicExtensionFootprints => Owner.CanExportPublicExtensionFootprints;

    public bool CanExportPublicDesignSnapshot => Owner.CanExportPublicDesignSnapshot;

    public bool CanExportPublicDesignScreenshots => Owner.CanExportPublicDesignScreenshots;

    public bool CanExportStoreConfiguration => Owner.CanExportStoreConfiguration;

    protected override void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ExportCsv):
                OnPropertyChanged(nameof(ExportCsv));
                break;
            case nameof(MainViewModel.ExportShopify):
                OnPropertyChanged(nameof(ExportShopify));
                break;
            case nameof(MainViewModel.ExportWoo):
                OnPropertyChanged(nameof(ExportWoo));
                break;
            case nameof(MainViewModel.ExportReviews):
                OnPropertyChanged(nameof(ExportReviews));
                break;
            case nameof(MainViewModel.ExportXlsx):
                OnPropertyChanged(nameof(ExportXlsx));
                break;
            case nameof(MainViewModel.ExportJsonl):
                OnPropertyChanged(nameof(ExportJsonl));
                break;
            case nameof(MainViewModel.ExportPluginsCsv):
                OnPropertyChanged(nameof(ExportPluginsCsv));
                break;
            case nameof(MainViewModel.ExportPluginsJsonl):
                OnPropertyChanged(nameof(ExportPluginsJsonl));
                break;
            case nameof(MainViewModel.ExportThemesCsv):
                OnPropertyChanged(nameof(ExportThemesCsv));
                break;
            case nameof(MainViewModel.ExportThemesJsonl):
                OnPropertyChanged(nameof(ExportThemesJsonl));
                break;
            case nameof(MainViewModel.ExportPublicExtensionFootprints):
                OnPropertyChanged(nameof(ExportPublicExtensionFootprints));
                break;
            case nameof(MainViewModel.AdditionalPublicExtensionPages):
                OnPropertyChanged(nameof(AdditionalPublicExtensionPages));
                break;
            case nameof(MainViewModel.PublicExtensionMaxPages):
                OnPropertyChanged(nameof(PublicExtensionMaxPages));
                break;
            case nameof(MainViewModel.PublicExtensionMaxBytes):
                OnPropertyChanged(nameof(PublicExtensionMaxBytes));
                break;
            case nameof(MainViewModel.ExportPublicDesignSnapshot):
                OnPropertyChanged(nameof(ExportPublicDesignSnapshot));
                break;
            case nameof(MainViewModel.AdditionalDesignSnapshotPages):
                OnPropertyChanged(nameof(AdditionalDesignSnapshotPages));
                break;
            case nameof(MainViewModel.ExportPublicDesignScreenshots):
                OnPropertyChanged(nameof(ExportPublicDesignScreenshots));
                break;
            case nameof(MainViewModel.DesignScreenshotBreakpointsText):
                OnPropertyChanged(nameof(DesignScreenshotBreakpointsText));
                break;
            case nameof(MainViewModel.ExportStoreConfiguration):
                OnPropertyChanged(nameof(ExportStoreConfiguration));
                break;
            case nameof(MainViewModel.IsWooCommerce):
                OnPropertyChanged(nameof(IsWooCommerce));
                break;
            case nameof(MainViewModel.IsShopify):
                OnPropertyChanged(nameof(IsShopify));
                break;
            case nameof(MainViewModel.HasWordPressCredentials):
                OnPropertyChanged(nameof(HasWordPressCredentials));
                OnPropertyChanged(nameof(CanExportExtensions));
                OnPropertyChanged(nameof(CanExportPublicExtensionFootprints));
                OnPropertyChanged(nameof(CanExportPublicDesignSnapshot));
                OnPropertyChanged(nameof(CanExportPublicDesignScreenshots));
                OnPropertyChanged(nameof(CanExportStoreConfiguration));
                break;
            case nameof(MainViewModel.CanExportExtensions):
                OnPropertyChanged(nameof(CanExportExtensions));
                break;
            case nameof(MainViewModel.CanExportPublicExtensionFootprints):
                OnPropertyChanged(nameof(CanExportPublicExtensionFootprints));
                break;
            case nameof(MainViewModel.CanExportPublicDesignSnapshot):
                OnPropertyChanged(nameof(CanExportPublicDesignSnapshot));
                break;
            case nameof(MainViewModel.CanExportPublicDesignScreenshots):
                OnPropertyChanged(nameof(CanExportPublicDesignScreenshots));
                break;
            case nameof(MainViewModel.CanExportStoreConfiguration):
                OnPropertyChanged(nameof(CanExportStoreConfiguration));
                break;
        }
    }
}
