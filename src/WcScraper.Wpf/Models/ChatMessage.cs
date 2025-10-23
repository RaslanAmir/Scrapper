using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WcScraper.Wpf.Models;

public enum ChatMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _content;

    public ChatMessage(ChatMessageRole role, string content)
    {
        Role = role;
        _content = content ?? string.Empty;
    }

    public ChatMessageRole Role { get; }

    public string Content
    {
        get => _content;
        set
        {
            var newValue = value ?? string.Empty;
            if (_content == newValue)
            {
                return;
            }

            _content = newValue;
            OnPropertyChanged();
        }
    }

    public void Append(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        Content = string.Concat(_content, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
