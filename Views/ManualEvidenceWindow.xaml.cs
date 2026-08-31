using System.Windows;

namespace Timenotifier.Views;

public partial class ManualEvidenceWindow : Window
{
    public ManualEvidenceWindow(
        IReadOnlyList<string> defaultTags,
        string title = "记录完成事项",
        string prompt = "我刚完成了什么")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        TagsBox.Text = string.Join("，", defaultTags);
    }

    public string ContentText => ContentBox.Text.Trim();
    public IReadOnlyList<string> Tags => TagsBox.Text
        .Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
