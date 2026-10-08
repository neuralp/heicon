using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace heicon.Models
{
    /// <summary>
    /// A single HEIC file queued for conversion.
    /// </summary>
    /// <remarks>Partial so CsWinRT can generate its WinRT interop code; required for trimmed builds.</remarks>
    public sealed partial class ConversionItem : INotifyPropertyChanged
    {
        private ConversionStatus _status = ConversionStatus.Pending;
        private string _message = "Ready";
        private string? _outputPath;

        public ConversionItem(string sourcePath)
        {
            SourcePath = sourcePath;
            FileName = Path.GetFileName(sourcePath);
            FolderPath = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string SourcePath { get; }

        public string FileName { get; }

        public string FolderPath { get; }

        public ConversionStatus Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        public string Message
        {
            get => _message;
            set => SetField(ref _message, value);
        }

        public string? OutputPath
        {
            get => _outputPath;
            set => SetField(ref _outputPath, value);
        }

        public bool IsFinished => Status is ConversionStatus.Done or ConversionStatus.Skipped;

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
