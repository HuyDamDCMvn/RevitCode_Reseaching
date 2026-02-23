using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HD.Core.Models;

namespace CommonFeature.Views
{
    /// <summary>
    /// Isolate Window - Cascading dropdown filters with immediate isolate
    /// </summary>
    public partial class IsolateWindow : Window
    {
        #region Fields & Properties
        
        private int _suppressEvents = 0;
        private bool IsEventsSuppressed => _suppressEvents > 0;
        
        // Selection state for view change re-isolation
        private string _currentCategoryName;
        private string _currentFamilyName;
        private string _currentFamilyTypeName;
        private string _currentParameterName;
        private string _currentParameterValue;
        
        // View change detection
        private string _lastViewName;
        private System.Windows.Threading.DispatcherTimer _viewCheckTimer;
        
        #endregion

        #region Callbacks
        
        public Func<List<CategoryItem>> GetCategoriesCallback { get; set; }
        public Func<string, List<FamilyItem>> GetFamiliesCallback { get; set; }
        public Func<string, string, List<FamilyTypeItem>> GetTypesCallback { get; set; }
        public Func<string, string, string, List<string>> GetParametersCallback { get; set; }
        public Func<string, string, string, string, List<ParameterValueItem>> GetValuesCallback { get; set; }
        public Action<IsolateRequest> IsolateCallback { get; set; }
        public Action ResetIsolateCallback { get; set; }
        public Func<string, string, string, string, string, List<long>> ReIsolateCallback { get; set; }
        public Func<string> GetViewNameCallback { get; set; }
        /// <summary>Call to refresh cached view when user switches views in Revit.</summary>
        public Action RefreshIsolateViewRequested { get; set; }
        
        #endregion

        #region Constructor & Lifecycle
        
        public IsolateWindow()
        {
            InitializeComponent();
            InitializeTimer();
        }
        
        private void InitializeTimer()
        {
            _viewCheckTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _viewCheckTimer.Tick += OnViewCheckTick;
        }
        
        protected override void OnClosed(EventArgs e)
        {
            _viewCheckTimer?.Stop();
            base.OnClosed(e);
        }
        
        #endregion

        #region Public Entry Point
        
        public void LoadData()
        {
            SuppressEvents(() =>
            {
                LoadCategories();
                UpdateStatus("Ready. Select a Category to isolate.");
                _lastViewName = GetViewNameCallback?.Invoke();
                _viewCheckTimer.Start();
            });
        }
        
        #endregion

        #region Event Suppression Helper
        
        private void SuppressEvents(Action action)
        {
            _suppressEvents++;
            try { action(); }
            finally { if (_suppressEvents > 0) _suppressEvents--; }
        }
        
        #endregion

        #region Data Loading
        
        private void LoadCategories()
        {
            var data = GetCategoriesCallback?.Invoke() ?? new List<CategoryItem>();
            
            var allIds = data.SelectMany(c => c.ElementIds).ToList();
            var items = new List<CategoryItem>
            {
                new CategoryItem { Name = "(All Categories)", CategoryId = -1, ElementCount = allIds.Count, ElementIds = allIds }
            };
            items.AddRange(data.OrderBy(c => c.Name));
            
            SetComboData(CategoryCombo, items, true);
            ClearCombo(FamilyCombo, FamilyTypeCombo, ParameterCombo, ValueCombo);
        }
        
        private void LoadFamilies(string categoryName)
        {
            if (string.IsNullOrEmpty(categoryName)) { ClearCombo(FamilyCombo, FamilyTypeCombo, ParameterCombo, ValueCombo); return; }
            
            var data = GetFamiliesCallback?.Invoke(categoryName) ?? new List<FamilyItem>();
            
            if (data.Count == 0)
            {
                ClearCombo(FamilyCombo, FamilyTypeCombo);
                LoadParameters(categoryName, null, null);
                return;
            }
            
            var allIds = data.SelectMany(f => f.ElementIds).ToList();
            var items = new List<FamilyItem>
            {
                new FamilyItem { FamilyName = "(All Families)", CategoryName = categoryName, ElementCount = allIds.Count, ElementIds = allIds }
            };
            items.AddRange(data.OrderBy(f => f.FamilyName));
            
            SetComboData(FamilyCombo, items, true);
            ClearCombo(FamilyTypeCombo);
            LoadParameters(categoryName, null, null);
        }
        
        private void LoadTypes(string categoryName, string familyName)
        {
            if (string.IsNullOrEmpty(familyName)) { ClearCombo(FamilyTypeCombo); LoadParameters(categoryName, null, null); return; }
            
            var data = GetTypesCallback?.Invoke(categoryName, familyName) ?? new List<FamilyTypeItem>();
            
            if (data.Count == 0)
            {
                ClearCombo(FamilyTypeCombo);
                LoadParameters(categoryName, familyName, null);
                return;
            }
            
            var allIds = data.SelectMany(t => t.ElementIds).ToList();
            var items = new List<FamilyTypeItem>
            {
                new FamilyTypeItem { TypeName = "(All Types)", FamilyName = familyName, CategoryName = categoryName, ElementCount = allIds.Count, ElementIds = allIds }
            };
            items.AddRange(data.OrderBy(t => t.TypeName));
            
            SetComboData(FamilyTypeCombo, items, true);
            LoadParameters(categoryName, familyName, null);
        }
        
        private void LoadParameters(string categoryName, string familyName, string typeName)
        {
            var data = GetParametersCallback?.Invoke(categoryName, familyName, typeName) ?? new List<string>();
            
            if (data.Count == 0) { ClearCombo(ParameterCombo, ValueCombo); return; }
            
            var items = new List<string> { "(Select Parameter)" };
            items.AddRange(data.OrderBy(p => p));
            
            SetComboData(ParameterCombo, items, true);
            ClearCombo(ValueCombo);
        }
        
        private void LoadValues(string categoryName, string familyName, string typeName, string paramName)
        {
            if (string.IsNullOrEmpty(paramName)) { ClearCombo(ValueCombo); return; }
            
            var data = GetValuesCallback?.Invoke(categoryName, familyName, typeName, paramName) ?? new List<ParameterValueItem>();
            
            if (data.Count == 0) { ClearCombo(ValueCombo); return; }
            
            var allIds = data.SelectMany(v => v.ElementIds).ToList();
            var items = new List<ParameterValueItem>
            {
                new ParameterValueItem { Value = "(All Values)", ElementCount = allIds.Count, ElementIds = allIds }
            };
            items.AddRange(data.OrderBy(v => v.Value));
            
            SetComboData(ValueCombo, items, true);
        }
        
        #endregion

        #region ComboBox Helpers
        
        private void SetComboData<T>(ComboBox combo, List<T> items, bool enable)
        {
            combo.ItemsSource = items;
            combo.SelectedIndex = items.Count > 0 ? 0 : -1;
            combo.IsEnabled = enable && items.Count > 0;
        }
        
        private void ClearCombo(params ComboBox[] combos)
        {
            foreach (var c in combos)
            {
                c.ItemsSource = null;
                c.IsEnabled = false;
            }
        }
        
        #endregion

        #region Selection State Helpers
        
        private CategoryItem GetSelectedCategory() => CategoryCombo.SelectedItem as CategoryItem;
        private FamilyItem GetSelectedFamily() => FamilyCombo.SelectedItem as FamilyItem;
        private FamilyTypeItem GetSelectedType() => FamilyTypeCombo.SelectedItem as FamilyTypeItem;
        private string GetSelectedParameter() => ParameterCombo.SelectedItem as string;
        private ParameterValueItem GetSelectedValue() => ValueCombo.SelectedItem as ParameterValueItem;
        
        private string GetEffectiveFamilyName()
        {
            var f = GetSelectedFamily();
            return (f != null && !f.IsAllItem) ? f.FamilyName : null;
        }
        
        private string GetEffectiveTypeName()
        {
            var t = GetSelectedType();
            return (t != null && !t.IsAllItem) ? t.TypeName : null;
        }
        
        private void SaveSelectionState(string category, string family, string type, string param, string value)
        {
            _currentCategoryName = category;
            _currentFamilyName = family;
            _currentFamilyTypeName = type;
            _currentParameterName = param;
            _currentParameterValue = value;
        }
        
        private void ClearSelectionState() => SaveSelectionState(null, null, null, null, null);
        
        #endregion

        #region Event Handlers
        
        private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsEventsSuppressed) return;
            
            var cat = GetSelectedCategory();
            if (cat == null) return;
            
            SuppressEvents(() =>
            {
                ClearCombo(FamilyCombo, FamilyTypeCombo, ParameterCombo, ValueCombo);
                
                if (cat.IsAllItem)
                {
                    UpdateInfo("Select a specific Category to isolate.");
                    ClearSelectionState();
                    return;
                }
                
                LoadFamilies(cat.Name);
                DoIsolate(cat.ElementIds, $"Category: {cat.Name}", cat.Name, null, null, null, null);
            });
        }
        
        private void FamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsEventsSuppressed) return;
            
            var cat = GetSelectedCategory();
            var fam = GetSelectedFamily();
            if (cat == null || cat.IsAllItem || fam == null) return;
            
            SuppressEvents(() =>
            {
                ClearCombo(FamilyTypeCombo, ParameterCombo, ValueCombo);
                
                if (fam.IsAllItem)
                {
                    LoadParameters(cat.Name, null, null);
                    DoIsolate(cat.ElementIds, $"Category: {cat.Name}", cat.Name, null, null, null, null);
                }
                else
                {
                    LoadTypes(cat.Name, fam.FamilyName);
                    DoIsolate(fam.ElementIds, $"Family: {fam.FamilyName}", cat.Name, fam.FamilyName, null, null, null);
                }
            });
        }
        
        private void FamilyTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsEventsSuppressed) return;
            
            var cat = GetSelectedCategory();
            var fam = GetSelectedFamily();
            var typ = GetSelectedType();
            if (cat == null || fam == null || typ == null) return;
            
            SuppressEvents(() =>
            {
                ClearCombo(ParameterCombo, ValueCombo);
                
                var famName = GetEffectiveFamilyName();
                var typName = GetEffectiveTypeName();
                
                LoadParameters(cat.Name, famName, typName);
                
                if (typ.IsAllItem)
                {
                    if (fam.IsAllItem)
                        DoIsolate(cat.ElementIds, $"Category: {cat.Name}", cat.Name, null, null, null, null);
                    else
                        DoIsolate(fam.ElementIds, $"Family: {fam.FamilyName}", cat.Name, fam.FamilyName, null, null, null);
                }
                else
                {
                    DoIsolate(typ.ElementIds, $"Type: {typ.TypeName}", cat.Name, famName, typ.TypeName, null, null);
                }
            });
        }
        
        private void ParameterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsEventsSuppressed) return;
            
            var param = GetSelectedParameter();
            
            SuppressEvents(() =>
            {
                ClearCombo(ValueCombo);
                
                if (string.IsNullOrEmpty(param) || param.StartsWith("(")) return;
                
                var cat = GetSelectedCategory();
                LoadValues(cat?.Name, GetEffectiveFamilyName(), GetEffectiveTypeName(), param);
            });
        }
        
        private void ValueCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsEventsSuppressed) return;
            
            var val = GetSelectedValue();
            if (val == null) return;
            
            var cat = GetSelectedCategory();
            var fam = GetSelectedFamily();
            var typ = GetSelectedType();
            var param = GetSelectedParameter();
            
            if (val.IsAllItem)
            {
                // Isolate by highest specific level
                if (typ != null && !typ.IsAllItem)
                    DoIsolate(typ.ElementIds, $"Type: {typ.TypeName}", cat?.Name, GetEffectiveFamilyName(), typ.TypeName, null, null);
                else if (fam != null && !fam.IsAllItem)
                    DoIsolate(fam.ElementIds, $"Family: {fam.FamilyName}", cat?.Name, fam.FamilyName, null, null, null);
                else if (cat != null && !cat.IsAllItem)
                    DoIsolate(cat.ElementIds, $"Category: {cat.Name}", cat.Name, null, null, null, null);
            }
            else
            {
                DoIsolate(val.ElementIds, $"{param} = {val.Value}", cat?.Name, GetEffectiveFamilyName(), GetEffectiveTypeName(), param, val.Value);
            }
        }
        
        #endregion

        #region Isolate Execution
        
        private void DoIsolate(List<long> elementIds, string description, string catName, string famName, string typName, string paramName, string paramValue)
        {
            if (IsolateCallback == null) return;
            
            var ids = elementIds ?? new List<long>();
            if (ids.Count == 0)
            {
                UpdateInfo("No elements found for current selection.");
                return;
            }
            
            SaveSelectionState(catName, famName, typName, paramName, paramValue);
            
            var request = new IsolateRequest { ElementIds = ids, Description = description };
            IsolateCallback(request);
            
            UpdateInfo($"Isolated: {description}");
            ElementCountText.Text = $"{ids.Count} elements isolated";
            UpdateStatus($"Isolated {ids.Count} elements");
        }
        
        #endregion

        #region View Change Detection
        
        private void OnViewCheckTick(object sender, EventArgs e)
        {
            if (GetViewNameCallback == null || ReIsolateCallback == null) return;
            
            try
            {
                // Refresh cache from Revit thread so GetViewNameCallback returns current view
                RefreshIsolateViewRequested?.Invoke();
                var viewName = GetViewNameCallback();
                if (!string.IsNullOrEmpty(viewName) && viewName != _lastViewName)
                {
                    _lastViewName = viewName;
                    OnViewChanged(viewName);
                }
            }
            catch { /* Ignore */ }
        }
        
        private void OnViewChanged(string viewName)
        {
            if (string.IsNullOrEmpty(_currentCategoryName))
            {
                UpdateStatus($"View: {viewName}");
                return;
            }
            
            var ids = ReIsolateCallback(_currentCategoryName, _currentFamilyName, _currentFamilyTypeName, _currentParameterName, _currentParameterValue);
            
            if (ids == null || ids.Count == 0)
            {
                UpdateStatus($"View: {viewName} - No matching elements");
                UpdateInfo($"No elements match filter in '{viewName}'");
                return;
            }
            
            var desc = BuildDescription();
            IsolateCallback?.Invoke(new IsolateRequest { ElementIds = ids, Description = desc });
            
            UpdateStatus($"View: {viewName} - {ids.Count} elements");
            UpdateInfo($"Re-isolated on '{viewName}': {desc}");
            ElementCountText.Text = $"{ids.Count} elements isolated";
        }
        
        private string BuildDescription()
        {
            if (!string.IsNullOrEmpty(_currentParameterValue)) return $"{_currentParameterName} = {_currentParameterValue}";
            if (!string.IsNullOrEmpty(_currentFamilyTypeName)) return $"Type: {_currentFamilyTypeName}";
            if (!string.IsNullOrEmpty(_currentFamilyName)) return $"Family: {_currentFamilyName}";
            return $"Category: {_currentCategoryName}";
        }
        
        #endregion

        #region Button Handlers
        
        private void ClearFilters_Click(object sender, RoutedEventArgs e)
        {
            SuppressEvents(() =>
            {
                ClearSelectionState();
                CategoryCombo.SelectedIndex = 0;
                ClearCombo(FamilyCombo, FamilyTypeCombo, ParameterCombo, ValueCombo);
                ResetIsolateCallback?.Invoke();
                UpdateInfo("Filters cleared. All elements visible.");
                ElementCountText.Text = "";
                UpdateStatus("Cleared");
            });
        }
        
        private void ResetIsolate_Click(object sender, RoutedEventArgs e)
        {
            ResetIsolateCallback?.Invoke();
            UpdateInfo("Isolation reset. All elements visible.");
            ElementCountText.Text = "";
            UpdateStatus("Reset");
        }
        
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        
        #endregion

        #region UI Helpers
        
        private void UpdateStatus(string msg) => StatusText.Text = msg;
        private void UpdateInfo(string msg) => IsolateInfoText.Text = msg;
        
        #endregion
    }
}
