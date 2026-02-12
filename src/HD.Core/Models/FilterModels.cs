using System.Collections.Generic;
using System.ComponentModel;

namespace HD.Core.Models
{
    /// <summary>
    /// Category item for dropdown filters
    /// </summary>
    public class CategoryItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public long CategoryId { get; set; }
        public int ElementCount { get; set; }
        public List<long> ElementIds { get; set; } = new();
        public string DisplayName => ElementCount > 0 ? $"{Name} ({ElementCount})" : Name;
        public bool IsAllItem => CategoryId == -1;
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Family item for dropdown filters
    /// </summary>
    public class FamilyItem : INotifyPropertyChanged
    {
        public string FamilyName { get; set; }
        public string CategoryName { get; set; }
        public int ElementCount { get; set; }
        public List<long> ElementIds { get; set; } = new();
        public string DisplayName => ElementCount > 0 ? $"{FamilyName} ({ElementCount})" : FamilyName;
        public bool IsAllItem => FamilyName?.StartsWith("(") == true;
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Family Type item for dropdown filters
    /// </summary>
    public class FamilyTypeItem : INotifyPropertyChanged
    {
        public string TypeName { get; set; }
        public string FamilyName { get; set; }
        public string CategoryName { get; set; }
        public int ElementCount { get; set; }
        public List<long> ElementIds { get; set; } = new();
        public string DisplayName => ElementCount > 0 ? $"{TypeName} ({ElementCount})" : TypeName;
        public bool IsAllItem => TypeName?.StartsWith("(") == true;
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Parameter value item for dropdown filters
    /// </summary>
    public class ParameterValueItem : INotifyPropertyChanged
    {
        public string Value { get; set; }
        public int ElementCount { get; set; }
        public List<long> ElementIds { get; set; } = new();
        public string DisplayName => ElementCount > 0 ? $"{Value} ({ElementCount})" : Value;
        public bool IsAllItem => Value?.StartsWith("(") == true;
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Request for isolate operation
    /// </summary>
    public class IsolateRequest
    {
        public List<long> ElementIds { get; set; } = new();
        public string Description { get; set; }
    }

    /// <summary>
    /// Filter item for multi-select popup
    /// </summary>
    public class FilterItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        public string Value { get; set; }
        
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
