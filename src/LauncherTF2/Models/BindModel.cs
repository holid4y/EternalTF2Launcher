using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LauncherTF2.Models;

public class BindModel : INotifyPropertyChanged
{
    private string _name = "New Bind";
    private string _key = "";
    private string _command = "";

    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
            }
        }
    }

    public string Key
    {
        get => _key;
        set
        {
            if (_key != value)
            {
                _key = value;
                OnPropertyChanged();
            }
        }
    }

    public string Command
    {
        get => _command;
        set
        {
            if (_command != value)
            {
                _command = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isListening;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsListening
    {
        get => _isListening;
        set
        {
            if (_isListening != value)
            {
                _isListening = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
