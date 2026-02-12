using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResearchHive.ViewModels;

namespace ResearchHive.Views;

public partial class SessionWorkspaceView : UserControl
{
    public SessionWorkspaceView()
    {
        InitializeComponent();
        AllowDrop = true;
        Drop += OnFileDrop;
        DragOver += OnDragOver;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && DataContext is SessionWorkspaceViewModel vm)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            await vm.HandleDroppedFilesAsync(files);
        }
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && DataContext is SessionWorkspaceViewModel vm)
        {
            // Handle both data-bound TabItemViewModel (new) and ListBoxItem (fallback)
            if (lb.SelectedItem is TabItemViewModel tabItem)
            {
                vm.ActiveTab = tabItem.Tag;
            }
            else if (lb.SelectedItem is ListBoxItem item && item.Tag is string tag)
            {
                vm.ActiveTab = tag;
            }
        }
    }

    private void Job_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is JobViewModel jobVm 
            && DataContext is SessionWorkspaceViewModel vm)
        {
            vm.SelectedJob = jobVm;
        }
    }

    private void ToggleNoteEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NotebookEntryViewModel noteVm)
        {
            if (noteVm.IsEditing)
            {
                // Save: toggle off editing and mark dirty for auto-save
                noteVm.IsEditing = false;
                if (DataContext is SessionWorkspaceViewModel vm)
                    vm.MarkNotebookDirtyCommand.Execute(null);
            }
            else
            {
                noteVm.IsEditing = true;
            }
        }
    }

    private void ActivityLog_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && sender is ListBox lb && lb.SelectedItems.Count > 0)
        {
            var lines = lb.SelectedItems.Cast<object>().Select(o => o.ToString());
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            e.Handled = true;
        }
    }

    private void DismissProgress_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SessionWorkspaceViewModel vm)
        {
            vm.ShowLiveProgress = false;
            vm.IsResearchComplete = false;
        }
    }
}
