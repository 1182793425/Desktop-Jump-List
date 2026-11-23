using System.Collections.ObjectModel;
using System.Windows.Input;
using DesktopJumpList.Helpers;
using DesktopJumpList.Models;

namespace DesktopJumpList.ViewModels;

public sealed class ShortcutEditorViewModel : ObservableObject
{
    private ShortcutItem? _existing;
    private string _name = string.Empty;
    private string _target = string.Empty;
    private string _group = "常用";
    private ShortcutType _type = ShortcutType.Folder;
    private string _error = string.Empty;
    private bool _isNew = true;

    public ShortcutEditorViewModel()
    {
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke());
        SetTypeCommand = new RelayCommand(parameter =>
        {
            if (parameter is string text && Enum.TryParse<ShortcutType>(text, out var type)) Type = type;
        });
    }

    public event Action<ShortcutItem, bool>? Saved;
    public event Action? Cancelled;

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SetTypeCommand { get; }

    public ObservableCollection<string> GroupSuggestions { get; } = new();

    public bool IsNew
    {
        get => _isNew;
        private set
        {
            if (!SetProperty(ref _isNew, value)) return;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ActionLabel));
        }
    }

    public string Title => IsNew ? "添加快捷项" : "编辑快捷项";
    public string ActionLabel => IsNew ? "添加" : "保存修改";

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value)) Error = string.Empty;
        }
    }

    public string Target
    {
        get => _target;
        set
        {
            if (SetProperty(ref _target, value)) Error = string.Empty;
        }
    }

    public string Group
    {
        get => _group;
        set => SetProperty(ref _group, value);
    }

    public ShortcutType Type
    {
        get => _type;
        set
        {
            if (!SetProperty(ref _type, value)) return;
            OnPropertyChanged(nameof(IsUrl));
            OnPropertyChanged(nameof(TargetLabel));
            OnPropertyChanged(nameof(TargetPlaceholder));
            OnPropertyChanged(nameof(ShowBrowse));
        }
    }

    public string Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsUrl => Type == ShortcutType.Url;
    public bool ShowBrowse => !IsUrl;
    public string TargetLabel => IsUrl ? "网址" : "目标位置";
    public string TargetPlaceholder => Type switch
    {
        ShortcutType.Url => "https://example.com",
        ShortcutType.Folder => "选择或粘贴文件夹路径",
        ShortcutType.Application => "选择或粘贴应用程序路径",
        _ => "选择或粘贴文件路径"
    };

    public void Load(ShortcutItem? existing, IEnumerable<string> groups)
    {
        _existing = existing;
        IsNew = existing is null;
        Name = existing?.Name ?? string.Empty;
        Target = existing?.Target ?? string.Empty;
        Group = existing?.Group ?? "常用";
        Type = existing?.Type ?? ShortcutType.Folder;
        Error = string.Empty;

        GroupSuggestions.Clear();
        foreach (var group in groups.Where(group => !string.IsNullOrWhiteSpace(group)).Distinct().Take(6))
            GroupSuggestions.Add(group);
    }

    public void Fill(string name, string target, string group, ShortcutType type)
    {
        Name = name;
        Target = target;
        Group = group;
        Type = type;
    }

    public void SetTargetFromPicker(string path)
    {
        Target = path;
        if (string.IsNullOrWhiteSpace(Name))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(name)) Name = name;
        }
    }

    private void Save()
    {
        var name = Name.Trim();
        var target = Target.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Error = "给这个快捷项起一个名字吧。";
            return;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            Error = IsUrl ? "请输入网页地址。" : "请选择或填写目标位置。";
            return;
        }

        if (IsUrl)
        {
            if (!target.Contains("://", StringComparison.Ordinal)) target = "https://" + target;
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Error = "请输入有效的网址，例如 https://example.com。";
                return;
            }
        }

        var item = _existing?.Clone() ?? new ShortcutItem();
        item.Name = name;
        item.Target = target;
        item.Type = Type;
        item.Group = string.IsNullOrWhiteSpace(Group) ? "常用" : Group.Trim();
        item.Arguments = string.IsNullOrWhiteSpace(_existing?.Arguments) ? null : _existing.Arguments;
        item.IconPath = string.IsNullOrWhiteSpace(_existing?.IconPath) ? null : _existing.IconPath;
        Saved?.Invoke(item, IsNew);
    }
}
