using System.Collections.ObjectModel;
using WcScraper.Wpf.Models;
using WcScraper.Wpf;

namespace WcScraper.Wpf.ViewModels;

public sealed class FilterOptionsViewModel : MainViewModelFeatureBase
{
    public FilterOptionsViewModel(MainViewModel owner)
        : base(owner)
    {
    }

    public RelayCommand ExportCollectionsCommand => Owner.ExportCollectionsCommand;

    public RelayCommand SelectAllCategoriesCommand => Owner.SelectAllCategoriesCommand;

    public RelayCommand ClearCategoriesCommand => Owner.ClearCategoriesCommand;

    public RelayCommand SelectAllTagsCommand => Owner.SelectAllTagsCommand;

    public RelayCommand ClearTagsCommand => Owner.ClearTagsCommand;

    public ObservableCollection<SelectableTerm> CategoryChoices => Owner.CategoryChoices;

    public ObservableCollection<SelectableTerm> TagChoices => Owner.TagChoices;
}
