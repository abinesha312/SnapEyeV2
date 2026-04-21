using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SnapEye.Models;

namespace SnapEye.Controls
{
    /// <summary>
    /// Interaction logic for TranscriptionDisplay.xaml
    /// </summary>
    public partial class TranscriptionDisplay : UserControl, INotifyPropertyChanged
    {
        public TranscriptionDisplay()
        {
            InitializeComponent();
            DataContext = this;
        }

        // Properties
        private string interimText = "";
        public string InterimText
        {
            get => interimText;
            set
            {
                interimText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(InterimTextVisibility));
            }
        }

        private ObservableCollection<TranscriptItem> finalTranscripts = new();
        public ObservableCollection<TranscriptItem> FinalTranscripts
        {
            get => finalTranscripts;
            set
            {
                finalTranscripts = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TranscriptCount));
                OnPropertyChanged(nameof(EmptyStateVisibility));
            }
        }

        private string statusText = "Disconnected";
        public string StatusText
        {
            get => statusText;
            set
            {
                statusText = value;
                OnPropertyChanged();
            }
        }

        private Brush statusColor = Brushes.Gray;
        public Brush StatusColor
        {
            get => statusColor;
            set
            {
                statusColor = value;
                OnPropertyChanged();
            }
        }

        public int TranscriptCount => FinalTranscripts.Count;

        public Visibility InterimTextVisibility =>
            string.IsNullOrWhiteSpace(InterimText) ? Visibility.Collapsed : Visibility.Visible;

        public Visibility EmptyStateVisibility =>
            FinalTranscripts.Count == 0 && string.IsNullOrWhiteSpace(InterimText)
                ? Visibility.Visible
                : Visibility.Collapsed;

        // Commands
        private ICommand? clearCommand;
        public ICommand ClearCommand
        {
            get
            {
                return clearCommand ??= new RelayCommand(
                    execute: _ => ClearTranscripts(),
                    canExecute: _ => FinalTranscripts.Count > 0
                );
            }
        }

        // Methods
        public void AddInterimTranscript(string text)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                InterimText = text;
            });
        }

        public void AddFinalTranscript(string text, MessageSource source = MessageSource.Microphone)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                FinalTranscripts.Add(new TranscriptItem
                {
                    Text = text,
                    Timestamp = DateTime.Now,
                    Source = source == MessageSource.Microphone ? "Microphone" : "Speaker",
                    SourceColor = source == MessageSource.Microphone
                        ? new SolidColorBrush(Color.FromRgb(52, 152, 219))  // Blue
                        : new SolidColorBrush(Color.FromRgb(46, 204, 113))  // Green
                });

                InterimText = ""; // Clear interim text
                OnPropertyChanged(nameof(TranscriptCount));
                OnPropertyChanged(nameof(EmptyStateVisibility));
            });
        }

        public void ClearTranscripts()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                FinalTranscripts.Clear();
                InterimText = "";
                OnPropertyChanged(nameof(TranscriptCount));
                OnPropertyChanged(nameof(EmptyStateVisibility));
            });
        }

        public void SetStatus(string status, bool isConnected)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                StatusText = status;
                StatusColor = isConnected
                    ? new SolidColorBrush(Color.FromRgb(46, 204, 113))  // Green
                    : new SolidColorBrush(Color.FromRgb(231, 76, 60));  // Red
            });
        }

        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Transcript item model
    /// </summary>
    public class TranscriptItem
    {
        public string Text { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string Source { get; set; } = "Microphone";
        public Brush SourceColor { get; set; } = Brushes.Blue;
    }

    /// <summary>
    /// Simple relay command implementation
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> execute;
        private readonly Predicate<object?>? canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            this.canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            return canExecute == null || canExecute(parameter);
        }

        public void Execute(object? parameter)
        {
            execute(parameter);
        }

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}

