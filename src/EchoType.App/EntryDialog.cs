namespace EchoType.App;

/// <summary>小さな設定項目を検証して保存する共通の編集画面。</summary>
internal sealed class EntryDialog : Window
{
    private readonly StackPanel _fields = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    public EntryDialog(Window owner, string title, Action save)
    {
        Owner = owner; Title = WindowTitle.Create(title); Width = 640; Height = 570; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(16) }; Content = layout;
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer); footer.Children.Add(_status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(buttons);
        buttons.Children.Add(new Button { Content = "取り消す", IsCancel = true });
        var accept = new Button { Content = "保存" }; buttons.Children.Add(accept);
        accept.Click += (_, _) => { try { save(); DialogResult = true; } catch (Exception ex) { _status.Text = ex.Message; } };
        layout.Children.Add(new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
    public TextBox Text(string label, string value, int max = 100, bool multiline = false)
    {
        _fields.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
        var box = new TextBox { Text = value, MaxLength = max, MinHeight = 30, AcceptsReturn = multiline,
            Height = multiline ? 95 : 30, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _fields.Children.Add(box); return box;
    }
    public CheckBox Check(string label, bool value) { var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 8, 0, 0) }; _fields.Children.Add(box); return box; }
    public (Label Label, PasswordBox Box) Password(string label, string value)
    {
        var box = new PasswordBox { Password = value, MaxLength = 4096, MinHeight = 30 };
        var title = new Label { Content = label, Target = box };
        _fields.Children.Add(title);
        _fields.Children.Add(box);
        return (title, box);
    }
    public ComboBox Choice(string label, IEnumerable<string> choices, string selected)
    {
        _fields.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
        var box = new ComboBox { ItemsSource = choices.ToArray(), SelectedItem = selected, MinHeight = 30 }; _fields.Children.Add(box); return box;
    }
}
