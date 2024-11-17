﻿/*
* WhackerLink - WhackerLinkConsoleV2
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the GNU General Public License as published by
* the Free Software Foundation, either version 3 of the License, or
* (at your option) any later version.
*
* This program is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU General Public License for more details.
*
* You should have received a copy of the GNU General Public License
* along with this program.  If not, see <http://www.gnu.org/licenses/>.
* 
* Copyright (C) 2024 Caleb, K4PHP
* 
*/

using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WhackerLinkLib.Models.Radio;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using WhackerLinkConsoleV2.Controls;
using WebSocketManager = WhackerLinkLib.Managers.WebSocketManager;
using System.Windows.Media;
using WhackerLinkLib.Utils;
using WhackerLinkLib.Models;
using WhackerLinkLib.Handlers;
using System.Net;
using NAudio.Wave;
using WhackerLinkLib.Interfaces;
using WhackerLinkLib.Models.IOSP;
using Nancy;

namespace WhackerLinkConsoleV2
{
    public partial class MainWindow : Window
    {
        public Codeplug Codeplug { get; set; }
        private bool isEditMode = false;

        private UIElement _draggedElement;
        private Point _startPoint;
        private double _offsetX;
        private double _offsetY;
        private bool _isDragging;
        private bool _stopSending;

        private SettingsManager _settingsManager = new SettingsManager();
        private SelectedChannelsManager _selectedChannelsManager;
        private WebSocketManager _webSocketManager = new WebSocketManager();

        private readonly WaveInEvent _waveIn;
        private readonly WaveOutEvent _waveOut;
        private readonly BufferedWaveProvider _waveProvider;

        public MainWindow()
        {
            InitializeComponent();
            _settingsManager.LoadSettings();
            _selectedChannelsManager = new SelectedChannelsManager();

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(8000, 16, 1)
            };
            _waveIn.DataAvailable += WaveIn_DataAvailable;
            _waveIn.RecordingStopped += WaveIn_RecordingStopped;

            _waveIn.StartRecording();

            _waveOut = new WaveOutEvent();
            _waveProvider = new BufferedWaveProvider(new WaveFormat(8000, 16, 1))
            {
                DiscardOnBufferOverflow = true
            };
            _waveOut.Init(_waveProvider);

            _waveOut.Play();

            _selectedChannelsManager.SelectedChannelsChanged += SelectedChannelsChanged;
            Loaded += MainWindow_Loaded;
        }

        private void OpenCodeplug_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Codeplug Files (*.yml)|*.yml|All Files (*.*)|*.*",
                Title = "Open Codeplug"
            };
            if (openFileDialog.ShowDialog() == true)
            {
                LoadCodeplug(openFileDialog.FileName);

                _settingsManager.LastCodeplugPath = openFileDialog.FileName;
                _settingsManager.SaveSettings();
            }
        }

        private void ResetSettings_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists("UserSettings.json"))
                File.Delete("UserSettings.json");
        }

        private void LoadCodeplug(string filePath)
        {
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .Build();

                var yaml = File.ReadAllText(filePath);
                Codeplug = deserializer.Deserialize<Codeplug>(yaml);

                GenerateChannelWidgets();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading codeplug: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GenerateChannelWidgets()
        {
            ChannelsCanvas.Children.Clear();
            double offsetX = 20;
            double offsetY = 20;

            if (_settingsManager.ShowSystemStatus && Codeplug != null)
            {
                foreach (var system in Codeplug.Systems)
                {
                    var systemStatusBox = new SystemStatusBox(system.Name, system.Address, system.Port);

                    if (_settingsManager.SystemStatusPositions.TryGetValue(system.Name, out var position))
                    {
                        Canvas.SetLeft(systemStatusBox, position.X);
                        Canvas.SetTop(systemStatusBox, position.Y);
                    }
                    else
                    {
                        Canvas.SetLeft(systemStatusBox, offsetX);
                        Canvas.SetTop(systemStatusBox, offsetY);
                    }

                    systemStatusBox.MouseLeftButtonDown += SystemStatusBox_MouseLeftButtonDown;
                    systemStatusBox.MouseMove += SystemStatusBox_MouseMove;
                    systemStatusBox.MouseRightButtonDown += SystemStatusBox_MouseRightButtonDown;

                    ChannelsCanvas.Children.Add(systemStatusBox);

                    offsetX += 220;
                    if (offsetX + 200 > ChannelsCanvas.ActualWidth)
                    {
                        offsetX = 20;
                        offsetY += 140;
                    }

                    _webSocketManager.AddWebSocketHandler(system.Name);

                    IWebSocketHandler handler = _webSocketManager.GetWebSocketHandler(system.Name);
                    handler.OnVoiceChannelResponse += HandleVoiceResponse;
                    handler.OnVoiceChannelRelease += HandleVoiceRelease;
                    handler.OnAudioData += HandleReceivedAudio;

                    Task.Factory.StartNew(() =>
                    {
                        handler.Connect(system.Address, system.Port);

                        handler.OnUnitRegistrationResponse += (response) =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (response.Status == (int)ResponseType.GRANT)
                                {
                                    systemStatusBox.Background = new SolidColorBrush(Colors.Green);
                                    systemStatusBox.ConnectionState = "Connected";
                                }
                                else
                                {
                                    systemStatusBox.Background = new SolidColorBrush(Colors.Red);
                                    systemStatusBox.ConnectionState = "Disconnected";
                                }
                            });
                        };

                        handler.OnGroupAffiliationResponse += (response) => { /* TODO */ };

                        if (handler.IsConnected)
                        {
                            handler.SendMessage(PacketFactory.CreateUnitRegistrationRequest(system.Rid, system.Site));
                        }
                        else
                        {
                            systemStatusBox.Background = new SolidColorBrush(Colors.Red);
                            systemStatusBox.ConnectionState = "Disconnected";
                        }
                    });
                }
            }

            if (_settingsManager.ShowChannels && Codeplug != null)
            {
                foreach (var zone in Codeplug.Zones)
                {
                    foreach (var channel in zone.Channels)
                    {
                        var channelBox = new ChannelBox(_selectedChannelsManager, channel.Name, channel.System);

                        if (_settingsManager.ChannelPositions.TryGetValue(channel.Name, out var position))
                        {
                            Canvas.SetLeft(channelBox, position.X);
                            Canvas.SetTop(channelBox, position.Y);
                        }
                        else
                        {
                            Canvas.SetLeft(channelBox, offsetX);
                            Canvas.SetTop(channelBox, offsetY);
                        }

                        channelBox.PTTButtonClicked += ChannelBox_PTTButtonClicked;

                        channelBox.MouseLeftButtonDown += ChannelBox_MouseLeftButtonDown;
                        channelBox.MouseMove += ChannelBox_MouseMove;
                        channelBox.MouseRightButtonDown += ChannelBox_MouseRightButtonDown;
                        ChannelsCanvas.Children.Add(channelBox);

                        offsetX += 220;
                        if (offsetX + 200 > ChannelsCanvas.ActualWidth)
                        {
                            offsetX = 20;
                            offsetY += 140;
                        }
                    }
                }
            }

            if (_settingsManager.ShowAlertTones && Codeplug != null)
            {
                foreach (var alertPath in _settingsManager.AlertToneFilePaths)
                {
                    var alertTone = new AlertTone(alertPath)
                    {
                        IsEditMode = isEditMode
                    };

                    if (_settingsManager.AlertTonePositions.TryGetValue(alertPath, out var position))
                    {
                        Canvas.SetLeft(alertTone, position.X);
                        Canvas.SetTop(alertTone, position.Y);
                    }
                    else
                    {
                        Canvas.SetLeft(alertTone, 20);
                        Canvas.SetTop(alertTone, 20);
                    }

                    alertTone.MouseRightButtonUp += AlertTone_MouseRightButtonUp;

                    ChannelsCanvas.Children.Add(alertTone);
                }
            }

            AdjustCanvasHeight();
        }

        private void WaveIn_RecordingStopped(object sender, EventArgs e)
        {
            /* stub */
        }

        private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IWebSocketHandler handler = _webSocketManager.GetWebSocketHandler(system.Name);

                if (channel.IsSelected && channel.VoiceChannel != null && !_stopSending)
                {
                    handler.SendMessage(PacketFactory.CreateVoicePacket(system.Rid, cpgChannel.Tgid, channel.VoiceChannel, e.Buffer, system.Site));
                } else
                {
                    _stopSending = true;
                }
            }
        }

        private void SelectedChannelsChanged()
        {
            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IWebSocketHandler handler = _webSocketManager.GetWebSocketHandler(system.Name);

                if (channel.IsSelected && handler.IsConnected)
                    handler.SendMessage(PacketFactory.CreateAffiliationRequest(system.Rid, cpgChannel.Tgid, system.Site));
            }
        }

        private void AudioSettings_Click(object sender, RoutedEventArgs e)
        {
            AudioSettingsWindow audioSettingsWindow = new AudioSettingsWindow();

            if (audioSettingsWindow.ShowDialog() == true)
            {
                int? inputDeviceIndex = audioSettingsWindow.SelectedInputDeviceIndex;
                int? outputDeviceIndex = audioSettingsWindow.SelectedOutputDeviceIndex;

                if (inputDeviceIndex.HasValue && outputDeviceIndex.HasValue)
                {
                    MessageBox.Show($"Selected Input Device Index: {inputDeviceIndex}\n" +
                                    $"Selected Output Device Index: {outputDeviceIndex}",
                                    "Selected Devices");

                    _waveIn.DeviceNumber = inputDeviceIndex.Value;
                    _waveOut.DeviceNumber = outputDeviceIndex.Value;
                }
                else
                {
                    MessageBox.Show("No device selected.", "Warning");
                }
            }
        }

        private void SelectWidgets_Click(object sender, RoutedEventArgs e)
        {
            var widgetSelectionWindow = new WidgetSelectionWindow();
            widgetSelectionWindow.Owner = this;
            if (widgetSelectionWindow.ShowDialog() == true)
            {
                _settingsManager.ShowSystemStatus = widgetSelectionWindow.ShowSystemStatus;
                _settingsManager.ShowChannels = widgetSelectionWindow.ShowChannels;
                _settingsManager.ShowAlertTones = widgetSelectionWindow.ShowAlertTones;

                GenerateChannelWidgets();
                _settingsManager.SaveSettings();
            }
        }

        private void HandleReceivedAudio(byte[] audioData, VoiceChannel voiceChannel)
        {
            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IWebSocketHandler handler = _webSocketManager.GetWebSocketHandler(system.Name);

                if (voiceChannel.SrcId != system.Rid && voiceChannel.Frequency == channel.VoiceChannel && voiceChannel.DstId == cpgChannel.Tgid)
                {
                    _waveProvider.AddSamples(audioData, 0, audioData.Length);
                }
            }
        }

        private void HandleVoiceRelease(GRP_VCH_RLS response)
        {
            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IWebSocketHandler handler = _webSocketManager.GetWebSocketHandler(system.Name);

                if (response.DstId == cpgChannel.Tgid && response.SrcId != system.Rid)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (channel.IsSelected)
                            channel.Background = Brushes.DodgerBlue;
                        else
                            channel.Background = new SolidColorBrush(Colors.DarkGray);
                    });

                    channel.VoiceChannel = null;
                }
            }
        }

        private void HandleVoiceResponse(GRP_VCH_RSP response)
        {
            foreach (ChannelBox channel in _selectedChannelsManager.GetSelectedChannels())
            {
                Codeplug.System system = Codeplug.GetSystemForChannel(channel.ChannelName);
                Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(channel.ChannelName);
                IWebSocketHandler handler = _webSocketManager.GetWebSocketHandler(system.Name);

                if (channel.PttState && response.Status == (int)ResponseType.GRANT && response.Channel != null && response.SrcId == system.Rid && response.DstId == cpgChannel.Tgid)
                {
                    channel.VoiceChannel = response.Channel;
                    _stopSending = false;
                } else if (response.Status == (int)ResponseType.GRANT && response.SrcId != system.Rid && response.DstId == cpgChannel.Tgid)
                {
                    channel.VoiceChannel = response.Channel;
                    channel.LastSrcId = "Last SRC: " + response.SrcId;
                    Dispatcher.Invoke(() =>
                    {
                        channel.Background = new SolidColorBrush(Colors.DarkCyan);
                    });
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (channel.IsSelected)
                            Background = new SolidColorBrush(Colors.DodgerBlue);
                        else
                            Background = new SolidColorBrush(Colors.Gray);
                    });

                    channel.VoiceChannel = null;
                    _stopSending = true;
                }
            }
        }

        private void ChannelBox_PTTButtonClicked(object sender, ChannelBox e)
        {
            Codeplug.System system = Codeplug.GetSystemForChannel(e.ChannelName);
            Codeplug.Channel cpgChannel = Codeplug.GetChannelByName(e.ChannelName);
            IWebSocketHandler handler = _webSocketManager.GetWebSocketHandler(system.Name);

            if (e.PttState)
                handler.SendMessage(PacketFactory.CreateVoiceChannelRequest(system.Rid, cpgChannel.Tgid, system.Site));
            else
            {
                _stopSending = true;
                handler.SendMessage(PacketFactory.CreateVoiceChannelRelease(system.Rid, cpgChannel.Tgid, e.VoiceChannel, system.Site));
                e.VoiceChannel = null;
            }
        }

        private void ChannelBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!isEditMode || !(sender is UIElement element)) return;

            _draggedElement = element;
            _startPoint = e.GetPosition(ChannelsCanvas);
            _offsetX = _startPoint.X - Canvas.GetLeft(_draggedElement);
            _offsetY = _startPoint.Y - Canvas.GetTop(_draggedElement);
            _isDragging = true;

            element.CaptureMouse();
        }

        private void ChannelBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isEditMode || !_isDragging || _draggedElement == null) return;

            Point currentPosition = e.GetPosition(ChannelsCanvas);
            double newLeft = Math.Max(0, Math.Min(currentPosition.X - _offsetX, ChannelsCanvas.ActualWidth - _draggedElement.RenderSize.Width));
            double newTop = Math.Max(0, Math.Min(currentPosition.Y - _offsetY, ChannelsCanvas.ActualHeight - _draggedElement.RenderSize.Height));

            Canvas.SetLeft(_draggedElement, newLeft);
            Canvas.SetTop(_draggedElement, newTop);

            if (_draggedElement is ChannelBox channelBox)
            {
                _settingsManager.UpdateChannelPosition(channelBox.ChannelName, newLeft, newTop);
            }

            AdjustCanvasHeight();
        }

        private void ChannelBox_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!isEditMode || !_isDragging || _draggedElement == null) return;

            _isDragging = false;
            _draggedElement.ReleaseMouseCapture();
            _draggedElement = null;
        }

        private void SystemStatusBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ChannelBox_MouseLeftButtonDown(sender, e);
        private void SystemStatusBox_MouseMove(object sender, MouseEventArgs e) => ChannelBox_MouseMove(sender, e);

        private void SystemStatusBox_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!isEditMode) return;

            if (sender is SystemStatusBox systemStatusBox)
            {
                double x = Canvas.GetLeft(systemStatusBox);
                double y = Canvas.GetTop(systemStatusBox);
                _settingsManager.SystemStatusPositions[systemStatusBox.SystemName] = new ChannelPosition { X = x, Y = y };

                ChannelBox_MouseRightButtonDown(sender, e);

                AdjustCanvasHeight();
            }
        }

        private void ToggleEditMode_Click(object sender, RoutedEventArgs e)
        {
            isEditMode = !isEditMode;
            var menuItem = (MenuItem)sender;
            menuItem.Header = isEditMode ? "Disable Edit Mode" : "Enable Edit Mode";
            UpdateEditModeForWidgets();
        }

        private void UpdateEditModeForWidgets()
        {
            foreach (var child in ChannelsCanvas.Children)
            {
                if (child is AlertTone alertTone)
                {
                    alertTone.IsEditMode = isEditMode;
                }

                if (child is ChannelBox channelBox)
                {
                    channelBox.IsEditMode = isEditMode;
                }
            }
        }

        private void AddAlertTone_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "WAV Files (*.wav)|*.wav|All Files (*.*)|*.*",
                Title = "Select Alert Tone"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string alertFilePath = openFileDialog.FileName;
                var alertTone = new AlertTone(alertFilePath)
                {
                    IsEditMode = isEditMode
                };

                if (_settingsManager.AlertTonePositions.TryGetValue(alertFilePath, out var position))
                {
                    Canvas.SetLeft(alertTone, position.X);
                    Canvas.SetTop(alertTone, position.Y);
                }
                else
                {
                    Canvas.SetLeft(alertTone, 20);
                    Canvas.SetTop(alertTone, 20);
                }

                alertTone.MouseRightButtonUp += AlertTone_MouseRightButtonUp;

                ChannelsCanvas.Children.Add(alertTone);
                _settingsManager.UpdateAlertTonePaths(alertFilePath);

                AdjustCanvasHeight();
            }
        }

        private void AlertTone_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!isEditMode) return;

            if (sender is AlertTone alertTone)
            {
                double x = Canvas.GetLeft(alertTone);
                double y = Canvas.GetTop(alertTone);
                _settingsManager.UpdateAlertTonePosition(alertTone.AlertFilePath, x, y);

                AdjustCanvasHeight();
            }
        }

        private void AdjustCanvasHeight()
        {
            double maxBottom = 0;

            foreach (UIElement child in ChannelsCanvas.Children)
            {
                double childBottom = Canvas.GetTop(child) + child.RenderSize.Height;
                if (childBottom > maxBottom)
                {
                    maxBottom = childBottom;
                }
            }

            ChannelsCanvas.Height = maxBottom + 150;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_settingsManager.LastCodeplugPath) && File.Exists(_settingsManager.LastCodeplugPath))
            {
                LoadCodeplug(_settingsManager.LastCodeplugPath);
            }
            else
            {
                GenerateChannelWidgets();
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _settingsManager.SaveSettings();
            base.OnClosing(e);
        }
    }
}
