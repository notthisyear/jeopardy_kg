﻿using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JeopardyKing.Common;
using JeopardyKing.Common.FileUtilities;
using JeopardyKing.GameComponents;
using JeopardyKing.Windows;
using Ookii.Dialogs.Wpf;

namespace JeopardyKing.ViewModels
{
    using InputManager = Communication.InputManager;

    public class GameManagerViewModel : ObservableObject
    {
        #region Public properties

        #region Backing fields
        private Board? _gameBoard;
        private Question? _selectedQuestion;
        private bool _buttonAssignmentOngoing;
        #endregion

        public Board? GameBoard
        {
            get => _gameBoard;
            private set => SetProperty(ref _gameBoard, value);
        }

        public Question? SelectedQuestion
        {
            get => _selectedQuestion;
            private set => SetProperty(ref _selectedQuestion, value);
        }

        public bool ButtonAssignmentOngoing
        {
            get => _buttonAssignmentOngoing;
            private set => SetProperty(ref _buttonAssignmentOngoing, value);
        }

        public QuestionModeManager QuestionModeManager { get; }

        public CategoryViewViewModel CategoryViewModel { get; }

        public ObservableCollection<Player> Players { get; }

        public string ProgramDescription { get; init; } = "Manage";

        public const int MaxNumberOfPlayers = 6;
        #endregion

        #region Commands
        private RelayCommand? _loadBoardCommand;
        private RelayCommand? _addPlayerCommand;
        private RelayCommand<Player>? _assignPlayerCommand;
        private RelayCommand<Player>? _removePlayerCommand;

        public ICommand LoadBoardCommand
        {
            get
            {
                _loadBoardCommand ??= new RelayCommand(() =>
                {
                    if (_loadDialog.ShowDialog() == true)
                    {
                        var reader = new FileTextReader(_loadDialog.FileName);
                        // TODO: Make error handling nicer
                        if (!reader.SuccessfulRead)
                            throw reader.ReadException!;

                        var (board, e) = reader.AllText.DeserializeJsonString<Board>(convertSnakeCaseToPascalCase: true);
                        // TODO: Make error handling nicer
                        if (e != default)
                            throw e;

                        GameBoard = board;
                    }
                });
                return _loadBoardCommand;

            }
        }

        public ICommand AddPlayerCommand
        {
            get
            {
                _addPlayerCommand ??= new RelayCommand(() =>
                {
                    lock (_playersAccessLock)
                    {
                        if (Players.Count == MaxNumberOfPlayers)
                            return;
                        var playerName = $"Player {Players.Count + 1}";
                        Players.Add(new(_playerIdCounter++, playerName));
                    }
                });
                return _addPlayerCommand;
            }
        }

        public ICommand AssignPlayerCommand
        {
            get
            {
                _assignPlayerCommand ??= new RelayCommand<Player>(p =>
                {
                    if (p == default)
                        return;

                    var hasLock = Monitor.TryEnter(_playersAccessLock);
                    if (!hasLock)
                        return;

                    // Note: This should be impossible as the flag as always set after the lock is acquired
                    if (ButtonAssignmentOngoing)
                    {
                        Monitor.Exit(_playersAccessLock);
                        return;
                    }

                    SelectButtonPopupModal buttonSelectionDialog = new(Application.Current.MainWindow,
                        p.Name,
                        (deviceId, key) =>
                        {
                            _ = _inputManager.TryAddPlayerKeyMapping(p.Id, deviceId, key);
                        });

                    _eventAction = e =>
                    {
                        if (e.Event == InputManager.KeyEvent.KeyDown)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                buttonSelectionDialog.LastEvent = e;
                                buttonSelectionDialog.LastKeyPressed = e.Key.ToString();
                                if (_inputManager.TryGetInformationForKeyboard(e.SourceId, out var info))
                                    buttonSelectionDialog.LastPressedSource = $"{info!.DeviceDescription} [{info!.KeyboardType}] ({e.SourceId})";
                                else
                                    buttonSelectionDialog.LastPressedSource = $"Unknown source ({e.SourceId})";
                            });
                        }
                    };

                    _inputManager.SetPropagationMode(InputManager.PropagationMode.All);

                    _ = buttonSelectionDialog.ShowDialog();

                    _inputManager.SetPropagationMode(InputManager.PropagationMode.OnlyMappedKeys);
                    _eventAction = default;

                    ButtonAssignmentOngoing = true;
                    ButtonAssignmentOngoing = false;
                    Monitor.Exit(_playersAccessLock);
                });
                return _assignPlayerCommand;
            }
        }

        public ICommand RemovePlayerCommand
        {
            get
            {
                _removePlayerCommand ??= new RelayCommand<Player>(p =>
                {
                    lock (_playersAccessLock)
                    {
                        if (p == default || Players.Count == 0)
                            return;
                        Players.Remove(p);
                    }
                });
                return _removePlayerCommand;
            }
        }
        #endregion

        #region Private fields
        private readonly VistaOpenFileDialog _loadDialog = new()
        {
            Title = "Load game board",
            Filter = "JSON file (*.json)|*.json",
            Multiselect = false,
        };
        private readonly object _playersAccessLock = new();
        private readonly InputManager _inputManager;
        private readonly ConcurrentQueue<InputManager.KeyboardEvent> _eventQueue = new();
        private readonly Thread _inputThread;
        private Action<InputManager.KeyboardEvent>? _eventAction;
        private int _playerIdCounter = 0;
        private bool _shouldExit = false;
        #endregion

        public GameManagerViewModel()
        {
            QuestionModeManager = new();
            CategoryViewModel = new(QuestionModeManager);
            Players = new();

            _inputThread = new(MonitorInputThread) { IsBackground = true };
            _inputThread.Start();
            _inputManager = new(_eventQueue);
            _ = _inputManager.TryEnumerateKeyboardDevices(out _);

            BindingOperations.EnableCollectionSynchronization(Players, _playersAccessLock);
        }

        public void NotifyWindowClosed()
        {
            _shouldExit = true;
        }

        private void MonitorInputThread(object? state)
        {
            while (!_shouldExit)
            {
                if (_eventQueue.TryDequeue(out var newKeyEvent))
                {
                    if (_eventAction != default)
                        _eventAction(newKeyEvent);

                    var player = Players.FirstOrDefault(x => x.Id == newKeyEvent.PlayerId);
                    if (player != default)
                        player.IsPressingKey = newKeyEvent.Event == InputManager.KeyEvent.KeyDown;
                }
                Thread.Sleep(50);
            }
        }
    }
}