using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WcScraper.Wpf.ViewModels;

internal abstract class MainViewModelFeatureBase : INotifyPropertyChanged
{
    protected MainViewModelFeatureBase(MainViewModel owner)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Owner.PropertyChanged += OnOwnerPropertyChangedInternal;
    }

    protected MainViewModel Owner { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnOwnerPropertyChangedInternal(object? sender, PropertyChangedEventArgs e)
        => OnOwnerPropertyChanged(sender, e);

    protected virtual void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
