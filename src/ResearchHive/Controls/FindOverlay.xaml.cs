using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ResearchHive.Controls;

/// <summary>
/// A Ctrl+F search overlay that finds text across all visible TextBox and MarkdownViewer
/// controls within a target parent element. Highlights matches and scrolls to them.
/// </summary>
public partial class FindOverlay : UserControl
{
    /// <summary>The parent element whose visual tree we search.</summary>
    public FrameworkElement? SearchRoot { get; set; }

    private readonly List<SearchMatch> _matches = new();
    private int _currentIndex = -1;

    // Highlight brushes
    private static readonly SolidColorBrush HighlightBrush = new(Color.FromArgb(100, 255, 213, 0));
    private static readonly SolidColorBrush ActiveHighlightBrush = new(Color.FromArgb(180, 255, 165, 0));

    public FindOverlay()
    {
        InitializeComponent();
    }

    /// <summary>Show the overlay and focus the search box.</summary>
    public void Open()
    {
        Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Hide the overlay and clear highlights.</summary>
    public void Close()
    {
        Visibility = Visibility.Collapsed;
        ClearHighlights();
        _matches.Clear();
        _currentIndex = -1;
        MatchCounter.Text = "";
    }

    /// <summary>Toggle open/close.</summary>
    public void Toggle()
    {
        if (Visibility == Visibility.Visible)
            Close();
        else
            Open();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PerformSearch();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                NavigatePrevious();
            else
                NavigateNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => NavigatePrevious();
    private void NextButton_Click(object sender, RoutedEventArgs e) => NavigateNext();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void PerformSearch()
    {
        ClearHighlights();
        _matches.Clear();
        _currentIndex = -1;

        var query = SearchBox.Text;
        if (string.IsNullOrEmpty(query) || SearchRoot == null)
        {
            MatchCounter.Text = "";
            return;
        }

        // Walk the visual tree and find all searchable controls
        var textBoxes = FindVisualChildren<TextBox>(SearchRoot)
            .Where(tb => tb.IsVisible && !string.IsNullOrEmpty(tb.Text))
            .ToList();

        var markdownViewers = FindVisualChildren<MarkdownViewer>(SearchRoot)
            .Where(mv => mv.IsVisible)
            .ToList();

        // Search TextBoxes
        foreach (var tb in textBoxes)
        {
            var text = tb.Text;
            int startIndex = 0;
            while (true)
            {
                int idx = text.IndexOf(query, startIndex, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                _matches.Add(new SearchMatch(tb, idx, query.Length));
                startIndex = idx + 1;
            }
        }

        // Search MarkdownViewer FlowDocuments
        foreach (var mv in markdownViewers)
        {
            if (mv.Document == null) continue;
            var textRange = new TextRange(mv.Document.ContentStart, mv.Document.ContentEnd);
            var fullText = textRange.Text;
            int startIndex = 0;
            while (true)
            {
                int idx = fullText.IndexOf(query, startIndex, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                _matches.Add(new SearchMatch(mv, idx, query.Length));
                startIndex = idx + 1;
            }
        }

        // Update counter
        if (_matches.Count > 0)
        {
            _currentIndex = 0;
            MatchCounter.Text = $"1/{_matches.Count}";
            HighlightCurrent();
        }
        else
        {
            MatchCounter.Text = "0/0";
        }
    }

    private void NavigateNext()
    {
        if (_matches.Count == 0) return;
        ClearCurrentHighlight();
        _currentIndex = (_currentIndex + 1) % _matches.Count;
        MatchCounter.Text = $"{_currentIndex + 1}/{_matches.Count}";
        HighlightCurrent();
    }

    private void NavigatePrevious()
    {
        if (_matches.Count == 0) return;
        ClearCurrentHighlight();
        _currentIndex = (_currentIndex - 1 + _matches.Count) % _matches.Count;
        MatchCounter.Text = $"{_currentIndex + 1}/{_matches.Count}";
        HighlightCurrent();
    }

    private void HighlightCurrent()
    {
        if (_currentIndex < 0 || _currentIndex >= _matches.Count) return;
        var match = _matches[_currentIndex];

        if (match.Control is TextBox tb)
        {
            // Select the match text in the TextBox
            tb.Focus();
            tb.Select(match.StartIndex, match.Length);

            // Scroll the TextBox into view
            tb.BringIntoView();

            // Also scroll the parent ScrollViewer to show this TextBox
            ScrollIntoViewWithinParent(tb);
        }
        else if (match.Control is MarkdownViewer mv && mv.Document != null)
        {
            // Find the TextPointer at the match position and highlight it
            var start = GetTextPointerAtOffset(mv.Document.ContentStart, match.StartIndex);
            var end = GetTextPointerAtOffset(mv.Document.ContentStart, match.StartIndex + match.Length);
            if (start != null && end != null)
            {
                mv.Selection.Select(start, end);
                // Bring the FlowDocumentScrollViewer into view
                var rect = start.GetCharacterRect(LogicalDirection.Forward);
                if (!rect.IsEmpty)
                {
                    mv.BringIntoView();
                    ScrollIntoViewWithinParent(mv);
                }
            }
        }
    }

    private void ClearCurrentHighlight()
    {
        // TextBox selections clear automatically when focus moves
        // MarkdownViewer selections also clear — no explicit cleanup needed
    }

    private void ClearHighlights()
    {
        // Clear any TextBox selections
        foreach (var match in _matches)
        {
            if (match.Control is TextBox tb)
            {
                try { tb.Select(0, 0); } catch { }
            }
            else if (match.Control is MarkdownViewer mv)
            {
                try
                {
                    mv.Selection.Select(mv.Document.ContentStart, mv.Document.ContentStart);
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Gets a TextPointer at a character offset from a starting position in a FlowDocument.
    /// </summary>
    private static TextPointer? GetTextPointerAtOffset(TextPointer start, int offset)
    {
        var current = start;
        int count = 0;

        while (current != null)
        {
            if (current.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = current.GetTextInRun(LogicalDirection.Forward);
                if (count + text.Length >= offset)
                {
                    return current.GetPositionAtOffset(offset - count);
                }
                count += text.Length;
            }
            current = current.GetNextContextPosition(LogicalDirection.Forward);
        }
        return null;
    }

    /// <summary>
    /// Scrolls the parent ScrollViewer so the target element is visible.
    /// </summary>
    private static void ScrollIntoViewWithinParent(FrameworkElement target)
    {
        var scrollViewer = FindParent<ScrollViewer>(target);
        if (scrollViewer == null) return;

        // Get target's position relative to the ScrollViewer
        var transform = target.TransformToAncestor(scrollViewer);
        var position = transform.Transform(new Point(0, 0));

        // Scroll so the element is visible (with some padding above)
        var targetOffset = scrollViewer.VerticalOffset + position.Y - 60;
        if (targetOffset < 0) targetOffset = 0;
        scrollViewer.ScrollToVerticalOffset(targetOffset);
    }

    /// <summary>Find a parent of a given type in the visual tree.</summary>
    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T result) return result;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    /// <summary>Find all children of a given type in the visual tree (recursive).</summary>
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                yield return typedChild;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    /// <summary>Represents a single search match in a control.</summary>
    private record SearchMatch(FrameworkElement Control, int StartIndex, int Length);
}
