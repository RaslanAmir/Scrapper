using System;
using System.ComponentModel;

namespace WcScraper.Wpf.ViewModels;

public sealed class WooCommerceSettingsViewModel : MainViewModelFeatureBase
{
    public WooCommerceSettingsViewModel(MainViewModel owner)
        : base(owner)
    {
    }

    public bool IsWooCommerce
    {
        get => Owner.IsWooCommerce;
        set => Owner.IsWooCommerce = value;
    }

    public string WordPressUsername
    {
        get => Owner.WordPressUsername;
        set => Owner.WordPressUsername = value;
    }

    public string WordPressApplicationPassword
    {
        get => Owner.WordPressApplicationPassword;
        set => Owner.WordPressApplicationPassword = value;
    }

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
            case nameof(MainViewModel.IsWooCommerce):
                OnPropertyChanged(nameof(IsWooCommerce));
                break;
            case nameof(MainViewModel.WordPressUsername):
                OnPropertyChanged(nameof(WordPressUsername));
                goto case nameof(MainViewModel.HasWordPressCredentials);
            case nameof(MainViewModel.WordPressApplicationPassword):
                OnPropertyChanged(nameof(WordPressApplicationPassword));
                goto case nameof(MainViewModel.HasWordPressCredentials);
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
