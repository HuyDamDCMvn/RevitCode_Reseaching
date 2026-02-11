using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CommonFeature
{
    /// <summary>
    /// Base class for ViewModels using CommunityToolkit.Mvvm.
    /// Provides ObservableObject functionality with source generators.
    /// </summary>
    public abstract class ViewModelBase : ObservableObject
    {
        // CommunityToolkit.Mvvm provides:
        // - SetProperty() method
        // - OnPropertyChanged() method  
        // - INotifyPropertyChanged implementation
        //
        // Use [ObservableProperty] attribute on fields for auto-generated properties
        // Use [RelayCommand] attribute on methods for auto-generated commands
    }
}
