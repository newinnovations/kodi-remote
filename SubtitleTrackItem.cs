using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KodiListenerGui
{
    // Display model for a single row in the subtitle track list.
    public class SubtitleTrackItem : INotifyPropertyChanged
    {
        private string _text = "";
        private bool _isSelected;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Text
        {
            get => _text;
            set => SetField(ref _text, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
            {
                return;
            }
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
