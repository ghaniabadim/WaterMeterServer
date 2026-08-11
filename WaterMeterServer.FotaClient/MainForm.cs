using WaterMeterServer.FotaSimulator;

namespace WaterMeterServer.FotaClient
{
    public sealed class MainForm : Form
    {
        private readonly TextBox _hostTextBox = new() { Text = "127.0.0.1" };
        private readonly NumericUpDown _portInput = new()
        {
            Minimum = 1,
            Maximum = 65535,
            Value = 502
        };
        private readonly TextBox _meterIdTextBox = new() { Text = "000000000001" };
        private readonly TextBox _aesKeyTextBox = new()
        {
            UseSystemPasswordChar = true
        };
        private readonly NumericUpDown _chunkSizeInput = new()
        {
            Minimum = 1,
            Maximum = 2048,
            Value = 256
        };
        private readonly NumericUpDown _timeoutInput = new()
        {
            Minimum = 1,
            Maximum = 300,
            Value = 120
        };
        private readonly ComboBox _scenarioComboBox = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        private readonly CheckBox _showKeyCheckBox = new()
        {
            Text = "نمایش کلید"
        };
        private readonly Button _startButton = new()
        {
            Text = "شروع تست",
            AutoSize = true
        };
        private readonly Button _cancelButton = new()
        {
            Text = "لغو",
            AutoSize = true,
            Enabled = false
        };
        private readonly Button _clearLogButton = new()
        {
            Text = "پاک‌کردن لاگ",
            AutoSize = true
        };
        private readonly RichTextBox _logTextBox = new()
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(25, 25, 25),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 10),
            WordWrap = false
        };
        private readonly Label _statusLabel = new()
        {
            Text = "آماده",
            AutoSize = true,
            ForeColor = Color.DimGray
        };

        private CancellationTokenSource? _runCancellation;

        public MainForm()
        {
            Text = "Water Meter FOTA Test Client";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(850, 650);
            Size = new Size(980, 760);
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Segoe UI", 10);

            _scenarioComboBox.DataSource = Enum.GetValues<SimulationScenario>();
            _scenarioComboBox.SelectedItem = SimulationScenario.Success;

            _showKeyCheckBox.CheckedChanged += (_, _) =>
                _aesKeyTextBox.UseSystemPasswordChar = !_showKeyCheckBox.Checked;
            _startButton.Click += StartButtonClick;
            _cancelButton.Click += (_, _) => _runCancellation?.Cancel();
            _clearLogButton.Click += (_, _) => _logTextBox.Clear();
            FormClosing += (_, _) => _runCancellation?.Cancel();

            Controls.Add(BuildLayout());
        }

        private Control BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 3
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var settings = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 4,
                RowCount = 4,
                Padding = new Padding(8)
            };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            AddSetting(settings, 0, "آدرس سرور", _hostTextBox, "پورت", _portInput);
            AddSetting(settings, 1, "شناسه کنتور", _meterIdTextBox, "سناریو", _scenarioComboBox);
            AddSetting(settings, 2, "اندازه Chunk", _chunkSizeInput, "Timeout (ثانیه)", _timeoutInput);

            settings.Controls.Add(CreateLabel("کلید AES"), 0, 3);
            settings.Controls.Add(_aesKeyTextBox, 1, 3);
            settings.SetColumnSpan(_aesKeyTextBox, 2);
            settings.Controls.Add(_showKeyCheckBox, 3, 3);

            var settingsGroup = new GroupBox
            {
                Text = "پارامترهای اتصال و FOTA",
                Dock = DockStyle.Top,
                AutoSize = true
            };
            settingsGroup.Controls.Add(settings);

            var logGroup = new GroupBox
            {
                Text = "لاگ اجرای پروتکل",
                Dock = DockStyle.Fill,
                Padding = new Padding(8)
            };
            logGroup.Controls.Add(_logTextBox);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0)
            };
            actions.Controls.Add(_startButton);
            actions.Controls.Add(_cancelButton);
            actions.Controls.Add(_clearLogButton);
            actions.Controls.Add(_statusLabel);

            root.Controls.Add(settingsGroup, 0, 0);
            root.Controls.Add(logGroup, 0, 1);
            root.Controls.Add(actions, 0, 2);
            return root;
        }

        private static void AddSetting(
            TableLayoutPanel panel,
            int row,
            string firstLabel,
            Control firstControl,
            string secondLabel,
            Control secondControl)
        {
            firstControl.Dock = DockStyle.Fill;
            secondControl.Dock = DockStyle.Fill;
            panel.Controls.Add(CreateLabel(firstLabel), 0, row);
            panel.Controls.Add(firstControl, 1, row);
            panel.Controls.Add(CreateLabel(secondLabel), 2, row);
            panel.Controls.Add(secondControl, 3, row);
        }

        private static Label CreateLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(8)
            };
        }

        private async void StartButtonClick(object? sender, EventArgs eventArgs)
        {
            if (_runCancellation != null)
            {
                return;
            }

            try
            {
                SimulatorOptions options = SimulatorOptions.Create(
                    _hostTextBox.Text.Trim(),
                    decimal.ToInt32(_portInput.Value),
                    _meterIdTextBox.Text.Trim(),
                    _aesKeyTextBox.Text.Trim(),
                    decimal.ToInt32(_chunkSizeInput.Value),
                    decimal.ToInt32(_timeoutInput.Value),
                    (SimulationScenario)_scenarioComboBox.SelectedItem!);

                _runCancellation = new CancellationTokenSource();
                SetRunningState(true);
                AppendLog($"Starting scenario {options.Scenario} against {options.Host}:{options.Port}...");

                var simulator = new FotaTerminalSimulator(options, AppendLog);
                await Task.Run(
                    () => simulator.RunAsync(_runCancellation.Token),
                    _runCancellation.Token);

                AppendLog($"Scenario {options.Scenario} completed as expected.");
                _statusLabel.Text = "سناریو تأیید شد";
                _statusLabel.ForeColor = Color.ForestGreen;
            }
            catch (OperationCanceledException)
            {
                AppendLog("Test canceled by user.");
                _statusLabel.Text = "لغو شد";
                _statusLabel.ForeColor = Color.DarkOrange;
            }
            catch (Exception exception)
            {
                AppendLog($"ERROR: {exception.Message}");
                _statusLabel.Text = "ناموفق";
                _statusLabel.ForeColor = Color.Firebrick;
            }
            finally
            {
                _runCancellation?.Dispose();
                _runCancellation = null;
                SetRunningState(false);
            }
        }

        private void SetRunningState(bool isRunning)
        {
            _startButton.Enabled = !isRunning;
            _cancelButton.Enabled = isRunning;
            _hostTextBox.Enabled = !isRunning;
            _portInput.Enabled = !isRunning;
            _meterIdTextBox.Enabled = !isRunning;
            _aesKeyTextBox.Enabled = !isRunning;
            _chunkSizeInput.Enabled = !isRunning;
            _timeoutInput.Enabled = !isRunning;
            _scenarioComboBox.Enabled = !isRunning;

            if (isRunning)
            {
                _statusLabel.Text = "در حال اجرا...";
                _statusLabel.ForeColor = Color.RoyalBlue;
            }
        }

        private void AppendLog(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => AppendLog(message));
                return;
            }

            _logTextBox.AppendText(
                $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            _logTextBox.SelectionStart = _logTextBox.TextLength;
            _logTextBox.ScrollToCaret();
        }
    }
}
