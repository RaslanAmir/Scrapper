using System.ComponentModel;

namespace WcScraper.Wpf.ViewModels;

public sealed class ShopifySettingsViewModel : MainViewModelFeatureBase
{
    public ShopifySettingsViewModel(MainViewModel owner)
        : base(owner)
    {
    }

    public bool IsShopify
    {
        get => Owner.IsShopify;
        set => Owner.IsShopify = value;
    }

    public string ShopifyAdminAccessToken
    {
        get => Owner.ShopifyAdminAccessToken;
        set => Owner.ShopifyAdminAccessToken = value;
    }

    public string ShopifyStorefrontAccessToken
    {
        get => Owner.ShopifyStorefrontAccessToken;
        set => Owner.ShopifyStorefrontAccessToken = value;
    }

    public string ShopifyApiKey
    {
        get => Owner.ShopifyApiKey;
        set => Owner.ShopifyApiKey = value;
    }

    public string ShopifyApiSecret
    {
        get => Owner.ShopifyApiSecret;
        set => Owner.ShopifyApiSecret = value;
    }

    protected override void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsShopify):
                OnPropertyChanged(nameof(IsShopify));
                break;
            case nameof(MainViewModel.ShopifyAdminAccessToken):
                OnPropertyChanged(nameof(ShopifyAdminAccessToken));
                break;
            case nameof(MainViewModel.ShopifyStorefrontAccessToken):
                OnPropertyChanged(nameof(ShopifyStorefrontAccessToken));
                break;
            case nameof(MainViewModel.ShopifyApiKey):
                OnPropertyChanged(nameof(ShopifyApiKey));
                break;
            case nameof(MainViewModel.ShopifyApiSecret):
                OnPropertyChanged(nameof(ShopifyApiSecret));
                break;
        }
    }
}
